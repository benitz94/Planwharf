// Ports the checks that call diagnoseProvider:
//  - engine/grill-chat.test.cjs, "unrecognized Antigravity model is classified precisely and never replaced
//    with another provider": the classification as PROVIDER_MODEL_UNAVAILABLE.
//  - engine/model-policy.test.cjs: an unknown flag is PROVIDER_CLI_INCOMPATIBLE and the report never holds
//    the raw output; a missing structured result is PROVIDER_PROTOCOL_CHANGED.
// Plus: the whole object compared with the JavaScript one (field order included), one case per category,
// the order of the checks, JavaScript's `.` and `i` flag, and encodeURIComponent on !'()*.
//
// Deferred: the rest of those two tests needs the server and the orchestrator (the model marked
// unavailable, the lead not replaced, the rejected provider action), which are not ported yet.
using System.Text.Json.Nodes;
using Planwharf.Engine;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderDiagnosticsTests
{
    static readonly JsonObject Codex = new() { ["id"] = "lead", ["provider"] = "codex", ["cli"] = "gpt-6-astra", ["plan"] = "ChatGPT", ["enabled"] = true, ["kind"] = "premium" };

    [Fact]
    public void Unrecognized_Antigravity_model_is_classified_precisely()
    {
        var model = new JsonObject { ["id"] = "lead", ["provider"] = "antigravity", ["cli"] = "gemini-3.1-pro-high", ["enabled"] = true, ["available"] = true, ["kind"] = "default" };
        var diagnostic = ProviderDiagnostics.DiagnoseProvider(model, "invalid model selection (--model \"gemini-3.1-pro-high\" --effort \"\"): model gemini-3.1-pro-high is not recognized as a known model or custom model in settings");
        Assert.Equal("PROVIDER_MODEL_UNAVAILABLE", diagnostic["code"]!.GetValue<string>());
        Assert.Equal("PROVIDER_MODEL_UNAVAILABLE: antigravity / gemini-3.1-pro-high. Refresh models and verify access to this exact provider/model ID.", diagnostic["message"]!.GetValue<string>());
    }

    [Fact]
    public void Provider_diagnostics_never_hold_the_raw_output()
    {
        var diagnostic = ProviderDiagnostics.DiagnoseProvider(Codex, "unknown flag --old token secret personal prompt", 2);
        Assert.Equal("PROVIDER_CLI_INCOMPATIBLE", diagnostic["code"]!.GetValue<string>());
        Assert.DoesNotContain("secret", diagnostic.ToJsonString());
        Assert.Equal("PROVIDER_PROTOCOL_CHANGED", ProviderDiagnostics.DiagnoseProvider(Codex, "Missing or invalid structured worker result.")["code"]!.GetValue<string>());
    }

    [Fact]
    public void The_whole_diagnostic_is_the_JavaScript_one()
    {
        var expected = JsonNode.Parse(@"{""code"":""PROVIDER_CLI_INCOMPATIBLE"",""provider"":""codex"",""adapter"":""codex"",""model"":""gpt-6-astra"",""exitCode"":2,""message"":""PROVIDER_CLI_INCOMPATIBLE: codex / gpt-6-astra. The CLI interface may have changed. Check its version and update the Planwharf adapter."",""suggestion"":""The CLI interface may have changed. Check its version and update the Planwharf adapter."",""report"":""Planwharf provider integration report\n\nCategory: PROVIDER_CLI_INCOMPATIBLE\nProvider: codex\nAdapter: codex\nExact model: gpt-6-astra\nExit code: 2\n\nCLI version: [fill in locally]\nExpected behavior: [describe]\nObserved behavior: [describe without credentials or personal project data]\n\nRaw logs and prompts are intentionally excluded."",""reportUrl"":""https://github.com/benitz94/Planwharf/issues/new?title=PROVIDER_CLI_INCOMPATIBLE%3A%20codex&body=Planwharf%20provider%20integration%20report%0A%0ACategory%3A%20PROVIDER_CLI_INCOMPATIBLE%0AProvider%3A%20codex%0AAdapter%3A%20codex%0AExact%20model%3A%20gpt-6-astra%0AExit%20code%3A%202%0A%0ACLI%20version%3A%20%5Bfill%20in%20locally%5D%0AExpected%20behavior%3A%20%5Bdescribe%5D%0AObserved%20behavior%3A%20%5Bdescribe%20without%20credentials%20or%20personal%20project%20data%5D%0A%0ARaw%20logs%20and%20prompts%20are%20intentionally%20excluded.""}")!.AsObject();
        var actual = ProviderDiagnostics.DiagnoseProvider(Codex, "unknown flag --old token secret personal prompt", 2);
        Assert.Equal(expected.Select(pair => pair.Key), actual.Select(pair => pair.Key));
        Assert.True(JsonNode.DeepEquals(expected, actual), actual.ToJsonString());
    }

    [Theory]
    [InlineData("PERMISSION_MODE_UNSUPPORTED: x", "PERMISSION_MODE_UNSUPPORTED")]
    [InlineData("permission_mode_unsupported", "PROVIDER_EXECUTION_FAILED")]
    [InlineData("PROVIDER_MODEL_IDENTITY_CHANGED", "PROVIDER_MODEL_IDENTITY_CHANGED")]
    [InlineData("CLI_CONFIGURATION_INVALID", "PROVIDER_CONFIGURATION_INVALID")]
    [InlineData("unrecognized option '--x'", "PROVIDER_CLI_INCOMPATIBLE")]
    [InlineData("Unsupported output format", "PROVIDER_CLI_INCOMPATIBLE")]
    [InlineData("unsupported\noutput format", "PROVIDER_EXECUTION_FAILED")]
    [InlineData("unsupported\routput format", "PROVIDER_EXECUTION_FAILED")]
    [InlineData("unsupported\u2028format", "PROVIDER_EXECUTION_FAILED")]
    [InlineData("provider_protocol_changed", "PROVIDER_PROTOCOL_CHANGED")]
    [InlineData("Unreadable session metadata", "PROVIDER_PROTOCOL_CHANGED")]
    [InlineData("HTTP 401", "PROVIDER_AUTHENTICATION_FAILED")]
    [InlineData("Invalid API key", "PROVIDER_AUTHENTICATION_FAILED")]
    [InlineData("\u212Aey invalid", "PROVIDER_EXECUTION_FAILED")]
    [InlineData("429 quota", "PROVIDER_USAGE_LIMIT")]
    [InlineData("Spending limit reached", "PROVIDER_USAGE_LIMIT")]
    [InlineData("Model gpt-x not found", "PROVIDER_MODEL_UNAVAILABLE")]
    [InlineData("spawn grok ENOENT", "PROVIDER_CLI_MISSING")]
    [InlineData("connect ETIMEDOUT", "PROVIDER_CONNECTION_FAILED")]
    [InlineData("self signed certificate", "PROVIDER_CONNECTION_FAILED")]
    [InlineData("Segmentation fault", "PROVIDER_EXECUTION_FAILED")]
    [InlineData("", "PROVIDER_EXECUTION_FAILED")]
    [InlineData(null, "PROVIDER_EXECUTION_FAILED")]
    // The first category that matches wins: authentication is checked before the usage limit.
    [InlineData("401 and 429", "PROVIDER_AUTHENTICATION_FAILED")]
    [InlineData("invalid model token", "PROVIDER_AUTHENTICATION_FAILED")]
    public void Every_category_is_recognised_in_the_JavaScript_order(string? output, string code) =>
        Assert.Equal(code, ProviderDiagnostics.DiagnoseProvider(Codex, output)["code"]!.GetValue<string>());

    [Fact]
    public void Missing_identity_reads_unknown_and_the_exit_code_unavailable()
    {
        var diagnostic = ProviderDiagnostics.DiagnoseProvider(new JsonObject(), "boom");
        Assert.Equal("PROVIDER_EXECUTION_FAILED: unknown / unknown. Review the retained local output and retry after checking the provider CLI.", diagnostic["message"]!.GetValue<string>());
        Assert.Contains("\nExit code: unavailable\n", diagnostic["report"]!.GetValue<string>());
        Assert.True(diagnostic.ContainsKey("exitCode"));
        Assert.Null(diagnostic["exitCode"]);
        var routed = ProviderDiagnostics.DiagnoseProvider(new JsonObject { ["providerId"] = "custom", ["provider"] = "p", ["adapter"] = "api", ["id"] = "m" }, "x", 0);
        Assert.Equal("custom", routed["provider"]!.GetValue<string>());
        Assert.Equal("api", routed["adapter"]!.GetValue<string>());
        Assert.Equal("m", routed["model"]!.GetValue<string>());
        Assert.Contains("\nExit code: 0\n", routed["report"]!.GetValue<string>());
    }

    [Fact]
    public void Report_links_encode_as_encodeURIComponent()
    {
        Assert.Equal("!'()*~-_.%20%3B%2F%3F%3A%40%26%3D%2B%24%2C%23%C3%A9%F0%9F%98%80", ProviderDiagnostics.EncodeURIComponent("!'()*~-_. ;/?:@&=+$,#\u00e9\uD83D\uDE00"));
        Assert.Throws<InvalidOperationException>(() => ProviderDiagnostics.EncodeURIComponent("\uD83D"));
        var url = ProviderDiagnostics.DiagnoseProvider(new JsonObject { ["adapter"] = "a(b)!*'" }, "x")["reportUrl"]!.GetValue<string>();
        Assert.StartsWith("https://github.com/benitz94/Planwharf/issues/new?title=PROVIDER_EXECUTION_FAILED%3A%20a(b)!*'&body=Planwharf%20provider", url);
    }

    [Fact]
    public void Identity_and_exit_codes_stringify_as_JavaScript_templates()
    {
        var diagnostic = ProviderDiagnostics.DiagnoseProvider(new JsonObject { ["providerId"] = new JsonObject(), ["adapter"] = "", ["provider"] = "fallback", ["id"] = 0, ["cli"] = "" }, "x", new JsonArray { 1, 2 });
        Assert.Equal("PROVIDER_EXECUTION_FAILED: [object Object] / unknown. Review the retained local output and retry after checking the provider CLI.", diagnostic["message"]!.GetValue<string>());
        Assert.Contains("\nExit code: 1,2\n", diagnostic["report"]!.GetValue<string>());
        Assert.Equal("PROVIDER_EXECUTION_FAILED", ProviderDiagnostics.DiagnoseProvider(Codex, "unsupported\u2028format")["code"]!.GetValue<string>());

        var gate = new System.Threading.Barrier(4);
        var codes = new string[4];
        System.Threading.Tasks.Parallel.For(0, 4, i =>
        {
            gate.SignalAndWait();
            codes[i] = ProviderDiagnostics.DiagnoseProvider(Codex, i % 2 == 0 ? "HTTP 401" : "429 quota")["code"]!.GetValue<string>();
        });
        Assert.Equal(["PROVIDER_AUTHENTICATION_FAILED", "PROVIDER_USAGE_LIMIT", "PROVIDER_AUTHENTICATION_FAILED", "PROVIDER_USAGE_LIMIT"], codes);
    }
}
