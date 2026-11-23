// Tests for Claims.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ClaimsTests
{
    [Fact]
    public void GetActiveAttempt_RequiresNonEmptyTaskIdAndProjectId()
    {
        var claims = ClaimEngine.Create();
        Assert.Null(claims.GetActiveAttempt(null));
        Assert.Null(claims.GetActiveAttempt(new JsonObject { ["taskId"] = "", ["projectId"] = "p1" }));
        Assert.Null(claims.GetActiveAttempt(new JsonObject { ["taskId"] = "t1", ["projectId"] = "   " }));
    }

    [Fact]
    public void Claim_GlobalTaskIdActiveCollision_RejectsWithTaskAlreadyAssigned()
    {
        var claims = ClaimEngine.Create();
        var candidates = new JsonArray { new JsonObject { ["id"] = "m1", ["enabled"] = true } };

        var res1 = claims.Claim(new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = (JsonArray)candidates.DeepClone()
        });
        Assert.True(res1["ok"]?.GetValue<bool>());

        // Same taskId across different project
        var res2 = claims.Claim(new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p2",
            ["modelId"] = "m1",
            ["candidates"] = (JsonArray)candidates.DeepClone()
        });
        Assert.False(res2["ok"]?.GetValue<bool>());
        Assert.Equal("task already assigned", res2["reason"]?.GetValue<string>());
    }

    [Fact]
    public void Claim_DelegatesToUnderlyingAttemptEngine()
    {
        var claims = ClaimEngine.Create();
        var candidates = new JsonArray { new JsonObject { ["id"] = "m1", ["enabled"] = true } };

        var res1 = claims.Claim(new JsonObject
        {
            ["taskId"] = "t1",
            ["projectId"] = "p1",
            ["modelId"] = "m1",
            ["candidates"] = candidates
        });
        Assert.True(res1["ok"]?.GetValue<bool>());
        Assert.Single(claims.GetAttempts());

        var active = claims.GetActiveAttempt(new JsonObject { ["taskId"] = "t1", ["projectId"] = "p1" });
        Assert.NotNull(active);
        Assert.Equal("attempt-1", active["id"]?.GetValue<string>());
    }
}
