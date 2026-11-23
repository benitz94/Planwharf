// Tests for ProviderCatalog.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderCatalogTests
{
    [Fact]
    public void LoadCatalog_Schema3Explicit_PreservesProvidersAndBindsModels()
    {
        var config = new JsonObject
        {
            ["schema"] = 3,
            ["providers"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "claude",
                    ["adapter"] = "claude",
                    ["models"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "claude-3-5-sonnet", ["cli"] = "claude-3-5-sonnet" }
                    }
                }
            }
        };

        var catalog = ProviderCatalog.LoadCatalog(config);
        Assert.Single(catalog.Providers);
        Assert.Single(catalog.Models);
        Assert.Equal("claude", catalog.Models[0]["providerId"]?.GetValue<string>());
    }

    [Fact]
    public void LoadCatalog_LegacyFlatList_SynthesizesProvidersWithDefaultPools()
    {
        var models = new JsonArray
        {
            new JsonObject { ["id"] = "m1", ["provider"] = "codex", ["cli"] = "gpt-4" }
        };

        var catalog = ProviderCatalog.LoadCatalog(models);
        Assert.Single(catalog.Providers);
        Assert.Equal("ChatGPT", catalog.Providers[0]["label"]?.GetValue<string>());
        Assert.Equal("ChatGPT", catalog.Providers[0]["quotaPoolId"]?.GetValue<string>());
    }

    [Fact]
    public void LoadCatalog_DuplicateProviderOrModelIds_ThrowsValidationException()
    {
        var config = new JsonObject
        {
            ["schema"] = 3,
            ["providers"] = new JsonArray
            {
                new JsonObject { ["id"] = "p1", ["models"] = new JsonArray() },
                new JsonObject { ["id"] = "p1", ["models"] = new JsonArray() }
            }
        };

        Assert.Throws<InvalidOperationException>(() => ProviderCatalog.LoadCatalog(config));
    }

    [Fact]
    public void ProviderViews_ComputesAvailability_InstalledAvailableExhaustedUnread()
    {
        var providers = new List<JsonObject>
        {
            new JsonObject { ["id"] = "claude", ["quotaPoolId"] = "claude", ["adapter"] = "claude" }
        };
        var models = new List<JsonObject>
        {
            new JsonObject { ["id"] = "m1", ["providerId"] = "claude", ["available"] = true }
        };
        var plans = new List<JsonObject>
        {
            new JsonObject { ["id"] = "claude", ["readable"] = true, ["leftPct"] = 0.0, ["exhausted"] = true }
        };

        var views = ProviderCatalog.ProviderViews(providers, models, plans);
        Assert.Single(views);
        Assert.Equal("EXHAUSTED", views[0]["availability"]?.GetValue<string>());
    }

    [Fact]
    public void NotInstalledReason_FormatsOfficialToolInstallHint()
    {
        var provider = new JsonObject { ["adapter"] = "claude", ["id"] = "claude" };
        var hint = ProviderCatalog.NotInstalledReason(provider);
        Assert.Contains("Claude Code is not installed on this computer.", hint);
        Assert.Contains("npm install -g @anthropic-ai/claude-code", hint);
    }
}
