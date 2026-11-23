using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class SupervisorTests
{
    private static JsonObject CreateSnapshot()
    {
        var model = new JsonObject
        {
            ["id"] = "supervisor-fixture",
            ["label"] = "Fixture Supervisor",
            ["provider"] = "api",
            ["adapter"] = "api",
            ["cli"] = "exact-fixture",
            ["enabled"] = true,
            ["available"] = true,
            ["roles"] = new JsonObject
            {
                ["worker"] = false,
                ["orchestrator"] = false,
                ["reviewer"] = false,
                ["supervisor"] = true
            }
        };

        return new JsonObject
        {
            ["models"] = new JsonArray { model },
            ["projects"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "a",
                    ["name"] = "Alpha",
                    ["status"] = "watching",
                    ["megaTaskId"] = "MT-a",
                    ["dispatch"] = new JsonArray
                    {
                        new JsonObject { ["modelId"] = "supervisor-fixture", ["attemptId"] = "A-1", ["subTaskId"] = "ST-work", ["alive"] = true },
                        new JsonObject { ["modelId"] = "supervisor-fixture", ["attemptId"] = "A-2", ["subTaskId"] = "ST-review", ["alive"] = true }
                    }
                },
                new JsonObject
                {
                    ["id"] = "b",
                    ["name"] = "Beta",
                    ["status"] = "idle"
                }
            },
            ["execution"] = new JsonObject
            {
                ["megaTasks"] = new JsonArray
                {
                    new JsonObject { ["id"] = "MT-a", ["projectId"] = "a" }
                },
                ["subTasks"] = new JsonArray
                {
                    new JsonObject { ["id"] = "ST-work", ["megaTaskId"] = "MT-a", ["title"] = "Build feature", ["type"] = "IMPLEMENTATION" },
                    new JsonObject { ["id"] = "ST-review", ["megaTaskId"] = "MT-a", ["title"] = "Review feature", ["type"] = "AUDIT" }
                },
                ["results"] = new JsonArray(),
                ["timeline"] = new JsonArray
                {
                    new JsonObject { ["id"] = "E-a", ["projectId"] = "a", ["summary"] = "Approved work" },
                    new JsonObject { ["id"] = "E-b", ["projectId"] = "b", ["summary"] = "Other project" }
                }
            }
        };
    }

    [Fact]
    public void AdvisoryError_NonSupervisorModelOrQuotaBlocked_ReturnsSpecificExplanation()
    {
        var nonSupervisor = new JsonObject
        {
            ["id"] = "m1",
            ["roles"] = new JsonObject { ["reviewer"] = true, ["supervisor"] = false }
        };
        var err1 = Supervisor.AdvisoryError(nonSupervisor);
        Assert.Equal("Supervisor permission is required; Reviewer permission does not authorize advisory calls.", err1);

        var unsupportedTransport = new JsonObject
        {
            ["id"] = "m2",
            ["provider"] = "claude",
            ["adapter"] = "claude",
            ["roles"] = new JsonObject { ["supervisor"] = true }
        };
        var err2 = Supervisor.AdvisoryError(unsupportedTransport);
        Assert.Equal("No verified no-tool Supervisor transport for this adapter. No fallback was used.", err2);
    }

    [Fact]
    public void ProjectFacts_DetectsConflictingCompletion_WhenWorkersStillAlive()
    {
        var snap = CreateSnapshot();
        ((JsonObject)((JsonArray)snap["projects"]!)[0]!)["status"] = "complete";

        var facts = Supervisor.ProjectFacts(snap, "a");
        var alerts = facts["alerts"] as JsonArray;
        Assert.NotNull(alerts);
        Assert.Contains(alerts, a => a?["kind"]?.ToString() == "conflicting-completion");
        Assert.DoesNotContain("no workers are active", facts["summary"]?.ToString() ?? "");
    }

    [Fact]
    public void ProjectFacts_DetectsThreeConsecutiveFailures_EmitsRepeatedFailuresWarning()
    {
        var snap = CreateSnapshot();
        var results = snap["execution"]?["results"] as JsonArray;
        results!.Add(new JsonObject { ["resultId"] = "R-1", ["megaTaskId"] = "MT-a", ["status"] = "FAILED" });
        results.Add(new JsonObject { ["resultId"] = "R-2", ["megaTaskId"] = "MT-a", ["status"] = "BLOCKED" });
        results.Add(new JsonObject { ["resultId"] = "R-3", ["megaTaskId"] = "MT-a", ["status"] = "FAILED" });

        var facts = Supervisor.ProjectFacts(snap, "a");
        var alerts = facts["alerts"] as JsonArray;
        Assert.NotNull(alerts);
        Assert.Contains(alerts, a => a?["kind"]?.ToString() == "repeated-failures");
    }

    [Fact]
    public async Task Supervisor_Observe_EmitsAlertsAndTriggersAutoReviewWithinBudget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        int calls = 0;
        try
        {
            var snap = CreateSnapshot();
            var projA = ((JsonArray)snap["projects"]!)[0] as JsonObject;
            projA!["status"] = "failed";
            projA["dispatch"] = new JsonArray();

            var supervisor = new Supervisor(
                tempDir,
                review: req =>
                {
                    calls++;
                    return Task.FromResult(new JsonObject
                    {
                        ["answer"] = "Review the evidence.",
                        ["provider"] = "api",
                        ["model"] = "exact-fixture"
                    });
                }
            );

            supervisor.Settings(new JsonObject { ["name"] = " Captain <&> " }, snap);
            var view1 = supervisor.View(snap);
            Assert.Equal("Captain <&>", view1["name"]?.ToString());

            supervisor.Settings(new JsonObject { ["modelId"] = "supervisor-fixture", ["autoReview"] = true }, snap);

            // Trigger multiple observes
            for (int i = 0; i < 5; i++)
            {
                projA["status"] = "idle";
                supervisor.Observe(snap);
                projA["status"] = "failed";
                supervisor.Observe(snap);
                await Task.Delay(10);
            }

            Assert.True(calls > 0 && calls <= 3);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Supervisor_RestartRecovery_MarksInterruptedRunningTurnsAsFailed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "supervisor.json");
            var initialJson = new JsonObject
            {
                ["schema"] = 1,
                ["alerts"] = new JsonArray(),
                ["turns"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "interrupted",
                        ["projectId"] = "a",
                        ["status"] = "running",
                        ["question"] = "Explain progress"
                    }
                }
            };
            File.WriteAllText(file, initialJson.ToJsonString());

            var supervisor = new Supervisor(tempDir, review: req => throw new Exception("Unexpected inference"));
            var snap = CreateSnapshot();
            var view = supervisor.View(snap);
            var projA = ((JsonArray)view["projects"]!)[0] as JsonObject;
            var turn = ((JsonArray)projA!["turns"]!)[0] as JsonObject;

            Assert.Equal("failed", turn!["status"]?.ToString());
            Assert.Contains("interrupted", turn["answer"]?.ToString());
            Assert.False(view["busy"]?.GetValue<bool>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Supervisor_Schema1Migration_UpgradesToSchema2PreservingLegacyModelId()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "supervisor.json");
            var initialJson = new JsonObject
            {
                ["schema"] = 1,
                ["modelId"] = "legacy-model-1",
                ["autoReview"] = true,
                ["alerts"] = new JsonArray(),
                ["turns"] = new JsonArray()
            };
            File.WriteAllText(file, initialJson.ToJsonString());

            var supervisor = new Supervisor(tempDir);
            var savedText = File.ReadAllText(file);
            var savedObj = JsonNode.Parse(savedText) as JsonObject;

            Assert.NotNull(savedObj);
            Assert.Equal(2, savedObj["schema"]?.GetValue<int>());
            Assert.Equal("legacy-model-1", savedObj["legacyModelId"]?.ToString());
            Assert.Equal("", savedObj["modelId"]?.ToString());
            Assert.False(savedObj["autoReview"]?.GetValue<bool>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Supervisor_CorruptedStateOnDisk_RecoversToEmptyState()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var file = Path.Combine(tempDir, "supervisor.json");
            File.WriteAllText(file, "{ corrupted json syntax ");

            var supervisor = new Supervisor(tempDir);
            var snap = CreateSnapshot();
            var view = supervisor.View(snap);

            Assert.NotNull(view);
            Assert.Equal("Supervisor", view["name"]?.ToString());
            Assert.False(view["configured"]?.GetValue<bool>());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Supervisor_Ask_EmptyQuestion_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var supervisor = new Supervisor(tempDir);
            var snap = CreateSnapshot();

            await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.Ask(snap, "a", "   "));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Supervisor_IgnoreAlert_MarksAlertAsIgnored()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-sup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var supervisor = new Supervisor(tempDir);
            var snap = CreateSnapshot();
            ((JsonObject)((JsonArray)snap["projects"]!)[0]!)["status"] = "complete";

            supervisor.Observe(snap);
            var viewBefore = supervisor.View(snap);
            var projABefore = ((JsonArray)viewBefore["projects"]!)[0] as JsonObject;
            var alertsBefore = projABefore!["alerts"] as JsonArray;
            Assert.NotEmpty(alertsBefore!);

            var alertId = alertsBefore![0]!["id"]!.ToString();
            supervisor.Ignore("a", alertId);

            var viewAfter = supervisor.View(snap);
            var projAAfter = ((JsonArray)viewAfter["projects"]!)[0] as JsonObject;
            var alertsAfter = projAAfter!["alerts"] as JsonArray;
            var alert = alertsAfter!.FirstOrDefault(a => a?["id"]?.ToString() == alertId) as JsonObject;
            Assert.NotNull(alert);
            Assert.True(alert["ignored"]?.GetValue<bool>());
            Assert.NotNull(alert["ignoredAt"]);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
