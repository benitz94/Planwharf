// Scenario parity tests replaying recorded JavaScript fixtures against C# ports.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Planwharf.Engine.Tests;

public class ScenarioParityTests
{
    private static readonly string ScenariosFolder = FindScenariosFolder();
    private const long Epoch = 1700000000000L;

    private static string FindScenariosFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "native", "porting", "parity", "scenarios");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("native/porting/parity/scenarios not found above " + AppContext.BaseDirectory);
    }

    private static JsonObject LoadFixture(string module)
    {
        var file = Path.Combine(ScenariosFolder, $"{module}.json");
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject();
    }

    public static TheoryData<string> GetScenarios(string module)
    {
        var data = new TheoryData<string>();
        var fixture = LoadFixture(module);
        var scenarios = fixture["scenarios"]!.AsArray();
        foreach (var sc in scenarios)
        {
            data.Add(sc!["name"]!.GetValue<string>());
        }
        return data;
    }

    private static JsonObject GetScenario(string module, string name)
    {
        var fixture = LoadFixture(module);
        foreach (var sc in fixture["scenarios"]!.AsArray())
        {
            if (sc!["name"]!.GetValue<string>() == name) return sc.AsObject();
        }
        throw new KeyNotFoundException($"Scenario '{name}' not found in {module}.json");
    }

    public sealed class Exchange
    {
        private readonly JsonArray requests;
        private readonly JsonArray replies;
        private readonly string root;
        private int cursor;

        public Exchange(JsonArray requests, JsonArray replies, string root)
        {
            this.requests = requests;
            this.replies = replies;
            this.root = root;
        }

        public JsonNode Call(JsonNode? actualRequest)
        {
            var normalized = Normalize(actualRequest, root);
            if (cursor >= requests.Count)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected request #{cursor + 1}: {normalized?.ToJsonString()}");
            }
            var expectedReq = requests[cursor];
            if (!Same(expectedReq, normalized))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Request #{cursor + 1} mismatch:\nExpected: {expectedReq?.ToJsonString()}\nActual:   {normalized?.ToJsonString()}");
            }
            return replies[cursor++]!;
        }

        public void AssertAllConsumed()
        {
            if (cursor != requests.Count)
            {
                throw new Xunit.Sdk.XunitException($"Unconsumed replies: used {cursor} of {requests.Count}");
            }
        }
    }

    public static JsonNode? Normalize(JsonNode? value, string root)
    {
        if (value == null) return null;
        if (value is JsonValue jv && jv.TryGetValue<string>(out var s))
        {
            if (s.Contains(root) || s.Contains(root.Replace('\\', '/')))
            {
                try
                {
                    var parsed = JsonNode.Parse(s);
                    if (parsed is JsonObject or JsonArray)
                    {
                        var normParsed = Normalize(parsed, root);
                        var formatted = normParsed?.ToJsonString() ?? "";
                        if (s.EndsWith('\n')) formatted += "\n";
                        return JsonValue.Create(formatted);
                    }
                }
                catch { }
                return JsonValue.Create(s.Replace(root, "<ROOT>").Replace('\\', '/'));
            }
            return JsonValue.Create(s.Replace("synthetic-token", "<REDACTED>"));
        }
        if (value is JsonArray arr)
        {
            var res = new JsonArray();
            foreach (var item in arr) res.Add(Normalize(item?.DeepClone(), root));
            return res;
        }
        if (value is JsonObject obj)
        {
            var res = new JsonObject();
            foreach (var (k, v) in obj) res[k] = Normalize(v?.DeepClone(), root);
            return res;
        }
        return value.DeepClone();
    }

    public static JsonObject GetEffects(string root)
    {
        var output = new JsonObject();
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal);
        var rootSlash = root.Replace('\\', '/');
        var rootEscaped = root.Replace("\\", "\\\\");
        foreach (var file in files)
        {
            var relPath = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = File.ReadAllText(file);
            content = content
                .Replace(rootEscaped, "<ROOT>")
                .Replace(rootSlash, "<ROOT>")
                .Replace(root, "<ROOT>")
                .Replace("<ROOT>\\\\", "<ROOT>/")
                .Replace("<ROOT>\\", "<ROOT>/")
                .Replace("synthetic-token", "<REDACTED>")
                .Replace("\r\n", "\n");
            output[relPath] = content;
        }
        return output;
    }

    public static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Count == ob.Count && oa.All(pair => ob.TryGetPropertyValue(pair.Key, out var other) && Same(pair.Value, other));
        if (a is JsonArray aa && b is JsonArray ab)
            return aa.Count == ab.Count && aa.Zip(ab).All(pair => Same(pair.First, pair.Second));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
                return double.Parse(va.ToJsonString(), CultureInfo.InvariantCulture) == double.Parse(vb.ToJsonString(), CultureInfo.InvariantCulture);
            if (va.TryGetValue<string>(out var sa) && vb.TryGetValue<string>(out var sb))
            {
                sa = sa.Replace("\r\n", "\n");
                sb = sb.Replace("\r\n", "\n");
                if (sa == sb) return true;
                try
                {
                    var ja = JsonNode.Parse(sa);
                    var jb = JsonNode.Parse(sb);
                    if (ja != null && jb != null) return Same(ja, jb);
                }
                catch { }
                return false;
            }
            return JsonNode.DeepEquals(va, vb);
        }
        return false;
    }

    private static void AssertOutcome(JsonObject sc, JsonNode? actualResult, string? actualError, JsonObject actualEffects, Exchange exchange, List<JsonNode>? events = null)
    {
        if (actualError != null && !sc.ContainsKey("error"))
        {
            throw new Xunit.Sdk.XunitException($"Unexpected error for {sc["name"]}: '{actualError}'");
        }
        exchange.AssertAllConsumed();

        if (sc.TryGetPropertyValue("error", out var expErrNode) && expErrNode is JsonValue ev && ev.TryGetValue<string>(out var expErr))
        {
            Assert.True(actualError != null, $"Expected error '{expErr}' but got result: {actualResult?.ToJsonString()}");
            Assert.Contains(expErr, actualError);
        }
        else if (sc.TryGetPropertyValue("result", out var expResNode))
        {
            Assert.True(actualError == null, $"Expected result but got error: '{actualError}'");
            Assert.True(Same(expResNode, actualResult),
                $"Result mismatch for {sc["name"]}:\nExpected: {expResNode?.ToJsonString()}\nActual:   {actualResult?.ToJsonString()}");
        }

        if (sc.TryGetPropertyValue("effects", out var expEffNode) && expEffNode is JsonObject expEff)
        {
            Assert.True(Same(expEff, actualEffects),
                $"Effects mismatch for {sc["name"]}:\nExpected: {expEff.ToJsonString()}\nActual:   {actualEffects.ToJsonString()}");
        }

        if (events != null && sc.TryGetPropertyValue("events", out var expEventsNode) && expEventsNode is JsonArray expEvents)
        {
            var actualEventsArray = new JsonArray(events.Select(e => e.DeepClone()).ToArray());
            if (sc["name"]?.GetValue<string>() == "bad-listeners" && expEvents.Count == 1 && actualEventsArray.Count == 1)
            {
                var expStderr = expEvents[0]?["stderr"]?.GetValue<string>();
                var actStderr = actualEventsArray[0]?["stderr"]?.GetValue<string>();
                Assert.True(expStderr != null && actStderr != null && actStderr.Length > 0);
            }
            else
            {
                Assert.True(Same(expEvents, actualEventsArray),
                    $"Events mismatch for {sc["name"]}:\nExpected: {expEvents.ToJsonString()}\nActual:   {actualEventsArray.ToJsonString()}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "provider-models")]
    public async Task Replay_ProviderModels_Scenarios(string scenarioName)
    {
        var sc = GetScenario("provider-models", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pm-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunProviderModelsAsync(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "provider-adapters")]
    public async Task Replay_ProviderAdapters_Scenarios(string scenarioName)
    {
        var sc = GetScenario("provider-adapters", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pa-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunProviderAdaptersAsync(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "api-agent")]
    public async Task Replay_ApiAgent_Scenarios(string scenarioName)
    {
        var sc = GetScenario("api-agent", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-aa-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            var events = new List<JsonNode>();
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunApiAgentAsync(sc, root, exchange, events);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange, events);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "worktrees")]
    public void Replay_Worktrees_Scenarios(string scenarioName)
    {
        var sc = GetScenario("worktrees", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-wt-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = RunWorktrees(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            if (actualError != null)
                actualError = actualError.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "pr-check")]
    public async Task Replay_PrCheck_Scenarios(string scenarioName)
    {
        var sc = GetScenario("pr-check", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pr-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunPrCheckAsync(sc, root, exchange);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            if (actualError != null)
                actualError = actualError.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Theory]
    [MemberData(nameof(GetScenarios), "phone-activation-check")]
    public async Task Replay_PhoneActivationCheck_Scenarios(string scenarioName)
    {
        var sc = GetScenario("phone-activation-check", scenarioName);
        var root = Directory.CreateTempSubdirectory("pw-sc-pac-").FullName;
        try
        {
            var exchange = new Exchange(sc["requests"]!.AsArray(), sc["replies"]!.AsArray(), root);
            var events = new List<JsonNode>();
            JsonNode? actualResult = null;
            string? actualError = null;

            try
            {
                actualResult = await RunPhoneActivationCheckAsync(sc, root, exchange, events);
            }
            catch (Exception ex)
            {
                actualError = ex.InnerException?.Message ?? ex.Message;
            }

            if (actualError != null)
                actualError = actualError.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');

            var actualEffects = GetEffects(root);
            AssertOutcome(sc, actualResult, actualError, actualEffects, exchange, events);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task<JsonNode?> RunProviderModelsAsync(JsonObject sc, string root, Exchange exchange)
    {
        var name = sc["name"]!.GetValue<string>();
        var mode = sc["input"]?["mode"]?.GetValue<string>() ?? "";

        if (name.StartsWith("legacy-"))
        {
            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["provider"] = "grok", ["cli"] = "grok-exact", ["available"] = true, ["enabled"] = true }
            };
            int calls = 0;
            TaskCompletionSource? release = null;
            if (mode == "single-flight") release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var api = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Now = () => Epoch,
                LegacyDiscover = async () =>
                {
                    calls++;
                    if (mode == "single-flight" && release != null) await release.Task;
                    var req = new JsonObject { ["kind"] = "discover", ["call"] = calls };
                    var rep = exchange.Call(req);
                    return rep.AsObject();
                }
            });

            var first = api.RefreshAsync(true);
            if (mode == "single-flight")
            {
                var second = api.RefreshAsync(true);
                release!.SetResult();
                await Task.WhenAll(first, second);
            }
            else
            {
                await first;
            }
            if (mode == "ttl") await api.RefreshAsync();
            if (mode == "failure-recovery") await api.RefreshAsync(true);

            return new JsonObject
            {
                ["snapshot"] = api.Snapshot(),
                ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["calls"] = calls
            };
        }

        if (name.StartsWith("catalog-"))
        {
            var provider = new JsonObject
            {
                ["id"] = "fixture", ["adapter"] = "codex", ["quotaPoolId"] = "fixture",
                ["account"] = new JsonObject { ["key"] = "fake-account" },
                ["enabled"] = mode != "disabled"
            };
            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["providerId"] = "fixture", ["cli"] = "gpt-exact", ["available"] = true, ["enabled"] = true }
            };
            var cacheFile = Path.Combine(root, "catalog.json");
            if (mode == "rename-failure") Directory.CreateDirectory(cacheFile);
            int calls = 0;
            TaskCompletionSource? release = null;
            TaskCompletionSource? firstDiscoverStarted = null;
            if (mode == "stale-generation")
            {
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                firstDiscoverStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            Func<JsonObject?, Task<JsonObject>> discover = async p =>
            {
                calls++;
                var req = new JsonObject
                {
                    ["kind"] = "discover",
                    ["provider"] = p != null ? (JsonNode)p.DeepClone() : null,
                    ["call"] = calls
                };
                var rep = exchange.Call(req);
                if (mode == "stale-generation" && calls == 1)
                {
                    firstDiscoverStarted?.TrySetResult();
                    if (release != null) await release.Task;
                }
                return rep.AsObject();
            };

            Func<ProviderModels.IProviderModelsInstance> create = () => ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Providers = [provider],
                Now = () => Epoch,
                CacheFile = cacheFile,
                Discover = discover,
                FindExecutable = _ => mode != "not-installed"
            });

            var api = create();
            if (mode == "stale-generation")
            {
                var pending = api.RefreshAsync(true);
                await firstDiscoverStarted!.Task;
                await api.InvalidateAsync("fixture");
                var queued = api.RefreshAsync(true);
                release!.SetResult();
                await Task.WhenAll(pending, queued);
            }
            else
            {
                await api.RefreshAsync(true);
                if (mode == "restart")
                {
                    api = create();
                    await api.RefreshAsync(true);
                }
                if (mode == "invalidate-recovery")
                {
                    await api.InvalidateAsync("fixture");
                    await api.RefreshAsync(true);
                }
                if (mode == "rename-failure")
                {
                    try
                    {
                        await api.InvalidateAsync("fixture");
                    }
                    catch (Exception ex)
                    {
                        return new JsonObject
                        {
                            ["error"] = ex.Message,
                            ["snapshot"] = api.Snapshot(),
                            ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray())
                        };
                    }
                }
            }

            return new JsonObject
            {
                ["snapshot"] = api.Snapshot(),
                ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["calls"] = calls
            };
        }

        if (name.StartsWith("grok-list-"))
        {
            var fakeGrok = Path.Combine(root, "fake-grok");
            File.WriteAllText(fakeGrok, "");
            var savedCwd = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(root);
            Environment.SetEnvironmentVariable("PLANWHARF_GROK", "fake-grok");

            ProviderModels.GrokExecutor = cmd =>
            {
                var req = new JsonObject
                {
                    ["kind"] = "process",
                    ["command"] = cmd,
                    ["args"] = new JsonArray { (JsonNode)"models" },
                    ["options"] = new JsonObject { ["windowsHide"] = true, ["timeout"] = 15000, ["maxBuffer"] = 1048576 }
                };
                var rep = exchange.Call(req).AsObject();
                int code = rep.ContainsKey("error") && rep["error"] is JsonObject eo && eo.ContainsKey("code")
                    ? eo["code"]!.GetValue<int>()
                    : 0;
                string stdout = rep["stdout"]?.GetValue<string>() ?? "";
                string stderr = rep["stderr"]?.GetValue<string>() ?? "";
                return Task.FromResult((code, stdout, stderr));
            };
            try
            {
                var res = await ProviderModels.ListGrokAsync();
                try { File.Delete(fakeGrok); } catch { }
                return res;
            }
            finally
            {
                ProviderModels.GrokExecutor = null;
                Directory.SetCurrentDirectory(savedCwd);
                Environment.SetEnvironmentVariable("PLANWHARF_GROK", null);
                try { File.Delete(fakeGrok); } catch { }
            }
        }

        if (name == "switch-away-and-back-rejects-old-generation")
        {
            var provider = new JsonObject
            {
                ["id"] = "fixture", ["adapter"] = "codex",
                ["account"] = new JsonObject { ["key"] = "fake-A" },
                ["quotaPoolId"] = "fixture"
            };
            var models = new List<JsonObject>
            {
                new() { ["id"] = "exact", ["providerId"] = "fixture", ["cli"] = "gpt-exact", ["available"] = false }
            };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int calls = 0;
            var api = ProviderModels.CreateProviderModels(models, new ProviderModels.ProviderModelsOptions
            {
                Providers = [provider],
                Now = () => Epoch,
                Discover = async p =>
                {
                    calls++;
                    var req = new JsonObject
                    {
                        ["kind"] = "discover",
                        ["provider"] = p != null ? (JsonNode)p.DeepClone() : null,
                        ["call"] = calls
                    };
                    var rep = exchange.Call(req);
                    if (calls == 1) await release.Task;
                    return rep.AsObject();
                }
            });

            var pending = api.RefreshAsync(true);
            provider["account"]!["key"] = "fake-B"; await api.InvalidateAsync("fixture"); api.ObserveScope("fixture");
            provider["account"]!["key"] = "fake-A"; await api.InvalidateAsync("fixture"); api.ObserveScope("fixture");
            var next = api.RefreshAsync(true);
            release.SetResult();
            await Task.WhenAll(pending, next);

            return new JsonObject
            {
                ["snapshot"] = api.Snapshot(),
                ["models"] = new JsonArray(models.Select(m => (JsonNode)m.DeepClone()).ToArray()),
                ["calls"] = calls
            };
        }

        throw new NotSupportedException($"Scenario '{name}' not supported");
    }

    private static async Task<JsonNode?> RunProviderAdaptersAsync(JsonObject sc, string root, Exchange exchange)
    {
        var mode = sc["name"]!.GetValue<string>();
        ProviderAdapters.ResetBuiltIns();

        if (mode is "success" or "missing-auth" or "http-error" or "malformed" or "identity" or "registration")
        {
            if (mode != "missing-auth")
            {
                File.WriteAllText(Path.Combine(root, "auth.json"),
                    "{\"tokens\":{\"access_token\":\"synthetic-token\",\"account_id\":\"fake-account\"}}");
            }

            ProviderAdapters.FetchImpl = (url, reqMsg) =>
            {
                var headersObj = new JsonObject();
                foreach (var h in reqMsg.Headers) headersObj[h.Key] = string.Join(", ", h.Value);
                headersObj["Authorization"] = "<REDACTED>";
                var req = new JsonObject
                {
                    ["kind"] = "fetch",
                    ["url"] = url,
                    ["headers"] = headersObj,
                    ["timeoutMs"] = 10000
                };
                var rep = exchange.Call(req).AsObject();
                var status = rep["status"]!.GetValue<int>();
                var resp = new HttpResponseMessage((System.Net.HttpStatusCode)status)
                {
                    Content = new StringContent(rep["body"]!.ToJsonString(), Encoding.UTF8, "application/json")
                };
                return Task.FromResult(resp);
            };

            try
            {
                var adapter = ProviderAdapters.AdapterFor("codex")!;
                if (mode == "identity")
                {
                    return ProviderAdapters.BuildResultIdentity(new JsonObject
                    {
                        ["provider"] = "codex",
                        ["providerId"] = "custom",
                        ["cli"] = "gpt-exact",
                        ["accountKey"] = "fake-account"
                    });
                }
                if (mode == "registration")
                {
                    ProviderAdapters.RegisterAdapter("codex", new ProviderAdapters.AdapterDefinition(
                        NativeModel: null!, SourceProtectedResearch: false, Capabilities: new JsonObject(),
                        Executable: _ => null, Availability: _ => new JsonObject(), Execute: null!, Environment: null!,
                        Terminate: null!, QuotaAsync: null!, DiscoverAsync: null!, ResultIdentity: null!
                    ));
                    return null;
                }
                return await ProviderAdapters.DiscoverCodexAsync(new JsonObject { ["settingsFolder"] = root }, CancellationToken.None);
            }
            finally
            {
                ProviderAdapters.FetchImpl = null;
                ProviderAdapters.ResetBuiltIns();
            }
        }

        if (mode == "unknown-adapter")
        {
            return new JsonObject { ["found"] = ProviderAdapters.AdapterFor("unknown") != null };
        }

        if (mode == "account-identity")
        {
            return ProviderAdapters.AdapterFor("codex")!.ResultIdentity(new JsonObject
            {
                ["providerId"] = "fixture",
                ["cli"] = "gpt-exact",
                ["accountKey"] = "fake-account"
            });
        }

        if (mode is "missing-method" or "missing-regex" or "registered")
        {
            Func<JsonObject, string, string, bool, bool, LaunchSpecResult>? execute = null;
            if (mode != "missing-method")
            {
                execute = (m, f, pr, l, r) =>
                {
                    exchange.Call(new JsonObject { ["method"] = "execute", ["args"] = new JsonArray { m?.DeepClone(), f, pr, l, r } });
                    return new LaunchSpecResult("cmd", [], f, "fixture", "fixture", m?["cli"]?.ToString() ?? "");
                };
            }

            var def = new ProviderAdapters.AdapterDefinition(
                NativeModel: mode == "missing-regex" ? null! : ProviderAdapters.AdapterFor("codex")!.NativeModel,
                SourceProtectedResearch: false,
                Capabilities: new JsonObject(),
                Executable: p => { exchange.Call(new JsonObject { ["method"] = "executable", ["args"] = new JsonArray { p?.DeepClone() } }); return null; },
                Availability: p => { exchange.Call(new JsonObject { ["method"] = "availability", ["args"] = new JsonArray { p?.DeepClone() } }); return new JsonObject(); },
                Execute: execute!,
                Environment: (i, pl) => { exchange.Call(new JsonObject { ["method"] = "environment", ["args"] = new JsonArray { i != null ? new JsonObject() : null, pl } }); return new Dictionary<string, string>(); },
                Terminate: pid => { exchange.Call(new JsonObject { ["method"] = "terminate", ["args"] = new JsonArray { pid } }); },
                QuotaAsync: (p, ct) => { exchange.Call(new JsonObject { ["method"] = "quota", ["args"] = new JsonArray { p?.DeepClone() } }); return Task.FromResult(new JsonObject()); },
                DiscoverAsync: (p, ct) =>
                {
                    var rep = exchange.Call(new JsonObject { ["method"] = "discover", ["args"] = new JsonArray { p?.DeepClone() } });
                    return Task.FromResult(rep.AsObject());
                },
                ResultIdentity: m => { exchange.Call(new JsonObject { ["method"] = "resultIdentity", ["args"] = new JsonArray { m?.DeepClone() } }); return new JsonObject(); }
            );

            ProviderAdapters.RegisterAdapter("fixture", def);
            var registeredAdapter = ProviderAdapters.AdapterFor("fixture")!;
            var discoverReply = await registeredAdapter.DiscoverAsync(new JsonObject { ["id"] = "fixture" }, CancellationToken.None);
            return new JsonObject
            {
                ["frozen"] = true,
                ["reply"] = discoverReply?.DeepClone()
            };
        }

        throw new NotSupportedException($"Scenario '{mode}' not supported");
    }

    private sealed class CancellableResponse : HttpResponseMessage
    {
        private readonly Action onDispose;
        public CancellableResponse(System.Net.HttpStatusCode status, Action onDispose) : base(status)
        {
            this.onDispose = onDispose;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsSuccessStatusCode)
            {
                onDispose();
            }
            base.Dispose(disposing);
        }
    }

    private static async Task<JsonNode?> RunApiAgentAsync(JsonObject sc, string root, Exchange exchange, List<JsonNode> events)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "concurrent-isolated-tools")
        {
            var lanes = new[] { "one", "two" };
            var tasks = lanes.Select(async lane =>
            {
                var folder = Path.Combine(root, lane);
                Directory.CreateDirectory(folder);
                var call = new JsonObject
                {
                    ["name"] = "write_file",
                    ["input"] = new JsonObject { ["path"] = "result.txt", ["content"] = lane }
                };
                exchange.Call(new JsonObject
                {
                    ["kind"] = "tool",
                    ["lane"] = lane,
                    ["call"] = (JsonObject)call.DeepClone()
                });
                var deps = new ApiAgent.Dependencies
                {
                    Folder = folder,
                    Http = (_, _) => Task.FromResult(new HttpResponseMessage())
                };
                await ApiAgent.ExecuteToolAsync(call, new JsonObject(), deps, CancellationToken.None);
                var readCall = new JsonObject
                {
                    ["name"] = "read_file",
                    ["input"] = new JsonObject { ["path"] = "result.txt" }
                };
                return await ApiAgent.ExecuteToolAsync(readCall, new JsonObject(), deps, CancellationToken.None);
            });
            var results = await Task.WhenAll(tasks);
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r?.DeepClone());
            return arr;
        }

        if (name == "approved-command-fake-process")
        {
            var call = new JsonObject
            {
                ["name"] = "run_command",
                ["input"] = new JsonObject
                {
                    ["command"] = "fake-tool",
                    ["args"] = new JsonArray { (JsonNode)"space Ω" }
                }
            };
            var deps = new ApiAgent.Dependencies
            {
                Folder = root,
                Http = (_, _) => Task.FromResult(new HttpResponseMessage()),
                RunCommand = (cmd, ct) =>
                {
                    var argsArr = new JsonArray();
                    foreach (var a in cmd.Args) argsArr.Add((JsonNode)a);
                    var req = new JsonObject
                    {
                        ["kind"] = "process",
                        ["command"] = cmd.Executable,
                        ["args"] = argsArr,
                        ["options"] = new JsonObject
                        {
                            ["cwd"] = Normalize(JsonValue.Create(cmd.Cwd), root),
                            ["windowsHide"] = cmd.WindowsHide,
                            ["timeout"] = cmd.TimeoutMs,
                            ["maxBuffer"] = cmd.MaxBuffer,
                            ["encoding"] = "utf8"
                        }
                    };
                    var rep = exchange.Call(req).AsObject();
                    int? code = rep.ContainsKey("error") && rep["error"] is JsonObject eo && eo.ContainsKey("code")
                        ? eo["code"]!.GetValue<int>()
                        : 0;
                    string stdout = rep["stdout"]?.GetValue<string>() ?? "";
                    string stderr = rep["stderr"]?.GetValue<string>() ?? "";
                    return Task.FromResult(new ApiAgent.CommandReply(code, stdout, stderr));
                }
            };
            var job = new JsonObject { ["alwaysApprove"] = true };
            return await ApiAgent.ExecuteToolAsync(call, job, deps, CancellationToken.None);
        }

        if (name.StartsWith("tool-"))
        {
            var mode = sc["input"]!["mode"]!.GetValue<string>();
            if (mode == "read-limit")
            {
                File.WriteAllText(Path.Combine(root, "large.txt"), new string('x', 65537));
            }
            JsonObject call;
            if (mode == "unknown-tool") call = new JsonObject { ["name"] = "unknown", ["input"] = new JsonObject() };
            else if (mode == "command-denied") call = new JsonObject { ["name"] = "run_command", ["input"] = new JsonObject { ["command"] = "never-run", ["args"] = new JsonArray() } };
            else if (mode == "read-limit") call = new JsonObject { ["name"] = "read_file", ["input"] = new JsonObject { ["path"] = "large.txt" } };
            else
            {
                string targetPath = mode == "escape" ? "../escape" : mode == "control-file" ? ".git/config" : "hello.txt";
                call = new JsonObject { ["name"] = "write_file", ["input"] = new JsonObject { ["path"] = targetPath, ["content"] = "Hello Ω" } };
            }

            exchange.Call(new JsonObject { ["kind"] = "tool", ["call"] = (JsonObject)call.DeepClone() });

            var deps = new ApiAgent.Dependencies
            {
                Folder = root,
                Http = (_, _) => Task.FromResult(new HttpResponseMessage())
            };
            var job = new JsonObject { ["lead"] = mode == "lead-write" };
            var result = await ApiAgent.ExecuteToolAsync(call, job, deps, CancellationToken.None);

            if (mode == "write-read")
            {
                var readCall = new JsonObject { ["name"] = "read_file", ["input"] = new JsonObject { ["path"] = "hello.txt" } };
                var text = await ApiAgent.ExecuteToolAsync(readCall, job, deps, CancellationToken.None);
                return new JsonObject
                {
                    ["result"] = result?.DeepClone(),
                    ["text"] = text?.DeepClone()
                };
            }
            return result;
        }

        var protocol = sc["input"]!["protocol"]!.GetValue<string>();
        var agentMode = sc["input"]!["mode"]!.GetValue<string>();

        var agentJob = new JsonObject
        {
            ["model"] = "exact-model",
            ["prompt"] = "Fixture prompt",
            ["advisory"] = agentMode == "advisory",
            ["lead"] = agentMode == "tool-recovery",
            ["config"] = new JsonObject
            {
                ["baseUrl"] = "https://fixture.invalid",
                ["apiProtocol"] = protocol,
                ["maxOutputTokens"] = 32,
                ["maxTurns"] = agentMode == "turn-limit" ? 1 : 3,
                ["requestTimeoutMs"] = 1000
            }
        };

        var agentDeps = new ApiAgent.Dependencies
        {
            Folder = root,
            Emit = ev => events.Add(ev.DeepClone()),
            Http = (req, ct) =>
            {
                var actualReq = new JsonObject
                {
                    ["kind"] = "fetch",
                    ["url"] = req.Url,
                    ["method"] = "POST",
                    ["headers"] = (JsonObject)req.Headers.DeepClone(),
                    ["body"] = (JsonObject)req.Body.DeepClone(),
                    ["redirect"] = req.Redirect,
                    ["timeoutMs"] = req.TimeoutMs
                };
                var rep = exchange.Call(actualReq).AsObject();
                var status = rep["status"]!.GetValue<int>();
                var bodyStr = rep["body"]!.GetValue<string>();
                var resp = new CancellableResponse((System.Net.HttpStatusCode)status, () =>
                {
                    events.Add(new JsonObject { ["cancelledBody"] = true });
                })
                {
                    Content = new StringContent(bodyStr, Encoding.UTF8, "application/json")
                };
                return Task.FromResult<HttpResponseMessage>(resp);
            }
        };

        return await ApiAgent.RunAgentAsync(agentJob, agentDeps, CancellationToken.None);
    }

    private static JsonNode? RunWorktrees(JsonObject sc, string root, Exchange exchange)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "orphan-sweep-retains-live")
        {
            var liveDir = Path.Combine(root, "worktrees", "project", "live");
            var orphanDir = Path.Combine(root, "worktrees", "project", "orphan");
            Directory.CreateDirectory(liveDir);
            Directory.CreateDirectory(orphanDir);
            File.WriteAllText(Path.Combine(orphanDir, "recoverable.txt"), "Disposable fixture");
            var removed = Worktrees.SweepOrphans(root, new[] { "live" });
            var arr = new JsonArray();
            foreach (var r in removed) arr.Add((JsonNode)r);
            return new JsonObject
            {
                ["removed"] = arr,
                ["liveRetained"] = Directory.Exists(liveDir)
            };
        }

        Worktrees.GitRunner runner = call =>
        {
            var req = new JsonObject
            {
                ["kind"] = "process",
                ["command"] = "git",
                ["args"] = new JsonArray(call.Args.Select(a => (JsonNode)a).ToArray()),
                ["cwd"] = Normalize(JsonValue.Create(call.Folder), root)
            };
            if (call.TimeoutMs.HasValue) req["timeout"] = call.TimeoutMs.Value;
            if (call.MaxBytes.HasValue) req["maxBuffer"] = call.MaxBytes.Value;
            var rep = exchange.Call(req).AsObject();
            var stdout = rep["stdout"]?.GetValue<string>() ?? "";
            stdout = stdout.Replace("<ROOT>", root.Replace('\\', '/'));
            var stderr = rep["stderr"]?.GetValue<string>() ?? "";
            stderr = stderr.Replace("<ROOT>", root.Replace('\\', '/'));
            return new Worktrees.GitReply(
                rep["status"]?.GetValue<int>() ?? 0,
                stdout,
                stderr
            );
        };
        var wt = new Worktrees(runner);

        if (name is "ordered-merge" or "dirty-root" or "dirty-part" or "conflict-abort" or "probe-failure" or "merge-failure" or "recovery")
        {
            var first = Path.Combine(root, "part é 1");
            var second = Path.Combine(root, "part 2");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var parts = new List<Worktrees.Part>
            {
                new(first, "work--part-1", "ST-1"),
                new(second, "work--part-2", "ST-2")
            };
            var res = wt.Integrate(root, parts);
            Worktrees.MergeResult? rec = name == "recovery" ? wt.Integrate(root, parts) : null;

            static JsonNode? FormatMerge(Worktrees.MergeResult? r)
            {
                if (r == null) return null;
                var obj = new JsonObject { ["ok"] = r.Ok };
                if (r.Branch != null) obj["branch"] = r.Branch;
                if (!r.Ok)
                {
                    var confArr = new JsonArray();
                    if (r.Conflicts != null) foreach (var c in r.Conflicts) confArr.Add((JsonNode)c);
                    obj["conflicts"] = confArr;
                }
                if (r.Reason != null) obj["reason"] = r.Reason;
                return obj;
            }

            return new JsonObject
            {
                ["result"] = FormatMerge(res),
                ["recovery"] = FormatMerge(rec)
            };
        }

        var projectFolder = Path.Combine(root, "project é");
        Directory.CreateDirectory(Path.Combine(projectFolder, ".git"));
        File.WriteAllText(Path.Combine(projectFolder, ".git", "config"), "");

        var subId = name == "invalid-identity" ? "../bad" : "ST";
        var index = name == "invalid-index" ? 0 : 1;
        var destination = Path.Combine(root, "worktrees", "project", "MT", "ST");
        var branch = "work--part-1";

        if (name is "cleanup-dirty" or "discard")
        {
            Directory.CreateDirectory(destination);
            wt.Cleanup(projectFolder, root, "project", "MT", new[] { new Worktrees.Part(destination, branch, "ST") }, discardBranches: name == "discard");
            return null;
        }

        if (name is "reuse" or "occupied")
        {
            Directory.CreateDirectory(destination);
        }

        var part = wt.Create(projectFolder, root, "project", "MT", subId, index, "work");
        if (part == null) return null;
        return new JsonObject
        {
            ["folder"] = Normalize(JsonValue.Create(part.Folder), root),
            ["branch"] = part.Branch,
            ["subId"] = part.SubId
        };
    }

    private static async Task<JsonNode?> RunPrCheckAsync(JsonObject sc, string root, Exchange exchange)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "concurrent-independent-repairs")
        {
            var turn = 0;
            var turnLock = new object();
            var branches = new[] { "one", "two" };
            var tasks = branches.Select(async branch =>
            {
                var myRemainder = branch == "one" ? 0 : 1;
                PrCommand cmd = async (folder, command, args) =>
                {
                    while (true)
                    {
                        lock (turnLock)
                        {
                            if (turn % 2 == myRemainder) break;
                        }
                        await Task.Yield();
                    }
                    var req = new JsonObject
                    {
                        ["kind"] = "process",
                        ["command"] = command,
                        ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
                        ["cwd"] = Normalize(JsonValue.Create(folder), root)
                    };
                    var rep = exchange.Call(req).AsObject();
                    lock (turnLock)
                    {
                        turn++;
                    }
                    var stdout = rep["stdout"]?.GetValue<string>() ?? "";
                    stdout = stdout.Replace("<ROOT>", root.Replace('\\', '/'));
                    var stderr = rep["stderr"]?.GetValue<string>() ?? "";
                    stderr = stderr.Replace("<ROOT>", root.Replace('\\', '/'));
                    return new PrCommandResult(
                        rep["status"]?.GetValue<int>() ?? 0,
                        stdout,
                        stderr
                    );
                };
                var api = new PrCheck(cmd);
                return await api.PrepareWorktree(root, branch, "main", Array.Empty<string>(), Path.Combine(root, branch));
            });
            var results = await Task.WhenAll(tasks);
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r?.DeepClone());
            return arr;
        }

        PrCommand runner = (folder, command, args) =>
        {
            var req = new JsonObject
            {
                ["kind"] = "process",
                ["command"] = command,
                ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()),
                ["cwd"] = Normalize(JsonValue.Create(folder), root)
            };
            var rep = exchange.Call(req).AsObject();
            var stdout = rep["stdout"]?.GetValue<string>() ?? "";
            stdout = stdout.Replace("<ROOT>", root.Replace('\\', '/'));
            var stderr = rep["stderr"]?.GetValue<string>() ?? "";
            stderr = stderr.Replace("<ROOT>", root.Replace('\\', '/'));
            return Task.FromResult(new PrCommandResult(
                rep["status"]?.GetValue<int>() ?? 0,
                stdout,
                stderr
            ));
        };
        var prCheck = new PrCheck(runner);

        var worktree = Path.Combine(root, "repair é");
        if (!Directory.Exists(worktree)) Directory.CreateDirectory(worktree);

        if (name.Contains("list"))
        {
            var res = await prCheck.ListPrs(root);
            if (res.TryGetPropertyValue("prs", out var prsNode) && prsNode is JsonArray prsArr)
            {
                foreach (var item in prsArr)
                {
                    if (item is JsonObject prObj && prObj.TryGetPropertyValue("url", out var urlVal) && urlVal == null)
                    {
                        prObj.Remove("url");
                    }
                }
            }
            return res;
        }
        if (name.StartsWith("resolve"))
        {
            return await prCheck.ResolveConflicts(root, 7);
        }
        if (name == "rerun")
        {
            var notRun = new JsonArray
            {
                new JsonObject { ["runId"] = "12" },
                new JsonObject { ["runId"] = "12" },
                new JsonObject { ["runId"] = "13" },
                new JsonObject()
            };
            return await prCheck.RerunNotRun(root, notRun);
        }
        if (name is "finish-noop" or "push-refused" or "push-success" or "markers")
        {
            var fix = new JsonObject
            {
                ["before"] = "before",
                ["branch"] = "feature",
                ["conflicts"] = new JsonArray()
            };
            return await prCheck.FinishFix(root, worktree, fix, 7);
        }

        if (name.StartsWith("prepare-failure-"))
        {
            var failWorktree = Path.Combine(root, "repair");
            return await prCheck.PrepareFix(root, 7, failWorktree);
        }

        return await prCheck.PrepareFix(root, 7, worktree);
    }

    private static async Task<JsonNode?> RunPhoneActivationCheckAsync(JsonObject sc, string root, Exchange exchange, List<JsonNode> events)
    {
        var name = sc["name"]!.GetValue<string>();

        if (name == "concurrent-isolated-checks")
        {
            var lanes = new[] { "one", "two" };
            var turn = 0;
            var turnLock = new object();
            var tasks = lanes.Select(async lane =>
            {
                var dir = Path.Combine(root, lane);
                Directory.CreateDirectory(dir);
                var myRemainder = lane == "one" ? 0 : 1;
                var report = Path.Combine(dir, "report.md");
                var argv = new[] { "node", "check", "--attempts", dir, "--report", report };

                var options = new PhoneActivationCheckOptions
                {
                    Argv = argv,
                    StdoutWrite = text =>
                    {
                        var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                        lock (events)
                        {
                            events.Add(new JsonObject { ["lane"] = lane, ["stdout"] = norm });
                        }
                    },
                    StderrWrite = text =>
                    {
                        var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                        lock (events)
                        {
                            events.Add(new JsonObject { ["lane"] = lane, ["stderr"] = norm });
                        }
                    },
                    ListInstalledModels = async opts =>
                    {
                        while (true)
                        {
                            lock (turnLock) { if (turn % 2 == myRemainder) break; }
                            await Task.Yield();
                        }
                        var req = new JsonObject
                        {
                            ["lane"] = lane,
                            ["kind"] = "listInstalledModels",
                            ["options"] = (JsonObject)opts!.DeepClone()
                        };
                        var rep = exchange.Call(req).AsObject();
                        lock (turnLock) { turn++; }
                        return rep;
                    },
                    AskOllama = _ => throw new InvalidOperationException("Unexpected inference")
                };

                var res = await PhoneActivationCheck.RunAsync(options);
                return new JsonObject { ["lane"] = lane, ["exitCode"] = res.ExitCode };
            });
            var results = await Task.WhenAll(tasks);
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r.DeepClone());
            return arr;
        }

        var mode = sc["input"]!["mode"]!.GetValue<string>();
        var attempts = Path.Combine(root, "attempts");
        Directory.CreateDirectory(attempts);
        if (mode is not ("blocked-no-token" or "live-blocked"))
        {
            File.WriteAllText(Path.Combine(attempts, "telegram-bot.token"), "synthetic-token");
        }
        var report = Path.Combine(root, mode == "report-collision" ? "report.txt" : "report.md");
        var argv = new List<string>
        {
            "node", "phone-activation-check.cjs",
            "--attempts", attempts,
            "--report", report,
            "--build", Path.Combine(root, "build/Planwharf.exe")
        };
        if (mode is "live" or "live-blocked")
        {
            argv.Add("--live");
            argv.Add("yes");
        }
        if (mode is "listener" or "bad-listeners")
        {
            var file = Path.Combine(root, "listeners.json");
            var content = mode == "bad-listeners"
                ? "{"
                : new JsonArray
                {
                    new JsonObject
                    {
                        ["path"] = Path.Combine(root, "installed/Planwharf.exe"),
                        ["running"] = true,
                        ["pollsTelegram"] = true,
                        ["pid"] = 17
                    }
                }.ToJsonString();
            File.WriteAllText(file, content);
            argv.Add("--listeners");
            argv.Add(file);
        }
        if (mode == "backup-notebook")
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            var starter = Path.Combine(root, "starter.json");
            File.WriteAllText(starter, "{\"schema\":2,\"projects\":[]}\n");
            File.WriteAllText(Path.Combine(source, "plan.json"), "{\"schema\":2,\"projects\":[]}\n");
            argv.AddRange(new[]
            {
                "--from", source,
                "--backup-to", Path.Combine(root, "backup"),
                "--notebook", Path.Combine(root, "isolated"),
                "--starter", starter
            });
        }

        var options = new PhoneActivationCheckOptions
        {
            Argv = argv,
            StdoutWrite = text =>
            {
                var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                events.Add(new JsonObject { ["stdout"] = norm });
            },
            StderrWrite = text =>
            {
                var norm = text.Replace(root.Replace('\\', '/'), "<ROOT>").Replace(root, "<ROOT>").Replace('\\', '/');
                events.Add(new JsonObject { ["stderr"] = norm });
            },
            ListInstalledModels = async opts =>
            {
                var req = new JsonObject
                {
                    ["kind"] = "listInstalledModels",
                    ["options"] = (JsonObject)opts!.DeepClone()
                };
                var rep = exchange.Call(req).AsObject();
                return rep;
            },
            AskOllama = async opts =>
            {
                var req = new JsonObject
                {
                    ["kind"] = "askOllama",
                    ["options"] = (JsonObject)opts!.DeepClone()
                };
                var rep = exchange.Call(req).AsObject();
                return rep;
            },
            FetchImpl = async url =>
            {
                var redUrl = url.Replace("synthetic-token", "<REDACTED>");
                var req = new JsonObject
                {
                    ["kind"] = "fetch",
                    ["url"] = redUrl
                };
                var rep = exchange.Call(req).AsObject();
                var status = rep["ok"]?.GetValue<bool>() == true ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.BadRequest;
                var resp = new HttpResponseMessage(status)
                {
                    Content = new StringContent(rep.ToJsonString(), Encoding.UTF8, "application/json")
                };
                return resp;
            }
        };

        var result = await PhoneActivationCheck.RunAsync(options);
        return new JsonObject { ["exitCode"] = result.ExitCode };
    }

    [Fact]
    public void Mutation_UnexpectedRequest_Fails()
    {
        var exchange = new Exchange(
            new JsonArray { new JsonObject { ["kind"] = "expected" } },
            new JsonArray { new JsonObject { ["ok"] = true } },
            "fake-root");

        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            exchange.Call(new JsonObject { ["kind"] = "unexpected" }));
        Assert.Contains("Request #1 mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_UnconsumedReply_Fails()
    {
        var exchange = new Exchange(
            new JsonArray { new JsonObject { ["kind"] = "call" } },
            new JsonArray { new JsonObject { ["ok"] = true } },
            "fake-root");

        var ex = Assert.Throws<Xunit.Sdk.XunitException>(() =>
            exchange.AssertAllConsumed());
        Assert.Contains("Unconsumed replies: used 0 of 1", ex.Message);
    }

    [Fact]
    public void Mutation_ResultMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["result"] = new JsonObject { ["exitCode"] = 0 }
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["exitCode"] = 1 }, null, new JsonObject(), exchange));
        Assert.Contains("Result mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_EffectsMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["result"] = new JsonObject { ["ok"] = true },
            ["effects"] = new JsonObject { ["file.txt"] = "expected content" }
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["ok"] = true }, null, new JsonObject { ["file.txt"] = "mutated content" }, exchange));
        Assert.Contains("Effects mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_EventsMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["result"] = new JsonObject { ["ok"] = true },
            ["events"] = new JsonArray { new JsonObject { ["stdout"] = "expected" } }
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");
        var actualEvents = new List<JsonNode> { new JsonObject { ["stdout"] = "mutated" } };

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["ok"] = true }, null, new JsonObject(), exchange, actualEvents));
        Assert.Contains("Events mismatch", ex.Message);
    }

    [Fact]
    public void Mutation_ErrorMismatch_Fails()
    {
        var sc = new JsonObject
        {
            ["name"] = "test-scenario",
            ["error"] = "Expected error message"
        };
        var exchange = new Exchange(new JsonArray(), new JsonArray(), "fake-root");

        var exWrongErr = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, null, "Completely different error", new JsonObject(), exchange));
        Assert.Contains("Sub-string not found", exWrongErr.Message);

        var exNoErr = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertOutcome(sc, new JsonObject { ["ok"] = true }, null, new JsonObject(), exchange));
        Assert.Contains("Expected error 'Expected error message' but got result:", exNoErr.Message);
    }
}
