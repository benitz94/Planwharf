// Port of engine/invocations.cjs.
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Metadata only: prompts, output, environment and credentials never enter this file.
public static class Invocations
{
    public static InvocationLedger CreateInvocationLedger(string dir, Func<string>? now = null) => new(dir, now);
}

public sealed class InvocationLedger
{
    const double MaxSafeInteger = 9007199254740991d;
    static readonly JsonSerializerOptions JsJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Regex StatusLine = new(@"^STATUS:\s*(COMPLETE|FAILED|BLOCKED|CHECKPOINTED)\b", RegexOptions.ECMAScript | RegexOptions.Multiline);

    readonly Dictionary<string, JsonObject> _records = new(StringComparer.Ordinal);
    readonly List<string> _order = new();
    readonly Func<string> _now;
    readonly string _file;

    public string File => _file;

    public InvocationLedger(string dir, Func<string>? now = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "invocations.jsonl");
        _now = now ?? (() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        if (System.IO.File.Exists(_file)) Load();
        EnsureTrailingNewline();
        // A lead or advisory response cannot survive backend replacement. Worker state is reconciled separately.
        foreach (var key in _order.ToArray())
        {
            var row = _records[key];
            if (Running(row) && Role(row) is "orchestrator" or "reviewer" or "supervisor") Finish(key, "BLOCKED");
        }
    }

    public string Start(JsonObject info)
    {
        ArgumentNullException.ThrowIfNull(info);
        var startTime = _now();
        var record = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("D"),
            ["timestamp"] = startTime,
            ["startTime"] = startTime
        };
        if (info.ContainsKey("role")) record["role"] = info["role"]?.DeepClone();
        record["projectId"] = OrNull(info, "projectId");
        record["megaTaskId"] = OrNull(info, "megaTaskId");
        record["subTaskId"] = OrNull(info, "subTaskId");
        record["attemptId"] = OrNull(info, "attemptId");
        record["provider"] = OrNull(info, "provider");
        record["model"] = OrNull(info, "model");
        record["modelId"] = OrNull(info, "modelId");
        record["accountKey"] = OrNull(info, "accountKey");
        record["accountType"] = JsTruthy(Prop(info, "accountType")) ? info["accountType"]!.DeepClone() : JsonValue.Create("unread");
        if (info.ContainsKey("reason")) record["reason"] = info["reason"]?.DeepClone();
        record["status"] = "RUNNING";
        record["durationMs"] = null;
        record["processExit"] = null;
        return Write(record);
    }

    public void Update(string? id, JsonObject fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (id is null || !_records.TryGetValue(id, out var row) || !Running(row)) return;
        var copy = Copy(row);
        foreach (var key in new[] { "pid", "exitPath", "resultPath" })
            if (fields.ContainsKey(key)) copy[key] = fields[key]?.DeepClone();
        Write(copy);
    }

    public void Finish(string? id, string? status, JsonNode? processExit = null, JsonObject? tokens = null)
    {
        if (id is null || !_records.TryGetValue(id, out var row) || !Running(row)) return;
        var endTime = _now();
        var copy = Copy(row);
        copy["status"] = status;
        copy["endTime"] = endTime;
        copy["durationMs"] = DurationMs(endTime, Text(row, "startTime"));
        copy["processExit"] = IntegerOrNull(processExit);
        if (IsSafeInteger(Prop(tokens, "input"), out var input) && IsSafeInteger(Prop(tokens, "output"), out var output))
        {
            var stored = new JsonObject { ["input"] = JsonInteger(input), ["output"] = JsonInteger(output) };
            if (IsSafeInteger(Prop(tokens, "cachedInput"), out var cached)) stored["cachedInput"] = JsonInteger(cached);
            copy["tokens"] = stored;
        }
        Write(copy);
    }

    public void Reconcile(JsonObject? snapshot, Func<JsonNode?, bool>? isAlive)
    {
        foreach (var key in _order.ToArray())
        {
            var row = _records[key];
            if (Role(row) != "worker" || !Running(row)) continue;
            JsonObject? tokens = null;
            try
            {
                if (JsonNode.Parse(System.IO.File.ReadAllText(JsPath(row, "resultPath") + ".usage.json")) is JsonObject usage) tokens = usage;
            }
            catch { /* A missing usage file does not fail reconcile. */ }
            var result = FindResult(snapshot, row);
            if (result is not null)
            {
                Finish(key, Text(result, "status"), result.ContainsKey("exitCode") ? result["exitCode"]?.DeepClone() : null, tokens);
                continue;
            }
            if (!JsTruthy(Prop(row, "pid")) || isAlive is null || isAlive(row["pid"])) continue;
            JsonNode? exitCode = null;
            var exitText = ReadQuiet(JsPath(row, "exitPath"));
            if (exitText is not null)
            {
                try
                {
                    if (JsonNode.Parse(exitText) is JsonObject exit && exit.ContainsKey("exitCode")) exitCode = exit["exitCode"];
                }
                catch { /* Unreadable exit metadata leaves processExit null. */ }
            }
            var text = ReadQuiet(JsPath(row, "resultPath")) ?? "";
            if (text.Length > 8192) text = text[..8192];
            // JavaScript ^ matches after CR. .NET multiline ^ matches only after LF.
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var matched = StatusLine.Match(text);
            var status = !matched.Success ? "BLOCKED" : matched.Groups[1].Value == "CHECKPOINTED" ? "BLOCKED" : matched.Groups[1].Value;
            Finish(key, status, exitCode, tokens);
        }
    }

    public JsonObject View(string? projectId = null)
    {
        var rows = new List<JsonObject>();
        foreach (var key in _order)
            if (string.IsNullOrEmpty(projectId) || (Text(_records[key], "projectId") == projectId)) rows.Add(_records[key]);
        var recent = new JsonArray();
        for (var i = rows.Count - 1; i >= Math.Max(0, rows.Count - 20); i--) recent.Add(rows[i].DeepClone());
        return new JsonObject
        {
            ["orchestrator"] = Groups(rows, "orchestrator"),
            ["workers"] = Groups(rows, "worker"),
            ["supervisors"] = Groups(rows, "supervisor"),
            ["recent"] = recent
        };
    }

    JsonArray Groups(List<JsonObject> rows, string role)
    {
        var totals = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var row in rows)
        {
            if (Role(row) != role) continue;
            var groupKey = new JsonArray(CloneProp(row, "provider"), CloneProp(row, "model")).ToJsonString(JsJson);
            if (!totals.TryGetValue(groupKey, out var group))
            {
                group = new JsonObject
                {
                    ["provider"] = CloneProp(row, "provider"),
                    ["model"] = CloneProp(row, "model"),
                    ["calls"] = 0,
                    ["durationMs"] = 0,
                    ["COMPLETE"] = 0,
                    ["FAILED"] = 0,
                    ["BLOCKED"] = 0,
                    ["RUNNING"] = 0
                };
                totals[groupKey] = group;
                order.Add(groupKey);
            }
            group["calls"] = Whole(group["calls"]) + 1;
            group["durationMs"] = JsonNumber(Added(group["durationMs"]) + Added(Prop(row, "durationMs")));
            var status = StatusKey(row);
            group[status] = (group.ContainsKey(status) ? FalsyInt(group[status]) : 0) + 1;
            if (JsTruthy(Prop(row, "tokens")))
            {
                group["inputTokens"] = JsonNumber(FalsyNumber(group, "inputTokens") + TokenPart(row["tokens"], "input"));
                group["outputTokens"] = JsonNumber(FalsyNumber(group, "outputTokens") + TokenPart(row["tokens"], "output"));
                group["tokenReadings"] = FalsyInt(group.ContainsKey("tokenReadings") ? group["tokenReadings"] : null) + 1;
            }
        }
        var array = new JsonArray();
        foreach (var key in order) array.Add(totals[key]);
        return array;
    }

    string Write(JsonObject record)
    {
        var key = MapKey(record) ?? throw new InvalidOperationException("The invocation record has no id.");
        // Node applies mode 0o600 when append creates the file. CreateNew is the path that stores UnixCreateMode.
        if (!System.IO.File.Exists(_file))
        {
            using var created = OpenNew();
        }
        var bytes = Encoding.UTF8.GetBytes(record.ToJsonString(JsJson) + "\n");
        using (var stream = OpenAppend()) stream.Write(bytes.AsSpan());
        if (!_records.ContainsKey(key)) _order.Add(key);
        _records[key] = record;
        return key;
    }

    void Load()
    {
        foreach (var line in System.IO.File.ReadAllText(_file, Encoding.UTF8).Split('\n'))
        {
            try
            {
                if (JsonNode.Parse(line) is not JsonObject record) continue;
                var key = MapKey(record);
                if (key is null) continue;
                if (!_records.ContainsKey(key)) _order.Add(key);
                _records[key] = record;
            }
            catch { /* Preserve a truncated historical line. */ }
        }
    }

    void EnsureTrailingNewline()
    {
        if (!System.IO.File.Exists(_file)) return;
        var size = new FileInfo(_file).Length;
        if (size == 0) return;
        int last;
        using (var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            stream.Position = size - 1;
            last = stream.ReadByte();
        }
        if (last == '\n') return;
        using var append = OpenAppend();
        append.WriteByte((byte)'\n');
    }

    // mode 0o600. Windows ignores the Unix mode; the JSON line is what every platform can check.
    FileStream OpenNew() => new(_file, new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.ReadWrite,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    });

    FileStream OpenAppend() => new(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

    static JsonObject? FindResult(JsonObject? snapshot, JsonObject row)
    {
        if (snapshot is null || !snapshot.ContainsKey("execution") || snapshot["execution"] is null) return null;
        if (snapshot["execution"] is not JsonObject execution) throw new InvalidOperationException("Cannot read properties of execution (reading 'results')");
        if (!execution.ContainsKey("results") || execution["results"] is null) return null;
        if (execution["results"] is not JsonArray results) throw new InvalidOperationException("results.find is not a function");
        foreach (var item in results)
            if (item is JsonObject result && SameAttempt(result, row)) return result;
        return null;
    }

    static JsonNode? DurationMs(string endTime, string? startTime)
    {
        if (!UnixMs(endTime, out var end) || !UnixMs(startTime, out var start)) return null;
        return JsonInteger(Math.Max(0, end - start));
    }

    static bool UnixMs(string? text, out long ms)
    {
        ms = 0;
        if (text is null || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        ms = parsed.ToUnixTimeMilliseconds();
        return true;
    }

    static JsonObject Copy(JsonObject row)
    {
        var copy = new JsonObject();
        foreach (var pair in row) copy[pair.Key] = pair.Value?.DeepClone();
        return copy;
    }

    static string? MapKey(JsonObject record)
    {
        var id = Prop(record, "id");
        if (!JsTruthy(id)) return null;
        return id is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : id!.ToJsonString();
    }

    static bool Running(JsonObject row) => Text(row, "status") == "RUNNING";
    static string? Role(JsonObject row) => Text(row, "role");
    static string? Text(JsonObject row, string key) => Prop(row, key) is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    static JsonNode? Prop(JsonObject? row, string key) => row is not null && row.ContainsKey(key) ? row[key] : null;
    static JsonNode? OrNull(JsonObject row, string key) => JsTruthy(Prop(row, key)) ? row[key]!.DeepClone() : null;
    static JsonNode? CloneProp(JsonObject row, string key) => Prop(row, key)?.DeepClone();

    static string JsPath(JsonObject row, string key)
    {
        if (!row.ContainsKey(key)) return "undefined";
        var node = row[key];
        if (node is null) return "null";
        if (node is JsonValue value)
        {
            return value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => value.ToJsonString(),
                _ => "null"
            };
        }
        return node is JsonArray ? node.ToJsonString() : "[object Object]";
    }

    static string? ReadQuiet(string path)
    {
        try { return System.IO.File.ReadAllText(path); }
        catch { return null; }
    }

    static string StatusKey(JsonObject row)
    {
        if (!row.ContainsKey("status")) return "undefined";
        var node = row["status"];
        if (node is null) return "null";
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.String) return value.GetValue<string>();
        return node.ToJsonString();
    }

    static bool JsTruthy(JsonNode? node)
    {
        if (node is null) return false;
        if (node is not JsonValue value) return true;
        return value.GetValueKind() switch
        {
            JsonValueKind.False or JsonValueKind.Null => false,
            JsonValueKind.String => value.GetValue<string>().Length > 0,
            JsonValueKind.Number => TryNumber(value, out var number) && number != 0 && !double.IsNaN(number),
            _ => true
        };
    }

    static bool TryNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number) return false;
        number = double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        return true;
    }

    static bool IsSafeInteger(JsonNode? node, out long number)
    {
        number = 0;
        if (!TryNumber(node, out var value) || !double.IsFinite(value) || value != Math.Truncate(value) || Math.Abs(value) > MaxSafeInteger) return false;
        number = (long)value;
        return true;
    }

    static JsonNode? IntegerOrNull(JsonNode? node)
    {
        if (!TryNumber(node, out var value) || !double.IsFinite(value) || value != Math.Truncate(value)) return null;
        if (value >= long.MinValue && value <= long.MaxValue && (long)value == value) return JsonInteger((long)value);
        return JsonValue.Create(value);
    }

    static JsonNode JsonInteger(long number) => number >= int.MinValue && number <= int.MaxValue ? JsonValue.Create((int)number) : JsonValue.Create(number);

    static JsonNode? JsonNumber(double number)
    {
        if (!double.IsFinite(number)) return null;
        if (number == Math.Truncate(number) && number >= long.MinValue && number <= long.MaxValue) return JsonInteger((long)number);
        return JsonValue.Create(number);
    }

    static int Whole(JsonNode? node) => TryNumber(node, out var number) ? (int)number : 0;
    static int FalsyInt(JsonNode? node) => JsTruthy(node) ? Whole(node) : 0;
    static double Added(JsonNode? node) => JsTruthy(node) && TryNumber(node, out var number) ? number : 0;

    static double FalsyNumber(JsonObject row, string key) => JsTruthy(Prop(row, key)) && TryNumber(row[key], out var number) ? number : 0;

    static double TokenPart(JsonNode? tokens, string key) => tokens is JsonObject row && TryNumber(Prop(row, key), out var number) ? number : double.NaN;

    static bool SameAttempt(JsonObject result, JsonObject row)
    {
        var leftMissing = !result.ContainsKey("attemptId");
        var rightMissing = !row.ContainsKey("attemptId");
        if (leftMissing || rightMissing) return leftMissing && rightMissing;
        return StrictEqual(result["attemptId"], row["attemptId"]);
    }

    static bool StrictEqual(JsonNode? left, JsonNode? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left is not JsonValue a || right is not JsonValue b || a.GetValueKind() != b.GetValueKind()) return false;
        return a.GetValueKind() switch
        {
            JsonValueKind.String => a.GetValue<string>() == b.GetValue<string>(),
            JsonValueKind.Number => TryNumber(a, out var da) && TryNumber(b, out var db) && da == db,
            JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False => true,
            _ => false
        };
    }
}
