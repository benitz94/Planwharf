// Port of engine/activity-evidence.cjs.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Optional Technical view only. Never infer an operation or validated outcome.
public static class ActivityEvidence
{
    public static class LIMITS
    {
        public const int TailBytes = 32768;
        public const int LineBytes = 16384;
        public const int FieldBytes = 4096;
        public const int CommandBytes = 2048;
        public const int TotalBytes = 16384;
        public const int Entries = 20;

        public const int tailBytes = 32768;
        public const int lineBytes = 16384;
        public const int fieldBytes = 4096;
        public const int commandBytes = 2048;
        public const int totalBytes = 16384;
        public const int entries = 20;
    }

    const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    const RegexOptions JsM = RegexOptions.ECMAScript | RegexOptions.CultureInvariant | RegexOptions.Multiline;
    // JavaScript `\s` without the `u` flag. ECMAScript mode in .NET only knows the ASCII ones.
    const string JsWs = "\\t\\n\\v\\f\\r \\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000\\ufeff";

    static readonly Regex CrLf = new(@"\r\n?", Js);
    static readonly Regex PrivateKey = new(@"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|$)", Js);
    static readonly Regex Osc = new(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)", Js);
    static readonly Regex Csi = new(@"\x1b\[[0-?]*[ -/]*[@-~]", Js);
    static readonly Regex Control = new(@"[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]", Js);
    // Matched against ASCII-lowered text. .NET IgnoreCase would fold the Kelvin sign onto `k`; JavaScript's `i` does not.
    static readonly Regex Bearer = new(@"\b(bearer[" + JsWs + @"]+)[^" + JsWs + @",;""']+", Js);
    static readonly Regex Assignment = new(
        "((?:\\\\?[\"'])?(?:api[_-]?key|access[_-]?(?:key|token)|refresh[_-]?token|token|password|secret|authorization|cookie)(?:\\\\?[\"'])?[" + JsWs + "]*[=:][" + JsWs + "]*)(?:\\\\?\"[^\"\\n]*\\\\?\"|\\\\?'[^'\\n]*\\\\?'|[^" + JsWs + ",;&]+)",
        Js);
    static readonly Regex CliFlags = new(
        "(--(?:api[-_]?key|access[-_]?token|token|password|secret)(?:=|[" + JsWs + "]+))(?:\"[^\"\\n]*\"|'[^'\\n]*'|[^" + JsWs + "]+)",
        Js);
    static readonly Regex KnownTokens = new(@"\b(?:sk-[\w-]{8,}|gh[pousr]_[\w]{8,}|github_pat_[\w]+)\b", Js);
    static readonly Regex UrlCredentials = new("(https?:\\/\\/)[^" + JsWs + "/@]+:[^" + JsWs + "/@]+@", Js);
    static readonly Regex EncodedValue = new(@"^[A-Za-z0-9+/=]{48,}$", JsM);
    static readonly Regex StructuredHint = new(@"^\s*[\[{]|""(?:event|step_update|tool_name|tool_info|item|type)""\s*:", Js);

    public static string Redact(string? value)
    {
        var text = CrLf.Replace(value ?? "", "\n");
        text = PrivateKey.Replace(text, "[REDACTED PRIVATE KEY]");
        text = Osc.Replace(text, "");
        text = Csi.Replace(text, "");
        text = Control.Replace(text, "");
        text = ReplaceInsensitive(text, Bearer, "[REDACTED]");
        text = ReplaceInsensitive(text, Assignment, "[REDACTED]");
        text = ReplaceInsensitive(text, CliFlags, "[REDACTED]");
        text = KnownTokens.Replace(text, "[REDACTED]");
        text = ReplaceInsensitive(text, UrlCredentials, "[REDACTED]@");
        text = EncodedValue.Replace(text, "[REDACTED ENCODED VALUE]");
        return text.Replace("<", "&lt;").Replace(">", "&gt;");
    }

    // The pattern is written in lower case and applied to ASCII-lowered text, then the kept prefix is copied
    // from the original so "Bearer" and "PASSWORD" keep the case the source used.
    static string ReplaceInsensitive(string text, Regex pattern, string replacement)
    {
        var lowered = AsciiLower(text);
        var matches = pattern.Matches(lowered);
        if (matches.Count == 0) return text;
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (Match match in matches)
        {
            builder.Append(text, cursor, match.Index - cursor);
            var group = match.Groups[1];
            builder.Append(text, group.Index, group.Length);
            builder.Append(replacement);
            cursor = match.Index + match.Length;
        }
        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    static string? TryString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static bool TruthyNode(JsonNode? node)
    {
        if (node is null) return false;
        if (node is not JsonValue value) return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture) is var number && number != 0 && !double.IsNaN(number),
            _ => true
        };
    }

    static bool TryJsInteger(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        number = double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture);
        return double.IsFinite(number) && number == Math.Truncate(number);
    }

    static JsonNode JsonInteger(double number)
    {
        if (number >= int.MinValue && number <= int.MaxValue) return JsonValue.Create((int)number);
        if (number >= long.MinValue && number <= long.MaxValue && (long)number == number) return JsonValue.Create((long)number);
        return JsonValue.Create(number);
    }

    static string AsciiLower(string text) => string.Create(text.Length, text, (span, source) =>
    {
        for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

    // JavaScript's default argument applies only when the caller omits context. An explicit null throws.
    public static JsonObject TechnicalEvidence(string? tail) => TechnicalEvidence(tail, new JsonObject());

    public static JsonObject TechnicalEvidence(string? tail, JsonObject? context)
    {
        if (context is null) throw new InvalidOperationException("Cannot read properties of null (reading 'adapter')");
        var truncated = false;
        var bytes = 0;
        var entries = new List<JsonObject>();
        var warningSet = new List<string>();

        void Warn(string message)
        {
            if (!warningSet.Contains(message)) warningSet.Add(message);
        }

        string Field(string? value, int maximum = LIMITS.FieldBytes)
        {
            var safe = Redact(value);
            var encoded = Encoding.UTF8.GetBytes(safe);
            if (encoded.Length <= maximum) return safe;
            truncated = true;
            const string marker = "\n[excerpt truncated]";
            var markerLen = Encoding.UTF8.GetByteCount(marker);
            var maxBytes = Math.Max(0, maximum - markerLen);
            var decoded = Encoding.UTF8.GetString(encoded, 0, maxBytes);
            if (decoded.EndsWith('\uFFFD')) decoded = decoded[..^1];
            return decoded + marker;
        }

        void Append(JsonObject entry)
        {
            var size = Encoding.UTF8.GetByteCount(entry.ToJsonString());
            entries.Add(entry);
            bytes += size;
            while (entries.Count > LIMITS.Entries || bytes > LIMITS.TotalBytes)
            {
                var removed = entries[0];
                entries.RemoveAt(0);
                bytes -= Encoding.UTF8.GetByteCount(removed.ToJsonString());
                truncated = true;
            }
        }

        var adapterNode = context.TryGetPropertyValue("adapter", out var adapterValue) ? adapterValue : null;
        var providerNode = context.TryGetPropertyValue("provider", out var providerValue) ? providerValue : null;
        var chosen = TruthyNode(adapterNode) ? adapterNode : providerNode;
        var adapter = TruthyNode(chosen) ? TryString(chosen) ?? "\0" : null;
        string[] knownAdapters = ["codex", "api", "antigravity", "grok", "claude"];
        var known = adapter is not null && knownAdapters.Contains(adapter);
        if (!known) Warn(Strings.T("Adapter identity is unknown; protocol excerpts are not verified provider evidence."));

        var rawBytes = Encoding.UTF8.GetBytes(tail ?? "");
        if (rawBytes.Length > LIMITS.TailBytes)
        {
            var sliceStart = rawBytes.Length - LIMITS.TailBytes;
            var sub = new byte[LIMITS.TailBytes];
            Array.Copy(rawBytes, sliceStart, sub, 0, LIMITS.TailBytes);
            truncated = true;
            var newline = Array.IndexOf(sub, (byte)10);
            if (newline < 0) rawBytes = Array.Empty<byte>();
            else
            {
                var remaining = sub.Length - (newline + 1);
                rawBytes = new byte[remaining];
                Array.Copy(sub, newline + 1, rawBytes, 0, remaining);
            }
        }

        var plain = new List<string>();
        void FlushPlain()
        {
            if (plain.Count == 0) return;
            var joined = string.Join("\n", plain);
            plain.Clear();
            var output = Field(joined);
            if (!string.IsNullOrEmpty(output))
            {
                Append(new JsonObject
                {
                    ["protocol"] = "plain",
                    ["provenance"] = "reported",
                    ["output"] = output
                });
            }
        }

        var rawString = Encoding.UTF8.GetString(rawBytes);
        var lines = rawString.Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (plain.Count > 0) plain.Add("");
                continue;
            }

            if (Encoding.UTF8.GetByteCount(line) > LIMITS.LineBytes)
            {
                FlushPlain();
                truncated = true;
                Warn(Strings.T("An oversized source record was omitted."));
                continue;
            }

            JsonNode? parsed = null;
            var parsedOk = false;
            try
            {
                parsed = JsonNode.Parse(line);
                parsedOk = true;
            }
            catch
            {
                // parsing failed
            }

            if (!parsedOk || parsed is not JsonObject)
            {
                if (parsedOk)
                {
                    FlushPlain();
                    Warn(Strings.T("Unknown structured evidence was omitted."));
                }
                else if (StructuredHint.IsMatch(line))
                {
                    FlushPlain();
                    Warn(Strings.T("Malformed structured evidence was omitted."));
                }
                else plain.Add(line);
                continue;
            }

            var row = (JsonObject)parsed;

            FlushPlain();

            var item = row["item"] as JsonObject;
            var step = row["step_update"] as JsonObject;
            JsonObject? entry = null;

            var rowType = TryString(row["type"]);
            var itemType = TryString(item?["type"]);
            string[] itemEventTypes = ["item.started", "item.completed", "item.updated"];

            if (item is not null && itemType == "command_execution" && rowType is not null && itemEventTypes.Contains(rowType))
            {
                if (known && adapter != "codex" && adapter != "api")
                {
                    Warn(Strings.T("Evidence protocol does not match the configured adapter; record omitted."));
                    continue;
                }

                entry = new JsonObject
                {
                    ["protocol"] = adapter == "api" ? "planwharf-api-bridge" : "item-events",
                    ["provenance"] = known ? "observed" : "unknown",
                    ["eventType"] = rowType
                };

                if (item.TryGetPropertyValue("command", out var cmdNode) && cmdNode is JsonValue cmdVal && cmdVal.TryGetValue<string>(out var cmdStr))
                {
                    entry["command"] = Field(cmdStr, LIMITS.CommandBytes);
                }

                if (item.TryGetPropertyValue("aggregated_output", out var outNode) && outNode is JsonValue outVal && outVal.TryGetValue<string>(out var outStr))
                {
                    entry["output"] = Field(outStr);
                }

                if (item.TryGetPropertyValue("exit_code", out var codeNode) && TryJsInteger(codeNode, out var codeNumber))
                {
                    entry["exitCode"] = JsonInteger(codeNumber);
                }

                if (adapter == "api")
                {
                    Warn(Strings.T("API bridge command field may contain only a tool name; arguments and output may be unavailable."));
                }
            }
            else if (step is not null && step.TryGetPropertyValue("step_type", out var stNode) &&
                     stNode is JsonValue stVal && stVal.TryGetValue<string>(out var stepType) &&
                     (stepType == "tool" || stepType == "tool_call"))
            {
                if (known && adapter != "antigravity")
                {
                    Warn(Strings.T("Evidence protocol does not match the configured adapter; record omitted."));
                    continue;
                }

                entry = new JsonObject
                {
                    ["protocol"] = "antigravity-step",
                    ["provenance"] = known ? "observed" : "unknown",
                    ["eventType"] = "step_update"
                };

                var toolName = TryString(step["tool_name"]) ?? TryString((step["tool_info"] as JsonObject)?["name"]);
                if (toolName is not null) entry["tool"] = Field(toolName, 120);

                var stateStr = TryString(step["state"]);
                if (stateStr is not null) entry["state"] = Field(stateStr, 80);

                string[] cmdTools = ["run_command", "exec_command", "command_execution", "shell", "terminal", "bash"];
                if (toolName is not null && cmdTools.Contains(toolName))
                {
                    var parameters = (step["tool_info"] as JsonObject)?["parameters"] as JsonObject ?? step["parameters"] as JsonObject;
                    var command = parameters?["CommandLine"]?.GetValue<string>() ??
                                  parameters?["command"]?.GetValue<string>() ??
                                  parameters?["cmd"]?.GetValue<string>();
                    if (command is not null) entry["command"] = Field(command, LIMITS.CommandBytes);
                }

                Warn(Strings.T("Antigravity excerpts contain retained tool command/state metadata only; detailed output is not verified."));
            }
            else if (adapter == "claude")
            {
                var block = row["content_block"] as JsonObject ?? (row["event"] as JsonObject)?["content_block"] as JsonObject;
                JsonObject? tool = null;
                if (block is not null && TryString(block["type"]) == "tool_use")
                {
                    tool = block;
                }
                else if (rowType == "assistant" && (row["message"] as JsonObject)?["content"] is JsonArray contentArr)
                {
                    foreach (var part in contentArr)
                    {
                        if (part is JsonObject partObj && TryString(partObj["type"]) == "tool_use")
                        {
                            tool = partObj;
                            break;
                        }
                    }
                }

                var toolName = TryString(tool?["name"]);
                if (tool is null || string.IsNullOrEmpty(toolName))
                {
                    Warn(Strings.T("Unsupported structured payload omitted; raw source content is not displayed."));
                    continue;
                }

                entry = new JsonObject
                {
                    ["protocol"] = "message-events",
                    ["provenance"] = "observed",
                    ["eventType"] = Field(rowType, 80),
                    ["tool"] = Field(toolName, 120)
                };

                if ((toolName == "Bash" || toolName == "PowerShell") &&
                    (tool["input"] as JsonObject)?["command"]?.GetValue<string>() is string cmd)
                {
                    entry["command"] = Field(cmd, LIMITS.CommandBytes);
                }

                Warn(Strings.T("Claude Code excerpts contain retained tool metadata only; detailed output is not verified."));
            }
            else
            {
                Warn(Strings.T("Unsupported structured payload omitted; raw source content is not displayed."));
                continue;
            }

            if (row.TryGetPropertyValue("timestamp", out var tsNode) &&
                tsNode is JsonValue tsVal && tsVal.TryGetValue<string>(out var tsStr) &&
                DateTimeOffset.TryParse(tsStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                entry["sourceTimestamp"] = Field(tsStr, 80);
            }

            Append(entry);
        }

        FlushPlain();

        if (truncated) Warn(Strings.T("Technical evidence is a bounded truncated excerpt, not the full source log."));
        if (entries.Count == 0) Warn(Strings.T("No supported original technical evidence is retained for this assignment."));
        Warn(Strings.T("Only the supplied retained assignment tail is available; earlier source records may be absent."));
        Warn(Strings.T("Reported output and zero exit codes do not establish validated success."));

        var entriesArr = new JsonArray();
        foreach (var e in entries) entriesArr.Add((JsonNode)e);

        var warningsArr = new JsonArray();
        foreach (var w in warningSet) warningsArr.Add((JsonNode)JsonValue.Create(w));

        return new JsonObject
        {
            ["entries"] = entriesArr,
            ["truncated"] = truncated,
            ["warnings"] = warningsArr
        };
    }
}
