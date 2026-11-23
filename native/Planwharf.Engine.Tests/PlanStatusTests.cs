// Tests for PlanStatus.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class PlanStatusTests
{
    [Fact]
    public void StatusFrom_ReadableResponse_ReturnsActive()
    {
        var resp = new JsonObject { ["readable"] = true };
        Assert.Equal("active", PlanStatus.StatusFrom("claude", resp));
    }

    [Fact]
    public void StatusFrom_ExpiredPattern_ReturnsExpired()
    {
        var resp = new JsonObject { ["reason"] = "Your subscription has expired." };
        Assert.Equal("expired", PlanStatus.StatusFrom("claude", resp));
    }

    [Fact]
    public void StatusFrom_SignedOutPattern_ReturnsNotSignedIn()
    {
        var resp = new JsonObject { ["reason"] = "User is not logged in." };
        Assert.Equal("not signed in", PlanStatus.StatusFrom("codex", resp));
    }

    [Fact]
    public void NotePlanRefusal_ExpiredModel_UpdatesPoolStatusAndReason()
    {
        var plans = new JsonArray();
        var model = new JsonObject
        {
            ["provider"] = "claude",
            ["quotaPoolId"] = "claude-sub",
            ["companyId"] = "Anthropic"
        };

        var (updatedPlans, status) = PlanStatus.NotePlanRefusal(plans, model, "subscription is expired");
        Assert.Equal("expired", status);
        Assert.Single(updatedPlans);
        var row = updatedPlans[0] as JsonObject;
        Assert.NotNull(row);
        Assert.Equal("claude-sub", row["id"]?.GetValue<string>());
        Assert.Equal("expired", row["planStatus"]?.GetValue<string>());
        Assert.Equal("Anthropic plan expired.", row["reason"]?.GetValue<string>());
    }

    [Fact]
    public void MergePlans_PreservesExpiredOrNotSignedInAcrossRefreshes()
    {
        var prev = new JsonArray
        {
            new JsonObject { ["id"] = "claude", ["planStatus"] = "expired", ["reason"] = "Subscription expired" }
        };
        var next = new JsonArray
        {
            new JsonObject { ["id"] = "claude", ["readable"] = false }
        };

        var merged = PlanStatus.MergePlans(prev, next);
        Assert.Single(merged);
        var plan = merged[0] as JsonObject;
        Assert.NotNull(plan);
        Assert.Equal("expired", plan["planStatus"]?.GetValue<string>());
        Assert.Equal("Subscription expired", plan["reason"]?.GetValue<string>());
    }
}
