using System.Text.Json.Nodes;

namespace Planwharf.Native.Core;

public sealed record ModelToolsView(bool SameAsProject, IReadOnlyDictionary<string, bool> Tools)
{
    public static readonly string[] Names = ["mcp", "browser", "hooks", "memory"];

    public static ModelToolsView Read(JsonNode? snapshot, string projectId, string modelId)
    {
        var settings = snapshot?["projectToolSettings"]?[projectId];
        var own = settings?["models"]?[modelId];
        var chosen = own ?? settings?["default"] ?? snapshot?["projectTools"]?[projectId];
        return new(own == null, Names.ToDictionary(name => name,
            name => chosen?[name] is JsonValue value && value.TryGetValue<bool>(out var on) && on));
    }
}
