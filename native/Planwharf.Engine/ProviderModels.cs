// Port of engine/provider-models.cjs.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ProviderModels
{
    private static InvalidOperationException InvalidOp(string message) => new(message);
    private const RegexOptions JsOptions = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    private static readonly Regex GrokModelPattern = new(@"^grok-[\w.:-]+$", JsOptions);
    private static readonly Regex ControlCharsPattern = new(@"[\x00-\x1f\x7f]", JsOptions);
    private static readonly Regex PolicyLockPattern = new(@"policy lock file[\s\S]*not accessible", RegexOptions.IgnoreCase | JsOptions);
    private static readonly Regex AnsiPattern = new(@"\x1b\[[0-9;]*m", JsOptions);
    private static readonly Regex GrokIdPattern = new(@"\bgrok-[a-zA-Z0-9][a-zA-Z0-9._:-]*", JsOptions);
    private static readonly Regex Hex64Pattern = new(@"^[a-f0-9]{64}$", JsOptions);

    private static readonly string[] CliTools = ["claude", "codex", "grok", "antigravity"];

    public static Func<string, Task<(int ExitCode, string Stdout, string Stderr)>>? GrokExecutor { get; set; }

    public static async Task<JsonObject> ListGrokAsync(CancellationToken cancellationToken = default)
    {
        var command = ProviderAdapters.Executable("grok") ?? ProviderRuntime.Executable("grok");
        if (string.IsNullOrEmpty(command))
        {
            return new JsonObject { ["ok"] = false, ["reason"] = "Grok CLI executable not found." };
        }

        string stdout;
        string stderr;
        int exitCode;

        if (GrokExecutor != null)
        {
            var res = await GrokExecutor(command).ConfigureAwait(false);
            exitCode = res.ExitCode;
            stdout = res.Stdout;
            stderr = res.Stderr;
        }
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(15000);

                var psi = new ProcessStartInfo(command)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("models");

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    return new JsonObject { ["ok"] = false, ["reason"] = "Grok CLI could not list models for the active login. Check CLI sign-in and refresh models." };
                }

                var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
                var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);

                stdout = await outTask.ConfigureAwait(false);
                stderr = await errTask.ConfigureAwait(false);
                exitCode = proc.ExitCode;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new JsonObject { ["ok"] = false, ["reason"] = "Grok CLI could not list models for the active login. Check CLI sign-in and refresh models." };
            }
            catch
            {
                return new JsonObject { ["ok"] = false, ["reason"] = "Grok CLI could not list models for the active login. Check CLI sign-in and refresh models." };
            }
        }

        if (exitCode != 0)
        {
            var policy = PolicyLockPattern.IsMatch(stdout + stderr);
            var reason = policy
                ? "Grok CLI cannot access its managed policy lock file. Check the CLI permissions and sign-in, then refresh models."
                : "Grok CLI could not list models for the active login. Check CLI sign-in and refresh models.";
            return new JsonObject { ["ok"] = false, ["reason"] = reason };
        }

        var clean = AnsiPattern.Replace(stdout, "");
        var matches = GrokIdPattern.Matches(clean);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new JsonArray();
        foreach (Match m in matches)
        {
            if (seen.Add(m.Value)) ids.Add((JsonNode)m.Value);
        }

        if (ids.Count > 0)
        {
            return new JsonObject { ["ok"] = true, ["ids"] = ids };
        }

        return new JsonObject
        {
            ["ok"] = false,
            ["reason"] = "Grok CLI returned no exact model IDs for the active login."
        };
    }

    public static JsonObject ListGrok() => ListGrokAsync().GetAwaiter().GetResult();

    public static string CacheKey(JsonObject provider)
    {
        var id = provider["id"]?.ToString();
        var adapter = provider["adapter"]?.ToString();
        var accountKey = provider["account"]?["key"]?.ToString();
        var config = ProviderCatalog.ProviderConfiguration(provider);
        var arr = new JsonArray
        {
            (JsonNode?)id,
            (JsonNode?)adapter,
            accountKey != null ? (JsonNode?)accountKey : null,
            (JsonNode)config
        };
        var json = arr.ToJsonString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(hash);
    }

    public static List<JsonObject>? CatalogEntries(JsonObject result)
    {
        JsonArray? rows = null;
        if (result["models"] is JsonArray mArr)
        {
            rows = mArr;
        }
        else if (result["ids"] is JsonArray idsArr)
        {
            rows = new JsonArray();
            foreach (var item in idsArr)
            {
                if (item is JsonValue jv && jv.TryGetValue<string>(out var cli))
                {
                    rows.Add((JsonNode)new JsonObject { ["cli"] = cli });
                }
                else
                {
                    rows.Add((JsonNode?)null);
                }
            }
        }

        if (rows == null) return null;

        var entries = new List<JsonObject>();
        foreach (var r in rows)
        {
            if (r is not JsonObject m) return null;
            var cliNode = m["cli"];
            if (cliNode is not JsonValue jv || !jv.TryGetValue<string>(out var cli) || string.IsNullOrEmpty(cli))
                return null;
            if (cli.Trim() != cli || ControlCharsPattern.IsMatch(cli))
                return null;

            var entry = new JsonObject { ["cli"] = cli };
            if (m.ContainsKey("label") && m["label"] is JsonValue lv && lv.TryGetValue<string>(out var label))
            {
                entry["label"] = label;
            }
            if (m.ContainsKey("orchestratorEligible") && m["orchestratorEligible"] is JsonValue oe && oe.TryGetValue<bool>(out var oeb))
            {
                entry["orchestratorEligible"] = oeb;
            }
            if (m.ContainsKey("workerEligible") && m["workerEligible"] is JsonValue we && we.TryGetValue<bool>(out var web))
            {
                entry["workerEligible"] = web;
            }

            var caps = new JsonObject();
            if (m["capabilities"] is JsonObject cObj)
            {
                foreach (var (k, v) in cObj)
                {
                    if (v is JsonValue vVal && vVal.TryGetValue<bool>(out var bVal))
                    {
                        caps[k] = bVal;
                    }
                }
            }
            entry["capabilities"] = caps;
            entries.Add(entry);
        }

        return entries;
    }

    public interface IProviderModelsInstance
    {
        Task<JsonObject> RefreshAsync(bool force = false, string? providerId = null);
        JsonObject Snapshot();
        Task InvalidateAsync(string providerId);
        void ObserveScope(string? providerId = null);
    }

    public sealed class ProviderModelsOptions
    {
        public Func<JsonObject?, Task<JsonObject>>? Discover { get; set; }
        public Func<Task<JsonObject>>? LegacyDiscover { get; set; }
        public IReadOnlyList<JsonObject>? Providers { get; set; }
        public Func<long>? Now { get; set; }
        public int Ttl { get; set; } = 60000;
        public string? CacheFile { get; set; }
        public Func<string, bool>? FindExecutable { get; set; }
    }

    public static IProviderModelsInstance CreateProviderModels(
        IList<JsonObject> models,
        ProviderModelsOptions? options = null)
    {
        options ??= new ProviderModelsOptions();
        var now = options.Now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var ttl = options.Ttl;
        var cacheFile = options.CacheFile;
        var providers = options.Providers;

        Func<string, bool>? lookup = options.FindExecutable;
        if (lookup == null && options.Discover == null && options.LegacyDiscover == null)
        {
            lookup = adapter => ProviderRuntime.Executable(adapter) != null;
        }

        if (providers != null)
        {
            return new CatalogDiscoveryInstance(models, providers, options.Discover, now, ttl, cacheFile, lookup);
        }

        Func<Task<JsonObject>> legacyDiscover;
        if (options.LegacyDiscover != null) legacyDiscover = options.LegacyDiscover;
        else if (options.Discover != null) legacyDiscover = () => options.Discover(null);
        else legacyDiscover = () => ListGrokAsync(CancellationToken.None);
        return new LegacyGrokDiscoveryInstance(models, legacyDiscover, now, ttl);
    }

    private sealed class LegacyGrokDiscoveryInstance : IProviderModelsInstance
    {
        private readonly IList<JsonObject> _models;
        private readonly Func<Task<JsonObject>> _discover;
        private readonly Func<long> _now;
        private readonly int _ttl;

        private JsonObject _state;
        private Task<JsonObject>? _inflight;
        private readonly object _lock = new();

        public LegacyGrokDiscoveryInstance(
            IList<JsonObject> models,
            Func<Task<JsonObject>> discover,
            Func<long> now,
            int ttl)
        {
            _models = models;
            _discover = discover;
            _now = now;
            _ttl = ttl;
            _state = new JsonObject
            {
                ["status"] = "unknown",
                ["checkedAt"] = null,
                ["reason"] = null,
                ["ids"] = new JsonArray()
            };
        }

        public Task<JsonObject> RefreshAsync(bool force = false, string? providerId = null)
        {
            lock (_lock)
            {
                if (_inflight != null) return _inflight;

                if (!force && _state["checkedAt"] != null &&
                    DateTimeOffset.TryParse(_state["checkedAt"]!.ToString(), out var dt))
                {
                    if (_now() - dt.ToUnixTimeMilliseconds() < _ttl)
                    {
                        return Task.FromResult((JsonObject)_state.DeepClone());
                    }
                }

                _inflight = DoRefreshAsync();
                return _inflight;
            }
        }

        private async Task<JsonObject> DoRefreshAsync()
        {
            await Task.Yield();
            try
            {
                JsonObject result;
                try
                {
                    result = await _discover().ConfigureAwait(false);
                }
                catch
                {
                    result = new JsonObject
                    {
                        ["ok"] = false,
                        ["reason"] = "Grok model discovery failed. Check CLI sign-in and refresh models."
                    };
                }

                var ids = new List<string>();
                var ok = result["ok"]?.GetValue<bool>() == true;
                if (ok && result["ids"] is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is JsonValue jv && jv.TryGetValue<string>(out var s) && GrokModelPattern.IsMatch(s))
                        {
                            ids.Add(s);
                        }
                    }
                }

                foreach (var model in _models)
                {
                    if (model["provider"]?.ToString() == "grok")
                    {
                        model["availabilityVerified"] = ok;
                        model["availabilityWarning"] = ok ? null : result["reason"]?.DeepClone();
                        if (ok)
                        {
                            var cli = model["cli"]?.ToString() ?? "";
                            var avail = ids.Contains(cli);
                            model["available"] = avail;
                            model["unavailableReason"] = avail ? null : $"The active Grok login does not list the exact model {cli}. No substitute was selected.";
                        }
                    }
                }

                var idsArray = new JsonArray();
                if (ok)
                {
                    foreach (var id in ids) idsArray.Add((JsonNode)id);
                }
                else if (_state["ids"] is JsonArray prevIds)
                {
                    foreach (var id in prevIds) idsArray.Add(id?.DeepClone());
                }

                var checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(_now()).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                _state = new JsonObject
                {
                    ["status"] = ok ? "current" : "unread",
                    ["checkedAt"] = checkedAt,
                    ["reason"] = ok ? null : result["reason"]?.DeepClone(),
                    ["ids"] = idsArray,
                    ["preservingPreviousAvailability"] = !ok
                };

                return (JsonObject)_state.DeepClone();
            }
            finally
            {
                lock (_lock)
                {
                    _inflight = null;
                }
            }
        }

        public JsonObject Snapshot()
        {
            lock (_lock)
            {
                var clone = (JsonObject)_state.DeepClone();
                clone["checking"] = _inflight != null;
                return clone;
            }
        }

        public Task InvalidateAsync(string providerId) => Task.CompletedTask;
        public void ObserveScope(string? providerId = null) { }
    }

    private sealed class CatalogDiscoveryInstance : IProviderModelsInstance
    {
        private readonly IList<JsonObject> _models;
        private readonly IReadOnlyList<JsonObject> _providers;
        private readonly Func<JsonObject?, Task<JsonObject>>? _discover;
        private readonly Func<long> _now;
        private readonly int _ttl;
        private readonly string? _cacheFile;
        private readonly Func<string, bool>? _lookup;

        private readonly Dictionary<string, JsonObject> _states = new(StringComparer.Ordinal);
        private readonly HashSet<string> _restored = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _stateScopes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _generations = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _observedScopes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _blockedScopes = new(StringComparer.Ordinal);

        private Dictionary<string, JsonObject> _cache = new(StringComparer.Ordinal);
        private Task<JsonObject>? _inflight;
        private Task _saving = Task.CompletedTask;
        private readonly object _stateLock = new();
        private readonly object _saveLock = new();

        public CatalogDiscoveryInstance(
            IList<JsonObject> models,
            IReadOnlyList<JsonObject> providers,
            Func<JsonObject?, Task<JsonObject>>? discover,
            Func<long> now,
            int ttl,
            string? cacheFile,
            Func<string, bool>? lookup)
        {
            _models = models;
            _providers = providers;
            _discover = discover;
            _now = now;
            _ttl = ttl;
            _cacheFile = cacheFile;
            _lookup = lookup;

            LoadCache();

            foreach (var p in _providers)
            {
                var id = p["id"]?.ToString() ?? "";
                _observedScopes[id] = CacheKey(p);
            }
        }

        private void LoadCache()
        {
            if (string.IsNullOrEmpty(_cacheFile)) return;
            try
            {
                if (File.Exists(_cacheFile))
                {
                    var saved = JsonNode.Parse(File.ReadAllText(_cacheFile)) as JsonObject;
                    if (saved != null && saved["schema"]?.GetValue<int>() == 1 && saved["catalogs"] is JsonObject cats)
                    {
                        foreach (var (k, v) in cats)
                        {
                            if (v is JsonObject catObj) _cache[k] = catObj;
                        }
                        if (saved["invalidatedScopes"] is JsonArray inv)
                        {
                            foreach (var item in inv)
                            {
                                if (item is JsonValue jv && jv.TryGetValue<string>(out var key) && Hex64Pattern.IsMatch(key))
                                {
                                    _blockedScopes.Add(key);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private Task SaveCacheAsync(bool strict = false)
        {
            if (string.IsNullOrEmpty(_cacheFile)) return Task.CompletedTask;

            lock (_saveLock)
            {
                var pending = _saving.ContinueWith(async _ =>
                {
                    var temporary = _cacheFile + "." + Guid.NewGuid().ToString("D") + ".tmp";
                    try
                    {
                        var dir = Path.GetDirectoryName(_cacheFile);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        var blockedArr = new JsonArray();
                        lock (_stateLock)
                        {
                            foreach (var s in _blockedScopes) blockedArr.Add((JsonNode)s);
                        }

                        var catalogsObj = new JsonObject();
                        lock (_stateLock)
                        {
                            foreach (var (k, v) in _cache) catalogsObj[k] = (JsonObject)v.DeepClone();
                        }

                        var root = new JsonObject
                        {
                            ["schema"] = 1,
                            ["catalogs"] = catalogsObj,
                            ["invalidatedScopes"] = blockedArr
                        };

                        var json = root.ToJsonString();
                        using (var fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (var writer = new StreamWriter(fs, Encoding.UTF8))
                        {
                            await writer.WriteAsync(json).ConfigureAwait(false);
                        }

                        File.Move(temporary, _cacheFile, overwrite: true);
                    }
                    catch
                    {
                        if (strict)
                        {
                            throw InvalidOp("Provider access invalidation could not be persisted. Access remains blocked; retry before accepting the account change.");
                        }
                    }
                    finally
                    {
                        try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                    }
                }, TaskScheduler.Default).Unwrap();

                _saving = pending.ContinueWith(_ => { }, TaskScheduler.Default);
                return pending;
            }
        }

        private void Block(JsonObject provider)
        {
            var key = CacheKey(provider);
            var id = provider["id"]?.ToString() ?? "";
            _blockedScopes.Add(key);
            _observedScopes[id] = key;
            _generations[id] = (_generations.TryGetValue(id, out var g) ? g : 0) + 1;
            _restored.Remove(key);
            _states.Remove(id);
            _stateScopes.Remove(id);

            foreach (var model in _models)
            {
                if (model["providerId"]?.ToString() == id)
                {
                    model["available"] = false;
                    model["availabilityVerified"] = false;
                    model["unavailableReason"] = "Provider account or configuration changed. Access requires fresh discovery for the current scope.";
                    model["availabilityWarning"] = model["unavailableReason"]?.DeepClone();
                }
            }
        }

        public async Task InvalidateAsync(string providerId)
        {
            lock (_stateLock)
            {
                foreach (var provider in _providers)
                {
                    if (provider["id"]?.ToString() == providerId)
                    {
                        Block(provider);
                    }
                }
            }
            await SaveCacheAsync(strict: true).ConfigureAwait(false);
        }

        public void ObserveScope(string? providerId = null) => EnsureScopes(providerId);

        private void EnsureScopes(string? providerId = null)
        {
            var changed = false;
            lock (_stateLock)
            {
                foreach (var provider in _providers)
                {
                    var id = provider["id"]?.ToString() ?? "";
                    if (providerId != null && id != providerId) continue;

                    var key = CacheKey(provider);
                    if (!_observedScopes.TryGetValue(id, out var obsKey) || obsKey != key)
                    {
                        if (_states.ContainsKey(id) || _generations.ContainsKey(id))
                        {
                            Block(provider);
                            changed = true;
                        }
                        else
                        {
                            _observedScopes[id] = key;
                        }
                    }

                    if (_blockedScopes.Contains(key))
                    {
                        foreach (var model in _models)
                        {
                            if (model["providerId"]?.ToString() == id)
                            {
                                model["available"] = false;
                                model["availabilityVerified"] = false;
                                model["unavailableReason"] = "Access requires fresh discovery for the current provider account or configuration.";
                            }
                        }
                    }
                }
            }
            if (changed) _ = SaveCacheAsync();
        }

        private void ApplyCatalog(JsonObject provider, List<JsonObject> entries, string checkedAt, bool verified)
        {
            var id = provider["id"]?.ToString() ?? "";
            var label = provider["label"]?.ToString() ?? id;
            var adapter = provider["adapter"]?.ToString() ?? "";

            foreach (var model in _models)
            {
                if (model["providerId"]?.ToString() != id) continue;
                var cli = model["cli"]?.ToString() ?? "";
                var match = entries.FirstOrDefault(e => e["cli"]?.ToString() == cli);
                var avail = match != null;
                model["available"] = avail;

                var named = entries.FirstOrDefault(e => e["cli"]?.ToString() == cli && !string.IsNullOrEmpty(e["label"]?.ToString()));
                if (named != null)
                {
                    var curLabel = model["label"]?.ToString();
                    if (string.IsNullOrEmpty(curLabel) || curLabel == cli)
                    {
                        model["label"] = named["label"]?.ToString();
                    }
                }

                model["notInstalled"] = false;
                model["catalogExcluded"] = !avail;
                model["unavailableReason"] = avail ? null : $"The {label} catalog does not list exact model {cli}. Configuration retained; no substitute selected.";
            }

            foreach (var entry in entries)
            {
                var entryCli = entry["cli"]?.ToString() ?? "";
                if (_models.Any(m => m["providerId"]?.ToString() == id && m["cli"]?.ToString() == entryCli))
                    continue;

                var newModel = new JsonObject();
                foreach (var (k, v) in entry) newModel[k] = v?.DeepClone();

                newModel["id"] = id + ":" + entryCli;
                newModel["providerId"] = id;
                newModel["provider"] = id;
                newModel["adapter"] = adapter;

                if (provider["settingsFolder"] != null)
                {
                    newModel["settingsFolder"] = provider["settingsFolder"]?.DeepClone();
                    newModel["companyId"] = provider["companyId"]?.DeepClone();
                    var qPool = provider["quotaPoolId"]?.ToString() ?? "";
                    newModel["companyPoolId"] = qPool.Split('@')[0];
                }

                var quotaPoolId = provider["quotaPoolId"]?.ToString();
                if (adapter == "antigravity")
                {
                    var poolNode = Antigravity.PoolOf(entryCli, quotaPoolId);
                    newModel["quotaPoolId"] = poolNode?.DeepClone();
                    newModel["plan"] = poolNode?.DeepClone();
                }
                else
                {
                    newModel["quotaPoolId"] = quotaPoolId;
                    newModel["plan"] = quotaPoolId;
                }

                newModel["label"] = entry["label"]?.ToString() ?? entryCli;
                newModel["enabled"] = false;
                newModel["available"] = true;
                newModel["catalogExcluded"] = false;
                newModel["availabilityVerified"] = verified;
                newModel["availabilityCheckedAt"] = checkedAt;
                newModel["availabilityWarning"] = null;
                newModel["providerCatalog"] = true;

                bool orch = true;
                if (entry["orchestratorEligible"] is JsonValue oVal && oVal.TryGetValue<bool>(out var ob)) orch = ob;
                else if (entry["capabilities"]?["canOrchestrate"] is JsonValue cVal && cVal.TryGetValue<bool>(out var cb)) orch = cb;
                newModel["orchestratorEligible"] = orch;

                bool worker = true;
                if (entry["workerEligible"] is JsonValue wVal && wVal.TryGetValue<bool>(out var wb)) worker = wb;
                else if (entry["capabilities"]?["canCode"] is JsonValue cwVal && cwVal.TryGetValue<bool>(out var cwb)) worker = cwb;
                newModel["workerEligible"] = worker;

                newModel["capabilities"] = entry["capabilities"]?.DeepClone() ?? new JsonObject();
                newModel["providerConfig"] = ProviderCatalog.ProviderConfiguration(provider);

                _models.Add(newModel);
            }
        }

        public Task<JsonObject> RefreshAsync(bool force = false, string? providerId = null)
        {
            EnsureScopes();
            lock (_stateLock)
            {
                if (_inflight != null)
                {
                    return _inflight.ContinueWith(_ => RefreshAsync(force, providerId), TaskScheduler.Default).Unwrap();
                }

                _inflight = DoRefreshAsync(force, providerId);
                return _inflight;
            }
        }

        private async Task<JsonObject> DoRefreshAsync(bool force, string? providerId)
        {
            await Task.Yield();
            try
            {
                var activeProviders = _providers
                    .Where(p => (providerId == null || p["id"]?.ToString() == providerId) && p["enabled"]?.GetValue<bool>() != false)
                    .ToList();

                var tasks = activeProviders.Select(async provider =>
                {
                    var id = provider["id"]?.ToString() ?? "";
                    var label = provider["label"]?.ToString() ?? id;
                    var adapter = provider["adapter"]?.ToString() ?? "";
                    var key = CacheKey(provider);

                    Task scopeSave = Task.CompletedTask;
                    lock (_stateLock)
                    {
                        if (!_observedScopes.TryGetValue(id, out var obs) || obs != key)
                        {
                            Block(provider);
                            scopeSave = SaveCacheAsync();
                        }
                    }
                    await scopeSave.ConfigureAwait(false);

                    int generation;
                    lock (_stateLock)
                    {
                        generation = _generations.TryGetValue(id, out var g) ? g : 0;
                        if (_blockedScopes.Contains(key))
                        {
                            foreach (var m in _models)
                            {
                                if (m["providerId"]?.ToString() == id)
                                {
                                    m["available"] = false;
                                    m["availabilityVerified"] = false;
                                    m["unavailableReason"] = "Access requires fresh discovery for the current provider account or configuration.";
                                }
                            }
                        }

                        if (!_restored.Contains(key))
                        {
                            _restored.Add(key);
                            if (_cache.TryGetValue(key, out var saved) && !_blockedScopes.Contains(key))
                            {
                                var restoredEntries = CatalogEntries(saved);
                                var chk = saved["checkedAt"]?.ToString();
                                if (restoredEntries != null && !string.IsNullOrEmpty(chk))
                                {
                                    ApplyCatalog(provider, restoredEntries, chk, false);
                                    var idsArr = new JsonArray();
                                    foreach (var e in restoredEntries) idsArr.Add((JsonNode)(e["cli"]?.ToString() ?? ""));
                                    _states[id] = new JsonObject
                                    {
                                        ["providerId"] = id,
                                        ["label"] = label,
                                        ["status"] = "unread",
                                        ["checkedAt"] = chk,
                                        ["reason"] = "Using the last verified catalog until startup discovery completes.",
                                        ["ids"] = idsArr,
                                        ["preservingPreviousAvailability"] = true
                                    };
                                    _stateScopes[id] = key;
                                }
                            }
                        }
                    }

                    if (_lookup != null && CliTools.Contains(adapter) && !_lookup(adapter))
                    {
                        var reason = ProviderCatalog.NotInstalledReason(provider);
                        lock (_stateLock)
                        {
                            foreach (var m in _models)
                            {
                                if (m["providerId"]?.ToString() == id)
                                {
                                    m["available"] = false;
                                    m["notInstalled"] = true;
                                    m["availabilityVerified"] = false;
                                    m["unavailableReason"] = reason;
                                    m["availabilityWarning"] = reason;
                                }
                            }
                            var chkAt = DateTimeOffset.FromUnixTimeMilliseconds(_now()).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                            _states[id] = new JsonObject
                            {
                                ["providerId"] = id,
                                ["label"] = label,
                                ["status"] = "not-installed",
                                ["checkedAt"] = chkAt,
                                ["reason"] = reason,
                                ["ids"] = new JsonArray(),
                                ["preservingPreviousAvailability"] = false
                            };
                            _stateScopes[id] = key;
                        }
                        return;
                    }

                    JsonObject? previous = null;
                    lock (_stateLock)
                    {
                        if (_stateScopes.TryGetValue(id, out var prevScope) && prevScope == key)
                        {
                            _states.TryGetValue(id, out previous);
                        }
                    }

                    if (!force && previous != null && previous["status"]?.ToString() != "not-installed")
                    {
                        var prevCheckedAt = previous["checkedAt"]?.ToString();
                        if (!string.IsNullOrEmpty(prevCheckedAt) && DateTimeOffset.TryParse(prevCheckedAt, out var pDt))
                        {
                            if (_now() - pDt.ToUnixTimeMilliseconds() < _ttl) return;
                        }
                    }

                    JsonObject result;
                    try
                    {
                        if (_discover != null)
                        {
                            result = await _discover(provider).ConfigureAwait(false);
                        }
                        else
                        {
                            var ad = ProviderAdapters.AdapterFor(provider);
                            result = ad?.DiscoverAsync != null
                                ? await ad.DiscoverAsync(provider, CancellationToken.None).ConfigureAwait(false)
                                : new JsonObject { ["ok"] = false, ["reason"] = "Model enumeration unavailable for this adapter." };
                        }
                    }
                    catch
                    {
                        result = new JsonObject
                        {
                            ["ok"] = false,
                            ["reason"] = "Provider catalog could not be read; previous model configuration is preserved."
                        };
                    }

                    Task mismatchSave = Task.CompletedTask;
                    lock (_stateLock)
                    {
                        if (CacheKey(provider) != key || (_generations.TryGetValue(id, out var curG) ? curG : 0) != generation)
                        {
                            if (CacheKey(provider) != (_observedScopes.TryGetValue(id, out var curObs) ? curObs : null))
                            {
                                Block(provider);
                                mismatchSave = SaveCacheAsync();
                            }
                        }
                    }
                    await mismatchSave.ConfigureAwait(false);
                    lock (_stateLock)
                    {
                        if (CacheKey(provider) != key || (_generations.TryGetValue(id, out var curG2) ? curG2 : 0) != generation)
                        {
                            return;
                        }
                    }

                    var ok = result["ok"]?.GetValue<bool>() == true;
                    var entries = CatalogEntries(result);
                    if (ok && entries == null)
                    {
                        result = new JsonObject
                        {
                            ["ok"] = false,
                            ["reason"] = "Provider returned an invalid model catalog; previous availability is retained."
                        };
                        ok = false;
                    }

                    var checkedAt = DateTimeOffset.FromUnixTimeMilliseconds(_now()).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

                    Task saveTask = Task.CompletedTask;
                    lock (_stateLock)
                    {
                        if (ok && entries != null)
                        {
                            ApplyCatalog(provider, entries, checkedAt, true);
                        }

                        foreach (var m in _models)
                        {
                            if (m["providerId"]?.ToString() == id)
                            {
                                m["availabilityVerified"] = ok;
                                m["availabilityCheckedAt"] = checkedAt;
                                m["availabilityWarning"] = ok ? null : result["reason"]?.DeepClone() ?? (JsonNode)"Model catalog check failed; previous availability is retained.";
                            }
                        }

                        if (ok && entries != null)
                        {
                            _blockedScopes.Remove(key);
                            var modelEntriesArr = new JsonArray();
                            foreach (var e in entries) modelEntriesArr.Add((JsonNode)(JsonObject)e.DeepClone());
                            _cache[key] = new JsonObject
                            {
                                ["checkedAt"] = checkedAt,
                                ["models"] = modelEntriesArr
                            };
                            saveTask = SaveCacheAsync();
                        }

                        if (CacheKey(provider) != key || (_generations.TryGetValue(id, out var gen2) ? gen2 : 0) != generation)
                        {
                            EnsureScopes();
                            return;
                        }

                        var idsArr = new JsonArray();
                        if (ok && entries != null)
                        {
                            foreach (var e in entries) idsArr.Add((JsonNode)(e["cli"]?.ToString() ?? ""));
                        }
                        else if (previous?["ids"] is JsonArray prevIds)
                        {
                            foreach (var item in prevIds) idsArr.Add(item?.DeepClone());
                        }

                        _states[id] = new JsonObject
                        {
                            ["providerId"] = id,
                            ["label"] = label,
                            ["status"] = ok ? "current" : "unread",
                            ["checkedAt"] = checkedAt,
                            ["reason"] = ok ? null : result["reason"]?.DeepClone(),
                            ["preservingPreviousAvailability"] = !ok && !_blockedScopes.Contains(key),
                            ["ids"] = idsArr
                        };
                        _stateScopes[id] = key;
                    }
                    await saveTask.ConfigureAwait(false);
                }).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
                return Snapshot();
            }
            finally
            {
                lock (_stateLock)
                {
                    _inflight = null;
                }
            }
        }

        public JsonObject Snapshot()
        {
            EnsureScopes();
            lock (_stateLock)
            {
                var list = new JsonArray();
                foreach (var p in _providers)
                {
                    var id = p["id"]?.ToString() ?? "";
                    var label = p["label"]?.ToString() ?? id;
                    if (_states.TryGetValue(id, out var state))
                    {
                        list.Add((JsonNode)(JsonObject)state.DeepClone());
                    }
                    else
                    {
                        var isDis = p["enabled"]?.GetValue<bool>() == false;
                        list.Add((JsonNode)new JsonObject
                        {
                            ["providerId"] = id,
                            ["label"] = label,
                            ["status"] = isDis ? "disabled" : "unknown",
                            ["checkedAt"] = null,
                            ["ids"] = new JsonArray()
                        });
                    }
                }

                bool allGood = true;
                foreach (var item in list)
                {
                    var st = item?["status"]?.ToString();
                    if (st != "current" && st != "disabled" && st != "not-installed")
                    {
                        allGood = false;
                        break;
                    }
                }

                return new JsonObject
                {
                    ["providers"] = list,
                    ["checking"] = _inflight != null,
                    ["status"] = allGood ? "current" : "unread"
                };
            }
        }
    }
}
