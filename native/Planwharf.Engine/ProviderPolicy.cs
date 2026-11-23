// Port of engine/provider-policy.cjs.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class ProviderPolicy
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "minRemainingPercent",
        "warningRemainingPercent",
        "fallbackProviderId",
        "alwaysApprove"
    };

    public static JsonObject DefaultProviderPolicy() => new()
    {
        ["minRemainingPercent"] = null,
        ["warningRemainingPercent"] = null,
        ["fallbackProviderId"] = null,
        ["alwaysApprove"] = false
    };

    public static JsonObject ValidateProviderPolicy(JsonNode? value)
    {
        var migrated = ModelPolicy.MigratePolicy(value);
        if (migrated == null)
            throw new InvalidOperationException("Invalid provider policy fields.");

        foreach (var key in migrated.Select(p => p.Key))
        {
            if (!AllowedKeys.Contains(key))
                throw new InvalidOperationException("Invalid provider policy fields.");
        }

        var policy = DefaultProviderPolicy();
        foreach (var pair in migrated)
        {
            policy[pair.Key] = pair.Value?.DeepClone();
        }

        var common = new JsonObject();
        foreach (var pair in policy)
        {
            if (pair.Key != "fallbackProviderId")
                common[pair.Key] = pair.Value?.DeepClone();
        }

        ModelPolicy.ValidatePolicy(common);

        if (policy.TryGetPropertyValue("fallbackProviderId", out var fb) && fb != null)
        {
            if (fb is not JsonValue v || !v.TryGetValue<string>(out var fbStr) || string.IsNullOrWhiteSpace(fbStr))
                throw new InvalidOperationException("Fallback must be an explicit provider ID or null.");
        }

        return policy;
    }

    public static JsonObject ProviderPolicyFor(JsonObject provider, JsonArray? models, JsonObject? settings = null)
    {
        var (policy, source) = GetPolicyAndSource(provider, models, settings);
        return new JsonObject
        {
            ["policy"] = policy,
            ["source"] = source
        };
    }

    private static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue kind || kind.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
        return double.TryParse(kind.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static double AsDouble(JsonNode? node) => TryDouble(node, out var v) ? v : 0.0;

    public static (JsonObject Policy, string Source) GetPolicyAndSource(JsonObject provider, JsonArray? models, JsonObject? settings = null)
    {
        var providerId = provider["id"]?.GetValue<string>() ?? "";
        if (settings?["providerPolicies"] is JsonObject providerPolicies && providerPolicies[providerId] != null)
        {
            return (ValidateProviderPolicy(providerPolicies[providerId]), "saved");
        }

        if (provider["policy"] != null)
        {
            return (ValidateProviderPolicy(provider["policy"]), "configured");
        }

        var modelsList = models?.OfType<JsonObject>().ToList() ?? new List<JsonObject>();
        var siblings = modelsList.Where(m =>
        {
            var pId = m["providerId"]?.GetValue<string>() ?? m["provider"]?.GetValue<string>();
            return pId == providerId;
        }).ToList();

        var settingsModelPolicies = settings?["modelPolicies"] as JsonObject;
        var legacy = new List<(JsonObject Model, JsonObject Policy)>();
        foreach (var m in siblings)
        {
            var mId = m["id"]?.GetValue<string>() ?? "";
            var hasSettingsPolicy = settingsModelPolicies?[mId] != null;
            var hasModelPolicy = m["policy"] != null;
            var hasAlwaysApprove = m.ContainsKey("alwaysApprove");

            if (hasSettingsPolicy || hasModelPolicy || hasAlwaysApprove)
            {
                var merged = new JsonObject();
                merged["alwaysApprove"] = m["alwaysApprove"]?.GetValue<bool>() ?? false;

                if (m["policy"] is JsonObject pObj)
                {
                    var migratedP = ModelPolicy.MigratePolicy(pObj);
                    foreach (var pair in migratedP) merged[pair.Key] = pair.Value?.DeepClone();
                }

                if (settingsModelPolicies?[mId] is JsonObject sObj)
                {
                    var migratedS = ModelPolicy.MigratePolicy(sObj);
                    foreach (var pair in migratedS) merged[pair.Key] = pair.Value?.DeepClone();
                }

                legacy.Add((m, ModelPolicy.ValidatePolicy(merged)));
            }
        }

        if (legacy.Count == 0)
        {
            return (DefaultProviderPolicy(), "default");
        }

        var minima = legacy
            .Where(r => r.Policy["minRemainingPercent"] != null && TryDouble(r.Policy["minRemainingPercent"], out _))
            .Select(r => AsDouble(r.Policy["minRemainingPercent"]))
            .ToList();

        double? minRemainingPercent = minima.Count > 0 ? minima.Max() : null;

        var warnings = legacy
            .Where(r => r.Policy["warningRemainingPercent"] != null && TryDouble(r.Policy["warningRemainingPercent"], out var w) && minRemainingPercent != null && w > minRemainingPercent.Value)
            .Select(r => AsDouble(r.Policy["warningRemainingPercent"]))
            .ToList();

        double? warningRemainingPercent = warnings.Count > 0 ? warnings.Max() : null;

        var fallbackIds = legacy.Select(r =>
        {
            var fbModelId = r.Policy["fallbackModelId"]?.GetValue<string>();
            var targetModel = modelsList.FirstOrDefault(m => m["id"]?.GetValue<string>() == fbModelId);
            return targetModel != null ? (targetModel["providerId"]?.GetValue<string>() ?? targetModel["provider"]?.GetValue<string>()) : null;
        }).ToList();

        string? fallbackProviderId = null;
        if (fallbackIds.Count > 0 && fallbackIds[0] != null && fallbackIds.All(id => id == fallbackIds[0]) && fallbackIds[0] != providerId)
        {
            fallbackProviderId = fallbackIds[0];
        }

        bool alwaysApprove = legacy.All(r => r.Policy["alwaysApprove"]?.GetValue<bool>() == true);

        var resultPolicy = new JsonObject
        {
            ["minRemainingPercent"] = minRemainingPercent != null ? JsonValue.Create(minRemainingPercent.Value) : null,
            ["warningRemainingPercent"] = warningRemainingPercent != null ? JsonValue.Create(warningRemainingPercent.Value) : null,
            ["fallbackProviderId"] = fallbackProviderId != null ? JsonValue.Create(fallbackProviderId) : null,
            ["alwaysApprove"] = alwaysApprove
        };

        return (resultPolicy, "legacy");
    }
}
