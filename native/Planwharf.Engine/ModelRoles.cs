// Port of engine/model-roles.cjs.
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Which jobs each model may take. Models are JSON objects (as in the JavaScript); this module reads
// them without a fixed shape, because the model list on disk keeps fields it does not know.
public static class ModelRoles
{
    public static readonly string[] RoleIds = ["orchestrator", "worker", "verifier", "reviewer", "supervisor"];
    // The verify slice checks and fixes the builder's work. Only Verifier models take it.
    public const string VerifySlice = "Verify the work independently against the job";

    public static bool IsVerifySlice(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().StartsWith(VerifySlice, StringComparison.Ordinal);

    // Only the API advisory transport has a verified no-tool execution contract.
    public static bool SupportsSupervisor(JsonObject model)
    {
        var adapter = Prop(model, "adapter");
        var chosen = Truthy(adapter) ? adapter : Prop(model, "provider");
        return chosen is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "api";
    }

    public static Dictionary<string, bool> RoleEligibility(JsonObject model)
    {
        var capabilities = Prop(model, "capabilities");
        bool tools = !IsFalse(Prop(capabilities, "canUseTools")) && !IsFalse(Prop(capabilities, "canInspectFilesystem"));
        bool workerOk = !IsFalse(Prop(model, "workerEligible")) && !IsFalse(Prop(capabilities, "workerEligible"));
        var orchestrator = Prop(model, "orchestratorEligible") ?? Prop(capabilities, "orchestratorEligible") ?? Prop(capabilities, "canOrchestrate");
        var reviewer = Prop(model, "reviewerEligible") ?? Prop(capabilities, "reviewerEligible");
        var kind = Prop(model, "kind");
        return new Dictionary<string, bool>
        {
            ["orchestrator"] = tools && (orchestrator is null ? !IsString(kind, "local") : Truthy(orchestrator)),
            ["worker"] = workerOk,
            ["verifier"] = workerOk,
            ["reviewer"] = tools && (reviewer is null ? !IsFalse(Prop(model, "workerEligible")) : Truthy(reviewer)),
            ["supervisor"] = SupportsSupervisor(model) && !IsFalse(Prop(model, "supervisorEligible")) && !IsFalse(Prop(capabilities, "supervisorEligible"))
        };
    }

    // A copy of the model with roleEligibility and roles set; every other field is kept.
    public static JsonObject WithModelRoles(JsonObject model, JsonObject? preferences = null)
    {
        var eligibility = RoleEligibility(model);
        JsonNode? Chosen(string role) => Prop(preferences, role) ?? Prop(Prop(model, "roles"), role);
        // A model saved before the Verifier role existed keeps verifying where it could work.
        bool Fallback(string role) => role == "supervisor" ? false : role == "verifier" ? Chosen("worker") is null || Truthy(Chosen("worker")) : true;
        var roles = new JsonObject();
        var eligible = new JsonObject();
        foreach (var role in RoleIds)
        {
            var chosen = Chosen(role);
            roles[role] = eligibility[role] && (chosen is null ? Fallback(role) : Truthy(chosen));
            eligible[role] = eligibility[role];
        }
        var result = (JsonObject)model.DeepClone();
        result["roleEligibility"] = eligible;
        result["roles"] = roles;
        return result;
    }

    public static string AssignmentRole(JsonObject? job)
    {
        job ??= new JsonObject();
        if (IsString(Prop(job, "type"), "AUDIT") || IsString(Prop(job, "kind"), "audit") || IsString(Prop(job, "kind"), "review")) return "reviewer";
        return IsString(Prop(job, "kind"), "verify") || IsVerifySlice(Prop(job, "title")) || IsVerifySlice(Prop(job, "detail")) || IsVerifySlice(Prop(job, "slice")) ? "verifier" : "worker";
    }

    public static bool PermitsAssignment(JsonObject model, JsonObject? job)
    {
        var role = AssignmentRole(job);
        var resolved = WithModelRoles(model);
        var goldNode = Prop(job, "goldModel");
        if (role == "worker" && Truthy(goldNode))
        {
            // Reading a field of a non-object (a string, a number) gives undefined in JavaScript.
            var gold = goldNode as JsonObject ?? new JsonObject();
            var providerKey = Truthy(Prop(model, "providerId")) ? "providerId" : "provider";
            if (!StrictEquals(model, "id", gold, "id") || !StrictEquals(model, providerKey, gold, "provider") || !StrictEquals(model, "cli", gold, "cli")) return false;
        }
        return resolved["roles"]![role]!.GetValue<bool>();
    }

    // Property of an object, or null (JavaScript undefined and null both count as missing for ?. and ??).
    static JsonNode? Prop(JsonNode? node, string name) => node is JsonObject o && o.TryGetPropertyValue(name, out var v) ? v : null;
    static bool IsFalse(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.False;
    static bool IsString(JsonNode? node, string text) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == text;

    // JavaScript truthiness of a JSON value. JsonValue may store 0 as int, long or double.
    static bool Truthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is not JsonValue v) return true;
        return v.GetValueKind() switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => Convert.ToDouble(v.ToString(), CultureInfo.InvariantCulture) != 0,
            _ => true
        };
    }

    // JavaScript a.x === b.y, where a missing field (undefined) differs from a JSON null.
    static bool StrictEquals(JsonObject a, string aKey, JsonObject b, string bKey)
    {
        var hasA = a.TryGetPropertyValue(aKey, out var x);
        var hasB = b.TryGetPropertyValue(bKey, out var y);
        if (hasA != hasB) return false;
        if (!hasA) return true;
        return JsonNode.DeepEquals(x, y);
    }
}
