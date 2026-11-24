using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ExternalAdaptersTests
{
    private static JsonObject Read(string json) => JsonNode.Parse(json)!.AsObject();
    private static readonly Dictionary<string, string> Env = new() { ["FIXTURE_KEY"] = "synthetic-key" };
    private static JsonObject Provider(string protocol = "openai") => new() { ["baseUrl"] = "https://example.invalid/v1/", ["authEnv"] = "FIXTURE_KEY", ["apiProtocol"] = protocol };
    private static Dictionary<string, ExternalAdapters.Adapter> Adapters(
        Func<ExternalAdapters.CatalogRequest, CancellationToken, Task<ExternalAdapters.CatalogResponse>>? transport = null, Action<int>? terminate = null)
    {
        var adapters = new Dictionary<string, ExternalAdapters.Adapter>();
        ExternalAdapters.RegisterExternalAdapters(adapters.Add, new(Env, _ => "/fake/native",
            transport ?? ((_, _) => throw new InvalidOperationException("Unexpected HTTP")),
            () => new("/fake/api-host", ["--api-agent"]), terminate ?? (_ => { })));
        return adapters;
    }
    [Theory]
    [InlineData("https://EXAMPLE.invalid:443/v1///", "https://example.invalid/v1")]
    [InlineData("http://localhost:123/v1", "http://localhost:123/v1")]
    [InlineData("http://127.0.0.1", "http://127.0.0.1")]
    [InlineData("http://[::1]:42/", "http://[::1]:42")]
    [InlineData("http://127.1/", "http://127.0.0.1")]
    [InlineData("http://2130706433", "http://127.0.0.1")]
    [InlineData("https:example.invalid", "https://example.invalid")]
    [InlineData("https://example.invalid/日本語", "https://example.invalid/%E6%97%A5%E6%9C%AC%E8%AA%9E")]
    [InlineData("https://example.invalid/a/../b", "https://example.invalid/b")]
    [InlineData("https://example.invalid?", "https://example.invalid/?")]
    [InlineData("https://example.invalid#", "https://example.invalid/#")]
    public void ApiConfig_UsesHttpsOrLoopbackAndExactDefaults(string url, string expected)
    {
        var result = ExternalAdapters.ApiConfig(new() { ["baseUrl"] = url });
        Assert.Equal(expected, result["baseUrl"]!.GetValue<string>());
        Assert.Equal(4096, result["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(30, result["maxTurns"]!.GetValue<int>());
        Assert.Equal(90000, result["requestTimeoutMs"]!.GetValue<int>());
        Assert.False(result.ContainsKey("authEnv"));
    }
    [Theory]
    [InlineData("http://remote.invalid")]
    [InlineData("https://user:synthetic@example.invalid")]
    [InlineData("https://example.invalid?a=1")]
    [InlineData("https://example.invalid#fragment")]
    [InlineData("ftp://localhost")]
    [InlineData("http://localhost.evil.invalid")]
    public void ApiConfig_RejectsUnsafeEndpoints(string url) => Assert.Equal(
        "API_CONFIGURATION_INVALID: use HTTPS, or HTTP on loopback, without credentials or query parameters.",
        Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(new() { ["baseUrl"] = url })).Message);

    [Fact]
    public void ApiConfig_ValidatesInSourceOrderAndKeepsLimits()
    {
        Assert.Equal("API_CONFIGURATION_INVALID: baseUrl is required.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(new())).Message);
        var provider = Provider(); provider["authEnv"] = "bad-name"; provider["apiProtocol"] = "invalid";
        Assert.EndsWith("authEnv must name an environment variable.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(provider)).Message);
        provider.Remove("authEnv");
        Assert.EndsWith("apiProtocol must be openai or anthropic.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(provider)).Message);
        foreach (var (key, max) in new[] { ("maxOutputTokens", 65536), ("maxTurns", 100), ("requestTimeoutMs", 300000) })
        {
            foreach (var value in new[] { "null", "true", "\"1\"", "0", "-1", "1.5", (max + 1).ToString() })
            {
                provider = Provider(); provider[key] = JsonNode.Parse(value);
                Assert.Equal("API_CONFIGURATION_INVALID: invalid " + key, Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(provider)).Message);
            }
            provider = Provider(); provider[key] = max;
            Assert.Equal(max, ExternalAdapters.ApiConfig(provider)[key]!.GetValue<int>());
        }
    }
    [Theory]
    [InlineData("openai", "Authorization", "Bearer synthetic-key")]
    [InlineData("anthropic", "x-api-key", "synthetic-key")]
    public void ApiHeaders_PreservesBothFormatsWithoutLeakingKeysInErrors(string protocol, string header, string expected)
    {
        var config = ExternalAdapters.ApiConfig(Provider(protocol));
        var headers = ExternalAdapters.ApiHeaders(config, Env);
        Assert.Equal(expected, headers[header]!.GetValue<string>());
        Assert.Equal("application/json", headers["Content-Type"]!.GetValue<string>());
        Assert.Equal(protocol == "anthropic", headers.ContainsKey("anthropic-version"));
        Assert.Equal("PROVIDER_AUTHENTICATION_FAILED: API key environment variable is not set: FIXTURE_KEY",
            Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiHeaders(config, new Dictionary<string, string>())).Message);
    }
    [Theory]
    [InlineData("codex-jsonl")]
    [InlineData("messages-jsonl")]
    [InlineData("antigravity-jsonl")]
    public void ConfiguredCli_UsesDeclaredCommandExactIdPromptAndIndependentPermissions(string protocol)
    {
        var model = Read("""{"provider":"fallback","providerId":"route","cli":"Exact/日本語","providerConfig":{"cliArgs":["--model={model}","{cwd}"],"yoloArgs":["--approve"],"restrictedArgs":[],"outputProtocol":"codex-jsonl"}}""");
        model["providerConfig"]!["outputProtocol"] = protocol;
        model["alwaysApprove"] = true; model["policy"] = new JsonObject { ["alwaysApprove"] = false };
        var spec = ExternalAdapters.CliSpec(model, "/tmp/è space", "Unicode 日本語\ntext", true, false, _ => "/fake/native");
        Assert.Equal("route", spec["provider"]!.GetValue<string>());
        Assert.Equal("--model=Exact/日本語", spec["args"]![0]!.GetValue<string>());
        Assert.Equal("/tmp/è space", spec["args"]![1]!.GetValue<string>());
        Assert.Equal(2, spec["args"]!.AsArray().Count);
        var stdin = spec["stdin"]!.GetValue<string>();
        Assert.Equal(protocol == "antigravity-jsonl" ? "{\"event\":\"user\",\"message\":{\"content\":\"Unicode 日本語\\ntext\"}}\n" : "Unicode 日本語\ntext", stdin);
        model["policy"]!["alwaysApprove"] = true;
        Assert.Equal(3, ExternalAdapters.CliSpec(model, "cwd", "prompt", false, false, _ => "fake")["args"]!.AsArray().Count);
    }
    [Fact]
    public void CliSpec_RejectsResearchBeforeLookupAndChecksPermissionsBeforeArguments()
    {
        var model = Read("""{"cli":"exact","providerConfig":{"cliArgs":["{model}",null]}}""");
        Assert.Equal("Source-protected research is not supported by configured-cli.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.CliSpec(model, "", "", false, true, _ => throw new Exception())).Message);
        Assert.Equal("Configured CLI executable not found.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.CliSpec(model, "", "", false, false, _ => null)).Message);
        Assert.StartsWith("PERMISSION_MODE_UNSUPPORTED:", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.CliSpec(model, "", "", false, false, _ => "fake")).Message);
        model["providerConfig"]!["restrictedArgs"] = new JsonArray();
        Assert.Equal("CLI_CONFIGURATION_INVALID: arguments must be single-line strings.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.CliSpec(model, "", "", false, false, _ => "fake")).Message);
    }
    [Fact]
    public void ConfiguredCommand_UsesOnlyInjectedPathAndNativeFiles()
    {
        var visited = new List<string>();
        bool Lookup(string path) { visited.Add(path); return path == "/two/native"; }
        var env = new Dictionary<string, string> { ["PATH"] = "/one:/two" };
        Assert.Equal("/two/native", ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = "native" }, env, Lookup, false));
        Assert.Equal(new[] { "/one/native", "/two/native" }, visited);
        foreach (var command in new[] { "native --bad", "./native", "native\n", "native\0" })
            Assert.Null(ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = command }, env, _ => throw new Exception(), false));
        Assert.Equal("C:\\日本語 space\\native.exe", ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = "native" },
            new Dictionary<string, string> { ["PATH"] = "C:\\日本語 space" }, p => p.EndsWith("native.exe", StringComparison.Ordinal), true));
        Assert.Null(ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = "/missing" }, env, _ => false, false));
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async Task ApiDiscovery_PreservesExactIdsAndNeverGrantsRoles(string protocol)
    {
        var calls = new List<ExternalAdapters.CatalogRequest>();
        using var cancel = new CancellationTokenSource();
        var api = Adapters((request, token) =>
        {
            Assert.Equal(cancel.Token, token); calls.Add(request);
            return Task.FromResult(new ExternalAdapters.CatalogResponse(200, Read("""{"data":[{"id":"Exact/日","display_name":"Name"},{"id":"Exact/日"},{"id":3},{}]}""")));
        })["api"];
        var result = await api.DiscoverAsync(Provider(protocol), cancel.Token);
        var request = Assert.Single(calls);
        Assert.Equal("https://example.invalid/v1/models", request.Url);
        Assert.Equal("error", request.Redirect); Assert.Equal(10000, request.TimeoutMs);
        Assert.True(request.Headers.ContainsKey(protocol == "anthropic" ? "x-api-key" : "Authorization"));
        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(2, result["models"]!.AsArray().Count);
        Assert.Equal("Exact/日", result["models"]![0]!["cli"]!.GetValue<string>());
        Assert.Equal("Name", result["models"]![0]!["label"]!.GetValue<string>());
        Assert.Equal("Exact/日", result["models"]![1]!["label"]!.GetValue<string>());
        foreach (var model in result["models"]!.AsArray())
        {
            Assert.False(model!["workerEligible"]!.GetValue<bool>());
            Assert.False(model["orchestratorEligible"]!.GetValue<bool>());
            Assert.All(model["capabilities"]!.AsObject(), pair => Assert.False(pair.Value!.GetValue<bool>()));
        }
    }
    [Fact]
    public async Task Discovery_ContainsHttpFailuresAndPropagatesTransportFailures()
    {
        var api = Adapters((_, _) => Task.FromResult(new ExternalAdapters.CatalogResponse(429, Read("""{"secret":"NEVER_COPY"}"""))))["api"];
        var result = await api.DiscoverAsync(Provider());
        Assert.Equal("API model catalog HTTP 429; configured model IDs are preserved.", result["reason"]!.GetValue<string>());
        Assert.DoesNotContain("NEVER_COPY", result.ToJsonString());
        api = Adapters((_, _) => Task.FromResult(new ExternalAdapters.CatalogResponse(200, new JsonObject())))["api"];
        Assert.Equal("API model catalog format changed; expected data array.", (await api.DiscoverAsync(Provider()))["reason"]!.GetValue<string>());
        api = Adapters((_, _) => throw new OperationCanceledException())["api"];
        await Assert.ThrowsAsync<OperationCanceledException>(() => api.DiscoverAsync(Provider()));
        var cli = Adapters()["configured-cli"];
        Assert.Equal("Configured CLI uses explicit model IDs; no enumeration command has been verified.", (await cli.DiscoverAsync(new()))["reason"]!.GetValue<string>());
    }
    [Fact]
    public async Task Registration_PreservesUnreadQuotaEnvironmentIdentityAndTermination()
    {
        var pids = new List<int>(); var adapters = Adapters(terminate: pids.Add);
        Assert.Equal(new[] { "api", "configured-cli" }, adapters.Keys);
        foreach (var adapter in adapters.Values)
        {
            Assert.False(adapter.SourceProtectedResearch);
            var inherited = Read("""{"SYNTHETIC":"fixture"}""");
            var env = adapter.Environment(inherited); env["SYNTHETIC"] = "changed";
            Assert.Equal("fixture", inherited["SYNTHETIC"]!.GetValue<string>());
            var quota = await adapter.QuotaAsync();
            Assert.False(quota["readable"]!.GetValue<bool>()); Assert.False(quota["exhausted"]!.GetValue<bool>()); Assert.Null(quota["leftPct"]);
            Assert.Equal("This adapter has no verified provider balance/quota endpoint. Observed API tokens are recorded separately.", quota["reason"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(Read("""{"provider":"route","model":"exact"}"""), adapter.ResultIdentity(Read("""{"provider":"fallback","providerId":"route","cli":"exact"}"""))));
            adapter.Terminate(123);
        }
        Assert.Equal(new[] { 123, 123 }, pids);
        var provider = Provider(); provider["authEnv"] = "MISSING_FIXTURE";
        var availability = adapters["api"].Availability(provider);
        Assert.False(availability["available"]!.GetValue<bool>());
        Assert.Equal("PROVIDER_AUTHENTICATION_FAILED: API key environment variable is not set: MISSING_FIXTURE", availability["reason"]!.GetValue<string>());
    }
    [Fact]
    public void ApiExecution_RequiresStructuredWorkerContractAndKeepsSecretOutOfEnvelope()
    {
        var api = Adapters()["api"];
        var model = Read("""{"providerId":"route","cli":"Exact/日","alwaysApprove":true,"policy":{"alwaysApprove":false},"advisory":true}""");
        model["providerConfig"] = Provider();
        Assert.Equal("Source-protected research is not supported by the API adapter.", Assert.Throws<InvalidOperationException>(() => api.Execute(model, "folder", "prompt", false, true)).Message);
        Assert.Equal("API workers require the structured MegaTask result contract.", Assert.Throws<InvalidOperationException>(() => api.Execute(model, "folder", "prompt")).Message);
        var lead = api.Execute(model, "/è space", "prompt 日本語", true);
        Assert.Equal("/fake/api-host", lead["command"]!.GetValue<string>());
        Assert.Equal("--api-agent", lead["args"]![0]!.GetValue<string>());
        Assert.DoesNotContain("synthetic-key", lead.ToJsonString());
        Assert.StartsWith("{\"config\":{\"baseUrl\":\"https://example.invalid/v1\",\"authEnv\":\"FIXTURE_KEY\",\"apiProtocol\":\"openai\",\"maxOutputTokens\":4096,\"maxTurns\":30,\"requestTimeoutMs\":90000},\"model\":\"Exact/日\"", lead["stdin"]!.GetValue<string>());
        var envelope = Read(lead["stdin"]!.GetValue<string>());
        Assert.False(envelope["alwaysApprove"]!.GetValue<bool>()); Assert.True(envelope["advisory"]!.GetValue<bool>());
        Assert.True(envelope["lead"]!.GetValue<bool>()); Assert.False(envelope.ContainsKey("resultOutputPath"));
        model["apiResultOutputPath"] = "/isolated/result.json";
        model["apiResultIdentity"] = Read("""{"resultId":"R-fixture","attemptId":"fixture"}""");
        envelope = Read(api.Execute(model, "folder", "prompt")["stdin"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(model["apiResultIdentity"], envelope["resultIdentity"]));
        Assert.Equal("/isolated/result.json", envelope["resultOutputPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task MalformedCatalogsPreserveSourceFailuresRatherThanInventingAvailability()
    {
        foreach (var (body, expected) in new[] { ("null", "Cannot read properties of null (reading 'data')"), ("{\"data\":[null]}", "Cannot read properties of null (reading 'id')") })
        {
            var api = Adapters((_, _) => Task.FromResult(new ExternalAdapters.CatalogResponse(200, JsonNode.Parse(body))))["api"];
            Assert.Equal(expected, (await Assert.ThrowsAsync<InvalidOperationException>(() => api.DiscoverAsync(Provider()))).Message);
        }
        var scalar = Adapters((_, _) => Task.FromResult(new ExternalAdapters.CatalogResponse(200, JsonValue.Create(1))))["api"];
        Assert.Equal("API model catalog format changed; expected data array.", (await scalar.DiscoverAsync(Provider()))["reason"]!.GetValue<string>());
    }
    [Fact]
    public void ConfigurationPreservesJavaScriptCoercionAndFalsyFallbacks()
    {
        var provider = Provider(); provider["authEnv"] = true;
        var config = ExternalAdapters.ApiConfig(provider);
        Assert.Equal("Bearer synthetic", ExternalAdapters.ApiHeaders(config, new Dictionary<string, string> { ["true"] = "synthetic" })["Authorization"]!.GetValue<string>());
        provider["authEnv"] = new JsonObject();
        Assert.EndsWith("authEnv must name an environment variable.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(provider)).Message);
        provider.Remove("authEnv"); provider["apiProtocol"] = 0;
        Assert.Equal("openai", ExternalAdapters.ApiConfig(provider)["apiProtocol"]!.GetValue<string>());
        provider["providerConfig"] = new JsonArray();
        Assert.Equal("API_CONFIGURATION_INVALID: baseUrl is required.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.ApiConfig(provider)).Message);
        Assert.Equal("fallback", Adapters()["api"].ResultIdentity(Read("""{"providerId":"","provider":"fallback","cli":"exact"}"""))["provider"]!.GetValue<string>());
    }

    [Fact]
    public void ExecutableLookupNormalizesPathSegmentsAndPreservesAbsoluteCommand()
    {
        Assert.Equal("/native", ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = "native" },
            new Dictionary<string, string> { ["PATH"] = "/one/../" }, p => p == "/native", false));
        Assert.Equal("native", ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = "native" },
            new Dictionary<string, string> { ["PATH"] = "." }, p => p == "native", false));
        Assert.Equal("C:\\folder space\\日本語.EXE", ExternalAdapters.ConfiguredCommand(new() { ["cliCommand"] = "C:\\folder space\\日本語.EXE" },
            new Dictionary<string, string>(), p => p == "C:\\folder space\\日本語.EXE", true));
    }
    [Fact]
    public void CliValidationRejectsMissingModelAndUnsupportedProtocolInOrder()
    {
        var model = Read("""{"cli":"exact","providerConfig":{"cliArgs":[],"restrictedArgs":[],"outputProtocol":"text"}}""");
        Assert.Equal("CLI_CONFIGURATION_INVALID: cliArgs must explicitly contain {model}.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.CliSpec(model, "", "", false, false, _ => "fake")).Message);
        model["providerConfig"]!["cliArgs"] = new JsonArray("{model}");
        Assert.Equal("CLI_CONFIGURATION_INVALID: declare a supported structured outputProtocol.", Assert.Throws<InvalidOperationException>(() => ExternalAdapters.CliSpec(model, "", "", false, false, _ => "fake")).Message);
    }
}
