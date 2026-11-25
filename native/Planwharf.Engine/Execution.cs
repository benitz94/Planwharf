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
    public Func<string, string, string, string, string, int, Worktrees.Part?>? CreateWorktree { get; init; }
    public PlanFile? Plan { get; init; }
    public Func<string, bool>? SafeGitWorkspace { get; init; }
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
    IReadOnlyList<JsonObject> SubTasks(JsonObject mega) => Data.SubTasks.OfType<JsonObject>()
        .Where(sub => JsonNode.DeepEquals(sub["megaTaskId"], mega["id"])).ToArray();

    public void Event(string projectId, string type, string summary, JsonObject reference, string? key = null)
    {
        if (!string.IsNullOrEmpty(key) && Data.Timeline.OfType<JsonObject>().Any(row => Execution.Text(row["key"]) == key)) return;
        var entry = new JsonObject { ["id"] = "EV-" + Options.NewId(), ["projectId"] = projectId,
            ["type"] = type, ["summary"] = summary, ["reference"] = reference.DeepClone(), ["createdAt"] = Options.Now() };
        if (!string.IsNullOrEmpty(key)) entry["key"] = key;
        Data.Timeline.Add((JsonNode)entry);
    }

    public void Observe(JsonObject item)
    {
        var projectId = Execution.Text(item["project"]?["id"]);
        foreach (var revision in (item["revisions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = Execution.Text(revision["id"]);
            Event(projectId, "PLAN_REVISION", $"Living Plan R{id}: {Execution.Text(revision["title"] ?? revision["direction"])}",
                new JsonObject { ["planRevisionId"] = revision["id"]?.DeepClone() }, $"revision:{projectId}:{id}");
        }
        foreach (var note in (item["home"]?["notes"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = Execution.Text(note["id"]);
            Event(projectId, "USER_NOTE", Execution.Text(note["text"]),
                new JsonObject { ["noteId"] = note["id"]?.DeepClone() }, $"note:{projectId}:{id}");
        }
    }

    public JsonObject? Find(string projectId, string jobId) => Data.MegaTasks.OfType<JsonObject>()
        .FirstOrDefault(mega => Execution.Text(mega["projectId"]) == projectId && Execution.Text(mega["taskId"]) == jobId);

    public JsonObject Target(string projectId, string megaTaskId) => Data.MegaTasks.OfType<JsonObject>()
        .FirstOrDefault(mega => Execution.Text(mega["id"]) == megaTaskId && Execution.Text(mega["projectId"]) == projectId)
        ?? throw new InvalidOperationException("MegaTask does not belong to the target project.");

    public JsonObject View(string projectId)
    {
        var megas = Data.MegaTasks.OfType<JsonObject>().Where(m => Execution.Text(m["projectId"]) == projectId).ToArray();
        var ids = megas.Select(m => Execution.Text(m["id"])).ToHashSet(StringComparer.Ordinal);
        static JsonArray Copy(IEnumerable<JsonObject> rows) => new(rows.Select(row => (JsonNode?)row.DeepClone()).ToArray());
        return new JsonObject { ["megaTasks"] = Copy(megas),
            ["subTasks"] = Copy(Data.SubTasks.OfType<JsonObject>().Where(s => ids.Contains(Execution.Text(s["megaTaskId"])))),
            ["results"] = Copy(Data.Results.OfType<JsonObject>().Where(r => ids.Contains(Execution.Text(r["megaTaskId"])))),
            ["checkpoints"] = Copy(Data.Checkpoints.OfType<JsonObject>().Where(c => Execution.Text(c["projectId"]) == projectId)),
            ["timeline"] = Copy(Data.Timeline.OfType<JsonObject>().Where(e => Execution.Text(e["projectId"]) == projectId)) };
    }

    static string JobFingerprint(JsonObject job)
    {
        var data = new JsonArray(job["id"]?.DeepClone(), job["title"]?.DeepClone(), job["detail"]?.DeepClone(),
            job["revision_id"]?.DeepClone());
        if (Execution.Truthy(job["goldModel"])) data.Add(job["goldModel"]?.DeepClone());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data.ToJsonString(Execution.Json))));
    }

    JsonObject? Model(string id) => Options.Models().FirstOrDefault(m => Execution.Text(m["id"]) == id);
    static JsonArray CopyStrings(JsonArray? source) => new((source ?? []).Select(n => n?.DeepClone()).ToArray());
    static JsonArray StringArray(IEnumerable<string> source) => new(source.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    bool IsResearch(JsonObject job, IReadOnlyList<JsonObject> parts) => Execution.Text(job["kind"]) == "research" ||
        parts.All(part => Execution.Text(part["type"]) == "RESEARCH");

    public JsonObject Propose(JsonObject item, JsonObject job, IReadOnlyList<JsonObject> parts)
    {
        var project = item["project"] as JsonObject ?? throw new InvalidOperationException("Project unavailable.");
        var projectId = Execution.Text(project["id"]);
        var existing = Find(projectId, Execution.Text(job["id"]));
        if (existing is not null)
        {
            if (Execution.Text(existing["jobFingerprint"]) != JobFingerprint(job))
                throw new InvalidOperationException("MegaTask job identity changed. Prepare a new objective.");
            if (!existing.ContainsKey("timing"))
            {
                existing["timing"] = Execution.Truthy(job["timing"]) ? job["timing"]?.DeepClone() : JsonValue.Create("discretion");
                existing["deadlineAt"] = Execution.Text(job["timing"]) == "deadline" ? job["deadlineAt"]?.DeepClone() : null;
            }
            return existing;
        }
        if (parts.Count == 0)
        {
            var gold = job["goldModel"] as JsonObject;
            throw new InvalidOperationException(gold is null ? "No eligible workers for the proposed MegaTask." :
                $"Gold model {Execution.Text(gold["provider"])}/{Execution.Text(gold["cli"])} ({Execution.Text(gold["id"])}) is unavailable, disabled or blocked by quota. No substitute will be used.");
        }
        var id = "MT-" + Options.NewId();
        var folder = Execution.Text(project["folder"]);
        var linked = Options.Files.LinkedWorkspace(folder);
        var facts = linked is null ? new JsonObject { ["error"] = "Relink a real local project folder before implementation.",
            ["git"] = false, ["dirty"] = new JsonArray(), ["names"] = new JsonArray(), ["fileCount"] = 0 } : Options.Inspect(linked);
        if (!Execution.Truthy(project["folder"]) && !IsResearch(job, parts))
            throw new InvalidOperationException("Set a local source folder before proposing implementation work.");
        if (Execution.Truthy(facts["error"]) && !IsResearch(job, parts))
            throw new InvalidOperationException(Execution.Text(facts["error"]));
        var revisions = item["revisions"] as JsonArray;
        var last = revisions?.LastOrDefault() as JsonObject;
        var mega = new JsonObject { ["id"] = id, ["projectId"] = projectId, ["taskId"] = job["id"]?.DeepClone(),
            ["planRevisionId"] = job["revision_id"]?.DeepClone(), ["title"] = job["title"]?.DeepClone(),
            ["goal"] = Execution.Truthy(job["detail"]) ? job["detail"]?.DeepClone() : last?["direction"]?.DeepClone(),
            ["createdAt"] = Options.Now(), ["approvedAt"] = null, ["status"] = "READY_FOR_APPROVAL",
            ["orchestratorModelId"] = Options.LeadId(), ["currentCheckpointId"] = null, ["completedAt"] = null,
            ["jobFingerprint"] = JobFingerprint(job), ["detached"] = Execution.Truthy(job["detached"]),
            ["repoBefore"] = Execution.RepoState(folder, facts, Options.Files),
            ["nextAction"] = "Review the proposed slices and approve this MegaTask.", ["pauseRequested"] = false,
            ["timing"] = Execution.Truthy(job["timing"]) ? job["timing"]?.DeepClone() : JsonValue.Create("discretion"),
            ["deadlineAt"] = Execution.Text(job["timing"]) == "deadline" ? job["deadlineAt"]?.DeepClone() : null };
        var ids = parts.Select((_, i) => $"{id}-ST-{i + 1:00}").ToArray();
        var subs = new List<JsonObject>();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index]; var model = Model(Execution.Text(part["modelId"]));
            var dependencies = part["dependsOn"] is JsonArray positions
                ? positions.Select(p => { var n = p?.GetValue<int>() ?? -1; return n >= 0 && n < ids.Length ? ids[n] : ""; }).ToArray()
                : part["dependencies"] is JsonArray explicitIds ? explicitIds.Select(Execution.Text).ToArray()
                : index > 0 ? [ids[index - 1]] : [];
            var sub = new JsonObject { ["id"] = ids[index], ["megaTaskId"] = id,
                ["title"] = (part["slice"] ?? part["title"])?.DeepClone(),
                ["detail"] = (part["detail"] ?? part["slice"] ?? job["detail"])?.DeepClone(),
                ["status"] = "PENDING", ["assignedModelId"] = part["modelId"]?.DeepClone(),
                ["attemptIds"] = new JsonArray(), ["resultId"] = null, ["createdAt"] = Options.Now(), ["completedAt"] = null,
                ["type"] = Execution.Truthy(part["type"]) ? part["type"]?.DeepClone() : JsonValue.Create(Execution.Text(job["kind"]) == "research" ? "RESEARCH" : Execution.Text(job["kind"]) == "audit" ? "AUDIT" : "IMPLEMENTATION"),
                ["dependencies"] = StringArray(dependencies), ["proposedProvider"] = model?["provider"]?.DeepClone(),
                ["proposedCli"] = model?["cli"]?.DeepClone(), ["provider"] = null, ["cli"] = null };
            if (job["goldModel"] is JsonObject gold) sub["goldModel"] = gold.DeepClone();
            if (part["scope"] is JsonArray { Count: > 0 } scope) sub["scope"] = CopyStrings(scope);
            if (Execution.Truthy(part["size"])) sub["size"] = part["size"]?.DeepClone();
            subs.Add(sub);
        }
        foreach (var (sub, index) in subs.Select((s, i) => (s, i)))
            if (((JsonArray)sub["dependencies"]!).Any(d => Array.IndexOf(ids, Execution.Text(d)) >= index || Array.IndexOf(ids, Execution.Text(d)) < 0))
                throw new InvalidOperationException("Dependencies must reference an earlier SubTask in the same MegaTask.");
        Data.MegaTasks.Add((JsonNode)mega); foreach (var sub in subs) Data.SubTasks.Add((JsonNode)sub);
        mega["blockers"] = StringArray(ProposalBlockers(mega));
        if (((JsonArray)mega["blockers"]!).Count > 0)
            mega["nextAction"] = "Resolve the proposal blockers before approval: " + string.Join(" ", ((JsonArray)mega["blockers"]!).Select(Execution.Text));
        if (!Execution.Truthy(job["detached"])) Options.RowFor(projectId, id)["megaTaskId"] = id;
        Event(projectId, "MEGATASK_PROPOSED", $"{Execution.Text(mega["title"])}: {subs.Count} proposed slice(s).", new JsonObject { ["megaTaskId"] = id });
        Options.Persist();
        return mega;
    }

    IReadOnlyList<string> ProposalBlockers(JsonObject mega)
    {
        if (Options.SpawnWorker is null)
            return ["The worker launcher is unavailable. Start Planwharf through its desktop launcher."];
        var blockers = new List<string>();
        foreach (var sub in SubTasks(mega))
        {
            var assigned = Execution.Text(sub["assignedModelId"]);
            var worker = Model(assigned);
            var candidate = Options.ResolveWorker is null ? worker : Options.ResolveWorker(worker, sub);
            if (candidate is null || !Execution.Truthy(candidate["enabled"]) || candidate["available"]?.ToJsonString() == "false" ||
                (Options.WorkerEligible is not null && !Options.WorkerEligible(candidate, sub)))
            {
                var reason = Options.ExplainNoWorker?.Invoke(worker, sub);
                if (!Execution.Truthy(reason?["waiting"]))
                    blockers.Add(Execution.Truthy(reason?["message"]) ? Execution.Text(reason?["message"]) :
                        $"Worker {assigned} is unavailable or has insufficient remaining quota.");
                continue;
            }
            if ((Execution.Text(candidate["id"]) == Execution.Text(worker?["id"]) &&
                (!JsonNode.DeepEquals(worker?["provider"], sub["proposedProvider"]) || !JsonNode.DeepEquals(worker?["cli"], sub["proposedCli"]))) ||
                (worker is null && Execution.Text(candidate["id"]) == assigned))
            {
                blockers.Add($"Provider/model configuration changed for {assigned}; review a new proposal.");
                continue;
            }
            if (Execution.Text(sub["type"]) == "RESEARCH" && ProviderAdapters.AdapterFor(candidate)?.SourceProtectedResearch != true)
                blockers.Add($"Source-protected research is not supported by {Execution.Text(worker?["provider"])}.");
        }
        return blockers;
    }

    public JsonObject Approve(string projectId, string megaTaskId, JsonNode? policy = null)
    {
        var mega = Target(projectId, megaTaskId);
        if (Execution.Text(mega["status"]) != "READY_FOR_APPROVAL")
            throw new InvalidOperationException("Only a proposed MegaTask can be approved.");
        var blockers = ProposalBlockers(mega);
        mega["blockers"] = StringArray(blockers);
        if (blockers.Count > 0)
        {
            Options.Persist();
            throw new InvalidOperationException(string.Join(" ", blockers));
        }
        mega["approvedAt"] = Options.Now(); mega["status"] = "RUNNING";
        mega["approvedMegaTaskId"] = mega["id"]?.DeepClone();
        mega["authorizedSessionId"] = Options.SessionId;
        Event(projectId, "MEGATASK_APPROVED", Execution.Truthy(policy) ?
            $"Approved {Execution.Text(mega["title"])} by the project autonomy setting." :
            $"Approved {Execution.Text(mega["title"])}.", new JsonObject { ["megaTaskId"] = megaTaskId });
        Options.Persist();
        return mega;
    }

    JsonObject? ReadJson(string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var bytes = Options.Files.Read(path);
            if (bytes.Length > 1024 * 1024) return null;
            return JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF')) as JsonObject;
        }
        catch { return null; }
    }

    static JsonObject Identity(JsonObject mega, JsonObject sub, string attemptId) => new()
    {
        ["megaTaskId"] = mega["id"]?.DeepClone(), ["subTaskId"] = sub["id"]?.DeepClone(),
        ["attemptId"] = attemptId, ["resultId"] = "R-" + attemptId,
        ["provider"] = sub["provider"]?.DeepClone(), ["cli"] = sub["cli"]?.DeepClone()
    };

    static bool Integer(JsonNode? value) => value is JsonValue v && v.TryGetValue<double>(out var n) &&
        double.IsFinite(n) && Math.Truncate(n) == n;

    JsonObject? ValidatedResult(JsonObject mega, JsonObject sub, JsonObject part)
    {
        var identity = Identity(mega, sub, Execution.Text(part["attemptId"]));
        var result = ReadJson(Execution.Text(part["structuredResultPath"]));
        var exit = ReadJson(Execution.Text(part["exitPath"]));
        if (!WorkerResult.ValidateResult(result, identity) || exit is null ||
            !JsonNode.DeepEquals(exit["attemptId"], identity["attemptId"]) || !Integer(exit["exitCode"]) ||
            (Execution.Truthy(identity["provider"]) && !JsonNode.DeepEquals(identity["provider"], exit["provider"])) ||
            (Execution.Truthy(identity["cli"]) && !JsonNode.DeepEquals(identity["cli"], exit["model"])) ||
            (Execution.Text(result!["status"]) == "COMPLETE" && exit["exitCode"]!.ToString() != "0")) return null;
        if (!Execution.Truthy(result!["createdAt"]) && exit.ContainsKey("createdAt")) result["createdAt"] = exit["createdAt"]?.DeepClone();
        result["exitCode"] = exit["exitCode"]?.DeepClone(); result["evidencePath"] = part["structuredResultPath"]?.DeepClone();
        return result;
    }

    JsonObject Technical(JsonObject mega, JsonObject sub, JsonObject part, string status, string summary, JsonObject? partial = null)
    {
        var result = Identity(mega, sub, Execution.Text(part["attemptId"]));
        var exit = ReadJson(Execution.Text(part["exitPath"]));
        result["status"] = status; result["summary"] = summary;
        result["filesChanged"] = partial?["filesChanged"]?.DeepClone() ?? new JsonArray();
        result["tests"] = partial?["tests"]?.DeepClone() ?? new JsonArray();
        result["blockers"] = new JsonArray(JsonValue.Create(summary));
        foreach (var blocker in partial?["blockers"] as JsonArray ?? []) result["blockers"]!.AsArray().Add(blocker?.DeepClone());
        result["nextAction"] = "Review the evidence and resume the remaining work from an explicit checkpoint.";
        result["createdAt"] = Options.Now(); result["exitCode"] = exit is not null &&
            JsonNode.DeepEquals(exit["attemptId"], part["attemptId"]) && JsonNode.DeepEquals(exit["provider"], sub["provider"]) &&
            JsonNode.DeepEquals(exit["model"], sub["cli"]) && Integer(exit["exitCode"]) ? exit["exitCode"]?.DeepClone() : null;
        result["evidenceKind"] = "TECHNICAL"; result["evidencePath"] = part["logPath"]?.DeepClone();
        return result;
    }

    static string Slash(JsonNode? file) => Regex.Replace(Execution.Text(file).Replace('\\', '/'), @"^\./|/+$", "");
    static bool InScope(string file, IEnumerable<string> scope) => scope.Any(entry => file == entry || file.StartsWith(entry + "/", StringComparison.Ordinal));
    Worktrees.GitReply? GitReply(string folder, IReadOnlyList<string> args, int timeout = 4000, int bytes = 8 * 1024 * 1024) =>
        Options.Git?.Invoke(new Worktrees.GitCall(folder, args, timeout, bytes));

    JsonObject ScopeCheck(JsonObject mega, JsonObject sub, JsonObject part, JsonObject result)
    {
        if (Execution.Text(result["status"]) != "COMPLETE" || sub["scope"] is not JsonArray { Count: > 0 } own) return result;
        var all = SubTasks(mega); var earlier = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Stack<string>((sub["dependencies"] as JsonArray ?? []).Select(Execution.Text));
        while (queue.TryPop(out var id))
        {
            if (!earlier.Add(id)) continue;
            foreach (var parent in all.FirstOrDefault(s => Execution.Text(s["id"]) == id)?["dependencies"] as JsonArray ?? []) queue.Push(Execution.Text(parent));
        }
        var busy = all.Where(s => !ReferenceEquals(s, sub) && !earlier.Contains(Execution.Text(s["id"])))
            .SelectMany(s => (s["scope"] as JsonArray ?? []).Select(Slash)).ToArray();
        var continued = Data.MegaTasks.OfType<JsonObject>().Where(m => JsonNode.DeepEquals(m["projectId"], mega["projectId"]) &&
            !JsonNode.DeepEquals(m["id"], mega["id"]) && Execution.Text(m["status"]).ToUpperInvariant() == "CANCELLED").Select(m => Execution.Text(m["id"])).ToHashSet();
        var scope = own.Select(Slash).Concat(all.Where(s => earlier.Contains(Execution.Text(s["id"])) && Execution.Text(s["status"]) == "COMPLETE")
            .SelectMany(s => (s["scope"] as JsonArray ?? []).Select(Slash))).Concat(Data.Results.OfType<JsonObject>()
            .Where(r => Execution.Text(r["status"]) == "COMPLETE" && continued.Contains(Execution.Text(r["megaTaskId"])))
            .SelectMany(r => (r["filesChanged"] as JsonArray ?? []).Select(Slash)).Where(file => !InScope(file, busy))).ToArray();
        var outside = new List<string>();
        void Add(string file) { if (file.Length > 0 && !InScope(file, scope) && !outside.Contains(file, StringComparer.Ordinal)) outside.Add(file); }
        foreach (var file in result["filesChanged"] as JsonArray ?? []) Add(Slash(file));
        var others = all.Where(s => !ReferenceEquals(s, sub)).ToArray();
        var alone = !others.Any(s => Execution.Text(s["status"]) == "RUNNING" || (Execution.Truthy(s["finishedAt"]) &&
            Execution.Truthy(part["startedAt"]) && string.CompareOrdinal(Execution.Text(s["finishedAt"]), Execution.Text(part["startedAt"])) > 0));
        if (alone || Execution.Truthy(part["branch"]))
        {
            var before = (mega["repoBefore"]?["dirtyFiles"] as JsonArray ?? []).Select(n =>
            {
                var record = Execution.Text(n); var prefix = Regex.IsMatch(record, @"^[MADRCU?!] \S", RegexOptions.ECMAScript) ? 2 : 3;
                return Slash(JsonValue.Create((record.Length >= prefix ? record[prefix..] : "").Split(" -> ").Last().Trim('"')));
            }).ToHashSet();
            var owned = others.Where(s => Execution.Text(s["status"]) == "COMPLETE").SelectMany(s => (s["scope"] as JsonArray ?? []).Select(Slash)).ToArray();
            var folder = Options.Files.LinkedWorkspace(Execution.Text(part["folder"]));
            var listed = folder is null ? null : GitReply(folder, ["status", "--porcelain", "-z", "--untracked-files=all"], 15000, 16 * 1024 * 1024);
            if (listed?.Status == 0)
            {
                var rows = listed.Stdout.Split('\0');
                for (var i = 0; i < rows.Length; i++)
                {
                    var row = rows[i]; if (row.Length < 4) continue;
                    var file = Slash(JsonValue.Create(row[3..]));
                    if (!before.Contains(file) && !InScope(file, owned)) Add(file);
                    if (row[0] is 'R' or 'C') i++;
                }
            }
            if (Execution.Truthy(part["branch"]) && Execution.Truthy(part["baseHead"]))
            {
                try
                {
                    if (Options.Git is null) throw new IOException("Git unavailable");
                    foreach (var file in new Worktrees(Options.Git).Git(Execution.Text(part["folder"]), "diff", "--name-only", Execution.Text(part["baseHead"]) + "..HEAD").Split('\n').Select(s => s.TrimEnd('\r')).Where(s => s.Length > 0)) Add(file);
                }
                catch { outside.Add("(commit inspection unavailable)"); }
            }
        }
        if (earlier.Count > 0) outside.RemoveAll(file => !InScope(file, busy));
        if (outside.Count == 0) { sub.Remove("scopeViolation"); return result; }
        sub["scopeViolation"] = StringArray(outside.Take(20));
        var names = string.Join(", ", outside.Take(20));
        var copy = (JsonObject)result.DeepClone(); copy["status"] = "BLOCKED";
        copy["blockers"]!.AsArray().Add((JsonNode?)JsonValue.Create("Changed files outside its scope: " + names + "."));
        copy["summary"] = $"Stopped: changed files outside its scope ({names}). {Execution.Text(result["summary"])}";
        return copy;
    }

    void RecordOutcome(JsonObject mega, JsonObject sub, JsonObject? part, JsonObject? result, bool accepted, int fixedFiles,
        bool switched = false, string? refusal = null)
    {
        var record = new JsonObject { ["model"] = sub["assignedModelId"]?.DeepClone(), ["role"] = StepRole(sub),
            ["size"] = sub["size"]?.DeepClone(), ["kind"] = (sub["type"] ?? mega["kind"])?.DeepClone(),
            ["switch"] = switched, ["refusal"] = refusal, ["accepted"] = accepted, ["fixed"] = fixedFiles,
            ["startedAt"] = part?["startedAt"]?.DeepClone(), ["endedAt"] = sub["finishedAt"]?.DeepClone() ?? JsonValue.Create(Options.Now()) };
        if (result is not null)
            foreach (var key in new[] { "tests", "testsReported", "testsPassed" })
                if (result.ContainsKey(key)) record[key] = result[key]?.DeepClone();
        if (part is not null && Execution.Truthy(part["structuredResultPath"]))
            record["quota"] = ReadJson(Execution.Text(part["structuredResultPath"]) + ".usage.json");
        Execution.AppendStepOutcome(Options.Directory, record, Options.Files);
    }

    void RecordAccepted(JsonObject mega, JsonObject sub, JsonObject? part, JsonObject? result, bool accepted, int fixedFiles)
    {
        mega["recordedOutcomeSubIds"] ??= new JsonArray();
        mega["recordedOutcomeSubIds"]!.AsArray().Add(sub["id"]?.DeepClone());
        RecordOutcome(mega, sub, part, result, accepted, fixedFiles);
    }

    void CollectDecisions(JsonObject mega, JsonObject result)
    {
        foreach (var line in Execution.Text(result["summary"]).Split('\n'))
        {
            var at = line.IndexOf("needs your decision:", StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var raw = line[(at + "needs your decision:".Length)..].Trim();
            var text = raw.Split('|')[0].Trim();
            if (raw.Length == 0 || Data.Decisions.OfType<JsonObject>().Any(d => JsonNode.DeepEquals(d["projectId"], mega["projectId"]) &&
                JsonNode.DeepEquals(d["megaTaskId"], mega["id"]) && Execution.Text(d["text"]) == text)) continue;
            // The JavaScript collection closure references an unbound `item` in both decision
            // policy branches. Preserve its error and partial state; fixing it needs owner review.
            throw new InvalidOperationException("item is not defined");
        }
    }

    // Collection mutates the shared store before attempt IO, just as JavaScript does. A failed
    // attempt write is observable; it must not silently replay result/log accounting on retry.
    void Collect(JsonObject mega, JsonObject sub, JsonObject part, JsonObject result)
    {
        if (Data.Results.OfType<JsonObject>().Any(r => JsonNode.DeepEquals(r["resultId"], result["resultId"]))) return;
        result = ScopeCheck(mega, sub, part, result);
        if (Execution.Text(sub["type"]) == "INTEGRATION" && Execution.Text(result["status"]) == "COMPLETE")
        {
            try
            {
                if (Options.Git is null) throw new IOException("Git unavailable");
                var trees = new Worktrees(Options.Git);
                foreach (var branch in (mega["worktreeParts"] as JsonArray ?? []).OfType<JsonObject>().Where(p =>
                    (mega["activeGroup"] as JsonArray ?? []).Any(id => JsonNode.DeepEquals(id, p["subId"]))))
                    trees.Git(Execution.Text(part["projectFolder"]), "merge-base", "--is-ancestor", Execution.Text(branch["branch"]), "HEAD");
                if (trees.Git(Execution.Text(part["projectFolder"]), "status", "--porcelain").Length > 0) throw new IOException("Work branch has uncommitted changes");
                mega["integratedSubIds"] = StringArray((mega["integratedSubIds"] as JsonArray ?? []).Concat(mega["activeGroup"] as JsonArray ?? []).Select(Execution.Text).Distinct());
                mega["activeGroup"] = null; mega["awaitingIntegration"] = false;
            }
            catch (Exception error)
            {
                result = (JsonObject)result.DeepClone(); result["status"] = "BLOCKED";
                result["summary"] = "Integration is unfinished: " + error.Message;
                result["blockers"]!.AsArray().Add((JsonNode?)JsonValue.Create(error.Message));
            }
        }
        sub["finishedAt"] = Options.Now();
        result = (JsonObject)result.DeepClone();
        foreach (var key in new[] { "assignedModelId", "provider", "cli" }) result[key] = sub[key]?.DeepClone();
        result["pid"] = part["pid"]?.DeepClone(); result["startedAt"] = part["startedAt"]?.DeepClone();
        Data.Results.Add((JsonNode)result); sub["resultId"] = result["resultId"]?.DeepClone(); sub["status"] = result["status"]?.DeepClone();
        CollectDecisions(mega, result);
        part["validatedResultStatus"] = result["status"]?.DeepClone(); part["recordedSubTaskStatus"] = sub["status"]?.DeepClone();
        var complete = Execution.Text(result["status"]) == "COMPLETE";
        sub["completedAt"] = complete ? Options.Now() : null;
        Options.Attempts.AttachResult(new JsonObject { ["attemptId"] = part["attemptId"]?.DeepClone(),
            ["resultPath"] = part["structuredResultPath"]?.DeepClone(), ["status"] = result["status"]?.DeepClone(), ["exitCode"] = result["exitCode"]?.DeepClone() });
        Options.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = part["attemptId"]?.DeepClone(), ["reason"] = Execution.Text(result["status"]).ToLowerInvariant() });
        Event(Execution.Text(mega["projectId"]), complete ? "SUBTASK_COMPLETED" : Execution.Truthy(result["modelChange"]) ? "SUBTASK_REASSIGNED" : "SUBTASK_FAILED",
            $"{Execution.Text(sub["title"])}: {Execution.Text(result["summary"])}", new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone(),
                ["subTaskId"] = sub["id"]?.DeepClone(), ["resultId"] = result["resultId"]?.DeepClone() });
        if (complete && Execution.Text(sub["type"]) != "RESEARCH")
        {
            try
            {
                var folder = Execution.Text(part["projectFolder"]);
                var commit = Execution.Truthy(part["branch"]) && Options.Git is not null ? Execution.Text(part["branch"]) + " " +
                    new Worktrees(Options.Git).Git(Execution.Text(part["folder"]), "rev-parse", "--short", "HEAD") : Options.Plan?.CommitNote(folder) ?? "";
                Options.Plan?.TickStep(folder, new PlanStep(Title: Execution.Text(sub["title"]), Role: StepRole(sub), Model: Execution.Text(part["label"]), Commit: commit));
            }
            catch { /* the step result is already saved */ }
        }
        try
        {
            Execution.AppendStepTime(Options.Directory, TimeLeft.StepRecord(new JsonObject { ["role"] = StepRole(sub),
                ["model"] = sub["assignedModelId"]?.DeepClone(), ["size"] = sub["size"]?.DeepClone(),
                ["files"] = (result["filesChanged"] as JsonArray)?.Count ?? 0, ["startedAt"] = part["startedAt"]?.DeepClone(),
                ["endedAt"] = sub["finishedAt"]?.DeepClone(), ["status"] = result["status"]?.DeepClone(), ["modelChange"] = Execution.Truthy(result["modelChange"]) }), Options.Files);
        }
        catch { /* the step result is already saved */ }
        try
        {
            var switched = Execution.Truthy(result["modelChange"]);
            if (switched || !complete) RecordOutcome(mega, sub, part, result, false, 0, switched);
            else if (StepRole(sub) == "Verify")
            {
                var fixedFiles = (result["filesChanged"] as JsonArray)?.Count ?? 0;
                RecordOutcome(mega, sub, part, result, fixedFiles == 0, fixedFiles);
                mega["recordedOutcomeSubIds"] = new JsonArray((mega["recordedOutcomeSubIds"] as JsonArray ?? []).Select(n => n?.DeepClone()).Append(sub["id"]?.DeepClone()).ToArray());
                foreach (var build in SubTasks(mega).Where(s => StepRole(s) == "Build" && Execution.Text(s["status"]) == "COMPLETE"))
                {
                    if (OutcomeRecorded(mega, build)) continue;
                    RecordAccepted(mega, build, PartFor(mega, build), ResultFor(build), fixedFiles == 0, fixedFiles);
                }
            }
            else if (!SubTasks(mega).Any(s => StepRole(s) == "Verify")) RecordAccepted(mega, sub, part, result, true, 0);
        }
        catch { /* the step result is already saved */ }
    }

    bool OutcomeRecorded(JsonObject mega, JsonObject sub) => (mega["recordedOutcomeSubIds"] as JsonArray ?? []).Any(id => JsonNode.DeepEquals(id, sub["id"]));
    JsonObject? PartFor(JsonObject mega, JsonObject sub) => (Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"]))["dispatch"] as JsonArray ?? [])
        .OfType<JsonObject>().LastOrDefault(p => JsonNode.DeepEquals(p["subTaskId"], sub["id"]));
    JsonObject? ResultFor(JsonObject sub) => Data.Results.OfType<JsonObject>().LastOrDefault(r => JsonNode.DeepEquals(r["subTaskId"], sub["id"]) && Execution.Text(r["status"]) == "COMPLETE") ??
        Data.Results.OfType<JsonObject>().LastOrDefault(r => JsonNode.DeepEquals(r["subTaskId"], sub["id"]));

    bool RerouteRefusal(JsonObject mega, JsonObject sub, JsonObject part)
    {
        if (Options.OnRefusal is null || Options.Replacement is null || Number(sub["refusalReroutes"]) >= 2) return false;
        var reason = ProviderRefusal.RefusalReason(ReadText(Execution.Text(part["logPath"])));
        if (reason is null) return false;
        try { RecordOutcome(mega, sub, part, null, false, 0, refusal: reason); } catch { }
        var from = Execution.Text(sub["assignedModelId"]);
        Options.OnRefusal(from, reason);
        var next = Options.Replacement(sub, [from]);
        if (next is null) return false;
        Options.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = part["attemptId"]?.DeepClone(), ["reason"] = "provider refused" });
        sub["status"] = "PENDING"; sub["refusalReroutes"] = Number(sub["refusalReroutes"]) + 1;
        SwitchModel(sub, next, from); part["alive"] = false; part["refused"] = reason;
        Event(Execution.Text(mega["projectId"]), "MODEL_REFUSAL_FALLBACK", $"{from} refused the slice ({reason}). Reassigned to {Execution.Text(next["id"])}.",
            new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone(), ["subTaskId"] = sub["id"]?.DeepClone(), ["fromModelId"] = from, ["modelId"] = next["id"]?.DeepClone() });
        Options.Persist(); return true;
    }

    string ReadText(string path) { try { return Encoding.UTF8.GetString(Options.Files.Read(path)); } catch { return ""; } }
    static double Number(JsonNode? node) => double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
    static void SwitchModel(JsonObject sub, JsonObject next, string from)
    {
        sub["fallbackFrom"] = from; sub["assignedModelId"] = next["id"]?.DeepClone();
        sub["proposedProvider"] = next["provider"]?.DeepClone(); sub["proposedCli"] = next["cli"]?.DeepClone();
    }

    bool RerouteNoVerdict(JsonObject mega, JsonObject sub, JsonObject part)
    {
        var plan = Execution.NoVerdictPlan(Number(sub["noVerdictTries"]));
        if (plan == "stop") return false;
        var text = ReadText(Execution.Text(part["logPath"]));
        Options.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = part["attemptId"]?.DeepClone(), ["reason"] = "no verdict" });
        sub["takeover"] = new JsonObject { ["fromModelId"] = sub["assignedModelId"]?.DeepClone(), ["at"] = Options.Now(),
            ["filesChanged"] = new JsonArray(), ["tests"] = new JsonArray(),
            ["summary"] = "The previous attempt ended without a result. Continue from the folder; do not start over.",
            ["logTail"] = text.Length > 1500 ? text[^1500..] : text };
        part["alive"] = false;
        var from = Execution.Text(sub["assignedModelId"]);
        if (plan == "retry-same")
        {
            sub["status"] = "PENDING"; sub["noVerdictTries"] = 1; sub["resultId"] = null;
            Event(Execution.Text(mega["projectId"]), "MODEL_NO_VERDICT_RETRY", from + " ended without a result. Same model tries once more.",
                new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone(), ["subTaskId"] = sub["id"]?.DeepClone(), ["modelId"] = from });
            Options.Persist(); return true;
        }
        var next = Options.Replacement?.Invoke(sub, new[] { from }.Concat((sub["noVerdictFrom"] as JsonArray ?? []).Select(Execution.Text)).ToArray());
        if (next is null) return false;
        try { RecordOutcome(mega, sub, part, null, false, 0, switched: true); } catch { }
        sub["status"] = "PENDING"; sub["noVerdictTries"] = 2; sub["resultId"] = null;
        sub["noVerdictFrom"] = new JsonArray((sub["noVerdictFrom"] as JsonArray ?? []).Select(n => n?.DeepClone()).Append(JsonValue.Create(from)).ToArray());
        SwitchModel(sub, next, from);
        Event(Execution.Text(mega["projectId"]), "MODEL_NO_VERDICT_SWITCH", from + " ended without a result twice. Reassigned to " + Execution.Text(next["id"]) + ".",
            new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone(), ["subTaskId"] = sub["id"]?.DeepClone(), ["fromModelId"] = from, ["modelId"] = next["id"]?.DeepClone() });
        Options.Persist(); return true;
    }

    JsonObject? Delivery(JsonObject mega, JsonObject sub, JsonObject part, bool requireExit)
    {
        var exit = ReadJson(Execution.Text(part["exitPath"]));
        if (requireExit && (exit is null || !JsonNode.DeepEquals(exit["attemptId"], part["attemptId"]) || !Integer(exit["exitCode"]) ||
            (Execution.Truthy(exit["provider"]) && !JsonNode.DeepEquals(exit["provider"], sub["provider"])) ||
            (Execution.Truthy(exit["model"]) && !JsonNode.DeepEquals(exit["model"], sub["cli"])))) return null;
        var folder = Options.Files.LinkedWorkspace(Execution.Text(part["folder"]));
        var before = Execution.Text(part["baseHead"] ?? mega["repoBefore"]?["repoHead"]);
        if (folder is null || before.Length == 0 || Options.Git is null ||
            Options.Files.LinkedWorkspace(Execution.Text(part["projectFolder"])) is null ||
            !(Options.SafeGitWorkspace?.Invoke(Execution.Text(part["projectFolder"])) ?? (ProjectWorkspace.SafeGitWorkspace(Execution.Text(part["projectFolder"])) is not null))) return null;
        var after = GitReply(folder, ["--no-optional-locks", "-C", folder, "rev-parse", "HEAD"])?.Stdout.Trim() ?? "";
        if (!Regex.IsMatch(after, "^[a-f0-9]{40,64}$", RegexOptions.ECMAScript) || after == before) return null;
        var listed = GitReply(folder, ["--no-optional-locks", "-C", folder, "diff", "--name-only", before + ".." + after]);
        var names = listed?.Status == 0 ? listed.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries) : [];
        if (requireExit) return Execution.DeliveredWithoutVerdict(new JsonObject { ["folder"] = folder, ["before"] = before,
            ["after"] = after, ["exitCode"] = exit?["exitCode"]?.DeepClone(), ["scope"] = sub["scope"]?.DeepClone(), ["names"] = StringArray(names) });
        // JavaScript's older committedSince fallback accepts a commit without exit evidence.
        return names.Length == 0 ? null : new JsonObject { ["filesChanged"] = StringArray(names.Take(40)),
            ["summary"] = "Delivered on disk: the step committed before the process ended, with no result file." };
    }

    bool ReportExists(JsonObject result, JsonObject part)
    {
        try
        {
            var report = Execution.Text(result["reportPath"]);
            if (Path.GetFullPath(report) != Path.Combine(Path.GetFullPath(Execution.Text(part["folder"])), "report.md")) return false;
            var stat = Options.Files.Stat(report);
            return stat.IsFile && !stat.IsSymbolicLink && stat.Size > 0;
        }
        catch { return false; }
    }

    public IReadOnlyList<JsonObject> Reconcile(JsonObject mega)
    {
        var row = Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"]));
        foreach (var sub in SubTasks(mega).Where(s => Execution.Text(s["status"]) == "RUNNING"))
        {
            var attempt = (sub["attemptIds"] as JsonArray)?.LastOrDefault();
            var part = (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(p =>
                JsonNode.DeepEquals(p["subTaskId"], sub["id"]) && JsonNode.DeepEquals(p["attemptId"], attempt));
            if (part is null) { sub["status"] = "FAILED"; continue; }
            if (!Execution.Truthy(part["pid"]))
            {
                var receipt = ReadJson(Execution.Text(part["exitPath"]) + ".process.json");
                if (receipt is not null && JsonNode.DeepEquals(receipt["attemptId"], part["attemptId"]) && Integer(receipt["pid"])) part["pid"] = receipt["pid"]?.DeepClone();
            }
            var alive = Execution.Truthy(part["pid"]) && (Options.IsProcessAlive?.Invoke((int)Number(part["pid"])) ?? false);
            part["observedAlive"] = alive; part["alive"] = alive;
            if (alive) continue;
            var result = ValidatedResult(mega, sub, part);
            var valid = result is not null && (Execution.Text(sub["type"]) != "RESEARCH" || Execution.Text(result["status"]) != "COMPLETE" || ReportExists(result, part));
            if (!valid && RerouteRefusal(mega, sub, part)) continue;
            if (!valid)
            {
                var delivered = Delivery(mega, sub, part, true) ?? Delivery(mega, sub, part, false);
                if (delivered is not null) { Collect(mega, sub, part, Technical(mega, sub, part, "COMPLETE", Execution.Text(delivered["summary"]), delivered)); continue; }
                if (RerouteNoVerdict(mega, sub, part)) continue;
                Collect(mega, sub, part, Technical(mega, sub, part, "FAILED", "Worker process ended without a valid structured result and exit evidence."));
                continue;
            }
            Collect(mega, sub, part, result!);
        }
        var dispatch = (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        row["workerAlive"] = dispatch.Any(p => Execution.Truthy(p["alive"]));
        var monitoring = row["monitoring"] as JsonObject ?? new JsonObject();
        monitoring["workers"] = dispatch.Count(p => Execution.Truthy(p["pid"])); monitoring["alive"] = dispatch.Count(p => Execution.Truthy(p["alive"]));
        if (row["monitoring"] is not JsonObject) row["monitoring"] = monitoring;
        return SubTasks(mega);
    }

    static string StepRole(JsonObject sub) => Execution.Text(sub["type"]) == "AUDIT" ? "Review" :
        Execution.Text(sub["type"]) == "RESEARCH" ? "Research" :
        Execution.Text(sub["title"]).StartsWith("Verify the work independently", StringComparison.Ordinal) ? "Verify" : "Build";

    public JsonObject Replan(string projectId, string megaTaskId, IReadOnlyList<JsonObject> parts, string why = "")
    {
        var mega = Target(projectId, megaTaskId);
        if (Execution.Text(mega["status"]) != "READY_FOR_APPROVAL")
            throw new InvalidOperationException("Only a proposal that has not started can be split again.");
        var old = SubTasks(mega);
        if (old.Any(sub => Execution.Text(sub["status"]) != "PENDING" || (sub["attemptIds"] as JsonArray)?.Count > 0))
            throw new InvalidOperationException("A step of this proposal already ran.");
        var ids = parts.Select((_, i) => $"{megaTaskId}-ST-{i + 1:00}").ToArray();
        var type = Execution.Text(old.FirstOrDefault()?["type"]); if (type.Length == 0) type = "IMPLEMENTATION";
        var subs = new List<JsonObject>();
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index]; var modelId = Execution.Text(part["modelId"]); var model = Model(modelId);
            if (model is null) throw new InvalidOperationException("Model not present: " + modelId);
            var dependencies = new List<string>();
            foreach (var parent in (part["dependsOn"] as JsonArray ?? []))
            {
                var position = parent?.GetValue<int>() ?? -1;
                if (position < 0 || position >= index)
                    throw new InvalidOperationException("Dependencies must reference an earlier SubTask in the same MegaTask.");
                dependencies.Add(ids[position]);
            }
            var sub = new JsonObject { ["id"] = ids[index], ["megaTaskId"] = megaTaskId,
                ["title"] = part["slice"]?.DeepClone(), ["detail"] = (part["detail"] ?? part["slice"])?.DeepClone(),
                ["status"] = "PENDING", ["assignedModelId"] = modelId, ["attemptIds"] = new JsonArray(), ["resultId"] = null,
                ["createdAt"] = Options.Now(), ["completedAt"] = null,
                ["type"] = Execution.Truthy(part["type"]) ? part["type"]?.DeepClone() : JsonValue.Create(type),
                ["dependencies"] = StringArray(dependencies), ["proposedProvider"] = model["provider"]?.DeepClone(),
                ["proposedCli"] = model["cli"]?.DeepClone(), ["provider"] = null, ["cli"] = null };
            if (part["scope"] is JsonArray { Count: > 0 } scope) sub["scope"] = CopyStrings(scope);
            if (Execution.Truthy(part["size"])) sub["size"] = part["size"]?.DeepClone();
            subs.Add(sub);
        }
        for (var i = Data.SubTasks.Count - 1; i >= 0; i--)
            if (Data.SubTasks[i] is JsonObject row && Execution.Text(row["megaTaskId"]) == megaTaskId) Data.SubTasks.RemoveAt(i);
        foreach (var sub in subs) Data.SubTasks.Add((JsonNode)sub);
        mega["blockers"] = StringArray(ProposalBlockers(mega));
        Event(projectId, "MEGATASK_PROPOSED", Execution.SplitSummary(subs, id => Execution.Text(Model(id)?["label"]) is { Length: > 0 } label ? label : id, why),
            new JsonObject { ["megaTaskId"] = megaTaskId });
        Options.Persist();
        return Paint(mega);
    }

    public JsonObject Reassign(string projectId, string megaTaskId, IReadOnlyList<string> modelIds, string why = "")
    {
        var mega = Target(projectId, megaTaskId);
        if (Execution.Text(mega["status"]) != "READY_FOR_APPROVAL")
            throw new InvalidOperationException("Only a proposed MegaTask can be reassigned.");
        var subs = SubTasks(mega);
        if (modelIds.Count != subs.Count) throw new InvalidOperationException("One model per slice is required.");
        var chosen = new List<JsonObject>();
        for (var i = 0; i < modelIds.Count; i++)
        {
            var model = Model(modelIds[i]);
            if (model is null || !Execution.Truthy(model["enabled"]) || model["available"]?.ToJsonString() == "false" ||
                (Options.WorkerEligible is not null && !Options.WorkerEligible(model, subs[i])))
                throw new InvalidOperationException($"Model {modelIds[i]} cannot take slice {i + 1}.");
            chosen.Add(model);
        }
        if (subs.Select((s, i) => Execution.Text(s["assignedModelId"]) == Execution.Text(chosen[i]["id"])).All(x => x)) return mega;
        for (var i = 0; i < subs.Count; i++)
        {
            subs[i]["assignedModelId"] = chosen[i]["id"]?.DeepClone();
            subs[i]["proposedProvider"] = chosen[i]["provider"]?.DeepClone();
            subs[i]["proposedCli"] = chosen[i]["cli"]?.DeepClone();
        }
        mega["blockers"] = StringArray(ProposalBlockers(mega));
        var assignment = string.Join(" · ", subs.Select((sub, i) => $"{StepRole(sub)}: {Execution.Text(chosen[i]["label"] ?? chosen[i]["id"])}"));
        Event(projectId, "MEGATASK_ASSIGNED", $"Lead assigned {assignment}.{(why.Length > 0 ? " Why: " + why : "")}",
            new JsonObject { ["megaTaskId"] = megaTaskId });
        Paint(mega); Options.Persist();
        return mega;
    }

    public JsonObject Paint(JsonObject mega)
    {
        var row = Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"]));
        var subs = SubTasks(mega);
        if (Execution.Text(mega["status"]) == "CANCELLED")
        {
            if (!Execution.Truthy(row["megaTaskId"]) || JsonNode.DeepEquals(row["megaTaskId"], mega["id"]))
            {
                row["jobId"] = null; row["megaTaskId"] = null; row["megaTask"] = null;
                row["subTasks"] = new JsonArray(); row["dispatch"] = new JsonArray(); row["status"] = "idle";
            }
            return row;
        }
        row["jobId"] = mega["taskId"]?.DeepClone(); row["jobTitle"] = mega["title"]?.DeepClone();
        row["jobFingerprint"] = mega["jobFingerprint"]?.DeepClone(); row["jobRevisionId"] = mega["planRevisionId"]?.DeepClone();
        row["megaTaskId"] = mega["id"]?.DeepClone(); row["megaTask"] = mega.DeepClone();
        row["subTasks"] = new JsonArray(subs.Select(s => (JsonNode?)s.DeepClone()).ToArray());
        row["status"] = Execution.Text(mega["status"]) switch { "READY_FOR_APPROVAL" => "needs_approval", "RUNNING" => "watching",
            "PAUSED" => "checkpointed", "COMPLETE" => "complete", "FAILED" => "failed", _ => "idle" };
        row["doing"] = $"{Execution.Text(mega["title"])}: {subs.Count(s => Execution.Text(s["status"]) == "COMPLETE")}/{subs.Count} slices complete.";
        if (Execution.Truthy(mega["deadlineError"])) row["doing"] = Execution.Text(row["doing"]) + " Deadline reached. " + Execution.Text(mega["deadlineError"]);
        row["workerActivity"] = row["doing"]?.DeepClone();
        row["next"] = ExecutionNext.GetExecutionNext(mega, Data.Node);
        row["waitingFor"] = Execution.Text(mega["status"]) switch { "READY_FOR_APPROVAL" => $"Approval of MegaTask {Execution.Text(mega["id"])}",
            "PAUSED" => "Resume an explicit checkpoint", "COMPLETE" => "Nothing. Structured results are complete.", _ => "Structured worker results" };
        row["workerLabel"] = string.Join(" + ", (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(part => Execution.Truthy(part["pid"])) .Select(part => Execution.Text(part["label"])));
        if (Execution.Text(row["workerLabel"]).Length == 0) row["workerLabel"] = null;
        row["plan"] = StringArray(new[] { "MegaTask: " + Execution.Text(mega["title"]), "Objective: " + Execution.Text(mega["goal"]),
            "Latest git: " + (Execution.Text(mega["repoBefore"]?["repoHead"]) is { Length: > 0 } head ? head : "Not applicable") }
            .Concat(subs.Select(sub => $"{Execution.Text(sub["title"])} — {Execution.Text(sub["assignedModelId"])}; " +
                ((sub["dependencies"] as JsonArray)?.Count > 0 ? "after " + string.Join(", ", ((JsonArray)sub["dependencies"]!).Select(id =>
                    Execution.Text(subs.FirstOrDefault(p => JsonNode.DeepEquals(p["id"], id))?["title"]))) : "parallel-ready") +
                $"; {Execution.Text(sub["status"])}")));
        row["approved"] = Execution.Truthy(mega["approvedAt"]);
        return row;
    }

    bool DeadlineDue(JsonObject mega)
    {
        if (Execution.Text(mega["timing"]) != "deadline" || Execution.Truthy(mega["deadlineHonoredAt"])) return false;
        if (Execution.Truthy(mega["deadlineReachedAt"])) return true;
        return DateTimeOffset.TryParse(Execution.Text(mega["deadlineAt"]), out var due) &&
            DateTimeOffset.TryParse(Options.Now(), out var now) && now >= due;
    }

    static bool DependsOnComplete(JsonObject sub, ExecutionData data) =>
        (sub["dependencies"] as JsonArray ?? []).All(id => data.SubTasks.OfType<JsonObject>().Any(parent =>
            JsonNode.DeepEquals(parent["id"], id) && Execution.Text(parent["status"]) == "COMPLETE"));

    IReadOnlyList<JsonObject> ParallelBuilds(JsonObject mega)
    {
        if (mega["activeGroup"] is JsonArray { Count: > 0 } active)
            return SubTasks(mega).Where(s => active.Any(id => JsonNode.DeepEquals(id, s["id"]))).ToArray();
        var ready = SubTasks(mega).Where(s => Execution.Text(s["status"]) == "PENDING" &&
            Execution.Text(s["type"]) == "IMPLEMENTATION" && StepRole(s) == "Build" && DependsOnComplete(s, Data) &&
            (s["dependencies"] as JsonArray ?? []).All(id =>
                !(mega["worktreeParts"] as JsonArray ?? []).OfType<JsonObject>().Any(p => JsonNode.DeepEquals(p["subId"], id)) ||
                (mega["integratedSubIds"] as JsonArray ?? []).Any(integrated => JsonNode.DeepEquals(integrated, id)))).ToArray();
        return ready.Length > 1 ? ready : [];
    }

    bool AllIntegrated(JsonObject mega) => (mega["activeGroup"] as JsonArray)?.Count is null or 0 &&
        (mega["worktreeParts"] as JsonArray ?? []).OfType<JsonObject>().All(p =>
            (mega["integratedSubIds"] as JsonArray ?? []).Any(id => JsonNode.DeepEquals(id, p["subId"])));

    JsonObject CurrentRepo(JsonObject item, JsonObject mega)
    {
        var folder = Options.Files.LinkedWorkspace(Execution.Text(item["project"]?["folder"]));
        var facts = folder is null ? new JsonObject { ["error"] = "Relink a real local project folder before implementation.", ["git"] = false, ["dirty"] = new JsonArray() } : Options.Inspect(folder);
        var repo = Execution.RepoState(Execution.Text(item["project"]?["folder"]), facts, Options.Files);
        var before = Execution.Text(mega["repoBefore"]?["repoHead"]); var after = Execution.Text(repo["repoHead"]);
        var commits = new JsonArray();
        if (folder is not null && before != after && Regex.IsMatch(before, "^[a-f0-9]{40,64}$", RegexOptions.ECMAScript) &&
            Regex.IsMatch(after, "^[a-f0-9]{40,64}$", RegexOptions.ECMAScript) &&
            (Options.SafeGitWorkspace?.Invoke(folder) ?? (ProjectWorkspace.SafeGitWorkspace(folder) is not null)))
        {
            var root = GitReply(folder, ["--no-optional-locks", "-C", folder, "rev-parse", "--show-toplevel"], bytes: 65536);
            if (root?.Status == 0 && Options.Files.LinkedWorkspace(root.Stdout.Trim()) == folder)
            {
                var listed = GitReply(folder, ["--no-optional-locks", "-C", folder, "rev-list", "--max-count=1000", before + ".." + after], bytes: 256 * 1024);
                commits = listed?.Status == 0 ? StringArray(listed.Stdout.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(id => Regex.IsMatch(id, "^[a-f0-9]{40,64}$", RegexOptions.ECMAScript))) : new JsonArray(after);
            }
        }
        repo["commitIds"] = commits;
        repo["sourceChangesObserved"] = Execution.Truthy(repo["gitReadable"]) && Execution.Truthy(repo["fingerprint"]) && Execution.Truthy(mega["repoBefore"]?["fingerprint"])
            ? JsonValue.Create(!JsonNode.DeepEquals(repo["fingerprint"], mega["repoBefore"]?["fingerprint"])) : null;
        return repo;
    }

    JsonObject Checkpoint(JsonObject item, JsonObject job, JsonObject mega, string reason, IReadOnlyList<string>? interrupted = null)
    {
        var subs = SubTasks(mega); var repo = CurrentRepo(item, mega);
        var cp = new JsonObject { ["id"] = "CP-" + Options.NewId(), ["projectId"] = mega["projectId"]?.DeepClone(),
            ["megaTaskId"] = mega["id"]?.DeepClone(), ["createdAt"] = Options.Now(), ["reason"] = reason,
            ["planRevisionId"] = mega["planRevisionId"]?.DeepClone(), ["repoHead"] = repo["repoHead"]?.DeepClone(),
            ["repoState"] = repo.DeepClone(), ["dirtyFiles"] = repo["dirtyFiles"]?.DeepClone(),
            ["completedSubTaskIds"] = StringArray(subs.Where(s => Execution.Text(s["status"]) == "COMPLETE").Select(s => Execution.Text(s["id"]))),
            ["pendingSubTaskIds"] = StringArray(subs.Where(s => Execution.Text(s["status"]) is "PENDING" or "BLOCKED").Select(s => Execution.Text(s["id"]))),
            ["failedSubTaskIds"] = StringArray(subs.Where(s => Execution.Text(s["status"]) == "FAILED").Select(s => Execution.Text(s["id"]))),
            ["activeAttemptIds"] = StringArray((interrupted ?? []).Concat((mega["interruptedAttemptIds"] as JsonArray ?? []).Select(Execution.Text)).Distinct()),
            ["resultIds"] = StringArray(subs.Where(s => Execution.Truthy(s["resultId"])).Select(s => Execution.Text(s["resultId"]))),
            ["summary"] = $"{subs.Count(s => Execution.Text(s["status"]) == "COMPLETE")}/{subs.Count} slices complete. {reason}.",
            ["nextAction"] = Execution.Truthy(mega["nextAction"]) ? mega["nextAction"]?.DeepClone() : JsonValue.Create("Review remaining work and resume this checkpoint."),
            ["jobId"] = job["id"]?.DeepClone(), ["jobFingerprint"] = JobFingerprint(job), ["at"] = Options.Now() };
        Data.Checkpoints.Add((JsonNode)cp); mega["currentCheckpointId"] = cp["id"]?.DeepClone(); mega["repoAfter"] = repo;
        mega["status"] = "PAUSED"; mega["pauseRequested"] = false;
        Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"]))["checkpoint"] = cp.DeepClone();
        Event(Execution.Text(mega["projectId"]), "CHECKPOINT_SAVED", Execution.Text(cp["summary"]), new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone(), ["checkpointId"] = cp["id"]?.DeepClone() });
        Options.Persist(); return cp;
    }

    void Complete(JsonObject item, JsonObject mega)
    {
        mega["status"] = "COMPLETE"; mega["completedAt"] = Options.Now(); mega["repoAfter"] = CurrentRepo(item, mega); mega["deadlineError"] = null;
        mega["nextAction"] = Data.Results.OfType<JsonObject>().LastOrDefault(r => JsonNode.DeepEquals(r["megaTaskId"], mega["id"]))?["nextAction"]?.DeepClone();
        if (!Execution.Truthy(mega["nextAction"])) mega["nextAction"] = "Review the completed results.";
        if (mega["worktreeParts"] is JsonArray { Count: > 0 } parts)
        {
            try
            {
                if (Options.Git is null) throw new IOException("Git unavailable");
                new Worktrees(Options.Git).Cleanup(Execution.Text(item["project"]?["folder"]), Options.Directory, Execution.Text(mega["projectId"]),
                    Execution.Text(mega["id"]), parts.OfType<JsonObject>().Select(p => new Worktrees.Part(Execution.Text(p["folder"]), Execution.Text(p["branch"]), Execution.Text(p["subId"]))).ToArray());
            }
            catch (Exception error) { mega["nextAction"] = "Completed; worktree cleanup needs attention: " + error.Message; }
        }
        Event(Execution.Text(mega["projectId"]), "MEGATASK_COMPLETED", $"Completed {Execution.Text(mega["title"])}. {Execution.Text(mega["repoAfter"]?["disposition"])}.", new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone() });
        try
        {
            foreach (var sub in SubTasks(mega).Where(s => Execution.Text(s["status"]) == "COMPLETE"))
                if (!OutcomeRecorded(mega, sub)) RecordAccepted(mega, sub, PartFor(mega, sub), ResultFor(sub), true, 0);
        }
        catch { }
        try
        {
            var summaries = Data.Results.OfType<JsonObject>().Where(r => JsonNode.DeepEquals(r["megaTaskId"], mega["id"]) && Execution.Text(r["status"]) == "COMPLETE")
                .Select(r => Execution.Text(r["summary"])).Where(text => text.Length > 0).TakeLast(3);
            var summary = string.Join(" ", summaries); if (summary.Length == 0) summary = Execution.Text(mega["title"]);
            var folder = Execution.Text(item["project"]?["folder"]);
            Options.Plan?.Finish(folder, summary, pushed: Options.Plan.Pushed(folder));
        }
        catch { }
        var head = Execution.Text(mega["repoAfter"]?["repoHead"]);
        if (head.Length > 0 && head != Execution.Text(mega["repoBefore"]?["repoHead"]))
            Event(Execution.Text(mega["projectId"]), "GIT_COMMIT_OBSERVED", "Repository HEAD changed: " + head,
                new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone(), ["repoHead"] = head }, "commit:" + Execution.Text(mega["projectId"]) + ":" + head);
    }

    public JsonObject Advance(JsonObject item, JsonObject job, IReadOnlyList<JsonObject> parts, bool schedule = true)
    {
        Observe(item);
        var mega = Propose(item, job, parts);
        var subs = Reconcile(mega);
        if (Execution.Text(mega["status"]) == "RUNNING")
        {
            if (subs.All(s => Execution.Text(s["status"]) == "COMPLETE") && AllIntegrated(mega)) Complete(item, mega);
            else if (DeadlineDue(mega))
            {
                if (!schedule) { Paint(mega); Options.Persist(); return Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"])); }
                if (!Execution.Truthy(mega["deadlineReachedAt"]))
                {
                    mega["deadlineReachedAt"] = Options.Now();
                    Event(Execution.Text(mega["projectId"]), "DEADLINE_REACHED",
                        $"Finish by {Execution.Text(mega["deadlineAt"])} reached for {Execution.Text(mega["title"])}.",
                        new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone() }, "deadline:" + Execution.Text(mega["id"]));
                }
                mega["pauseRequested"] = true; Options.Persist();
            }
            else if (subs.Any(s => Execution.Text(s["status"]) == "FAILED"))
            {
                mega["pauseRequested"] = true; mega["nextAction"] = "Review failed results and resume only the remaining work.";
                if (!subs.Any(s => Execution.Text(s["status"]) == "RUNNING")) { Checkpoint(item, job, mega, "worker failure"); mega["status"] = "FAILED"; }
            }
            else if (Execution.Truthy(mega["pauseRequested"]))
            {
                if (!subs.Any(s => Execution.Text(s["status"]) == "RUNNING")) Checkpoint(item, job, mega, "pause after current tasks");
            }
            else if (Execution.Text(mega["authorizedSessionId"]) != Options.SessionId &&
                !subs.Any(s => Execution.Text(s["status"]) == "RUNNING") && subs.Any(s => Execution.Text(s["status"]) != "COMPLETE"))
            {
                mega["nextAction"] = "Resume the remaining work explicitly after backend restart.";
                Checkpoint(item, job, mega, "backend restarted");
            }
            else
            {
                // A shared-folder group has no branches to integrate. Branch integration is
                // performed at this same boundary by the worktree lifecycle slice.
                if (schedule && !Execution.Truthy(mega["awaitingIntegration"]) && mega["activeGroup"] is JsonArray { Count: > 0 } groupIds &&
                    groupIds.All(id => subs.Any(s => JsonNode.DeepEquals(s["id"], id) && Execution.Text(s["status"]) == "COMPLETE")) &&
                    !(mega["worktreeParts"] as JsonArray ?? []).OfType<JsonObject>().Any(p => groupIds.Any(id => JsonNode.DeepEquals(id, p["subId"])))) mega["activeGroup"] = null;
                if (schedule && (mega["activeGroup"] as JsonArray)?.Count is null or 0)
                {
                    var group = ParallelBuilds(mega);
                    if (group.Count > 0) mega["activeGroup"] = StringArray(group.Select(s => Execution.Text(s["id"])));
                }
                foreach (var sub in (schedule ? subs.Where(s => Execution.Text(s["status"]) == "PENDING") : []))
                {
                    if (DeadlineDue(mega)) break;
                    var active = mega["activeGroup"] as JsonArray;
                    if (active is { Count: > 0 } && !active.Any(id => JsonNode.DeepEquals(id, sub["id"])) && Execution.Text(sub["type"]) != "INTEGRATION") continue;
                    if (Execution.Text(sub["type"]) != "INTEGRATION" && (sub["dependencies"] as JsonArray ?? []).Any(id =>
                        (mega["worktreeParts"] as JsonArray ?? []).OfType<JsonObject>().Any(p => JsonNode.DeepEquals(p["subId"], id)) &&
                        !(mega["integratedSubIds"] as JsonArray ?? []).Any(done => JsonNode.DeepEquals(done, id)))) continue;
                    if (StepRole(sub) == "Verify" && subs.Any(other => !ReferenceEquals(other, sub) && StepRole(other) == "Build" && Execution.Text(other["status"]) != "COMPLETE")) continue;
                    if (subs.Count(s => Execution.Text(s["status"]) == "RUNNING") >= Execution.MaxParallel) break;
                    if (DependsOnComplete(sub, Data)) Launch(item, job, mega, sub);
                }
                Reconcile(mega);
                if (Execution.Text(mega["status"]) == "RUNNING" && !DeadlineDue(mega))
                {
                    if (subs.All(s => Execution.Text(s["status"]) == "COMPLETE") && AllIntegrated(mega)) Complete(item, mega);
                    else if (subs.Any(s => Execution.Text(s["status"]) == "BLOCKED") && !subs.Any(s => Execution.Text(s["status"]) == "RUNNING") &&
                        !subs.Any(s => Execution.Text(s["status"]) == "PENDING" && StepRole(s) == "Build" && DependsOnComplete(s, Data))) Checkpoint(item, job, mega, "blocked worker");
                }
            }
        }
        Paint(mega); Options.Persist();
        return Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"]));
    }

    static string TakeoverText(JsonObject sub)
    {
        if (sub["takeover"] is not JsonObject note) return "";
        var lines = new List<string> { $"HANDOFF: you take over this step from {Execution.Text(note["fromModelId"])}, which was stopped before finishing.",
            "Continue from the current state of the project folder. Do not start over: first look at what is already there (git status and the files below), keep what works, then finish the step." };
        if (note["filesChanged"] is JsonArray { Count: > 0 } files) lines.Add("Files it changed: " + string.Join(", ", files.Select(Execution.Text)));
        if (note["tests"] is JsonArray { Count: > 0 } tests) lines.Add("Tests it reported: " + string.Join("; ", tests.Select(test => test is JsonObject obj ?
            string.Join(" ", new[] { Execution.Text(obj["command"]), Execution.Text(obj["status"]) }.Where(text => text.Length > 0)) : Execution.Text(test))));
        if (Execution.Truthy(note["summary"])) lines.Add("Its last report: " + Execution.Text(note["summary"]));
        if (Execution.Truthy(note["logTail"])) lines.Add("Its last activity (log tail):\n" + Execution.Text(note["logTail"]));
        return string.Join("\n", lines) + "\n\n";
    }

    void Launch(JsonObject item, JsonObject job, JsonObject mega, JsonObject sub)
    {
        var projectId = Execution.Text(mega["projectId"]); var megaId = Execution.Text(mega["id"]);
        var row = Options.RowFor(projectId, megaId); var assigned = Execution.Text(sub["assignedModelId"]);
        var requested = Model(assigned);
        if (requested is not null && (!Execution.Truthy(requested["enabled"]) || requested["available"]?.ToJsonString() == "false")) requested = null;
        if (requested is not null && (!JsonNode.DeepEquals(requested["provider"], sub["proposedProvider"]) ||
            !JsonNode.DeepEquals(requested["cli"], sub["proposedCli"])))
        { sub["status"] = "BLOCKED"; mega["nextAction"] = "Review changed provider/model configuration before continuing."; return; }
        var worker = Options.ResolveWorker is null ? requested : Options.ResolveWorker(requested, sub);
        if (worker is not null && Execution.Text(worker["id"]) != assigned)
        {
            var from = assigned; assigned = Execution.Text(worker["id"]);
            sub["fallbackFrom"] = from; sub["assignedModelId"] = assigned;
            sub["proposedProvider"] = worker["provider"]?.DeepClone(); sub["proposedCli"] = worker["cli"]?.DeepClone();
            Event(projectId, "MODEL_USAGE_FALLBACK", $"Switched from {from} to {assigned}: the assigned model is out of quota, disabled or no longer has this role.",
                new JsonObject { ["megaTaskId"] = megaId, ["subTaskId"] = sub["id"]?.DeepClone(), ["fromModelId"] = from, ["modelId"] = assigned });
            Options.Persist();
        }
        if (worker is null || Options.SpawnWorker is null ||
            (Options.WorkerEligible is not null && !Options.WorkerEligible(worker, sub)))
        {
            var why = Options.SpawnWorker is not null ? Options.ExplainNoWorker?.Invoke(requested, sub) : null;
            if (Execution.Truthy(why?["waiting"]))
            { sub["status"] = "PENDING"; sub["waitingFor"] = why?["message"]?.DeepClone(); mega["nextAction"] = why?["message"]?.DeepClone(); return; }
            sub["status"] = "BLOCKED"; sub["waitingFor"] = null;
            mega["nextAction"] = Execution.Truthy(why?["message"]) ? why?["message"]?.DeepClone() :
                JsonValue.Create($"Enable the exact worker {assigned}, verify its remaining quota, and resume.");
            return;
        }
        sub["waitingFor"] = null;
        if (!JsonNode.DeepEquals(worker["provider"], sub["proposedProvider"]) || !JsonNode.DeepEquals(worker["cli"], sub["proposedCli"]))
        { sub["status"] = "BLOCKED"; mega["nextAction"] = "Review changed provider/model configuration and explicitly reassign this slice on Resume."; return; }
        if (Execution.Text(sub["type"]) == "RESEARCH" && ProviderAdapters.AdapterFor(worker)?.SourceProtectedResearch != true)
        { sub["status"] = "BLOCKED"; mega["nextAction"] = "Source-protected research is not supported by this adapter."; return; }
        var claim = Options.Attempts.Claim(new JsonObject { ["taskId"] = sub["id"]?.DeepClone(),
            ["projectId"] = Execution.Truthy(mega["detached"]) ? "research:" + megaId : projectId,
            ["modelId"] = worker["id"]?.DeepClone(), ["candidates"] = new JsonArray(Options.Models().Select(m => (JsonNode?)m.DeepClone()).ToArray()),
            ["megaTaskId"] = megaId, ["subTaskId"] = sub["id"]?.DeepClone(), ["research"] = Execution.Truthy(mega["detached"]) });
        if (!Execution.Truthy(claim["ok"]))
        { sub["status"] = "BLOCKED"; mega["nextAction"] = claim["reason"]?.DeepClone(); return; }
        var attemptId = Execution.Text(claim["attempt"]?["id"]);
        var projectFolder = Execution.Text(item["project"]?["folder"]);
        var linked = Options.Files.LinkedWorkspace(projectFolder);
        var research = Execution.Text(sub["type"]) == "RESEARCH";
        if (!research && linked is null)
        { sub["status"] = "BLOCKED"; mega["nextAction"] = "Relink a real local project folder before implementation.";
            Options.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = attemptId, ["reason"] = "invalid folder" }); return; }
        var folder = research ? Path.Combine(Options.Directory, "research", megaId, attemptId) : linked!;
        Worktrees.Part? worktree = null;
        if (!research && Execution.Text(sub["type"]) == "IMPLEMENTATION" && ParallelBuilds(mega).Any(s => ReferenceEquals(s, sub)))
        {
            try
            {
                var index = SubTasks(mega).ToList().FindIndex(s => ReferenceEquals(s, sub)) + 1;
                if (Options.CreateWorktree is not null) worktree = Options.CreateWorktree(folder, Options.Directory, projectId, megaId, Execution.Text(sub["id"]), index);
                else if (Options.Git is not null)
                {
                    var trees = new Worktrees(Options.Git);
                    if (trees.Repository(folder)) worktree = trees.Create(folder, Options.Directory, projectId, megaId,
                        Execution.Text(sub["id"]), index, trees.Git(folder, "branch", "--show-current"));
                }
            }
            catch (Exception error)
            {
                var failedPart = new JsonObject { ["attemptId"] = attemptId, ["exitPath"] = "", ["logPath"] = "", ["structuredResultPath"] = "",
                    ["projectFolder"] = projectFolder, ["folder"] = projectFolder };
                Collect(mega, sub, failedPart, Technical(mega, sub, failedPart, "FAILED", "Worktree creation failed: " + error.Message));
                return;
            }
            if (worktree is not null)
            {
                folder = worktree.Folder;
                mega["worktreeParts"] ??= new JsonArray();
                if (!((JsonArray)mega["worktreeParts"]!).OfType<JsonObject>().Any(p => Execution.Text(p["subId"]) == Execution.Text(sub["id"])))
                    ((JsonArray)mega["worktreeParts"]!).Add((JsonNode)new JsonObject { ["subId"] = sub["id"]?.DeepClone(), ["folder"] = folder, ["branch"] = worktree.Branch });
            }
        }
        if (research) Options.Files.CreateDirectory(folder);
        var resultPath = research ? Path.Combine(folder, "result.txt") : Path.Combine(Options.Directory, "results", attemptId + ".txt");
        var structured = research ? Path.Combine(folder, "result.json") : Path.Combine(Options.Directory, "results", attemptId + ".json");
        var part = new JsonObject { ["projectFolder"] = projectFolder, ["modelId"] = worker["id"]?.DeepClone(),
            ["label"] = worker["label"]?.DeepClone(), ["provider"] = worker["provider"]?.DeepClone(), ["cli"] = worker["cli"]?.DeepClone(),
            ["subTaskId"] = sub["id"]?.DeepClone(), ["megaTaskId"] = megaId, ["attemptId"] = attemptId, ["slice"] = sub["title"]?.DeepClone(),
            ["resultPath"] = resultPath, ["structuredResultPath"] = structured,
            ["exitPath"] = research ? Path.Combine(folder, "exit.json") : Path.Combine(Options.Directory, "results", attemptId + ".exit.json"),
            ["logPath"] = Path.Combine(Options.Directory, "logs", attemptId + ".log"), ["folder"] = folder,
            ["pid"] = null, ["alive"] = false, ["startedAt"] = Options.Now(), ["sessionId"] = Options.SessionId };
        if (worktree is not null)
        {
            part["branch"] = worktree.Branch;
            if (Options.Git is not null) part["baseHead"] = new Worktrees(Options.Git).Git(folder, "rev-parse", "HEAD");
        }
        Options.Files.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        Options.Files.Write(resultPath, Encoding.UTF8.GetBytes("STATUS: WATCHING\n"));
        Options.Attempts.AttachResult(new JsonObject { ["attemptId"] = attemptId, ["resultPath"] = structured });
        Options.FreezeSavingMode?.Invoke(projectId, sub);
        sub["attemptIds"] ??= new JsonArray(); ((JsonArray)sub["attemptIds"]!).Add((JsonNode)JsonValue.Create(attemptId)!);
        sub["status"] = "RUNNING"; sub["provider"] = worker["provider"]?.DeepClone(); sub["cli"] = worker["cli"]?.DeepClone();
        row["dispatch"] ??= new JsonArray();
        var dispatch = (JsonArray)row["dispatch"]!;
        for (var i = dispatch.Count - 1; i >= 0; i--) if (Execution.Text(dispatch[i]?["subTaskId"]) == Execution.Text(sub["id"])) dispatch.RemoveAt(i);
        dispatch.Add((JsonNode)part);
        row["attemptId"] = attemptId; row["modelId"] = worker["id"]?.DeepClone(); row["resultPath"] = resultPath;
        Options.Persist();
        var handoff = new JsonObject { ["megaTask"] = mega.DeepClone(),
            ["subTasks"] = new JsonArray(SubTasks(mega).Select(s => (JsonNode?)s.DeepClone()).ToArray()),
            ["results"] = new JsonArray(Data.Results.OfType<JsonObject>().Where(r => Execution.Text(r["megaTaskId"]) == megaId).Select(r => (JsonNode?)r.DeepClone()).ToArray()),
            ["checkpoint"] = Data.Checkpoints.OfType<JsonObject>().FirstOrDefault(c => Execution.Text(c["id"]) == Execution.Text(mega["currentCheckpointId"]))?.DeepClone(),
            ["notes"] = new JsonArray((item["home"]?["notes"] as JsonArray ?? []).OfType<JsonObject>().Where(n => Execution.Truthy(n["aiRelevant"])).Select(n => (JsonNode?)n.DeepClone()).ToArray()) };
        var scope = sub["scope"] is JsonArray { Count: > 0 } scopes
            ? $"\n\nYour scope: change only these files or folders, relative to {(worktree is null ? "the project folder" : "this worker folder")}: {string.Join(", ", scopes.Select(Execution.Text))}. {(worktree is null ? "Other workers are changing other files in this folder at the same time." : "Other workers have their own folders.")} A change outside your scope stops your part."
            : "";
        if (scope.Length > 0 && sub["scopeViolation"] is JsonArray { Count: > 0 } violations)
            scope += $"\nLast time this part was stopped for changing files outside its scope: {string.Join(", ", violations.Select(Execution.Text))}. Do not touch them: if the work needs them, say so in your result and stop.";
        var detail = TakeoverText(sub) + $"{Execution.Text(job["detail"])}\n\nSubTask: {Execution.Text(sub["detail"])}{scope}\n\n" +
            (research ? $"Source project (read only for research): {projectFolder}" : $"Project folder (work here): {folder}") +
            "\n\nDurable handoff:\n" + handoff.ToJsonString(Execution.Json);
        var request = (JsonObject)part.DeepClone();
        request["linkedFolder"] = projectFolder; request["projectId"] = projectId; request["worker"] = worker.DeepClone();
        request["resultId"] = "R-" + attemptId;
        request["job"] = new JsonObject { ["title"] = sub["title"]?.DeepClone(), ["kind"] = research ? "research" :
            Execution.Text(sub["type"]) == "AUDIT" ? "audit" : Execution.Text(job["kind"]), ["detail"] = detail };
        try
        {
            var launched = Options.SpawnWorker!(request);
            if (launched["pid"] is not JsonValue pidValue || !pidValue.TryGetValue<int>(out var pid) || pid <= 0)
                throw new InvalidOperationException("Worker did not start.");
            part["pid"] = pid; part["alive"] = true; part["observedAlive"] = true;
            part["logPath"] = Execution.Truthy(launched["logPath"]) ? launched["logPath"]?.DeepClone() : part["logPath"]?.DeepClone();
            row["workerPid"] = pid; row["workerLog"] = part["logPath"]?.DeepClone();
            Options.Store["usage"] ??= new JsonObject();
            var usage = (JsonObject)Options.Store["usage"]!;
            usage[assigned] ??= new JsonObject { ["used"] = 0 };
            var current = double.TryParse(usage[assigned]!["used"]?.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var used) ? used : 0;
            var cost = double.TryParse(worker["cost"]?.ToJsonString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rate) ? rate : 1;
            usage[assigned]!["used"] = current + cost;
            Event(projectId, "SUBTASK_STARTED", $"{Execution.Text(worker["label"] ?? worker["id"])} started {Execution.Text(sub["title"])}.",
                new JsonObject { ["megaTaskId"] = megaId, ["subTaskId"] = sub["id"]?.DeepClone(), ["attemptId"] = attemptId,
                    ["provider"] = worker["provider"]?.DeepClone(), ["cli"] = worker["cli"]?.DeepClone() });
        }
        catch (Exception error)
        {
            Collect(mega, sub, part, Technical(mega, sub, part, "FAILED", "Worker launch failed: " + error.Message));
        }
        Options.Persist();
    }
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
