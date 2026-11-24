// Port of engine/provider-adapters.cjs.
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ProviderAdapters
{
    private const RegexOptions JsOptions = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;

    public sealed record AdapterDefinition(
        Regex NativeModel,
        bool SourceProtectedResearch,
        JsonObject Capabilities,
        Func<JsonObject?, string?> Executable,
        Func<JsonObject?, JsonObject> Availability,
        Func<JsonObject, string, string, bool, bool, LaunchSpecResult> Execute,
        Func<IDictionary<string, string>?, string?, Dictionary<string, string>> Environment,
        Action<int> Terminate,
        Func<JsonObject?, CancellationToken, Task<JsonObject>> QuotaAsync,
        Func<JsonObject?, CancellationToken, Task<JsonObject>> DiscoverAsync,
        Func<JsonObject, JsonObject> ResultIdentity,
        Func<JsonObject?, ProviderAccounts.AccountMetadata>? Account = null
    );

    private static readonly Dictionary<string, AdapterDefinition> Adapters = new(StringComparer.Ordinal);
    private static readonly object SyncLock = new();

    public static Func<string, HttpRequestMessage, Task<HttpResponseMessage>>? FetchImpl { get; set; }

    static ProviderAdapters()
    {
        ResetBuiltIns();
    }

    public static void ResetBuiltIns()
    {
        lock (SyncLock)
        {
            Adapters.Clear();
            RegisterBuiltIn("claude", new AdapterDefinition(
                NativeModel: new Regex(@"^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$", JsOptions),
                SourceProtectedResearch: false,
                Capabilities: DefaultCapabilities(canResearch: false),
                Executable: _ => ProviderRuntime.Executable("claude"),
                Availability: _ => new JsonObject { ["available"] = ProviderRuntime.Executable("claude") != null },
                Execute: (model, folder, prompt, lead, research) => ProviderRuntime.LaunchSpec(model, folder, prompt, lead, research),
                Environment: (inherited, platform) => ProviderRuntime.ProviderEnvironment("claude", inherited, platform),
                Terminate: pid => RunnerKill(pid),
                QuotaAsync: (provider, ct) => ReadClaudeQuotaAsync(provider, ct),
                DiscoverAsync: (provider, ct) => DiscoverClaudeAsync(provider, ct),
                ResultIdentity: model => BuildResultIdentity(model),
                Account: _ => ProviderAccounts.UnknownAccount()
            ));

            RegisterBuiltIn("codex", new AdapterDefinition(
                NativeModel: new Regex(@"^gpt-", JsOptions),
                SourceProtectedResearch: true,
                Capabilities: DefaultCapabilities(canResearch: true),
                Executable: _ => ProviderRuntime.Executable("codex"),
                Availability: _ => new JsonObject { ["available"] = ProviderRuntime.Executable("codex") != null },
                Execute: (model, folder, prompt, lead, research) => ProviderRuntime.LaunchSpec(model, folder, prompt, lead, research),
                Environment: (inherited, platform) => ProviderRuntime.ProviderEnvironment("codex", inherited, platform),
                Terminate: pid => RunnerKill(pid),
                QuotaAsync: (provider, ct) => ReadCodexQuotaAsync(provider, ct),
                DiscoverAsync: (provider, ct) => DiscoverCodexAsync(provider, ct),
                ResultIdentity: model => BuildResultIdentity(model),
                Account: provider => ProviderAccounts.CodexAccount(provider?["id"]?.ToString(), provider?["settingsFolder"]?.ToString())
            ));

            RegisterBuiltIn("grok", new AdapterDefinition(
                NativeModel: new Regex(@"^grok-", JsOptions),
                SourceProtectedResearch: false,
                Capabilities: DefaultCapabilities(canResearch: true),
                Executable: _ => ProviderRuntime.Executable("grok"),
                Availability: _ => new JsonObject { ["available"] = ProviderRuntime.Executable("grok") != null },
                Execute: (model, folder, prompt, lead, research) => ProviderRuntime.LaunchSpec(model, folder, prompt, lead, research),
                Environment: (inherited, platform) => ProviderRuntime.ProviderEnvironment("grok", inherited, platform),
                Terminate: pid => RunnerKill(pid),
                QuotaAsync: (provider, ct) => ReadGrokQuotaAsync(ct),
                DiscoverAsync: (_, ct) => ProviderModels.ListGrokAsync(ct),
                ResultIdentity: model => BuildResultIdentity(model),
                Account: provider => ProviderAccounts.GrokAccount(provider?["id"]?.ToString())
            ));

            RegisterBuiltIn("antigravity", new AdapterDefinition(
                NativeModel: new Regex(@"^(gemini-|claude-|gpt-oss-)", JsOptions),
                SourceProtectedResearch: false,
                Capabilities: DefaultCapabilities(canResearch: true),
                Executable: _ => ProviderRuntime.Executable("antigravity"),
                Availability: _ => new JsonObject { ["available"] = ProviderRuntime.Executable("antigravity") != null },
                Execute: (model, folder, prompt, lead, research) => ProviderRuntime.LaunchSpec(model, folder, prompt, lead, research),
                Environment: (inherited, platform) => ProviderRuntime.ProviderEnvironment("antigravity", inherited, platform),
                Terminate: pid => RunnerKill(pid),
                QuotaAsync: (_, ct) => ReadAntigravityQuotaAsync(ct),
                DiscoverAsync: (_, ct) => DiscoverAntigravityAsync(ct),
                ResultIdentity: model => BuildResultIdentity(model),
                Account: _ => ProviderAccounts.UnknownAccount()
            ));
        }
    }

    private static void RegisterBuiltIn(string id, AdapterDefinition def)
    {
        Adapters[id] = def;
    }

    private static JsonObject DefaultCapabilities(bool canResearch) => new()
    {
        ["canCode"] = true,
        ["canResearch"] = canResearch,
        ["canUseTools"] = true,
        ["canInspectFilesystem"] = true,
        ["supportsStreaming"] = true
    };

    private static void RunnerKill(int pid)
    {
        try
        {
            var proc = System.Diagnostics.Process.GetProcessById(pid);
            proc.Kill(true);
        }
        catch { }
    }

    public static JsonObject BuildResultIdentity(JsonObject model)
    {
        var provider = model["providerId"]?.DeepClone() ?? model["provider"]?.DeepClone();
        var cli = model["cli"]?.DeepClone();
        var res = new JsonObject
        {
            ["provider"] = provider,
            ["model"] = cli
        };
        if (model.ContainsKey("accountKey") && model["accountKey"] != null)
        {
            res["accountKey"] = model["accountKey"]?.DeepClone();
        }
        return res;
    }

    public static IReadOnlyDictionary<string, AdapterDefinition> GetAdapters()
    {
        lock (SyncLock)
        {
            return new Dictionary<string, AdapterDefinition>(Adapters, StringComparer.Ordinal);
        }
    }

    public static AdapterDefinition? AdapterFor(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (SyncLock)
        {
            return Adapters.TryGetValue(id, out var adapter) ? adapter : null;
        }
    }

    public static AdapterDefinition? AdapterFor(JsonObject? value)
    {
        if (value == null) return null;
        var id = value["adapter"]?.GetValue<string>() ?? value["provider"]?.GetValue<string>();
        return AdapterFor(id);
    }

    private static InvalidOperationException InvalidOp(string message) => new(message);

    public static void RegisterAdapter(string id, AdapterDefinition adapter)
    {
        if (string.IsNullOrEmpty(id)) throw InvalidOp("Adapter ID must be unique.");
        lock (SyncLock)
        {
            if (Adapters.ContainsKey(id)) throw InvalidOp("Adapter ID must be unique.");
            if (adapter.Executable == null) throw InvalidOp("Adapter requires executable");
            if (adapter.Availability == null) throw InvalidOp("Adapter requires availability");
            if (adapter.Execute == null) throw InvalidOp("Adapter requires execute");
            if (adapter.Environment == null) throw InvalidOp("Adapter requires environment");
            if (adapter.Terminate == null) throw InvalidOp("Adapter requires terminate");
            if (adapter.QuotaAsync == null) throw InvalidOp("Adapter requires quota");
            if (adapter.DiscoverAsync == null) throw InvalidOp("Adapter requires discover");
            if (adapter.ResultIdentity == null) throw InvalidOp("Adapter requires resultIdentity");
            if (adapter.NativeModel == null) throw InvalidOp("Adapter requires exact nativeModel validation.");

            Adapters[id] = adapter;
        }
    }

    public static void RegisterAdapter(string id, ExternalAdapters.Adapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var def = new AdapterDefinition(
            NativeModel: new Regex(adapter.NativeModel, JsOptions),
            SourceProtectedResearch: adapter.SourceProtectedResearch,
            Capabilities: (JsonObject)adapter.Capabilities.DeepClone(),
            Executable: provider => adapter.Executable(provider ?? new JsonObject()),
            Availability: provider => adapter.Availability(provider ?? new JsonObject()),
            Execute: (model, folder, prompt, lead, research) =>
            {
                var execRes = adapter.Execute(model, folder, prompt, lead, research);
                var args = new List<string>();
                if (execRes["args"] is JsonArray arr)
                {
                    foreach (var item in arr) args.Add(item?.ToString() ?? "");
                }
                return new LaunchSpecResult(
                    Command: execRes["command"]?.ToString() ?? "",
                    Args: args,
                    Cwd: execRes["cwd"]?.ToString() ?? folder,
                    Provider: execRes["provider"]?.ToString() ?? "",
                    Adapter: execRes["adapter"]?.ToString() ?? "",
                    Model: execRes["model"]?.ToString() ?? "",
                    Stdin: execRes["stdin"]?.ToString(),
                    PromptFile: execRes["promptFile"]?.ToString()
                );
            },
            Environment: (inherited, _) =>
            {
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (inherited != null)
                {
                    foreach (var (k, v) in inherited) dict[k] = v;
                }
                return dict;
            },
            Terminate: adapter.Terminate,
            QuotaAsync: (_, _) => adapter.QuotaAsync(),
            DiscoverAsync: (provider, ct) => adapter.DiscoverAsync(provider ?? new JsonObject(), ct),
            ResultIdentity: adapter.ResultIdentity
        );
        RegisterAdapter(id, def);
    }

    public static void RegisterAdapter(string id,
        Regex? nativeModel,
        Func<JsonObject?, string?>? executable,
        Func<JsonObject?, JsonObject>? availability,
        Func<JsonObject, string, string, bool, bool, LaunchSpecResult>? execute,
        Func<IDictionary<string, string>?, string?, Dictionary<string, string>>? environment,
        Action<int>? terminate,
        Func<JsonObject?, CancellationToken, Task<JsonObject>>? quota,
        Func<JsonObject?, CancellationToken, Task<JsonObject>>? discover,
        Func<JsonObject, JsonObject>? resultIdentity,
        JsonObject? capabilities = null,
        bool sourceProtectedResearch = false)
    {
        if (string.IsNullOrEmpty(id)) throw InvalidOp("Adapter ID must be unique.");
        lock (SyncLock)
        {
            if (Adapters.ContainsKey(id)) throw InvalidOp("Adapter ID must be unique.");
            if (executable == null) throw InvalidOp("Adapter requires executable");
            if (availability == null) throw InvalidOp("Adapter requires availability");
            if (execute == null) throw InvalidOp("Adapter requires execute");
            if (environment == null) throw InvalidOp("Adapter requires environment");
            if (terminate == null) throw InvalidOp("Adapter requires terminate");
            if (quota == null) throw InvalidOp("Adapter requires quota");
            if (discover == null) throw InvalidOp("Adapter requires discover");
            if (resultIdentity == null) throw InvalidOp("Adapter requires resultIdentity");
            if (nativeModel == null) throw InvalidOp("Adapter requires exact nativeModel validation.");

            Adapters[id] = new AdapterDefinition(
                NativeModel: nativeModel,
                SourceProtectedResearch: sourceProtectedResearch,
                Capabilities: capabilities ?? DefaultCapabilities(canResearch: false),
                Executable: executable,
                Availability: availability,
                Execute: execute,
                Environment: environment,
                Terminate: terminate,
                QuotaAsync: quota,
                DiscoverAsync: discover,
                ResultIdentity: resultIdentity
            );
        }
    }

    // providers.cjs facade composition:
    public static string? Executable(JsonObject? provider) => AdapterFor(provider)?.Executable(provider);
    public static string? Executable(string? provider) => AdapterFor(provider)?.Executable(null);

    public static Dictionary<string, string> ProviderEnvironment(
        JsonObject? provider,
        IDictionary<string, string>? inherited = null,
        string? platform = null)
    {
        var adapter = AdapterFor(provider);
        return adapter?.Environment(inherited, platform) ?? ProviderRuntime.ProviderEnvironment("", inherited, platform);
    }

    public static Dictionary<string, string> ProviderEnvironment(
        string? provider,
        IDictionary<string, string>? inherited = null,
        string? platform = null)
    {
        var adapter = AdapterFor(provider);
        return adapter?.Environment(inherited, platform) ?? ProviderRuntime.ProviderEnvironment(provider ?? "", inherited, platform);
    }

    public static LaunchSpecResult LaunchSpec(
        JsonObject model,
        string folder,
        string prompt,
        bool lead = false,
        bool research = false)
    {
        var error = ModelRouting.ModelRouteError(model);
        if (error != null) throw InvalidOp(error);
        var adapter = AdapterFor(model) ?? throw InvalidOp("Unsupported provider adapter");
        return adapter.Execute(model, folder, prompt, lead, research);
    }

    // Provider quota implementations
    private static async Task<JsonObject> ReadClaudeQuotaAsync(JsonObject? provider, CancellationToken ct)
    {
        if (provider != null && provider["settingsFolder"]?.ToString() is string folder && !string.IsNullOrEmpty(folder))
        {
            var env = ProviderRuntime.AccountEnvironment("claude", provider);
            var reader = ClaudeUsage.CreateClaudeUsageReader(
                readAuth: () => ClaudeUsage.ReadClaudeAuthAsync(env),
                fetchImpl: FetchImpl);
            return await reader().ConfigureAwait(false);
        }
        var defaultReader = ClaudeUsage.CreateClaudeUsageReader(fetchImpl: FetchImpl);
        return await defaultReader().ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadCodexQuotaAsync(JsonObject? provider, CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var settingsFolder = provider?["settingsFolder"]?.ToString();
        var dep = new PlanUsage.Dependencies(
            Home: home,
            ActualHome: home,
            ReadJson: path =>
            {
                try { return JsonNode.Parse(File.ReadAllText(path)); } catch { return null; }
            },
            Http: async (req, token) =>
            {
                using var http = new HttpClient();
                using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
                foreach (var (k, v) in req.Headers) msg.Headers.TryAddWithoutValidation(k, v);
                if (!string.IsNullOrEmpty(req.Body))
                {
                    msg.Content = new StringContent(req.Body, System.Text.Encoding.UTF8, "application/json");
                }
                using var res = await http.SendAsync(msg, token).ConfigureAwait(false);
                var text = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return new PlanUsage.Response((int)res.StatusCode, text);
            }
        );
        return await PlanUsage.ReadChatGptAsync(dep, settingsFolder, ct).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadGrokQuotaAsync(CancellationToken ct)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dep = new PlanUsage.Dependencies(
            Home: home,
            ActualHome: home,
            ReadJson: path =>
            {
                try { return JsonNode.Parse(File.ReadAllText(path)); } catch { return null; }
            },
            Http: async (req, token) =>
            {
                using var http = new HttpClient();
                using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Url);
                foreach (var (k, v) in req.Headers) msg.Headers.TryAddWithoutValidation(k, v);
                if (!string.IsNullOrEmpty(req.Body))
                {
                    msg.Content = new StringContent(req.Body, System.Text.Encoding.UTF8, "application/json");
                }
                using var res = await http.SendAsync(msg, token).ConfigureAwait(false);
                var text = await res.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                return new PlanUsage.Response((int)res.StatusCode, text);
            }
        );
        return await PlanUsage.ReadGrokAsync(dep, ct).ConfigureAwait(false);
    }

    private static async Task<JsonObject> ReadAntigravityQuotaAsync(CancellationToken ct)
    {
        var cmd = ProviderRuntime.Executable("antigravity");
        return await Antigravity.ReadAntigravityAsync(cmd, async (c, token) =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo(c.Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = c.WindowsHide
            };
            foreach (var a in c.Arguments) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return new Antigravity.Reply(Failed: true);
            var stdout = await p.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
            await p.WaitForExitAsync(token).ConfigureAwait(false);
            return new Antigravity.Reply(stdout, stderr, p.ExitCode != 0, false);
        }, ct).ConfigureAwait(false);
    }

    private static async Task<JsonObject> DiscoverClaudeAsync(JsonObject? provider, CancellationToken ct)
    {
        var env = provider != null && provider["settingsFolder"] != null
            ? ProviderRuntime.AccountEnvironment("claude", provider)
            : null;
        var (ok, reason, models) = await Claude.ListClaudeAsync(null, env).ConfigureAwait(false);
        if (!ok) return new JsonObject { ["ok"] = false, ["reason"] = reason };
        var arr = new JsonArray();
        if (models != null)
        {
            foreach (var m in models) arr.Add((JsonNode)m.DeepClone());
        }
        return new JsonObject { ["ok"] = true, ["models"] = arr };
    }

    public static async Task<JsonObject> DiscoverCodexAsync(JsonObject? provider, CancellationToken ct)
    {
        var home = provider?["settingsFolder"]?.ToString();
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            }
        }

        var authPath = Path.Combine(home, "auth.json");
        JsonObject? auth = null;
        try
        {
            if (File.Exists(authPath))
            {
                auth = JsonNode.Parse(File.ReadAllText(authPath)) as JsonObject;
            }
        }
        catch
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "Codex authentication unavailable." };
        }

        if (auth == null)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "Codex authentication unavailable." };
        }

        var token = auth["tokens"]?["access_token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(token))
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "Codex authentication unavailable." };
        }

        var accountId = auth["tokens"]?["account_id"]?.GetValue<string>() ?? "";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(10000);

        try
        {
            HttpResponseMessage res;
            if (FetchImpl != null)
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/codex/models?client_version=0.162.1");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
                res = await FetchImpl("https://chatgpt.com/backend-api/codex/models?client_version=0.162.1", req).ConfigureAwait(false);
            }
            else
            {
                using var client = new HttpClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/codex/models?client_version=0.162.1");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
                res = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            }

            using (res)
            {
                if (!res.IsSuccessStatusCode)
                {
                    return new JsonObject { ["ok"] = false, ["reason"] = "Codex model catalog HTTP " + (int)res.StatusCode };
                }
                var text = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var body = JsonNode.Parse(text) as JsonObject;
                if (body == null || body["models"] is not JsonArray modelsArr)
                {
                    return new JsonObject { ["ok"] = false, ["reason"] = "Codex returned an invalid model catalog." };
                }

                var models = new JsonArray();
                foreach (var row in modelsArr)
                {
                    if (row is not JsonObject m) continue;
                    var cli = m["slug"]?.ToString() ?? m["id"]?.ToString();
                    var label = m["display_name"]?.ToString() ?? m["slug"]?.ToString() ?? m["id"]?.ToString();
                    models.Add((JsonNode)new JsonObject
                    {
                        ["cli"] = cli,
                        ["label"] = label,
                        ["capabilities"] = new JsonObject
                        {
                            ["canUseTools"] = true,
                            ["canCode"] = true,
                            ["canOrchestrate"] = true,
                            ["canResearch"] = true
                        }
                    });
                }
                return new JsonObject { ["ok"] = true, ["models"] = models };
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "Codex model discovery timed out." };
        }
        catch (Exception ex)
        {
            return new JsonObject { ["ok"] = false, ["reason"] = ex.Message };
        }
    }

    private static async Task<JsonObject> DiscoverAntigravityAsync(CancellationToken ct)
    {
        var cmd = ProviderRuntime.Executable("antigravity");
        return await Antigravity.ListAntigravityAsync(cmd, async (c, token) =>
        {
            var psi = new System.Diagnostics.ProcessStartInfo(c.Executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = c.WindowsHide
            };
            foreach (var a in c.Arguments) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return new Antigravity.Reply(Failed: true);
            var stdout = await p.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
            var stderr = await p.StandardError.ReadToEndAsync(token).ConfigureAwait(false);
            await p.WaitForExitAsync(token).ConfigureAwait(false);
            return new Antigravity.Reply(stdout, stderr, p.ExitCode != 0, false);
        }, ct).ConfigureAwait(false);
    }
}
