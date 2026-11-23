// Tests for Attempts.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class AttemptsTests : IDisposable
{
    private readonly string _testDir;

    public AttemptsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "pw_attempts_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }

    [Fact]
    public void Claim_ValidCandidate_AllocatesAttemptWithIncrementalId()
    {
        var engine = AttemptEngine.Create(_testDir);
        var candidates = new JsonArray
        {
            new JsonObject { ["id"] = "model-1", ["enabled"] = true }
        };
        var input = new JsonObject
        {
            ["taskId"] = "task-1",
            ["projectId"] = "proj-1",
            ["modelId"] = "model-1",
            ["candidates"] = candidates
        };

        var result = engine.Claim(input);
        Assert.True(result["ok"]?.GetValue<bool>());
        var attempt = result["attempt"] as JsonObject;
        Assert.NotNull(attempt);
        Assert.Equal("attempt-1", attempt["id"]?.GetValue<string>());
        Assert.Equal("active", attempt["status"]?.GetValue<string>());

        var attempts = engine.GetAttempts();
        Assert.Single(attempts);
        Assert.Equal("attempt-1", attempts[0]["id"]?.GetValue<string>());
    }

    [Fact]
    public void Claim_ModelNotEligibleOrDisabled_RejectsWithExplicitReason()
    {
        var engine = AttemptEngine.Create(_testDir);

        // No model enabled
        var input1 = new JsonObject
        {
            ["taskId"] = "task-1",
            ["projectId"] = "proj-1",
            ["modelId"] = "model-1",
            ["candidates"] = new JsonArray
            {
                new JsonObject { ["id"] = "model-1", ["enabled"] = false }
            }
        };
        var res1 = engine.Claim(input1);
        Assert.False(res1["ok"]?.GetValue<bool>());
        Assert.Equal("no model enabled", res1["reason"]?.GetValue<string>());

        // Model not present
        var input2 = new JsonObject
        {
            ["taskId"] = "task-1",
            ["projectId"] = "proj-1",
            ["modelId"] = "model-missing",
            ["candidates"] = new JsonArray
            {
                new JsonObject { ["id"] = "model-1", ["enabled"] = true }
            }
        };
        var res2 = engine.Claim(input2);
        Assert.False(res2["ok"]?.GetValue<bool>());
        Assert.Equal("model not present", res2["reason"]?.GetValue<string>());

        // Model not eligible
        var input3 = new JsonObject
        {
            ["taskId"] = "task-1",
            ["projectId"] = "proj-1",
            ["modelId"] = "model-2",
            ["candidates"] = new JsonArray
            {
                new JsonObject { ["id"] = "model-1", ["enabled"] = true },
                new JsonObject { ["id"] = "model-2", ["enabled"] = false }
            }
        };
        var res3 = engine.Claim(input3);
        Assert.False(res3["ok"]?.GetValue<bool>());
        Assert.Equal("model not eligible", res3["reason"]?.GetValue<string>());
    }

    [Fact]
    public void Claim_ConcurrentSubTasks_MegaTaskDisallowsSecondActiveUnlessDifferentSubTaskId()
    {
        var engine = AttemptEngine.Create(_testDir);
        var candidates = new JsonArray { new JsonObject { ["id"] = "m1", ["enabled"] = true } };

        var input1 = new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = (JsonArray)candidates.DeepClone(),
            ["megaTaskId"] = "MT-1",
            ["subTaskId"] = "ST-1"
        };
        var res1 = engine.Claim(input1);
        Assert.True(res1["ok"]?.GetValue<bool>());

        // Same project, same megaTaskId, different subTaskId -> allowed
        var input2 = new JsonObject
        {
            ["taskId"] = "t2",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = (JsonArray)candidates.DeepClone(),
            ["megaTaskId"] = "MT-1",
            ["subTaskId"] = "ST-2"
        };
        var res2 = engine.Claim(input2);
        Assert.True(res2["ok"]?.GetValue<bool>());

        // Same project, no megaTaskId -> rejected (project already assigned)
        var input3 = new JsonObject
        {
            ["taskId"] = "t3",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = (JsonArray)candidates.DeepClone()
        };
        var res3 = engine.Claim(input3);
        Assert.False(res3["ok"]?.GetValue<bool>());
        Assert.Equal("project already assigned", res3["reason"]?.GetValue<string>());
    }

    [Fact]
    public void CloseAttempt_ValidActive_MarksClosedWithCustomReason()
    {
        var engine = AttemptEngine.Create(_testDir);
        var candidates = new JsonArray { new JsonObject { ["id"] = "m1", ["enabled"] = true } };
        var res1 = engine.Claim(new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = candidates
        });
        var attemptId = res1["attempt"]!["id"]!.GetValue<string>();

        var closeRes = engine.CloseAttempt(new JsonObject
        {
            ["attemptId"] = attemptId,
            ["reason"] = "completed normally"
        });
        Assert.True(closeRes["ok"]?.GetValue<bool>());
        Assert.Equal("closed", closeRes["attempt"]!["status"]?.GetValue<string>());
        Assert.Equal("completed normally", closeRes["attempt"]!["closedReason"]?.GetValue<string>());

        var repeatClose = engine.CloseAttempt(new JsonObject { ["attemptId"] = attemptId });
        Assert.False(repeatClose["ok"]?.GetValue<bool>());
        Assert.Equal("attempt not active", repeatClose["reason"]?.GetValue<string>());
    }

    [Fact]
    public void AttachResult_ValidAttempt_BindsPathStatusAndExitCode()
    {
        var engine = AttemptEngine.Create(_testDir);
        var candidates = new JsonArray { new JsonObject { ["id"] = "m1", ["enabled"] = true } };
        var res1 = engine.Claim(new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = candidates
        });
        var attemptId = res1["attempt"]!["id"]!.GetValue<string>();

        var attachRes = engine.AttachResult(new JsonObject
        {
            ["attemptId"] = attemptId,
            ["resultPath"] = "/path/to/result.json",
            ["status"] = "PASSED",
            ["exitCode"] = 0
        });
        Assert.True(attachRes["ok"]?.GetValue<bool>());
        var att = attachRes["attempt"]!;
        Assert.Equal("/path/to/result.json", att["resultPath"]?.GetValue<string>());
        Assert.Equal("PASSED", att["resultStatus"]?.GetValue<string>());
        Assert.Equal(0, att["exitCode"]?.GetValue<int>());

        var retrieved = engine.GetResult(attemptId);
        Assert.NotNull(retrieved);
        Assert.Equal("/path/to/result.json", retrieved["resultPath"]?.GetValue<string>());
    }

    [Fact]
    public void EmptyOrCorruptStore_RecoversToEmptyStoreWithNextId1()
    {
        File.WriteAllText(Path.Combine(_testDir, "attempts.json"), "{ invalid json ");
        var engine = AttemptEngine.Create(_testDir);
        var attempts = engine.GetAttempts();
        Assert.Empty(attempts);

        var candidates = new JsonArray { new JsonObject { ["id"] = "m1", ["enabled"] = true } };
        var claimed = engine.Claim(new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = candidates
        });
        Assert.True(claimed["ok"]?.GetValue<bool>());
        Assert.Equal("attempt-1", claimed["attempt"]!["id"]?.GetValue<string>());
    }
}
