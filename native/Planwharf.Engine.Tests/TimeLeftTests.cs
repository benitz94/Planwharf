// Tests for TimeLeft.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class TimeLeftTests
{
    [Fact]
    public void TypicalMinutes_SameRoleAndModel_PrefersSpecificMedian()
    {
        var history = new List<JsonObject>
        {
            new JsonObject { ["role"] = "Build", ["model"] = "sonnet", ["minutes"] = 15.0 },
            new JsonObject { ["role"] = "Build", ["model"] = "sonnet", ["minutes"] = 25.0 },
            new JsonObject { ["role"] = "Build", ["model"] = "other", ["minutes"] = 50.0 }
        };

        var step = new JsonObject { ["role"] = "Build", ["model"] = "sonnet" };
        var typical = TimeLeft.TypicalMinutes(step, history);
        Assert.Equal(20.0, typical);
    }

    [Fact]
    public void Estimate_ParallelIndependentSteps_TakesMaximumCriticalPath()
    {
        var steps = new List<JsonObject>
        {
            new JsonObject { ["id"] = "s1", ["role"] = "Build", ["status"] = "PENDING" },
            new JsonObject { ["id"] = "s2", ["role"] = "Verify", ["status"] = "PENDING" }
        };

        var est = TimeLeft.Estimate(null, steps);
        Assert.NotNull(est);
        Assert.Equal(20, est.Minutes); // Build is 20, Verify is 10; max is 20
    }

    [Fact]
    public void Estimate_DependentChainedSteps_SumsSequentialDurations()
    {
        var steps = new List<JsonObject>
        {
            new JsonObject { ["id"] = "s1", ["role"] = "Build", ["status"] = "PENDING" },
            new JsonObject { ["id"] = "s2", ["role"] = "Verify", ["status"] = "PENDING", ["dependencies"] = new JsonArray { "s1" } }
        };

        var est = TimeLeft.Estimate(null, steps);
        Assert.NotNull(est);
        Assert.Equal(30, est.Minutes); // 20 + 10 = 30
    }

    [Fact]
    public void Estimate_ActiveStepRunningLongerThanTypical_EnforcesFloorOf2Minutes()
    {
        var now = "2026-11-23T12:00:00.000Z";
        var startedAt = "2026-11-23T11:00:00.000Z"; // 60 minutes ago
        var steps = new List<JsonObject>
        {
            new JsonObject { ["id"] = "s1", ["role"] = "Build", ["status"] = "RUNNING", ["startedAt"] = startedAt }
        };

        var est = TimeLeft.Estimate(null, steps, now);
        Assert.NotNull(est);
        Assert.Equal(2, est.Minutes);
        Assert.True(est.LongerThanUsual);
    }

    [Fact]
    public void ParseHistory_And_HistoryText_RoundTripsJsonlDeterministically()
    {
        var text = "{\"role\":\"Build\",\"model\":\"m1\",\"files\":3,\"minutes\":15,\"outcome\":\"done\"}\n";
        var parsed = TimeLeft.ParseHistory(text);
        Assert.Single(parsed);
        var res = TimeLeft.HistoryText(parsed);
        Assert.Equal(text, res);
    }
}
