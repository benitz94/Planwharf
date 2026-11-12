// Port of engine/structured-final.cjs.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

// Claude can return the worker result as schema-checked final output, saving the
// file-writing call. Grok is deliberately excluded: on 2026-11-03 grok-4.7 with
// --json-schema reported nonexistent files and passing tests. See docs/PROJECT-AUTONOMY.md.
public static class StructuredFinal
{
    public const int MaxOutputChars = 8 * 1024 * 1024;
    internal static readonly string[] IdentityKeys = ["resultId", "attemptId", "subTaskId", "megaTaskId"];
    static readonly char[] WhiteSpace = "\u0009\u000a\u000b\u000c\u000d\u0020\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000\ufeff".ToCharArray();
    internal static string Trim(string text) => text.Trim(WhiteSpace);
    internal static string? String(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    internal static JsonNode? Field(JsonNode? node, string key) => node is JsonObject o ? o[key] : null;
    internal static bool Truthy(JsonNode? node) => node is not null && !(node is JsonValue v &&
        (v.TryGetValue<bool>(out var b) && !b || v.TryGetValue<string>(out var s) && s.Length == 0 || v.TryGetValue<double>(out var n) && n == 0));
    public static bool SupportsFinalResult(JsonNode? model)
    {
        var adapter = Field(model, "adapter");
        return String(Truthy(adapter) ? adapter : Field(model, "provider")) == "claude";
    }

    public static JsonObject ResultSchema(JsonObject identity)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var key in IdentityKeys)
        {
            properties[key] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(identity[key]?.DeepClone()) };
            required.Add((JsonNode?)JsonValue.Create(key));
        }
        foreach (var key in new[] { "status", "summary", "filesChanged", "tests", "blockers", "nextAction" }) required.Add((JsonNode?)JsonValue.Create(key));
        properties["status"] = JsonNode.Parse("""{"type":"string","enum":["COMPLETE","FAILED","BLOCKED"]}""");
        properties["summary"] = new JsonObject { ["type"] = "string" };
        properties["filesChanged"] = Strings();
        properties["tests"] = JsonNode.Parse("""{"type":"array","items":{"type":"object","required":["command","status","evidence"],"properties":{"command":{"type":"string"},"status":{"type":"string","enum":["PASSED","FAILED","NOT_RUN"]},"evidence":{"type":"string"}}}}""");
        properties["blockers"] = Strings();
        properties["nextAction"] = new JsonObject { ["type"] = "string" };
        properties["reportPath"] = new JsonObject { ["type"] = "string" };
        properties["sources"] = Strings();
        properties["recommendedNextAction"] = new JsonObject { ["type"] = "string" };
        return new JsonObject { ["type"] = "object", ["required"] = required, ["properties"] = properties };
    }

    static JsonNode Strings() => JsonNode.Parse("""{"type":"array","items":{"type":"string"}}""")!;

    // Format captured from Claude Code stream-json output, 2026-11-03.
    public static JsonNode? ReadFinalResult(string? adapter, string? output)
    {
        if (adapter != "claude" || output is null || Trim(output).Length == 0) return null;
        foreach (var line in Trim(output).Split('\n').Reverse())
        {
            JsonNode? item;
            try { item = JsonNode.Parse(line.TrimEnd('\r')); }
            catch (System.Text.Json.JsonException) { continue; }
            if (String(Field(item, "type")) == "result")
                return Truthy(Field(item, "is_error")) ? null : Field(item, "structured_output")?.DeepClone();
        }
        return null;
    }
}
