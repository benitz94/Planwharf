// Launch registration for Planwharf's own MCP server (job 25).
// Owner MCP servers stay out of this config. A program is MCP-capable only when
// its launch flags were inspected and a fake program can start this server.
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class WorkerMcpLaunch
{
    public sealed record ServerSpec(string Command, IReadOnlyList<string> Args);

    public sealed record Registration(
        string Adapter,
        string Transport,
        bool OwnerMcpEnabled,
        bool PlanwharfTools,
        ServerSpec? Server,
        IReadOnlyList<string> ExtraArgs,
        string Reason);

    // Adapters whose current launch spec accepts an MCP server on the command line.
    // grok, antigravity, api, and configured-cli stay on the JSON result file.
    public static Registration Register(string adapter, bool ownerMcpEnabled, ServerSpec? server)
    {
        if (adapter is not ("claude" or "codex"))
            return JsonFile(adapter, ownerMcpEnabled, "no verified MCP launch flag");
        if (server is null || string.IsNullOrWhiteSpace(server.Command) || server.Command.Any(c => c < ' '))
            return JsonFile(adapter, ownerMcpEnabled, "mcp server command missing");
        foreach (var arg in server.Args)
            if (arg.Any(c => c is '\r' or '\n' or '\0'))
                return JsonFile(adapter, ownerMcpEnabled, "mcp server command missing");
        var args = adapter == "claude" ? ClaudeArgs(ownerMcpEnabled, server) : CodexArgs(ownerMcpEnabled, server);
        return new Registration(adapter, "mcp", ownerMcpEnabled, true, server, args,
            "fake launch path only; the vendor CLI is not executed here");
    }

    static Registration JsonFile(string adapter, bool ownerMcpEnabled, string reason) =>
        new(adapter, "json-file", ownerMcpEnabled, false, null, [], reason);

    static string[] ClaudeArgs(bool ownerMcpEnabled, ServerSpec server)
    {
        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["planwharf"] = ServerJson(server)
            }
        };
        var json = config.ToJsonString();
        return ownerMcpEnabled
            ? ["--mcp-config", json]
            : ["--strict-mcp-config", "--mcp-config", json];
    }

    static JsonObject ServerJson(ServerSpec server)
    {
        var args = new JsonArray();
        foreach (var arg in server.Args) args.Add((JsonNode)JsonValue.Create(arg)!);
        return new JsonObject { ["command"] = server.Command, ["args"] = args };
    }

    static string[] CodexArgs(bool ownerMcpEnabled, ServerSpec server)
    {
        var list = new List<string>();
        if (!ownerMcpEnabled) { list.Add("-c"); list.Add("mcp_servers={}"); }
        list.Add("-c");
        list.Add("mcp_servers.planwharf.command=" + Toml(server.Command));
        var args = new JsonArray();
        foreach (var arg in server.Args) args.Add((JsonNode)JsonValue.Create(arg)!);
        list.Add("-c");
        list.Add("mcp_servers.planwharf.args=" + args.ToJsonString());
        return list.ToArray();
    }

    static string Toml(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
