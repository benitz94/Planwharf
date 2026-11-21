// Stdio MCP server for one worker attempt (job 25). Newline-delimited JSON-RPC.
// Identity comes from the session token on the command line, never from tool arguments.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class WorkerMcpServer
{
    public static int Serve(TextReader input, TextWriter output, TextWriter error, string root, string token)
    {
        var now = Environment.TickCount64;
        var attached = WorkerMcp.Attach(root, token, now);
        if (!attached.Ok)
        {
            error.WriteLine("planwharf-mcp\tattach\t" + attached.Code);
            return 2;
        }
        var call = new WorkerMcp.Call(token, attached.Body!["connectionId"]!.GetValue<string>());
        try
        {
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (line.Length == 0) continue;
                if (line.Length > WorkerMcp.JsonMax)
                {
                    Fail(output, null, -32700, "message too large");
                    continue;
                }
                JsonObject? message;
                try { message = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { Fail(output, null, -32700, "parse error"); continue; }
                if (message is null) { Fail(output, null, -32700, "parse error"); continue; }
                var method = Text(message, "method");
                if (!message.ContainsKey("id")) continue;
                var id = message["id"];
                if (method == "initialize") Ok(output, id, Initialize());
                else if (method == "ping") Ok(output, id, new JsonObject());
                else if (method == "tools/list") Ok(output, id, new JsonObject { ["tools"] = Tools() });
                else if (method == "tools/call") Ok(output, id, Call(root, call, message["params"] as JsonObject));
                else Fail(output, id, -32601, "method not found");
            }
            return 0;
        }
        finally { WorkerMcp.Detach(root, call, Environment.TickCount64); }
    }

    static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "planwharf", ["version"] = "1" }
    };

    static JsonArray Tools()
    {
        var tools = new JsonArray();
        tools.Add((JsonNode)Tool("ask", "Ask the lead, or the owner when forOwner is true.", """{"type":"object","required":["question"],"properties":{"question":{"type":"string"},"forOwner":{"type":"boolean"}}}"""));
        tools.Add((JsonNode)Tool("progress", "Record a short status. This does not finish the work.", """{"type":"object","required":["status"],"properties":{"status":{"type":"string"}}}"""));
        tools.Add((JsonNode)Tool("finish", "Submit the structured result. Field errors come back at once.", """{"type":"object","required":["resultId","attemptId","subTaskId","megaTaskId","status","summary","filesChanged","tests","blockers","nextAction"],"properties":{"resultId":{"type":"string"},"attemptId":{"type":"string"},"subTaskId":{"type":"string"},"megaTaskId":{"type":"string"},"status":{"type":"string"},"summary":{"type":"string"},"filesChanged":{"type":"array"},"tests":{"type":"array"},"blockers":{"type":"array"},"nextAction":{"type":"string"}}}"""));
        tools.Add((JsonNode)Tool("decision", "Ask the owner something only the owner can decide.", """{"type":"object","required":["question"],"properties":{"question":{"type":"string"}}}"""));
        tools.Add((JsonNode)Tool("handoff", "Save notes for the next model on this subtask.", """{"type":"object","required":["notes"],"properties":{"notes":{"type":"string"}}}"""));
        return tools;
    }

    static JsonObject Tool(string name, string description, string schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = JsonNode.Parse(schema)
    };

    static JsonObject Call(string root, WorkerMcp.Call call, JsonObject? parameters)
    {
        if (parameters is null || Text(parameters, "name").Length == 0) return ToolText("malformed", true);
        var name = Text(parameters, "name");
        if (parameters["arguments"] is not null and not JsonObject) return ToolText("malformed", true);
        var arguments = parameters["arguments"] as JsonObject ?? new JsonObject();
        if (name != "finish" && HasAuthority(arguments)) return ToolText("identity-mismatch", true);
        var textKey = name switch { "progress" => "status", "handoff" => "notes", "ask" or "decision" => "question", _ => "" };
        if (textKey.Length > 0 && BadString(arguments, textKey)) return ToolText("malformed", true);
        if (name == "ask" && BadFlag(arguments)) return ToolText("malformed", true);
        var now = Environment.TickCount64;
        WorkerMcp.ToolReply reply;
        if (name == "progress") reply = WorkerMcp.Progress(root, call, TextOrNull(arguments, "status"), now);
        else if (name == "ask") reply = Wait(root, call, arguments, ownerTool: false);
        else if (name == "decision") reply = Wait(root, call, arguments, ownerTool: true);
        else if (name == "handoff") reply = WorkerMcp.Handoff(root, call, TextOrNull(arguments, "notes"), now);
        else if (name == "finish") reply = WorkerMcp.Finish(root, call, arguments, now);
        else return ToolText("unknown-tool", true);
        return ToolText(Describe(reply), !reply.Ok || reply.Code == "pending");
    }

    static WorkerMcp.ToolReply Wait(string root, WorkerMcp.Call call, JsonObject arguments, bool ownerTool)
    {
        var now = Environment.TickCount64;
        var reply = ownerTool
            ? WorkerMcp.Decision(root, call, TextOrNull(arguments, "question"), now)
            : WorkerMcp.Ask(root, call, TextOrNull(arguments, "question"), Flag(arguments, "forOwner"), now);
        while (reply.Code == "pending")
        {
            Thread.Sleep(20);
            now = Environment.TickCount64;
            reply = ownerTool ? WorkerMcp.PollDecision(root, call, now) : WorkerMcp.PollAsk(root, call, now);
        }
        return reply;
    }

    static bool BadString(JsonObject arguments, string key) =>
        arguments.ContainsKey(key) && (arguments[key] is not JsonValue value || !value.TryGetValue<string>(out _));

    static bool BadFlag(JsonObject arguments) =>
        arguments.ContainsKey("forOwner") && (arguments["forOwner"] is not JsonValue value || !value.TryGetValue<bool>(out _));

    static bool Flag(JsonObject arguments, string key)
    {
        if (!arguments.ContainsKey(key)) return false;
        return arguments[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    }

    static bool HasAuthority(JsonObject arguments)
    {
        foreach (var key in new[] { "projectId", "worktree", "token", "store", "storeRoot", "recipient", "connectionId", "attemptId", "resultId", "subTaskId", "megaTaskId", "provider", "model" })
            if (arguments.ContainsKey(key)) return true;
        return false;
    }

    static string Describe(WorkerMcp.ToolReply reply)
    {
        if (reply.Body?["answer"] is JsonValue answer && answer.TryGetValue<string>(out var text) && text.Length > 0) return text;
        if (reply.Code == "invalid" && reply.Body?["fields"] is JsonArray fields)
        {
            var names = new List<string>();
            foreach (var field in fields)
                if (field is JsonValue value && value.TryGetValue<string>(out var name)) names.Add(name);
            return "invalid:" + string.Join(',', names);
        }
        return reply.Ok ? "ok" : reply.Code;
    }

    static JsonObject ToolText(string text, bool isError)
    {
        var content = new JsonArray();
        content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        return new JsonObject { ["content"] = content, ["isError"] = isError };
    }

    static void Ok(TextWriter output, JsonNode? id, JsonObject result) =>
        Write(output, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    static void Fail(TextWriter output, JsonNode? id, int code, string message) =>
        Write(output, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });

    static void Write(TextWriter output, JsonObject message)
    {
        output.Write(message.ToJsonString());
        output.Write('\n');
        output.Flush();
    }

    static string Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    static string? TextOrNull(JsonObject node, string key) => node.ContainsKey(key) ? Text(node, key) : null;
}
