// Tests for engine/provider-adapters.cjs and engine/providers.cjs.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ProviderAdaptersTests
{
    [Fact]
    public void Built_in_adapters_are_registered_and_have_correct_research_and_capabilities()
    {
        ProviderAdapters.ResetBuiltIns();

        var claude = ProviderAdapters.AdapterFor("claude");
        var codex = ProviderAdapters.AdapterFor("codex");
        var grok = ProviderAdapters.AdapterFor("grok");
        var antigravity = ProviderAdapters.AdapterFor("antigravity");

        Assert.NotNull(claude);
        Assert.NotNull(codex);
        Assert.NotNull(grok);
        Assert.NotNull(antigravity);

        Assert.False(claude.SourceProtectedResearch);
        Assert.True(codex.SourceProtectedResearch);
        Assert.False(grok.SourceProtectedResearch);
        Assert.False(antigravity.SourceProtectedResearch);

        Assert.False(claude.Capabilities["canResearch"]?.GetValue<bool>());
        Assert.True(codex.Capabilities["canResearch"]?.GetValue<bool>());
        Assert.True(grok.Capabilities["canResearch"]?.GetValue<bool>());
        Assert.True(antigravity.Capabilities["canResearch"]?.GetValue<bool>());
    }

    [Fact]
    public void AdapterFor_supports_string_and_json_object_lookups()
    {
        ProviderAdapters.ResetBuiltIns();

        Assert.NotNull(ProviderAdapters.AdapterFor("codex"));
        Assert.Null(ProviderAdapters.AdapterFor("unknown-adapter"));
        Assert.Null(ProviderAdapters.AdapterFor((string?)null));

        var byAdapter = new JsonObject { ["adapter"] = "grok" };
        Assert.NotNull(ProviderAdapters.AdapterFor(byAdapter));

        var byProvider = new JsonObject { ["provider"] = "claude" };
        Assert.NotNull(ProviderAdapters.AdapterFor(byProvider));

        var unknown = new JsonObject { ["adapter"] = "non-existent" };
        Assert.Null(ProviderAdapters.AdapterFor(unknown));
    }

    [Fact]
    public void RegisterAdapter_validates_uniqueness_and_required_methods_in_order()
    {
        ProviderAdapters.ResetBuiltIns();

        // Duplicate id
        var exDup = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("codex", null, null, null, null, null, null, null, null, null));
        Assert.Equal("Adapter ID must be unique.", exDup.Message);

        var emptyId = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("", null, null, null, null, null, null, null, null, null));
        Assert.Equal("Adapter ID must be unique.", emptyId.Message);

        // Missing executable
        var exExe = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, null, null, null, null, null, null, null, null));
        Assert.Equal("Adapter requires executable", exExe.Message);

        // Missing availability
        var exAvail = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, null, null, null, null, null, null, null));
        Assert.Equal("Adapter requires availability", exAvail.Message);

        // Missing execute
        var exExec = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(), null, null, null, null, null, null));
        Assert.Equal("Adapter requires execute", exExec.Message);

        // Missing environment
        var exEnv = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(),
                (m, f, p, l, r) => new LaunchSpecResult("cmd", [], f, "p", "a", "m"), null, null, null, null, null));
        Assert.Equal("Adapter requires environment", exEnv.Message);

        // Missing terminate
        var exTerm = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(),
                (m, f, p, l, r) => new LaunchSpecResult("cmd", [], f, "p", "a", "m"),
                (i, pl) => new Dictionary<string, string>(), null, null, null, null));
        Assert.Equal("Adapter requires terminate", exTerm.Message);

        // Missing quota
        var exQuota = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(),
                (m, f, p, l, r) => new LaunchSpecResult("cmd", [], f, "p", "a", "m"),
                (i, pl) => new Dictionary<string, string>(), _ => { }, null, null, null));
        Assert.Equal("Adapter requires quota", exQuota.Message);

        // Missing discover
        var exDisc = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(),
                (m, f, p, l, r) => new LaunchSpecResult("cmd", [], f, "p", "a", "m"),
                (i, pl) => new Dictionary<string, string>(), _ => { }, (_, _) => Task.FromResult(new JsonObject()), null, null));
        Assert.Equal("Adapter requires discover", exDisc.Message);

        // Missing resultIdentity
        var exResId = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(),
                (m, f, p, l, r) => new LaunchSpecResult("cmd", [], f, "p", "a", "m"),
                (i, pl) => new Dictionary<string, string>(), _ => { }, (_, _) => Task.FromResult(new JsonObject()),
                (_, _) => Task.FromResult(new JsonObject()), null));
        Assert.Equal("Adapter requires resultIdentity", exResId.Message);

        // Missing nativeModel regex
        var exRegex = Assert.Throws<InvalidOperationException>(() =>
            ProviderAdapters.RegisterAdapter("test1", null, _ => null, _ => new JsonObject(),
                (m, f, p, l, r) => new LaunchSpecResult("cmd", [], f, "p", "a", "m"),
                (i, pl) => new Dictionary<string, string>(), _ => { }, (_, _) => Task.FromResult(new JsonObject()),
                (_, _) => Task.FromResult(new JsonObject()), m => new JsonObject()));
        Assert.Equal("Adapter requires exact nativeModel validation.", exRegex.Message);

        // Successful registration
        ProviderAdapters.RegisterAdapter("test1", new Regex(@"^test-", RegexOptions.ECMAScript),
            _ => "/bin/test", _ => new JsonObject { ["available"] = true },
            (m, f, p, l, r) => new LaunchSpecResult("test", [], f, "p", "a", "m"),
            (i, pl) => new Dictionary<string, string>(), _ => { },
            (_, _) => Task.FromResult(new JsonObject { ["ok"] = true }),
            (_, _) => Task.FromResult(new JsonObject { ["ok"] = true }),
            m => new JsonObject { ["provider"] = "test1", ["model"] = m["cli"]?.ToString() });

        Assert.NotNull(ProviderAdapters.AdapterFor("test1"));
    }

    [Fact]
    public void ResultIdentity_preserves_providerId_model_and_accountKey()
    {
        var inputWithAccount = new JsonObject
        {
            ["provider"] = "codex",
            ["providerId"] = "custom",
            ["cli"] = "gpt-exact",
            ["accountKey"] = "fake-account"
        };
        var res1 = ProviderAdapters.BuildResultIdentity(inputWithAccount);
        Assert.Equal("custom", res1["provider"]?.ToString());
        Assert.Equal("gpt-exact", res1["model"]?.ToString());
        Assert.Equal("fake-account", res1["accountKey"]?.ToString());

        var inputWithoutAccount = new JsonObject
        {
            ["provider"] = "grok",
            ["cli"] = "grok-exact"
        };
        var res2 = ProviderAdapters.BuildResultIdentity(inputWithoutAccount);
        Assert.Equal("grok", res2["provider"]?.ToString());
        Assert.Equal("grok-exact", res2["model"]?.ToString());
        Assert.False(res2.ContainsKey("accountKey"));
    }

    [Fact]
    public void ProviderEnvironment_does_not_mutate_inherited_dictionary()
    {
        var inherited = new Dictionary<string, string>
        {
            ["PATH"] = "preserved-path",
            ["CODEX_THREAD_ID"] = "parent-session"
        };

        var res = ProviderAdapters.ProviderEnvironment("codex", inherited, "win32");
        Assert.Equal("preserved-path", inherited["PATH"]);
        Assert.Equal("parent-session", inherited["CODEX_THREAD_ID"]);
        Assert.Equal(2, inherited.Count);

        Assert.Equal("preserved-path", res["PATH"]);
        Assert.Equal("parent-session", res["CODEX_THREAD_ID"]);
    }

    [Fact]
    public void LaunchSpec_validates_model_and_dispatches()
    {
        Environment.SetEnvironmentVariable("PLANWHARF_GROK", "grok.exe");
        Environment.SetEnvironmentVariable("PLANWHARF_CODEX", "codex.exe");
        Environment.SetEnvironmentVariable("PLANWHARF_ANTIGRAVITY", "agy.exe");

        var grokModel = new JsonObject
        {
            ["provider"] = "grok",
            ["cli"] = "grok-4.6",
            ["alwaysApprove"] = true
        };
        var spec = ProviderAdapters.LaunchSpec(grokModel, "D:/demo folder", "job");
        Assert.Contains("-m", spec.Args);
        Assert.Contains("grok-4.6", spec.Args);
        Assert.Contains("--always-approve", spec.Args);
        Assert.NotNull(spec.PromptFile);
        Assert.Equal("job", File.ReadAllText(spec.PromptFile));

        // Mismatches throw
        var mismatch = new JsonObject
        {
            ["provider"] = "codex",
            ["cli"] = "grok-4.7"
        };
        Assert.Throws<InvalidOperationException>(() => ProviderAdapters.LaunchSpec(mismatch, ".", "job"));

        var unavailable = new JsonObject
        {
            ["available"] = false,
            ["unavailableReason"] = "Account unavailable"
        };
        var exUnavail = Assert.Throws<InvalidOperationException>(() => ProviderAdapters.LaunchSpec(unavailable, ".", "job"));
        Assert.Equal("Account unavailable", exUnavail.Message);
    }

    [Fact]
    public async Task DiscoverCodexAsync_handles_success_missing_auth_http_error_and_malformed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "pw-test-codex-disc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // Missing auth
            var noAuthProv = new JsonObject { ["settingsFolder"] = tempDir };
            var resMissing = await ProviderAdapters.DiscoverCodexAsync(noAuthProv, CancellationToken.None);
            Assert.False(resMissing["ok"]?.GetValue<bool>());
            Assert.Equal("Codex authentication unavailable.", resMissing["reason"]?.ToString());

            // Write auth
            var auth = new JsonObject
            {
                ["tokens"] = new JsonObject
                {
                    ["access_token"] = "synthetic-token",
                    ["account_id"] = "fake-account"
                }
            };
            File.WriteAllText(Path.Combine(tempDir, "auth.json"), auth.ToJsonString());

            // Mock HTTP success
            ProviderAdapters.FetchImpl = (url, req) =>
            {
                var body = new JsonObject
                {
                    ["models"] = new JsonArray
                    {
                        new JsonObject { ["slug"] = "gpt-exact", ["display_name"] = "Exact Model" }
                    }
                };
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            var resSuccess = await ProviderAdapters.DiscoverCodexAsync(noAuthProv, CancellationToken.None);
            Assert.True(resSuccess["ok"]?.GetValue<bool>());
            var models = resSuccess["models"] as JsonArray;
            Assert.NotNull(models);
            Assert.Single(models);
            Assert.Equal("gpt-exact", models[0]?["cli"]?.ToString());
            Assert.Equal("Exact Model", models[0]?["label"]?.ToString());
            Assert.True(models[0]?["capabilities"]?["canResearch"]?.GetValue<bool>());

            // Mock HTTP 401
            ProviderAdapters.FetchImpl = (url, req) =>
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            var res401 = await ProviderAdapters.DiscoverCodexAsync(noAuthProv, CancellationToken.None);
            Assert.False(res401["ok"]?.GetValue<bool>());
            Assert.Equal("Codex model catalog HTTP 401", res401["reason"]?.ToString());

            // Mock malformed catalog (models not array)
            ProviderAdapters.FetchImpl = (url, req) =>
            {
                var resp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            var resMalformed = await ProviderAdapters.DiscoverCodexAsync(noAuthProv, CancellationToken.None);
            Assert.False(resMalformed["ok"]?.GetValue<bool>());
            Assert.Equal("Codex returned an invalid model catalog.", resMalformed["reason"]?.ToString());
        }
        finally
        {
            ProviderAdapters.FetchImpl = null;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
