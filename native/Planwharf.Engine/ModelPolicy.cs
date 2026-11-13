// Port of engine/model-policy.cjs.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Quota thresholds and fallbacks for one model. Policies are JSON objects, same as the JavaScript:
// unknown fields are rejected, and a value that is loaded is never overwritten by migration.
public static class ModelPolicy
{
    public const long RetryUnavailableMs = 5 * 60000;
    public const int AutoSwitchThresholdPercent = 20;

    static readonly string[] PolicyKeys = ["minRemainingPercent", "warningRemainingPercent", "fallbackModelId", "alwaysApprove"];
    static readonly (string OldKey, string NewKey)[] LegacyKeys = [("maxUsedPercent", "minRemainingPercent"), ("warningUsedPercent", "warningRemainingPercent")];

    public static JsonObject DefaultPolicy() => new()
    {
        ["minRemainingPercent"] = null,
        ["warningRemainingPercent"] = null,
        ["fallbackModelId"] = null,
        ["alwaysApprove"] = false
    };

    // Automatic model switch when a model's quota runs low: on by default, at 20% left.
    public static JsonObject AutoSwitchDefault() => new()
    {
        ["enabled"] = true,
        ["thresholdPercent"] = AutoSwitchThresholdPercent
    };

    public static JsonObject MigratePolicy(JsonNode? value)
    {
        if (value is not JsonObject source) throw new InvalidOperationException("Invalid quota policy.");
        var migrated = new JsonObject();
        foreach (var pair in source) migrated[pair.Key] = pair.Value?.DeepClone();
        foreach (var (oldKey, newKey) in LegacyKeys)
        {
            if (!migrated.ContainsKey(oldKey)) continue;
            if (migrated.ContainsKey(newKey)) throw new InvalidOperationException("Do not mix consumed and remaining quota settings.");
            var previous = migrated[oldKey];
            if (previous is not null && !TryPercent(previous, out _)) throw new InvalidOperationException("Invalid legacy usage percentage.");
            migrated[newKey] = previous is null ? null : JsonNumber(100 - Number(previous));
            migrated.Remove(oldKey);
        }
        return migrated;
    }

    public static JsonObject ValidatePolicy(JsonNode? value)
    {
        var migrated = MigratePolicy(value);
        foreach (var key in migrated.Select(pair => pair.Key))
            if (!PolicyKeys.Contains(key)) throw new InvalidOperationException("Invalid model policy fields.");
        var minNode = Field(migrated, "minRemainingPercent");
        var warningNode = Field(migrated, "warningRemainingPercent");
        if (minNode is not null && !TryPercent(minNode, out _)) throw new InvalidOperationException("Remaining quota thresholds must be percentages from 0 to 100, or null.");
        if (warningNode is not null && !TryPercent(warningNode, out _)) throw new InvalidOperationException("Remaining quota thresholds must be percentages from 0 to 100, or null.");
        double? min = minNode is null ? null : Number(minNode);
        double? warning = warningNode is null ? null : Number(warningNode);
        if (warning is not null && (min is null || warning <= min)) throw new InvalidOperationException("Warning remaining quota must be higher than the stop threshold.");
        var always = migrated.ContainsKey("alwaysApprove") ? migrated["alwaysApprove"] : JsonValue.Create(false);
        if (always is not JsonValue kind || (kind.GetValueKind() is not JsonValueKind.True and not JsonValueKind.False))
            throw new InvalidOperationException("Always approve must be a boolean.");
        var fallback = Field(migrated, "fallbackModelId");
        if (fallback is not null && (AsString(fallback) is not string id || StructuredFinal.Trim(id).Length == 0))
            throw new InvalidOperationException("Fallback must be an explicit model ID, or null.");
        return new JsonObject
        {
            ["minRemainingPercent"] = minNode?.DeepClone(),
            ["warningRemainingPercent"] = warningNode?.DeepClone(),
            ["fallbackModelId"] = fallback?.DeepClone(),
            ["alwaysApprove"] = always.DeepClone()
        };
    }

    public static JsonObject UsagePolicy(JsonObject model, JsonArray? plans = null)
    {
        var policyNode = model.TryGetPropertyValue("policy", out var configured) && configured is not null ? configured : new JsonObject();
        var policy = ValidatePolicy(policyNode);
        var plan = FindPlan(plans, PoolOf(model), skipNull: false);
        double? left = plan is not null && IsTrue(plan, "readable") && TryPercent(plan["leftPct"], out var leftPct) ? leftPct
            : plan is not null && IsTrue(plan, "readable") && TryPercent(plan["pct"], out var usedPct) ? 100 - usedPct : null;
        double? usedPercent = left is null ? null : Math.Max(0, Math.Min(100, 100 - left.Value));
        double? min = policy["minRemainingPercent"] is null ? null : Number(policy["minRemainingPercent"]!);
        double? warning = policy["warningRemainingPercent"] is null ? null : Number(policy["warningRemainingPercent"]!);
        if (policy["warningRemainingPercent"] is null && min is not null && min < 100) warning = Math.Min(100, min.Value + 10);
        var unread = left is null && min is not null;
        var reached = left is not null && min is not null && left <= min;
        var near = left is not null && warning is not null && left <= warning;
        var state = unread ? "unread" : reached ? "limit" : near ? "warning" : "ok";
        string? reason = unread ? "Configured remaining quota threshold cannot be verified: provider quota is unread."
            : reached ? $"Remaining quota stop threshold reached ({ToFixed1(left!.Value)}% remaining / stop at {JsNumber(min!.Value)}%)."
            : near ? $"Low provider quota ({ToFixed1(left!.Value)}% remaining / warning at {JsNumber(warning!.Value)}%)." : null;
        return new JsonObject
        {
            ["minRemainingPercent"] = policy["minRemainingPercent"]?.DeepClone(),
            ["warningRemainingPercent"] = warning is null ? null : JsonNumber(warning.Value),
            ["fallbackModelId"] = policy["fallbackModelId"]?.DeepClone(),
            ["alwaysApprove"] = policy["alwaysApprove"]?.DeepClone(),
            ["remainingPercent"] = left is null ? null : JsonNumber(left.Value),
            ["usedPercent"] = usedPercent is null ? null : JsonNumber(usedPercent.Value),
            ["blocked"] = unread || reached,
            ["state"] = state,
            ["reason"] = reason
        };
    }

    static JsonObject? FindPlan(JsonArray? plans, JsonNode? poolId, bool skipNull)
    {
        if (plans is null) return null;
        foreach (var item in plans)
        {
            if (item is null)
            {
                if (skipNull) continue;
                throw new InvalidOperationException("Invalid quota plan.");
            }
            if (!StructuredFinal.Truthy(item) || item is not JsonObject plan) continue;
            if (IdEquals(plan, poolId)) return plan;
        }
        return null;
    }

    static bool IdEquals(JsonObject obj, JsonNode? poolId)
    {
        var has = obj.TryGetPropertyValue("id", out var id);
        if (poolId is null) return !has;
        if (!has || id is null) return false;
        return JsonNode.DeepEquals(id, poolId);
    }

    static JsonNode? PoolOf(JsonObject model) => OrTruthy(model, "quotaPoolId", "plan");

    static JsonNode? OrTruthy(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
            if (obj.TryGetPropertyValue(key, out var value) && StructuredFinal.Truthy(value)) return value;
        return null;
    }

    static JsonNode? Field(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var value) ? value : null;

    static bool IsTrue(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    static string? AsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool TryPercent(JsonNode? node, out double value) => TryFinite(node, out value) && value >= 0 && value <= 100;

    static bool TryFinite(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue kind || kind.GetValueKind() != JsonValueKind.Number) return false;
        return double.TryParse(kind.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    static double Number(JsonNode node)
    {
        if (!TryFinite(node, out var value)) throw new InvalidOperationException("Remaining quota thresholds must be percentages from 0 to 100, or null.");
        return value;
    }

    // JavaScript Number#toFixed(1): a midpoint rounds toward +infinity.
    static string ToFixed1(double value)
    {
        var rounded = decimal.Round((decimal)value * 10m, 0, MidpointRounding.ToPositiveInfinity);
        return (rounded / 10m).ToString("0.0", CultureInfo.InvariantCulture);
    }

    static string JsNumber(double value) => value.ToString("G15", CultureInfo.InvariantCulture);

    public static JsonObject? ResolveUsageFallback(JsonObject model, JsonArray models, JsonArray? plans, Func<JsonObject, bool>? eligible)
    {
        eligible ??= static _ => true;
        plans ??= new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        JsonObject? current = model;
        while (current is not null && seen.Add(SeenKey(current)))
        {
            if (!StructuredFinal.Truthy(Field(current, "enabled")) || IsFalse(current, "available") || IsFalse(current, "providerEnabled") || !eligible(current)) return null;
            var status = UsagePolicy(current, plans);
            var exhausted = IsExhausted(current, plans);
            if (AsString(status["state"]) == "unread") return null;
            if (!Blocked(status) && !exhausted) return eligible(current) ? current : null;
            var fallbackProvider = Field(current, "fallbackProviderId");
            if (AsString(fallbackProvider) == "@automatic") current = FindAutomatic(current, models, plans, eligible, seen);
            else if (StructuredFinal.Truthy(fallbackProvider)) current = FindProviderFallback(models, plans, eligible, seen, fallbackProvider!);
            else current = FindById(models, status["fallbackModelId"]);
        }
        return null;
    }

    static JsonObject? FindAutomatic(JsonObject current, JsonArray models, JsonArray plans, Func<JsonObject, bool> eligible, HashSet<string> seen)
    {
        var sourceProvider = OrTruthy(current, "providerId", "provider");
        var sourcePool = PoolOf(current);
        foreach (var item in models)
        {
            if (item is not JsonObject other || seen.Contains(SeenKey(other)) || !EligibleModel(other, eligible)) continue;
            if (!Different(OrTruthy(other, "providerId", "provider"), sourceProvider)) continue;
            if (sourcePool is not null && !Different(PoolOf(other), sourcePool)) continue;
            if (Blocked(UsagePolicy(other, plans)) || IsExhausted(other, plans)) continue;
            return other;
        }
        return null;
    }

    static JsonObject? FindProviderFallback(JsonArray models, JsonArray plans, Func<JsonObject, bool> eligible, HashSet<string> seen, JsonNode providerId)
    {
        JsonObject? first = null;
        foreach (var item in models)
        {
            if (item is not JsonObject other || seen.Contains(SeenKey(other)) || !EligibleModel(other, eligible)) continue;
            if (!SameJson(OrTruthy(other, "providerId", "provider"), providerId)) continue;
            first ??= other;
            if (!Blocked(UsagePolicy(other, plans)) && !IsExhausted(other, plans)) return other;
        }
        return first;
    }

    static bool EligibleModel(JsonObject model, Func<JsonObject, bool> eligible) =>
        StructuredFinal.Truthy(Field(model, "enabled")) && !IsFalse(model, "available") && !IsFalse(model, "providerEnabled") && eligible(model);

    static JsonObject? FindById(JsonArray models, JsonNode? id)
    {
        foreach (var item in models)
        {
            if (item is not JsonObject other || !other.TryGetPropertyValue("id", out var modelId)) continue;
            if (SameJson(modelId, id)) return other;
        }
        return null;
    }

    static bool IsExhausted(JsonObject model, JsonArray plans)
    {
        var pool = PoolOf(model);
        foreach (var item in plans)
        {
            if (item is null) throw new InvalidOperationException("Invalid quota plan.");
            if (item is not JsonObject plan || !IdEquals(plan, pool) || !StructuredFinal.Truthy(Field(plan, "readable"))) continue;
            if (StructuredFinal.Truthy(Field(plan, "exhausted"))) return true;
            if (TryFinite(plan["leftPct"], out var left) && left == 0) return true;
        }
        return false;
    }

    static bool Blocked(JsonObject status) => status["blocked"] is JsonValue value && value.TryGetValue<bool>(out var blocked) && blocked;

    static bool IsFalse(JsonObject obj, string key) => obj.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var flag) && !flag;

    static bool Different(JsonNode? left, JsonNode? right) => !SameJson(left, right);

    static bool PropEquals(JsonObject left, string leftKey, JsonObject right, string rightKey)
    {
        var hasLeft = left.TryGetPropertyValue(leftKey, out var a);
        var hasRight = right.TryGetPropertyValue(rightKey, out var b);
        if (hasLeft != hasRight) return false;
        return SameJson(a, b);
    }

    static JsonNode JsonNumber(double value) =>
        value == Math.Truncate(value) && value is >= int.MinValue and <= int.MaxValue ? JsonValue.Create((int)value) : JsonValue.Create(value);

    // Missing and JSON null are different, matching JavaScript ===.
    static bool SameJson(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return JsonNode.DeepEquals(left, right);
    }

    static string SeenKey(JsonObject model)
    {
        if (!model.ContainsKey("id")) return "\u0000missing";
        var id = model["id"];
        if (id is null) return "\u0000null";
        return id is JsonValue ? "v:" + id.ToJsonString() : "o:" + id.ToJsonString();
    }

    public static double? QuotaLeft(JsonObject model, JsonArray? plans = null)
    {
        var plan = FindPlan(plans, PoolOf(model), skipNull: true);
        if (plan is null || !IsTrue(plan, "readable")) return null;
        if (TryFinite(plan["leftPct"], out var left)) return left;
        return TryFinite(plan["pct"], out var used) ? 100 - used : null;
    }

    // Low means exhausted, at the provider's own stop threshold, or at or under the switch threshold.
    // Unread quota is not low: nothing is switched on a guess.
    public static bool LowQuota(JsonObject model, JsonArray? plans = null, double thresholdPercent = AutoSwitchThresholdPercent)
    {
        plans ??= new JsonArray();
        var plan = FindPlan(plans, PoolOf(model), skipNull: true);
        if (plan is not null && IsTrue(plan, "readable") && StructuredFinal.Truthy(Field(plan, "exhausted"))) return true;
        if (AsString(UsagePolicy(model, plans)["state"]) == "limit") return true;
        var left = QuotaLeft(model, plans);
        return left is not null && left <= thresholdPercent;
    }

    // Which working steps should move, and to which model: the eligible one with the most quota left above the threshold.
    public static JsonArray QuotaSwitches(JsonArray? running = null, JsonArray? models = null, JsonArray? plans = null, double thresholdPercent = AutoSwitchThresholdPercent, bool enabled = true, Func<JsonObject, JsonObject, bool>? eligible = null)
    {
        if (!enabled) return new JsonArray();
        eligible ??= static (_, _) => true;
        running ??= new JsonArray();
        models ??= new JsonArray();
        plans ??= new JsonArray();
        var switches = new JsonArray();
        foreach (var item in running)
        {
            if (item is not JsonObject step) throw new InvalidOperationException("Invalid running step.");
            var current = models.OfType<JsonObject>().FirstOrDefault(model => PropEquals(model, "id", step, "modelId"));
            if (current is null || !LowQuota(current, plans, thresholdPercent)) continue;
            var ranked = models.OfType<JsonObject>()
                .Where(model => !PropEquals(model, "id", current, "id") && EligibleModel(model, candidate => eligible(candidate, step)))
                .Select(model => (Model: model, Left: QuotaLeft(model, plans)))
                .Where(row => row.Left is not null && !LowQuota(row.Model, plans, thresholdPercent))
                .Select(row => (row.Model, Left: row.Left!.Value, Shared: Avoids(step, OrTruthy(row.Model, "providerId", "provider"))))
                .OrderBy(row => row.Shared)
                .ThenByDescending(row => row.Left)
                .ToList();
            if (ranked.Count == 0) continue;
            var best = ranked[0];
            switches.Add((JsonNode)new JsonObject
            {
                ["subTaskId"] = step["subTaskId"]?.DeepClone(),
                ["fromModelId"] = current["id"]?.DeepClone(),
                ["toModelId"] = best.Model["id"]?.DeepClone(),
                ["leftPercent"] = QuotaLeft(current, plans) is double left ? JsonNumber(left) : null
            });
        }
        return switches;
    }

    static bool Avoids(JsonObject step, JsonNode? provider)
    {
        if (provider is null || !step.TryGetPropertyValue("avoidProviders", out var list) || list is null) return false;
        if (list is not JsonArray providers) throw new InvalidOperationException("avoidProviders must be an array.");
        return providers.Any(item => item is not null && JsonNode.DeepEquals(item, provider));
    }

    // A model set unavailable by a failed call is tried again after a while, not left out for the whole session.
    public static bool AvailableAgain(JsonObject? model, long? nowMs = null, long? retryMs = null)
    {
        if (model is null || !IsFalse(model, "available") || !StructuredFinal.Truthy(Field(model, "unavailableAt"))) return false;
        if (AsString(Field(model, "unavailableAt")) is not string stamp || !TryJsDate(stamp, out var since)) return false;
        var now = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return now - since >= (retryMs ?? RetryUnavailableMs);
    }

    static bool TryJsDate(string text, out long unixMs)
    {
        unixMs = 0;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        unixMs = parsed.ToUnixTimeMilliseconds();
        return true;
    }
}
