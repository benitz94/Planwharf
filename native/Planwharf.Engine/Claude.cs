// Port of engine/claude.cjs.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class Claude
{
    public static readonly IReadOnlyList<string> IsolatedArgs =
    [
        "--no-session-persistence",
        "--setting-sources",
        "user",
        "--settings",
        "{\"disableAllHooks\":true,\"autoMemoryEnabled\":false}",
        "--strict-mcp-config",
        "--mcp-config",
        "{\"mcpServers\":{}}",
        "--no-chrome"
    ];

    public static readonly string[] ToolNames = ["mcp", "browser", "hooks", "memory"];

    private static readonly Regex ModelRegex = new(@"^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$", RegexOptions.ECMAScript);

    public static IReadOnlyList<string> ClaudeToolArgs(JsonObject? tools)
    {
        bool On(string name) => tools?[name]?.GetValue<bool>() == true;

        var settingsJson = $"{{\"disableAllHooks\":{(!On("hooks")).ToString().ToLowerInvariant()},\"autoMemoryEnabled\":{On("memory").ToString().ToLowerInvariant()}}}";

        var args = new List<string>
        {
            "--no-session-persistence",
            "--setting-sources",
            "user",
            "--settings",
            settingsJson
        };

        if (!On("mcp"))
        {
            args.Add("--strict-mcp-config");
            args.Add("--mcp-config");
            args.Add("{\"mcpServers\":{}}");
        }

        args.Add(On("browser") ? "--chrome" : "--no-chrome");

        return args;
    }

    public static (bool Ok, string? Reason, IReadOnlyList<JsonObject>? Models) ParseModels(JsonArray? rows)
    {
        if (rows == null)
            return (false, "Claude Code returned no model catalog.", null);

        var models = new List<JsonObject>();
        foreach (var item in rows)
        {
            if (item is not JsonObject row) continue;
            var cli = row["resolvedModel"]?.GetValue<string>() ?? row["value"]?.GetValue<string>();
            if (string.IsNullOrEmpty(cli) || cli.Length > 512 || !ModelRegex.IsMatch(cli)) continue;

            var existing = models.FirstOrDefault(m => m["cli"]?.GetValue<string>() == cli);
            if (existing != null)
            {
                if (row["value"]?.GetValue<string>() != "default" && row.ContainsKey("displayName") && row["displayName"] is JsonValue jv && jv.TryGetValue<string>(out var dn))
                    existing["label"] = dn;
                continue;
            }

            var label = row["displayName"]?.GetValue<string>() ?? cli;
            var modelObj = new JsonObject
            {
                ["cli"] = cli,
                ["label"] = label,
                ["capabilities"] = new JsonObject
                {
                    ["canCode"] = true,
                    ["canUseTools"] = true,
                    ["canInspectFilesystem"] = true,
                    ["canOrchestrate"] = true,
                    ["canResearch"] = false
                }
            };
            models.Add(modelObj);
        }

        return models.Count > 0
            ? (true, null, models)
            : (false, "Claude Code returned no exact model IDs. Aliases were not guessed.", null);
    }

    public static async Task<(bool Ok, string? Reason, IReadOnlyList<JsonObject>? Models)> ListClaudeAsync(
        string? command = null,
        IDictionary<string, string>? env = null,
        Func<string, IReadOnlyList<string>, Task<ProcessResult>>? run = null)
    {
        if (string.IsNullOrEmpty(command))
            return (false, "Claude Code executable not found. Install Claude Code and run claude auth login.", null);

        if (run != null)
        {
            var authRes = await run(command, ["auth", "status"]);
            if (authRes.Status != 0)
                return (false, "Claude Code authentication unavailable. Run claude auth login, then refresh models.", null);

            var streamRes = await run(command, ["-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose"]);
            if (streamRes.Status != 0)
                return (false, "Claude Code model discovery failed or its control protocol changed. No substitute was selected.", null);

            try
            {
                var parsed = JsonNode.Parse(streamRes.Stdout);
                if (parsed is JsonArray arr)
                    return ParseModels(arr);
            }
            catch { }

            return (false, "Claude Code returned no model catalog.", null);
        }

        return (false, "Claude Code authentication unavailable. Run claude auth login, then refresh models.", null);
    }
}
