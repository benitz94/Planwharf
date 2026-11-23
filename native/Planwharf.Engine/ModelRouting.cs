// Port of engine/model-routing.cjs.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class ModelRouting
{
    private static readonly Regex ControlCharsRegex = new(@"[\x00-\x1f\x7f]", RegexOptions.ECMAScript);

    private static readonly Dictionary<string, Regex> NativeModelRegexes = new(StringComparer.Ordinal)
    {
        ["claude"] = new Regex(@"^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.ECMAScript),
        ["codex"] = new Regex(@"^gpt-", RegexOptions.ECMAScript),
        ["grok"] = new Regex(@"^grok-", RegexOptions.ECMAScript),
        ["antigravity"] = new Regex(@"^(gemini-|claude-|gpt-oss-)", RegexOptions.ECMAScript)
    };

    public static string? ModelRouteError(JsonObject? model)
    {
        if (model == null) return "Model configuration is required";
        if (model["available"]?.GetValue<bool>() == false)
            return model["unavailableReason"]?.GetValue<string>() ?? "Model unavailable";

        var provider = model["adapter"]?.GetValue<string>() ?? model["provider"]?.GetValue<string>();
        if (string.IsNullOrEmpty(provider) || !NativeModelRegexes.TryGetValue(provider, out var nativeRegex))
        {
            return "Provider/model mismatch: unsupported provider adapter " + (provider ?? "null");
        }

        var cli = model["cli"]?.GetValue<string>();
        if (string.IsNullOrEmpty(cli) || cli != cli.Trim() || ControlCharsRegex.IsMatch(cli))
        {
            return "Provider/model mismatch: an exact nonempty CLI model ID is required for " + provider;
        }

        if (Encoding.UTF8.GetByteCount(cli) > 512)
        {
            return "CLI model ID exceeds the 512-byte configuration limit";
        }

        if (model.ContainsKey("modelRouting"))
        {
            var routing = model["modelRouting"]?.GetValue<string>();
            if (routing != "configured")
                return "Invalid modelRouting: expected \"configured\" or an omitted value";
        }

        var isConfigured = model["modelRouting"]?.GetValue<string>() == "configured";
        if (!isConfigured && !nativeRegex.IsMatch(cli))
        {
            return $"Provider/model mismatch: {provider}/{cli}. Set modelRouting to \"configured\" only when this exact CLI supports the model.";
        }

        return null;
    }

    public static string? ResolveCliModel(JsonObject? model)
    {
        return ModelRouteError(model) != null ? null : model?["cli"]?.GetValue<string>();
    }

    public static string PersistedSavingMode(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant().Replace('_', '-').Replace("  ", " ");
        if (text is "normal" or "normale") return "normal";
        if (text is "safe" or "sicura" or "sicuro") return "safe";
        if (text is "ultra-safe" or "ultra safe" or "ultrasafe" or "ultra sicura" or "ultra sicuro") return "ultra-safe";
        return "normal";
    }

    public static string ModelClass(JsonObject? model, int? tier = null)
    {
        var weightSource = model?["weightSource"]?.GetValue<string>();
        var weight = model?["weight"]?.GetValue<string>()?.Trim().ToLowerInvariant();

        string? ClassOfWeight(string? w) => w switch
        {
            "premium" or "flagship" => "flagship",
            "default" or "mid" => "mid",
            "light" => "light",
            _ => null
        };

        if (weightSource == "team" && ClassOfWeight(weight) is string teamCls)
            return teamCls;

        int? ranked = tier;
        if (ranked == null && model?["leadTier"] is JsonNode ltNode && ltNode.GetValue<int>() is int lt)
            ranked = lt;

        if (ranked is 0) return "flagship";
        if (ranked is 1) return "mid";
        if (ranked is 2) return "light";

        if (ClassOfWeight(weight) is string wCls)
            return wCls;

        return "mid";
    }

    public static bool AllowsSavingMode(string mode, JsonObject? model, int? tier = null)
    {
        var saved = PersistedSavingMode(mode);
        var cls = ModelClass(model, tier);
        return saved switch
        {
            "safe" => cls is "mid" or "light",
            "ultra-safe" => cls is "light",
            _ => true
        };
    }

    public static string SavingModeLabel(string mode)
    {
        var saved = PersistedSavingMode(mode);
        return saved switch
        {
            "safe" => "Safe",
            "ultra-safe" => "Ultra safe",
            _ => "Normal"
        };
    }

    public static string? SavingModeError(JsonObject? model, string? mode, int? tier = null)
    {
        var saved = PersistedSavingMode(mode);
        if (saved == "normal" || AllowsSavingMode(saved, model, tier))
            return null;

        var name = model?["label"]?.GetValue<string>() ?? model?["id"]?.GetValue<string>() ?? "This model";
        var cls = ModelClass(model, tier);
        var label = SavingModeLabel(saved);
        return $"{name} is {cls}, and {label} does not allow it.";
    }
}
