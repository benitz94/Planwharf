// Port of engine/provider-catalog.cjs.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public record CatalogResult(IReadOnlyList<JsonObject> Providers, IReadOnlyList<JsonObject> Models);

public static class ProviderCatalog
{
    private static readonly Dictionary<string, (string Name, string Pool)> Defaults = new(StringComparer.Ordinal)
    {
        ["codex"] = ("ChatGPT", "ChatGPT"),
        ["grok"] = ("Grok", "Grok"),
        ["antigravity"] = ("Gemini", "Gemini"),
        ["claude"] = ("Claude Code", "Claude")
    };

    public static readonly Dictionary<string, string> InstallHints = new(StringComparer.Ordinal)
    {
        ["claude"] = "Install it with npm install -g @anthropic-ai/claude-code, then run claude once to sign in.",
        ["codex"] = "Install it with npm install -g @openai/codex, then run codex once to sign in.",
        ["grok"] = "Install the Grok CLI and sign in, then look for models again.",
        ["antigravity"] = "Install Antigravity and sign in, then look for models again."
    };

    private static readonly Dictionary<string, string> ToolNames = new(StringComparer.Ordinal)
    {
        ["claude"] = "Claude Code",
        ["codex"] = "Codex",
        ["grok"] = "The Grok CLI",
        ["antigravity"] = "Antigravity"
    };

    private static readonly string[] ConfigKeys =
    [
        "baseUrl", "authEnv", "subscriptionIncluded", "apiProtocol",
        "maxOutputTokens", "maxTurns", "requestTimeoutMs", "cliCommand",
        "cliArgs", "yoloArgs", "restrictedArgs", "outputProtocol"
    ];

    public static JsonObject ProviderConfiguration(JsonObject provider)
    {
        var result = new JsonObject();
        foreach (var key in ConfigKeys)
        {
            if (provider.ContainsKey(key) && provider[key] != null)
                result[key] = provider[key]?.DeepClone();
        }
        return result;
    }

    public static string NotInstalledReason(JsonObject provider)
    {
        var adapter = provider["adapter"]?.GetValue<string>() ?? "";
        var label = provider["label"]?.GetValue<string>() ?? provider["id"]?.GetValue<string>() ?? "";
        var toolName = ToolNames.TryGetValue(adapter, out var tn) ? tn : (!string.IsNullOrEmpty(label) ? label : provider["id"]?.GetValue<string>() ?? "");
        var hint = InstallHints.TryGetValue(adapter, out var ih) ? ih : "Install it and sign in, then look for models again.";
        return $"{toolName} is not installed on this computer. {hint}";
    }

    public static CatalogResult LoadCatalog(JsonNode? config)
    {
        bool explicitCatalog = config is JsonObject;
        var providersList = new List<JsonObject>();

        if (explicitCatalog)
        {
            var configObj = (JsonObject)config!;
            if (configObj["schema"]?.GetValue<int>() != 3 || configObj["providers"] is not JsonArray pArr)
                throw new InvalidOperationException("Invalid provider catalog: expected schema 3 providers.");

            foreach (var p in pArr)
            {
                if (p is JsonObject pObj)
                {
                    var clonedP = (JsonObject)pObj.DeepClone();
                    var pModels = (clonedP["models"] as JsonArray)?.OfType<JsonObject>().Select(m => (JsonObject)m.DeepClone()).ToList() ?? new List<JsonObject>();
                    clonedP["models"] = new JsonArray(pModels.Select(m => (JsonNode)m).ToArray());
                    providersList.Add(clonedP);
                }
            }
        }
        else if (config is JsonArray mArr)
        {
            foreach (var mNode in mArr)
            {
                if (mNode is not JsonObject model) continue;
                var id = model["providerId"]?.GetValue<string>() ?? model["provider"]?.GetValue<string>() ?? model["plan"]?.GetValue<string>() ?? "legacy";
                var provider = providersList.FirstOrDefault(p => p["id"]?.GetValue<string>() == id);
                if (provider == null)
                {
                    var prov = model["provider"]?.GetValue<string>() ?? "";
                    var (fallbackLabel, fallbackPool) = Defaults.TryGetValue(prov, out var def) ? def : (model["plan"]?.GetValue<string>() ?? id, model["plan"]?.GetValue<string>() ?? id);
                    provider = new JsonObject
                    {
                        ["id"] = id,
                        ["label"] = fallbackLabel,
                        ["quotaPoolId"] = model["quotaPoolId"]?.GetValue<string>() ?? fallbackPool,
                        ["adapter"] = model["adapter"]?.GetValue<string>() ?? model["provider"]?.GetValue<string>(),
                        ["enabled"] = true,
                        ["models"] = new JsonArray()
                    };
                    providersList.Add(provider);
                }
                ((JsonArray)provider["models"]!).Add((JsonNode)model.DeepClone());
            }
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var modelIds = new HashSet<string>(StringComparer.Ordinal);
        var models = new List<JsonObject>();

        foreach (var provider in providersList)
        {
            var pId = provider["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pId) || !ids.Add(pId))
                throw new InvalidOperationException("Provider IDs must be nonempty and unique.");

            if (string.IsNullOrEmpty(provider["label"]?.GetValue<string>()))
                provider["label"] = pId;
            if (string.IsNullOrEmpty(provider["quotaPoolId"]?.GetValue<string>()))
                provider["quotaPoolId"] = pId;
            if (!provider.ContainsKey("enabled") || provider["enabled"] == null)
                provider["enabled"] = true;

            var pModels = (provider["models"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
            foreach (var original in pModels)
            {
                var mId = original["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(mId) || !modelIds.Add(mId))
                    throw new InvalidOperationException("Model IDs must be nonempty and unique.");

                var model = (JsonObject)original.DeepClone();
                model["providerId"] = pId;
                if (explicitCatalog || original.ContainsKey("provider"))
                    model["provider"] = pId;
                model["adapter"] = provider["adapter"]?.GetValue<string>();

                var adapter = provider["adapter"]?.GetValue<string>();
                var pool = provider["quotaPoolId"]?.GetValue<string>();
                model["quotaPoolId"] = pool;
                model["plan"] = pool;

                var origCaps = original["capabilities"] as JsonObject;
                bool orchElig = original["orchestratorEligible"]?.GetValue<bool>() ??
                               origCaps?["orchestratorEligible"]?.GetValue<bool>() ??
                               origCaps?["canOrchestrate"]?.GetValue<bool>() ??
                               (original["kind"]?.GetValue<string>() != "local");

                bool workerElig = original["workerEligible"]?.GetValue<bool>() ??
                                 origCaps?["workerEligible"]?.GetValue<bool>() ?? true;

                bool revElig = original["reviewerEligible"]?.GetValue<bool>() ??
                              origCaps?["reviewerEligible"]?.GetValue<bool>() ??
                              (original["workerEligible"]?.GetValue<bool>() != false);

                model["orchestratorEligible"] = orchElig;
                model["workerEligible"] = workerElig;
                model["reviewerEligible"] = revElig;
                model["providerCatalog"] = explicitCatalog || original["providerCatalog"]?.GetValue<bool>() == true;

                var caps = origCaps != null ? (JsonObject)origCaps.DeepClone() : new JsonObject();
                caps["orchestratorEligible"] = orchElig;
                caps["workerEligible"] = workerElig;
                caps["reviewerEligible"] = revElig;
                model["capabilities"] = caps;

                model["providerConfig"] = ProviderConfiguration(provider);

                if (explicitCatalog)
                {
                    if (string.IsNullOrEmpty(model["kind"]?.GetValue<string>()))
                        model["kind"] = "default";
                    if (model["cost"] == null)
                        model["cost"] = 1;
                }

                models.Add(model);
            }

            provider["models"] = new JsonArray(models.Where(m => m["providerId"]?.GetValue<string>() == pId).Select(m => (JsonNode)m.DeepClone()).ToArray());
            provider["source"] = provider["adapter"]?.GetValue<string>();
        }

        return new CatalogResult(providersList, models);
    }

    public static IReadOnlyList<JsonObject> ProviderViews(IReadOnlyList<JsonObject> providers, IReadOnlyList<JsonObject> models, IReadOnlyList<JsonObject>? plans = null, JsonObject? settings = null)
    {
        var planList = plans ?? Array.Empty<JsonObject>();
        var result = new List<JsonObject>();

        foreach (var provider in providers)
        {
            var pId = provider["id"]?.GetValue<string>() ?? "";
            var rows = models.Where(m => m["providerId"]?.GetValue<string>() == pId || (string.IsNullOrEmpty(m["providerId"]?.GetValue<string>()) && m["provider"]?.GetValue<string>() == pId)).ToList();
            var poolId = provider["quotaPoolId"]?.GetValue<string>();
            var pool = planList.FirstOrDefault(p => p["id"]?.GetValue<string>() == poolId);

            double? remaining = null;
            if (pool != null && pool["readable"]?.GetValue<bool>() == true && double.TryParse(pool["leftPct"]?.ToString(), out var lp) && double.IsFinite(lp))
            {
                remaining = Math.Max(0, Math.Min(100, lp));
            }

            bool available = provider["available"]?.GetValue<bool>() != false && rows.Any(m => m["available"]?.GetValue<bool>() != false);

            bool enabled = provider["enabled"]?.GetValue<bool>() != false;
            if (settings?["providerEnabled"] is JsonObject pe && pe.ContainsKey(pId) && pe[pId] != null)
                enabled = pe[pId]!.GetValue<bool>();

            bool installed = !rows.Any(m => m["notInstalled"]?.GetValue<bool>() == true);

            var view = (JsonObject)provider.DeepClone();
            view["enabled"] = enabled;
            view["available"] = available;
            view["installed"] = installed;
            view["installHint"] = installed ? null : NotInstalledReason(provider);

            string availability = !installed ? "NOT_INSTALLED" :
                                  !available ? "UNAVAILABLE" :
                                  (pool != null && pool["readable"]?.GetValue<bool>() == true && (pool["exhausted"]?.GetValue<bool>() == true || remaining == 0)) ? "EXHAUSTED" :
                                  remaining == null ? "UNREAD" : "AVAILABLE";

            view["availability"] = availability;
            view["quotaReadable"] = remaining != null;
            view["remainingPercent"] = remaining != null ? JsonValue.Create(remaining.Value) : null;
            view["resetAt"] = pool?["resetAt"]?.GetValue<string>();
            view["quota"] = pool != null ? (JsonObject)pool.DeepClone() : new JsonObject
            {
                ["id"] = poolId,
                ["label"] = provider["label"]?.GetValue<string>(),
                ["readable"] = false,
                ["leftPct"] = null,
                ["exhausted"] = false
            };
            view["models"] = new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray());

            result.Add(view);
        }

        return result;
    }
}
