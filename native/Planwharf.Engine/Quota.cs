// Port of quota.js.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Remaining-quota sentence for one plan. The browser root / window / AMD wrapper stays in quota.js.
public static class Quota
{
    public static JsonObject Describe(JsonNode? plan)
    {
        double? value = null;
        if (plan is JsonObject obj && Truthy(obj, "readable"))
        {
            if (Finite(obj, "leftPct", out var left)) value = left;
            else if (Finite(obj, "pct", out var pct)) value = 100 - pct;
        }
        if (value is null) return Unread();
        var remaining = ClampRound(value.Value);
        var band = remaining >= 75 ? "green" : remaining >= 50 ? "orange" : remaining >= 25 ? "yellow" : "red";
        var text = remaining + "% REMAINING" + (remaining == 0 ? " \u00b7 EXHAUSTED" : "");
        return new JsonObject
        {
            ["remaining"] = JsonValue.Create(remaining),
            ["band"] = band,
            ["text"] = text
        };
    }

    public static string Html(JsonNode? plan)
    {
        var described = Describe(plan);
        return "<span class=\"quota-remaining quota-" + described["band"]!.GetValue<string>() + "\">" + described["text"]!.GetValue<string>() + "</span>";
    }

    static JsonObject Unread() => new()
    {
        ["remaining"] = null,
        ["band"] = "grey",
        ["text"] = "REMAINING UNREAD"
    };

    // JavaScript Math.round: a midpoint goes toward +infinity, then the result is clamped to 0..100.
    static int ClampRound(double value)
    {
        var rounded = JsRound(value);
        if (double.IsNaN(rounded) || rounded <= 0) return 0;
        if (rounded >= 100) return 100;
        return (int)rounded;
    }

    static double JsRound(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number)) return number;
        if (number < 0.5 && number > 0) return 0;
        if (number < 0 && number >= -0.5) return 0;
        var floor = Math.Floor(number);
        return number - floor >= 0.5 ? floor + 1 : floor;
    }

    static bool Truthy(JsonObject owner, string key)
    {
        if (!owner.TryGetPropertyValue(key, out var node) || node is null) return false;
        if (node is not JsonValue value) return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => Number(value) is var number && number != 0 && !double.IsNaN(number),
            _ => true
        };
    }

    static bool Finite(JsonObject owner, string key, out double number)
    {
        number = 0;
        if (!owner.TryGetPropertyValue(key, out var node) || node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        number = Number(value);
        return double.IsFinite(number);
    }

    static double Number(JsonValue value) => double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
