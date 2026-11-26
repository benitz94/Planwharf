// Port of orchestrator lead questions from engine/orchestrator.js.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public static partial class OrchestratorLeadQuestions
{
    public const string OrchName = "Orchestrator";

    // Module-local contracts preserve the complete JS objects, including unknown metadata.
    // Earlier port modules' summary records deliberately omit fields needed by lead workflows.
    public sealed record Snapshot(JsonObject Data);
    public sealed record Project(JsonObject Data);
    public sealed record Facts(JsonObject Data);
    public sealed record Model(JsonObject Data);
    public sealed record Item(JsonObject Data);
    public sealed record DeterministicAnswer(bool Ok, string Answer, string? ProjectId, string? Reason = null);
    public sealed record Envelope(string? Answer, JsonArray Actions, string? Error = null);

    // Nested to avoid changing Job 49's existing namespace-level ILeadModelAdapter.
    // JsonObject/JsonArray carry exact provider diagnostics, usage, action metadata and history.
    public interface ILeadModelAdapter
    {
        ValueTask<LeadSpokenResponse> AskModelAsync(LeadAskInvocation invocation,
            CancellationToken cancellationToken = default);
    }

    public sealed record LeadAskInvocation(
        string Question, Model Model, string? ProjectId, string? AskKey, string? Folder,
        string? Github, JsonObject State, bool? AllowActions = null, string? ChatMode = null,
        bool WorkRunning = false, string? ClarificationKind = null, string? GrillKind = null,
        string? Purpose = null, Action<LeadAskProgress>? OnProgress = null);

    public sealed record LeadAskProgress(string? Thinking = null, string? Answer = null, long? Ms = null);
    public sealed record LeadSpokenResponse(
        string? Answer, string? Thinking = null, long? Ms = null, JsonArray? Actions = null,
        string? Failure = null, JsonObject? Diagnostic = null, int? ProcessExit = null,
        JsonObject? Tokens = null, string? Error = null, JsonObject? Metadata = null);

    // Effects are proposals/observations for the composition root, never durable writes here.
    // Payload keeps refusal timestamps, quota/credit details, lead switches, assumptions,
    // task preparation requests, summaries, usage increments and ledger completion evidence.
    public sealed record LeadEffect(string Kind, string? ProjectId, JsonObject Payload);
    public sealed record LeadAction(JsonNode? Data);
    public sealed record LeadActionResult(bool Ok, string? Text, string? Reason,
        LeadAction Action, JsonObject? Payload = null);
    public sealed record LeadSelectionOptions(bool Credits = false, string? Company = null);
    public sealed record TaskPreparationRequest(string ProjectId, string Question,
        Func<bool> IsCurrent, JsonObject? GoldModel = null);

    static readonly Func<Model?> DefaultLeadModel = () => null;

    public sealed record LeadServices
    {
        public Func<Model?> LeadModel { get; init; } = DefaultLeadModel;
        public IReadOnlyList<Model> Roster { get; init; } = [];
        public JsonArray Plans { get; init; } = [];
        public IFileSystem FileSystem { get; init; } = new PhysicalFileSystem();
        public Func<long> NowMilliseconds { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public Func<string> Now { get; init; } = () => DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        public Func<Model, bool>? CanOrchestrate { get; init; }
        public Func<Model, bool>? QuotaBlocked { get; init; }
        public Func<Model, bool>? PlanExhausted { get; init; }
        public Func<Model, bool>? SupervisorSupported { get; init; }
        public Func<IReadOnlySet<string>, LeadSelectionOptions, Model?>? NextAllowedLead { get; init; }
        public Func<string?, JsonObject, JsonObject>? AttachWorkPlan { get; init; }
        public Func<JsonObject, JsonObject?, JsonObject>? CompactState { get; init; }
        public Action<LeadEffect>? OnEffect { get; init; }
        public Action<LeadAskProgress>? OnProgress { get; init; }
        public Func<string, string, Func<bool>, CancellationToken, ValueTask<JsonObject>>? PrepareTask { get; init; }
        // The composition root needs the exact Gold constraint before preparing a task.
        // PrepareTask remains compatible with callers that prepare unconstrained autonomy work.
        public Func<TaskPreparationRequest, CancellationToken, ValueTask<JsonObject>>? PrepareTaskWithContext { get; init; }
        public Func<LeadAction, CancellationToken, ValueTask<LeadActionResult>>? ApplyAction { get; init; }
    }

    public sealed record LeadAskTurnRequest(
        string Question, string? ProjectId, string? ChatSessionId, string RequestMode,
        string? RequestedGrillKind, JsonObject? GoldModel, Snapshot Snapshot, Project? TargetProject,
        JsonArray ConversationHistory, IReadOnlyList<Model> Roster, Func<bool> IsCurrent,
        string? StateFile = null, JsonObject? BriefingBaseline = null, string? CurrentTaskId = null,
        JsonObject? Metadata = null);
    public sealed record LeadAskTurnResult(
        bool Ok, string Answer, string? Thinking, long Ms, JsonObject? Diagnostic,
        Model? InvokedLead, IReadOnlyList<LeadActionResult> ActionResults, string? NewTaskId,
        string? Reason = null, IReadOnlyList<LeadEffect>? Effects = null, JsonObject? Metadata = null);
    public sealed record LeadDecisionRequest(
        string ProjectId, Item Item, JsonObject Settings, string Question,
        IReadOnlyList<Model> Roster, string? EvalMegaTaskId = null, Func<bool>? IsCurrent = null);
    public sealed record LeadDecisionResult(
        bool Ok, JsonObject? Decision, string? FailureReason = null, string? StopReason = null,
        JsonObject? Settings = null, IReadOnlyList<LeadEffect>? Effects = null);
    public sealed record SupervisorReviewRequest(
        string ProjectId, string ModelId, string Question, JsonObject Facts, JsonArray History,
        IReadOnlyList<Model> Roster);
    public sealed record SupervisorReviewResult(
        string? Answer, string? Provider, string? Model, LeadSpokenResponse? Response = null,
        IReadOnlyList<LeadEffect>? Effects = null);

    // Read-only boundary: enumeration must preserve readdir order; RealPath resolves every link.
    public interface IFileSystem
    {
        IReadOnlyList<string> ReadDirectory(string path);
        string RealPath(string path);
        bool IsDirectory(string path);
        string ReadText(string path);
    }

    public sealed class PhysicalFileSystem : IFileSystem
    {
        public IReadOnlyList<string> ReadDirectory(string path) => Directory.GetFileSystemEntries(path)
            .Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray();
        public string RealPath(string path) => ProjectWorkspace.LinkedWorkspace(Path.GetFullPath(path))
            ?? throw new IOException();
        public bool IsDirectory(string path) => Directory.Exists(path);
        public string ReadText(string path) => File.ReadAllText(path);
    }

    static bool Match(string text, string pattern) => Regex.IsMatch(text, pattern,
        RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static JsonNode? Field(JsonNode? obj, string key) => obj is JsonObject o ? o[key] : null;
    static bool Truth(JsonNode? node) => ExternalAdapters.Truth(node);
    static string Str(JsonNode? node) => node switch
    {
        JsonArray array => string.Join(",", array.Select(n => n is null ? "" : Str(n))),
        JsonObject => "[object Object]",
        JsonValue value when value.GetValueKind() == JsonValueKind.Number => JsNumber(Number(value)),
        _ => ExternalAdapters.JsString(node)
    };
    static string JsNumber(double number)
    {
        if (number == 0) return "0";
        if (double.IsPositiveInfinity(number)) return "Infinity";
        if (double.IsNegativeInfinity(number)) return "-Infinity";
        if (number == Math.Truncate(number) && Math.Abs(number) < 1e21)
            return number.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        var raw = number.ToString("R", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
        var e = raw.IndexOf('e');
        if (e < 0) return raw;
        var exponent = int.Parse(raw[(e + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (exponent >= -6 && exponent < 21)
        {
            var negative = raw.StartsWith('-');
            var digits = raw[..e].TrimStart('-').Replace(".", "", StringComparison.Ordinal);
            var point = exponent + 1;
            return (negative ? "-" : "") + (point <= 0 ? "0." + new string('0', -point) + digits
                : point >= digits.Length ? digits + new string('0', point - digits.Length)
                : digits[..point] + "." + digits[point..]);
        }
        return raw[..e] + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    const string JsSpace = @"[\u0009-\u000d\u0020\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]";
    static string Trim(string value) => Regex.Replace(value, "^" + JsSpace + "+|" + JsSpace + "+$", "");
    static string Text(JsonNode? obj, string key, string fallback = "") =>
        Truth(Field(obj, key)) ? Str(Field(obj, key)) : fallback;
    static JsonArray Array(JsonNode? obj, string key) => Field(obj, key) as JsonArray ?? [];
    static JsonNode? Copy(JsonNode? node) => node?.DeepClone();
    static JsonArray Take(JsonArray array, int count) => new(array.Take(count).Select(Copy).ToArray());

    public static bool IsDocName(string? name)
    {
        var value = (name ?? "").Replace('\\', '/');
        return Match(value, @"^docs/.+\.md$") ||
            Match(value.Split('/')[^1], @"^(readme(\.md)?|agents\.md|claude\.md|skill\.md|contributing\.md)$");
    }

    public static string[] ListDocs(string? folder, IFileSystem? fs = null)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(folder)) return [];
        fs ??= new PhysicalFileSystem();
        try
        {
            foreach (var name in fs.ReadDirectory(folder))
                if (name is not "." and not ".." && IsDocName(name)) found.Add(name);
            var docs = Path.Combine(folder, "docs");
            try
            {
                if (ProjectWorkspace.Within(folder, fs.RealPath(docs)) && fs.IsDirectory(docs))
                    foreach (var name in fs.ReadDirectory(docs))
                        if (Match(name, @"\.md$")) found.Add("docs/" + name);
            }
            catch (Exception) { }
        }
        catch (Exception) { }
        return found.Take(12).ToArray();
    }

    public static string[] CollectDocs(Facts? facts)
    {
        var docs = Array(facts?.Data, "docs");
        return docs.Count > 0
            ? docs.Select(Str).Select(Trim).Where(s => s.Length > 0).Take(12).ToArray()
            : Array(facts?.Data, "names").Where(n => IsDocName(Truth(n) ? Str(n) : ""))
                .Take(12).Select(Str).ToArray();
    }

    public static string[] ParseGrillQuestions(string? text)
    {
        if (text is null || Trim(text).Length == 0) return [];
        var raw = Trim(text);
        var match = Regex.Match(raw, @"\[[\s\S]*\]", RegexOptions.ECMAScript);
        if (match.Success)
        {
            try
            {
                if (ParseJson(match.Value) is JsonArray parsed)
                    return parsed.Select(item =>
                    {
                        if (item is JsonValue v && v.TryGetValue<string>(out var s)) return Trim(s);
                        if (item is not (JsonObject or JsonArray)) return "";
                        var title = Trim(Text(item, "title", Text(item, "question")));
                        var body = Trim(Text(item, "body"));
                        var recommend = Trim(Text(item, "recommend", Text(item, "recommended")));
                        var line = string.Join(": ", new[] { title, body }.Where(x => x.Length > 0));
                        return recommend.Length > 0 ? line + " Recommended: " + recommend : line;
                    }).Where(s => s.Length > 0).Take(8).ToArray();
            }
            catch (JsonException) { }
        }
        return Regex.Split(raw, @"\r?\n").Select(line => Regex.Replace(line,
            @"^\s*(?:\d+[.)]|[-*]|❓\s*\*\*Q\d+\*\*\s*[-—–]?)\s*".Replace(@"\s", JsSpace, StringComparison.Ordinal), "",
            RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Replace("**", "", StringComparison.Ordinal)).Select(Trim)
            .Where(line => line.Length > 0 && (Match(line, "recommended:") || line.Contains('?'))).Take(8).ToArray();
    }

    public static string[] ParseGrillQuestionsValue(JsonNode? text) =>
        text is JsonValue value && value.TryGetValue<string>(out var raw) ? ParseGrillQuestions(raw) : [];

    public static string GrillPrompt(Item item, Facts? facts, IReadOnlyList<string>? docs)
    {
        var project = Field(item.Data, "project");
        var revisions = Array(item.Data, "revisions");
        var latest = revisions.LastOrDefault();
        var names = Array(facts?.Data, "names");
        return string.Join("\n\n", new[]
        {
            "You are interviewing the user about this Planwharf project before any planning.",
            "Ask only what blocks a good plan: the open decisions at the frontier, in one round. Word each question so yes accepts your recommended answer.",
            "Project: " + (Field(project, "name") is { } name ? Str(name) : "undefined"),
            Truth(Field(latest, "direction")) ? "Goal: " + Text(latest, "direction") : "",
            Truth(Field(project, "folder")) ? "Folder: " + Text(project, "folder") : "",
            Truth(Field(facts?.Data, "lastCommit")) ? "Latest commit: " + Text(facts?.Data, "lastCommit") : "",
            names.Count > 0 ? "Top files: " + string.Join(", ", names.Take(12).Select(n => n is null ? "" : Str(n))) : "",
            docs?.Count > 0 ? "Docs: " + string.Join(", ", docs) : "",
            "Return ONLY a JSON array of 3 to 6 strings.",
            "Each string is one frontier question about THIS project, with \"Recommended: ...\" in the same string.",
            "Do not ask for facts listed above. Do not implement."
        }.Where(s => s.Length > 0));
    }

    public static bool GrillDone(Item? item) => Array(item?.Data, "revisions").Any(r =>
        Text(r, "mode") == "grill" || Text(r, "title") is "Grill me" or "Grill with the docs");

    static string Property(JsonNode? obj, string key) => obj is JsonObject o && o.ContainsKey(key)
        ? Str(o[key]) : "undefined";
    static string LastActivity(string? tail)
    {
        tail ??= "";
        if (Regex.IsMatch(tail, "[{}]|\"event\"|\"step_update\""))
            return Activity.ActivityMessages(tail).LastOrDefault() ?? "";
        var last = tail.Split('\n').Select(Trim).LastOrDefault(s => s.Length > 0) ?? "";
        var clipped = Trim(Regex.Replace(last, JsSpace + "+", " "));
        return clipped.Length <= 180 ? clipped : clipped[..179] + "…";
    }

    static bool Finite(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number);
    static double Number(JsonNode node) => double.Parse(node.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture);
    static string FileCount(JsonNode monitoring) => Finite(Field(monitoring, "folderFiles"))
        ? Property(monitoring, "folderFiles") + " files" : "file count unavailable";

    public static string BriefRow(Project? project, string? lead = null)
    {
        if (project is null) return "No project.";
        var row = project.Data;
        var who = string.IsNullOrEmpty(lead) ? OrchName : lead;
        var bits = new List<string>
        {
            $"Speaking as {who}. {Property(row, "name")} is {Property(row, "label")}.",
            Text(row, "orchestratorDoing"),
            Truth(row["jobTitle"]) ? $"Job: {Text(row, "jobTitle")}." : "No running job."
        };
        var parts = Array(row, "dispatch");
        if (parts.Count > 0)
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var activity = Text(part, "activity", Truth(Field(part, "pid"))
                    ? $"Process {Text(part, "pid")}{(Truth(Field(part, "alive")) ? " alive" : " stopped")}" : "Not started.");
                bits.Add($"Worker {i + 1} {Text(part, "label", Property(part, "modelId"))}: {Text(part, "slice", "slice")}. {activity}");
            }
        }
        else bits.Add(Truth(row["workerLabel"]) ? $"Worker: {Text(row, "workerLabel")}." : "No worker assigned.");
        if (Truth(row["monitoring"]))
        {
            var monitoring = row["monitoring"]!;
            bits.Add($"I am monitoring processes, worker logs, and the project folder: {Property(monitoring, "alive")}/{Property(monitoring, "workers")} alive, {FileCount(monitoring)}.");
        }
        if (Truth(row["waitingFor"])) bits.Add($"Waiting for: {Text(row, "waitingFor")}.");
        if (Truth(row["next"])) bits.Add($"Next: {Text(row, "next")}");
        var last = LastActivity(Text(row, "workerTail"));
        if (last.Length > 0) bits.Add("Last worker output: " + last);
        return string.Join(" ", bits.Select(Trim).Where(s => s.Length > 0));
    }

    public static DeterministicAnswer AnswerQuestion(string? question, Snapshot? snapshot, string? projectId = null)
    {
        var q = Trim(question ?? "");
        if (q.Length == 0) return new(false, "", null, "empty question");
        var snap = snapshot?.Data;
        var lead = Text(snap, "orchestratorLabel", OrchName);
        var rows = Array(snap, "projects").OfType<JsonObject>().ToArray();
        if (Match(q, @"\b(all projects|tutti|fleet|ogni progetto)\b") && rows.Length > 0)
            return new(true, string.Join("\n", rows.Select(r => BriefRow(new(r), lead))), null);
        var ql = q.ToLowerInvariant();
        var row = PickAskedProjectJson(snap, projectId, q);
        if (row is null) return new(true, $"{lead}: no projects in the notebook.", null);
        var id = row["id"] is null ? null : Str(row["id"]);
        var name = Property(row, "name");
        var parts = Array(row, "dispatch");
        var worker = parts.FirstOrDefault(p =>
            (Text(p, "label").Length > 0 && ql.Contains(Text(p, "label").ToLowerInvariant(), StringComparison.Ordinal)) ||
            (Text(p, "modelId").Length > 0 && ql.Contains(Text(p, "modelId").ToLowerInvariant(), StringComparison.Ordinal)));
        if (worker is not null)
            return new(true, $"{lead} on {name}: {Text(worker, "label", Property(worker, "modelId"))} has slice \"{Text(worker, "slice", "whole job")}\". {Text(worker, "activity", "No interpreted activity yet.")} Process {Text(worker, "pid", "none")} is {(Truth(Field(worker, "alive")) ? "alive" : "stopped")}.", id);
        if (Match(q, @"\b(split|spacchett|slice|why two|perch[eé]|quanti worker)\b"))
            return new(true, parts.Count > 1
                ? $"{lead} split {name} into {parts.Count} slices: {string.Join("; ", parts.Select(p => $"{Property(p, "label")} — {Property(p, "slice")}"))}."
                : $"{lead} did not split {name}. One worker on the whole job.", id);
        if (Match(q, @"\b(quota|remaining|residu|left|utilizzo|usage|percent)\b"))
        {
            var plans = Array(snap, "plans");
            var list = new List<string>();
            foreach (var plan in plans)
            {
                var label = Property(plan, "label");
                if (!Truth(Field(plan, "readable"))) { list.Add(label + ": " + Text(plan, "reason", "not readable")); continue; }
                var windows = Array(plan, "windows");
                var usage = windows.Count > 0
                    ? string.Join(", ", windows.Select(w => $"{Property(w, "name")} {Property(w, "pct")}% used ({Property(w, "leftPct")}% left)"))
                    : $"{Property(plan, "pct")}% used ({Property(plan, "leftPct")}% left)";
                list.Add(label + ": " + (Truth(Field(plan, "exhausted")) ? "exhausted (" + usage + ")" : usage));
            }
            if (plans.Count == 0)
                foreach (var model in Array(snap, "models"))
                {
                    // usageOf accepts negative used values; CalculateUsage in the earlier module clamps them.
                    var used = Finite(Field(model, "used")) ? Number(Field(model, "used")!) : 0;
                    var limit = Finite(Field(model, "limit")) ? Number(Field(model, "limit")!) : 0;
                    var pct = limit > 0 ? Math.Min(100, Math.Floor(used * 100 / limit + 0.5)) : 0;
                    var left = limit > 0 ? Math.Max(0, 100 - pct) : 100;
                    list.Add($"{Property(model, "label")}: {pct.ToString(System.Globalization.CultureInfo.InvariantCulture)}% used ({left.ToString(System.Globalization.CultureInfo.InvariantCulture)}% left)");
                }
            return new(true, list.Count > 0 ? $"{lead} usage: {string.Join("; ", list)}." : $"{lead}: no plans loaded.", id);
        }
        if (Match(q, @"\b(monitor|vivo|alive|stall|watch|controll)\b"))
        {
            var m = row["monitoring"];
            var status = Truth(m) ? $"{Property(m, "alive")}/{Property(m, "workers")} processes alive, {FileCount(m!)}." : "";
            return new(true, $"{lead}: {Text(row, "orchestratorDoing", "not monitoring a run.")} {status} Waiting for: {Text(row, "waitingFor", "nothing named")}.", id);
        }
        if (Match(q, @"\b(next|prossimo|waiting|aspet)\b"))
            return new(true, $"{lead} on {name}: waiting for {Text(row, "waitingFor", "nothing")}. Next: {Text(row, "next", "nothing queued.")}", id);
        if (Match(q, @"\b(log|output|detto|said)\b"))
        {
            var last = LastActivity(Text(row, "workerTail"));
            return new(true, last.Length > 0 ? $"{lead} last worker output on {name}: {last}" : $"{lead}: no worker log yet on {name}.", id);
        }
        return new(true, BriefRow(new(row), lead), id);
    }

    static void CopyFields(JsonObject target, JsonNode? source, params string[] keys)
    {
        if (source is not JsonObject obj) return;
        foreach (var key in keys)
            if (obj.ContainsKey(key)) target[key] = Copy(obj[key]);
    }

    static JsonArray SelectRows(JsonArray source, Func<JsonNode?, bool> predicate) =>
        new(source.Where(predicate).Select(Copy).ToArray());

    public static string? LinkedFolder(string? folder, IFileSystem? fs = null)
    {
        if (folder is null || Trim(folder).Length == 0 || !Path.IsPathRooted(folder)) return null;
        fs ??= new PhysicalFileSystem();
        try
        {
            var real = fs.RealPath(folder);
            return fs.IsDirectory(real) ? real : null;
        }
        catch (Exception) { return null; }
    }

    static string TailLog(string? path, IFileSystem fs)
    {
        if (path is null || Trim(path).Length == 0) return "";
        try
        {
            var raw = fs.ReadText(path);
            return Trim(raw[Math.Max(0, raw.Length - 1600)..].Replace("\r", "", StringComparison.Ordinal));
        }
        catch (Exception) { return ""; }
    }

    // contextArchive/history/currentRequest belong to AskAsync, not JavaScript packState.
    // The optional stateFile argument is retained as a compatibility seam, never read here.
    public static JsonObject PackState(Snapshot? snapshot, Project? project, string? stateFile = null, IFileSystem? fs = null)
    {
        fs ??= new PhysicalFileSystem();
        var snap = snapshot?.Data;
        var row = project?.Data;
        var facts = Truth(Field(row, "facts")) ? Field(row, "facts") : null;
        var execution = Field(snap, "execution");
        JsonObject? handoff = null;
        if (row is not null && Truth(execution))
        {
            bool OwnMega(JsonNode? node) => JsonNode.DeepEquals(Field(node, "projectId"), row["id"]);
            bool OwnChild(JsonNode? node) => Array(execution, "megaTasks").Any(mega => OwnMega(mega) &&
                JsonNode.DeepEquals(Field(mega, "id"), Field(node, "megaTaskId")));
            handoff = new JsonObject
            {
                ["megaTasks"] = SelectRows(Array(execution, "megaTasks"), OwnMega),
                ["subTasks"] = SelectRows(Array(execution, "subTasks"), OwnChild),
                ["results"] = SelectRows(Array(execution, "results"), OwnChild),
                ["checkpoints"] = SelectRows(Array(execution, "checkpoints"), OwnMega),
                ["notes"] = Truth(row["notes"]) ? Copy(row["notes"]) : new JsonArray()
            };
            CopyFields(handoff, row, "facts");
            if (handoff.ContainsKey("facts"))
            {
                handoff["repository"] = Copy(handoff["facts"]);
                handoff.Remove("facts");
            }
        }
        JsonObject? focus = null;
        if (row is not null)
        {
            JsonObject? checkpoint = null;
            if (Truth(row["checkpoint"]))
            {
                var cp = row["checkpoint"];
                checkpoint = new JsonObject { ["plan"] = Take(Array(cp, "plan"), 12) };
                foreach (var key in new[] { "reason", "at", "path", "jobTitle" })
                    checkpoint[key] = Truth(Field(cp, key)) ? Copy(Field(cp, key)) : null;
            }
            var workers = new JsonArray();
            foreach (var part in Array(row, "dispatch"))
            {
                var worker = new JsonObject
                {
                    ["pid"] = Truth(Field(part, "pid")) ? Copy(Field(part, "pid")) : null,
                    ["alive"] = Truth(Field(part, "alive")),
                    ["activity"] = Truth(Field(part, "activity")) ? Copy(Field(part, "activity")) : JsonValue.Create(""),
                    ["log"] = LastActivity(TailLog(Text(part, "logPath"), fs))
                };
                foreach (var (target, source) in new[] { ("id", "modelId"), ("name", "label"), ("slice", "slice") })
                    if (part is JsonObject obj && obj.ContainsKey(source)) worker[target] = Copy(obj[source]);
                workers.Add((JsonNode)Ordered(worker, "id", "name", "slice", "pid", "alive", "activity", "log"));
            }
            focus = new JsonObject
            {
                ["folder"] = LinkedFolder(Text(row, "folder"), fs),
                ["github"] = Truth(row["github"]) ? Copy(row["github"]) : null,
                ["repository"] = Truth(row["repository"]) ? Copy(row["repository"]) : null,
                ["goal"] = Truth(row["goal"]) ? Copy(row["goal"]) : JsonValue.Create(""),
                ["state"] = Truth(row["stateArt"]) ? Copy(row["stateArt"]) : JsonValue.Create(""),
                ["open"] = Take(Array(row, "open"), 12),
                ["analysis"] = Truth(row["analysis"]) ? Copy(row["analysis"]) : JsonValue.Create(""),
                ["job"] = Truth(row["jobTitle"]) ? Copy(row["jobTitle"]) : null,
                ["plan"] = Take(Array(row, "plan"), 16),
                ["checkpoint"] = checkpoint,
                ["git"] = Truth(facts) ? JsonValue.Create(Truth(Field(facts, "git"))) : null,
                ["lastCommit"] = Truth(Field(facts, "lastCommit")) ? Copy(Field(facts, "lastCommit")) : JsonValue.Create(""),
                ["dirty"] = Take(Array(facts, "dirty"), 12),
                ["workers"] = workers,
                ["monitoring"] = Truth(row["monitoring"]) ? Copy(row["monitoring"]) : null,
                ["lastLog"] = LastActivity(Text(row, "workerTail"))
            };
            CopyFields(focus, row, "id", "name", "status", "label");
            foreach (var key in new[] { "orchestratorDoing", "waitingFor", "next" })
                focus[key] = Truth(row[key]) ? Copy(row[key]) : JsonValue.Create("");
            if (facts is null) focus["files"] = null;
            else if (facts is JsonObject f && f.ContainsKey("fileCount")) focus["files"] = Copy(f["fileCount"]);
            if (Truth(row["savingMode"]) && Text(row, "savingMode") != "normal") focus["savingMode"] = Copy(row["savingMode"]);
        }
        var others = new JsonArray();
        if (row is null)
            foreach (var item in Array(snap, "projects"))
            {
                var other = new JsonObject();
                CopyFields(other, item, "id", "name", "status");
                foreach (var key in new[] { "folder", "github", "repository" })
                    other[key] = Truth(Field(item, key)) ? Copy(Field(item, key)) : null;
                other["job"] = Truth(Field(item, "jobTitle")) ? Copy(Field(item, "jobTitle")) : null;
                others.Add((JsonNode)Ordered(other, "id", "name", "folder", "github", "repository", "status", "job"));
            }
        var fleet = new JsonArray();
        foreach (var item in Array(snap, "fleet"))
        {
            var entry = new JsonObject { ["exhausted"] = Truth(Field(item, "exhausted")), ["lead"] = Truth(Field(item, "lead")) };
            CopyFields(entry, item, "id", "label", "role", "plan", "enabled", "roles", "roleEligibility");
            fleet.Add((JsonNode)Ordered(entry, "id", "label", "role", "plan", "exhausted", "enabled", "roles", "roleEligibility", "lead"));
        }
        var plans = new JsonArray();
        foreach (var item in Array(snap, "plans"))
        {
            var entry = new JsonObject
            {
                ["readable"] = Truth(Field(item, "readable")), ["exhausted"] = Truth(Field(item, "exhausted")),
                ["windows"] = Truth(Field(item, "windows")) ? Copy(Field(item, "windows")) : new JsonArray()
            };
            CopyFields(entry, item, "id", "label", "pct", "leftPct");
            plans.Add((JsonNode)Ordered(entry, "id", "label", "readable", "exhausted", "pct", "leftPct", "windows"));
        }
        var result = new JsonObject
        {
            ["orchestrator"] = Truth(Field(snap, "orchestratorLabel")) ? Copy(Field(snap, "orchestratorLabel")) : JsonValue.Create(OrchName),
            ["executionHandoff"] = handoff,
            ["researches"] = SelectRows(Array(snap, "researches"), r => row is null || JsonNode.DeepEquals(Field(r, "projectId"), row["id"])),
            ["grill"] = new JsonObject
            {
                ["propose"] = snap is not null && Field(snap, "grillPropose")?.GetValueKind() != JsonValueKind.False,
                ["kind"] = Truth(Field(snap, "grillKind")) ? Copy(Field(snap, "grillKind"))
                    : JsonValue.Create(Field(snap, "grillPropose")?.GetValueKind() == JsonValueKind.False ? "off" : "me")
            },
            ["focus"] = focus, ["others"] = others, ["fleet"] = fleet, ["plans"] = plans
        };
        CopyFields(result, snap, "orchestratorId", "mode");
        if (snap is null) { result["orchestratorId"] = null; result["mode"] = null; result["grill"]!["propose"] = null; }
        if (handoff is not null) result["executionHandoff"] = Ordered(handoff,
            "megaTasks", "subTasks", "results", "checkpoints", "repository", "notes");
        if (focus is not null)
        {
            if (focus["checkpoint"] is JsonObject cp) focus["checkpoint"] = Ordered(cp, "reason", "at", "path", "jobTitle", "plan");
            result["focus"] = Ordered(focus, "id", "name", "folder", "github", "repository", "status", "label", "goal", "state",
                "open", "analysis", "job", "plan", "checkpoint", "orchestratorDoing", "waitingFor", "next", "files", "git",
                "lastCommit", "dirty", "savingMode", "workers", "monitoring", "lastLog");
        }
        return Ordered(result, "orchestrator", "orchestratorId", "executionHandoff", "researches", "mode", "grill", "focus", "others", "fleet", "plans");
    }

    static JsonObject Ordered(JsonObject source, params string[] keys)
    {
        var result = new JsonObject();
        foreach (var key in keys)
            if (source.ContainsKey(key)) result[key] = Copy(source[key]);
        return result;
    }

    public static Envelope LeadEnvelope(string? answer, JsonArray? actions = null)
    {
        if (actions is not null) return new(answer, (JsonArray)actions.DeepClone());
        var text = Trim(answer ?? "");
        var raw = Regex.Replace(text, "^```(?:json)?" + JsSpace + "*", "", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        raw = Regex.Replace(raw, JsSpace + "*```$", "", RegexOptions.ECMAScript);
        if (!raw.StartsWith('{')) return new(text, []);
        try
        {
            var parsed = ParseJson(raw) as JsonObject;
            if (parsed?["answer"] is not JsonValue v || !v.TryGetValue<string>(out var response) ||
                parsed["actions"] is not JsonArray list || parsed.Any(p => p.Key is not ("answer" or "actions")))
                return new(text, [], "Invalid lead action envelope; no workers were dispatched.");
            return new(response, (JsonArray)list.DeepClone());
        }
        catch (JsonException) { return new(text, [], "Unreadable lead action envelope; no workers were dispatched."); }
    }

    // JSON.parse keeps the final occurrence of a duplicate property. JsonNode's lazy
    // object materialization otherwise rejects duplicates when a property is accessed.
    static JsonNode? ParseJson(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = int.MaxValue });
        return Materialize(document.RootElement);
    }
    static JsonNode? Materialize(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var obj = new JsonObject();
            foreach (var property in element.EnumerateObject()) obj[property.Name] = Materialize(property.Value);
            return obj;
        }
        if (element.ValueKind == JsonValueKind.Array)
            return new JsonArray(element.EnumerateArray().Select(Materialize).ToArray());
        return JsonNode.Parse(element.GetRawText());
    }

    static InvalidOperationException Error(string message) => new(message);
    static string? OptText(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static bool IsTrue(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static bool IsFalse(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.False;
    static string ModelId(Model model) => Text(model.Data, "id");
    static string ModelLabel(Model model) => Text(model.Data, "label", ModelId(model));
    static string ProviderOf(Model model) => Text(model.Data, "providerId", Text(model.Data, "provider"));
    static string PoolOf(Model model) => Text(model.Data, "quotaPoolId", Text(model.Data, "plan"));

    static string AsciiLower(string text) => string.Create(text.Length, text, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
            span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
    });

    static readonly Regex CreditsNeededRegex = new(
        @"requires usage credits|purchase (?:more )?(?:usage )?credits|manage usage credits",
        RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    static bool IsCreditsNeeded(string? text) =>
        !string.IsNullOrEmpty(text) && CreditsNeededRegex.IsMatch(AsciiLower(text));

    static JsonArray PlansOf(LeadServices services, Snapshot? snapshot = null) =>
        services.Plans.Count > 0 ? services.Plans : snapshot?.Data["plans"] as JsonArray ?? services.Plans;

    static IReadOnlyList<Model> RosterOf(IReadOnlyList<Model>? primary, LeadServices services) =>
        primary is { Count: > 0 } ? primary : services.Roster;

    static JsonObject AttachWorkPlan(LeadServices services, string? folder, JsonObject state)
    {
        if (services.AttachWorkPlan is not null) return services.AttachWorkPlan(folder, state);
        var cloned = (JsonObject)state.DeepClone();
        if (string.IsNullOrEmpty(folder)) return cloned;
        string text;
        try { text = services.FileSystem.ReadText(Path.Combine(folder, ".planwharf", "plan.md")); }
        catch { return cloned; }
        if (string.IsNullOrEmpty(text)) return cloned;
        cloned["workPlanPath"] = ".planwharf/plan.md";
        cloned["workPlan"] = text.Length > 12000 ? text[..12000] : text;
        return cloned;
    }

    static ValueTask<JsonObject> PrepareTaskAsync(LeadServices services, string projectId,
        string question, Func<bool> isCurrent, JsonObject? goldModel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrent()) return ValueTask.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "cancelled" });
        if (services.PrepareTaskWithContext is not null)
            return services.PrepareTaskWithContext(new TaskPreparationRequest(projectId, question,
                isCurrent, goldModel?.DeepClone() as JsonObject), cancellationToken);
        // The legacy seam cannot carry an exact model constraint. Never drop it silently.
        if (goldModel is null && services.PrepareTask is not null)
            return services.PrepareTask(projectId, question, isCurrent, cancellationToken);
        return ValueTask.FromResult(new JsonObject { ["ok"] = false, ["reason"] = "task preparation is unavailable" });
    }

    static bool CanOrchestrateModel(LeadServices services, Model model) =>
        services.CanOrchestrate is not null ? services.CanOrchestrate(model) : OrchestratorTeamModels.CanOrchestrate(model.Data);

    static bool IsQuotaBlocked(LeadServices services, Model model, JsonArray plans) =>
        services.QuotaBlocked is not null
            ? services.QuotaBlocked(model)
            : Truth(Field(Field(model.Data, "usagePolicy"), "blocked")) || Truth(ModelPolicy.UsagePolicy(model.Data, plans)["blocked"]);

    static bool IsPlanExhausted(LeadServices services, Model model, JsonArray plans) =>
        services.PlanExhausted is not null ? services.PlanExhausted(model) : PlanUsage.PlanExhausted(plans, PoolOf(model));

    static bool IsSupervisorSupported(LeadServices services, Model model) =>
        services.SupervisorSupported is not null ? services.SupervisorSupported(model) : ModelRoles.SupportsSupervisor(model.Data);

    static bool RemainingBelowCost(Model model) =>
        !Truth(Field(model.Data, "providerCatalog"))
        && Finite(Field(model.Data, "remaining")) && Finite(Field(model.Data, "cost"))
        && Number(Field(model.Data, "remaining")!) < Number(Field(model.Data, "cost")!);

    static string SavingModeOf(Snapshot? snapshot, Project? target, string? projectId)
    {
        var fromRow = Text(target?.Data, "savingMode");
        if (fromRow.Length > 0) return ModelRouting.PersistedSavingMode(fromRow);
        if (!string.IsNullOrEmpty(projectId) && Field(snapshot?.Data, "savingModes") is JsonObject modes)
            return ModelRouting.PersistedSavingMode(Text(modes, projectId));
        return "normal";
    }

    static string ChatModeOf(Snapshot? snapshot, Project? target, string? projectId)
    {
        var saved = Text(target?.Data, "chatMode").Trim().ToLowerInvariant();
        if (saved is "talk" or "plan") return saved;
        if (!string.IsNullOrEmpty(projectId) && Field(snapshot?.Data, "chatModes") is JsonObject modes)
        {
            var fromSnap = Text(modes, projectId).Trim().ToLowerInvariant();
            if (fromSnap is "talk" or "plan") return fromSnap;
        }
        return "plan";
    }

    static bool WorkInFlight(Project? target)
    {
        if (target is null) return false;
        var row = target.Data;
        var mega = Text(Field(row, "megaTask"), "status").ToUpperInvariant();
        if (mega is "RUNNING" or "READY_FOR_APPROVAL") return true;
        if (IsTrue(row["canStop"])) return true;
        if (Text(row, "status") is "watching" or "needs_approval") return true;
        return Array(row, "dispatch").Any(part => Truth(Field(part, "alive")));
    }

    static Model? ResolveLeadModel(LeadServices services, Snapshot? snapshot, IReadOnlyList<Model> roster)
    {
        if (!ReferenceEquals(services.LeadModel, DefaultLeadModel)) return services.LeadModel();
        var orchId = OptText(Field(snapshot?.Data, "orchestratorId"));
        if (string.IsNullOrEmpty(orchId)) return null;
        var picked = roster.FirstOrDefault(m => ModelId(m) == orchId);
        if (picked is null) return null;
        var plans = PlansOf(services, snapshot);
        if (!CanOrchestrateModel(services, picked)) return null;
        var policy = ModelPolicy.UsagePolicy(picked.Data, plans);
        var state = OptText(policy["state"]);
        var signedOut = state is "expired" or "not signed in";
        if ((signedOut || !Truth(policy["blocked"])) && !IsPlanExhausted(services, picked, plans))
            return picked;
        return null;
    }

    static bool IsLeadUsable(LeadServices services, Model model, JsonArray plans, JsonObject refusalStore, ISet<string> creditsBlocked)
    {
        var id = ModelId(model);
        if (string.IsNullOrEmpty(id)) return false;
        if (IsFalse(Field(model.Data, "leadAllowed"))) return false;
        if (creditsBlocked.Contains(id) || Truth(Field(model.Data, "creditsNeeded"))) return false;
        if (!ModelRoles.RoleEligibility(model.Data)["orchestrator"]) return false;
        bool canOrch;
        if (services.CanOrchestrate is not null)
        {
            canOrch = services.CanOrchestrate(model);
        }
        else
        {
            var promoted = (JsonObject)model.Data.DeepClone();
            var roles = promoted["roles"] is JsonObject existing ? (JsonObject)existing.DeepClone() : new JsonObject();
            roles["orchestrator"] = true;
            promoted["roles"] = roles;
            canOrch = OrchestratorTeamModels.CanOrchestrate(promoted);
        }
        return canOrch && !IsQuotaBlocked(services, model, plans) && !IsPlanExhausted(services, model, plans)
            && ProviderRefusal.ActiveRefusal(refusalStore, id, services.NowMilliseconds()) is null;
    }

    static Model? NextAllowedLead(
        LeadServices services,
        IReadOnlyList<Model> roster,
        JsonArray plans,
        string savingMode,
        JsonObject refusalStore,
        ISet<string> creditsBlocked,
        IReadOnlySet<string> exclude,
        LeadSelectionOptions options)
    {
        if (services.NextAllowedLead is not null) return services.NextAllowedLead(exclude, options);
        var baseList = roster.Where(model =>
            !exclude.Contains(ModelId(model))
            && IsLeadUsable(services, model, plans, refusalStore, creditsBlocked)
            && (savingMode == "normal" || CanOrchestrateModel(services, model))).ToList();
        var allowed = baseList.Where(model => OrchestratorTeamModels.AllowedBySavingMode(savingMode, model.Data)).ToList();
        if (allowed.Count == 0) return LowestClassLead(baseList, plans);
        var roomy = allowed.Where(model => !Truth(Field(model.Data, "lowQuota"))).ToList();
        if (roomy.Count == 0) roomy = allowed;
        var pool = roomy;
        if (!options.Credits && !string.IsNullOrEmpty(options.Company))
        {
            var otherCompany = roomy.Where(model => ProviderOf(model) != options.Company).ToList();
            if (otherCompany.Count > 0) pool = otherCompany;
        }
        return RankFirstLead(pool, plans);
    }

    static Model? LowestClassLead(List<Model> usable, JsonArray plans)
    {
        if (usable.Count == 0) return null;
        static int ClassRank(Model model)
        {
            int? tier = Finite(Field(model.Data, "leadTier")) ? (int)Number(Field(model.Data, "leadTier")!) : OrchestratorTeamModels.LeadTier(model.Data);
            return ModelRouting.ModelClass(model.Data, tier) switch { "flagship" => 0, "light" => 2, _ => 1 };
        }
        var worst = usable.Max(ClassRank);
        return RankFirstLead(usable.Where(m => ClassRank(m) == worst).ToList(), plans);
    }

    static Model? RankFirstLead(List<Model> pool, JsonArray plans)
    {
        if (pool.Count == 0) return null;
        var arr = new JsonArray(pool.Select(m => (JsonNode)m.Data.DeepClone()).ToArray());
        var ranked = OrchestratorTeamModels.LeadRanking(arr, plans);
        var topId = OptText(Field(ranked.FirstOrDefault(), "id"));
        return topId is null ? null : pool.FirstOrDefault(m => ModelId(m) == topId);
    }

    static void EmitEffect(LeadServices services, List<LeadEffect> effects, string kind, string? projectId, JsonObject payload)
    {
        var effect = new LeadEffect(kind, projectId, payload);
        lock (effects) { effects.Add(effect); }
        services.OnEffect?.Invoke(effect);
    }

    static JsonObject LedgerStartPayload(string role, string? projectId, string? megaTaskId, Model model, string reason) => new()
    {
        ["role"] = role,
        ["projectId"] = projectId,
        ["megaTaskId"] = megaTaskId,
        ["provider"] = ProviderOf(model) is { Length: > 0 } p ? p : null,
        ["model"] = OptText(Field(model.Data, "cli")),
        ["modelId"] = ModelId(model),
        ["accountKey"] = Copy(Field(model.Data, "accountKey")),
        ["accountType"] = Copy(Field(model.Data, "accountType")),
        ["reason"] = reason
    };

    static JsonObject LedgerFinishPayload(Model model, string status, int? processExit = null, JsonObject? tokens = null) => new()
    {
        ["modelId"] = ModelId(model),
        ["status"] = status,
        ["processExit"] = processExit,
        ["tokens"] = tokens?.DeepClone()
    };

    public static ValueTask<IReadOnlyList<string>> ComposeGrillRoundAsync(
        Item item,
        Facts? facts,
        string kind,
        ILeadModelAdapter? leadAdapter,
        CancellationToken cancellationToken = default) =>
        ComposeGrillRoundAsync(item, facts, kind, leadAdapter, new LeadServices(), cancellationToken);

    public static async ValueTask<IReadOnlyList<string>> ComposeGrillRoundAsync(
        Item item,
        Facts? facts,
        string kind,
        ILeadModelAdapter? leadAdapter,
        LeadServices services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revisions = Array(item?.Data, "revisions");
        var latest = revisions.LastOrDefault() as JsonObject;
        var project = Field(item?.Data, "project") as JsonObject;
        var docs = kind == "docs" ? CollectDocs(facts) : [];
        var lead = ResolveLeadModel(services, null, services.Roster);
        if (leadAdapter is not null && lead is not null && item is not null)
        {
            var effects = new List<LeadEffect>();
            var projectId = Field(project, "id") is { } pidNode && Truth(pidNode) ? Str(pidNode) : null;
            var folder = Truth(Field(project, "folder")) ? Text(project, "folder") : null;
            var linked = LinkedFolder(folder, services.FileSystem);
            var rawState = new JsonObject { ["name"] = Copy(Field(project, "name")) };
            if (latest is not null && latest.ContainsKey("direction")) rawState["goal"] = Copy(latest["direction"]);
            if (project is not null && project.ContainsKey("folder")) rawState["folder"] = Copy(project["folder"]);
            rawState["docs"] = new JsonArray(docs.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
            var invocation = new LeadAskInvocation(
                Question: GrillPrompt(item, facts, docs),
                Model: lead,
                ProjectId: projectId,
                AskKey: null,
                Folder: folder,
                Github: null,
                State: AttachWorkPlan(services, linked, rawState),
                AllowActions: false,
                ClarificationKind: kind);
            EmitEffect(services, effects, "ledger_start", projectId,
                LedgerStartPayload("orchestrator", projectId, null, lead, "clarification"));
            try
            {
                var spoken = await leadAdapter.AskModelAsync(invocation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var failed = !string.IsNullOrEmpty(spoken.Failure);
                EmitEffect(services, effects, "ledger_finish", projectId,
                    LedgerFinishPayload(lead, failed ? "FAILED" : "COMPLETE", spoken.ProcessExit, spoken.Tokens));
                if (!failed)
                {
                    var parsed = ParseGrillQuestions(LeadEnvelope(spoken.Answer).Answer);
                    if (parsed.Length > 0) return parsed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                EmitEffect(services, effects, "ledger_finish", projectId, LedgerFinishPayload(lead, "BLOCKED"));
                throw;
            }
            catch
            {
                EmitEffect(services, effects, "ledger_finish", projectId, LedgerFinishPayload(lead, "FAILED"));
            }
        }
        var fallback = new JsonObject { ["name"] = Copy(Field(project, "name")) };
        if (latest is not null && latest.ContainsKey("direction")) fallback["goal"] = Copy(latest["direction"]);
        if (project is not null && project.ContainsKey("folder")) fallback["folder"] = Copy(project["folder"]);
        fallback["docs"] = new JsonArray(docs.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
        return Plan.GrillFrontier(fallback).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : Str(n)).ToArray();
    }

    public static ValueTask<SupervisorReviewResult> ReviewSnapshotAsync(
        SupervisorReviewRequest request,
        ILeadModelAdapter? leadAdapter,
        CancellationToken cancellationToken = default) =>
        ReviewSnapshotAsync(request, leadAdapter, new LeadServices(), cancellationToken);

    public static async ValueTask<SupervisorReviewResult> ReviewSnapshotAsync(
        SupervisorReviewRequest request,
        ILeadModelAdapter? leadAdapter,
        LeadServices services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plans = PlansOf(services);
        var roster = RosterOf(request.Roster, services);
        var model = roster.FirstOrDefault(m => ModelId(m) == request.ModelId);
        if (model is null || !Truth(Field(model.Data, "enabled")) || IsFalse(Field(model.Data, "available"))
            || IsFalse(Field(model.Data, "providerEnabled")) || !IsTrue(Field(Field(model.Data, "roles"), "supervisor"))
            || IsQuotaBlocked(services, model, plans) || IsPlanExhausted(services, model, plans)
            || RemainingBelowCost(model) || !IsSupervisorSupported(services, model))
            throw Error("The selected Supervisor is unavailable, not permitted, or blocked by quota. No fallback was used.");
        if (leadAdapter is null)
            throw Error("The model transport is unavailable. Live evidence remains available.");

        var effects = new List<LeadEffect>();
        var provider = ProviderOf(model) is { Length: > 0 } p ? p : null;
        var cli = OptText(Field(model.Data, "cli"));
        EmitEffect(services, effects, "ledger_start", request.ProjectId,
            LedgerStartPayload("supervisor", request.ProjectId, OptText(Field(request.Facts, "megaTaskId")), model, "Supervisor advisory question"));
        try
        {
            var cost = Finite(Field(model.Data, "cost")) ? Number(Field(model.Data, "cost")!) : 0;
            EmitEffect(services, effects, "usage", request.ProjectId, new JsonObject
            {
                ["modelId"] = ModelId(model),
                ["cost"] = cost
            });
            var invocation = new LeadAskInvocation(
                Question: request.Question,
                Model: model,
                ProjectId: request.ProjectId,
                AskKey: null,
                Folder: null,
                Github: null,
                State: new JsonObject
                {
                    ["facts"] = request.Facts.DeepClone(),
                    ["history"] = request.History.DeepClone()
                },
                AllowActions: false,
                Purpose: "supervisor",
                OnProgress: services.OnProgress);
            var spoken = await leadAdapter.AskModelAsync(invocation, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EmitEffect(services, effects, "ledger_finish", request.ProjectId,
                LedgerFinishPayload(model, !string.IsNullOrEmpty(spoken.Failure) ? "FAILED" : "COMPLETE", spoken.ProcessExit, spoken.Tokens));
            return new SupervisorReviewResult(spoken.Answer, provider, cli, spoken, effects);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            EmitEffect(services, effects, "ledger_finish", request.ProjectId, LedgerFinishPayload(model, "BLOCKED"));
            throw;
        }
        catch
        {
            EmitEffect(services, effects, "ledger_finish", request.ProjectId, LedgerFinishPayload(model, "FAILED"));
            throw;
        }
    }

    public static ValueTask<LeadDecisionResult> AskLeadDecisionAsync(
        LeadDecisionRequest request,
        ILeadModelAdapter? leadAdapter,
        CancellationToken cancellationToken = default) =>
        AskLeadDecisionAsync(request, leadAdapter, new LeadServices(), cancellationToken);

    public static ValueTask<LeadDecisionResult> AskLeadDecisionAsync(
        string projectId,
        Item item,
        JsonObject settings,
        string question,
        ILeadModelAdapter? leadAdapter,
        IReadOnlyList<Model> roster,
        string? evalMegaTaskId = null,
        CancellationToken cancellationToken = default) =>
        AskLeadDecisionAsync(new LeadDecisionRequest(projectId, item, settings, question, roster, evalMegaTaskId),
            leadAdapter, new LeadServices(), cancellationToken);

    public static ValueTask<LeadDecisionResult> AskLeadDecisionAsync(
        string projectId,
        Item item,
        JsonObject settings,
        string question,
        ILeadModelAdapter? leadAdapter,
        IReadOnlyList<Model> roster,
        LeadServices services,
        string? evalMegaTaskId = null,
        CancellationToken cancellationToken = default) =>
        AskLeadDecisionAsync(new LeadDecisionRequest(projectId, item, settings, question, roster, evalMegaTaskId),
            leadAdapter, services, cancellationToken);

    public static async ValueTask<LeadDecisionResult> AskLeadDecisionAsync(
        LeadDecisionRequest request,
        ILeadModelAdapter? leadAdapter,
        LeadServices services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effects = new List<LeadEffect>();
        var nextSettings = request.Settings is not null ? (JsonObject)request.Settings.DeepClone() : new JsonObject();

        LeadDecisionResult Stop(string reason, bool ok, JsonObject? parsed = null, string? failureReason = null)
        {
            nextSettings["stopReason"] = reason;
            EmitEffect(services, effects, "note", request.ProjectId, new JsonObject { ["text"] = "Project autonomy stopped: " + reason });
            EmitEffect(services, effects, "stop_autonomy", request.ProjectId, new JsonObject { ["reason"] = reason });
            return new LeadDecisionResult(ok, parsed, failureReason, reason, nextSettings, effects);
        }

        if (leadAdapter is null) return Stop("NO_LEAD_AVAILABLE", ok: false, failureReason: "NO_LEAD_AVAILABLE");

        var roster = RosterOf(request.Roster, services);
        var plans = PlansOf(services);
        var project = Field(request.Item.Data, "project") as JsonObject;
        var latest = Array(request.Item.Data, "revisions").LastOrDefault() as JsonObject;
        var folder = Truth(Field(project, "folder")) ? Text(project, "folder") : null;
        var linked = LinkedFolder(folder, services.FileSystem);
        var refusalStore = new JsonObject();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var triedModels = new Dictionary<string, Model>(StringComparer.Ordinal);
        JsonObject? decision = null;
        string? failure = null;
        Model? strictAgain = null;

        for (var round = 0; round < 3 && decision is null; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.IsCurrent is not null && !request.IsCurrent())
                return new LeadDecisionResult(false, null, FailureReason: "cancelled", Settings: nextSettings, Effects: effects);

            var lead = strictAgain ?? ResolveLeadModel(services, null, roster);
            var ask = strictAgain is not null ? Autonomy.StrictDecisionPrompt(request.Question) : request.Question;
            strictAgain = null;
            if (lead is not null && tried.Contains(ModelId(lead)) && ask == request.Question)
            {
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in tried)
                {
                    var seen = roster.FirstOrDefault(m => ModelId(m) == id) ?? triedModels.GetValueOrDefault(id);
                    if (seen is not null && ProviderOf(seen) is { Length: > 0 } p) used.Add(p);
                }
                lead = roster.FirstOrDefault(model =>
                    !tried.Contains(ModelId(model))
                    && !used.Contains(ProviderOf(model))
                    && CanOrchestrateModel(services, model)
                    && !IsQuotaBlocked(services, model, plans)
                    && !IsPlanExhausted(services, model, plans));
            }
            if (lead is null) break;

            var leadId = ModelId(lead);
            tried.Add(leadId);
            triedModels[leadId] = lead;

            var rawState = new JsonObject { ["name"] = Copy(Field(project, "name")) };
            if (latest is not null && latest.ContainsKey("direction")) rawState["goal"] = Copy(latest["direction"]);
            if (project is not null && project.ContainsKey("folder")) rawState["folder"] = Copy(project["folder"]);

            var invocation = new LeadAskInvocation(
                Question: ask,
                Model: lead,
                ProjectId: request.ProjectId,
                AskKey: "auto:" + request.ProjectId,
                Folder: linked,
                Github: null,
                State: AttachWorkPlan(services, linked, rawState),
                AllowActions: false,
                OnProgress: services.OnProgress);

            EmitEffect(services, effects, "ledger_start", request.ProjectId,
                LedgerStartPayload("orchestrator", request.ProjectId, request.EvalMegaTaskId, lead, "clarification"));
            LeadSpokenResponse spoken;
            try
            {
                spoken = await leadAdapter.AskModelAsync(invocation, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var failed = !string.IsNullOrEmpty(spoken.Failure);
                EmitEffect(services, effects, "ledger_finish", request.ProjectId,
                    LedgerFinishPayload(lead, failed ? "FAILED" : "COMPLETE", spoken.ProcessExit, spoken.Tokens));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                EmitEffect(services, effects, "ledger_finish", request.ProjectId, LedgerFinishPayload(lead, "BLOCKED"));
                throw;
            }
            catch (Exception ex)
            {
                EmitEffect(services, effects, "ledger_finish", request.ProjectId, LedgerFinishPayload(lead, "FAILED"));
                failure = "LEAD_FAILED: " + ex.Message;
                continue;
            }

            if (request.IsCurrent is not null && !request.IsCurrent())
                return new LeadDecisionResult(false, null, FailureReason: "cancelled", Settings: nextSettings, Effects: effects);

            var text = spoken.Answer ?? "";
            var refused = !string.IsNullOrEmpty(spoken.Failure) ? ProviderRefusal.RefusalReason(text) : null;
            if (refused is not null)
            {
                var row = ProviderRefusal.RecordRefusal(refusalStore, leadId, refused, services.Now());
                EmitEffect(services, effects, "refusal", request.ProjectId, new JsonObject
                {
                    ["modelId"] = leadId,
                    ["reason"] = refused,
                    ["at"] = Copy(row["at"]),
                    ["until"] = Copy(row["until"])
                });
                if (IsCreditsNeeded(text))
                {
                    EmitEffect(services, effects, "credits_needed", request.ProjectId, new JsonObject
                    {
                        ["modelId"] = leadId,
                        ["at"] = services.Now(),
                        ["reason"] = text
                    });
                }
                EmitEffect(services, effects, "note", request.ProjectId, new JsonObject
                {
                    ["text"] = $"Lead {ModelLabel(lead)} refused work: {refused}"
                });
            }

            decision = !string.IsNullOrEmpty(spoken.Failure) ? null : Autonomy.ParseDecision(text);
            if (decision is null)
            {
                failure = "LEAD_UNCONFIRMED";
                EmitEffect(services, effects, "note", request.ProjectId, new JsonObject
                {
                    ["text"] = $"Lead {ModelLabel(lead)} gave no readable decision{(!string.IsNullOrEmpty(spoken.Failure) ? " (" + spoken.Failure + ")" : "")}."
                });
                if (string.IsNullOrEmpty(spoken.Failure) && ask == request.Question) strictAgain = lead;
            }
        }

        if (decision is null)
        {
            var reason = failure ?? "NO_LEAD_AVAILABLE";
            return Stop(reason, ok: false, failureReason: reason);
        }

        if (OptText(Field(decision, "summary")) is { Length: > 0 } summary && !string.IsNullOrEmpty(request.EvalMegaTaskId))
        {
            EmitEffect(services, effects, "lead_summary", request.ProjectId, new JsonObject
            {
                ["megaTaskId"] = request.EvalMegaTaskId,
                ["summary"] = summary
            });
        }

        if (Field(decision, "assumptions") is JsonArray newAssumptions && newAssumptions.Count > 0)
        {
            var combined = new JsonArray();
            foreach (var existing in Array(nextSettings, "assumptions")) combined.Add(Copy(existing));
            var texts = new List<string>();
            foreach (var item in newAssumptions)
            {
                combined.Add(Copy(item));
                texts.Add(OptText(item) ?? Str(item));
            }
            var capped = new JsonArray(combined.TakeLast(50).Select(Copy).ToArray());
            nextSettings["assumptions"] = capped;
            EmitEffect(services, effects, "assumption", request.ProjectId, new JsonObject
            {
                ["added"] = newAssumptions.DeepClone(),
                ["assumptions"] = capped.DeepClone()
            });
            EmitEffect(services, effects, "note", request.ProjectId, new JsonObject
            {
                ["text"] = "Lead assumptions: " + string.Join("; ", texts)
            });
        }

        if (Truth(Field(decision, "done"))) return Stop("GOAL_REACHED", ok: true, parsed: decision);
        if (!Truth(Field(decision, "next"))) return new LeadDecisionResult(true, decision, Settings: nextSettings, Effects: effects);

        if (Array(nextSettings, "approved").Count >= Autonomy.LIMITS.MaxMegaTasks)
            return Stop("MEGATASK_LIMIT", ok: true, parsed: decision);

        var nextObjective = OptText(Field(decision, "next")) ?? Str(Field(decision, "next")!);
        bool IsCurrent() => !cancellationToken.IsCancellationRequested && (request.IsCurrent?.Invoke() ?? true);
        var prepared = await PrepareTaskAsync(services, request.ProjectId, nextObjective,
            IsCurrent, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent())
            return new LeadDecisionResult(false, null, FailureReason: "cancelled", Settings: nextSettings, Effects: effects);
        EmitEffect(services, effects, "prepare_task", request.ProjectId, new JsonObject
        {
            ["next"] = nextObjective,
            ["result"] = prepared.DeepClone()
        });
        if (!Truth(Field(prepared, "ok")))
        {
            EmitEffect(services, effects, "note", request.ProjectId, new JsonObject
            {
                ["text"] = "Autonomous next step was not prepared: " + Text(prepared, "reason", "unknown reason")
            });
        }
        return new LeadDecisionResult(true, decision, Settings: nextSettings, Effects: effects);
    }

    static readonly Regex TypedGrillMeRegex = new(@"^grill me[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex TypedGrillDocsRegex = new(@"^grill with (?:the )?docs[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex EndGrillCmdRegex = new(@"^(?:stop|finish|end) grill(?:ing)?[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex QuotaStatusCmdRegex = new(@"^(?:quota(?: remaining)?|remaining quota|how much quota is left\??|provider status|model status)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex StateStatusCmdRegex = new(@"^(?:status|current (?:process )?state|current checkpoint(?: identity)?|selected project)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex SuggestedGoalRegex = new(@"^\s*\**Suggested next goal:\**\s*([^\r\n]+)\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex StripSuggestedGoalRegex = new(@"^\s*\**Suggested next goal:\**\s*[^\r\n]*(?:\r?\n)?", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex AdditionRegex = new(@"^\s*\**Addition to this work:\**\s*([^\r\n]+)\r?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex StripAdditionRegex = new(@"^\s*\**Addition to this work:\**\s*[^\r\n]*(?:\r?\n)?", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    static readonly Regex StructuredDraftRegex = new("^" + JsSpace + @"*(?:\{|```)", RegexOptions.ECMAScript);

    static string StripTalkLine(string answer, Regex stripPattern)
    {
        var stripped = stripPattern.Replace(answer, "");
        return Trim(Regex.Replace(stripped, @"\n{3,}", "\n\n"));
    }

    static JsonObject? PickAskedProjectJson(JsonObject? snapData, string? projectId, string? question)
    {
        var rows = Array(snapData, "projects").OfType<JsonObject>().ToArray();
        if (rows.Length == 0) return null;
        if (projectId is not null && Trim(projectId).Length > 0)
        {
            return rows.FirstOrDefault(r => Text(r, "id") == projectId);
        }
        var ql = (question ?? "").ToLowerInvariant();
        return rows.FirstOrDefault(r =>
            ql.Contains(Text(r, "name").ToLowerInvariant(), StringComparison.Ordinal) ||
            ql.Contains(Text(r, "id").ToLowerInvariant(), StringComparison.Ordinal))
            ?? (rows.Length == 1 ? rows[0] : null);
    }

    static string FormatOrderAnswer(JsonObject order, JsonObject? snapData, JsonObject? actedPayload, bool actedOk, string? actedText, string? actedReason, string who, string question)
    {
        var action = Text(order, "action");
        var projectId = Text(order, "projectId");
        var name = Text(order, "name", projectId);
        if (actedOk && action == "quota")
        {
            var lines = new List<string>();
            foreach (var pool in Array(snapData, "plans").OfType<JsonObject>())
            {
                var label = Text(pool, "label", Text(pool, "id"));
                var desc = OptText(Quota.Describe(pool)["text"]) ?? "REMAINING UNREAD";
                var resetAt = OptText(pool["resetAt"]);
                if (string.IsNullOrEmpty(resetAt))
                {
                    var leftPct = pool["leftPct"];
                    foreach (var win in Array(pool, "windows").OfType<JsonObject>())
                        if (JsonNode.DeepEquals(win["leftPct"], leftPct) && OptText(win["resetAt"]) is { Length: > 0 } wr) { resetAt = wr; break; }
                }
                lines.Add($"{label}: {desc}{(string.IsNullOrEmpty(resetAt) ? "" : "; resets " + resetAt)}");
            }
            return lines.Count > 0 ? string.Join("\n", lines) : "REMAINING UNREAD";
        }
        if (actedOk && action == "state")
        {
            var selected = Array(snapData, "projects").OfType<JsonObject>().FirstOrDefault(r => Text(r, "id") == projectId);
            if (selected is null) return "No project selected.";
            var cp = Field(selected, "checkpoint");
            var cpText = Truth(cp) ? " Checkpoint: " + Text(cp, "id", Text(cp, "path", "saved")) : "";
            return $"{Text(selected, "name")}: {Text(selected, "status")}. {Text(selected, "next")}{cpText}";
        }
        if (actedOk && !string.IsNullOrEmpty(actedText)) return actedText;
        if (actedOk && action == "model") return $"{who}: {Text(order, "label", Text(order, "id"))} is now {(Truth(order["enabled"]) ? "on" : "off")}.";
        if (actedOk && action == "lead") return $"{who}: orchestrator lead is now {Text(order, "label", Text(order, "id"))}.";
        if (actedOk && action == "saving-mode")
        {
            var it = Match(question, "modalit");
            var savedMode = ModelRouting.PersistedSavingMode(Text(order, "savingMode"));
            var modeLabel = it ? (savedMode == "safe" ? "Sicura" : savedMode == "ultra-safe" ? "Ultra sicura" : "Normale") : ModelRouting.SavingModeLabel(savedMode);
            var sentence = it ? $"La modalità di risparmio è ora {modeLabel}. Vale dal prossimo passo." : $"Saving mode is now {modeLabel}. It applies from the next step.";
            return $"{who}: {sentence}";
        }
        if (actedOk && action == "mode")
        {
            var chatMode = Text(order, "chatMode");
            var modeLabel = chatMode == "talk" ? "Talk" : "Plan";
            return $"{who}: mode is now {modeLabel}.";
        }
        if (actedOk && action == "endGrill") return "Grill session ended. Use Grill me to begin a new conversation.";
        if (actedOk && action == "grillPropose")
        {
            var kind = Text(order, "kind") switch { "docs" => "Grill with the docs", "me" => "Grill me", _ => "Grill" };
            return $"{who}: {kind} is now {(Truth(order["enabled"]) ? "on" : "off")}. The orchestrator skips grill when both are off.";
        }
        if (actedOk && action == "start")
        {
            var phase = Text(actedPayload, "phase") switch
            {
                "grill" => "Grill first, then workers can start",
                "plan" => "Plan ready. Approve to assign workers",
                _ => "started"
            };
            return $"{who}: {phase} on {name}. I dispatch; I do not do the job.";
        }
        if (actedOk && (action == "approve" || action == "watching"))
        {
            var rowNow = Array(snapData, "projects").OfType<JsonObject>().FirstOrDefault(r => Text(r, "id") == projectId);
            var dispatch = Array(rowNow, "dispatch");
            var crew = dispatch.Count > 0 ? string.Join(" + ", dispatch.Select(p => Text(p, "label"))) : Text(rowNow, "workerLabel", "workers");
            return action == "approve"
                ? $"{who}: assigned {crew} on {name}. I dispatch; I do not do the job."
                : $"{who}: {name} is already with {crew}. I dispatch; I do not do the job.";
        }
        if (actedOk && action == "skip") return $"{who}: skipped grill on {name}.";
        if (action == "reject") return $"{who}: Pipeline is not available.";
        if (actedOk) return $"{who}: {action} on {name}.";
        return $"{who}: could not {action}: {(string.IsNullOrEmpty(actedReason) ? "failed" : actedReason)}.";
    }

    static string? ValidateReassignCrew(JsonObject action, Project? row)
    {
        if (action.Any(p => p.Key is not ("type" or "projectId" or "assignments" or "why"))
            || Text(action, "type") != "reassign_crew"
            || action["projectId"]?.GetValueKind() != JsonValueKind.String
            || string.IsNullOrEmpty(Text(action, "projectId"))
            || action["assignments"] is not JsonObject)
            return "Invalid crew change.";
        var rowId = OptText(Field(row?.Data, "id"));
        if (row is null || Text(action, "projectId") != rowId)
            return "The crew change targets a different project from the selected one.";
        var megaTaskId = OptText(Field(row.Data, "megaTaskId")) ?? OptText(Field(Field(row.Data, "megaTask"), "id"));
        return string.IsNullOrEmpty(megaTaskId) ? "This project has no proposed or stopped work." : null;
    }

    static string? ValidateResearchDispatch(
        JsonObject action,
        IReadOnlyList<Model> roster,
        IReadOnlyList<JsonObject> snapProjects,
        LeadServices services,
        JsonArray plans,
        string savingMode)
    {
        if (action.Any(p => p.Key is not ("type" or "projectId" or "modelId" or "title" or "detail")) || Text(action, "type") != "dispatch_research")
            return "Invalid research dispatch action.";
        foreach (var key in new[] { "projectId", "modelId", "title", "detail" })
            if (action[key] is not JsonValue v || !v.TryGetValue<string>(out var val) || Trim(val).Length == 0)
                return "Research action requires " + key + ".";
        var title = OptText(action["title"]) ?? "";
        var detail = OptText(action["detail"]) ?? "";
        if (title.Length > 200 || detail.Length > 12000)
            return "Research instructions are too large (title 200, detail 12000 characters).";
        var actionProjectId = OptText(action["projectId"])!;
        if (!snapProjects.Any(p => Text(p, "id") == actionProjectId))
            return "Research project not found: " + actionProjectId;
        var actionModelId = OptText(action["modelId"])!;
        var worker = roster.FirstOrDefault(m => ModelId(m) == actionModelId);
        if (worker is null) return "Requested research model is not in the roster: " + actionModelId;
        int? tier = Finite(Field(worker.Data, "leadTier")) ? (int)Number(Field(worker.Data, "leadTier")!) : OrchestratorTeamModels.LeadTier(worker.Data);
        if (!OrchestratorTeamModels.AllowedBySavingMode(savingMode, worker.Data))
            return ModelRouting.SavingModeError(worker.Data, savingMode, tier);
        if (!ModelRoles.PermitsAssignment(worker.Data, new JsonObject { ["kind"] = "research" }))
            return "The requested model is not enabled for the Worker role: " + actionModelId;
        if (!Truth(Field(worker.Data, "enabled")) || IsFalse(Field(worker.Data, "available")) || IsFalse(Field(worker.Data, "providerEnabled")))
            return "Requested research model is disabled or unavailable: " + actionModelId;
        if (ProviderAdapters.AdapterFor(worker.Data)?.SourceProtectedResearch != true)
            return "Source-protected research is not supported by the requested adapter: " + ProviderOf(worker);
        if (worker.Data.ContainsKey("provider") || worker.Data.ContainsKey("adapter") || worker.Data.ContainsKey("cli"))
        {
            if (ModelRouting.ModelRouteError(worker.Data) is { } routeErr) return routeErr;
        }
        var policy = ModelPolicy.UsagePolicy(worker.Data, plans);
        if (IsQuotaBlocked(services, worker, plans)) return OptText(policy["reason"]) ?? "Quota policy blocked research model.";
        if (IsPlanExhausted(services, worker, plans) || RemainingBelowCost(worker))
            return "Requested research model has no remaining usage: " + actionModelId;
        return null;
    }

    public static ValueTask<LeadAskTurnResult> AskAsync(
        LeadAskTurnRequest request,
        ILeadModelAdapter? leadAdapter,
        CancellationToken cancellationToken = default) =>
        AskAsync(request, leadAdapter, new LeadServices(), cancellationToken);

    public static async ValueTask<LeadAskTurnResult> AskAsync(
        LeadAskTurnRequest request,
        ILeadModelAdapter? leadAdapter,
        LeadServices services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effects = new List<LeadEffect>();
        LeadAskTurnResult Fail(string reason) => new(false, "", null, 0, null, null, [], null, Reason: reason, Effects: effects);
        LeadAskTurnResult Cancelled(string chatKey)
        {
            EmitEffect(services, effects, "thinking_clear", chatKey.Length > 0 ? chatKey : null, new JsonObject { ["chatKey"] = chatKey });
            EmitEffect(services, effects, "cancelled", chatKey.Length > 0 ? chatKey : null, new JsonObject { ["chatKey"] = chatKey });
            return new(true, "", null, 0, null, null, [], null, Reason: "cancelled", Effects: effects,
                Metadata: new JsonObject { ["cancelled"] = true, ["chatKey"] = chatKey });
        }

        if (request.RequestedGrillKind is not null && request.RequestedGrillKind is not ("me" or "docs"))
            return Fail("Invalid Grill kind.");
        if (request.RequestMode is not ("default" or "gold"))
            return Fail("Invalid request mode.");

        var snap = request.Snapshot;
        var roster = RosterOf(request.Roster, services);
        var plans = PlansOf(services, snap);
        JsonObject? goldModel = null;
        if (request.RequestMode == "gold")
        {
            var goldId = OptText(Field(request.GoldModel, "id")) ?? OptText(Field(snap.Data, "goldModelId"));
            var selectedGold = string.IsNullOrEmpty(goldId) ? null : roster.FirstOrDefault(m => ModelId(m) == goldId);
            var invalidGold = selectedGold is null
                || !Truth(Field(selectedGold.Data, "enabled"))
                || IsFalse(Field(selectedGold.Data, "available"))
                || IsFalse(Field(selectedGold.Data, "providerEnabled"))
                || !ModelRoles.PermitsAssignment(selectedGold.Data, new JsonObject());
            if (!invalidGold && request.GoldModel is not null)
            {
                if (OptText(Field(request.GoldModel, "provider")) is { } gp && gp != ProviderOf(selectedGold!)) invalidGold = true;
                if (OptText(Field(request.GoldModel, "cli")) is { } gc && gc != OptText(Field(selectedGold!.Data, "cli"))) invalidGold = true;
            }
            if (invalidGold)
                return Fail("Choose an available Gold model with the Worker role enabled. No substitute will be used.");
            goldModel = new JsonObject
            {
                ["id"] = ModelId(selectedGold!),
                ["provider"] = ProviderOf(selectedGold!) is { Length: > 0 } p ? p : null,
                ["cli"] = OptText(Field(selectedGold!.Data, "cli"))
            };
        }

        var q = Trim(request.Question ?? "");
        if (q.Length == 0) return Fail("empty question");
        if (System.Text.Encoding.UTF8.GetByteCount(q) > 1024 * 1024) return Fail("message too long (maximum 1 MiB)");

        var projectId = request.ProjectId ?? (OptText(Field(snap.Data, "selectedProjectId")) is { Length: > 0 } sel ? sel : null);
        var chatKey = projectId ?? "";
        var askedAt = services.NowMilliseconds();
        var snapProjects = Array(snap.Data, "projects").OfType<JsonObject>().ToArray();
        if (projectId is not null && !snapProjects.Any(r => Text(r, "id") == projectId))
            return Fail("Selected project no longer exists. Select a project before sending.");

        var ql = q.ToLowerInvariant();
        var mentionedProjects = snapProjects.Where(r =>
            (Text(r, "id") is { Length: > 0 } pid && ql.Contains(pid.ToLowerInvariant(), StringComparison.Ordinal)) ||
            (Text(r, "name") is { Length: > 0 } pname && ql.Contains(pname.ToLowerInvariant(), StringComparison.Ordinal))).ToList();
        if (Truth(Field(request.Metadata, "phase2")) && projectId is null)
        {
            foreach (var candidate in mentionedProjects)
            {
                var matched = new[] { Text(candidate, "id"), Text(candidate, "name") }.Where(n => n.Length > 0).Any(n =>
                    Regex.IsMatch(q, $@"^(?:start|stop|resume|approve(?: run)?|avvia|ferma|riprendi|approva)\s+{Regex.Escape(n)}[.!?]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                if (matched) { projectId = Text(candidate, "id"); break; }
            }
        }
        if (projectId is not null && mentionedProjects.Any(r => Text(r, "id") != projectId))
            return Fail("Message names a different project from the selected recipient. Select the intended project before sending.");
        if (projectId is null && mentionedProjects.Count > 1)
            return Fail("Message names multiple projects. Select the intended recipient.");
        if (!request.IsCurrent()) return Cancelled(chatKey);

        var contextProjectId = projectId ?? (mentionedProjects.Count == 1 ? Text(mentionedProjects[0], "id") : null);
        var filteredConversation = new JsonArray();
        var turnHistories = new List<TurnHistory>();
        foreach (var node in request.ConversationHistory)
        {
            if (node is not JsonObject item) continue;
            var itemProj = OptText(Field(item, "projectId"));
            var itemChat = OptText(Field(item, "chatSessionId"));
            var projectOk = string.IsNullOrEmpty(contextProjectId) || itemProj == contextProjectId;
            var chatOk = string.IsNullOrEmpty(request.ChatSessionId)
                || (itemChat == request.ChatSessionId && (!string.IsNullOrEmpty(contextProjectId) || itemProj is null));
            if (!projectOk || !chatOk) continue;
            filteredConversation.Add((JsonNode)item.DeepClone());
            turnHistories.Add(new TurnHistory(Text(item, "question"), Text(item, "answer"), itemProj, itemChat, OptText(Field(item, "grillKind"))));
        }

        var typedGrill = TypedGrillMeRegex.IsMatch(q) ? "me" : TypedGrillDocsRegex.IsMatch(q) ? "docs" : null;
        var previousGrill = turnHistories.FirstOrDefault(t => t.ProjectId == projectId)?.GrillKind;
        if (string.IsNullOrEmpty(previousGrill)) previousGrill = null;
        var requestedGrill = request.RequestedGrillKind ?? typedGrill;
        ParsedOrder? preliminaryOrder = requestedGrill is not null ? null
            : EndGrillCmdRegex.IsMatch(q) ? new SystemQueryOrder("endGrill", projectId)
            : OrchestratorOrderParsing.ParseOrder(q, snap.Data, projectId);
        var grillKind = requestedGrill ?? (preliminaryOrder is null ? previousGrill : null);
        if (grillKind is not null && PickAskedProjectJson(snap.Data, projectId, q) is null)
            return Fail("Select the project before starting Grill.");
        var researchRequested = grillKind is null && OrchestratorOrderParsing.ResearchIntent(q, turnHistories);
        if (researchRequested && PickAskedProjectJson(snap.Data, projectId, q) is null)
            return Fail("Select a project or name it explicitly before dispatching research.");
        var statusKind = QuotaStatusCmdRegex.IsMatch(q) ? "quota" : StateStatusCmdRegex.IsMatch(q) ? "state" : null;
        var order = grillKind is not null || researchRequested ? null
            : statusKind is not null ? new SystemQueryOrder(statusKind, projectId)
            : preliminaryOrder;
        if (order is not null)
        {
            if (order.Action is "start" or "stop" or "resume" or "approve" or "skip" or "watching" && string.IsNullOrEmpty(order.ProjectId))
                return Fail("Select a project or name it explicitly before issuing a command.");
            if (goldModel is not null && order.Action != "start")
                return Fail("Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active.");
        }

        var rowJson = PickAskedProjectJson(snap.Data, projectId, q);
        var row = request.TargetProject is not null && rowJson is not null && Text(request.TargetProject.Data, "id") == Text(rowJson, "id")
            ? request.TargetProject
            : rowJson is not null ? new Project(rowJson) : null;
        JsonObject? currentTask = null;
        var currentTaskId = request.CurrentTaskId;
        var talking = ChatModeOf(snap, row, projectId ?? contextProjectId) == "talk";
        if (!talking && order is null && grillKind is null && !researchRequested && OrchestratorOrderParsing.FreshTaskIntent(q)
            && (services.PrepareTask is not null || services.PrepareTaskWithContext is not null))
        {
            var target = PickAskedProjectJson(snap.Data, projectId, q);
            if (target is null) return Fail("Select the project for this new task.");
            var targetId = Text(target, "id");
            var prepared = await PrepareTaskAsync(services, targetId, q,
                () => !cancellationToken.IsCancellationRequested && request.IsCurrent(), goldModel, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.IsCurrent()) return Cancelled(chatKey);
            EmitEffect(services, effects, "prepare_task", targetId, new JsonObject
            {
                ["question"] = q,
                ["goldModel"] = goldModel?.DeepClone(),
                ["result"] = prepared.DeepClone()
            });
            if (!Truth(Field(prepared, "ok")))
                return Fail(OptText(Field(prepared, "reason")) ?? "");
            currentTask = Field(prepared, "task") as JsonObject;
            currentTaskId = OptText(Field(currentTask, "id")) ?? currentTaskId;
            if (Field(prepared, "snapshot") is JsonObject nextSnap)
            {
                snap = new Snapshot(nextSnap);
                snapProjects = Array(snap.Data, "projects").OfType<JsonObject>().ToArray();
                rowJson = PickAskedProjectJson(snap.Data, projectId, q);
                row = rowJson is not null ? new Project(rowJson) : row;
            }
        }

        string answer;
        JsonObject? diagnostic = null;
        Model? invokedLead = null;
        var thinking = "";
        long? ms = null;
        var actionResults = new List<LeadActionResult>();
        string? orderName = order?.Action;

        if (order is not null)
        {
            var orderJson = order.ToJsonObject();
            if (goldModel is not null) orderJson["goldModel"] = goldModel.DeepClone();
            if (order.Action == "start" && previousGrill is not null)
            {
                var grillTurns = filteredConversation.OfType<JsonObject>()
                    .Where(t => OptText(Field(t, "projectId")) == projectId && !string.IsNullOrEmpty(OptText(Field(t, "grillKind"))))
                    .Take(12).Reverse()
                    .Select(t => $"User: {Text(t, "question")}\nOrchestrator: {Text(t, "answer")}");
                var grillContext = string.Join("\n\n", grillTurns);
                if (grillContext.Length > 24000) grillContext = grillContext[^24000..];
                orderJson["grillContext"] = grillContext;
            }
            if (order.Action == "approve")
            {
                var approveRow = snapProjects.FirstOrDefault(r => Text(r, "id") == order.ProjectId);
                if (OptText(Field(approveRow, "megaTaskId")) is { Length: > 0 } mtId) orderJson["megaTaskId"] = mtId;
            }

            EmitEffect(services, effects, order.Action == "endGrill" ? "end_grill" : "order", order.ProjectId, (JsonObject)orderJson.DeepClone());
            var actedOk = true;
            string? actedText = null, actedReason = null;
            JsonObject? actedPayload = null;
            if (services.ApplyAction is not null && order.Action is not ("quota" or "state" or "endGrill" or "reject"))
            {
                var acted = await services.ApplyAction(new LeadAction(orderJson.DeepClone()), cancellationToken).ConfigureAwait(false);
                actedOk = acted.Ok;
                actedText = acted.Text;
                actedReason = acted.Reason;
                actedPayload = acted.Payload;
                if (Field(acted.Payload, "snapshot") is JsonObject nextSnap) snap = new Snapshot(nextSnap);
            }
            var who = Text(snap.Data, "orchestratorLabel", OrchName);
            answer = FormatOrderAnswer(orderJson, snap.Data, actedPayload, actedOk, actedText, actedReason, who, q);
        }
        else
        {
            var fallback = AnswerQuestion(q, snap, projectId);
            var lead = ResolveLeadModel(services, snap, roster);
            var history = Briefing.RecentContext(filteredConversation);
            if (grillKind is not null && leadAdapter is null)
            {
                answer = "An orchestrator model connection is required for Grill me. Select an available lead and retry.";
            }
            else if (leadAdapter is not null && lead is null)
            {
                answer = "Pick an orchestrator model first (GPT-6 Astra, Grok 4.7, …). I coordinate workers; I do not do the job.";
            }
            else if (leadAdapter is not null && lead is not null)
            {
                if (!request.IsCurrent()) return Cancelled(chatKey);
                var started = services.NowMilliseconds();
                var thinkingState = new JsonObject
                {
                    ["question"] = q,
                    ["projectId"] = contextProjectId,
                    ["chatSessionId"] = request.ChatSessionId,
                    ["lead"] = ModelLabel(lead),
                    ["thinking"] = "",
                    ["draft"] = "",
                    ["startedAt"] = services.Now(),
                    ["ms"] = 0
                };
                EmitEffect(services, effects, "thinking_start", contextProjectId, (JsonObject)thinkingState.DeepClone());
                try
                {
                    invokedLead = lead;
                    var rowId = OptText(Field(row?.Data, "id"));
                    var askProjectId = rowId ?? fallback.ProjectId;
                    var linked = LinkedFolder(Text(row?.Data, "folder"), services.FileSystem);
                    var packed = PackState(snap, row, request.StateFile, services.FileSystem);
                    var baseline = request.BriefingBaseline ?? (Field(Field(snap.Data, "briefingBaselines"), rowId ?? "") as JsonObject);
                    var compacted = services.CompactState is not null ? services.CompactState(packed, baseline) : Briefing.CompactState(packed, baseline);
                    compacted["history"] = history;
                    compacted["contextArchive"] = !string.IsNullOrEmpty(contextProjectId) ? null : request.StateFile;
                    compacted["earlierTurns"] = Math.Max(0, filteredConversation.Count - history.Count);
                    compacted["currentRequest"] = new JsonObject
                    {
                        ["projectId"] = rowId,
                        ["text"] = q,
                        ["taskId"] = currentTaskId,
                        ["requestMode"] = request.RequestMode,
                        ["goldModel"] = goldModel?.DeepClone(),
                        ["grillKind"] = grillKind
                    };
                    var askState = AttachWorkPlan(services, linked, compacted);
                    var chatMode = ChatModeOf(snap, row, rowId ?? projectId);
                    var workRunning = WorkInFlight(row);
                    var github = Truth(Field(row?.Data, "github")) ? OptText(Field(row?.Data, "github")) : null;

                    var progressGate = new object();
                    void OnModelProgress(LeadAskProgress part)
                    {
                        if (cancellationToken.IsCancellationRequested || !request.IsCurrent()) return;
                        string nextThinking;
                        string nextDraft;
                        long nextMs;
                        JsonObject progressPayload;
                        lock (progressGate)
                        {
                            nextThinking = part.Thinking ?? OptText(thinkingState["thinking"]) ?? "";
                            nextDraft = part.Answer is not null
                                ? (StructuredDraftRegex.IsMatch(part.Answer) ? "" : part.Answer)
                                : OptText(thinkingState["draft"]) ?? "";
                            nextMs = part.Ms ?? Math.Max(0, services.NowMilliseconds() - started);
                            thinkingState["thinking"] = nextThinking;
                            thinkingState["draft"] = nextDraft;
                            thinkingState["ms"] = nextMs;
                            progressPayload = (JsonObject)thinkingState.DeepClone();
                        }
                        EmitEffect(services, effects, "progress", contextProjectId, progressPayload);
                        services.OnProgress?.Invoke(new LeadAskProgress(nextThinking, part.Answer is not null ? nextDraft : null, nextMs));
                    }

                    async ValueTask<LeadSpokenResponse> AskThisAsync(Model model)
                    {
                        var inv = new LeadAskInvocation(
                            Question: q,
                            Model: model,
                            ProjectId: askProjectId,
                            AskKey: "chat:" + (askProjectId ?? ""),
                            Folder: linked,
                            Github: github,
                            State: (JsonObject)askState.DeepClone(),
                            AllowActions: grillKind is not null ? false : null,
                            ChatMode: chatMode,
                            WorkRunning: workRunning,
                            GrillKind: grillKind,
                            OnProgress: OnModelProgress);
                        EmitEffect(services, effects, "ledger_start", askProjectId,
                            LedgerStartPayload("orchestrator", askProjectId, OptText(Field(Field(Field(askState, "executionHandoff"), "megaTask"), "id")), model, grillKind is not null ? "clarification" : "user reasoning request"));
                        try
                        {
                            var resp = await leadAdapter.AskModelAsync(inv, cancellationToken).ConfigureAwait(false);
                            EmitEffect(services, effects, "ledger_finish", askProjectId,
                                LedgerFinishPayload(model, !string.IsNullOrEmpty(resp.Failure) ? "FAILED" : "COMPLETE", resp.ProcessExit, resp.Tokens));
                            return resp;
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            EmitEffect(services, effects, "ledger_finish", askProjectId, LedgerFinishPayload(model, "BLOCKED"));
                            throw;
                        }
                        catch
                        {
                            EmitEffect(services, effects, "ledger_finish", askProjectId, LedgerFinishPayload(model, "FAILED"));
                            throw;
                        }
                    }

                    var spoken = await AskThisAsync(lead).ConfigureAwait(false);
                    static bool Died(LeadSpokenResponse s) => s.Failure == "process" || (s.Failure == "provider" && OptText(Field(s.Diagnostic, "code")) == "PROVIDER_EXECUTION_FAILED");
                    static bool OutOfQuota(LeadSpokenResponse s) => OptText(Field(s.Diagnostic, "code")) == "PROVIDER_USAGE_LIMIT";
                    static bool NeedsCredits(LeadSpokenResponse s) => !string.IsNullOrEmpty(s.Failure) && IsCreditsNeeded(!string.IsNullOrEmpty(s.Answer) ? s.Answer : s.Error);

                    var refusedLead = lead;
                    var triedLeads = new HashSet<string>(StringComparer.Ordinal) { ModelId(lead) };
                    var creditsBlocked = new HashSet<string>(StringComparer.Ordinal);
                    var refusalStore = new JsonObject();
                    var savingMode = SavingModeOf(snap, row, rowId ?? projectId);
                    var handedOver = false;

                    void SwitchLead(Model from, Model to, string why)
                    {
                        EmitEffect(services, effects, "lead_switch", contextProjectId, new JsonObject
                        {
                            ["fromId"] = ModelId(from),
                            ["fromLabel"] = ModelLabel(from),
                            ["toId"] = ModelId(to),
                            ["toLabel"] = ModelLabel(to),
                            ["why"] = why
                        });
                        EmitEffect(services, effects, "note", rowId ?? projectId, new JsonObject
                        {
                            ["text"] = $"Planwharf switched the lead: {ModelLabel(from)} → {ModelLabel(to)}. Why: {why}"
                        });
                    }

                    async ValueTask HandOverAsync()
                    {
                        while ((NeedsCredits(spoken) || OutOfQuota(spoken)) && !cancellationToken.IsCancellationRequested && request.IsCurrent() && triedLeads.Count <= 5)
                        {
                            var credits = NeedsCredits(spoken);
                            var refusedId = ModelId(refusedLead);
                            if (credits)
                            {
                                creditsBlocked.Add(refusedId);
                                EmitEffect(services, effects, "credits_needed", contextProjectId, new JsonObject
                                {
                                    ["modelId"] = refusedId,
                                    ["at"] = services.Now(),
                                    ["reason"] = spoken.Answer ?? spoken.Error ?? ""
                                });
                            }
                            else
                            {
                                ProviderRefusal.RecordRefusal(refusalStore, refusedId, OptText(Field(spoken.Diagnostic, "message")) ?? "quota limit", services.Now());
                            }
                            var company = ProviderOf(refusedLead);
                            var next = NextAllowedLead(services, roster, plans, savingMode, refusalStore, creditsBlocked, triedLeads, new LeadSelectionOptions(credits, company.Length > 0 ? company : null));
                            if (next is null) break;
                            SwitchLead(refusedLead, next, ModelLabel(refusedLead) + (credits ? " needs usage credits." : " is out of quota."));
                            triedLeads.Add(ModelId(next));
                            invokedLead = next;
                            refusedLead = next;
                            handedOver = true;
                            spoken = await AskThisAsync(next).ConfigureAwait(false);
                        }
                    }

                    await HandOverAsync().ConfigureAwait(false);
                    if (!handedOver && Died(spoken) && !cancellationToken.IsCancellationRequested && request.IsCurrent())
                    {
                        EmitEffect(services, effects, "note", rowId ?? projectId, new JsonObject
                        {
                            ["text"] = $"{ModelLabel(lead)} failed to answer ({spoken.Failure}). Retrying once."
                        });
                        spoken = await AskThisAsync(lead).ConfigureAwait(false);
                        if (Died(spoken) && !cancellationToken.IsCancellationRequested && request.IsCurrent())
                        {
                            var next = NextAllowedLead(services, roster, plans, savingMode, refusalStore, creditsBlocked, new HashSet<string>(StringComparer.Ordinal) { ModelId(lead) }, new LeadSelectionOptions());
                            if (next is not null)
                            {
                                SwitchLead(lead, next, $"{ModelLabel(lead)} failed to answer twice ({spoken.Failure}).");
                                invokedLead = next;
                                refusedLead = next;
                                triedLeads.Add(ModelId(next));
                                spoken = await AskThisAsync(next).ConfigureAwait(false);
                                await HandOverAsync().ConfigureAwait(false);
                            }
                        }
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (!request.IsCurrent()) return Cancelled(chatKey);

                    var unpackedAnswer = Trim(spoken.Answer ?? "");
                    thinking = Trim(spoken.Thinking ?? "");
                    ms = spoken.Ms ?? Math.Max(0, services.NowMilliseconds() - started);
                    if (row is not null && rowId is not null)
                    {
                        EmitEffect(services, effects, "briefing_baseline", rowId, new JsonObject
                        {
                            ["projectId"] = rowId,
                            ["head"] = Copy(Field(Field(row.Data, "facts"), "lastCommit")) ?? JsonValue.Create(""),
                            ["dirty"] = Take(Array(Field(row.Data, "facts"), "dirty"), 12)
                        });
                    }
                    diagnostic = spoken.Diagnostic?.DeepClone() as JsonObject;
                    if (OptText(Field(diagnostic, "code")) == "PROVIDER_MODEL_UNAVAILABLE")
                    {
                        EmitEffect(services, effects, "model_unavailable", contextProjectId, new JsonObject
                        {
                            ["modelId"] = ModelId(invokedLead),
                            ["reason"] = Copy(Field(diagnostic, "message")),
                            ["at"] = services.Now()
                        });
                    }

                    var envelope = !string.IsNullOrEmpty(spoken.Failure)
                        ? new Envelope(unpackedAnswer, [])
                        : LeadEnvelope(unpackedAnswer, spoken.Actions);
                    answer = !string.IsNullOrEmpty(envelope.Answer)
                        ? envelope.Answer!
                        : $"{ModelLabel(lead)} returned no answer. The provider CLI in the project folder produced nothing.";
                    if (!string.IsNullOrEmpty(envelope.Error)) answer += "\n\n" + envelope.Error;

                    var remainingActions = envelope.Actions.Select(a => a?.DeepClone()).ToList();
                    if (chatMode == "talk")
                    {
                        if (SuggestedGoalRegex.IsMatch(answer)) answer = StripTalkLine(answer, StripSuggestedGoalRegex);
                        if (AdditionRegex.IsMatch(answer)) answer = StripTalkLine(answer, StripAdditionRegex);
                        remainingActions.Clear();
                    }

                    var modeNow = SavingModeOf(snap, row, rowId ?? projectId);
                    if (modeNow != "normal" && invokedLead is not null && !OrchestratorTeamModels.AllowedBySavingMode(modeNow, invokedLead.Data))
                    {
                        var fallbackText = $"{ModelRouting.SavingModeLabel(modeNow)} allows no lead. Planwharf keeps {ModelLabel(invokedLead)}, the best model of the lowest class that can still answer.";
                        var noteKey = "lead-answer:" + modeNow;
                        if (!Truth(Field(Field(row?.Data, "savingNotes"), noteKey)))
                        {
                            EmitEffect(services, effects, "note_once", rowId ?? projectId, new JsonObject { ["key"] = noteKey, ["text"] = fallbackText });
                            if (!string.IsNullOrEmpty(answer) && !answer.Contains(fallbackText, StringComparison.Ordinal))
                                answer += "\n\n" + fallbackText;
                        }
                    }

                    if (remainingActions.Count <= 3)
                    {
                        var crewActions = remainingActions.Where(a => Text(a, "type") == "reassign_crew").ToList();
                        remainingActions = remainingActions.Where(a => Text(a, "type") != "reassign_crew").ToList();
                        foreach (var actionNode in crewActions)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!request.IsCurrent()) return Cancelled(chatKey);
                            var actionObj = actionNode as JsonObject ?? new JsonObject();
                            var leadAction = new LeadAction(actionObj.DeepClone());
                            LeadActionResult receipt;
                            if (ValidateReassignCrew(actionObj, row) is { } crewErr)
                            {
                                receipt = new LeadActionResult(false, null, crewErr, leadAction);
                            }
                            else if (services.ApplyAction is not null)
                            {
                                receipt = await services.ApplyAction(leadAction, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                EmitEffect(services, effects, "reassign_crew", rowId, (JsonObject)actionObj.DeepClone());
                                receipt = new LeadActionResult(true, "Crew change proposed.", null, leadAction, (JsonObject)actionObj.DeepClone());
                            }
                            actionResults.Add(receipt);
                            answer += receipt.Ok ? "\n\n" + receipt.Text : "\n\nCrew not changed: " + receipt.Reason;
                        }
                    }

                    if (remainingActions.Count > 3)
                    {
                        answer += "\n\nInvalid lead actions; no workers were dispatched.";
                    }
                    else if (remainingActions.Count > 0 && !researchRequested)
                    {
                        answer += "\n\nResearch was not dispatched: this message did not authorize research work.";
                        actionResults.Add(new LeadActionResult(false, null, "No research work authorization in the current message.", new LeadAction(remainingActions[0]?.DeepClone())));
                    }
                    else
                    {
                        var mentioned = roster.Where(m =>
                            (OptText(Field(m.Data, "label")) is { Length: > 0 } lbl && ql.Contains(lbl.ToLowerInvariant(), StringComparison.Ordinal)) ||
                            (OptText(Field(m.Data, "cli")) is { Length: > 0 } cliVal && ql.Contains(cliVal.ToLowerInvariant(), StringComparison.Ordinal))).ToList();
                        var requested = mentioned.Count > 1 ? mentioned.Where(m => ModelId(m) != ModelId(lead)).ToList() : mentioned;
                        foreach (var actionNode in remainingActions)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!request.IsCurrent()) return Cancelled(chatKey);
                            var actionObj = actionNode as JsonObject;
                            var leadAction = new LeadAction(actionNode?.DeepClone());
                            var actionModelId = OptText(Field(actionNode, "modelId"));
                            var actionProjId = OptText(Field(actionNode, "projectId"));
                            LeadActionResult receipt;
                            if (goldModel is not null && actionModelId != OptText(Field(goldModel, "id")))
                            {
                                receipt = new LeadActionResult(false, null, $"Gold model requires the exact worker {OptText(Field(goldModel, "id"))}. No model was substituted.", leadAction);
                            }
                            else if (requested.Count == 1 && actionNode is not null && actionModelId != ModelId(requested[0]))
                            {
                                receipt = new LeadActionResult(false, null, $"The action chose a different model from the requested worker: {ModelId(requested[0])}. No model was substituted.", leadAction);
                            }
                            else if (actionNode is not null && row is not null && actionProjId != rowId)
                            {
                                receipt = new LeadActionResult(false, null, "Research action targets a different project from the selected recipient.", leadAction);
                            }
                            else if (actionObj is null)
                            {
                                receipt = new LeadActionResult(false, null, "Invalid research dispatch action.", leadAction);
                            }
                            else if (ValidateResearchDispatch(actionObj, roster, snapProjects, services, plans, modeNow) is { } resErr)
                            {
                                receipt = new LeadActionResult(false, null, resErr, leadAction);
                            }
                            else if (!request.IsCurrent())
                            {
                                return Cancelled(chatKey);
                            }
                            else if (services.ApplyAction is not null)
                            {
                                receipt = await services.ApplyAction(leadAction, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                var payload = new JsonObject
                                {
                                    ["action"] = actionObj.DeepClone(),
                                    ["goldModel"] = goldModel?.DeepClone()
                                };
                                EmitEffect(services, effects, "dispatch_research", actionProjId, payload);
                                receipt = new LeadActionResult(true, null, null, leadAction, new JsonObject
                                {
                                    ["proposal"] = true,
                                    ["research"] = new JsonObject
                                    {
                                        ["projectId"] = actionProjId,
                                        ["projectName"] = Text(row?.Data, "name"),
                                        ["modelId"] = actionModelId,
                                        ["title"] = OptText(actionObj["title"]),
                                        ["detail"] = OptText(actionObj["detail"])
                                    }
                                });
                            }
                            actionResults.Add(receipt);
                            if (!receipt.Ok)
                            {
                                answer += "\n\nResearch was not dispatched: " + receipt.Reason;
                            }
                            else if (!string.IsNullOrEmpty(receipt.Text))
                            {
                                answer += "\n\n" + receipt.Text;
                            }
                            else if (Field(receipt.Payload, "research") is JsonObject resObj)
                            {
                                if (Truth(Field(receipt.Payload, "proposal")) && OptText(resObj["megaTaskId"]) is { Length: > 0 } mtId)
                                {
                                    answer += $"\n\nResearch MegaTask {mtId} proposed for {Text(resObj, "projectName")}. Review the objective and approve this MegaTask before any worker starts.";
                                }
                                else if (Finite(Field(resObj, "pid")))
                                {
                                    var verb = Truth(Field(receipt.Payload, "duplicate")) ? "already running" : "started";
                                    answer += $"\n\nNotebook: {verb} {Text(resObj, "workerLabel")} ({Text(resObj, "provider")}/{Text(resObj, "cli")}), process {Text(resObj, "pid")}, for research \"{Text(resObj, "title")}\". Source files are protected; findings will be saved to report.md.";
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    EmitEffect(services, effects, "thinking_clear", contextProjectId, new JsonObject { ["chatKey"] = chatKey });
                    throw;
                }
                catch (Exception ex)
                {
                    if (!request.IsCurrent()) return Cancelled(chatKey);
                    ms = Math.Max(0, services.NowMilliseconds() - started);
                    diagnostic = ProviderDiagnostics.DiagnoseProvider(lead.Data, string.IsNullOrEmpty(ex.Message) ? "CLI error" : ex.Message);
                    answer = OptText(Field(diagnostic, "message")) ?? ex.Message;
                }
                EmitEffect(services, effects, "thinking_clear", contextProjectId, new JsonObject { ["chatKey"] = chatKey });
                if (!request.IsCurrent()) return Cancelled(chatKey);
            }
            else
            {
                answer = fallback.Answer;
            }
        }

        if (!request.IsCurrent()) return Cancelled(chatKey);
        ms ??= Math.Max(0, services.NowMilliseconds() - askedAt);
        if (currentTask is not null)
            answer += $"\n\nNew task prepared from this message: {Text(currentTask, "title")}. Approve this task to assign workers. No previous checkpoint was resumed.";

        var finalRow = PickAskedProjectJson(snap.Data, projectId, q);
        var resultProjectId = finalRow is not null ? OptText(Field(finalRow, "id")) : OptText(Field(row?.Data, "id"));
        var finalLead = invokedLead ?? ResolveLeadModel(services, snap, roster);
        string? toolRequest = null;
        if (!string.IsNullOrEmpty(resultProjectId) && Runner.ToolRequestRegex.Match(answer) is { Success: true } toolMatch)
        {
            answer = Trim(Runner.ToolRequestRegex.Replace(answer, "", 1));
            toolRequest = toolMatch.Groups[1].Value.ToLowerInvariant();
            EmitEffect(services, effects, "tool_request", resultProjectId, new JsonObject
            {
                ["tool"] = toolRequest,
                ["leadId"] = finalLead is not null ? ModelId(finalLead) : null
            });
        }

        var metadata = new JsonObject
        {
            ["at"] = services.Now(),
            ["projectId"] = resultProjectId,
            ["chatSessionId"] = request.ChatSessionId,
            ["requestMode"] = request.RequestMode,
            ["goldModel"] = goldModel?.DeepClone(),
            ["grillKind"] = grillKind,
            ["order"] = orderName,
            ["toolRequest"] = toolRequest
        };
        EmitEffect(services, effects, "ask_turn", resultProjectId, (JsonObject)metadata.DeepClone());
        return new LeadAskTurnResult(
            Ok: true,
            Answer: answer,
            Thinking: string.IsNullOrEmpty(thinking) ? null : thinking,
            Ms: ms.Value,
            Diagnostic: diagnostic,
            InvokedLead: finalLead,
            ActionResults: actionResults,
            NewTaskId: currentTaskId,
            Reason: null,
            Effects: effects,
            Metadata: metadata);
    }
}
