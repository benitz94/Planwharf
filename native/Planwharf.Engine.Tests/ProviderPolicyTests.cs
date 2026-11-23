// Tests for ProviderPolicy.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderPolicyTests
{
    [Fact]
    public void ValidateProviderPolicy_UnknownKeys_ThrowsInvalidProviderPolicyFields()
    {
        var input = new JsonObject { ["unknownKey"] = 123 };
        var ex = Assert.Throws<InvalidOperationException>(() => ProviderPolicy.ValidateProviderPolicy(input));
        Assert.Equal("Invalid provider policy fields.", ex.Message);
    }

    [Fact]
    public void ValidateProviderPolicy_InvalidFallback_ThrowsExplicitProviderIdOrNull()
    {
        var input = new JsonObject { ["fallbackProviderId"] = "   " };
        var ex = Assert.Throws<InvalidOperationException>(() => ProviderPolicy.ValidateProviderPolicy(input));
        Assert.Equal("Fallback must be an explicit provider ID or null.", ex.Message);
    }

    [Fact]
    public void ProviderPolicyFor_SavedSettings_ReturnsSavedPolicy()
    {
        var provider = new JsonObject { ["id"] = "claude" };
        var settings = new JsonObject
        {
            ["providerPolicies"] = new JsonObject
            {
                ["claude"] = new JsonObject { ["minRemainingPercent"] = 20, ["alwaysApprove"] = true }
            }
        };

        var result = ProviderPolicy.ProviderPolicyFor(provider, new JsonArray(), settings);
        Assert.Equal("saved", result["source"]?.GetValue<string>());
        var policy = result["policy"] as JsonObject;
        Assert.NotNull(policy);
        Assert.Equal(20.0, Convert.ToDouble(policy["minRemainingPercent"]!.ToString()));
        Assert.True(policy["alwaysApprove"]?.GetValue<bool>());
    }

    [Fact]
    public void ProviderPolicyFor_LegacyModelPolicies_AggregatesMinAndWarningThresholds()
    {
        var provider = new JsonObject { ["id"] = "codex" };
        var models = new JsonArray
        {
            new JsonObject { ["id"] = "gpt-4", ["provider"] = "codex", ["policy"] = new JsonObject { ["minRemainingPercent"] = 15, ["warningRemainingPercent"] = 25, ["alwaysApprove"] = true } },
            new JsonObject { ["id"] = "gpt-3.5", ["provider"] = "codex", ["policy"] = new JsonObject { ["minRemainingPercent"] = 30, ["warningRemainingPercent"] = 40, ["alwaysApprove"] = true } }
        };

        var result = ProviderPolicy.ProviderPolicyFor(provider, models, null);
        Assert.Equal("legacy", result["source"]?.GetValue<string>());
        var policy = result["policy"] as JsonObject;
        Assert.NotNull(policy);
        Assert.Equal(30.0, Convert.ToDouble(policy["minRemainingPercent"]!.ToString()));
        Assert.Equal(40.0, Convert.ToDouble(policy["warningRemainingPercent"]!.ToString()));
        Assert.True(policy["alwaysApprove"]?.GetValue<bool>());
    }

    [Fact]
    public void ProviderPolicyFor_Default_ReturnsZeroedDefaults()
    {
        var provider = new JsonObject { ["id"] = "grok" };
        var result = ProviderPolicy.ProviderPolicyFor(provider, new JsonArray(), null);
        Assert.Equal("default", result["source"]?.GetValue<string>());
        var policy = result["policy"] as JsonObject;
        Assert.NotNull(policy);
        Assert.Null(policy["minRemainingPercent"]);
        Assert.False(policy["alwaysApprove"]?.GetValue<bool>());
    }
}
