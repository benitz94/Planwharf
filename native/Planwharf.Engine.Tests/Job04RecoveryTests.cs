// Edge, error, recovery and concurrency cases for job 04 that the ported module tests did not
// exercise. Expectations are the JavaScript engine (engine/*.cjs), which is the specification.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class Job04RecoveryTests
{
    static JsonObject Settings(JsonObject? extra = null)
    {
        var settings = Autonomy.CreateSettings("s1", "2026-11-03T10:00:00Z");
        settings["started"] = true;
        if (extra is null) return settings;
        foreach (var pair in extra) settings[pair.Key] = pair.Value?.DeepClone();
        return settings;
    }

    static JsonObject Step(JsonObject settings, JsonNode? row, JsonNode? alerts = null, JsonNode? now = null, bool includeAlerts = false, bool includeNow = false)
    {
        var options = new JsonObject
        {
            ["mode"] = "auto",
            ["sessionId"] = "s1",
            ["settings"] = settings.DeepClone(),
            ["row"] = row?.DeepClone()
        };
        if (includeAlerts) options["alerts"] = alerts?.DeepClone();
        if (includeNow) options["now"] = now?.DeepClone();
        return Autonomy.NextStep(options);
    }

    [Fact]
    public void Next_step_uses_JavaScript_truthiness_and_keeps_numeric_ids()
    {
        var watching = new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m" };
        var enabled = Settings();
        enabled["enabled"] = 1;
        Assert.Equal("idle", Step(enabled, watching)["reason"]!.GetValue<string>());

        var named = Settings();
        named["enabled"] = "yes";
        Assert.Equal("idle", Step(named, watching)["reason"]!.GetValue<string>());

        var stopped = Settings();
        stopped["stopReason"] = 1;
        Assert.Equal("stopped", Step(stopped, watching)["reason"]!.GetValue<string>());

        var paused = new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m", ["megaTask"] = new JsonObject { ["pauseRequested"] = 1 } };
        Assert.Equal("user-paused", Step(Settings(), paused)["reason"]!.GetValue<string>());

        var numericStatus = new JsonObject { ["status"] = 1, ["megaTaskId"] = "m" };
        Assert.Equal("idle", Step(Settings(), numericStatus)["reason"]!.GetValue<string>());

        var numericReason = new JsonObject { ["status"] = "checkpointed", ["megaTaskId"] = "m", ["checkpoint"] = new JsonObject { ["reason"] = 1 } };
        Assert.Equal("user-paused", Step(Settings(), numericReason)["reason"]!.GetValue<string>());

        var fresh = Autonomy.CreateSettings("s1", "2026-11-03T10:00:00Z");
        fresh["started"] = 1;
        Assert.Equal("idle", Step(fresh, new JsonObject { ["status"] = "idle" })["reason"]!.GetValue<string>());

        var approved = Step(Settings(), new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = 7 });
        Assert.Equal("approve", approved["type"]!.GetValue<string>());
        Assert.Equal(7, approved["megaTaskId"]!.GetValue<int>());

        var ignored = new JsonArray { new JsonObject { ["key"] = "k", ["ignored"] = 1, ["title"] = "t" } };
        Assert.Equal("idle", Step(Settings(), watching, ignored, includeAlerts: true)["reason"]!.GetValue<string>());

        var otherSession = new JsonObject
        {
            ["mode"] = "auto",
            ["sessionId"] = "1",
            ["settings"] = new JsonObject { ["enabled"] = true, ["sessionId"] = 1, ["started"] = true, ["approved"] = new JsonArray(), ["evaluated"] = new JsonArray(), ["nudgedKeys"] = new JsonArray(), ["nudgeTimes"] = new JsonArray() },
            ["row"] = new JsonObject { ["status"] = "watching" },
            ["alerts"] = new JsonArray()
        };
        Assert.Equal("other-session", Autonomy.NextStep(otherSession)["reason"]!.GetValue<string>());
    }

    [Fact]
    public void Next_step_throws_when_JavaScript_would_and_a_null_clock_is_not_now()
    {
        var bare = new JsonObject { ["enabled"] = true, ["sessionId"] = "s1", ["started"] = true };
        var approval = new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m" };
        var missingApproved = Assert.Throws<InvalidOperationException>(() => Step(bare, approval));
        Assert.Equal("Cannot read properties of undefined (reading 'includes')", missingApproved.Message);

        var missingEvaluated = Assert.Throws<InvalidOperationException>(() => Step(bare, new JsonObject { ["status"] = "complete", ["megaTaskId"] = "m" }));
        Assert.Equal("Cannot read properties of undefined (reading 'includes')", missingEvaluated.Message);

        var alert = new JsonArray { new JsonObject { ["key"] = "k", ["title"] = "t", ["message"] = "m" } };
        var noKeys = Settings();
        noKeys.Remove("nudgedKeys");
        var missingKeys = Assert.Throws<InvalidOperationException>(() => Step(noKeys, new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m" }, alert, includeAlerts: true));
        Assert.Equal("Cannot read properties of undefined (reading 'includes')", missingKeys.Message);

        var noTimes = Settings();
        noTimes.Remove("nudgeTimes");
        var missingTimes = Assert.Throws<InvalidOperationException>(() => Step(noTimes, new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m" }, alert, includeAlerts: true));
        Assert.Equal("Cannot read properties of undefined (reading 'filter')", missingTimes.Message);

        var nullAlerts = Assert.Throws<InvalidOperationException>(() => Step(Settings(), new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m" }, null, includeAlerts: true));
        Assert.Equal("Cannot read properties of null (reading 'find')", nullAlerts.Message);

        var nullItem = Assert.Throws<InvalidOperationException>(() => Step(Settings(), new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m" }, new JsonArray { null }, includeAlerts: true));
        Assert.Equal("Cannot read properties of null (reading 'active')", nullItem.Message);

        var times = new JsonArray { "2000-01-01T00:00:00.000Z", "2000-01-01T00:00:00.000Z", "2000-01-01T00:00:00.000Z" };
        var clock = Settings(new JsonObject { ["nudgeTimes"] = times });
        Assert.Equal("nudge-limit", Step(clock, new JsonObject { ["status"] = "watching", ["megaTaskId"] = "m" }, alert, null, true, true)["reason"]!.GetValue<string>());

        var bySubstring = Settings(new JsonObject { ["approved"] = "m1-extra" });
        Assert.Equal("already-approved", Step(bySubstring, new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m1" })["reason"]!.GetValue<string>());
        var byMiss = Settings(new JsonObject { ["approved"] = "nope" });
        Assert.Equal("approve", Step(byMiss, new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = "m1" })["type"]!.GetValue<string>());
    }

    [Fact]
    public void History_errors_match_JavaScript_and_strict_ids_stay_distinct()
    {
        JsonArray Events(JsonNode id) => new()
        {
            new JsonObject { ["id"] = "e", ["createdAt"] = "2026-01-01T00:00:00.000Z", ["reference"] = new JsonObject { ["megaTaskId"] = id.DeepClone() } }
        };

        var missingMega = Assert.Throws<InvalidOperationException>(() => ProjectHistory.SummarizeTimeline(Events("m"), new JsonObject()));
        Assert.Equal("Cannot read properties of undefined (reading 'find')", missingMega.Message);

        var alone = ProjectHistory.SummarizeTimeline(Events("unknown"), new JsonObject { ["megaTasks"] = new JsonArray(), ["results"] = new JsonArray() });
        Assert.Equal("e", alone[0]!["id"]!.GetValue<string>());

        var missingResults = Assert.Throws<InvalidOperationException>(() => ProjectHistory.SummarizeTimeline(Events("m"), new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["title"] = "T", ["status"] = "OPEN" } }
        }));
        Assert.Equal("Cannot read properties of undefined (reading 'filter')", missingResults.Message);

        var nullMega = Assert.Throws<InvalidOperationException>(() => ProjectHistory.SummarizeTimeline(Events("m"), new JsonObject
        {
            ["megaTasks"] = new JsonArray { null },
            ["results"] = new JsonArray()
        }));
        Assert.Equal("Cannot read properties of null (reading 'id')", nullMega.Message);

        var nullResult = Assert.Throws<InvalidOperationException>(() => ProjectHistory.SummarizeTimeline(Events("m"), new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = "m", ["title"] = "T", ["status"] = "OPEN" } },
            ["results"] = new JsonArray { null }
        }));
        Assert.Equal("Cannot read properties of null (reading 'megaTaskId')", nullResult.Message);

        var falsy = new JsonArray
        {
            new JsonObject { ["id"] = "e", ["createdAt"] = "2026-01-01", ["reference"] = new JsonObject { ["megaTaskId"] = false } },
            new JsonObject { ["id"] = "z", ["createdAt"] = "2026-01-02", ["reference"] = new JsonObject { ["megaTaskId"] = 0 } },
            new JsonObject { ["id"] = "n", ["createdAt"] = "2026-01-03", ["reference"] = new JsonObject { ["megaTaskId"] = "" } }
        };
        var kept = ProjectHistory.SummarizeTimeline(falsy, new JsonObject { ["megaTasks"] = new JsonArray(), ["results"] = new JsonArray() });
        Assert.Equal(["e", "z", "n"], kept.Select(node => node!["id"]!.GetValue<string>()));

        var split = ProjectHistory.SummarizeTimeline(new JsonArray
        {
            new JsonObject { ["id"] = "num", ["createdAt"] = "2026-01-02", ["reference"] = new JsonObject { ["megaTaskId"] = 1 } },
            new JsonObject { ["id"] = "str", ["createdAt"] = "2026-01-01", ["reference"] = new JsonObject { ["megaTaskId"] = "1" } }
        }, new JsonObject
        {
            ["megaTasks"] = new JsonArray
            {
                new JsonObject { ["id"] = 1, ["title"] = "N", ["status"] = "OPEN" },
                new JsonObject { ["id"] = "1", ["title"] = "S", ["status"] = "OPEN" }
            },
            ["results"] = new JsonArray()
        });
        Assert.Equal(["S: OPEN.", "N: OPEN."], split.Select(node => node!["summary"]!.GetValue<string>()));

        var flagged = ProjectHistory.SummarizeTimeline(Events(JsonValue.Create(true)!), new JsonObject
        {
            ["megaTasks"] = new JsonArray { new JsonObject { ["id"] = true, ["title"] = "T", ["status"] = "OPEN" } },
            ["results"] = new JsonArray()
        });
        Assert.Equal("milestone:true", flagged[0]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Checkpoint_diff_counts_like_JavaScript_includes_and_length()
    {
        var before = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "a", ["comparable"] = true };
        var current = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "a", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() };
        var mixed = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = (JsonObject)before.DeepClone(),
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray { 1, "kept" }
        }, (JsonObject)current.DeepClone(), 1, new JsonArray { "new", 1 });
        Assert.Equal("1 previously pending SubTask(s) already completed; these will not be replayed", mixed["changes"]![0]!.GetValue<string>());

        var textIds = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = (JsonObject)before.DeepClone(),
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = "done"
        }, (JsonObject)current.DeepClone(), 1, new JsonArray { "d", "done", "x" });
        Assert.Equal("1 previously pending SubTask(s) already completed; these will not be replayed", textIds["changes"]![0]!.GetValue<string>());

        var heads = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = 0, ["fingerprint"] = "a", ["comparable"] = true },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        }, new JsonObject { ["repoHead"] = false, ["fingerprint"] = "a", ["comparable"] = true, ["dirtyFiles"] = new JsonArray() }, 1);
        Assert.Equal("HEAD changed from unavailable to unavailable", heads["changes"]![0]!.GetValue<string>());

        var dirty = ProjectHistory.CheckpointDiff(new JsonObject
        {
            ["repoState"] = new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "old", ["comparable"] = true },
            ["planRevisionId"] = 1,
            ["completedSubTaskIds"] = new JsonArray()
        }, new JsonObject { ["repoHead"] = "a", ["fingerprint"] = "new", ["comparable"] = true, ["dirtyFiles"] = "ab" }, 1);
        Assert.Equal("External repository changes detected; 2 files currently changed locally", dirty["changes"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Evidence_accepts_non_string_identity_and_integer_exit_codes()
    {
        var unknown = ActivityEvidence.TechnicalEvidence("hello", new JsonObject { ["adapter"] = 1, ["provider"] = "codex" });
        Assert.Contains("Adapter identity is unknown", unknown["warnings"]!.ToJsonString());
        Assert.Equal("hello", unknown["entries"]![0]!["output"]!.GetValue<string>());

        var inherited = ActivityEvidence.TechnicalEvidence("hello", new JsonObject { ["adapter"] = 0, ["provider"] = "codex" });
        Assert.DoesNotContain("Adapter identity is unknown", inherited["warnings"]!.ToJsonString());

        var numericType = ActivityEvidence.TechnicalEvidence("""{"type":1,"item":{"type":"command_execution","command":"echo"}}""", new JsonObject { ["adapter"] = "codex" });
        Assert.Contains("Unsupported structured payload omitted", numericType["warnings"]!.ToJsonString());
        Assert.Empty(numericType["entries"]!.AsArray());

        var wide = ActivityEvidence.TechnicalEvidence("""{"type":"item.completed","item":{"type":"command_execution","command":"echo","exit_code":2147483648}}""", new JsonObject { ["adapter"] = "codex" });
        Assert.Contains("\"exitCode\":2147483648", wide["entries"]![0]!.ToJsonString());

        var negative = ActivityEvidence.TechnicalEvidence("""{"type":"item.completed","item":{"type":"command_execution","exit_code":-1}}""", new JsonObject { ["adapter"] = "codex" });
        Assert.Equal(-1, negative["entries"]![0]!["exitCode"]!.GetValue<int>());

        var tool = ActivityEvidence.TechnicalEvidence("""{"step_update":{"step_type":"tool","tool_name":1,"state":"DONE"}}""", new JsonObject { ["adapter"] = "antigravity" });
        Assert.Equal("DONE", tool["entries"]![0]!["state"]!.GetValue<string>());
        Assert.False(tool["entries"]![0]!.AsObject().ContainsKey("tool"));

        var nullContext = Assert.Throws<InvalidOperationException>(() => ActivityEvidence.TechnicalEvidence("hi", null));
        Assert.Equal("Cannot read properties of null (reading 'adapter')", nullContext.Message);
    }

    [Fact]
    public void Split_assignment_and_briefing_reject_the_values_JavaScript_rejects()
    {
        var split = Assert.Throws<InvalidOperationException>(() => Autonomy.ParseSplit(
            """{"parts":[{"title":"t","detail":"d","files":["a"],"size":"light","model":"b"}],"verify":"v"}""",
            new JsonObject()));
        Assert.Equal("Cannot read properties of undefined (reading 'length')", split.Message);

        var weights = Assert.Throws<InvalidOperationException>(() => Autonomy.FitWeights(new JsonArray { null }, new JsonArray { new JsonObject { ["id"] = "a", ["tier"] = "light" } }));
        Assert.Equal("Cannot read properties of null (reading 'size')", weights.Message);

        var assignment = Assert.Throws<InvalidOperationException>(() => Autonomy.ParseAssignment("{\"assign\":[\"a\"]}", 1, null));
        Assert.Equal("Cannot read properties of null (reading '0')", assignment.Message);

        Assert.ThrowsAny<Exception>(() => Briefing.CompactState(new JsonObject { ["focus"] = new JsonObject { ["id"] = "p", ["plan"] = "nope" } }));
        Assert.ThrowsAny<Exception>(() => Briefing.CompactState(new JsonObject { ["others"] = new JsonArray { null } }));

        var delta = Briefing.CompactState(new JsonObject { ["focus"] = null }, JsonValue.Create(0));
        Assert.True(delta["repositoryDelta"]!["initialInspection"]!.GetValue<bool>());
        Assert.Null(delta["repositoryDelta"]!["current"]);
    }

    [Fact]
    public void Independent_calls_can_run_together()
    {
        var gate = new System.Threading.Barrier(6);
        var reasons = new string[6];
        var errors = new Exception?[6];
        System.Threading.Tasks.Parallel.For(0, 6, i =>
        {
            try
            {
                gate.SignalAndWait();
                if (i % 3 == 0)
                {
                    var evidence = ActivityEvidence.TechnicalEvidence("{\"type\":1}\nplain " + i, new JsonObject { ["adapter"] = i });
                    reasons[i] = evidence["entries"]![0]!["output"]!.GetValue<string>();
                }
                else if (i % 3 == 1)
                {
                    var brief = Briefing.CompactState(new JsonObject { ["focus"] = new JsonObject { ["id"] = "p" + i, ["lastCommit"] = "h" } }, JsonValue.Create(false));
                    reasons[i] = brief["repositoryDelta"]!["initialInspection"]!.GetValue<bool>() ? "initial" : "delta";
                }
                else
                {
                    var step = Step(Settings(), new JsonObject { ["status"] = "needs_approval", ["megaTaskId"] = i });
                    reasons[i] = step["type"]!.GetValue<string>() + ":" + step["megaTaskId"]!.ToJsonString();
                }
            }
            catch (Exception ex) { errors[i] = ex; }
        });
        Assert.All(errors, error => Assert.Null(error));
        Assert.Equal("plain 0", reasons[0]);
        Assert.Equal("initial", reasons[1]);
        Assert.Equal("approve:2", reasons[2]);
        Assert.Equal("plain 3", reasons[3]);
        Assert.Equal("initial", reasons[4]);
        Assert.Equal("approve:5", reasons[5]);
    }
}
