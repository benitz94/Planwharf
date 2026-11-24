// Port of engine/api-agent.cjs.
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Planwharf.Engine;

public static class ApiAgent
{
    public sealed record Command(string Executable, IReadOnlyList<string> Args, string Cwd,
        int TimeoutMs = 60000, int MaxBuffer = 65536, bool WindowsHide = true);
    public sealed record CommandReply(int? ExitCode, string Stdout = "", string Stderr = "");
    public sealed record Request(string Url, JsonObject Headers, JsonObject Body, int TimeoutMs, string Redirect = "error");
    public sealed class Dependencies
    {
        public required string Folder { get; init; }
        public required Func<Request, CancellationToken, Task<HttpResponseMessage>> Http { get; init; }
        public Func<Command, CancellationToken, Task<CommandReply>>? RunCommand { get; init; }
        public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
        public Action<JsonObject> Emit { get; init; } = _ => { };
    }
    static Exception Error(string message) => new InvalidOperationException(message);
    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool Truth(JsonNode? n) => StructuredFinal.Truthy(n);
    static JsonNode? Copy(JsonNode? n) => n?.DeepClone();
    static int Number(JsonNode? n) => n!.GetValue<int>();
    static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget != null) current = info.ResolveLinkTarget(true)?.FullName ?? throw Error("No existing workspace parent.");
        }
        return Path.GetFullPath(current);
    }
    public static string WorkspacePath(string root, JsonNode? value, bool writing = false)
    {
        var name = Text(value);
        if (name == null || name.Contains('\0')) throw Error("Invalid workspace path.");
        var basis = RealPath(root);
        var target = Path.GetFullPath(name, basis);
        bool Within(string p)
        {
            var relative = Path.GetRelativePath(basis, p);
            return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
        }
        if (!Within(target)) throw Error("Path is outside the selected workspace.");
        var parts = Path.GetRelativePath(basis, target).Split(['/', '\\']);
        if (writing && parts.Any(p => new[] { ".git", ".codex", ".agents" }.Contains(p.ToLowerInvariant())))
            throw Error("Writing repository or agent control files is not allowed.");
        var existing = target;
        while (!Path.Exists(existing) && new FileInfo(existing).LinkTarget == null)
            existing = Path.GetDirectoryName(existing) ?? throw Error("No existing workspace parent.");
        if (!Within(RealPath(existing))) throw Error("Workspace path resolves outside the selected workspace.");
        return target;
    }
    public static async Task<JsonNode?> ExecuteToolAsync(JsonObject call, JsonObject job, Dependencies dependencies, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call["input"] is not JsonObject input) throw Error("Invalid tool arguments.");
        var name = Text(call["name"]);
        var lead = Truth(job["lead"]);
        if (name == "list_files")
        {
            var output = new JsonArray();
            foreach (var entry in new DirectoryInfo(WorkspacePath(dependencies.Folder, input["path"])).EnumerateFileSystemInfos().Take(1000))
                output.Add((JsonNode)new JsonObject { ["name"] = entry.Name, ["directory"] = entry is DirectoryInfo && entry.LinkTarget == null });
            return output;
        }
        if (name == "read_file")
        {
            var file = WorkspacePath(dependencies.Folder, input["path"]);
            if (new FileInfo(file).Length > 65536) throw Error("File exceeds the 64 KiB tool limit.");
            return JsonValue.Create(await File.ReadAllTextAsync(file, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
        }
        if (name == "write_file")
        {
            if (lead) throw Error("Coordinator cannot write project files.");
            var content = Text(input["content"]);
            if (content == null || Encoding.UTF8.GetByteCount(content) > 1048576) throw Error("Invalid write content or write exceeds 1 MiB.");
            var file = WorkspacePath(dependencies.Folder, input["path"], true);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            WorkspacePath(dependencies.Folder, input["path"], true);
            await File.WriteAllTextAsync(file, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["written"] = Copy(input["path"]) };
        }
        if (name == "write_result")
        {
            if (lead || !Truth(job["resultOutputPath"]) || job["resultIdentity"] is not JsonObject identity)
                throw Error("No structured worker result is assigned to this request.");
            var content = Text(input["content"]);
            if (content == null || Encoding.UTF8.GetByteCount(content) > 1048576) throw Error("Invalid structured result content.");
            var result = JsonNode.Parse(content);
            if (!WorkerResult.ValidateResult(result, identity)) throw Error("Structured result failed identity or schema validation.");
            await File.WriteAllTextAsync(Text(job["resultOutputPath"])!, result!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["recorded"] = true, ["status"] = Copy(result["status"]) };
        }
        if (name == "run_command")
        {
            if (lead || !Truth(job["alwaysApprove"])) throw Error("PERMISSION_DENIED: commands require a worker with YOLO enabled.");
            var command = Text(input["command"]);
            if (string.IsNullOrEmpty(command) || command.IndexOfAny(['\r', '\n', '\0']) >= 0 || input["args"] is not JsonArray args || args.Any(a => Text(a) == null || Text(a)!.Contains('\0')))
                throw Error("Command requires an executable and string arguments.");
            if (dependencies.RunCommand == null) throw Error("Command execution is not configured.");
            var reply = await dependencies.RunCommand(new Command(command, args.Select(a => Text(a)!).ToArray(), dependencies.Folder), cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["exitCode"] = reply.ExitCode, ["stdout"] = Tail(reply.Stdout, 24000), ["stderr"] = Tail(reply.Stderr, 8000) };
        }
        throw Error("Unknown API tool: " + name);
    }
    static string Tail(string value, int max) => value.Length <= max ? value : value[^max..];
    public static async Task<JsonNode?> ResponseJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (!response.IsSuccessStatusCode) throw Error("API HTTP " + (int)response.StatusCode);
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > 4194304) throw Error("PROVIDER_PROTOCOL_CHANGED: API response exceeds 4 MiB.");
            bytes.Write(buffer, 0, count);
        }
        try { return JsonNode.Parse(Encoding.UTF8.GetString(bytes.ToArray())); }
        catch (JsonException) { throw Error("PROVIDER_PROTOCOL_CHANGED: API returned invalid JSON."); }
    }
    static JsonArray Definitions()
    {
        var tools = new JsonArray();
        void Add(string name, string description, string[] fields)
        {
            var properties = new JsonObject(); var required = new JsonArray();
            foreach (var field in fields)
            {
                properties[field] = field == "args" ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } } : new JsonObject { ["type"] = "string" };
                required.Add((JsonNode)JsonValue.Create(field)!);
            }
            tools.Add((JsonNode)new JsonObject { ["name"] = name, ["description"] = description,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false } });
        }
        Add("list_files", "List entries in a workspace directory.", ["path"]);
        Add("read_file", "Read a UTF-8 workspace file, at most 64 KiB.", ["path"]);
        Add("write_file", "Write a UTF-8 workspace file. Not allowed for a coordinator or outside the workspace.", ["path", "content"]);
        Add("write_result", "Submit the required structured Planwharf result JSON to its assigned output file. Include the exact identity fields from the prompt.", ["content"]);
        Add("run_command", "Run a native executable with an argument array in the workspace. Available only to workers with YOLO enabled. No shell command string.", ["command", "args"]);
        return tools;
    }
    static bool Safe(JsonNode? n, out double value)
    {
        value = 0;
        return n is JsonValue v && v.TryGetValue<double>(out value) && double.IsFinite(value) && value >= 0 && value <= 9007199254740991 && Math.Truncate(value) == value;
    }
    public static async Task<JsonObject> RunAgentAsync(JsonObject job, Dependencies dependencies, CancellationToken cancellationToken = default)
    {
        var model = Text(job["model"]); var prompt = Text(job["prompt"]);
        if (string.IsNullOrEmpty(model) || prompt == null || job.ContainsKey("alwaysApprove") && (job["alwaysApprove"] is not JsonValue approval || !approval.TryGetValue<bool>(out _)))
            throw Error("Invalid API job.");
        var config = job["config"]!.AsObject();
        var headers = ExternalAdapters.ApiHeaders(config, dependencies.Environment);
        var advisory = Truth(job["advisory"]); var lead = Truth(job["lead"]);
        var allowed = Definitions().Where(t => !advisory && (!lead || Text(t!["name"]) is not ("write_file" or "write_result" or "run_command")) &&
            (Truth(job["alwaysApprove"]) || Text(t!["name"]) != "run_command") && (Text(t!["name"]) != "write_result" || Truth(job["resultOutputPath"]))).ToArray();
        var anthropic = Text(config["apiProtocol"]) == "anthropic";
        var messages = new JsonArray((JsonNode)new JsonObject { ["role"] = "user", ["content"] = prompt });
        double inputTotal = 0, outputTotal = 0, cachedTotal = 0; var observed = false;
        for (var turn = 0; turn < Number(config["maxTurns"]); turn++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = new JsonObject { ["model"] = model, ["messages"] = Copy(messages), ["max_tokens"] = Copy(config["maxOutputTokens"]), ["stream"] = false };
            if (!advisory)
            {
                var tools = new JsonArray();
                foreach (var tool in allowed)
                    tools.Add((JsonNode)(anthropic ? new JsonObject { ["name"] = Copy(tool!["name"]), ["description"] = Copy(tool["description"]), ["input_schema"] = Copy(tool["parameters"]) } : new JsonObject { ["type"] = "function", ["function"] = Copy(tool) }));
                body["tools"] = tools;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Number(config["requestTimeoutMs"]));
            using var response = await dependencies.Http(new Request(Text(config["baseUrl"]) + (anthropic ? "/messages" : "/chat/completions"), headers.DeepClone().AsObject(), body, Number(config["requestTimeoutMs"])), deadline.Token).ConfigureAwait(false);
            var data = await ResponseJsonAsync(response, deadline.Token).ConfigureAwait(false);
            if (Truth(data?["model"]) && !JsonNode.DeepEquals(data!["model"], JsonValue.Create(model)))
                throw Error("PROVIDER_MODEL_IDENTITY_CHANGED: API returned a different model ID. Review the configured exact model ID; no substitution was accepted.");
            var usage = data?["usage"];
            if (Safe(usage?[anthropic ? "input_tokens" : "prompt_tokens"], out var input) && Safe(usage?[anthropic ? "output_tokens" : "completion_tokens"], out var output))
            {
                observed = true; inputTotal += input; outputTotal += output;
                if (Safe(anthropic ? usage?["cache_read_input_tokens"] : usage?["prompt_tokens_details"]?["cached_tokens"], out var cached)) cachedTotal += cached;
                dependencies.Emit(new JsonObject { ["type"] = "turn.completed", ["usage"] = new JsonObject { ["input_tokens"] = inputTotal, ["output_tokens"] = outputTotal, ["cached_input_tokens"] = cachedTotal } });
            }
            JsonNode? content; JsonObject assistant; var calls = new List<JsonObject>();
            if (anthropic)
            {
                if (data?["content"] is not JsonArray blocks) throw Error("PROVIDER_PROTOCOL_CHANGED: Anthropic content array missing.");
                if (Text(data["stop_reason"]) == "max_tokens") throw Error("API_OUTPUT_LIMIT: response was truncated.");
                content = JsonValue.Create(string.Join("\n", blocks.Where(b => Text(b?["type"]) == "text").Select(b => Text(b?["text"]) ?? "")));
                calls.AddRange(blocks.Where(b => Text(b?["type"]) == "tool_use").Select(b => new JsonObject { ["id"] = Copy(b!["id"]), ["name"] = Copy(b["name"]), ["input"] = Copy(b["input"]) }));
                assistant = new JsonObject { ["role"] = "assistant", ["content"] = Copy(blocks) };
            }
            else
            {
                var choice = data?["choices"] is JsonArray choices && choices.Count > 0 ? choices[0] : null;
                if (choice?["message"] is not JsonObject message || Text(message["role"]) != "assistant") throw Error("PROVIDER_PROTOCOL_CHANGED: assistant message missing.");
                if (Text(choice["finish_reason"]) is "length" or "content_filter") throw Error("API_OUTPUT_LIMIT: response was truncated or filtered.");
                assistant = message.DeepClone().AsObject(); content = assistant["content"];
                if (assistant.ContainsKey("tool_calls") && assistant["tool_calls"] is not JsonArray) throw Error("PROVIDER_PROTOCOL_CHANGED: tool_calls must be an array.");
                foreach (var call in assistant["tool_calls"] as JsonArray ?? [])
                {
                    JsonNode? arguments;
                    try { arguments = JsonNode.Parse(Text(call?["function"]?["arguments"])!); }
                    catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { throw Error("PROVIDER_PROTOCOL_CHANGED: invalid tool arguments JSON."); }
                    calls.Add(new JsonObject { ["id"] = Copy(call?["id"]), ["name"] = Copy(call?["function"]?["name"]), ["input"] = arguments });
                }
            }
            messages.Add((JsonNode)assistant);
            if (advisory && calls.Count > 0) throw Error("PERMISSION_DENIED: Supervisor responses cannot execute tools.");
            if (calls.Count == 0)
            {
                var answer = Text(content);
                if (answer == null || StructuredFinal.Trim(answer).Length == 0) throw Error("PROVIDER_PROTOCOL_CHANGED: no recognized response.");
                dependencies.Emit(new JsonObject { ["type"] = "item.completed", ["item"] = new JsonObject { ["type"] = "agent_message", ["text"] = answer } });
                return new JsonObject { ["answer"] = answer, ["tokens"] = observed ? new JsonObject { ["input"] = inputTotal, ["output"] = outputTotal, ["cachedInput"] = cachedTotal } : null };
            }
            if (calls.Count > 32 || calls.Any(c => string.IsNullOrEmpty(Text(c["id"])) || Text(c["name"]) == null)) throw Error("PROVIDER_PROTOCOL_CHANGED: invalid or excessive tool calls.");
            var results = new JsonArray();
            foreach (var call in calls)
            {
                dependencies.Emit(new JsonObject { ["type"] = "item.started", ["item"] = new JsonObject { ["type"] = "command_execution", ["command"] = Copy(call["name"]) } });
                JsonNode? result;
                try
                {
                    if (!allowed.Any(t => Text(t!["name"]) == Text(call["name"]))) throw Error("PERMISSION_DENIED: tool is not available in this permission mode.");
                    result = await ExecuteToolAsync(call, job, dependencies, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { result = new JsonObject { ["error"] = e.Message }; }
                var text = result?.ToJsonString() ?? "null";
                if (anthropic) results.Add((JsonNode)new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = Copy(call["id"]), ["content"] = text });
                else messages.Add((JsonNode)new JsonObject { ["role"] = "tool", ["tool_call_id"] = Copy(call["id"]), ["content"] = text });
            }
            if (anthropic) messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = results });
        }
        throw Error("API_TURN_LIMIT: configured maximum tool rounds reached; completion was not verified.");
    }
}
