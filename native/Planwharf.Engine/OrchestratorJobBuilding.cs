// Port of orchestrator job building and work plan formation.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Planwharf.Engine;

public record JobDescriptor(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("timing")] string? Timing = null,
    [property: JsonPropertyName("deadlineAt")] string? DeadlineAt = null,
    [property: JsonPropertyName("revision_id")] string? RevisionId = null,
    [property: JsonPropertyName("executionVersion")] int? ExecutionVersion = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("goldModel")] ModelCandidate? GoldModel = null,
    [property: JsonPropertyName("proposedSubTasks")] IReadOnlyList<SubTaskProposal>? ProposedSubTasks = null
)
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
};

public record ProposedJob(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("revision_id")] string? RevisionId = null,
    [property: JsonPropertyName("timing")] string Timing = "discretion",
    [property: JsonPropertyName("deadlineAt")] string? DeadlineAt = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("goldModel")] ModelCandidate? GoldModel = null
);

public record PlanExtras(
    [property: JsonPropertyName("dispatch")] IReadOnlyList<DispatchPart>? Dispatch = null,
    [property: JsonPropertyName("job")] JobDescriptor? Job = null,
    [property: JsonPropertyName("checkpoint")] CheckpointRecord? Checkpoint = null
);

public record DispatchPart(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("slice")] string Slice,
    [property: JsonPropertyName("why")] string Why = "",
    [property: JsonPropertyName("pid")] int? Pid = null,
    [property: JsonPropertyName("logPath")] string? LogPath = null,
    [property: JsonPropertyName("resultPath")] string? ResultPath = null,
    [property: JsonPropertyName("stoppedByUser")] bool StoppedByUser = false
);

public record SubTaskProposal(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("slice")] string Slice,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<int> DependsOn,
    [property: JsonPropertyName("size")] string? Size = null,
    [property: JsonPropertyName("leadLine")] string? LeadLine = null,
    [property: JsonPropertyName("scope")] IReadOnlyList<string>? Scope = null
)
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("assignedModelId")] public string? AssignedModelId { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
};

public record PlanStepEntry(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("title")] string Title
);

public record WorkPlanResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("chat")] string? Chat = null
);

public record DecideOutcome(
    [property: JsonPropertyName("row")] ProjectRow Row,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("advanced")] bool Advanced = false,
    [property: JsonPropertyName("error")] string? Error = null
);

public record StartAutoTaskOptions(
    [property: JsonPropertyName("timing")] string Timing = "discretion",
    [property: JsonPropertyName("deadlineAt")] string? DeadlineAt = null,
    [property: JsonPropertyName("grillContext")] string? GrillContext = null,
    [property: JsonPropertyName("goldModel")] ModelCandidate? GoldModel = null
);

public record StartAutoTaskResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("phase")] string? Phase = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("job")] ProposedJob? Job = null,
    [property: JsonPropertyName("facts")] InspectionFacts? Facts = null,
    [property: JsonPropertyName("questions")] IReadOnlyList<string>? Questions = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("kind")] string? Kind = null,
    [property: JsonPropertyName("replace")] bool Replace = false,
    [property: JsonPropertyName("clearJob")] bool ClearJob = false
);

// These adapter contracts keep the port independent of the eventual composition root.
public record ModelCandidate(string Id, string? Label = null, string? Why = null, string? Provider = null,
    bool Enabled = true, bool Available = true, bool ProviderEnabled = true, bool CanCode = true,
    bool CanVerify = true, string? Plan = null, string? Role = null);
public record Revision(string? Id = null, string? Direction = null, IReadOnlyList<string>? Open = null);
public record ProjectInfo(string Id, string Name, string? Folder);
public record ProjectItem(ProjectInfo Project, IReadOnlyList<Revision> Revisions, IReadOnlyList<JobDescriptor>? Tasks = null);
public record InspectionFacts(bool Git = false, string? LastCommit = null, string? LastSubject = null,
    IReadOnlyList<string>? Recent = null, IReadOnlyList<string>? Dirty = null, string? Head = null,
    string? Branch = null, int FileCount = 0, string? Folder = null, string? Error = null);
public record CheckpointRecord(string? Path = null);
public sealed record ProjectRow
{
    public string Status { get; set; } = "idle";
    public string? Doing { get; set; }
    public string? Next { get; set; }
    public string? WaitingFor { get; set; }
    public string? JobId { get; set; }
    public string? JobFingerprint { get; set; }
    public string? JobTitle { get; set; }
    public string? JobDetail { get; set; }
    public string? JobRevisionId { get; set; }
    public bool Approved { get; set; }
    public bool HoldApproval { get; set; }
    public string? AttemptId { get; set; }
    public string? ResultPath { get; set; }
    public string? ModelId { get; set; }
    public IReadOnlyList<DispatchPart> Dispatch { get; set; } = [];
    public IReadOnlyList<string> Plan { get; set; } = [];
    public IReadOnlyList<string> Tried { get; set; } = [];
    public InspectionFacts? Facts { get; set; }
    public string? AuthorizedSessionId { get; set; }
    public bool GrillAsked { get; set; }
    public bool GrillSkipped { get; set; }
    public StartAutoTaskOptions? PendingAutoStart { get; set; }
}

public record DecideOptions(bool EnforceSessionBoundary = false, string? SessionId = null,
    IReadOnlyList<ModelCandidate>? Roster = null);
public interface IGitOperations : IPlanGit
{
    void SweepOrphans(string projectId);
}
public interface IExecutionBridge
{
    void Event(string projectId, string type, string summary);
    string? FindStatus(string projectId, string jobId);
    string Advance(ProjectItem item, JobDescriptor job, IReadOnlyList<DispatchPart> parts);
    void Replan(string projectId, string megaTaskId, IReadOnlyList<SubTaskProposal> parts, string? why);
    void Reassign(string projectId, string megaTaskId, IReadOnlyList<string> assignments, string? why);
    IReadOnlyList<SubTaskProposal> SubTasks(string projectId, string megaTaskId);
}
public interface ICrewSelector { IReadOnlyList<ModelCandidate> PickWorkers(JobDescriptor job, InspectionFacts? facts); }
public interface IFolderInspector { InspectionFacts Inspect(string folder); }
public interface IProcessLiveness { bool IsAlive(int pid); }
public interface ILeadModelAdapter
{
    ModelCandidate? LeadModel { get; }
    Task<string?> AskAsync(string projectId, ModelCandidate model, string question, string? folder, CancellationToken cancellationToken);
}
public interface IAutonomyOperations
{
    IReadOnlyList<ModelCandidate> Roster(string projectId);
    string SplitPrompt(ProjectItem item, IReadOnlyList<ModelCandidate> builders, IReadOnlyList<ModelCandidate> verifiers);
    IReadOnlyList<SubTaskProposal>? ParseSplit(string? response, IReadOnlyList<ModelCandidate> builders, IReadOnlyList<ModelCandidate> verifiers);
    string AssignmentPrompt(ProjectItem item, IReadOnlyList<SubTaskProposal> subs, IReadOnlyList<ModelCandidate> roster);
    IReadOnlyList<string>? ParseAssignment(string? response, IReadOnlyList<SubTaskProposal> subs, IReadOnlyList<ModelCandidate> roster);
}
public interface IProjectReader { Task<ProjectItem?> ReadAsync(string projectId, CancellationToken cancellationToken); }
public interface IWorkPlanManager { WorkPlanResult Open(string projectId, ProjectItem item, IReadOnlyList<PlanStepEntry>? steps); }
public interface IGrillComposer { Task<IReadOnlyList<string>?> ComposeAsync(ProjectItem item, InspectionFacts facts, CancellationToken cancellationToken); }

public static class OrchestratorJobBuilding
{
    public const string VerifySliceTitle = "Verify the work independently against the job";
    const string VerifySlice = VerifySliceTitle + ": read the requirements and run the tests, then go straight to what the tests do not cover (edge cases, error, recovery and concurrency paths), add tests for it and fix what fails. Do not rewrite code that works. If you find something you must not decide or fix yourself, write one line: \"Needs your decision: <plain words a non-programmer understands> | suggestion: <what you recommend> | breaks: yes\" only if the wrong choice could lose work or break the app, otherwise \"breaks: no\".";
    static readonly Regex HardPattern = new(@"\b(hard|difficult|escalat\w*|premium|complex)\b", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);
    static readonly Regex BuildPattern = new(@"\b(implement|code|fix|bug|test|build|write)\b", RegexOptions.ECMAScript | RegexOptions.CultureInvariant);

    public static string JobKind(JobDescriptor? job) => JobKind(job?.Title, job?.Detail);
    public static string JobKind(string? title, string? detail)
    {
        var text = ((title ?? "") + " " + (detail ?? "")).ToLowerInvariant();
        return HardPattern.IsMatch(text) ? "hard" : BuildPattern.IsMatch(text) ? "build" : "inspect";
    }

    public static int SliceCount(JobDescriptor? job, InspectionFacts? facts, int eligible)
    {
        var pool = eligible > 0 ? eligible : 1;
        return pool > 1 && (JobKind(job) is "hard" or "build" || facts?.Dirty?.Count >= 4) ? Math.Min(2, pool) : 1;
    }

    public static IReadOnlyList<DispatchPart> BuildDispatchPlan(IReadOnlyList<ModelCandidate> workers, JobDescriptor? job, InspectionFacts? facts)
    {
        if (workers.Count == 0) return [];
        var reviewer = string.Equals(job?.Kind, "review", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job?.Type, "AUDIT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(job?.Type, "REVIEW", StringComparison.OrdinalIgnoreCase);
        string[] review = ["Review the approved scope and report findings.", "Review correctness and edge cases; report findings.", "Review tests and verification evidence; report findings."];
        var dirty = facts?.Dirty?.Count > 0;
        var parts = new List<DispatchPart>(workers.Count);
        for (var i = 0; i < workers.Count; i++)
        {
            var worker = workers[i];
            var slice = reviewer && i < review.Length ? review[i]
                : !reviewer && workers.Count == 1 ? "Whole job"
                : !reviewer && i == 0 ? dirty
                    ? "Implement the whole job in the project folder, starting from the uncommitted changes, with automated tests. Run the tests before you finish."
                    : "Implement the whole job in the project folder, with automated tests. Run the tests before you finish."
                : !reviewer && i == 1 ? VerifySlice : $"Parallel slice {i + 1}";
            parts.Add(new(worker.Id, string.IsNullOrEmpty(worker.Label) ? worker.Id : worker.Label, slice, worker.Why ?? ""));
        }
        return parts;
    }

    static string Clip(string? source, int length)
    {
        var value = Regex.Replace(source ?? "", @"\s+", " ").Trim();
        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }

    public static ProposedJob JobFromFacts(Revision? revision, InspectionFacts? facts)
    {
        var goal = revision?.Direction?.Trim() ?? "";
        var subject = string.IsNullOrEmpty(facts?.LastSubject) ? "latest repo changes" : facts.LastSubject;
        var title = Clip(goal, 120);
        if (title.Length == 0) title = Clip("Continue: " + subject, 120);
        if (title.Length == 0) title = "Continue from the repo";
        var dirty = facts?.Dirty?.Take(12).ToArray() ?? [];
        var recent = facts?.Recent?.Take(5).ToArray() ?? [];
        var sections = new List<string>();
        if (goal.Length > 0) sections.Add("Goal: " + Clip(goal, 4000));
        sections.Add(!string.IsNullOrEmpty(facts?.LastCommit) ? "Latest commit: " + facts.LastCommit : facts?.Git == true ? "Git present." : "No git history.");
        if (recent.Length > 0) sections.Add("Recent:\n" + string.Join("\n", recent));
        sections.Add(dirty.Length > 0 ? "Uncommitted:\n" + string.Join("\n", dirty) : "Working tree clean.");
        sections.Add(goal.Length > 0 ? "Work toward the goal. Read only what that needs." : "Read the repo, take the latest changes, and proceed with the notebook plan.");
        return new(title, string.Join("\n\n", sections));
    }

    public static IReadOnlyList<string> PlanLines(ProjectItem item, InspectionFacts? facts, ModelCandidate shown, bool active, PlanExtras? extras = null)
    {
        var goal = Clip(item.Revisions.LastOrDefault()?.Direction, 180);
        var lines = new List<string> { $"Stay inside {item.Project.Folder}." };
        if (goal.Length > 0) lines.Add("Notebook goal: " + goal);
        if (facts?.Git == true)
        {
            lines.Add($"Latest git: {facts.LastCommit ?? facts.Head ?? "present"}{(string.IsNullOrEmpty(facts.Branch) ? "" : " on " + facts.Branch)}.");
            lines.Add(facts.Dirty?.Count > 0 ? $"Uncommitted: {facts.Dirty.Count} path(s). Read those changes." : "Working tree clean. Proceed from the latest commit.");
        }
        else lines.Add($"Read-only inspect ({facts?.FileCount ?? 0} top-level names).");
        lines.Add("Proceed with the plan from those changes.");
        if (extras?.Dispatch is { Count: > 1 } dispatch)
        {
            lines.Add($"Split into {dispatch.Count} slices for parallel workers.");
            for (var i = 0; i < dispatch.Count; i++)
                lines.Add($"Slice {i + 1}: {(string.IsNullOrEmpty(dispatch[i].Label) ? dispatch[i].ModelId : dispatch[i].Label)} — {dispatch[i].Slice}.");
        }
        else lines.Add($"{(active ? "Assigned" : "Pick")} {(string.IsNullOrEmpty(shown.Label) ? shown.Id : shown.Label)}{(string.IsNullOrEmpty(shown.Why) ? "" : " (" + shown.Why + ")")}.");
        var job = extras?.Job ?? item.Tasks?.FirstOrDefault(task => task.Status == "running");
        lines.Add(job?.Timing == "deadline" && !string.IsNullOrEmpty(job.DeadlineAt)
            ? $"Finish by {job.DeadlineAt}. At that time the orchestrator delivers the output or writes a checkpoint."
            : "Timing is at the worker’s discretion.");
        if (!string.IsNullOrEmpty(extras?.Checkpoint?.Path)) lines.Add($"Resume from checkpoint {extras.Checkpoint.Path}.");
        lines.Add("Watch the worker process, the result file (STATUS: COMPLETE), and the project folder.");
        return lines;
    }

    public static IReadOnlyList<PlanStepEntry> PlanSteps(IReadOnlyList<SubTaskProposal>? subs, IReadOnlyList<ModelCandidate> roster)
    {
        return (subs ?? []).Select(sub =>
        {
            var title = sub.Title ?? sub.Slice;
            var role = sub.Type == "AUDIT" ? "Review" : sub.Type == "RESEARCH" ? "Research"
                : title.StartsWith("Verify the work independently", StringComparison.Ordinal) ? "Verify" : "Build";
            var id = sub.AssignedModelId ?? sub.ModelId;
            var model = roster.FirstOrDefault(item => item.Id == id)?.Label ?? sub.Label ?? id;
            return new PlanStepEntry(role, string.IsNullOrEmpty(model) ? "unassigned" : model, title);
        }).ToArray();
    }

    public static string JobFingerprint(JobDescriptor? job)
    {
        if (job == null) return "";
        var data = new JsonArray(
            (JsonNode?)JsonValue.Create(job.Id),
            (JsonNode?)JsonValue.Create(job.Title),
            (JsonNode?)JsonValue.Create(job.Detail),
            string.IsNullOrEmpty(job.RevisionId) ? null : (JsonNode?)JsonValue.Create(job.RevisionId)
        );
        if (job.GoldModel != null)
        {
            var m = job.GoldModel;
            var gold = new JsonObject { ["id"] = m.Id };
            if (m.Label != null) gold["label"] = m.Label;
            if (m.Why != null) gold["why"] = m.Why;
            if (m.Provider != null) gold["provider"] = m.Provider;
            gold["enabled"] = m.Enabled;
            gold["available"] = m.Available;
            gold["providerEnabled"] = m.ProviderEnabled;
            gold["canCode"] = m.CanCode;
            gold["canVerify"] = m.CanVerify;
            if (m.Plan != null) gold["plan"] = m.Plan;
            if (m.Role != null) gold["role"] = m.Role;
            data.Add((JsonNode)gold);
        }
        var json = data.ToJsonString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static string ResultStatus(string? file)
    {
        if (string.IsNullOrEmpty(file)) return "MISSING";
        try
        {
            var raw = File.ReadAllText(file);
            if (Regex.IsMatch(raw, @"(^|\n)\s*STATUS:\s*COMPLETE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "COMPLETE";
            if (Regex.IsMatch(raw, @"(^|\n)\s*STATUS:\s*FAILED\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "FAILED";
            if (Regex.IsMatch(raw, @"(^|\n)\s*STATUS:\s*BLOCKED\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "BLOCKED";
            if (Regex.IsMatch(raw, @"(^|\n)\s*STATUS:\s*WATCHING\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "WATCHING";
            return "OTHER";
        }
        catch
        {
            return "MISSING";
        }
    }

    public static WorkPlanResult OpenWorkPlan(
        string projectId,
        ProjectItem item,
        IReadOnlyList<PlanStepEntry>? steps,
        IGitOperations? git,
        IExecutionBridge? bridge,
        bool keepWorkPlan = false)
    {
        var folder = ProjectWorkspace.LinkedWorkspace(item?.Project?.Folder);
        if (string.IsNullOrEmpty(folder)) return new(true);
        var goal = item?.Revisions?.LastOrDefault()?.Direction?.Trim() ?? "";
        PlanPrepareResult prepared;
        try
        {
            var planFile = new PlanFile(git);
            var planSteps = (steps ?? []).Select(s => new PlanStep(Role: s.Role, Model: s.Model, Title: s.Title)).ToArray();
            prepared = planFile.Prepare(folder, new PlanPrepareOptions(Goal: goal, Steps: planSteps, KeepWorkPlan: keepWorkPlan));
        }
        catch (Exception error)
        {
            return new(false, error.Message);
        }
        if (!prepared.Ok)
        {
            bridge?.Event(projectId, "AUTONOMY", prepared.Reason ?? "Prepare failed");
            return new(false, prepared.Reason);
        }
        if (ProjectWorkspace.SafeGitWorkspace(folder) != null && git != null)
        {
            try
            {
                var prune = git.Run(folder, ["worktree", "prune"]);
                if (!prune.Ok) return new(false, $"Git worktree cleanup failed: {prune.Error ?? prune.Stderr}");
            }
            catch (Exception error)
            {
                return new(false, $"Git worktree cleanup failed: {error.Message}");
            }
            try
            {
                git.SweepOrphans(projectId);
            }
            catch (Exception error)
            {
                return new(false, $"Git worktree cleanup failed: {error.Message}");
            }
        }
        if (!string.IsNullOrEmpty(prepared.Chat))
        {
            bridge?.Event(projectId, "AUTONOMY", prepared.Chat);
        }
        return new(true, Chat: prepared.Chat);
    }

    public static DecideOutcome Decide(
        ProjectItem item,
        ProjectRow row,
        IExecutionBridge bridge,
        ICrewSelector crewSelector,
        IFolderInspector inspector,
        IProcessLiveness liveness,
        DecideOptions options)
    {
        var id = item.Project.Id;
        var latest = item.Revisions?.LastOrDefault();
        var job = item.Tasks?.FirstOrDefault(task => task.Status == "running");

        if (job?.ExecutionVersion == 2 && ((latest?.Open == null || latest.Open.Count == 0) || job.Source == "chat"))
        {
            row.JobId = job.Id;
            row.JobFingerprint = JobFingerprint(job);
            row.JobTitle = job.Title;
            row.JobDetail = job.Detail;
            row.JobRevisionId = job.RevisionId;
            try
            {
                var existingStatus = bridge.FindStatus(id, job.Id ?? "");
                var linked = ProjectWorkspace.LinkedWorkspace(item.Project.Folder);
                var v2Facts = existingStatus != null && existingStatus != "RUNNING" && row.Facts?.Folder == linked
                    ? row.Facts
                    : inspector.Inspect(item.Project.Folder ?? "");
                row.Facts = v2Facts;

                IReadOnlyList<DispatchPart> parts;
                if (job.ProposedSubTasks is { Count: > 0 } proposals)
                {
                    parts = proposals.Select(sub =>
                    {
                        var role = sub.Type == "AUDIT" ? "reviewer" : sub.Type == "RESEARCH" ? "researcher"
                            : (sub.Title ?? sub.Slice).StartsWith(VerifySliceTitle, StringComparison.Ordinal) ? "verifier" : "worker";
                        var modelId = job.GoldModel != null && role == "worker" ? job.GoldModel.Id : (sub.AssignedModelId ?? sub.ModelId);
                        return new DispatchPart(modelId, sub.Label ?? modelId, sub.Title ?? sub.Slice, "");
                    }).ToArray();
                }
                else
                {
                    var v2Workers = crewSelector.PickWorkers(job, v2Facts);
                    parts = BuildDispatchPlan(v2Workers, job, v2Facts);
                }

                var status = bridge.Advance(item, job, parts);
                row.Status = status;
                return new(row, status, Advanced: true);
            }
            catch (Exception error)
            {
                row.Status = "blocked_no_worker";
                row.Doing = error.Message;
                row.Next = error.Message;
                return new(row, "blocked_no_worker", Advanced: false, Error: error.Message);
            }
        }

        var recordedFiles = (row.Dispatch ?? []).Select(p => p?.ResultPath).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (recordedFiles.Count == 0 && !string.IsNullOrEmpty(row.ResultPath)) recordedFiles.Add(row.ResultPath);

        var everySliceHasResultPath = row.Dispatch is not { Count: > 0 }
            || row.Dispatch.All(part => !string.IsNullOrEmpty(part?.ResultPath));
        if (job != null && row.JobId == job.Id && row.JobFingerprint == JobFingerprint(job)
            && !(row.Dispatch ?? []).Any(p => p?.StoppedByUser == true)
            && everySliceHasResultPath && recordedFiles.Count > 0
            && recordedFiles.All(file => ResultStatus(file) == "COMPLETE"))
        {
            row.Status = "complete";
            row.Doing = $"Evidence complete for {job.Title} on {item.Project.Name}.";
            row.WaitingFor = "Nothing. Evidence is complete.";
            row.Next = "Read the result, or add a new job.";
        }

        var completedJob = job ?? item.Tasks?.FirstOrDefault(task => task.Id == row.JobId && task.Status == "complete");
        if (row.Status == "complete" && completedJob != null && row.JobId == completedJob.Id
            && (string.IsNullOrEmpty(row.JobFingerprint) || row.JobFingerprint == JobFingerprint(completedJob)))
        {
            return new(row, "complete");
        }

        if (job != null && !string.IsNullOrEmpty(row.JobId) && (row.JobId != job.Id || (!string.IsNullOrEmpty(row.JobFingerprint) && row.JobFingerprint != JobFingerprint(job))))
        {
            row.Approved = false;
            row.HoldApproval = false;
            row.AttemptId = null;
            row.ModelId = null;
            row.Plan = [];
            row.Dispatch = [];
            row.Tried = [];
            bridge.Event(id, "AUTONOMY", "Job changed. Approval reset.");
        }

        row.JobId = job?.Id;
        row.JobFingerprint = job != null ? JobFingerprint(job) : null;
        row.JobTitle = job?.Title;
        row.JobDetail = job?.Detail;
        row.JobRevisionId = job?.RevisionId;

        var ongoing = (row.Dispatch ?? []).Any(p => p != null && !p.StoppedByUser && p.Pid.HasValue && liveness.IsAlive(p.Pid.Value));
        if (options.EnforceSessionBoundary && row.AuthorizedSessionId != options.SessionId && !ongoing)
        {
            if (row.Status == "complete") return new(row, "complete");
            row.Approved = false;
            row.HoldApproval = true;
        }

        if (job == null)
        {
            row.Status = "idle";
            row.Doing = row.PendingAutoStart != null
                ? $"Auto start is waiting to plan {item.Project.Name}."
                : $"No job on {item.Project.Name}. Tell the lead what to build, or let the crew do it.";
            row.WaitingFor = "Auto start";
            row.Next = "Tell the lead what to build, or let the crew do it.";
            row.Dispatch = [];
            row.Approved = false;
            row.HoldApproval = false;
            return new(row, "idle");
        }

        if (string.IsNullOrEmpty(item.Project.Folder))
        {
            row.Status = "blocked_no_folder";
            row.Doing = $"{item.Project.Name} has a job ({job.Title}) but no local folder. Set place so the orchestrator can inspect the repo.";
            row.WaitingFor = "A local folder";
            row.Next = "Set place with a folder on this PC.";
            return new(row, "blocked_no_folder");
        }

        var facts = inspector.Inspect(item.Project.Folder) ?? new InspectionFacts();
        row.Facts = facts;
        if (!string.IsNullOrEmpty(facts?.Error))
        {
            row.Status = "blocked_no_folder";
            row.Doing = $"Cannot inspect {item.Project.Name}: {facts.Error}.";
            row.WaitingFor = "A readable folder";
            row.Next = $"Fix the folder: {facts.Error}.";
            return new(row, "blocked_no_folder");
        }

        var workers = crewSelector.PickWorkers(job, facts);
        if (workers.Count == 0)
        {
            row.Status = "blocked_no_worker";
            row.Doing = "No eligible worker. Turn a model on, or wait until ChatGPT, Grok, or Gemini usage resets.";
            row.WaitingFor = "An enabled model whose provider still has usage";
            row.Next = "Turn a model on, or wait until a plan limit resets.";
            return new(row, "blocked_no_worker");
        }

        var keepDispatch = row.Dispatch is { Count: > 0 } && row.Dispatch.Any(part => part?.Pid != null || !string.IsNullOrEmpty(part?.ResultPath));
        if (!keepDispatch)
        {
            row.Dispatch = BuildDispatchPlan(workers, job, facts);
        }

        var shown = workers[0];
        row.Plan = PlanLines(item, facts, shown, false, new PlanExtras(row.Dispatch, job));

        if (!row.Approved)
        {
            row.Status = "needs_approval";
            return new(row, "needs_approval");
        }

        row.Status = "running";
        return new(row, "running");
    }

    public static async Task<bool> PlanByLeadAsync(
        string projectId,
        ProjectItem item,
        string megaTaskId,
        ILeadModelAdapter leadAdapter,
        IExecutionBridge bridge,
        IAutonomyOperations autonomyOps,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubTaskProposal> subs;
        try { subs = bridge.SubTasks(projectId, megaTaskId); } catch { return false; }
        var lead = leadAdapter.LeadModel;
        if (lead == null || subs.Count == 0) return false;
        if (subs.Any(sub => sub.Type != null && sub.Type != "IMPLEMENTATION"))
        {
            bridge.Event(projectId, "AUTONOMY", "The work is not split: it has a Gold model or a step that is not a build.");
            return false;
        }

        var roster = autonomyOps.Roster(projectId);
        var builders = roster.Where(m => m.Enabled && m.Available && m.ProviderEnabled && m.CanCode).ToArray();
        var verifiers = roster.Where(m => m.Enabled && m.Available && m.ProviderEnabled && m.CanVerify).ToArray();

        if (builders.Length < 2 || verifiers.Length == 0)
        {
            bridge.Event(projectId, "AUTONOMY", $"The work is not split: {(builders.Length < 2 ? "fewer than two models can build now (roles, saving mode, quota limits)" : "no model can verify now")}.");
            return false;
        }

        var question = autonomyOps.SplitPrompt(item, builders, verifiers);
        IReadOnlyList<SubTaskProposal>? split = null;
        string problem = "";
        for (var round = 0; round < 2 && split == null; round++)
        {
            var ask = round == 0
                ? question
                : $"{question}\n\nYour previous reply could not be used ({problem}). Reply with only the JSON object: every part needs a title, a detail, files (paths relative to the project folder, never absolute or with ..), dependsOn with indexes of earlier parts only, a size and a model.";
            string? spoken;
            try
            {
                spoken = await leadAdapter.AskAsync(projectId, lead, ask, item.Project.Folder, cancellationToken);
            }
            catch (Exception error)
            {
                problem = "the lead call failed: " + error.Message;
                continue;
            }
            split = autonomyOps.ParseSplit(spoken, builders, verifiers);
            if (split == null)
            {
                problem = (spoken ?? "").Contains("\"parts\"") ? "a part broke a rule" : "no JSON object with parts";
            }
        }

        if (split == null)
        {
            bridge.Event(projectId, "AUTONOMY", $"The lead did not split the work in a usable way ({problem}); it keeps one build step.");
            return false;
        }

        try
        {
            bridge.Replan(projectId, megaTaskId, split, "Lead model split");
            return true;
        }
        catch (Exception error)
        {
            bridge.Event(projectId, "AUTONOMY", "The lead split was refused: " + error.Message);
            return false;
        }
    }

    public static async Task AssignByLeadAsync(
        string projectId,
        ProjectItem item,
        string megaTaskId,
        ILeadModelAdapter leadAdapter,
        IExecutionBridge bridge,
        IAutonomyOperations autonomyOps,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SubTaskProposal> subs;
        try { subs = bridge.SubTasks(projectId, megaTaskId); } catch { return; }
        var lead = leadAdapter.LeadModel;
        if (subs.Count < 2 || lead == null) return;
        var roster = autonomyOps.Roster(projectId);
        var question = autonomyOps.AssignmentPrompt(item, subs, roster);
        string? spoken;
        try
        {
            spoken = await leadAdapter.AskAsync(projectId, lead, question, item.Project.Folder, cancellationToken);
        }
        catch
        {
            return;
        }
        var choice = autonomyOps.ParseAssignment(spoken, subs, roster);
        if (choice == null || choice.Count == 0)
        {
            bridge.Event(projectId, "AUTONOMY", "Lead crew choice was unreadable; keeping the proposed crew.");
            return;
        }
        try
        {
            bridge.Reassign(projectId, megaTaskId, choice, "Lead assigned crew");
        }
        catch (Exception error)
        {
            bridge.Event(projectId, "AUTONOMY", "Lead crew choice was refused: " + error.Message);
        }
    }

    public static async Task<StartAutoTaskResult> StartAutoTaskAsync(
        string projectId,
        StartAutoTaskOptions? options,
        IProjectReader projectReader,
        IFolderInspector inspector,
        IWorkPlanManager workPlanMgr,
        IExecutionBridge bridge,
        IGrillComposer grillComposer,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return new(false, Reason: "project not found");
        var item = await projectReader.ReadAsync(projectId, cancellationToken);
        if (item == null) return new(false, Reason: "project not found");
        if (string.IsNullOrEmpty(item.Project.Folder)) return new(false, Reason: "no folder");

        var timing = "discretion";
        string? deadlineAt = null;
        if (options?.Timing == "deadline")
        {
            if (string.IsNullOrEmpty(options.DeadlineAt) || !DateTimeOffset.TryParse(options.DeadlineAt, out var dto))
            {
                return new(false, Reason: "deadline required");
            }
            timing = "deadline";
            deadlineAt = dto.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        }

        var facts = inspector.Inspect(item.Project.Folder) ?? new InspectionFacts();
        if (!string.IsNullOrEmpty(facts.Error)) return new(false, Reason: facts.Error);

        var clearJob = item.Tasks?.Any(task => task.Status == "running") == true;
        var latest = item.Revisions?.LastOrDefault();
        var open = latest?.Open ?? [];
        var genericOpen = open.Count == Plan.GenericFrontier.Length && open.SequenceEqual(Plan.GenericFrontier);

        if (string.IsNullOrEmpty(options?.GrillContext))
        {
            try
            {
                var questions = await grillComposer.ComposeAsync(item, facts, cancellationToken);
                if (questions is { Count: > 0 })
                {
                    bridge.Event(projectId, "AUTONOMY", "Auto start: clarification interview.");
                    return new(
                        Ok: true,
                        Phase: "grill",
                        Facts: facts,
                        Questions: questions,
                        Title: "Grill me",
                        Kind: "me",
                        Replace: genericOpen,
                        ClearJob: clearJob
                    );
                }
            }
            catch (Exception error)
            {
                bridge.Event(projectId, "AUTONOMY", "Clarification interview unavailable: " + error.Message);
                return new(false, Reason: error.Message);
            }
        }

        var opened = workPlanMgr.Open(projectId, item, []);
        if (!opened.Ok)
        {
            return new(false, Reason: opened.Reason);
        }

        var job = JobFromFacts(latest, facts);
        var detail = !string.IsNullOrEmpty(options?.GrillContext)
            ? job.Detail + "\n\nGrill discussion for this task:\n" + options.GrillContext
            : job.Detail;
        var source = !string.IsNullOrEmpty(options?.GrillContext) ? "chat" : null;
        var payload = new ProposedJob(
            Title: job.Title,
            Detail: detail,
            RevisionId: latest?.Id,
            Timing: timing,
            DeadlineAt: deadlineAt,
            Source: source,
            GoldModel: options?.GoldModel
        );

        bridge.Event(projectId, "AUTONOMY", "Read the repo and latest changes. Plan ready for approval.");
        return new(
            Ok: true,
            Phase: "plan",
            Job: payload,
            Facts: facts,
            ClearJob: clearJob
        );
    }
}
