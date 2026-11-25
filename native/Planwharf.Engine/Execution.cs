// Port of engine/execution.cjs.
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

// Durable records remain JsonObjects: unknown fields, explicit nulls and legacy shapes belong
// to the store, not to a second serialized model. Collaborators share this exact store instance.
// MegaTasks own approval/session, timing, repository and integration state; SubTasks own exact
// assigned/proposed/actual model identity, dependencies, attempts and takeover state. Results
// retain technical evidence and structured identity. Checkpoints retain revision, fingerprint
// and interrupted attempts. Do not populate missing record fields while loading old stores.
public sealed class ExecutionData
{
    public JsonObject Node { get; }
    public ExecutionData(JsonObject store)
    {
        if (store["execution"] is not JsonObject) store["execution"] = new JsonObject();
        Node = (JsonObject)store["execution"]!;
        foreach (var key in new[] { "megaTasks", "subTasks", "results", "checkpoints", "timeline", "decisions" })
            if (Node[key] is not JsonArray) Node[key] = new JsonArray();
        if (!Execution.Truthy(Node["observations"])) Node["observations"] = new JsonObject();
    }
    public JsonArray MegaTasks => (JsonArray)Node["megaTasks"]!;
    public JsonArray SubTasks => (JsonArray)Node["subTasks"]!;
    public JsonArray Results => (JsonArray)Node["results"]!;
    public JsonArray Checkpoints => (JsonArray)Node["checkpoints"]!;
    public JsonArray Timeline => (JsonArray)Node["timeline"]!;
    public JsonArray Decisions => (JsonArray)Node["decisions"]!;
    // JavaScript preserves any truthy legacy observations value, so this is deliberately not
    // narrowed to JsonObject. Callers can distinguish missing properties with ContainsKey.
    public JsonNode? Observations => Node["observations"];
}

public sealed record ExecutionFileStat(long Size, bool IsFile, bool IsSymbolicLink);
public interface IExecutionFiles
{
    byte[] Read(string path);
    void Append(string path, byte[] bytes);
    void Write(string path, byte[] bytes);
    void CreateDirectory(string path);
    string? LinkedWorkspace(string? path);
    string RealDirectory(string path);
    ExecutionFileStat Stat(string path);
}

public sealed class ExecutionFiles : IExecutionFiles
{
    public byte[] Read(string path) => File.ReadAllBytes(path);
    public void Append(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(bytes);
    }
    public void Write(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public string? LinkedWorkspace(string? path) => ProjectWorkspace.LinkedWorkspace(path);
    public string RealDirectory(string path) => ProjectWorkspace.LinkedWorkspace(path) ?? throw new IOException("Directory unavailable.");
    public ExecutionFileStat Stat(string path)
    {
        var file = new FileInfo(path);
        var attrs = file.Attributes;
        return new(file.Length, (attrs & FileAttributes.Directory) == 0, (attrs & FileAttributes.ReparsePoint) != 0);
    }
}

// No process or Git default is used by construction. Dispatch collaborators must be supplied
// by the composition root; inspection never implicitly starts a worker.
public sealed class ExecutionOptions
{
    // RowFor returns the live mutable row in Store. Persist synchronously saves that same
    // store; it must not enqueue a later save. Inspect receives a canonical linked folder.
    public required JsonObject Store { get; init; }
    public required AttemptEngine Attempts { get; init; }
    public required string Directory { get; init; }
    public required Func<string, string?, JsonObject> RowFor { get; init; }
    public required Func<string, JsonObject> Inspect { get; init; }
    public required Func<IReadOnlyList<JsonObject>> Models { get; init; }
    public required Func<string> Now { get; init; }
    public required Action Persist { get; init; }
    public required Func<string?> LeadId { get; init; }
    public string? SessionId { get; init; }
    public Func<JsonObject, JsonObject>? SpawnWorker { get; init; }
    // Hosts must implement owned-process identity/tree checks here. No OS process fallback
    // is implicit; dispatch/recovery slices must enforce availability at their action boundary.
    public Func<int, bool>? IsProcessAlive { get; init; }
    public Func<int, bool>? KillWorker { get; init; }
    public Func<JsonObject?, JsonObject, JsonObject?>? ResolveWorker { get; init; }
    public Func<JsonObject, JsonObject, bool>? WorkerEligible { get; init; }
    public Func<JsonObject?, JsonObject, JsonObject?>? ExplainNoWorker { get; init; }
    public Action<string, string>? OnRefusal { get; init; }
    public Func<JsonObject, IReadOnlyList<string>, JsonObject?>? Replacement { get; init; }
    public Func<string, string>? LeadLabel { get; init; }
    public Func<string, JsonObject?, bool>? IsAutoDecide { get; init; }
    public Action<string, JsonObject>? FreezeSavingMode { get; init; }
    public Func<string> NewId { get; init; } = () => Guid.NewGuid().ToString();
    public IExecutionFiles Files { get; init; } = new ExecutionFiles();
    public Worktrees.GitRunner? Git { get; init; }
    public PlanFile? Plan { get; init; }
}

public sealed partial class ExecutionBridge
{
    public ExecutionOptions Options { get; }
    public ExecutionData Data { get; }
    public ExecutionBridge(ExecutionOptions options)
    {
        Options = options;
        Data = new ExecutionData(options.Store);
    }
    // Lifecycle methods are added by the dependent slices; there are no successful stubs.
}

public static class Execution
{
    public const int MaxParallel = 10;
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static string Text(JsonNode? node) => node is null ? "" : node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString(Json);
    internal static bool Truthy(JsonNode? node) => node is not null && node.GetValueKind() switch
    {
        JsonValueKind.Null or JsonValueKind.False => false,
        JsonValueKind.String => Text(node).Length > 0,
        JsonValueKind.Number => double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n != 0 && !double.IsNaN(n),
        _ => true
    };
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string Slash(string file) => Regex.Replace(file.Replace('\\', '/'), @"^\./|/+$", "", RegexOptions.ECMAScript);
    public static ExecutionBridge CreateExecutionBridge(ExecutionOptions options) => new(options);
    public static string NoVerdictPlan(double tries) => tries >= 2 ? "stop" : tries == 1 ? "switch" : "retry-same";

    // The last step checks the whole when it waits for every other step.
    public static string SplitSummary(IReadOnlyList<JsonObject> subs, Func<string, string> label, string why = "")
    {
        var last = subs.LastOrDefault();
        var checkedAll = subs.Count > 1 && subs.Take(subs.Count - 1).All(sub =>
            (last?["dependencies"] as JsonArray)?.Any(id => JsonNode.DeepEquals(id, sub["id"])) == true);
        var built = subs.Count - (checkedAll ? 1 : 0);
        var shape = built == 1 ? "Lead kept the work as one build step" : $"Lead split the work into {built} parts";
        return shape + (checkedAll ? " and a check of the whole" : "") + ": " +
            string.Join(" · ", subs.Select(sub => $"{Text(sub["title"])} ({label(Text(sub["assignedModelId"]))})")) + "." + (why.Length > 0 ? " Why: " + why : "");
    }

    // Exit 0 alone is not delivery: a new commit and in-scope files are required.
    public static JsonObject? DeliveredWithoutVerdict(JsonObject input)
    {
        if (input["exitCode"] is not JsonValue code || code.GetValueKind() != JsonValueKind.Number ||
            !double.TryParse(code.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var exit) || exit != 0) return null;
        var before = Text(input["before"]); var after = Text(input["after"]);
        if (!Truthy(input["folder"]) || before == after || !Regex.IsMatch(before, "^[a-f0-9]{40,64}$") || !Regex.IsMatch(after, "^[a-f0-9]{40,64}$")) return null;
        var files = (input["names"] as JsonArray ?? []).Select(n => Slash(Text(n))).Where(n => n.Length > 0).ToArray();
        if (files.Length == 0) return null;
        if (input["scope"] is JsonArray { Count: > 0 } scope && files.Any(file => !scope.Any(s => file == Slash(Text(s)) || file.StartsWith(Slash(Text(s)) + "/", StringComparison.Ordinal)))) return null;
        return new JsonObject { ["filesChanged"] = new JsonArray(files.Take(40).Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["summary"] = $"Delivered on disk without a structured result: {files.Length} file(s) committed after the step started." };
    }

    public static JsonObject RepoState(string? folder, JsonObject? facts = null, IExecutionFiles? files = null)
    {
        facts ??= new JsonObject(); files ??= new ExecutionFiles();
        var dirty = (JsonArray?)facts["dirty"]?.DeepClone() ?? new JsonArray();
        var contents = new JsonArray();
        long bytes = 0; var comparable = !Truthy(facts["error"]);
        foreach (var entry in dirty)
        {
            var record = Text(entry);
            var prefix = Regex.IsMatch(record, @"^[MADRCU?!] \S", RegexOptions.ECMAScript) ? 2 : 3;
            var name = record[Math.Min(prefix, record.Length)..].Split(" -> ")[^1];
            name = Regex.Replace(name, "^\"|\"$", "");
            var rootPath = Path.GetFullPath(string.IsNullOrEmpty(folder) ? "." : folder);
            var file = Path.GetFullPath(name, rootPath);
            if (!file.StartsWith(rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { comparable = false; continue; }
            try
            {
                var root = files.LinkedWorkspace(folder);
                if (root is null || !ProjectWorkspace.Within(root, files.RealDirectory(Path.GetDirectoryName(file)!))) { comparable = false; continue; }
                var stat = files.Stat(file); bytes += stat.Size;
                if (!stat.IsFile || stat.IsSymbolicLink || bytes > 8 * 1024 * 1024) { comparable = false; continue; }
                contents.Add((JsonNode)new JsonArray(name, Hash(files.Read(file))));
            }
            catch { contents.Add((JsonNode)new JsonArray(name, "unavailable")); comparable = false; }
        }
        var head = new[] { facts["commitId"], facts["lastCommit"], facts["head"] }.FirstOrDefault(Truthy)?.DeepClone();
        var payload = new JsonArray(head?.DeepClone(), dirty.DeepClone(), contents);
        return new JsonObject { ["repoHead"] = head, ["dirtyFiles"] = dirty, ["comparable"] = comparable,
            ["gitReadable"] = Truthy(facts["git"]), ["fingerprint"] = comparable ? Hash(Encoding.UTF8.GetBytes(payload.ToJsonString(Json))) : null,
            ["disposition"] = !Truthy(facts["git"]) ? "NOT APPLICABLE" : dirty.Count > 0 ? "UNCOMMITTED" : "COMMITTED" };
    }

    public static string StepTimesFile(string dir) => Path.Combine(dir, "step-times.jsonl");
    public static IReadOnlyList<JsonObject> ReadStepTimes(string? dir, IExecutionFiles? files = null)
    {
        if (string.IsNullOrEmpty(dir)) return [];
        try { return TimeLeft.ParseHistory(Encoding.UTF8.GetString((files ?? new ExecutionFiles()).Read(StepTimesFile(dir)))); }
        catch { return []; }
    }
    public static void AppendStepTime(string? dir, JsonObject? record, IExecutionFiles? files = null)
    {
        if (string.IsNullOrEmpty(dir) || record is null) return;
        files ??= new ExecutionFiles();
        files.CreateDirectory(dir);
        files.Write(StepTimesFile(dir), Encoding.UTF8.GetBytes(TimeLeft.HistoryText(ReadStepTimes(dir, files).Append(record).ToArray())));
    }
    public static string StepOutcomesFile(string dir) => StepOutcomes.OutcomesFile(dir);
    public static IReadOnlyList<JsonObject> ReadStepOutcomes(string? dir, IExecutionFiles? files = null)
    {
        if (string.IsNullOrEmpty(dir)) return [];
        try
        {
            var rows = new List<JsonObject>();
            foreach (var line in Encoding.UTF8.GetString((files ?? new ExecutionFiles()).Read(StepOutcomesFile(dir))).Split('\n'))
            {
                try { if (StepOutcomes.StepOutcomeRecord(JsonNode.Parse(line) as JsonObject) is { } clean) rows.Add(clean); }
                catch { /* skip corrupted lines */ }
            }
            return rows;
        }
        catch { return []; }
    }
    public static JsonObject? AppendStepOutcome(string? dir, JsonObject? record, IExecutionFiles? files = null)
    {
        if (string.IsNullOrEmpty(dir) || record is null) return null;
        var clean = StepOutcomes.StepOutcomeRecord(record);
        if (clean is null) return null;
        try
        {
            files ??= new ExecutionFiles(); files.CreateDirectory(dir);
            files.Append(StepOutcomesFile(dir), Encoding.UTF8.GetBytes(clean.ToJsonString(Json) + "\n"));
            return clean;
        }
        catch { return null; }
    }
}
