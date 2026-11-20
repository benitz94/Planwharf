// Port of models.js.
// The browser root, window, and AMD wrappers only publish these three functions. They are not ported.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Browser helper PlanwharfModels. This is not models.json and not the provider catalog.
public static class Models
{
    // New array of copies where enabled === true and available !== false.
    // Anything else is omitted, including a null entry. The input array and objects stay unchanged.
    // A non-array returns an empty array.
    public static JsonArray GetEnabledModels(JsonNode? models)
    {
        if (models is not JsonArray list) return new JsonArray();
        var result = new JsonArray();
        foreach (var entry in list)
        {
            if (entry is JsonObject model && IsEnabledAvailable(model))
                result.Add(CopyModel(model));
        }
        return result;
    }

    // New array. The named model's enabled value is stored as a boolean.
    // available === false forces enabled false even when the caller asked for true.
    // A null entry stays null, not a copy. Every object is copied. A non-array returns an empty array.
    public static JsonArray SetModelEnabled(JsonNode? models, string? id, bool enabled)
    {
        if (models is not JsonArray list) return new JsonArray();
        var result = new JsonArray();
        foreach (var entry in list)
        {
            // !m returns the entry itself (null, 0, "", false). A JsonNode cannot move
            // into the new array, so a primitive is cloned by value.
            if (entry is null || IsFalsy(entry))
            {
                result.Add(entry?.DeepClone());
                continue;
            }
            if (entry is not JsonObject model)
            {
                result.Add(AssignObject(entry));
                continue;
            }
            var copy = CopyModel(model);
            if (SameId(model, id))
                copy["enabled"] = enabled && !IsFalse(Prop(model, "available"));
            result.Add(copy);
        }
        return result;
    }

    // Order: no enabled and available model, then a missing id, then disabled or unavailable.
    // The repeated "model disabled" comment in models.js is not a second reason.
    // attempts.js returns "model not eligible". This function does not.
    public static JsonObject ValidateWorkerClaim(JsonNode? models, string? modelId)
    {
        JsonObject? found = null;
        var hasAnyEnabled = false;
        if (models is JsonArray list)
        {
            foreach (var entry in list)
            {
                if (entry is not JsonObject model) continue;
                if (!hasAnyEnabled && IsEnabledAvailable(model)) hasAnyEnabled = true;
                if (found is null && SameId(model, modelId)) found = model;
            }
        }

        if (!hasAnyEnabled) return Claim(false, "no model enabled");
        if (found is null) return Claim(false, "model not present");
        if (!IsTrue(Prop(found, "enabled")) || IsFalse(Prop(found, "available")))
            return Claim(false, "model disabled");
        return Claim(true, null);
    }

    // enabled === true and available !== false. Only a boolean true counts as enabled.
    // Missing, null, and any value other than boolean false count as available.
    static bool IsEnabledAvailable(JsonObject model) =>
        IsTrue(Prop(model, "enabled")) && !IsFalse(Prop(model, "available"));

    // Object.assign({}, model). A JsonNode can have only one parent, so property values are cloned.
    // Flat model fields (strings, booleans, numbers) match the JavaScript copy.
    static JsonObject CopyModel(JsonObject model) => (JsonObject)model.DeepClone();

    // Object.assign({}, value) for a truthy non-object. Strings and arrays expose their indexes.
    // A number or boolean true has no enumerable properties, so the copy is empty.
    static JsonObject AssignObject(JsonNode node)
    {
        var copy = new JsonObject();
        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
                copy[i.ToString(CultureInfo.InvariantCulture)] = array[i]?.DeepClone();
            return copy;
        }
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            var text = value.GetValue<string>();
            for (var i = 0; i < text.Length; i++)
                copy[i.ToString(CultureInfo.InvariantCulture)] = text.Substring(i, 1);
        }
        return copy;
    }

    // JavaScript falsy values other than null and undefined. JSON has no undefined or NaN.
    static bool IsFalsy(JsonNode node)
    {
        if (node is not JsonValue value) return false;
        return value.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null => true,
            JsonValueKind.String => value.GetValue<string>().Length == 0,
            JsonValueKind.Number => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) == 0,
            _ => false
        };
    }

    // JavaScript === against the id argument. A missing id is undefined and matches nothing here.
    // A JSON null id matches a null argument. A number does not match its decimal text.
    static bool SameId(JsonObject model, string? id)
    {
        if (!model.TryGetPropertyValue("id", out var node)) return false;
        if (node is null) return id is null;
        if (id is null || node is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return false;
        return value.GetValue<string>() == id;
    }

    static JsonNode? Prop(JsonObject model, string name)
    {
        if (!model.TryGetPropertyValue(name, out var value) || value is null) return null;
        return value;
    }

    static bool IsTrue(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.True;
    static bool IsFalse(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.False;

    static JsonObject Claim(bool ok, string? reason)
    {
        // A null reason is JSON null, the same shape as { ok: true, reason: null }.
        var claim = new JsonObject { ["ok"] = ok, ["reason"] = null };
        if (reason is not null) claim["reason"] = reason;
        return claim;
    }
}
