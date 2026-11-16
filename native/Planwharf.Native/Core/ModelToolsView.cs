using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record ModelToolsView(bool SameAsProject, IReadOnlyDictionary<string, bool> Tools)
{
    public static readonly string[] Names = ["mcp", "browser", "hooks", "memory"];

    public static ModelToolsView Read(JsonNode? snapshot, string projectId, string modelId)
    {
        var settings = snapshot?["projectToolSettings"] is JsonObject all ? all[projectId] as JsonObject : null;
        var models = settings?["models"] as JsonObject;
        var own = models != null && models.TryGetPropertyValue(modelId, out var row) ? row as JsonObject : null;
        var defaults = settings?["default"] as JsonObject
            ?? (snapshot?["projectTools"] is JsonObject flat ? flat[projectId] as JsonObject : null);
        var chosen = own ?? defaults;
        return new(own == null, Names.ToDictionary(name => name,
            name => chosen?[name] is JsonValue value && value.TryGetValue<bool>(out var on) && on));
    }
}
