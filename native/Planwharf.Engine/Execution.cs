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

    public JsonObject Advance(JsonObject item, JsonObject job, IReadOnlyList<JsonObject> parts, bool schedule = true)
    {
        Observe(item);
        var mega = Propose(item, job, parts);
        var subs = SubTasks(mega);
        if (Execution.Text(mega["status"]) == "RUNNING" && schedule)
        {
            if (DeadlineDue(mega))
            {
                if (!Execution.Truthy(mega["deadlineReachedAt"]))
                {
                    mega["deadlineReachedAt"] = Options.Now();
                    Event(Execution.Text(mega["projectId"]), "DEADLINE_REACHED",
                        $"Finish by {Execution.Text(mega["deadlineAt"])} reached for {Execution.Text(mega["title"])}.",
                        new JsonObject { ["megaTaskId"] = mega["id"]?.DeepClone() }, "deadline:" + Execution.Text(mega["id"]));
                }
                mega["pauseRequested"] = true; Options.Persist();
            }
            else if (Execution.Text(mega["authorizedSessionId"]) != Options.SessionId &&
                !subs.Any(s => Execution.Text(s["status"]) == "RUNNING") && subs.Any(s => Execution.Text(s["status"]) != "COMPLETE"))
                mega["nextAction"] = "Resume the remaining work explicitly after backend restart.";
            else if (!Execution.Truthy(mega["pauseRequested"]))
            {
                if ((mega["activeGroup"] as JsonArray)?.Count is null or 0)
                {
                    var group = ParallelBuilds(mega);
                    if (group.Count > 0) mega["activeGroup"] = StringArray(group.Select(s => Execution.Text(s["id"])));
                }
                foreach (var sub in subs.Where(s => Execution.Text(s["status"]) == "PENDING"))
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
            }
        }
        Paint(mega); Options.Persist();
        return Options.RowFor(Execution.Text(mega["projectId"]), Execution.Text(mega["id"]));
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
                sub["status"] = "FAILED"; mega["nextAction"] = "Worktree creation failed: " + error.Message;
                Options.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = attemptId, ["reason"] = "worktree creation failed" });
                Options.Persist(); return;
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
        var detail = $"{Execution.Text(job["detail"])}\n\nSubTask: {Execution.Text(sub["detail"])}{scope}\n\n" +
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
            sub["status"] = "FAILED"; mega["nextAction"] = "Worker launch failed: " + error.Message;
            Options.Attempts.CloseAttempt(new JsonObject { ["attemptId"] = attemptId, ["reason"] = "worker launch failed" });
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
