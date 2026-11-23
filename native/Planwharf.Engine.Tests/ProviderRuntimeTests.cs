using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderRuntimeTests
{
    [Fact]
    public void AccountEnvironment_Claude_SetsClaudeConfigDirAndStripsInheritedKeys()
    {
        var inherited = new Dictionary<string, string>
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "token123",
            ["ANTHROPIC_API_KEY"] = "sk-123",
            ["OTHER_KEY"] = "val"
        };
        var model = new JsonObject
        {
            ["settingsFolder"] = "C:\\claude-settings"
        };

        var env = ProviderRuntime.AccountEnvironment("claude", model, inherited, "win32");
        Assert.Equal("C:\\claude-settings", env["CLAUDE_CONFIG_DIR"]);
        Assert.False(env.ContainsKey("CLAUDE_CODE_OAUTH_TOKEN"));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.Equal("val", env["OTHER_KEY"]);
    }

    [Fact]
    public void AccountEnvironment_Codex_SetsCodexHomeAndStripsOpenAiKey()
    {
        var inherited = new Dictionary<string, string>
        {
            ["OPENAI_API_KEY"] = "sk-openai",
            ["OTHER_KEY"] = "val"
        };
        var model = new JsonObject
        {
            ["settingsFolder"] = "C:\\codex-home"
        };

        var env = ProviderRuntime.AccountEnvironment("codex", model, inherited, "win32");
        Assert.Equal("C:\\codex-home", env["CODEX_HOME"]);
        Assert.False(env.ContainsKey("OPENAI_API_KEY"));
        Assert.Equal("val", env["OTHER_KEY"]);
    }

    [Fact]
    public void ProviderEnvironment_WindowsCodex_FiltersDeniedStoreShellsAndSuppliesCABundle()
    {
        var inherited = new Dictionary<string, string>
        {
            ["PATH"] = "C:\\Normal\\bin;C:\\Program Files\\WindowsApps\\denied;C:\\Other\\bin"
        };

        var env = ProviderRuntime.ProviderEnvironment("codex", inherited, "win32");
        Assert.True(env.ContainsKey("CODEX_CA_CERTIFICATE"));
        Assert.NotEmpty(env["CODEX_CA_CERTIFICATE"]);
        Assert.Contains("C:\\Normal\\bin", env["PATH"]);
    }

    [Fact]
    public void LaunchSpec_Claude_BuildsStreamJsonAndPermissionArgs()
    {
        var model = new JsonObject
        {
            ["providerId"] = "claude",
            ["adapter"] = "claude",
            ["cli"] = "claude-3-7-sonnet-20250219",
            ["alwaysApprove"] = true,
            ["tools"] = new JsonObject
            {
                ["mcp"] = false,
                ["browser"] = false
            }
        };

        var spec = ProviderRuntime.LaunchSpec(model, "C:\\work", "hello world", lead: false);
        Assert.Equal("claude", spec.Adapter);
        Assert.Contains("--output-format", spec.Args);
        Assert.Contains("stream-json", spec.Args);
        Assert.Contains("--permission-mode", spec.Args);
        Assert.Contains("bypassPermissions", spec.Args);
        Assert.Equal("hello world", spec.Stdin);
    }

    [Fact]
    public void LaunchSpec_Grok_WritesPromptFileAndConfiguresMaxTurns()
    {
        var model = new JsonObject
        {
            ["providerId"] = "grok",
            ["adapter"] = "grok",
            ["cli"] = "grok-2",
            ["alwaysApprove"] = true
        };

        var spec = ProviderRuntime.LaunchSpec(model, "C:\\work", "hello grok", lead: false);
        Assert.Equal("grok", spec.Adapter);
        Assert.Contains("--max-turns", spec.Args);
        Assert.Contains("500", spec.Args);
        Assert.NotNull(spec.PromptFile);
        Assert.True(File.Exists(spec.PromptFile));
        Assert.Equal("hello grok", File.ReadAllText(spec.PromptFile));
    }

    [Fact]
    public void LaunchSpec_Codex_ConfiguresSandboxWritableRootsAndLeadJson()
    {
        var model = new JsonObject
        {
            ["providerId"] = "codex",
            ["adapter"] = "codex",
            ["cli"] = "gpt-4o",
            ["alwaysApprove"] = false,
            ["apiResultOutputPath"] = "C:\\results\\attempt-1.json"
        };

        var spec = ProviderRuntime.LaunchSpec(model, "C:\\work", "run lead", lead: true);
        Assert.Contains("--json", spec.Args);
        Assert.Contains("--sandbox", spec.Args);
        Assert.Contains("workspace-write", spec.Args);
        Assert.Contains(spec.Args, a => a.Contains("sandbox_workspace_write.writable_roots"));
    }

    [Fact]
    public void LaunchSpec_UnapprovedAntigravityOrClaude_ThrowsPermissionNeeded()
    {
        var agyModel = new JsonObject
        {
            ["providerId"] = "antigravity",
            ["adapter"] = "antigravity",
            ["cli"] = "gemini-2.5-pro",
            ["alwaysApprove"] = false
        };

        var exAgy = Assert.Throws<InvalidOperationException>(() =>
            ProviderRuntime.LaunchSpec(agyModel, "C:\\work", "prompt"));
        Assert.Contains("PERMISSION_MODE_UNSUPPORTED", exAgy.Message);

        var claudeModel = new JsonObject
        {
            ["providerId"] = "claude",
            ["adapter"] = "claude",
            ["cli"] = "claude-3-7-sonnet-20250219",
            ["alwaysApprove"] = false
        };

        var exClaude = Assert.Throws<InvalidOperationException>(() =>
            ProviderRuntime.LaunchSpec(claudeModel, "C:\\work", "prompt", lead: false));
        Assert.Contains("PERMISSION_NEEDED", exClaude.Message);
    }
}
