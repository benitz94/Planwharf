// Port of engine/plan-status.cjs.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class PlanStatus
{
    private static readonly Dictionary<string, Regex> ExpiredRegexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new Regex(@"(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)|subscription required", RegexOptions.IgnoreCase | RegexOptions.ECMAScript),
        ["codex"] = new Regex(@"(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)|your plan has expired", RegexOptions.IgnoreCase | RegexOptions.ECMAScript),
        ["grok"] = new Regex(@"(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript),
        ["antigravity"] = new Regex(@"(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)
    };

    private static readonly Dictionary<string, Regex> SignedOutRegexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = new Regex(@"(?:not logged in|login expired|sign in to claude|subscription credentials are unavailable)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript),
        ["codex"] = new Regex(@"(?:not logged in|login expired|sign in with codex)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript),
        ["grok"] = new Regex(@"(?:not logged in|login expired|sign in with grok)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript),
        ["antigravity"] = new Regex(@"(?:not logged in|login expired|sign in to agy)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)
    };

    public static string ProviderKey(string? provider)
    {
        var raw = provider ?? "";
        var key = raw.Split('@')[0];
        return ExpiredRegexes.ContainsKey(key) ? key : raw;
    }

    public static string StatusFrom(string? provider, JsonObject? response)
    {
        if (response?["readable"]?.GetValue<bool>() == true)
            return "active";

        var key = ProviderKey(provider);
        var reason = response?["reason"]?.GetValue<string>() ?? response?["error"]?.GetValue<string>() ?? "";

        if (ExpiredRegexes.TryGetValue(key, out var expRe) && expRe.IsMatch(reason))
            return "expired";
        if (SignedOutRegexes.TryGetValue(key, out var soRe) && soRe.IsMatch(reason))
            return "not signed in";

        return "unknown";
    }

    public static (JsonArray Plans, string? Status) NotePlanRefusal(JsonArray? plans, JsonObject? model, string? reason)
    {
        var list = new JsonArray();
        if (plans != null)
        {
            foreach (var p in plans)
            {
                if (p is JsonObject obj)
                    list.Add((JsonNode)obj.DeepClone());
            }
        }

        var provider = model?["adapter"]?.GetValue<string>() ?? model?["provider"]?.GetValue<string>() ?? model?["providerId"]?.GetValue<string>();
        var resp = new JsonObject { ["reason"] = reason };
        var status = StatusFrom(provider, resp);

        if (model == null || (status != "expired" && status != "not signed in"))
            return (list, null);

        var pool = model["quotaPoolId"]?.GetValue<string>() ?? model["plan"]?.GetValue<string>();
        if (string.IsNullOrEmpty(pool))
            return (list, null);

        var label = model["companyId"]?.GetValue<string>() ?? model["providerId"]?.GetValue<string>() ?? model["provider"]?.GetValue<string>() ?? pool;
        var row = new JsonObject
        {
            ["id"] = pool,
            ["planStatus"] = status,
            ["readable"] = false,
            ["exhausted"] = false,
            ["leftPct"] = null,
            ["pct"] = null,
            ["windows"] = new JsonArray(),
            ["reason"] = $"{label} plan {status}."
        };

        int index = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is JsonObject obj && obj["id"]?.GetValue<string>() == pool)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            list.Add((JsonNode)row);
        }
        else
        {
            var existing = (JsonObject)list[index]!;
            foreach (var pair in row)
            {
                existing[pair.Key] = pair.Value?.DeepClone();
            }
        }

        return (list, status);
    }

    public static JsonArray MergePlans(JsonArray? previous, JsonArray? next)
    {
        var incoming = (next ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(p => !string.IsNullOrEmpty(p["id"]?.GetValue<string>()))
            .Select(p => (JsonObject)p.DeepClone())
            .ToList();

        var prior = (previous ?? new JsonArray())
            .OfType<JsonObject>()
            .Where(p => !string.IsNullOrEmpty(p["id"]?.GetValue<string>()))
            .ToDictionary(p => p["id"]!.GetValue<string>(), p => (JsonObject)p.DeepClone(), StringComparer.Ordinal);

        var merged = new List<JsonObject>();
        foreach (var plan in incoming)
        {
            var id = plan["id"]!.GetValue<string>();
            if (!prior.TryGetValue(id, out var old) ||
                plan["readable"]?.GetValue<bool>() == true ||
                plan["planStatus"]?.GetValue<string>() is "active" or "expired" or "not signed in")
            {
                merged.Add(plan);
                continue;
            }

            var oldStatus = old["planStatus"]?.GetValue<string>();
            if (oldStatus != "expired" && oldStatus != "not signed in")
            {
                merged.Add(plan);
                continue;
            }

            var updated = (JsonObject)plan.DeepClone();
            updated["planStatus"] = oldStatus;
            updated["reason"] = old["reason"]?.GetValue<string>() ?? plan["reason"]?.GetValue<string>();
            merged.Add(updated);
        }

        var seen = merged.Select(p => p["id"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        foreach (var old in prior.Values)
        {
            var id = old["id"]!.GetValue<string>();
            var oldStatus = old["planStatus"]?.GetValue<string>();
            if (seen.Contains(id) || (oldStatus != "expired" && oldStatus != "not signed in"))
                continue;

            var preserved = (JsonObject)old.DeepClone();
            preserved["readable"] = false;
            preserved["exhausted"] = false;
            preserved["leftPct"] = null;
            preserved["pct"] = null;
            preserved["windows"] = new JsonArray();
            merged.Add(preserved);
        }

        var result = new JsonArray();
        foreach (var m in merged)
            result.Add((JsonNode)m);
        return result;
    }

    public static JsonObject WithStatus(string? provider, JsonObject response)
    {
        var clone = (JsonObject)response.DeepClone();
        clone["planStatus"] = StatusFrom(provider, response);
        return clone;
    }

    public static bool BlockedStatus(JsonObject? plan)
    {
        var status = plan?["planStatus"]?.GetValue<string>();
        return status is "expired" or "not signed in";
    }

    public static JsonObject? ProviderError(string? provider, string? text, string label)
    {
        var truncated = (text ?? "").Length > 8192 ? (text ?? "")[..8192] : (text ?? "");
        var state = StatusFrom(provider, new JsonObject { ["reason"] = truncated });
        if (state == "expired")
            return new JsonObject { ["planStatus"] = state, ["reason"] = $"{label} plan expired." };
        if (state == "not signed in")
            return new JsonObject { ["planStatus"] = state, ["reason"] = $"{label} is not signed in." };
        return null;
    }
}
