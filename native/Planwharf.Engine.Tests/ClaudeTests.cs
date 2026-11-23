// Tests for Claude.cs.
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ClaudeTests
{
    [Fact]
    public void ClaudeToolArgs_AllDisabled_SetsNoChromeAndStrictMcp()
    {
        var args = Claude.ClaudeToolArgs(new JsonObject());
        Assert.Contains("--no-chrome", args);
        Assert.Contains("--strict-mcp-config", args);
    }

    [Fact]
    public void ClaudeToolArgs_McpAndBrowserEnabled_RemovesStrictMcpAndEnablesChrome()
    {
        var tools = new JsonObject
        {
            ["mcp"] = true,
            ["browser"] = true
        };
        var args = Claude.ClaudeToolArgs(tools);
        Assert.Contains("--chrome", args);
        Assert.DoesNotContain("--no-chrome", args);
        Assert.DoesNotContain("--strict-mcp-config", args);
    }

    [Fact]
    public void ParseModels_ValidStreamResponse_ExtractsModelIdsAndLabels()
    {
        var rows = new JsonArray
        {
            new JsonObject { ["value"] = "claude-3-5-sonnet", ["displayName"] = "Claude 3.5 Sonnet" }
        };

        var (ok, reason, models) = Claude.ParseModels(rows);
        Assert.True(ok);
        Assert.Null(reason);
        Assert.NotNull(models);
        Assert.Single(models);
        Assert.Equal("claude-3-5-sonnet", models[0]["cli"]?.GetValue<string>());
        Assert.Equal("Claude 3.5 Sonnet", models[0]["label"]?.GetValue<string>());
    }

    [Fact]
    public void ParseModels_InvalidOrEmpty_ReturnsDescriptiveFailure()
    {
        var rows = new JsonArray
        {
            new JsonObject { ["value"] = "not-a-valid-claude-name" }
        };

        var (ok, reason, models) = Claude.ParseModels(rows);
        Assert.False(ok);
        Assert.Equal("Claude Code returned no exact model IDs. Aliases were not guessed.", reason);
        Assert.Null(models);
    }

    [Fact]
    public async Task ListClaudeAsync_Unauthenticated_ReturnsLoginRequiredReason()
    {
        Task<ProcessResult> MockRun(string cmd, IReadOnlyList<string> args)
        {
            return Task.FromResult(new ProcessResult(1, "Not logged in", ""));
        }

        var res = await Claude.ListClaudeAsync("claude", null, MockRun);
        Assert.False(res.Ok);
        Assert.Contains("Claude Code authentication unavailable.", res.Reason);
    }
}
