// Tests for ModelRouting.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ModelRoutingTests
{
    [Fact]
    public void ModelRouteError_NullOrUnavailable_ReturnsReason()
    {
        Assert.Equal("Model configuration is required", ModelRouting.ModelRouteError(null));

        var unavailable = new JsonObject { ["available"] = false, ["unavailableReason"] = "No API key" };
        Assert.Equal("No API key", ModelRouting.ModelRouteError(unavailable));
    }

    [Fact]
    public void ModelRouteError_UnsupportedAdapter_ReturnsMismatchMessage()
    {
        var model = new JsonObject { ["provider"] = "unknown_cli", ["cli"] = "model-1" };
        var err = ModelRouting.ModelRouteError(model);
        Assert.Contains("unsupported provider adapter", err);
    }

    [Fact]
    public void ModelRouteError_CliIdControlCharsOrTooLong_Rejects()
    {
        var modelControl = new JsonObject { ["provider"] = "claude", ["cli"] = "claude-3\nopus" };
        var err1 = ModelRouting.ModelRouteError(modelControl);
        Assert.Contains("an exact nonempty CLI model ID is required", err1);

        var longId = "claude-" + new string('a', 600);
        var modelLong = new JsonObject { ["provider"] = "claude", ["cli"] = longId };
        var err2 = ModelRouting.ModelRouteError(modelLong);
        Assert.Equal("CLI model ID exceeds the 512-byte configuration limit", err2);
    }

    [Fact]
    public void ModelRouteError_UnconfiguredNativeMismatch_ReturnsSetModelRoutingConfigured()
    {
        var model = new JsonObject { ["provider"] = "claude", ["cli"] = "not-a-claude-model" };
        var err = ModelRouting.ModelRouteError(model);
        Assert.Contains("Set modelRouting to \"configured\" only when this exact CLI supports the model.", err);

        model["modelRouting"] = "configured";
        Assert.Null(ModelRouting.ModelRouteError(model));
    }

    [Fact]
    public void SavingModeError_UltraSafeWithFlagship_ReturnsDisallowedExplanation()
    {
        var model = new JsonObject
        {
            ["id"] = "claude-opus",
            ["label"] = "Claude Opus",
            ["weightSource"] = "team",
            ["weight"] = "premium"
        };

        var err = ModelRouting.SavingModeError(model, "ultra-safe");
        Assert.NotNull(err);
        Assert.Equal("Claude Opus is flagship, and Ultra safe does not allow it.", err);

        Assert.Null(ModelRouting.SavingModeError(model, "normal"));
    }
}
