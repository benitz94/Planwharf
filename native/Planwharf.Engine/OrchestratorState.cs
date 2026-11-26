// Port of engine/orchestrator.js state management.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Planwharf.Engine;

public record PipelineStepState(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("state")] string State
);

public record CrewMember(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("doing")] string Doing
);

public record DocInfo(
    [property: JsonPropertyName("path")] string Path
)
{
    public static implicit operator DocInfo(string path) => new(path);
    public static implicit operator string(DocInfo doc) => doc.Path;
}

// The notebook and execution adapters deliberately keep their input shape small. Unknown
// fields remain in Raw so a view never becomes a lossy second store.
public sealed record NotebookRevision(
    string? Direction = null,
    string State = "",
    IReadOnlyList<string>? Open = null,
    IReadOnlyList<string>? Closed = null);

public sealed record NotebookTask(
    string Id,
    string Title,
    string Detail = "",
    string Status = "",
    string? RevisionId = null,
    int ExecutionVersion = 1,
    string Timing = "discretion",
    string? DeadlineAt = null,
    string? MegaTaskId = null);

public sealed record NotebookProject(
    string Id,
    string Name,
    string? Folder = null,
    string? Github = null,
    IReadOnlyList<NotebookRevision>? Revisions = null,
    IReadOnlyList<NotebookTask>? Tasks = null,
    string Goal = "",
    bool GoalStarted = false,
    string ChatMode = "plan",
    string SavingMode = "standard",
    IReadOnlyList<string>? Notes = null,
    string StateArt = "",
    IReadOnlyList<string>? Open = null,
    JsonObject? Raw = null);

public sealed record NotebookPlan(
    IReadOnlyList<NotebookProject> Projects,
    JsonObject? Raw = null);

public sealed record SessionRecord(
    string Id,
    string? ProjectId = null,
    string? StartedAt = null,
    string? Status = null,
    JsonObject? Raw = null);

public sealed record WorkerHistoryRecord(
    string Id,
    string Status = "",
    IReadOnlyList<DispatchPart>? Dispatch = null,
    string? At = null,
    JsonObject? Raw = null);

public sealed record AutoDecideSettings(bool Enabled = false, string? SessionId = null, JsonObject? Raw = null);
public sealed record AutonomySettings(bool Enabled = false, bool Started = false, string? StopReason = null, JsonObject? Raw = null);
public sealed record ModelInfo(string Id, string Label, string? Provider = null, string? Cli = null, bool Enabled = true, bool Available = true, string? Plan = null, JsonObject? Raw = null);
public sealed record AskRecord(string Id, string ProjectId, string Question, string? Answer = null, string? At = null, JsonObject? Raw = null);
public sealed record ToolSettings(JsonObject? Values = null);
public sealed record OrchestratorChoice(string Id, string Label, string? ProviderId = null, string? Plan = null);
public sealed record DecisionRecord(string Id, string Text, string? ProjectId = null, string? Suggestion = null, bool Breaks = false, string? Answer = null, JsonObject? Raw = null);
public sealed record PlainSubTask(string Id, string Title = "", string Status = "", string? ResultId = null, JsonObject? Raw = null);
public sealed record PlainWorkSummary(string Status = "", string Summary = "", JsonObject? Raw = null);
public sealed record PlainResultSummary(string AttemptId, string Status = "", string Summary = "", JsonObject? Raw = null);
public sealed record ActivityPaginationInfo(bool Available, int Total, int PageSize);

public sealed record ActivitySession(
    string Id,
    string? ProjectId,
    string? JobId,
    string? MegaTaskId,
    string? SubTaskId,
    string? AttemptId,
    string? RecordedSessionId,
    string? Role,
    string? Provider,
    string? Adapter,
    string? Model,
    string? ModelId,
    string? StartedAt,
    string? CompletedAt,
    string? ReportedResultAt,
    string ProcessState,
    string State,
    bool Historical,
    string? ValidatedResultStatus,
    string? RecordedResultStatus,
    int? ExitCode,
    IReadOnlyList<JsonObject> Events,
    IReadOnlyList<string> Warnings,
    JsonObject? Raw = null);

public sealed record DispatchView(
    string? SubTaskId,
    string? MegaTaskId,
    string? AttemptId,
    string? ModelId,
    string? Label,
    string? Slice,
    string? Folder,
    string? ResultPath,
    string? LogPath,
    int? Pid,
    bool Alive,
    bool StoppedByUser,
    string? Activity,
    ActivitySession? ActivitySession = null,
    bool HistoricalAssignment = false,
    string? AssignmentJobTitle = null,
    string? AssignmentStatus = null,
    bool ObservedAlive = false,
    JsonObject? Diagnostic = null);

public sealed record ProjectSnapshot
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Folder { get; init; }
    public string? Github { get; init; }
    public RepositoryInfo? Repository { get; init; }
    public string Goal { get; init; } = "";
    public bool GoalStarted { get; init; }
    public string ChatMode { get; init; } = "plan";
    public string SavingMode { get; init; } = "standard";
    public IReadOnlyList<string> Notes { get; init; } = [];
    public string StateArt { get; init; } = "";
    public IReadOnlyList<string> Open { get; init; } = [];
    public string Status { get; init; } = "idle";
    public string Label { get; init; } = "Idle";
    public string Doing { get; init; } = "";
    public IReadOnlyList<string> Plan { get; init; } = [];
    public InspectionFacts? Facts { get; init; }
    public string? JobTitle { get; init; }
    public string? JobId { get; init; }
    public string? AttemptId { get; init; }
    public string? MegaTaskId { get; init; }
    public MegaTaskRecord? MegaTask { get; init; }
    public IReadOnlyList<PlainSubTask> SubTasks { get; init; } = [];
    public PlainWorkSummary Plain { get; init; } = new();
    public IReadOnlyList<PlainResultSummary> Results { get; init; } = [];
    public string? CheckpointId { get; init; }
    public IReadOnlyList<CheckpointRecord> ResumableCheckpoints { get; init; } = [];
    public string? ModelId { get; init; }
    public string? WorkerLabel { get; init; }
    public string WaitingFor { get; init; } = "";
    public IReadOnlyList<DecisionRecord> OpenDecisions { get; init; } = [];
    public string Next { get; init; } = "";
    public string Step { get; init; } = "idle";
    public IReadOnlyList<PipelineStepState> Pipeline { get; init; } = [];
    public string LeadLabel { get; init; } = OrchestratorState.OrchestratorDefaultName;
    public IReadOnlyList<CrewMember> Crew { get; init; } = [];
    public string? ResultPath { get; init; }
    public string? WatchingSince { get; init; }
    public bool Approved { get; init; }
    public string? SessionId { get; init; }
    public bool CanStart { get; init; }
    public bool CanResume { get; init; }
    public bool CanStop { get; init; }
    public bool PendingAutoStart { get; init; }
    public IReadOnlyList<DispatchView> Dispatch { get; init; } = [];
    public ActivityPaginationInfo ActivitySessionPagination { get; init; } = new(true, 0, global::Planwharf.Engine.ActivitySessions.SessionLimit);
    public IReadOnlyList<ActivitySession> ActivitySessions { get; init; } = [];
    public CheckpointRecord? Checkpoint { get; init; }
    public string? Timing { get; init; }
    public string? DeadlineAt { get; init; }
    public bool CanGrillDocs { get; init; }
    public string DocsAvailability { get; init; } = "unknown";
    public IReadOnlyList<DocInfo> Docs { get; init; } = [];
    public bool GrillSkipped { get; init; }
    public IReadOnlyList<ProjectLogLine> Log { get; init; } = [];
    public int? WorkerPid { get; init; }
    public bool WorkerAlive { get; init; }
    public string? WorkerLog { get; init; }
    public string WorkerTail { get; init; } = "";
    public string WorkerActivity { get; init; } = "";
    public string OrchestratorDoing { get; init; } = "Idle.";
    public string OrchestratorLabel { get; init; } = OrchestratorState.OrchestratorDefaultName;
    public MonitoringMetrics? Monitoring { get; init; }
}

public sealed record OrchestratorStateSnapshot
{
    public string UpdatedAt { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? SelectedProjectId { get; init; }
    public ExecutionState? Execution { get; init; }
    public IReadOnlyList<SessionRecord> Sessions { get; init; } = [];
    public IReadOnlyList<WorkerHistoryRecord> WorkerHistory { get; init; } = [];
    public string Mode { get; init; } = "manual";
    public IReadOnlyDictionary<string, AutoDecideSettings> AutoDecide { get; init; } = new Dictionary<string, AutoDecideSettings>();
    public bool DefaultAutoDecide { get; init; }
    public DecisionsInbox DecisionsInbox { get; init; } = new([], [], 0);
    public IReadOnlyDictionary<string, AutonomySettings> Autonomy { get; init; } = new Dictionary<string, AutonomySettings>();
    public bool GrillPropose { get; init; } = true;
    public string GrillKind { get; init; } = "me";
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];
    public IReadOnlyList<ResearchRecord> Researches { get; init; } = [];
    public IReadOnlyList<AskRecord> Asks { get; init; } = [];
    public IReadOnlyDictionary<string, object> Thinking { get; init; } = new Dictionary<string, object>();
    public IReadOnlyDictionary<string, object> ThinkingByProject { get; init; } = new Dictionary<string, object>();
    public IReadOnlyDictionary<string, ToolSettings> ProjectTools { get; init; } = new Dictionary<string, ToolSettings>();
    public IReadOnlyDictionary<string, ToolSettings> ProjectToolSettings { get; init; } = new Dictionary<string, ToolSettings>();
    public bool Asking { get; init; }
    public string? OrchestratorId { get; init; }
    public string? SelectedOrchestratorId { get; init; }
    public string? TeamProjectId { get; init; }
    public bool OwnTeam { get; init; }
    public string? GoldModelId { get; init; }
    public string? OrchestratorUnavailableReason { get; init; }
    public string OrchestratorLabel { get; init; } = OrchestratorState.OrchestratorDefaultName;
    public IReadOnlyList<OrchestratorChoice> Orchestrators { get; init; } = [];
    public IReadOnlyList<ProjectSnapshot> Projects { get; init; } = [];
}

public sealed record RepositoryInspectOptions(bool Force = false);
public sealed record InspectRepositoryResult(bool Ok, RepositoryInfo? Repository = null, OrchestratorStateSnapshot? Snapshot = null, string? Reason = null);
public sealed record DecisionsInbox(IReadOnlyList<DecisionRecord> Open, IReadOnlyList<DecisionRecord> Answered, int OpenCount);
public sealed record ArchivedSummary(string ProjectId, string Reason, string At);
public sealed record ArchivedProjectState(JsonObject State);
public sealed record ProjectArchiveRecord(string ProjectId, string Reason, string At, NotebookProject? Notebook, ArchivedProjectState State);
public sealed record ArchiveProjectOptions(bool KeepTeam = false, NotebookProject? Notebook = null);
public sealed record ArchiveProjectResult(bool Ok, ArchivedSummary? Archived = null, string? Reason = null);
public sealed record SetFocusResult(bool Ok, string? SelectedProjectId, string? Reason = null);
public sealed record PagedActivitySessions(string ProjectId, int Total, int Offset, int Limit, IReadOnlyList<ActivitySession> Sessions);

public sealed record ResearchRecord(
    string Id,
    string ProjectId,
    string? MegaTaskId = null,
    string? AttemptId = null,
    string? ModelId = null,
    string? Provider = null,
    string? Cli = null,
    string Status = "",
    string? Doing = null,
    string? Activity = null,
    string? Reason = null,
    string? Folder = null,
    string? ReportPath = null,
    string? ResultPath = null,
    string? LogPath = null,
    int? Pid = null,
    bool Alive = false,
    bool ReportAvailable = false,
    string WorkerTail = "",
    string? SessionId = null,
    string StartedAt = "",
    string UpdatedAt = "",
    int ExecutionVersion = 2,
    IReadOnlyList<DispatchView>? Dispatch = null);

public record ProjectLogLine(
    [property: JsonPropertyName("at")] string At,
    [property: JsonPropertyName("text")] string Text
);

public record PendingAutoStart(
    [property: JsonPropertyName("timing")] string Timing = "discretion",
    [property: JsonPropertyName("deadlineAt")] string? DeadlineAt = null,
    [property: JsonPropertyName("grillContext")] string? GrillContext = null,
    [property: JsonPropertyName("goldModel")] ModelCandidate? GoldModel = null
);

public record MonitoringMetrics(
    [property: JsonPropertyName("cpuPercent")] double? CpuPercent = null,
    [property: JsonPropertyName("memoryBytes")] long? MemoryBytes = null
);

public record RepositoryInfo(
    [property: JsonPropertyName("checkedAt")] string? CheckedAt = null,
    [property: JsonPropertyName("folder")] string? Folder = null,
    [property: JsonPropertyName("github")] string? Github = null,
    [property: JsonPropertyName("local")] JsonObject? Local = null,
    [property: JsonPropertyName("remote")] JsonObject? Remote = null,
    [property: JsonPropertyName("comparison")] JsonObject? Comparison = null,
    [property: JsonPropertyName("error")] string? Error = null
);

public record MegaTaskRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("taskId")] string TaskId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("status")] string Status
);

public record SubTaskRecord(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("megaTaskId")] string MegaTaskId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("status")] string Status
);

public record ExecutionState(
    [property: JsonPropertyName("megaTasks")] IReadOnlyList<JsonObject>? MegaTasks = null,
    [property: JsonPropertyName("subTasks")] IReadOnlyList<JsonObject>? SubTasks = null,
    [property: JsonPropertyName("results")] IReadOnlyList<JsonObject>? Results = null,
    [property: JsonPropertyName("checkpoints")] IReadOnlyList<JsonObject>? Checkpoints = null,
    [property: JsonPropertyName("timeline")] IReadOnlyList<JsonObject>? Timeline = null,
    [property: JsonPropertyName("observations")] JsonObject? Observations = null,
    [property: JsonPropertyName("decisions")] IReadOnlyList<JsonObject>? Decisions = null
);

public record ProjectEntry(
    string Status,
    string Doing,
    IReadOnlyList<string> Plan,
    InspectionFacts? Facts,
    string? JobId,
    string? JobTitle,
    string? AttemptId,
    string? ModelId,
    bool Approved,
    string WaitingFor,
    string Next,
    string? WorkerLabel,
    string? ResultPath,
    string? WatchingSince,
    bool HoldApproval,
    bool GrillSkipped,
    IReadOnlyList<DocInfo> Docs,
    IReadOnlyList<ProjectLogLine> Log,
    PendingAutoStart? PendingAutoStart,
    CheckpointRecord? Checkpoint,
    IReadOnlyList<DispatchPart> Dispatch,
    bool ResumeRequested,
    bool DeadlineHonored,
    int? WorkerPid,
    string? WorkerLog,
    bool WorkerAlive,
    IReadOnlyList<string> Tried,
    string? ProgressAt,
    string? ProgressKey,
    string WorkerTail,
    string WorkerActivity,
    string? MegaTaskId = null,
    MegaTaskRecord? MegaTask = null,
    IReadOnlyList<SubTaskRecord>? SubTasks = null,
    RepositoryInfo? Repository = null,
    MonitoringMetrics? Monitoring = null,
    IReadOnlyDictionary<string, string>? StepModes = null,
    IReadOnlyDictionary<string, bool>? SavingNotes = null,
    string? AuthorizedSessionId = null
);

public sealed record OrchestratorStateCollaborators(
    Func<string>? Now = null,
    string? SessionId = null,
    Action<string, string>? Rename = null,
    Func<string?, string>? LogStat = null,
    Func<string, bool>? IsProcessAlive = null,
    Func<CancellationToken, Task<NotebookPlan>>? ReadPlan = null,
    Func<string, RepositoryInspectOptions?, CancellationToken, Task<RepositoryInfo>>? InspectRepository = null
);

public sealed class OrchestratorState
{
    public const string OrchestratorDefaultName = "Orchestrator";

    private static readonly (string Id, string Label)[] PipelineSteps =
    [
        ("check", "Check"),
        ("grill", "Grill"),
        ("plan", "Plan"),
        ("approve", "Approve"),
        ("assign", "Assign"),
        ("watch", "Watch")
    ];

    public static readonly JsonSerializerOptions JsonSerializationOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string? _dir;
    private readonly string? _stateFile;
    private readonly OrchestratorStateCollaborators _collaborators;
    private readonly JsonObject _store;
    private readonly string _sessionId;
    private readonly Func<string> _now;
    private readonly Action<string, string> _rename;

    public string? Dir => _dir;
    public string? StateFile => _stateFile;
    public string SessionId => _sessionId;
    public JsonObject Store => _store;
    public OrchestratorStateCollaborators Collaborators => _collaborators;

    public Func<string, string?, JsonObject> RowForDelegate => RowFor;

    private static JsonObject ObjectOrEmpty(JsonNode? node) =>
        node is JsonObject value ? (JsonObject)value.DeepClone() : new JsonObject();

    private static JsonArray ArrayOrEmpty(JsonNode? node) =>
        node is JsonArray value ? (JsonArray)value.DeepClone() : new JsonArray();

    private static string? StringOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool BoolOrFalse(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

    private static int? IntOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static IReadOnlyList<string>? StringList(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        return array.Select(StringOrNull).Where(value => value != null).Cast<string>().ToList();
    }

    private static IReadOnlyDictionary<string, string>? StringMap(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return obj
            .Select(pair => (pair.Key, Value: StringOrNull(pair.Value)))
            .Where(pair => pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
    }

    private static IReadOnlyDictionary<string, bool>? BoolMap(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return obj
            .Select(pair => (pair.Key, Value: pair.Value is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : (bool?)null))
            .Where(pair => pair.Value.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.Value);
    }

    private static InspectionFacts? FactsFromObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return new InspectionFacts(
            Git: BoolOrFalse(obj["git"]),
            LastCommit: StringOrNull(obj["lastCommit"]),
            LastSubject: StringOrNull(obj["lastSubject"]),
            Recent: StringList(obj["recent"]),
            Dirty: StringList(obj["dirty"]),
            Head: StringOrNull(obj["head"]),
            Branch: StringOrNull(obj["branch"]),
            FileCount: IntOrNull(obj["fileCount"]) ?? 0,
            Folder: StringOrNull(obj["folder"]),
            Error: StringOrNull(obj["error"]));
    }

    private static PendingAutoStart? PendingAutoStartFromObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return new PendingAutoStart(
            Timing: StringOrNull(obj["timing"]) ?? "discretion",
            DeadlineAt: StringOrNull(obj["deadlineAt"]),
            GrillContext: StringOrNull(obj["grillContext"]),
            GoldModel: ModelCandidateFromObject(obj["goldModel"] as JsonObject));
    }

    private static ModelCandidate? ModelCandidateFromObject(JsonObject? obj)
    {
        if (obj == null) return null;
        return new ModelCandidate(
            Id: StringOrNull(obj["id"]) ?? "",
            Label: StringOrNull(obj["label"]),
            Why: StringOrNull(obj["why"]),
            Provider: StringOrNull(obj["provider"]),
            Enabled: !obj.ContainsKey("enabled") || BoolOrFalse(obj["enabled"]),
            Available: !obj.ContainsKey("available") || BoolOrFalse(obj["available"]),
            ProviderEnabled: !obj.ContainsKey("providerEnabled") || BoolOrFalse(obj["providerEnabled"]),
            CanCode: !obj.ContainsKey("canCode") || BoolOrFalse(obj["canCode"]),
            CanVerify: !obj.ContainsKey("canVerify") || BoolOrFalse(obj["canVerify"]),
            Plan: StringOrNull(obj["plan"]),
            Role: StringOrNull(obj["role"]));
    }

    private static MegaTaskRecord? MegaTaskFromObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return new MegaTaskRecord(
            StringOrNull(obj["id"]) ?? "",
            StringOrNull(obj["projectId"]) ?? "",
            StringOrNull(obj["taskId"]) ?? "",
            StringOrNull(obj["title"]) ?? "",
            StringOrNull(obj["goal"]) ?? "",
            StringOrNull(obj["status"]) ?? "");
    }

    private static IReadOnlyList<SubTaskRecord>? SubTasksFromArray(JsonNode? node)
    {
        if (node is not JsonArray array) return null;
        return array.OfType<JsonObject>().Select(obj => new SubTaskRecord(
            StringOrNull(obj["id"]) ?? "",
            StringOrNull(obj["megaTaskId"]) ?? "",
            StringOrNull(obj["title"]) ?? "",
            StringOrNull(obj["detail"]) ?? "",
            StringOrNull(obj["status"]) ?? "")).ToList();
    }

    private static RepositoryInfo? RepositoryFromObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        return new RepositoryInfo(StringOrNull(obj["checkedAt"]), StringOrNull(obj["folder"]), StringOrNull(obj["github"]));
    }

    private static MonitoringMetrics? MonitoringFromObject(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        double? cpu = obj["cpuPercent"] is JsonValue cpuValue && cpuValue.TryGetValue<double>(out var cpuValueResult) ? cpuValueResult : null;
        long? memory = obj["memoryBytes"] is JsonValue memoryValue && memoryValue.TryGetValue<long>(out var memoryValueResult) ? memoryValueResult : null;
        return new MonitoringMetrics(cpu, memory);
    }

    public OrchestratorState(string? dir, JsonObject store, OrchestratorStateCollaborators? collaborators = null)
    {
        _dir = dir;
        _stateFile = !string.IsNullOrWhiteSpace(dir) ? Path.Combine(dir, "orchestrator.json") : null;
        _collaborators = collaborators ?? new OrchestratorStateCollaborators();
        _store = store;
        _sessionId = _collaborators.SessionId ?? Guid.NewGuid().ToString();
        _now = _collaborators.Now ?? (() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        _rename = _collaborators.Rename ?? ((source, target) => File.Move(source, target, overwrite: true));
    }

    public static OrchestratorState Load(string? dir, OrchestratorStateCollaborators? collaborators = null)
    {
        collaborators ??= new OrchestratorStateCollaborators();
        var nowFunc = collaborators.Now ?? (() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        var sessionId = collaborators.SessionId ?? Guid.NewGuid().ToString();
        var stateFile = !string.IsNullOrWhiteSpace(dir) ? Path.Combine(dir, "orchestrator.json") : null;

        var store = new JsonObject
        {
            ["autonomy"] = new JsonObject(),
            ["chatModes"] = new JsonObject(),
            ["savingModes"] = new JsonObject(),
            ["modelWeights"] = new JsonObject(),
            ["mode"] = "manual",
            ["grillPropose"] = false,
            ["grillKind"] = "off",
            ["usage"] = new JsonObject(),
            ["enabled"] = new JsonObject(),
            ["providerEnabled"] = new JsonObject(),
            ["catalogVisibility"] = new JsonObject(),
            ["modelPolicies"] = new JsonObject(),
            ["providerPolicies"] = new JsonObject(),
            ["modelRoles"] = new JsonObject(),
            ["accountModelRoles"] = new JsonObject(),
            ["projectTeams"] = new JsonObject(),
            ["accountBindings"] = new JsonObject(),
            ["lastAccountKeys"] = new JsonObject(),
            ["briefingBaselines"] = new JsonObject(),
            ["projects"] = new JsonObject(),
            ["asks"] = new JsonArray(),
            ["researches"] = new JsonArray(),
            ["workerHistory"] = new JsonArray(),
            ["archivedProjects"] = new JsonObject(),
            ["projectArchive"] = new JsonArray(),
            ["projectTools"] = new JsonObject(),
            ["orchestratorId"] = null,
            ["selectedProjectId"] = null,
            ["sessions"] = new JsonArray(),
            ["goldModelId"] = null,
            ["pullRequestFixes"] = new JsonArray()
        };

        if (stateFile != null && File.Exists(stateFile))
        {
            try
            {
                var content = File.ReadAllText(stateFile, Encoding.UTF8);
                if (JsonNode.Parse(content) is JsonObject parsed)
                {
                    // Copy all fields from parsed to preserve unknown and legacy fields
                    foreach (var (key, value) in parsed)
                    {
                        store[key] = value?.DeepClone();
                    }

                    // Normalize and repair known fields to match JS loadState. Read legacy values
                    // defensively: one malformed field must not prevent unrelated evidence loading.
                    store["autonomy"] = ObjectOrEmpty(parsed["autonomy"]);
                    store["chatModes"] = ObjectOrEmpty(parsed["chatModes"]);
                    store["savingModes"] = ObjectOrEmpty(parsed["savingModes"]);
                    store["modelWeights"] = ObjectOrEmpty(parsed["modelWeights"]);
                    store["mode"] = StringOrNull(parsed["mode"]) == "auto" ? "auto" : "manual";
                    store["grillPropose"] = false;
                    store["grillKind"] = "off";
                    store["usage"] = ObjectOrEmpty(parsed["usage"]);
                    store["enabled"] = ObjectOrEmpty(parsed["enabled"]);
                    store["providerEnabled"] = ObjectOrEmpty(parsed["providerEnabled"]);
                    store["catalogVisibility"] = ObjectOrEmpty(parsed["catalogVisibility"]);
                    store["modelPolicies"] = ObjectOrEmpty(parsed["modelPolicies"]);
                    store["providerPolicies"] = ObjectOrEmpty(parsed["providerPolicies"]);
                    store["modelRoles"] = ObjectOrEmpty(parsed["modelRoles"]);
                    store["accountModelRoles"] = ObjectOrEmpty(parsed["accountModelRoles"]);
                    store["projectTeams"] = ObjectOrEmpty(parsed["projectTeams"]);
                    store["accountBindings"] = ObjectOrEmpty(parsed["accountBindings"]);
                    store["lastAccountKeys"] = ObjectOrEmpty(parsed["lastAccountKeys"]);
                    store["briefingBaselines"] = ObjectOrEmpty(parsed["briefingBaselines"]);
                    store["asks"] = ArrayOrEmpty(parsed["asks"]);
                    store["researches"] = ArrayOrEmpty(parsed["researches"]);
                    store["workerHistory"] = ArrayOrEmpty(parsed["workerHistory"]);
                    store["archivedProjects"] = ObjectOrEmpty(parsed["archivedProjects"]);
                    store["projectArchive"] = ArrayOrEmpty(parsed["projectArchive"]);
                    store["projectTools"] = ObjectOrEmpty(parsed["projectTools"]);
                    store["pullRequestFixes"] = ArrayOrEmpty(parsed["pullRequestFixes"]);
                    store["selectedProjectId"] = StringOrNull(parsed["selectedProjectId"]);
                    store["sessions"] = ArrayOrEmpty(parsed["sessions"]);
                    store["goldModelId"] = StringOrNull(parsed["goldModelId"]);
                    store["orchestratorId"] = StringOrNull(parsed["orchestratorId"]);

                    if (parsed["projects"] is JsonObject projObj)
                    {
                        var mergedProjects = new JsonObject();
                        foreach (var (pId, pRowNode) in projObj)
                        {
                            var empty = EmptyEntryObject();
                            if (pRowNode is JsonObject pRow)
                            {
                                foreach (var (propKey, propVal) in pRow)
                                {
                                    empty[propKey] = propVal?.DeepClone();
                                }
                            }
                            mergedProjects[pId] = empty;
                        }
                        store["projects"] = mergedProjects;
                    }
                    else
                    {
                        store["projects"] = new JsonObject();
                    }
                }
            }
            catch
            {
                // Unreadable or corrupt store file starts with clean defaults
            }
        }

        // Autonomy authority is session-scoped. A restart may display the saved
        // settings, but it must never resume an older autonomous run silently.
        if (store["autonomy"] is JsonObject autonomy)
        {
            foreach (var settings in autonomy.Select(pair => pair.Value).OfType<JsonObject>())
            {
                if (BoolOrFalse(settings["enabled"]) && StringOrNull(settings["sessionId"]) != sessionId)
                {
                    settings["enabled"] = false;
                }
            }
        }

        // Sessions registry setup
        var sessionsArr = store["sessions"] as JsonArray ?? new JsonArray();
        var knownSessions = new Dictionary<string, JsonObject>();
        foreach (var s in sessionsArr.OfType<JsonObject>())
        {
            var sId = StringOrNull(s["id"]);
            if (!string.IsNullOrEmpty(sId)) knownSessions[sId] = s;
        }

        var historicalSessions = new List<JsonObject>();
        if (store["asks"] is JsonArray aArr) historicalSessions.AddRange(aArr.OfType<JsonObject>());
        if (store["researches"] is JsonArray rArr) historicalSessions.AddRange(rArr.OfType<JsonObject>());
        if (store["projects"] is JsonObject prjObj)
        {
            foreach (var (_, rowNode) in prjObj)
            {
                if (rowNode is JsonObject r && r["dispatch"] is JsonArray dArr)
                {
                    historicalSessions.AddRange(dArr.OfType<JsonObject>());
                }
            }
        }
        if (store["workerHistory"] is JsonArray wArr)
        {
            foreach (var item in wArr.OfType<JsonObject>())
            {
                if (item["dispatch"] is JsonArray dArr)
                {
                    historicalSessions.AddRange(dArr.OfType<JsonObject>());
                }
            }
        }

        foreach (var rec in historicalSessions)
        {
            var sId = StringOrNull(rec["sessionId"]);
            if (!string.IsNullOrEmpty(sId) && !knownSessions.ContainsKey(sId))
            {
                var startedAt = StringOrNull(rec["startedAt"]) ?? StringOrNull(rec["at"]);
                knownSessions[sId] = new JsonObject
                {
                    ["id"] = sId,
                    ["startedAt"] = startedAt
                };
            }
        }

        var finalSessions = new JsonArray();
        foreach (var (_, sObj) in knownSessions) finalSessions.Add((JsonNode)sObj.DeepClone());
        finalSessions.Add((JsonNode)new JsonObject
        {
            ["id"] = sessionId,
            ["startedAt"] = nowFunc()
        });
        store["sessions"] = finalSessions;

        // Resolve the session once. The constructor must expose the same authority
        // that was registered above, including when the caller omitted SessionId.
        var resolvedCollaborators = collaborators with { SessionId = sessionId };
        var state = new OrchestratorState(dir, store, resolvedCollaborators);
        state.HydrateCheckpoints();
        return state;
    }

    public void Persist()
    {
        if (string.IsNullOrWhiteSpace(_stateFile)) return;
        var dir = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temporary = _stateFile + "." + Guid.NewGuid().ToString() + ".tmp";
        var json = _store.ToJsonString(JsonSerializationOptions) + "\n";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            AtomicRename.RenameWithRetry(temporary, _stateFile, rename: _rename);
        }
        catch
        {
            // AtomicRename also cleans up replacement failures; this covers a
            // write/flush failure before it can reach the rename helper.
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    public JsonObject EntryRow(string projectId)
    {
        if (_store["projects"] is not JsonObject projects)
        {
            projects = new JsonObject();
            _store["projects"] = projects;
        }
        if (projects[projectId] is not JsonObject row)
        {
            row = EmptyEntryObject();
            projects[projectId] = row;
        }
        return row;
    }

    public ProjectEntry Entry(string projectId) => ProjectEntryFromObject(EntryRow(projectId));

    public ProjectEntry? TryGetEntry(string projectId)
    {
        if (_store["projects"] is JsonObject projects && projects[projectId] is JsonObject row)
        {
            return ProjectEntryFromObject(row);
        }
        return null;
    }

    public JsonObject RowFor(string projectId, string? megaTaskId = null)
    {
        if (!string.IsNullOrEmpty(megaTaskId) && _store["researches"] is JsonArray researches)
        {
            var record = researches.OfType<JsonObject>().FirstOrDefault(r => StringOrNull(r["megaTaskId"]) == megaTaskId);
            if (record != null) return record;
        }
        return EntryRow(projectId);
    }

    public void HydrateCheckpoints()
    {
        if (string.IsNullOrWhiteSpace(_dir)) return;
        var checkpointsFolder = Path.Combine(_dir, "checkpoints");
        if (!Directory.Exists(checkpointsFolder)) return;

        var checkpointFiles = Directory.GetFiles(checkpointsFolder, "*.json");
        var ids = new HashSet<string>();
        if (_store["projects"] is JsonObject projectsObj)
        {
            foreach (var (key, _) in projectsObj) ids.Add(key);
        }
        foreach (var file in checkpointFiles)
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                ids.Add(name[..^5]);
            }
        }

        var restored = false;
        foreach (var id in ids)
        {
            var row = EntryRow(id);
            var disk = ReadCheckpointFile(id);
            if (row["checkpoint"] == null && disk != null)
            {
                row["checkpoint"] = disk.DeepClone();
                restored = true;
            }
            else if (row["checkpoint"] is JsonObject cp && disk != null && string.IsNullOrEmpty(StringOrNull(cp["path"])))
            {
                cp["path"] = StringOrNull(disk["path"]);
                restored = true;
            }

            var resumeRequested = BoolOrFalse(row["resumeRequested"]);
            var status = StringOrNull(row["status"]) ?? "idle";
            if (row["checkpoint"] is JsonObject && !resumeRequested && status != "watching" && status != "complete" && status != "needs_approval")
            {
                var title = StringOrNull(row["checkpoint"]?["jobTitle"]);
                PaintCheckpoint(row, id, title);
                restored = true;
            }
        }

        if (restored) Persist();
    }

    private JsonObject? ReadCheckpointFile(string id)
    {
        if (string.IsNullOrWhiteSpace(_dir)) return null;
        var file = Path.Combine(_dir, "checkpoints", $"{id}.json");
        if (!File.Exists(file)) return null;
        try
        {
            var content = File.ReadAllText(file, Encoding.UTF8);
            var parsed = JsonNode.Parse(content) as JsonObject;
            if (parsed == null) return null;
            parsed["path"] = file;
            if (string.IsNullOrEmpty(StringOrNull(parsed["projectId"])))
            {
                parsed["projectId"] = id;
            }
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static void PaintCheckpoint(JsonObject row, string name, string? title)
    {
            var reason = StringOrNull(row["checkpoint"]?["reason"]);
        var talk = OrchestratorOrderParsing.CheckpointTalk(reason);
            var jobTitle = title ?? StringOrNull(row["checkpoint"]?["jobTitle"]) ?? StringOrNull(row["jobTitle"]) ?? "job";

        row["status"] = "checkpointed";
        row["doing"] = talk.FormatDoing(name ?? "project", jobTitle);
        row["waitingFor"] = "Resume from checkpoint";
        row["next"] = "Press Resume to continue from the checkpoint.";
        row["orchestratorDoing"] = talk.Orch;
        row["workerAlive"] = false;
        row["workerActivity"] = talk.Activity;
        row["workerLabel"] = null;
        row["watchingSince"] = null;
    }

    public static JsonObject EmptyEntryObject()
    {
        return new JsonObject
        {
            ["status"] = "idle",
            ["doing"] = "",
            ["plan"] = new JsonArray(),
            ["facts"] = null,
            ["jobId"] = null,
            ["jobTitle"] = null,
            ["attemptId"] = null,
            ["modelId"] = null,
            ["approved"] = false,
            ["waitingFor"] = "",
            ["next"] = "",
            ["workerLabel"] = null,
            ["resultPath"] = null,
            ["watchingSince"] = null,
            ["holdApproval"] = false,
            ["grillSkipped"] = false,
            ["docs"] = new JsonArray(),
            ["log"] = new JsonArray(),
            ["pendingAutoStart"] = null,
            ["checkpoint"] = null,
            ["dispatch"] = new JsonArray(),
            ["resumeRequested"] = false,
            ["deadlineHonored"] = false,
            ["workerPid"] = null,
            ["workerLog"] = null,
            ["workerAlive"] = false,
            ["tried"] = new JsonArray(),
            ["progressAt"] = null,
            ["progressKey"] = null,
            ["workerTail"] = "",
            ["workerActivity"] = ""
        };
    }

    public static ProjectEntry EmptyEntry() => ProjectEntryFromObject(EmptyEntryObject());

    public static string StepOf(ProjectEntry? row)
    {
        if (row == null) return "idle";
        return row.Status switch
        {
            "blocked_no_folder" => "check",
            "propose_grill" or "waiting_on_you" => "grill",
            "idle" => row.PendingAutoStart != null ? "plan" : "idle",
            "needs_approval" => "approve",
            "blocked_no_worker" => "assign",
            "watching" or "checkpointed" or "failed" => "watch",
            "complete" => "done",
            _ => !string.IsNullOrEmpty(row.Status) ? row.Status : "idle"
        };
    }

    public static string StepOf(JsonObject? row)
    {
        if (row == null) return "idle";
        var status = StringOrNull(row["status"]) ?? "idle";
        var pendingAutoStart = row["pendingAutoStart"] != null;
        return status switch
        {
            "blocked_no_folder" => "check",
            "propose_grill" or "waiting_on_you" => "grill",
            "idle" => pendingAutoStart ? "plan" : "idle",
            "needs_approval" => "approve",
            "blocked_no_worker" => "assign",
            "watching" or "checkpointed" or "failed" => "watch",
            "complete" => "done",
            _ => !string.IsNullOrEmpty(status) ? status : "idle"
        };
    }

    public static IReadOnlyList<PipelineStepState> PipelineOf(ProjectEntry? row)
    {
        var step = StepOf(row);
        var order = PipelineSteps.Select(s => s.Id).ToArray();
        var nowIndex = step == "done" ? order.Length : Array.IndexOf(order, step);

        var list = new List<PipelineStepState>(PipelineSteps.Length);
        for (var i = 0; i < PipelineSteps.Length; i++)
        {
            var (id, label) = PipelineSteps[i];
            var state = "todo";
            if (step == "idle") state = "todo";
            else if (step == "done" || (nowIndex >= 0 && i < nowIndex)) state = "done";
            else if (i == nowIndex) state = "now";

            if (id == "grill" && row != null && row.GrillSkipped && state != "now")
            {
                state = nowIndex > i ? "skip" : "todo";
            }
            list.Add(new PipelineStepState(id, label, state));
        }
        return list;
    }

    public static IReadOnlyList<PipelineStepState> PipelineOf(JsonObject? row)
    {
        var step = StepOf(row);
        var order = PipelineSteps.Select(s => s.Id).ToArray();
        var nowIndex = step == "done" ? order.Length : Array.IndexOf(order, step);
        var grillSkipped = BoolOrFalse(row?["grillSkipped"]);

        var list = new List<PipelineStepState>(PipelineSteps.Length);
        for (var i = 0; i < PipelineSteps.Length; i++)
        {
            var (id, label) = PipelineSteps[i];
            var state = "todo";
            if (step == "idle") state = "todo";
            else if (step == "done" || (nowIndex >= 0 && i < nowIndex)) state = "done";
            else if (i == nowIndex) state = "now";

            if (id == "grill" && grillSkipped && state != "now")
            {
                state = nowIndex > i ? "skip" : "todo";
            }
            list.Add(new PipelineStepState(id, label, state));
        }
        return list;
    }

    public static string OrchestratorDoing(ProjectEntry? row)
    {
        if (row == null) return "Idle.";
        var n = row.Dispatch.Count > 0 ? row.Dispatch.Count : (!string.IsNullOrEmpty(row.WorkerLabel) ? 1 : 0);
        if (row.Status == "watching")
        {
            var alive = row.Dispatch.Count > 0
                ? row.Dispatch.Count(p => p.Pid != null)
                : (row.WorkerAlive ? 1 : 0);
            var files = row.Facts?.FileCount;
            var folder = files == null ? "project folder" : $"{files} files in the project folder";
            if (row.WorkerActivity.Contains("Hit an error", StringComparison.OrdinalIgnoreCase))
            {
                return "Worker looks unhealthy. Monitoring for recovery; I will switch models if it stalls or dies.";
            }
            return $"Monitoring {n} worker{(n == 1 ? "" : "s")} ({alive} alive). Watching process, worker log, and {folder}.";
        }
        if (row.Status == "needs_approval")
        {
            return n > 1
                ? $"Split the job into {n} slices across available workers. Waiting for Approve."
                : "Picked a worker. Waiting for Approve.";
        }
        if (row.Status is "propose_grill" or "waiting_on_you") return "Holding assign until grill is closed.";
        if (row.Status == "complete") return "Verified STATUS: COMPLETE and folder output.";
        if (row.Status == "failed") return "Stopped monitoring. No remaining worker, or every slice failed.";
        if (row.Status == "checkpointed")
        {
            return OrchestratorOrderParsing.CheckpointTalk(null).Orch;
        }
        if (row.Status == "blocked_no_folder") return "Cannot inspect: no local folder.";
        if (row.Status == "blocked_no_worker") return "No eligible worker. Turn a model on, or wait until its provider usage resets.";
        if (row.Status == "idle") return "Reading the notebook. No job to split.";
        return StatusLabel(row.Status);
    }

    public static string OrchestratorDoing(JsonObject? row)
    {
        if (row == null) return "Idle.";
        var dispatchArr = row["dispatch"] as JsonArray;
        var dispatchCount = dispatchArr?.Count ?? 0;
        var workerLabel = row["workerLabel"]?.GetValue<string>();
        var n = dispatchCount > 0 ? dispatchCount : (!string.IsNullOrEmpty(workerLabel) ? 1 : 0);
        var status = StringOrNull(row["status"]) ?? "idle";
        var workerActivity = row["workerActivity"]?.GetValue<string>() ?? "";
        var workerAlive = row["workerAlive"]?.GetValue<bool>() ?? false;

        if (status == "watching")
        {
            var alive = dispatchArr is not null
                ? dispatchArr!.OfType<JsonObject>().Count(p => p["alive"]?.GetValue<bool>() ?? false)
                : (workerAlive ? 1 : 0);
            var factsObj = row["facts"] as JsonObject;
            int? files = factsObj?["fileCount"]?.GetValue<int?>();
            var folder = files == null ? "project folder" : $"{files} files in the project folder";
            if (workerActivity.Contains("Hit an error", StringComparison.OrdinalIgnoreCase))
            {
                return "Worker looks unhealthy. Monitoring for recovery; I will switch models if it stalls or dies.";
            }
            return $"Monitoring {n} worker{(n == 1 ? "" : "s")} ({alive} alive). Watching process, worker log, and {folder}.";
        }
        if (status == "needs_approval")
        {
            return n > 1
                ? $"Split the job into {n} slices across available workers. Waiting for Approve."
                : "Picked a worker. Waiting for Approve.";
        }
        if (status is "propose_grill" or "waiting_on_you") return "Holding assign until grill is closed.";
        if (status == "complete") return "Verified STATUS: COMPLETE and folder output.";
        if (status == "failed") return "Stopped monitoring. No remaining worker, or every slice failed.";
        if (status == "checkpointed")
        {
            var reason = StringOrNull((row["checkpoint"] as JsonObject)?["reason"]);
            return OrchestratorOrderParsing.CheckpointTalk(reason).Orch;
        }
        if (status == "blocked_no_folder") return "Cannot inspect: no local folder.";
        if (status == "blocked_no_worker") return "No eligible worker. Turn a model on, or wait until its provider usage resets.";
        if (status == "idle") return "Reading the notebook. No job to split.";
        return StatusLabel(status);
    }

    private static string StatusLabel(string? status) => status switch
    {
        "waiting_on_you" => "Waiting on you",
        "propose_grill" => "Grill first",
        "idle" => "Idle",
        "blocked_no_folder" => "Needs a folder",
        "blocked_no_worker" => "No eligible worker",
        "needs_approval" => "Plan ready",
        "watching" => "Watching",
        "complete" => "Evidence complete",
        "failed" => "Stopped",
        "checkpointed" => "Paused",
        _ => "Idle."
    };

    public static IReadOnlyList<CrewMember> CrewOf(ProjectEntry? row, string? leadLabel, ExecutionState? execution = null)
    {
        var crew = new List<CrewMember>
        {
            new("Orchestrator", leadLabel ?? OrchestratorDefaultName, OrchestratorDoing(row))
        };

        var liveWorkers = row != null && row.Status != "idle" && row.Status != "propose_grill" && row.Status != "waiting_on_you" && row.Status != "blocked_no_folder";
        var dispatch = liveWorkers && row?.Dispatch != null && row.Dispatch.Count > 0 ? row.Dispatch : [];

        if (dispatch.Count > 0)
        {
            for (var index = 0; index < dispatch.Count; index++)
            {
                var part = dispatch[index];
                var executionSubTask = execution?.SubTasks != null && index < execution.SubTasks.Count
                    ? execution.SubTasks[index]
                    : null;
                var role = executionSubTask != null && ModelRoles.AssignmentRole(executionSubTask) == "reviewer"
                    || part.Slice != null && (part.Slice.Contains("review", StringComparison.OrdinalIgnoreCase) || part.Slice.Contains("audit", StringComparison.OrdinalIgnoreCase))
                    ? "Reviewer"
                    : "Worker";

                var doing = !string.IsNullOrEmpty(part.Slice) ? part.Slice : "On the plan";
                if (row!.Status == "needs_approval") doing = $"{(!string.IsNullOrEmpty(part.Slice) ? part.Slice : "Whole job")}. Waiting for Approve.";
                else if (row.Status == "watching")
                {
                    doing = !string.IsNullOrEmpty(row.WorkerActivity)
                        ? row.WorkerActivity
                        : $"{(!string.IsNullOrEmpty(part.Slice) ? part.Slice : "Whole job")}. {(part.Pid != null ? $"Process {part.Pid} running." : "Claimed.")}";
                }
                else if (row.Status == "complete") doing = "Evidence complete.";
                else if (row.Status == "checkpointed")
                {
                    doing = OrchestratorOrderParsing.CheckpointTalk(null).Orch;
                }
                else if (row.Status == "blocked_no_worker") doing = "No remaining provider usage.";

                crew.Add(new CrewMember(role, !string.IsNullOrEmpty(part.Label) ? part.Label : part.ModelId, doing));
            }
        }
        else if (row != null && !string.IsNullOrEmpty(row.WorkerLabel) && !row.WorkerLabel.Contains(" + "))
        {
            var doing = row.Status == "watching"
                ? (!string.IsNullOrEmpty(row.WorkerActivity)
                    ? row.WorkerActivity
                    : (row.WorkerPid != null ? $"Process {row.WorkerPid}{(row.WorkerAlive ? " running" : " stopped")}." : "Claimed."))
                : (row.Status == "needs_approval" ? "Picked. Waiting for Approve." : (row.WaitingFor ?? ""));
            crew.Add(new CrewMember("Worker", row.WorkerLabel, doing));
        }
        else if (row == null || string.IsNullOrEmpty(row.WorkerLabel))
        {
            var doing = row != null && (row.Status is "propose_grill" or "waiting_on_you")
                ? "Not assigned until grill is closed."
                : "Not assigned yet.";
            crew.Add(new CrewMember("Worker", "None yet", doing));
        }

        return crew;
    }

    public static IReadOnlyList<CrewMember> CrewOf(JsonObject? row, string? leadLabel, JsonObject? execution = null)
    {
        var rowOrchDoing = row?["orchestratorDoing"]?.GetValue<string>();
        var rowOrchLabel = row?["orchestratorLabel"]?.GetValue<string>();
        var crew = new List<CrewMember>
        {
            new("Orchestrator", leadLabel ?? rowOrchLabel ?? OrchestratorDefaultName, rowOrchDoing ?? OrchestratorDoing(row))
        };

        var status = row?["status"]?.GetValue<string>() ?? "idle";
        var liveWorkers = row != null && status != "idle" && status != "propose_grill" && status != "waiting_on_you" && status != "blocked_no_folder";
        var dispatchArr = liveWorkers ? row?["dispatch"] as JsonArray : null;

        if (dispatchArr != null && dispatchArr.Count > 0)
        {
            var subTasksArr = (execution?["subTasks"] as JsonArray) ?? (row?["subTasks"] as JsonArray) ?? [];
            foreach (var partNode in dispatchArr.OfType<JsonObject>())
            {
                var subTaskId = partNode["subTaskId"]?.GetValue<string>();
                var partMegaTaskId = partNode["megaTaskId"]?.GetValue<string>();
                var subObj = subTasksArr.OfType<JsonObject>().FirstOrDefault(s =>
                    s["id"]?.GetValue<string>() == subTaskId && (string.IsNullOrEmpty(partMegaTaskId) || s["megaTaskId"]?.GetValue<string>() == partMegaTaskId));

                var retainedRole = partNode["activitySession"]?["role"]?.GetValue<string>();
                var role = retainedRole == "reviewer" || (retainedRole == null && ModelRoles.AssignmentRole(subObj ?? partNode) == "reviewer")
                    ? "Reviewer"
                    : "Worker";

                var slice = partNode["slice"]?.GetValue<string>();
                var doing = !string.IsNullOrEmpty(slice) ? slice : "On the plan";
                var historical = partNode["historicalAssignment"]?.GetValue<bool>() ?? false;
                var label = partNode["label"]?.GetValue<string>() ?? partNode["modelId"]?.GetValue<string>() ?? "";

                if (historical)
                {
                    var histTitle = partNode["assignmentJobTitle"]?.GetValue<string>() ?? "Historical assignment";
                    var histAct = partNode["activity"]?.GetValue<string>() ?? "Original activity is not retained.";
                    crew.Add(new CrewMember(role, label, $"{histTitle}: {histAct}"));
                    continue;
                }

                if (status == "needs_approval") doing = $"{(!string.IsNullOrEmpty(slice) ? slice : "Whole job")}. Waiting for Approve.";
                else if (status == "watching")
                {
                    var partAct = partNode["activity"]?.GetValue<string>();
                    var rowAct = row?["workerActivity"]?.GetValue<string>();
                    var pid = partNode["pid"]?.GetValue<int?>();
                    var alive = partNode["alive"]?.GetValue<bool>() ?? false;
                    doing = !string.IsNullOrEmpty(partAct) ? partAct : (!string.IsNullOrEmpty(rowAct) ? rowAct : $"{(!string.IsNullOrEmpty(slice) ? slice : "Whole job")}. {(pid != null ? $"Process {pid}{(alive ? " running" : " stopped")}." : "Claimed.")}");
                }
                else if (status == "complete") doing = "Evidence complete.";
                else if (status == "checkpointed")
                {
                    var reason = StringOrNull((row?["checkpoint"] as JsonObject)?["reason"]);
                    doing = OrchestratorOrderParsing.CheckpointTalk(reason).Orch;
                }
                else if (status == "blocked_no_worker") doing = "No remaining provider usage.";

                crew.Add(new CrewMember(role, label, doing));
            }
        }
        else
        {
            var workerLabel = row?["workerLabel"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(workerLabel) && !workerLabel.Contains(" + "))
            {
                var workerActivity = row?["workerActivity"]?.GetValue<string>();
                var workerPid = row?["workerPid"]?.GetValue<int?>();
                var workerAlive = row?["workerAlive"]?.GetValue<bool>() ?? false;
                var waitingFor = row?["waitingFor"]?.GetValue<string>() ?? "";
                var doing = status == "watching"
                    ? (!string.IsNullOrEmpty(workerActivity)
                        ? workerActivity
                        : (workerPid != null ? $"Process {workerPid}{(workerAlive ? " running" : " stopped")}." : "Claimed."))
                    : (status == "needs_approval" ? "Picked. Waiting for Approve." : waitingFor);
                crew.Add(new CrewMember("Worker", workerLabel, doing));
            }
            else if (string.IsNullOrEmpty(workerLabel))
            {
                var doing = row != null && (status is "propose_grill" or "waiting_on_you")
                    ? "Not assigned until grill is closed."
                    : "Not assigned yet.";
                crew.Add(new CrewMember("Worker", "None yet", doing));
            }
        }

        return crew;
    }

    public static string LogStat(string? file, Func<string?, string>? hook = null)
    {
        if (hook != null) return hook(file);
        if (string.IsNullOrEmpty(file)) return "0|0";
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return "0|0";
            var mtimeMs = (long)(fi.LastWriteTimeUtc - DateTime.UnixEpoch).TotalMilliseconds;
            return $"{fi.Length}|{mtimeMs}";
        }
        catch
        {
            return "0|0";
        }
    }

    public static string ProgressKey(InspectionFacts? facts, string? logPath, IReadOnlyList<DispatchPart>? dispatch, Func<string?, string>? logStatHook = null)
    {
        var fileCount = facts?.FileCount ?? 0;
        var dirty = facts?.Dirty?.Count ?? 0;
        return ProgressKeyCore(fileCount, "", dirty, logPath, dispatch?.Select(d => d.LogPath), logStatHook);
    }

    public static string ProgressKey(JsonObject? facts, string? logPath, IReadOnlyList<DispatchPart>? dispatch, Func<string?, string>? logStatHook = null)
    {
        var fileCount = IntOrNull(facts?["fileCount"]) ?? 0;
        var names = (facts?["names"] as JsonArray)?.Select(StringOrNull).Where(s => !string.IsNullOrEmpty(s)).Cast<string>() ?? [];
        var namesStr = string.Join(",", names);
        var dirty = (facts?["dirty"] as JsonArray)?.Count ?? 0;
        return ProgressKeyCore(fileCount, namesStr, dirty, logPath, dispatch?.Select(d => d.LogPath), logStatHook);
    }

    public static string ProgressKey(JsonObject? facts, string? logPath, JsonArray? dispatch, Func<string?, string>? logStatHook = null)
    {
        var fileCount = IntOrNull(facts?["fileCount"]) ?? 0;
        var names = (facts?["names"] as JsonArray)?.Select(StringOrNull).Where(s => !string.IsNullOrEmpty(s)).Cast<string>() ?? [];
        var namesStr = string.Join(",", names);
        var dirty = (facts?["dirty"] as JsonArray)?.Count ?? 0;
        var dispatchLogs = dispatch?.OfType<JsonObject>().Select(d => StringOrNull(d["logPath"]));
        return ProgressKeyCore(fileCount, namesStr, dirty, logPath, dispatchLogs, logStatHook);
    }

    private static string ProgressKeyCore(int fileCount, string names, int dirty, string? logPath, IEnumerable<string?>? dispatchLogs, Func<string?, string>? logStatHook)
    {
        var logsList = dispatchLogs?.ToList();
        var logs = logsList != null && logsList.Count > 0
            ? string.Join(";", logsList.Select(p => LogStat(p, logStatHook)))
            : LogStat(logPath, logStatHook);
        return $"{fileCount}|{names}|{dirty}|{logs}";
    }

    public static InspectionFacts DefaultInspect(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new InspectionFacts(Error: "no folder");
        try
        {
            var linked = ProjectWorkspace.LinkedWorkspace(folder);
            if (linked is null) return new InspectionFacts(Folder: folder, Error: "folder not found");
            var allNames = Directory.EnumerateFileSystemEntries(linked)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToArray();
            var names = allNames.Take(24).ToArray();
            var git = ProjectWorkspace.SafeGitWorkspace(linked) is not null &&
                string.Equals(RunGit(linked, "rev-parse", "--show-toplevel"), linked, StringComparison.OrdinalIgnoreCase);
            var head = git ? ReadGitHead(linked) : "";
            var branch = git ? RunGit(linked, "rev-parse", "--abbrev-ref", "HEAD") : "";
            var lastCommit = git ? RunGit(linked, "log", "-1", "--format=%h %s") : "";
            var lastSubject = git ? RunGit(linked, "log", "-1", "--format=%s") : "";
            var recent = git ? Lines(RunGit(linked, "log", "-5", "--oneline"), 5) : [];
            var dirty = git ? Lines(RunGit(linked, "status", "--ignore-submodules=all", "--porcelain", "--no-renames"), 20) : [];
            var diffStat = git ? Clip(RunGit(linked, "diff", "--ignore-submodules=all", "--submodule=short", "--no-ext-diff", "--no-textconv", "--no-renames", "--stat", "HEAD"), 400) : "";
            return new InspectionFacts(Git: git, LastCommit: lastCommit, LastSubject: lastSubject,
                Recent: recent, Dirty: dirty, Head: head, Branch: branch, FileCount: allNames.Length, Folder: linked);
        }
        catch
        {
            return new InspectionFacts(Folder: folder, Error: "folder not found");
        }
    }

    private static string ReadGitHead(string folder)
    {
        try { return File.ReadAllText(Path.Combine(folder, ".git", "HEAD"), Encoding.UTF8).Trim(); }
        catch { return ""; }
    }

    private static string[] Lines(string value, int limit) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(limit).ToArray();

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    private static string RunGit(string folder, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = folder,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--no-optional-locks");
            foreach (var arg in args) start.ArgumentList.Add(arg);
            foreach (var pair in ProjectWorkspace.GitEnvironment()) start.Environment[pair.Key] = pair.Value;
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(4000) || process.ExitCode != 0) return "";
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch { return ""; }
    }

    public static ProjectEntry ProjectEntryFromObject(JsonObject obj)
    {
        return new ProjectEntry(
            Status: StringOrNull(obj["status"]) ?? "idle",
            Doing: StringOrNull(obj["doing"]) ?? "",
            Plan: StringList(obj["plan"]) ?? [],
            Facts: FactsFromObject(obj["facts"]),
            JobId: StringOrNull(obj["jobId"]),
            JobTitle: StringOrNull(obj["jobTitle"]),
            AttemptId: StringOrNull(obj["attemptId"]),
            ModelId: StringOrNull(obj["modelId"]),
            Approved: BoolOrFalse(obj["approved"]),
            WaitingFor: StringOrNull(obj["waitingFor"]) ?? "",
            Next: StringOrNull(obj["next"]) ?? "",
            WorkerLabel: StringOrNull(obj["workerLabel"]),
            ResultPath: StringOrNull(obj["resultPath"]),
            WatchingSince: StringOrNull(obj["watchingSince"]),
            HoldApproval: BoolOrFalse(obj["holdApproval"]),
            GrillSkipped: BoolOrFalse(obj["grillSkipped"]),
            Docs: (StringList(obj["docs"]) ?? []).Select(path => new DocInfo(path)).ToList(),
            Log: (obj["log"] as JsonArray)?.Select(n =>
            {
                if (n is JsonObject logObj)
                {
                    return new ProjectLogLine(StringOrNull(logObj["at"]) ?? "", StringOrNull(logObj["text"]) ?? "");
                }
                return new ProjectLogLine("", StringOrNull(n) ?? "");
            }).ToList() ?? [],
            PendingAutoStart: PendingAutoStartFromObject(obj["pendingAutoStart"]),
            Checkpoint: obj["checkpoint"] is JsonObject cpObj ? new CheckpointRecord(StringOrNull(cpObj["path"])) : null,
            Dispatch: (obj["dispatch"] as JsonArray)?.OfType<JsonObject>().Select(d => new DispatchPart(
                ModelId: StringOrNull(d["modelId"]) ?? "",
                Label: StringOrNull(d["label"]) ?? "",
                Slice: StringOrNull(d["slice"]) ?? "",
                Why: StringOrNull(d["why"]) ?? "",
                Pid: IntOrNull(d["pid"]),
                LogPath: StringOrNull(d["logPath"]),
                ResultPath: StringOrNull(d["resultPath"]),
                StoppedByUser: BoolOrFalse(d["stoppedByUser"])
            )).ToList() ?? [],
            ResumeRequested: BoolOrFalse(obj["resumeRequested"]),
            DeadlineHonored: BoolOrFalse(obj["deadlineHonored"]),
            WorkerPid: IntOrNull(obj["workerPid"]),
            WorkerLog: StringOrNull(obj["workerLog"]),
            WorkerAlive: BoolOrFalse(obj["workerAlive"]),
            Tried: StringList(obj["tried"]) ?? [],
            ProgressAt: StringOrNull(obj["progressAt"]),
            ProgressKey: StringOrNull(obj["progressKey"]),
            WorkerTail: StringOrNull(obj["workerTail"]) ?? "",
            WorkerActivity: StringOrNull(obj["workerActivity"]) ?? "",
            MegaTaskId: StringOrNull(obj["megaTaskId"]),
            MegaTask: MegaTaskFromObject(obj["megaTask"]),
            SubTasks: SubTasksFromArray(obj["subTasks"]),
            Repository: RepositoryFromObject(obj["repository"]),
            Monitoring: MonitoringFromObject(obj["monitoring"]),
            StepModes: StringMap(obj["stepModes"]),
            SavingNotes: BoolMap(obj["savingNotes"]),
            AuthorizedSessionId: StringOrNull(obj["authorizedSessionId"])
        );
    }

    public async Task<PagedActivitySessions> ActivitySessionPageAsync(string projectId, int offset = 0, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || limit < 1 || limit > ActivitySessions.SessionLimit)
            throw new InvalidOperationException(string.Concat("Invalid", " activity session page."));
        if (_store["projects"] is not JsonObject projects || projects[projectId] is null)
            throw new InvalidOperationException("Select an existing project.");

        var metadata = ActivityMetadata(projectId);
        var sessions = new List<ActivitySession>();
        foreach (var item in metadata.Skip(offset).Take(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var part = (JsonObject)item.Part.DeepClone();
            var record = (JsonObject)item.Record.DeepClone();
            var tail = ActivitySessions.ReadRecordedTail(StringOrNull(part["logPath"]), _dir);
            var session = ActivitySessions.AssignmentSession(new JsonObject
            {
                ["projectId"] = projectId,
                ["row"] = record,
                ["part"] = part,
                ["execution"] = ObjectOrEmpty(_store["execution"]),
                ["historical"] = item.Historical,
                ["index"] = item.Index,
                ["tail"] = tail
            });
            if (session is not null) sessions.Add(ActivitySessionFromJson(session));
        }
        return new PagedActivitySessions(projectId, metadata.Count, offset, limit, sessions);
    }

    private List<(JsonObject Part, JsonObject Record, bool Historical, int Index)> ActivityMetadata(string projectId)
    {
        var result = new List<(JsonObject, JsonObject, bool, int)>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var megas = (_store["execution"]?["megaTasks"] as JsonArray)?.OfType<JsonObject>()
            .Where(m => StringOrNull(m["projectId"]) == projectId)
            .ToDictionary(m => StringOrNull(m["id"]) ?? "", StringComparer.Ordinal) ?? [];
        var subs = (_store["execution"]?["subTasks"] as JsonArray)?.OfType<JsonObject>()
            .ToDictionary(s => StringOrNull(s["id"]) ?? "", StringComparer.Ordinal) ?? [];
        var index = 0;

        void Add(JsonObject? part, JsonObject record, bool historical)
        {
            if (part is null) return;
            var partProject = StringOrNull(part["projectId"]);
            if (!string.IsNullOrEmpty(partProject) && partProject != projectId) return;
            var megaId = StringOrNull(part["megaTaskId"]) ?? StringOrNull(record["megaTaskId"]);
            if (!string.IsNullOrEmpty(megaId) && !megas.ContainsKey(megaId)) return;
            var subId = StringOrNull(part["subTaskId"]);
            if (!string.IsNullOrEmpty(subId) && subs.TryGetValue(subId, out var sub) && StringOrNull(sub["megaTaskId"]) != megaId) return;
            var attempt = StringOrNull(part["attemptId"]);
            var key = attempt is not null ? $"{megaId ?? "null"}:{subId ?? "null"}:{attempt}" : "legacy-" + index;
            index++;
            if (!known.Add(key)) return;
            result.Add(((JsonObject)part.DeepClone(), (JsonObject)record.DeepClone(), historical, index - 1));
        }

        if (_store["projects"] is JsonObject projects && projects[projectId] is JsonObject row)
        {
            foreach (var part in (row["dispatch"] as JsonArray)?.OfType<JsonObject>() ?? [])
                Add(part, row, false);
        }
        foreach (var record in (_store["workerHistory"] as JsonArray)?.OfType<JsonObject>().Where(r => StringOrNull(r["id"]) == projectId).Reverse() ?? [])
            foreach (var part in (record["dispatch"] as JsonArray)?.OfType<JsonObject>() ?? []) Add(part, record, true);
        foreach (var resultRow in (_store["execution"]?["results"] as JsonArray)?.OfType<JsonObject>()
            .Where(r => megas.ContainsKey(StringOrNull(r["megaTaskId"]) ?? "") &&
                        (string.IsNullOrEmpty(StringOrNull(r["projectId"])) || StringOrNull(r["projectId"]) == projectId)).Reverse() ?? [])
        {
            var part = (JsonObject)resultRow.DeepClone();
            part["modelId"] = resultRow["assignedModelId"]?.DeepClone();
            part.Remove("logPath");
            Add(part, new JsonObject(), true);
        }
        return result;
    }

    private static ActivitySession ActivitySessionFromJson(JsonObject value)
    {
        static string? S(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static int? I(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        var events = (value["events"] as JsonArray)?.OfType<JsonObject>().Select(e => (JsonObject)e.DeepClone()).ToArray() ?? [];
        var warnings = (value["warnings"] as JsonArray)?.Select(n => n?.GetValue<string>()).Where(s => s is not null).Cast<string>().ToArray() ?? [];
        return new ActivitySession(
            S(value, "id") ?? "",
            S(value, "projectId"), S(value, "jobId"), S(value, "megaTaskId"), S(value, "subTaskId"),
            S(value, "attemptId"), S(value, "recordedSessionId"), S(value, "role"), S(value, "provider"),
            S(value, "adapter"), S(value, "model"), S(value, "modelId"), S(value, "startedAt"),
            S(value, "completedAt"), S(value, "reportedResultAt"), S(value, "processState") ?? "unknown",
            S(value, "state") ?? "unknown", value["historical"]?.GetValue<bool>() ?? false,
            S(value, "validatedResultStatus"), S(value, "recordedResultStatus"), I(value, "exitCode"),
            events, warnings, (JsonObject)value.DeepClone());
    }

    public OrchestratorStateSnapshot Snapshot(NotebookPlan plan, string? projectId = null)
    {
        var projects = new List<ProjectSnapshot>();
        foreach (var item in plan.Projects ?? [])
        {
            var row = EntryRow(item.Id);
            projects.Add(ProjectSnapshotOf(item, row));
        }
        var models = ModelsFromStore();
        var execution = ExecutionFromStore();
        return new OrchestratorStateSnapshot
        {
            UpdatedAt = _now(),
            SessionId = _sessionId,
            SelectedProjectId = StringOrNull(_store["selectedProjectId"]),
            Execution = execution,
            Sessions = SessionsFromStore(),
            WorkerHistory = WorkerHistoryFromStore(),
            Mode = StringOrNull(_store["mode"]) == "auto" ? "auto" : "manual",
            AutoDecide = AutoDecideFromStore(),
            DefaultAutoDecide = BoolOrFalse(_store["defaultAutoDecide"]),
            DecisionsInbox = DecisionsFromStore(),
            Autonomy = AutonomyFromStore(),
            GrillPropose = !(_store["grillPropose"] is JsonValue gp && gp.TryGetValue<bool>(out var gpv) && !gpv),
            GrillKind = StringOrNull(_store["grillKind"]) == "docs" ? "docs" : "me",
            Models = models,
            Researches = ResearchFromStore(),
            Asks = AsksFromStore(),
            Asking = (_store["thinkingByKey"] as JsonObject)?.Count > 0,
            GoldModelId = StringOrNull(_store["goldModelId"]),
            OrchestratorLabel = models.FirstOrDefault()?.Label ?? OrchestratorDefaultName,
            Orchestrators = models.Select(m => new OrchestratorChoice(m.Id, m.Label, m.Provider, m.Plan)).ToArray(),
            Projects = projects
        };
    }

    private ProjectSnapshot ProjectSnapshotOf(NotebookProject item, JsonObject row)
    {
        var entry = ProjectEntryFromObject(row);
        var terminal = entry.Status is "complete" or "failed" or "checkpointed";
        var dispatch = new List<DispatchView>();
        var sessions = new List<ActivitySession>();
        foreach (var part in (row["dispatch"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var pid = IntOrNull(part["pid"]);
            var alive = !terminal && pid is int p && (_collaborators.IsProcessAlive?.Invoke(p.ToString()) ?? BoolOrFalse(part["alive"]));
            part["observedAlive"] = alive;
            var session = SessionFor(item.Id, row, part, false, dispatch.Count);
            if (session is not null) sessions.Add(session);
            dispatch.Add(new DispatchView(StringOrNull(part["subTaskId"]), StringOrNull(part["megaTaskId"]),
                StringOrNull(part["attemptId"]), StringOrNull(part["modelId"]), StringOrNull(part["label"]),
                StringOrNull(part["slice"]), StringOrNull(part["folder"]), StringOrNull(part["resultPath"]),
                StringOrNull(part["logPath"]), pid, alive, BoolOrFalse(part["stoppedByUser"]),
                StringOrNull(part["activity"]), session, false, null, entry.Status, alive, ReadDiagnostic(StringOrNull(part["resultPath"]))));
        }
        var all = ActivityMetadata(item.Id);
        foreach (var old in all.Where(value => value.Historical).Take(ActivitySessions.SessionLimit - sessions.Count))
        {
            var session = SessionFor(item.Id, old.Record, old.Part, true, old.Index);
            if (session is not null && sessions.All(s => s.AttemptId != session.AttemptId)) sessions.Add(session);
        }
        var plan = item.Revisions?.LastOrDefault()?.Direction ?? item.Goal;
        var docs = entry.Docs.ToArray();
        var facts = entry.Facts;
        var docsAvailability = docs.Length > 0 ? "available" : item.Folder is null && item.Github is null ? "unlinked" : "unknown";
        var task = item.Tasks?.FirstOrDefault(t => t.Status == "running");
        var crew = CrewOf(entry, ModelsFromStore().FirstOrDefault()?.Label, ExecutionFromStore());
        return new ProjectSnapshot
        {
            Id = item.Id, Name = item.Name, Folder = item.Folder, Github = item.Github, Repository = entry.Repository,
            Goal = plan ?? "", GoalStarted = item.GoalStarted, ChatMode = item.ChatMode, SavingMode = item.SavingMode,
            Notes = (item.Notes ?? []).ToArray(), StateArt = item.StateArt, Open = (item.Open ?? []).ToArray(),
            Status = entry.Status, Label = StatusLabel(entry.Status), Doing = entry.Doing, Plan = entry.Plan.ToArray(),
            Facts = facts, JobTitle = entry.JobTitle ?? task?.Title, JobId = entry.JobId ?? task?.Id, AttemptId = entry.AttemptId,
            MegaTaskId = entry.MegaTaskId, MegaTask = entry.MegaTask, SubTasks = entry.SubTasks?.Select(s => new PlainSubTask(s.Id, s.Title, s.Status)).ToArray() ?? [],
            Plain = new PlainWorkSummary(entry.Status, entry.Doing), Results = [],
            CheckpointId = entry.Checkpoint?.Path, ResumableCheckpoints = entry.Checkpoint is null ? [] : [entry.Checkpoint],
            ModelId = entry.ModelId, WorkerLabel = entry.WorkerLabel, WaitingFor = entry.WaitingFor,
            Next = entry.Next, Step = StepOf(entry), Pipeline = PipelineOf(entry), LeadLabel = crew.FirstOrDefault()?.Name ?? OrchestratorDefaultName,
            Crew = crew.ToArray(), ResultPath = entry.ResultPath, WatchingSince = entry.WatchingSince, Approved = entry.Approved,
            SessionId = entry.AuthorizedSessionId ?? _sessionId, CanStart = entry.Status is "idle" or "failed" or "complete",
            CanResume = entry.Status == "checkpointed", CanStop = entry.Status == "watching", PendingAutoStart = entry.PendingAutoStart is not null,
            Dispatch = dispatch.ToArray(), ActivitySessionPagination = new(true, all.Count, ActivitySessions.SessionLimit),
            ActivitySessions = sessions.Take(ActivitySessions.SessionLimit).ToArray(), Checkpoint = entry.Checkpoint,
            Timing = task?.Timing ?? entry.PendingAutoStart?.Timing, DeadlineAt = task?.DeadlineAt ?? entry.PendingAutoStart?.DeadlineAt,
            CanGrillDocs = docsAvailability is "available" or "unknown", DocsAvailability = docsAvailability, Docs = docs,
            GrillSkipped = entry.GrillSkipped, Log = entry.Log.ToArray(), WorkerPid = entry.WorkerPid,
            WorkerAlive = !terminal && entry.WorkerPid is int wp && (_collaborators.IsProcessAlive?.Invoke(wp.ToString()) ?? entry.WorkerAlive),
            WorkerLog = entry.WorkerLog, WorkerTail = entry.WorkerTail, WorkerActivity = entry.WorkerActivity,
            OrchestratorDoing = OrchestratorDoing(entry), OrchestratorLabel = crew.FirstOrDefault()?.Name ?? OrchestratorDefaultName,
            Monitoring = entry.Monitoring
        };
    }

    private ActivitySession? SessionFor(string projectId, JsonObject row, JsonObject part, bool historical, int index)
    {
        var session = ActivitySessions.AssignmentSession(new JsonObject
        {
            ["projectId"] = projectId, ["row"] = (JsonObject)row.DeepClone(), ["part"] = (JsonObject)part.DeepClone(),
            ["execution"] = ObjectOrEmpty(_store["execution"]), ["historical"] = historical, ["index"] = index,
            ["tail"] = ActivitySessions.ReadRecordedTail(StringOrNull(part["logPath"]), _dir)
        });
        return session is null ? null : ActivitySessionFromJson(session);
    }

    private JsonObject? ReadDiagnostic(string? resultPath)
    {
        if (string.IsNullOrEmpty(resultPath) || string.IsNullOrEmpty(_dir)) return null;
        try
        {
            var file = Path.GetFullPath(resultPath + ".diagnostic.json");
            if (!ProjectWorkspace.Within(_dir, file) || !File.Exists(file)) return null;
            return JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) as JsonObject;
        }
        catch { return null; }
    }

    private ExecutionState ExecutionFromStore()
    {
        var execution = ObjectOrEmpty(_store["execution"]);
        static IReadOnlyList<JsonObject> List(JsonNode? node) => (node as JsonArray)?.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToArray() ?? [];
        return new ExecutionState(List(execution["megaTasks"]), List(execution["subTasks"]), List(execution["results"]),
            List(execution["checkpoints"]), List(execution["timeline"]), ObjectOrEmpty(execution["observations"]), List(execution["decisions"]));
    }

    private IReadOnlyList<ModelInfo> ModelsFromStore()
    {
        return (_store["models"] as JsonArray)?.OfType<JsonObject>().Select(m => new ModelInfo(
            StringOrNull(m["id"]) ?? "", StringOrNull(m["label"]) ?? StringOrNull(m["id"]) ?? "",
            StringOrNull(m["provider"]) ?? StringOrNull(m["providerId"]), StringOrNull(m["cli"]),
            !m.ContainsKey("enabled") || BoolOrFalse(m["enabled"]), !m.ContainsKey("available") || BoolOrFalse(m["available"]),
            StringOrNull(m["plan"]), (JsonObject)m.DeepClone())).ToArray() ?? [];
    }

    private IReadOnlyList<SessionRecord> SessionsFromStore()
    {
        return (_store["sessions"] as JsonArray)?.OfType<JsonObject>().Select(s => new SessionRecord(
            StringOrNull(s["id"]) ?? "", StringOrNull(s["projectId"]), StringOrNull(s["startedAt"]), StringOrNull(s["status"]), (JsonObject)s.DeepClone())).ToArray() ?? [];
    }

    private IReadOnlyList<WorkerHistoryRecord> WorkerHistoryFromStore()
    {
        return (_store["workerHistory"] as JsonArray)?.OfType<JsonObject>().Select(h => new WorkerHistoryRecord(
            StringOrNull(h["id"]) ?? "", StringOrNull(h["status"]) ?? "", (h["dispatch"] as JsonArray)?.OfType<JsonObject>().Select(DispatchPartFromObject).ToArray() ?? [],
            StringOrNull(h["at"]), (JsonObject)h.DeepClone())).ToArray() ?? [];
    }

    private static DispatchPart DispatchPartFromObject(JsonObject part) => new(
        StringOrNull(part["modelId"]) ?? "", StringOrNull(part["label"]) ?? StringOrNull(part["modelId"]) ?? "",
        StringOrNull(part["slice"]) ?? "", StringOrNull(part["why"]) ?? "", IntOrNull(part["pid"]),
        StringOrNull(part["logPath"]), StringOrNull(part["resultPath"]), BoolOrFalse(part["stoppedByUser"]));

    private IReadOnlyDictionary<string, AutoDecideSettings> AutoDecideFromStore()
    {
        var result = new Dictionary<string, AutoDecideSettings>(StringComparer.Ordinal);
        if (_store["autoDecide"] is not JsonObject obj) return result;
        foreach (var pair in obj)
        {
            if (pair.Value is JsonObject value)
                result[pair.Key] = new AutoDecideSettings(BoolOrFalse(value["enabled"]), StringOrNull(value["sessionId"]), (JsonObject)value.DeepClone());
        }
        return result;
    }

    private IReadOnlyDictionary<string, AutonomySettings> AutonomyFromStore()
    {
        var result = new Dictionary<string, AutonomySettings>(StringComparer.Ordinal);
        if (_store["autonomy"] is not JsonObject obj) return result;
        foreach (var pair in obj)
        {
            if (pair.Value is JsonObject value)
                result[pair.Key] = new AutonomySettings(BoolOrFalse(value["enabled"]), BoolOrFalse(value["started"]), StringOrNull(value["stopReason"]), (JsonObject)value.DeepClone());
        }
        return result;
    }

    private DecisionsInbox DecisionsFromStore()
    {
        var source = _store["decisionsInbox"] as JsonObject;
        static IReadOnlyList<DecisionRecord> Parse(JsonNode? node) => (node as JsonArray)?.OfType<JsonObject>().Select(d => new DecisionRecord(
            StringOrNull(d["id"]) ?? "", StringOrNull(d["text"]) ?? "", StringOrNull(d["projectId"]),
            StringOrNull(d["suggestion"]), BoolOrFalse(d["breaks"]), StringOrNull(d["answer"]), (JsonObject)d.DeepClone())).ToArray() ?? [];
        var open = Parse(source?["open"]);
        var answered = Parse(source?["answered"]);
        return new DecisionsInbox(open, answered, IntOrNull(source?["openCount"]) ?? open.Count);
    }

    private IReadOnlyList<ResearchRecord> ResearchFromStore()
    {
        return (_store["researches"] as JsonArray)?.OfType<JsonObject>().Select(r => new ResearchRecord(
            StringOrNull(r["id"]) ?? "", StringOrNull(r["projectId"]) ?? "", StringOrNull(r["megaTaskId"]), StringOrNull(r["attemptId"]),
            StringOrNull(r["modelId"]), StringOrNull(r["provider"]), StringOrNull(r["cli"]), StringOrNull(r["status"]) ?? "",
            StringOrNull(r["doing"]), StringOrNull(r["activity"]), StringOrNull(r["reason"]), StringOrNull(r["folder"]),
            StringOrNull(r["reportPath"]), StringOrNull(r["resultPath"]), StringOrNull(r["logPath"]), IntOrNull(r["pid"]),
            BoolOrFalse(r["alive"]), BoolOrFalse(r["reportAvailable"]), StringOrNull(r["workerTail"]) ?? "",
            StringOrNull(r["sessionId"]), StringOrNull(r["startedAt"]) ?? "", StringOrNull(r["updatedAt"]) ?? "",
            IntOrNull(r["executionVersion"]) ?? 2)).ToArray() ?? [];
    }

    private IReadOnlyList<AskRecord> AsksFromStore()
    {
        return (_store["asks"] as JsonArray)?.OfType<JsonObject>().Select(a => new AskRecord(
            StringOrNull(a["id"]) ?? "", StringOrNull(a["projectId"]) ?? "", StringOrNull(a["question"]) ?? "",
            StringOrNull(a["answer"]), StringOrNull(a["at"]), (JsonObject)a.DeepClone())).ToArray() ?? [];
    }

    public Task<OrchestratorStateSnapshot> TickAsync(string? projectId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _collaborators.ReadPlan is null
            ? new NotebookPlan([])
            : _collaborators.ReadPlan(cancellationToken).GetAwaiter().GetResult();
        return Task.FromResult(Snapshot(plan, projectId));
    }

    public Task<SetFocusResult> SetFocusAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_store["projects"] is not JsonObject projects || projects[projectId] is null)
            return Task.FromResult(new SetFocusResult(false, StringOrNull(_store["selectedProjectId"]), "Selected project no longer exists."));
        _store["selectedProjectId"] = projectId;
        Persist();
        return Task.FromResult(new SetFocusResult(true, projectId));
    }

    public Task<InspectRepositoryResult> InspectRepositoryAsync(string projectId, RepositoryInspectOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = InspectRepositoryCoreAsync(projectId, options, cancellationToken);
        return task;
    }

    private async Task<InspectRepositoryResult> InspectRepositoryCoreAsync(string projectId, RepositoryInspectOptions? options, CancellationToken cancellationToken)
    {
        if (_store["projects"] is not JsonObject projects || projects[projectId] is null)
            return new InspectRepositoryResult(false, Reason: "project not found");
        var row = EntryRow(projectId);
        var folder = StringOrNull(row["folder"]) ?? StringOrNull(row["facts"]?["folder"]);
        NotebookProject? notebookProject = null;
        if (_collaborators.ReadPlan is not null)
        {
            var plan = await _collaborators.ReadPlan(cancellationToken).ConfigureAwait(false);
            notebookProject = plan.Projects.FirstOrDefault(p => p.Id == projectId);
            folder ??= notebookProject?.Folder;
        }
        var repository = _collaborators.InspectRepository is not null
            ? await _collaborators.InspectRepository(folder ?? "", options, cancellationToken).ConfigureAwait(false)
            : await InspectRepositoryDefaultAsync(folder, cancellationToken).ConfigureAwait(false);
        row["repository"] = RepositoryToJson(repository);
        if (!string.IsNullOrEmpty(folder))
        {
            var facts = DefaultInspect(folder);
            row["facts"] = new JsonObject
            {
                ["git"] = facts.Git, ["lastCommit"] = facts.LastCommit, ["lastSubject"] = facts.LastSubject,
                ["recent"] = new JsonArray(facts.Recent?.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray() ?? []),
                ["dirty"] = new JsonArray(facts.Dirty?.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray() ?? []),
                ["head"] = facts.Head, ["branch"] = facts.Branch, ["fileCount"] = facts.FileCount, ["folder"] = facts.Folder, ["error"] = facts.Error
            };
        }
        Persist();
        var snapshot = notebookProject is null ? null : Snapshot(new NotebookPlan([notebookProject]), projectId);
        return new InspectRepositoryResult(true, repository, snapshot, null);
    }

    private static JsonObject RepositoryToJson(RepositoryInfo value)
    {
        var result = new JsonObject
        {
            ["checkedAt"] = value.CheckedAt,
            ["folder"] = value.Folder,
            ["github"] = value.Github,
            ["local"] = value.Local?.DeepClone(),
            ["remote"] = value.Remote?.DeepClone(),
            ["comparison"] = value.Comparison?.DeepClone()
        };
        if (value.Error is not null) result["error"] = value.Error;
        return result;
    }

    private async Task<RepositoryInfo> InspectRepositoryDefaultAsync(string? folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var linked = ProjectWorkspace.LinkedWorkspace(folder);
        var local = new JsonObject { ["exists"] = linked is not null, ["readable"] = false, ["writable"] = false, ["git"] = false };
        if (linked is null)
            return new RepositoryInfo(_now(), folder, null, local, new JsonObject { ["accessible"] = false }, new JsonObject { ["status"] = "unknown" });
        try
        {
            _ = Directory.EnumerateFileSystemEntries(linked).Take(1).ToArray();
            local["readable"] = true;
            try
            {
                var probe = Path.Combine(linked, ".planwharf-write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "");
                File.Delete(probe);
                local["writable"] = true;
            }
            catch { }
        }
        catch (Exception error)
        {
            return new RepositoryInfo(_now(), linked, null, local, new JsonObject { ["accessible"] = false }, new JsonObject { ["status"] = "unknown" }, error.Message);
        }

        var facts = DefaultInspect(linked);
        local["git"] = facts.Git;
        local["folder"] = linked;
        local["branch"] = facts.Branch;
        local["head"] = RunGit(linked, "rev-parse", "--verify", "HEAD");
        local["dirty"] = new JsonArray((facts.Dirty ?? []).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
        local["diffStat"] = Clip(RunGit(linked, "diff", "--stat", "HEAD"), 400);
        return new RepositoryInfo(_now(), linked, null, local,
            new JsonObject { ["accessible"] = false },
            new JsonObject { ["status"] = facts.Git ? "local-only" : "unknown", ["ahead"] = null, ["behind"] = null });
    }
}
