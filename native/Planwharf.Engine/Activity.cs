// Port of engine/activity.cjs.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static class Activity
{
    private static string Short(string? text)
    {
        var s = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        return s.Length > 260 ? s[..260] : s;
    }

    private static string FileName(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var normalized = value.Replace('\\', '/');
        var idx = normalized.LastIndexOf('/');
        return idx >= 0 ? normalized[(idx + 1)..] : normalized;
    }

    public static string CommandActivity(string? command)
    {
        var text = (command ?? "").Trim();
        var filesMatch = Regex.Matches(text, @"(?:[\w./\\-]+\.(?:test\.|spec\.)?[a-z]{1,5})\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        var files = filesMatch.Select(m => FileName(m.Value)).ToList();
        var target = files.FirstOrDefault(f => Regex.IsMatch(f, "test|spec|verify", RegexOptions.IgnoreCase)) ?? (files.Count > 0 ? files[0] : null);

        if (Regex.IsMatch(text, @"^(Get-Content|cat|type|head|tail)\s", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Reading {target ?? "a project file"}.";

        if (Regex.IsMatch(text, @"^(rg|grep|findstr|Select-String)(?:\s|$)", RegexOptions.ECMAScript))
            return $"Searching project files{(target != null ? $" near {target}" : "")}.";

        if (Regex.IsMatch(text, @"^(Get-ChildItem|ls|dir|find)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Inspecting project folders.";

        if (Regex.IsMatch(text, @"^(?:pytest|unittest|vitest|jest)(?:\s|$)|^(?:npm|pnpm|yarn|cargo|dotnet)\s+(?:run\s+)?test(?:\s|$)|^node\s+--test(?:\s|$)|^(?:(?:node|python|python3|pwsh)\s+)?(?:[\w.\/-]+[\\/])?(?:(?:test|verify)[\w.-]*\.(?:cjs|js|py|ps1)|[\w.-]+\.(?:test|spec)\.[\w]+)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Running tests{(target != null ? $" for {target}" : "")}.";

        if (Regex.IsMatch(text, @"^(build|compile|csc|tsc|msbuild)(?:\s|$)|^(?:npm|pnpm|yarn)\s+(?:run\s+)?build(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Building the application{(target != null ? $" using {target}" : "")}.";

        if (Regex.IsMatch(text, @"^(Set-Content|Out-File|Add-Content|tee)\s", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Writing {target ?? "a project file"}.";

        if (Regex.IsMatch(text, @"^git\s+(?:diff|status|log|show)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Reviewing source changes and Git history.";

        if (Regex.IsMatch(text, @"^(npm|pnpm|yarn|pip)\s+(?:install|add)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Installing project dependencies.";

        return "Running a project command.";
    }

    public static string ToolActivity(string? name, JsonNode? input = null)
    {
        JsonObject? inputObj = null;
        if (input is JsonObject o) inputObj = o;
        else if (input is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { inputObj = JsonNode.Parse(s) as JsonObject; } catch { }
        }

        var pathVal = inputObj?["AbsolutePath"]?.GetValue<string>() ??
                      inputObj?["TargetFile"]?.GetValue<string>() ??
                      inputObj?["file_path"]?.GetValue<string>() ??
                      inputObj?["path"]?.GetValue<string>() ??
                      inputObj?["file"]?.GetValue<string>() ??
                      inputObj?["filename"]?.GetValue<string>();

        var target = FileName(pathVal);
        var n = name ?? "";

        if (Regex.IsMatch(n, @"^Read$|view_file|read_file|read_text|open_file", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Reading {(!string.IsNullOrEmpty(target) ? target : "a project file")}.";

        if (Regex.IsMatch(n, @"^(?:Write|Edit|MultiEdit|NotebookEdit)$|write_to_file|write_file|replace_file|edit_file|apply_patch|replace_string|multi_replace", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
        {
            var patch = inputObj?["patch"]?.GetValue<string>() ?? inputObj?["input"]?.GetValue<string>() ?? inputObj?["CodeContent"]?.GetValue<string>() ?? "";
            var patchMatch = Regex.Match(patch, @"\*\*\* (?:Update|Add) File:\s*([^\r\n]+)", RegexOptions.ECMAScript);
            var patchTarget = patchMatch.Success ? FileName(patchMatch.Groups[1].Value) : "";
            var chosen = !string.IsNullOrEmpty(target) ? target : patchTarget;
            return $"Writing project files{(!string.IsNullOrEmpty(chosen) ? $": {chosen}" : "")}.";
        }

        if (Regex.IsMatch(n, @"run_command|exec_command|command_execution|shell|terminal|bash", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
        {
            var cmd = inputObj?["CommandLine"]?.GetValue<string>() ?? inputObj?["command"]?.GetValue<string>() ?? inputObj?["cmd"]?.GetValue<string>() ?? "";
            return CommandActivity(cmd);
        }

        if (Regex.IsMatch(n, @"command_status", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Checking the result of a running command.";

        if (Regex.IsMatch(n, @"search|grep", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Searching project files{(!string.IsNullOrEmpty(target) ? $" near {target}" : "")}.";

        if (Regex.IsMatch(n, @"^Glob$|list_dir|list_files|find_by_name", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Inspecting project folders.";

        if (Regex.IsMatch(n, @"browser|screenshot", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Checking the application in the browser.";

        if (Regex.IsMatch(n, @"web|fetch|browse", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Reading documentation and online sources.";

        if (Regex.IsMatch(n, @"finish", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return "Preparing the final work report.";

        return "Using a project tool.";
    }

    public static string PlainActivity(string? text)
    {
        var line = Short(text);
        if (string.IsNullOrEmpty(line) || Regex.IsMatch(line, @"[{}]|conversation_id|step_index|tool_info|""event""|""type""", RegexOptions.ECMAScript))
            return "";

        if (Regex.IsMatch(line, @"\b[1-9]\d*\s+failed\b|\bfailed\s*:\s*[1-9]|^(?:error[:\s]|FAIL\b|Traceback\b)|\bhit an error\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Hit an error: {line}";

        if (Regex.IsMatch(line, @"\b\d+\s+(?:passed|passing)|^PASS[:\s]|^OK:", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Test output: {line}";

        if (Regex.IsMatch(line, @"\b(writing|wrote|created|creating|saving|editing)\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return $"Writing project files: {line}";

        if (Regex.IsMatch(line, @"\b(reading|inspecting|reviewing|searching|testing|running tests|building)\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript))
            return line;

        return $"Worker update: {line}";
    }

    public static string EventActivity(JsonObject row)
    {
        if (row["step_update"] is JsonObject step)
        {
            var stepType = step["step_type"]?.GetValue<string>();
            if (stepType is "tool" or "tool_call")
            {
                var toolName = step["tool_name"]?.GetValue<string>() ?? step["tool_info"]?["name"]?.GetValue<string>() ?? "";
                var toolParams = step["tool_info"]?["parameters"] ?? step["parameters"];
                var action = ToolActivity(toolName, toolParams);
                var state = step["state"]?.GetValue<string>();
                if (state is "ERROR" or "FAILED")
                    return $"Hit an error while {char.ToLowerInvariant(action[0]) + action[1..]}";
                return state == "DONE" ? $"Finished {char.ToLowerInvariant(action[0]) + action[1..]}" : action;
            }
            if (stepType == "agent_response")
            {
                var delta = step["text_delta"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(delta)) return PlainActivity(delta);
                if (step["state"]?.GetValue<string>() == "ACTIVE") return "Preparing a response about the assigned task.";
                return "";
            }
        }

        if (row["item"] is JsonObject item)
        {
            var itemType = item["type"]?.GetValue<string>();
            if (itemType == "command_execution")
            {
                if (item["exit_code"] is JsonNode ecNode && ecNode.GetValue<int>() != 0)
                    return "Hit an error while running a project command.";
                var action = CommandActivity(item["command"]?.GetValue<string>());
                return row["type"]?.GetValue<string>() == "item.completed" ? $"Finished {char.ToLowerInvariant(action[0]) + action[1..]}" : action;
            }
            if (itemType == "file_change")
            {
                var changes = (item["changes"] as JsonArray)?.OfType<JsonObject>().Select(c => FileName(c["path"]?.GetValue<string>())).Where(p => !string.IsNullOrEmpty(p)).ToList();
                return $"Writing project files{(changes != null && changes.Count > 0 ? ": " + string.Join(", ", changes) : "")}.";
            }
            if (itemType == "agent_message")
                return PlainActivity(item["text"]?.GetValue<string>());
        }

        var block = row["content_block"] as JsonObject ?? (row["event"] as JsonObject)?["content_block"] as JsonObject;
        if (block?["type"]?.GetValue<string>() == "tool_use")
            return ToolActivity(block["name"]?.GetValue<string>(), block["input"]);

        if (row["type"]?.GetValue<string>() == "assistant" && (row["message"] as JsonObject)?["content"] is JsonArray contentArr)
        {
            var tool = contentArr.OfType<JsonObject>().FirstOrDefault(p => p["type"]?.GetValue<string>() == "tool_use");
            if (tool != null) return ToolActivity(tool["name"]?.GetValue<string>(), tool["input"]);
            var texts = contentArr.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "text").Select(p => PlainActivity(p["text"]?.GetValue<string>())).Where(s => !string.IsNullOrEmpty(s));
            return string.Join(" ", texts);
        }

        if (row["event"]?.GetValue<string>() == "result" && row["result"] is JsonObject resObj)
            return resObj["status"]?.GetValue<string>() == "SUCCESS" ? "Worker finished and returned its report." : "Worker reported an unsuccessful result.";

        if (row["type"]?.GetValue<string>() == "result")
            return row["is_error"]?.GetValue<bool>() == true ? "Worker reported an error." : "Worker returned its final response.";

        if (row["event"]?.GetValue<string>() == "init" || row["type"]?.GetValue<string>() == "thread.started")
            return "Worker connected. Starting the assigned task.";

        return "";
    }

    public static JsonObject ActivityCapabilities(JsonObject? context = null)
    {
        var adapter = context?["adapter"]?.GetValue<string>() ?? context?["provider"]?.GetValue<string>();
        bool api = adapter == "api";
        bool isLead = context?["lead"]?.GetValue<bool>() == true;
        bool structured = adapter is "antigravity" or "claude" || api || (isLead && adapter is "codex" or "grok");

        string expectedFormat = structured
            ? (api ? "planwharf-api-bridge" : (adapter is "antigravity" or "claude" ? "stream-json" : (adapter == "codex" ? "json" : "streaming-messages-json")))
            : (adapter is "codex" or "grok" ? "plain" : "unknown");

        return new JsonObject
        {
            ["configuredAdapter"] = ActivityEvents.Sanitize(adapter ?? "unknown", 80),
            ["expectedFormat"] = expectedFormat,
            ["detailedEventsExpected"] = structured,
            ["liveDeliveryVerified"] = false,
            ["validatedResults"] = false
        };
    }

    private static string Classify(string message)
    {
        if (Regex.IsMatch(message, @"^(?:Hit an error|Worker reported an (?:error|unsuccessful))")) return "warning";
        if (Regex.IsMatch(message, @"^(?:Finished )?(?:Reading|reading|Searching|searching|Inspecting|inspecting)")) return "read";
        if (Regex.IsMatch(message, @"^(?:Finished )?(?:Writing|writing)")) return "working";
        if (Regex.IsMatch(message, @"^(?:Finished )?(?:Running tests|running tests)|^Test output:")) return "test";
        if (Regex.IsMatch(message, @"^(?:Finished )?(?:Building|building)")) return "build";
        if (Regex.IsMatch(message, @"^(?:Finished )?(?:Reviewing|reviewing)")) return "review";
        if (Regex.IsMatch(message, @"^(?:Worker returned|Worker finished)")) return "report";
        return "unknown";
    }

    private static JsonObject BaseEvent(JsonObject context, string protocol, string message, string provenance)
    {
        var prov = context["providerId"]?.GetValue<string>() ?? context["provider"]?.GetValue<string>() ?? "unknown";
        var adap = context["adapter"]?.GetValue<string>() ?? context["provider"]?.GetValue<string>() ?? "unknown";
        var mod = context["model"]?.GetValue<string>() ?? "";

        return new JsonObject
        {
            ["provider"] = ActivityEvents.Sanitize(prov, 80),
            ["adapter"] = ActivityEvents.Sanitize(adap, 80),
            ["model"] = ActivityEvents.Sanitize(mod, 120),
            ["protocol"] = protocol,
            ["classification"] = Classify(message),
            ["provenance"] = provenance,
            ["state"] = "observed",
            ["validated"] = false,
            ["friendly"] = ActivityEvents.Sanitize(message),
            ["technical"] = new JsonObject { ["protocol"] = protocol }
        };
    }

    private static JsonObject UnknownEvent(JsonObject context, string protocol = "unknown", string eventType = "")
    {
        var res = BaseEvent(context, protocol, "Unclassified worker activity.", "unknown");
        res["state"] = "unknown";
        if (!string.IsNullOrEmpty(eventType))
            ((JsonObject)res["technical"]!)["eventType"] = ActivityEvents.Sanitize(eventType, 80);
        return res;
    }

    public static IReadOnlyList<JsonObject> ActivityEventsList(string? tail, JsonObject? context = null)
    {
        var ctx = context ?? new JsonObject();
        var events = new List<JsonObject>();
        var input = tail ?? "";
        var bounded = input.Length > ActivityEvents.Limits.Tail ? input[^ActivityEvents.Limits.Tail..] : input;
        if (input.Length > ActivityEvents.Limits.Tail)
        {
            var idx = bounded.IndexOf('\n');
            if (idx >= 0) bounded = bounded[(idx + 1)..];
        }

        foreach (var line in bounded.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.Length > ActivityEvents.Limits.Line)
            {
                events.Add(UnknownEvent(ctx));
                continue;
            }

            JsonObject? row = null;
            try { row = JsonNode.Parse(trimmed) as JsonObject; } catch { }

            if (row == null)
            {
                var looksLikeJson = Regex.IsMatch(trimmed, @"^[\s]*[\[{]|""(?:event|step_update|tool_name|tool_info)""\s*:");
                if (looksLikeJson)
                {
                    events.Add(UnknownEvent(ctx));
                }
                else
                {
                    var plainMsg = PlainActivity(trimmed);
                    if (!string.IsNullOrEmpty(plainMsg))
                    {
                        var ev = BaseEvent(ctx, "plain", plainMsg, "observed");
                        ev["technical"] = new JsonObject { ["protocol"] = "plain", ["rawLength"] = trimmed.Length };
                        events.Add(ev);
                    }
                }
                continue;
            }

            var step = row["step_update"] as JsonObject;
            var item = row["item"] as JsonObject;
            var block = row["content_block"] ?? (row["event"] as JsonObject)?["content_block"];

            string protocol = step != null ? "antigravity-step" :
                              item != null || row["type"]?.GetValue<string>() == "thread.started" || Regex.IsMatch(row["type"]?.GetValue<string>() ?? "", @"^turn\.") ? "item-events" :
                              block != null || row["type"]?.GetValue<string>() is "assistant" or "result" ? "message-events" :
                              row["event"]?.GetValue<string>() is "result" or "init" ? "antigravity-result" : "unknown";

            var msg = EventActivity(row);
            if (row["type"]?.GetValue<string>() is "turn.failed" or "error") msg = "Worker reported an error.";
            if (string.IsNullOrEmpty(msg))
            {
                events.Add(UnknownEvent(ctx, protocol, row["type"]?.GetValue<string>() ?? row["event"]?.GetValue<string>() ?? ""));
                continue;
            }

            bool reported = step?["step_type"]?.GetValue<string>() == "agent_response" ||
                            item?["type"]?.GetValue<string>() == "agent_message" ||
                            row["type"]?.GetValue<string>() == "result" ||
                            row["event"]?.GetValue<string>() == "result" ||
                            (row["type"]?.GetValue<string>() == "assistant" &&
                             !((row["message"] as JsonObject)?["content"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().Any(p => p["type"]?.GetValue<string>() == "tool_use"));

            var result = BaseEvent(ctx, protocol, msg, reported ? "reported" : "observed");
            var technical = (JsonObject)result["technical"]!;
            technical["eventType"] = ActivityEvents.Sanitize(row["type"]?.GetValue<string>() ?? row["event"]?.GetValue<string>() ?? "", 80);

            var tool = step?["tool_name"]?.GetValue<string>() ??
                       step?["tool_info"]?["name"]?.GetValue<string>() ??
                       (block as JsonObject)?["name"]?.GetValue<string>() ??
                       ((row["message"] as JsonObject)?["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => p["type"]?.GetValue<string>() == "tool_use")?["name"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(tool)) technical["tool"] = ActivityEvents.Sanitize(tool, 80);
            if (item?["exit_code"] is JsonNode ecNode) technical["exitCode"] = ecNode.GetValue<int>();

            result["state"] = (step?["state"]?.GetValue<string>() is "ERROR" or "FAILED") ||
                              row["type"]?.GetValue<string>() is "turn.failed" or "error" ||
                              row["is_error"]?.GetValue<bool>() == true ||
                              (item?["exit_code"] is JsonNode ec && ec.GetValue<int>() != 0) ? "failed" :
                              (step?["state"]?.GetValue<string>() == "DONE" ||
                               row["type"]?.GetValue<string>() is "item.completed" or "result" ||
                               row["event"]?.GetValue<string>() == "result") ? "finished" : "observed";

            if (row["event"]?.GetValue<string>() == "result" && (row["result"] as JsonObject)?["status"]?.GetValue<string>() is string rStatus && rStatus != "SUCCESS")
                result["state"] = "failed";

            events.Add(result);
        }

        return events;
    }

    public static IReadOnlyList<string> ActivityMessages(string? tail)
    {
        var messages = new List<string>();
        foreach (var ev in ActivityEventsList(tail))
        {
            var friendly = ev["friendly"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(friendly) && (messages.Count == 0 || messages[^1] != friendly))
                messages.Add(friendly);
        }
        return messages;
    }

    public static string InterpretActivity(string? tail, JsonObject? facts, bool? alive)
    {
        var msgs = ActivityMessages(tail);
        var last = msgs.Count > 0 ? msgs[^1] : null;
        if (!string.IsNullOrEmpty(last))
            return alive == false ? $"Stopped. Last recorded action: {last}" : last;
        return alive == true ? "Worker is running. Waiting for a readable activity update." : "";
    }

    public static string WorkerReport(string? tail)
    {
        string report = "";
        foreach (var raw in (tail ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line)) continue;
            try
            {
                var row = JsonNode.Parse(line) as JsonObject;
                if (row == null) continue;
                string? text = null;
                if (row["event"]?.GetValue<string>() == "result")
                    text = (row["result"] as JsonObject)?["response"]?.GetValue<string>();
                else if ((row["item"] as JsonObject)?["type"]?.GetValue<string>() == "agent_message")
                    text = (row["item"] as JsonObject)?["text"]?.GetValue<string>();
                else if (row["type"]?.GetValue<string>() == "assistant" && (row["message"] as JsonObject)?["content"] is JsonArray contentArr)
                    text = string.Join("\n", contentArr.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "text").Select(p => p["text"]?.GetValue<string>()));
                else if (row["type"]?.GetValue<string>() == "result" && row["result"] is JsonValue rv && rv.TryGetValue<string>(out var s))
                    text = s;

                if (!string.IsNullOrWhiteSpace(text)) report = text;
            }
            catch { }
        }
        return report;
    }
}
