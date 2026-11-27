// Port of engine/orchestrator.js state management.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
    IReadOnlyList<string>? Closed = null,
    string? Id = null,
    string? Title = null,
    string? Mode = null);

public sealed record NotebookTask(
    string Id,
    string Title,
    string Detail = "",
    string Status = "",
    string? RevisionId = null,
    int ExecutionVersion = 1,
    string Timing = "discretion",
    string? DeadlineAt = null,
    string? MegaTaskId = null,
    JsonObject? Raw = null,
    string? Source = null,
    ModelCandidate? GoldModel = null);

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
    public string? OrchestratorId { get; init; }
    public string OrchestratorLabel { get; init; } = OrchestratorState.OrchestratorDefaultName;
    public MonitoringMetrics? Monitoring { get; init; }
    public string? GoldModel { get; init; }
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
public sealed record ArchivedSummary(string At, string Reason, int Asks, int MegaTasks);
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
    IReadOnlyList<DispatchView>? Dispatch = null,
    JsonObject? Diagnostic = null);

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
    [property: JsonPropertyName("memoryBytes")] long? MemoryBytes = null,
    [property: JsonPropertyName("workers")] int? Workers = null,
    [property: JsonPropertyName("alive")] int? Alive = null,
    [property: JsonPropertyName("logMoving")] bool? LogMoving = null,
    [property: JsonPropertyName("folderFiles")] int? FolderFiles = null,
    [property: JsonPropertyName("lastProgress")] string? LastProgress = null,
    JsonObject? Raw = null
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
    Func<string, RepositoryInspectOptions?, CancellationToken, Task<RepositoryInfo>>? InspectRepository = null,
    Action? RestoreUnavailableModels = null,
    Func<bool, CancellationToken, Task>? RefreshPlans = null,
    Action<NotebookPlan, string?>? RefreshRepositoryMetadata = null,
    Action<OrchestratorTickProject>? Decide = null,
    Action<NotebookPlan, bool, string?>? RefreshPhaseTwoResearch = null,
    Action? RefreshResearch = null,
    Func<IReadOnlyList<CompletedNotebookTask>, CancellationToken, Task<NotebookPlan>>? CompleteTasks = null,
    Func<bool>? IsShuttingDown = null,
    Func<ExecutionBridge?>? Bridge = null,
    AttemptEngine? Attempts = null,
    Func<string, string>? ReadResult = null,
    bool EnforceSessionBoundary = false,
    Func<NotebookProject, NotebookTask, IReadOnlyList<JsonObject>>? DispatchParts = null,
    IExecutionAttempts? AttemptAdapter = null,
    Func<int, bool>? KillWorker = null,
    ExecutionBridge? ExecutionBridge = null,
    bool Phase2 = false
);

// The composition root supplies team selection/dispatch. Each invocation carries its own
// project and the exact durable row; there is no shared current-project field.
public sealed record OrchestratorTickProject(
    NotebookProject Project, JsonObject Row, bool WasRunningAtStartup,
    string SessionId, CancellationToken CancellationToken);

public sealed record CompletedNotebookTask(
    string ProjectId, string TaskId, string Fingerprint, string? AttemptId, string? MegaTaskId);

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
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly AsyncLocal<int> _gateDepth = new();
    private Dictionary<string, HashSet<string>>? _startupProjects;
    private ExecutionBridge? _executionBridge;

    public string? Dir => _dir;
    public string? StateFile => _stateFile;
    public string SessionId => _sessionId;
    public JsonObject Store => _store;
    public OrchestratorStateCollaborators Collaborators => _collaborators;

    public Func<string, string?, JsonObject> RowForDelegate => RowFor;

    public void AttachExecutionBridge(ExecutionBridge? bridge) => _executionBridge = bridge;

    private IDisposable EnterGate()
    {
        if (_gateDepth.Value > 0)
        {
            _gateDepth.Value++;
            return new GateScope(this, acquired: false);
        }
        _transitionGate.Wait();
        _gateDepth.Value = 1;
        return new GateScope(this, acquired: true);
    }

    // WaitGateIfNeededAsync must not be an async method that mutates _gateDepth directly,
    // because AsyncLocal mutations inside an async state machine do not flow back to callers.
    private Task<bool> WaitGateIfNeededAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_gateDepth.Value > 0) return Task.FromResult(false);
        return WaitGateCoreAsync(cancellationToken);
    }

    private async Task<bool> WaitGateCoreAsync(CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private IDisposable PushGateScope(bool acquired)
    {
        _gateDepth.Value++;
        return new GateScope(this, acquired);
    }

    private sealed class GateScope(OrchestratorState owner, bool acquired) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._gateDepth.Value = Math.Max(0, owner._gateDepth.Value - 1);
            if (acquired) owner._transitionGate.Release();
        }
    }

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

    private static JsonObject NormalizeProjectTeams(JsonNode? node)
    {
        var result = new JsonObject();
        if (node is not JsonObject teams) return result;
        foreach (var (id, teamNode) in teams)
        {
            if (teamNode is not JsonObject team || team["roles"] is not JsonObject roles) continue;
            var clean = new JsonObject
            {
                ["orchestratorId"] = StringOrNull(team["orchestratorId"]),
                ["roles"] = (JsonObject)roles.DeepClone()
            };
            if (team["notLead"] is JsonArray nl)
            {
                clean["notLead"] = new JsonArray(nl.Select(StringOrNull).Where(s => s != null).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            }
            result[id] = clean;
        }
        return result;
    }

    private static JsonArray NormalizePullRequestFixes(JsonNode? node)
    {
        var result = new JsonArray();
        if (node is not JsonArray arr) return result;
        foreach (var item in arr.OfType<JsonObject>())
        {
            if (StringOrNull(item["projectId"]) is null) continue;
            if (item["number"] is not JsonValue numVal || !numVal.TryGetValue<int>(out _)) continue;
            result.Add((JsonNode)item.DeepClone());
        }
        return result;
    }

    private static JsonObject ToolFlagsObject(JsonObject? chosen)
    {
        var flags = new JsonObject();
        foreach (var name in Claude.ToolNames)
        {
            flags[name] = chosen != null && BoolOrFalse(chosen[name]);
        }
        return flags;
    }

    private static JsonObject NormalizeToolSettingsObject(JsonNode? node)
    {
        var value = node as JsonObject;
        var defaultObj = value?["default"] as JsonObject ?? value;
        var modelsObj = new JsonObject();
        if (value?["models"] is JsonObject rawModels)
        {
            foreach (var (id, toolsNode) in rawModels)
            {
                if (toolsNode is JsonObject tools)
                {
                    modelsObj[id] = ToolFlagsObject(tools);
                }
            }
        }
        return new JsonObject
        {
            ["default"] = ToolFlagsObject(defaultObj),
            ["models"] = modelsObj
        };
    }

    private static string NormalizeChatMode(string? value)
    {
        var mode = (value ?? "").Trim().ToLowerInvariant();
        return mode is "talk" or "plan" ? mode : "plan";
    }

    private static string? NormalizeTeamWeight(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text switch
        {
            "flagship" or "premium" => "flagship",
            "mid" or "default" => "mid",
            "light" => "light",
            _ => null
        };
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
        bool? logMoving = obj["logMoving"] is JsonValue lmValue && lmValue.TryGetValue<bool>(out var lmResult) ? lmResult : null;
        return new MonitoringMetrics(
            cpu,
            memory,
            IntOrNull(obj["workers"]),
            IntOrNull(obj["alive"]),
            logMoving,
            IntOrNull(obj["folderFiles"]),
            StringOrNull(obj["lastProgress"]),
            (JsonObject)obj.DeepClone());
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
        _executionBridge = _collaborators.ExecutionBridge;
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
                    if (parsed["autoSwitch"] is JsonObject autoSwitchObj) store["autoSwitch"] = (JsonObject)autoSwitchObj.DeepClone();
                    else store.Remove("autoSwitch");
                    store["quotaLimits"] = ObjectOrEmpty(parsed["quotaLimits"]);
                    store["creditsNeeded"] = ObjectOrEmpty(parsed["creditsNeeded"]);
                    store["poolLimits"] = ObjectOrEmpty(parsed["poolLimits"]);
                    store["notLead"] = new JsonArray((StringList(parsed["notLead"]) ?? []).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
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
                    store["projectTeams"] = NormalizeProjectTeams(parsed["projectTeams"]);
                    store["accountBindings"] = ObjectOrEmpty(parsed["accountBindings"]);
                    store["lastAccountKeys"] = ObjectOrEmpty(parsed["lastAccountKeys"]);
                    store["briefingBaselines"] = ObjectOrEmpty(parsed["briefingBaselines"]);
                    store["asks"] = ArrayOrEmpty(parsed["asks"]);
                    store["researches"] = ArrayOrEmpty(parsed["researches"]);
                    store["workerHistory"] = ArrayOrEmpty(parsed["workerHistory"]);
                    store["archivedProjects"] = ObjectOrEmpty(parsed["archivedProjects"]);
                    store["projectArchive"] = ArrayOrEmpty(parsed["projectArchive"]);
                    store["projectTools"] = ObjectOrEmpty(parsed["projectTools"]);
                    store["pullRequestFixes"] = NormalizePullRequestFixes(parsed["pullRequestFixes"]);
                    var selProj = StringOrNull(parsed["selectedProjectId"]);
                    store["selectedProjectId"] = string.IsNullOrEmpty(selProj) ? null : selProj;
                    store["sessions"] = ArrayOrEmpty(parsed["sessions"]);
                    store["goldModelId"] = StringOrNull(parsed["goldModelId"]);
                    var orchId = StringOrNull(parsed["orchestratorId"]);
                    store["orchestratorId"] = !string.IsNullOrWhiteSpace(orchId) ? orchId : null;

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

        // Normalize projectTools, chatModes, savingModes, and modelWeights matching JS loadState
        if (store["projectTools"] is JsonObject rawProjectTools)
        {
            var normalizedTools = new JsonObject();
            foreach (var (projId, toolsNode) in rawProjectTools)
            {
                normalizedTools[projId] = NormalizeToolSettingsObject(toolsNode);
            }
            store["projectTools"] = normalizedTools;
        }
        if (store["chatModes"] is JsonObject rawChatModes)
        {
            foreach (var key in rawChatModes.Select(p => p.Key).ToArray())
            {
                rawChatModes[key] = NormalizeChatMode(StringOrNull(rawChatModes[key]));
            }
        }
        var savingRepaired = false;
        if (store["savingModes"] is JsonObject rawSavingModes)
        {
            foreach (var key in rawSavingModes.Select(p => p.Key).ToArray())
            {
                var currentVal = StringOrNull(rawSavingModes[key]);
                var nextVal = ModelRouting.PersistedSavingMode(currentVal);
                if (currentVal != nextVal) savingRepaired = true;
                rawSavingModes[key] = nextVal;
            }
        }
        if (store["modelWeights"] is JsonObject rawWeights)
        {
            foreach (var key in rawWeights.Select(p => p.Key).ToArray())
            {
                var nextWeight = NormalizeTeamWeight(StringOrNull(rawWeights[key]));
                if (nextWeight != null)
                {
                    if (StringOrNull(rawWeights[key]) != nextWeight) savingRepaired = true;
                    rawWeights[key] = nextWeight;
                }
                else
                {
                    rawWeights.Remove(key);
                    savingRepaired = true;
                }
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
        if (savingRepaired) state.Persist();
        state.HydrateCheckpoints();
        return state;
    }

    public void Persist()
    {
        using var _ = EnterGate();
        if (string.IsNullOrWhiteSpace(_stateFile)) return;
        var dir = Path.GetDirectoryName(_stateFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temporary = _stateFile + "." + Guid.NewGuid().ToString() + ".tmp";
        var json = _store.ToJsonString(JsonSerializationOptions) + "\n";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
            }
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
        using var _ = EnterGate();
        if (string.IsNullOrWhiteSpace(_dir)) return;
        var checkpointsFolder = Path.Combine(_dir, "checkpoints");
        var checkpointFiles = Directory.Exists(checkpointsFolder)
            ? Directory.GetFiles(checkpointsFolder, "*.json")
            : [];
        var ids = new HashSet<string>(StringComparer.Ordinal);
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
        if (string.IsNullOrWhiteSpace(_dir) || string.IsNullOrWhiteSpace(id)) return null;
        if (id.IndexOfAny(['/', '\\', ':']) >= 0 || id == "." || id == ".." || id.Contains("..", StringComparison.Ordinal))
            return null;
        var checkpointsFolder = Path.GetFullPath(Path.Combine(_dir, "checkpoints"));
        var file = Path.GetFullPath(Path.Combine(checkpointsFolder, $"{id}.json"));
        if (!ProjectWorkspace.Within(checkpointsFolder, file) || !File.Exists(file)) return null;
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
                ? row.Dispatch.Count(p => !p.StoppedByUser && p.Pid != null)
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
        var workerLabel = StringOrNull(row["workerLabel"]);
        var n = dispatchCount > 0 ? dispatchCount : (!string.IsNullOrEmpty(workerLabel) ? 1 : 0);
        var status = StringOrNull(row["status"]) ?? "idle";
        var workerActivity = StringOrNull(row["workerActivity"]) ?? "";
        var workerAlive = BoolOrFalse(row["workerAlive"]);

        if (status == "watching")
        {
            var alive = dispatchArr is not null
                ? dispatchArr.OfType<JsonObject>().Count(p => BoolOrFalse(p["alive"]))
                : (workerAlive ? 1 : 0);
            var factsObj = row["facts"] as JsonObject;
            int? files = IntOrNull(factsObj?["fileCount"]);
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
                        : $"{(!string.IsNullOrEmpty(part.Slice) ? part.Slice : "Whole job")}. {(part.Pid is int and not 0 ? $"Process {part.Pid} running." : "Claimed.")}";
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
                    : (row.WorkerPid is int and not 0 ? $"Process {row.WorkerPid}{(row.WorkerAlive ? " running" : " stopped")}." : "Claimed."))
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
        var rowOrchDoing = StringOrNull(row?["orchestratorDoing"]);
        var rowOrchLabel = StringOrNull(row?["orchestratorLabel"]);
        var crew = new List<CrewMember>
        {
            new(
                "Orchestrator",
                !string.IsNullOrEmpty(leadLabel) ? leadLabel : (!string.IsNullOrEmpty(rowOrchLabel) ? rowOrchLabel : OrchestratorDefaultName),
                !string.IsNullOrEmpty(rowOrchDoing) ? rowOrchDoing : OrchestratorDoing(row))
        };

        var status = StringOrNull(row?["status"]) ?? "idle";
        var liveWorkers = row != null && status != "idle" && status != "propose_grill" && status != "waiting_on_you" && status != "blocked_no_folder";
        var dispatchArr = liveWorkers ? row?["dispatch"] as JsonArray : null;

        if (dispatchArr != null && dispatchArr.Count > 0)
        {
            var subTasksArr = (execution?["subTasks"] as JsonArray) ?? (row?["subTasks"] as JsonArray) ?? [];
            foreach (var partNode in dispatchArr.OfType<JsonObject>())
            {
                var subTaskId = StringOrNull(partNode["subTaskId"]);
                var partMegaTaskId = StringOrNull(partNode["megaTaskId"]);
                var subObj = subTasksArr.OfType<JsonObject>().FirstOrDefault(s =>
                    StringOrNull(s["id"]) == subTaskId && (string.IsNullOrEmpty(partMegaTaskId) || StringOrNull(s["megaTaskId"]) == partMegaTaskId));

                var retainedRole = StringOrNull(partNode["activitySession"]?["role"]);
                var role = retainedRole == "reviewer" || (retainedRole == null && ModelRoles.AssignmentRole(subObj ?? partNode) == "reviewer")
                    ? "Reviewer"
                    : "Worker";

                var slice = StringOrNull(partNode["slice"]);
                var doing = !string.IsNullOrEmpty(slice) ? slice : "On the plan";
                var historical = BoolOrFalse(partNode["historicalAssignment"]);
                var label = StringOrNull(partNode["label"]) ?? StringOrNull(partNode["modelId"]) ?? "";

                if (historical)
                {
                    var histTitle = StringOrNull(partNode["assignmentJobTitle"]) ?? "Historical assignment";
                    var histAct = StringOrNull(partNode["activity"]) ?? "Original activity is not retained.";
                    crew.Add(new CrewMember(role, label, $"{histTitle}: {histAct}"));
                    continue;
                }

                if (status == "needs_approval") doing = $"{(!string.IsNullOrEmpty(slice) ? slice : "Whole job")}. Waiting for Approve.";
                else if (status == "watching")
                {
                    var partAct = StringOrNull(partNode["activity"]);
                    var rowAct = StringOrNull(row?["workerActivity"]);
                    var pid = IntOrNull(partNode["pid"]);
                    var alive = BoolOrFalse(partNode["alive"]);
                    doing = !string.IsNullOrEmpty(partAct) ? partAct : (!string.IsNullOrEmpty(rowAct) ? rowAct : $"{(!string.IsNullOrEmpty(slice) ? slice : "Whole job")}. {(pid is int and not 0 ? $"Process {pid}{(alive ? " running" : " stopped")}." : "Claimed.")}");
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
            var workerLabel = StringOrNull(row?["workerLabel"]);
            if (!string.IsNullOrEmpty(workerLabel) && !workerLabel.Contains(" + "))
            {
                var workerActivity = StringOrNull(row?["workerActivity"]);
                var workerPid = IntOrNull(row?["workerPid"]);
                var workerAlive = BoolOrFalse(row?["workerAlive"]);
                var waitingFor = StringOrNull(row?["waitingFor"]) ?? "";
                var doing = status == "watching"
                    ? (!string.IsNullOrEmpty(workerActivity)
                        ? workerActivity
                        : (workerPid is int and not 0 ? $"Process {workerPid}{(workerAlive ? " running" : " stopped")}." : "Claimed."))
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

    public static JsonObject DefaultInspectJson(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new JsonObject { ["error"] = "no folder" };
        try
        {
            if (File.Exists(folder) && !Directory.Exists(folder))
            {
                return new JsonObject { ["error"] = "not a directory", ["folder"] = folder };
            }
            var linked = ProjectWorkspace.LinkedWorkspace(folder);
            if (linked is null) return new JsonObject { ["error"] = "folder not found", ["folder"] = folder };

            var allNames = Directory.EnumerateFileSystemEntries(linked)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .ToArray();
            var names = allNames.Take(24).ToArray();
            var docs = OrchestratorLeadQuestions.ListDocs(linked);
            var topLevel = RunGit(linked, "rev-parse", "--show-toplevel");
            var git = ProjectWorkspace.SafeGitWorkspace(linked) is not null &&
                string.Equals(ProjectWorkspace.LinkedWorkspace(topLevel), linked, StringComparison.OrdinalIgnoreCase);
            var head = git ? ReadGitHead(linked) : "";
            var branch = git ? RunGit(linked, "rev-parse", "--abbrev-ref", "HEAD") : "";
            var lastCommit = git ? RunGit(linked, "log", "-1", "--format=%h %s") : "";
            var commitId = git ? RunGit(linked, "rev-parse", "--verify", "HEAD") : "";
            var lastSubject = git ? RunGit(linked, "log", "-1", "--format=%s") : "";
            var recent = git ? Lines(RunGit(linked, "log", "-5", "--oneline"), 5) : [];
            var dirty = git ? Lines(RunGit(linked, "status", "--ignore-submodules=all", "--porcelain", "--no-renames"), 20) : [];
            var diffStat = git ? Clip(RunGit(linked, "diff", "--ignore-submodules=all", "--submodule=short", "--no-ext-diff", "--no-textconv", "--no-renames", "--stat", "HEAD"), 400) : "";

            return new JsonObject
            {
                ["folder"] = linked,
                ["fileCount"] = allNames.Length,
                ["names"] = new JsonArray(names.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["docs"] = new JsonArray(docs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["git"] = git,
                ["head"] = head,
                ["branch"] = branch,
                ["lastCommit"] = lastCommit,
                ["commitId"] = string.IsNullOrEmpty(commitId) ? null : commitId,
                ["lastSubject"] = lastSubject,
                ["recent"] = new JsonArray(recent.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["dirty"] = new JsonArray(dirty.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["diffStat"] = diffStat
            };
        }
        catch
        {
            return new JsonObject { ["error"] = "folder not found", ["folder"] = folder };
        }
    }

    public static InspectionFacts DefaultInspect(string folder)
    {
        var obj = DefaultInspectJson(folder);
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
            var output = process.StandardOutput.ReadToEnd();
            return args.Length > 0 && args[0] == "status" ? output.TrimEnd('\r', '\n') : output.Trim();
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
        var gateAcquired = await WaitGateIfNeededAsync(cancellationToken).ConfigureAwait(false);
        using var _ = PushGateScope(gateAcquired);
        if (_collaborators.ReadPlan is not null)
        {
            var plan = await _collaborators.ReadPlan(cancellationToken).ConfigureAwait(false);
            if (!plan.Projects.Any(p => p.Id == projectId))
                throw new InvalidOperationException("Select an existing project.");
        }
        else if (_store["projects"] is not JsonObject projects || projects[projectId] is null)
        {
            throw new InvalidOperationException("Select an existing project.");
        }
        if (offset < 0 || limit < 1 || limit > ActivitySessions.SessionLimit)
            throw new InvalidOperationException(string.Concat("Invalid", " activity session page."));

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
            var itemIndex = index++;
            if (!known.Add(key)) return;
            result.Add(((JsonObject)part.DeepClone(), (JsonObject)record.DeepClone(), historical, itemIndex));
        }

        if (_store["projects"] is JsonObject projects && projects[projectId] is JsonObject row)
        {
            var rowProj = StringOrNull(row["projectId"]);
            var rowId = StringOrNull(row["id"]);
            if ((string.IsNullOrEmpty(rowProj) || rowProj == projectId) && (string.IsNullOrEmpty(rowId) || rowId == projectId))
            {
                foreach (var part in (row["dispatch"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    Add(part, row, false);
            }
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
        using var _ = EnterGate();
        var projectList = plan.Projects ?? [];
        RememberStartup(plan);
        var bridge = SharedBridge();
        foreach (var item in projectList.Where(p => string.IsNullOrEmpty(projectId) || p.Id == projectId))
        {
            var legacy = EntryRow(item.Id);
            if (bridge is not null)
            {
                var raw = NotebookItemJson(item);
                bridge.Observe(raw);
                if (_collaborators.Phase2 && string.IsNullOrEmpty(StringOrNull(legacy["megaTaskId"])) && StringOrNull(legacy["status"]) == "watching")
                {
                    ReconcileLegacyWatchingRow(item, legacy);
                }
                var job = item.Tasks?.FirstOrDefault(t => t.ExecutionVersion == 2 && t.Status == "running");
                if (job is not null && bridge.Find(item.Id, job.Id) is { } mega)
                {
                    RequireSharedRow(bridge, item.Id, StringOrNull(mega["id"]));
                    // A view may collect finished work, but it never schedules another worker.
                    bridge.Advance(raw, NotebookTaskJson(job), [], false);
                }
            }
            else if (_collaborators.Phase2 && string.IsNullOrEmpty(StringOrNull(legacy["megaTaskId"])) && StringOrNull(legacy["status"]) == "watching")
            {
                ReconcileLegacyWatchingRow(item, legacy);
            }
        }
        if (_collaborators.RefreshPhaseTwoResearch is not null)
            _collaborators.RefreshPhaseTwoResearch(plan, false, projectId);
        else RefreshPhaseTwoResearch(plan, false, projectId);
        if (_collaborators.RefreshResearch is not null) _collaborators.RefreshResearch();
        else RefreshResearch();
        var projects = new List<ProjectSnapshot>();
        foreach (var item in projectList)
        {
            var row = EntryRow(item.Id);
            projects.Add(ProjectSnapshotOf(item, row));
        }
        var models = ModelsFromStore();
        var execution = ExecutionFromStore();
        var selectedLeadId = ResolveLeadId(projectId);
        var leadModel = ResolveLeadModel(models, selectedLeadId);
        var grillPropose = !(_store["grillPropose"] is JsonValue gp && gp.TryGetValue<bool>(out var gpv) && !gpv);
        var grillKind = !grillPropose ? "off" : (StringOrNull(_store["grillKind"]) == "docs" ? "docs" : "me");
        var thinkingByProject = ThinkingByProjectFromStore();
        var thinkingFocus = LiveThinkingFromStore(thinkingByProject, projectId);
        var (projectTools, projectToolSettings) = ProjectToolsFromStore(projectId);
        var teamScopeId = !string.IsNullOrEmpty(projectId) && (_store["projectTeams"] as JsonObject)?.ContainsKey(projectId) == true ? projectId : null;

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
            DecisionsInbox = DecisionsFromStore(projectList),
            Autonomy = AutonomyFromStore(),
            GrillPropose = grillPropose,
            GrillKind = grillKind,
            Models = models,
            Researches = ResearchFromStore(),
            Asks = AsksFromStore(),
            Thinking = thinkingFocus,
            ThinkingByProject = thinkingByProject,
            ProjectTools = projectTools,
            ProjectToolSettings = projectToolSettings,
            Asking = thinkingByProject.Count > 0,
            OrchestratorId = leadModel?.Id,
            SelectedOrchestratorId = selectedLeadId,
            TeamProjectId = teamScopeId,
            OwnTeam = teamScopeId is not null,
            GoldModelId = StringOrNull(_store["goldModelId"]),
            OrchestratorUnavailableReason = !string.IsNullOrEmpty(selectedLeadId) && leadModel is null
                ? "Selected orchestrator is unavailable or blocked by its usage policy. Choose an eligible model or configure an explicit fallback."
                : null,
            OrchestratorLabel = leadModel?.Label ?? OrchestratorDefaultName,
            Orchestrators = models
                .Where(m => m.Raw is null || OrchestratorTeamModels.CanOrchestrate(m.Raw))
                .Select(m => new OrchestratorChoice(m.Id, m.Label, m.Provider, m.Plan))
                .ToArray(),
            Projects = projects
        };
    }

    private ProjectSnapshot ProjectSnapshotOf(NotebookProject item, JsonObject row)
    {
        var entry = ProjectEntryFromObject(row);
        var terminal = entry.Status is "complete" or "failed" or "checkpointed";
        var executionObj = _store["execution"] as JsonObject;
        var execResults = (executionObj?["results"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var execMegas = (executionObj?["megaTasks"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var rowSubTasks = (row["subTasks"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];

        var dispatch = new List<DispatchView>();
        var dispatchForCrew = new JsonArray();
        var sessions = new List<ActivitySession>();
        foreach (var part in (row["dispatch"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            var pid = IntOrNull(part["pid"]);
            var alive = pid is int p && (_collaborators.IsProcessAlive?.Invoke(p.ToString()) ?? BoolOrFalse(part["alive"]));
            var subId = StringOrNull(part["subTaskId"]);
            var attemptId = StringOrNull(part["attemptId"]);
            var resultObj = !string.IsNullOrEmpty(attemptId)
                ? execResults.FirstOrDefault(r => StringOrNull(r["attemptId"]) == attemptId)
                : null;
            var partMegaId = StringOrNull(part["megaTaskId"]) ?? StringOrNull(resultObj?["megaTaskId"]);
            var assignmentMega = !string.IsNullOrEmpty(partMegaId)
                ? execMegas.FirstOrDefault(m => StringOrNull(m["id"]) == partMegaId)
                : null;
            var historicalAssignment = assignmentMega is not null && StringOrNull(assignmentMega["id"]) != entry.MegaTaskId;
            var effectiveAlive = !terminal && !historicalAssignment && !BoolOrFalse(part["stoppedByUser"]) && alive;

            var partCopy = (JsonObject)part.DeepClone();
            if (assignmentMega is not null && !string.IsNullOrEmpty(StringOrNull(assignmentMega["id"])))
            {
                partCopy["megaTaskId"] = assignmentMega["id"]?.DeepClone();
            }
            partCopy["observedAlive"] = !terminal && !historicalAssignment && alive;
            partCopy["alive"] = effectiveAlive;

            var session = dispatch.Count < ActivitySessions.SessionLimit
                ? SessionFor(item.Id, row, partCopy, historicalAssignment, dispatch.Count)
                : null;
            if (session is not null) sessions.Add(session);

            var assignmentTitle = StringOrNull(assignmentMega?["title"]);
            var assignmentStatus = historicalAssignment
                ? StringOrNull(resultObj?["status"]) switch
                {
                    "COMPLETE" => "complete",
                    "FAILED" => "failed",
                    "BLOCKED" => "blocked",
                    _ => "historical"
                }
                : entry.Status;
            var partActivity = historicalAssignment
                ? (StringOrNull(resultObj?["summary"]) ?? "Original activity is not retained for this historical assignment.")
                : StringOrNull(part["activity"]);

            partCopy["historicalAssignment"] = historicalAssignment;
            partCopy["assignmentJobTitle"] = assignmentTitle;
            partCopy["assignmentStatus"] = assignmentStatus;
            if (historicalAssignment) partCopy["activity"] = partActivity;
            if (session?.Raw is not null) partCopy["activitySession"] = (JsonObject)session.Raw.DeepClone();
            dispatchForCrew.Add((JsonNode)partCopy);

            dispatch.Add(new DispatchView(
                subId,
                partMegaId,
                attemptId,
                StringOrNull(part["modelId"]),
                StringOrNull(part["label"]),
                StringOrNull(part["slice"]),
                StringOrNull(part["folder"]),
                StringOrNull(part["resultPath"]),
                StringOrNull(part["logPath"]),
                pid,
                effectiveAlive,
                BoolOrFalse(part["stoppedByUser"]),
                partActivity,
                session,
                historicalAssignment,
                assignmentTitle,
                assignmentStatus,
                alive,
                ReadDiagnostic(StringOrNull(part["resultPath"]))));
        }

        var all = ActivityMetadata(item.Id);
        foreach (var old in all.Where(value => value.Historical))
        {
            if (sessions.Count >= ActivitySessions.SessionLimit) break;
            var session = SessionFor(item.Id, old.Record, old.Part, true, old.Index);
            if (session is not null && (string.IsNullOrEmpty(session.AttemptId) || sessions.All(s => s.AttemptId != session.AttemptId)))
            {
                sessions.Add(session);
            }
        }

        var latestRevision = item.Revisions?.LastOrDefault();
        var goal = latestRevision?.Direction ?? item.Goal;
        var goalStarted = item.GoalStarted || (latestRevision is not null && !string.IsNullOrEmpty(latestRevision.Id) &&
            item.Tasks?.Any(t => t.RevisionId == latestRevision.Id) == true);
        var docs = entry.Docs.Count > 0
            ? entry.Docs.ToArray()
            : CollectDocsFromFactsObject(row["facts"] as JsonObject);
        var factsFolder = StringOrNull(row["facts"]?["folder"]);
        var docsInspected = row["facts"] is JsonObject && (string.IsNullOrEmpty(factsFolder) || factsFolder == item.Folder);
        var docsAvailability = docs.Length > 0
            ? "available"
            : item.Folder is null && item.Github is null
                ? "unlinked"
                : docsInspected && item.Github is null
                    ? "none"
                    : "unknown";
        var task = item.Tasks?.FirstOrDefault(t => t.Status == "running");
        var models = ModelsFromStore();
        var selectedLeadId = ResolveLeadId(item.Id);
        var leadModel = ResolveLeadModel(models, selectedLeadId);
        var leadLabel = ProjectLeadLabel(item.Id, leadModel, models);
        var orchDoing = OrchestratorDoing(row);

        var crewRow = (JsonObject)row.DeepClone();
        crewRow["dispatch"] = dispatchForCrew;
        crewRow["orchestratorDoing"] = orchDoing;
        crewRow["orchestratorLabel"] = leadLabel;
        var crew = CrewOf(crewRow, leadLabel, executionObj);

        var chatMode = (_store["chatModes"] as JsonObject)?[item.Id] is JsonNode cmNode && StringOrNull(cmNode) is string cmText
            ? NormalizeChatMode(cmText)
            : NormalizeChatMode(item.ChatMode);
        var savingMode = (_store["savingModes"] as JsonObject)?[item.Id] is JsonNode smNode && StringOrNull(smNode) is string smText
            ? ModelRouting.PersistedSavingMode(smText)
            : ModelRouting.PersistedSavingMode(item.SavingMode);

        var repoMatches = entry.Repository is not null &&
            entry.Repository.Folder == item.Folder &&
            entry.Repository.Github == item.Github;
        var repository = repoMatches ? RepositoryFromObject(row["repository"]) : null;

        var megaResultsArr = new JsonArray(!string.IsNullOrEmpty(entry.MegaTaskId)
            ? execResults.Where(r => StringOrNull(r["megaTaskId"]) == entry.MegaTaskId).Select(r => (JsonNode)r.DeepClone()).ToArray()
            : []);
        var (plainSummary, plainFrom) = PlainLines.WorkSummary(row["megaTask"] as JsonObject, megaResultsArr, row["subTasks"] as JsonArray);
        var plainSteps = new JsonArray(rowSubTasks.Select(s =>
        {
            var (line, from) = PlainLines.StepLine(s);
            return (JsonNode)new JsonObject { ["id"] = s["id"]?.DeepClone(), ["line"] = line, ["from"] = from };
        }).ToArray());
        var plainWork = new JsonObject { ["summary"] = plainSummary, ["summaryFrom"] = plainFrom, ["steps"] = plainSteps };

        var subTaskViews = rowSubTasks.Select(s =>
        {
            var cloned = (JsonObject)s.DeepClone();
            cloned["plainLine"] = PlainLines.StepLine(s).Line;
            return new PlainSubTask(
                StringOrNull(s["id"]) ?? "",
                StringOrNull(s["title"]) ?? "",
                StringOrNull(s["status"]) ?? "",
                StringOrNull(s["resultId"]),
                cloned);
        }).ToArray();

        var resultSummaries = !string.IsNullOrEmpty(entry.MegaTaskId)
            ? execResults.Where(r => StringOrNull(r["megaTaskId"]) == entry.MegaTaskId).Select(r => new PlainResultSummary(
                StringOrNull(r["attemptId"]) ?? "",
                StringOrNull(r["status"]) ?? "",
                ClipSummary(StringOrNull(r["summary"]) ?? ""),
                (JsonObject)r.DeepClone())).ToArray()
            : [];

        var openDecisions = OpenDecisionsOf(item.Id, entry.MegaTaskId, executionObj);
        var itemJson = NotebookItemJson(item);
        var nextText = ExecutionNext.GetHomeNext(itemJson, row, executionObj);
        if (string.IsNullOrEmpty(nextText)) nextText = entry.Next;

        var resumableCheckpoints = (executionObj?["checkpoints"] as JsonArray)?.OfType<JsonObject>()
            .Where(cp => StringOrNull(cp["projectId"]) == item.Id &&
                execMegas.FirstOrDefault(m => StringOrNull(m["id"]) == StringOrNull(cp["megaTaskId"])) is JsonObject targetMega &&
                StringOrNull(targetMega["status"]) is "PAUSED" or "FAILED")
            .Select(cp => new CheckpointRecord(StringOrNull(cp["path"]) ?? StringOrNull(cp["id"])))
            .ToArray() ?? [];

        var workerAlive = !terminal && (dispatch.Count > 0
            ? dispatch.Any(part => part.Alive)
            : (entry.WorkerPid is int wp && (_collaborators.IsProcessAlive?.Invoke(wp.ToString()) ?? entry.WorkerAlive)));

        var aliveCount = dispatch.Count > 0 ? dispatch.Count(p => p.Alive) : (workerAlive ? 1 : 0);
        MonitoringMetrics? monitoringView = null;
        if (entry.Monitoring is not null)
        {
            var rawMon = entry.Monitoring.Raw is not null ? (JsonObject)entry.Monitoring.Raw.DeepClone() : new JsonObject();
            rawMon["alive"] = aliveCount;
            monitoringView = entry.Monitoring with { Alive = aliveCount, Raw = rawMon };
        }

        var megaStatus = StringOrNull(row["megaTask"]?["status"])?.ToUpperInvariant() ?? "";
        var canResume = entry.Status != "complete" &&
            row["checkpoint"] is JsonObject cpObj &&
            megaStatus != "RUNNING" &&
            !dispatch.Any(p => p.Alive) &&
            (item.Tasks?.Any(t => t.Status == "running" && CheckpointMatches(cpObj, t)) == true);
        var canStop = entry.Status == "watching" &&
            (item.Tasks?.Any(t => t.Status == "running") == true);

        var isLegacyPhase2Complete = _collaborators.Phase2 && string.IsNullOrEmpty(entry.MegaTaskId) && entry.Status == "complete";

        return new ProjectSnapshot
        {
            Id = item.Id,
            Name = item.Name,
            Folder = item.Folder,
            Github = item.Github,
            Repository = repository,
            Goal = goal ?? "",
            GoalStarted = goalStarted,
            ChatMode = chatMode,
            SavingMode = savingMode,
            Notes = (item.Notes ?? []).ToArray(),
            StateArt = !string.IsNullOrEmpty(latestRevision?.State) ? latestRevision.State : item.StateArt,
            Open = (latestRevision?.Open ?? item.Open ?? []).ToArray(),
            Status = entry.Status,
            Label = isLegacyPhase2Complete ? "Legacy completion recorded" : StatusLabel(entry.Status),
            Doing = isLegacyPhase2Complete ? "Historical completion recorded. This run predates structured Phase 2 results." : entry.Doing,
            Plan = entry.Plan.ToArray(),
            Facts = entry.Facts,
            GoldModel = (item.Tasks?.FirstOrDefault(t => t.Id == entry.JobId)?.GoldModel?.Id) ?? task?.GoldModel?.Id,
            JobTitle = entry.JobTitle ?? task?.Title,
            JobId = entry.JobId ?? task?.Id,
            AttemptId = entry.AttemptId,
            MegaTaskId = entry.MegaTaskId,
            MegaTask = entry.MegaTask,
            SubTasks = subTaskViews,
            Plain = new PlainWorkSummary(entry.Status, plainSummary, plainWork),
            Results = resultSummaries,
            CheckpointId = StringOrNull(row["checkpoint"]?["id"]) ?? entry.Checkpoint?.Path,
            ResumableCheckpoints = resumableCheckpoints,
            ModelId = entry.ModelId,
            WorkerLabel = entry.WorkerLabel,
            WaitingFor = entry.WaitingFor,
            OpenDecisions = openDecisions,
            Next = nextText,
            Step = StepOf(row),
            Pipeline = PipelineOf(row),
            LeadLabel = leadLabel,
            Crew = crew.ToArray(),
            ResultPath = entry.ResultPath,
            WatchingSince = entry.WatchingSince,
            Approved = entry.Approved,
            SessionId = entry.AuthorizedSessionId,
            CanStart = true,
            CanResume = canResume,
            CanStop = canStop,
            PendingAutoStart = entry.PendingAutoStart is not null,
            Dispatch = dispatch.ToArray(),
            ActivitySessionPagination = new(true, all.Count, ActivitySessions.SessionLimit),
            ActivitySessions = sessions.Take(ActivitySessions.SessionLimit).ToArray(),
            Checkpoint = entry.Checkpoint,
            Timing = task?.Timing ?? entry.PendingAutoStart?.Timing,
            DeadlineAt = task?.DeadlineAt ?? entry.PendingAutoStart?.DeadlineAt,
            CanGrillDocs = docsAvailability is "available" or "unknown",
            DocsAvailability = docsAvailability,
            Docs = docs,
            GrillSkipped = entry.GrillSkipped,
            Log = entry.Log.ToArray(),
            WorkerPid = entry.WorkerPid,
            WorkerAlive = workerAlive,
            WorkerLog = entry.WorkerLog,
            WorkerTail = entry.WorkerTail,
            WorkerActivity = entry.WorkerActivity,
            OrchestratorDoing = orchDoing,
            OrchestratorId = leadModel?.Id,
            OrchestratorLabel = leadModel?.Label ?? OrchestratorDefaultName,
            Monitoring = monitoringView
        };
    }

    private ActivitySession? SessionFor(string projectId, JsonObject row, JsonObject part, bool historical, int index)
    {
        var session = ActivitySessions.AssignmentSession(new JsonObject
        {
            ["projectId"] = projectId,
            ["row"] = (JsonObject)row.DeepClone(),
            ["part"] = (JsonObject)part.DeepClone(),
            ["execution"] = ObjectOrEmpty(_store["execution"]),
            ["historical"] = historical,
            ["index"] = index,
            ["tail"] = ActivitySessions.ReadRecordedTail(StringOrNull(part["logPath"]), _dir)
        });
        return session is null ? null : ActivitySessionFromJson(session);
    }

    private JsonObject? ReadDiagnostic(string? resultPath)
    {
        if (string.IsNullOrEmpty(resultPath) || string.IsNullOrEmpty(_dir)) return null;
        try
        {
            var rootCanonical = ProjectWorkspace.LinkedWorkspace(_dir) ?? Path.GetFullPath(_dir);
            var file = Path.GetFullPath(resultPath + ".diagnostic.json");
            if (!ProjectWorkspace.Within(_dir, file) || !File.Exists(file)) return null;
            var parentCanonical = ProjectWorkspace.LinkedWorkspace(Path.GetDirectoryName(file));
            if (parentCanonical is null || !ProjectWorkspace.Within(rootCanonical, parentCanonical)) return null;
            var fileInfo = new FileInfo(file);
            if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null || !ProjectWorkspace.Within(rootCanonical, target.FullName)) return null;
            }
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

    private DecisionsInbox DecisionsFromStore(IReadOnlyList<NotebookProject> projects)
    {
        var executionDecisions = (_store["execution"]?["decisions"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var pipelineJobs = (_store["pipelineJobs"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (executionDecisions.Length == 0 && pipelineJobs.Length == 0 && _store["decisionsInbox"] is JsonObject source)
        {
            static IReadOnlyList<DecisionRecord> Parse(JsonNode? node) => (node as JsonArray)?.OfType<JsonObject>().Select(d => new DecisionRecord(
                StringOrNull(d["id"]) ?? "", StringOrNull(d["text"]) ?? "", StringOrNull(d["projectId"]),
                StringOrNull(d["suggestion"]), BoolOrFalse(d["breaks"]), StringOrNull(d["answer"]), (JsonObject)d.DeepClone())).ToArray() ?? [];
            var openLegacy = Parse(source["open"]);
            var answeredLegacy = Parse(source["answered"]);
            return new DecisionsInbox(openLegacy, answeredLegacy, IntOrNull(source["openCount"]) ?? openLegacy.Count);
        }

        var megaTasks = (_store["execution"]?["megaTasks"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var subTasks = (_store["execution"]?["subTasks"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        var projectMap = projects.ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);
        var all = new List<(DecisionRecord Record, bool Breaks, string SortTime)>();

        foreach (var d in executionDecisions)
        {
            var projId = StringOrNull(d["projectId"]) ?? "";
            var megaId = StringOrNull(d["megaTaskId"]) ?? "";
            var mega = megaTasks.FirstOrDefault(m => StringOrNull(m["id"]) == megaId);
            var projName = projectMap.TryGetValue(projId, out var p) ? p.Name : (string.IsNullOrEmpty(projId) ? "Project" : projId);
            var workTitle = StringOrNull(mega?["title"]) ?? StringOrNull(mega?["goal"]) ?? (string.IsNullOrEmpty(megaId) ? "Work" : megaId);
            var stepStarted = !string.IsNullOrEmpty(megaId) && subTasks.Any(s =>
                StringOrNull(s["megaTaskId"]) == megaId && StringOrNull(s["status"]) is "RUNNING" or "COMPLETE");
            var at = StringOrNull(d["at"]) ?? _now();
            var answer = StringOrNull(d["answer"]) ?? "";
            var answeredAt = StringOrNull(d["answeredAt"]);
            var raw = (JsonObject)d.DeepClone();
            raw["projectName"] = projName;
            raw["workId"] = megaId;
            raw["workTitle"] = workTitle;
            raw["isPipeline"] = false;
            raw["stepStarted"] = stepStarted;
            var rec = new DecisionRecord(
                StringOrNull(d["id"]) ?? "",
                StringOrNull(d["text"]) ?? "",
                projId,
                StringOrNull(d["suggestion"]) ?? "",
                BoolOrFalse(d["breaks"]),
                string.IsNullOrEmpty(answer) ? null : answer,
                raw);
            all.Add((rec, BoolOrFalse(d["breaks"]), answeredAt ?? at));
        }

        foreach (var job in pipelineJobs)
        {
            var projId = StringOrNull(job["projectId"]) ?? "";
            var jobId = StringOrNull(job["id"]) ?? "";
            var projName = projectMap.TryGetValue(projId, out var p) ? p.Name : (string.IsNullOrEmpty(projId) ? "Pipeline" : projId);
            var workTitle = StringOrNull(job["title"]) ?? (string.IsNullOrEmpty(jobId) ? "Pipeline job" : jobId);
            var stepStarted = StringOrNull(job["status"]) is "specifying" or "building" or "merged";
            foreach (var q in (job["questions"] as JsonArray)?.OfType<JsonObject>() ?? [])
            {
                var qId = StringOrNull(q["id"]) ?? "";
                var at = StringOrNull(q["at"]) ?? StringOrNull(job["updatedAt"]) ?? StringOrNull(job["createdAt"]) ?? _now();
                var answer = StringOrNull(q["answer"]) ?? "";
                var answeredAt = StringOrNull(q["answeredAt"]);
                var raw = (JsonObject)q.DeepClone();
                raw["id"] = $"pipeline:{jobId}:{qId}";
                raw["projectId"] = projId;
                raw["projectName"] = projName;
                raw["workId"] = jobId;
                raw["workTitle"] = workTitle;
                raw["isPipeline"] = true;
                raw["stepStarted"] = stepStarted;
                var rec = new DecisionRecord(
                    $"pipeline:{jobId}:{qId}",
                    StringOrNull(q["text"]) ?? "",
                    projId,
                    StringOrNull(q["suggestion"]) ?? "",
                    BoolOrFalse(q["breaks"]),
                    string.IsNullOrEmpty(answer) ? null : answer,
                    raw);
                all.Add((rec, BoolOrFalse(q["breaks"]), answeredAt ?? at));
            }
        }

        static long ParseTimeMs(string s) => DateTimeOffset.TryParse(s, out var dto) ? dto.ToUnixTimeMilliseconds() : 0;
        var sorted = all
            .OrderByDescending(x => x.Breaks)
            .ThenByDescending(x => ParseTimeMs(x.SortTime))
            .Select(x => x.Record)
            .ToArray();
        var open = sorted.Where(r => string.IsNullOrEmpty(r.Answer)).ToArray();
        var answered = sorted.Where(r => !string.IsNullOrEmpty(r.Answer)).ToArray();
        return new DecisionsInbox(open, answered, open.Length);
    }

    private IReadOnlyList<ResearchRecord> ResearchFromStore()
    {
        return (_store["researches"] as JsonArray)?.OfType<JsonObject>().Select(r =>
        {
            var dispatch = (r["dispatch"] as JsonArray)?.OfType<JsonObject>().Select(part => new DispatchView(
                StringOrNull(part["subTaskId"]),
                StringOrNull(part["megaTaskId"]),
                StringOrNull(part["attemptId"]),
                StringOrNull(part["modelId"]),
                StringOrNull(part["label"]),
                StringOrNull(part["slice"]),
                StringOrNull(part["folder"]),
                StringOrNull(part["resultPath"]),
                StringOrNull(part["logPath"]),
                IntOrNull(part["pid"]),
                BoolOrFalse(part["alive"]),
                BoolOrFalse(part["stoppedByUser"]),
                StringOrNull(part["activity"]),
                Diagnostic: ReadDiagnostic(StringOrNull(part["resultPath"])))).ToArray();
            return new ResearchRecord(
                StringOrNull(r["id"]) ?? "", StringOrNull(r["projectId"]) ?? "", StringOrNull(r["megaTaskId"]), StringOrNull(r["attemptId"]),
                StringOrNull(r["modelId"]), StringOrNull(r["provider"]), StringOrNull(r["cli"]), StringOrNull(r["status"]) ?? "",
                StringOrNull(r["doing"]), StringOrNull(r["activity"]), StringOrNull(r["reason"]), StringOrNull(r["folder"]),
                StringOrNull(r["reportPath"]), StringOrNull(r["resultPath"]), StringOrNull(r["logPath"]), IntOrNull(r["pid"]),
                BoolOrFalse(r["alive"]), BoolOrFalse(r["reportAvailable"]), StringOrNull(r["workerTail"]) ?? "",
                StringOrNull(r["sessionId"]), StringOrNull(r["startedAt"]) ?? "", StringOrNull(r["updatedAt"]) ?? "",
                IntOrNull(r["executionVersion"]) ?? 2, dispatch, ReadDiagnostic(StringOrNull(r["resultPath"])));
        }).ToArray() ?? [];
    }

    private string? ResolveLeadId(string? projectId)
    {
        if (!string.IsNullOrEmpty(projectId) && _store["projectTeams"] is JsonObject teams && teams[projectId] is JsonObject team)
        {
            return StringOrNull(team["orchestratorId"]);
        }
        return StringOrNull(_store["orchestratorId"]);
    }

    private static ModelInfo? ResolveLeadModel(IReadOnlyList<ModelInfo> models, string? leadId)
    {
        if (string.IsNullOrEmpty(leadId)) return null;
        var model = models.FirstOrDefault(m => m.Id == leadId);
        if (model is null) return null;
        if (!model.Enabled || !model.Available) return null;
        if (model.Raw is not null && !OrchestratorTeamModels.CanOrchestrate(model.Raw)) return null;
        return model;
    }

    private string ProjectLeadLabel(string projectId, ModelInfo? sharedLead, IReadOnlyList<ModelInfo> models)
    {
        if (_store["projectTeams"] is JsonObject teams && teams[projectId] is JsonObject team)
        {
            var ownId = StringOrNull(team["orchestratorId"]);
            if (!string.IsNullOrEmpty(ownId))
            {
                var ownModel = models.FirstOrDefault(m => m.Id == ownId);
                return ownModel?.Label ?? ownId;
            }
            return OrchestratorDefaultName;
        }
        return sharedLead?.Label ?? OrchestratorDefaultName;
    }

    private IReadOnlyDictionary<string, object> ThinkingByProjectFromStore()
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        if (_store["thinkingByKey"] is JsonObject obj)
        {
            foreach (var (key, val) in obj)
            {
                if (val is not null) result[key] = val.DeepClone();
            }
        }
        return result;
    }

    private IReadOnlyDictionary<string, object> LiveThinkingFromStore(IReadOnlyDictionary<string, object> byProject, string? projectId)
    {
        var key = !string.IsNullOrEmpty(projectId) ? projectId : StringOrNull(_store["selectedProjectId"]);
        object? selected = null;
        if (!string.IsNullOrEmpty(key) && byProject.TryGetValue(key, out var found))
        {
            selected = found;
        }
        else if (string.IsNullOrEmpty(projectId) && byProject.Count > 0)
        {
            selected = byProject.Values
                .OfType<JsonObject>()
                .OrderByDescending(o => StringOrNull(o["startedAt"]) ?? "", StringComparer.Ordinal)
                .FirstOrDefault();
        }
        var dict = new Dictionary<string, object>(StringComparer.Ordinal);
        if (selected is JsonObject selectedObj)
        {
            foreach (var (propKey, propVal) in selectedObj)
            {
                if (propVal is not null) dict[propKey] = propVal.DeepClone();
            }
        }
        return dict;
    }

    private (IReadOnlyDictionary<string, ToolSettings> Tools, IReadOnlyDictionary<string, ToolSettings> Settings) ProjectToolsFromStore(string? projectId)
    {
        var tools = new Dictionary<string, ToolSettings>(StringComparer.Ordinal);
        var settings = new Dictionary<string, ToolSettings>(StringComparer.Ordinal);
        var raw = _store["projectTools"] as JsonObject;
        var keys = !string.IsNullOrEmpty(projectId)
            ? [projectId]
            : (raw?.Select(p => p.Key).ToArray() ?? []);

        foreach (var id in keys)
        {
            var normalized = NormalizeToolSettingsObject(raw?[id]);
            settings[id] = new ToolSettings((JsonObject)normalized.DeepClone());
            tools[id] = new ToolSettings(ToolFlagsObject(normalized["default"] as JsonObject));
        }
        return (tools, settings);
    }

    private static IReadOnlyList<DecisionRecord> OpenDecisionsOf(string projectId, string? megaTaskId, JsonObject? execution)
    {
        if (string.IsNullOrEmpty(megaTaskId) || execution is null) return [];
        var mega = (execution["megaTasks"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(m => StringOrNull(m["id"]) == megaTaskId);
        if (StringOrNull(mega?["status"]) == "CANCELLED") return [];

        return (execution["decisions"] as JsonArray)?.OfType<JsonObject>()
            .Where(d => StringOrNull(d["projectId"]) == projectId &&
                        StringOrNull(d["megaTaskId"]) == megaTaskId &&
                        string.IsNullOrEmpty(StringOrNull(d["answer"])))
            .Select(d => new DecisionRecord(
                StringOrNull(d["id"]) ?? "",
                StringOrNull(d["text"]) ?? "",
                projectId,
                StringOrNull(d["suggestion"]),
                BoolOrFalse(d["breaks"]),
                null,
                (JsonObject)d.DeepClone()))
            .ToArray() ?? [];
    }

    private static string ClipSummary(string value)
    {
        if (value.Length <= 600) return value;
        var decisions = value.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(line => Regex.IsMatch(line, @"needs your decision:", RegexOptions.IgnoreCase))
            .Select(line => line.Trim())
            .ToArray();
        var slice = value[..600];
        var body = Regex.Replace(slice, @"\s*\S*$", "");
        body = Regex.Replace(body, @"\n[^\n]*needs your deci[^\n]*$", "", RegexOptions.IgnoreCase);
        body += "…";
        var parts = new List<string> { body };
        foreach (var d in decisions)
        {
            if (!body.Contains(d, StringComparison.Ordinal)) parts.Add(d);
        }
        return string.Join("\n", parts);
    }

    private static DocInfo[] CollectDocsFromFactsObject(JsonObject? facts)
    {
        if (facts is null) return [];
        return OrchestratorLeadQuestions.CollectDocs(new OrchestratorLeadQuestions.Facts(facts))
            .Select(path => new DocInfo(path))
            .ToArray();
    }

    private IReadOnlyList<AskRecord> AsksFromStore()
    {
        return (_store["asks"] as JsonArray)?.OfType<JsonObject>().Select(a => new AskRecord(
            StringOrNull(a["id"]) ?? "", StringOrNull(a["projectId"]) ?? "", StringOrNull(a["question"]) ?? "",
            StringOrNull(a["answer"]), StringOrNull(a["at"]), (JsonObject)a.DeepClone())).ToArray() ?? [];
    }

    public async Task<OrchestratorStateSnapshot> TickAsync(string? projectId = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gateAcquired = await WaitGateIfNeededAsync(cancellationToken).ConfigureAwait(false);
        using var _ = PushGateScope(gateAcquired);
        cancellationToken.ThrowIfCancellationRequested();

        if (_collaborators.RestoreUnavailableModels is not null) _collaborators.RestoreUnavailableModels();
        else RestoreUnavailableModels();
        if (_collaborators.RefreshPlans is not null)
            await _collaborators.RefreshPlans(false, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var plan = _collaborators.ReadPlan is null ? new NotebookPlan([])
            : await _collaborators.ReadPlan(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        RememberStartup(plan);
        _collaborators.RefreshRepositoryMetadata?.Invoke(plan, projectId);
        if (!IsShuttingDown)
        {
            foreach (var item in (plan.Projects ?? []).Where(p => string.IsNullOrEmpty(projectId) || p.Id == projectId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsShuttingDown) break;
                var context = new OrchestratorTickProject(item, EntryRow(item.Id),
                    _startupProjects!.TryGetValue(item.Id, out var initial) && initial.Contains(item.Tasks?.FirstOrDefault(t => t.Status == "running")?.Id ?? ""),
                    _sessionId, cancellationToken);
                if (_collaborators.Decide is not null) _collaborators.Decide(context);
                else DecideProject(context);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (_collaborators.RefreshPhaseTwoResearch is not null)
            _collaborators.RefreshPhaseTwoResearch(plan, !IsShuttingDown, projectId);
        else RefreshPhaseTwoResearch(plan, !IsShuttingDown, projectId);
        if (_collaborators.RefreshResearch is not null) _collaborators.RefreshResearch();
        else RefreshResearch();
        cancellationToken.ThrowIfCancellationRequested();
        if (_collaborators.CompleteTasks is not null)
        {
            var completed = CompletedTasks(plan, projectId);
            if (completed.Count > 0)
                plan = await _collaborators.CompleteTasks(completed, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Persist();
        return Snapshot(plan, projectId);
    }

    private bool IsShuttingDown => _collaborators.IsShuttingDown?.Invoke() == true;

    private void RememberStartup(NotebookPlan plan) => _startupProjects ??= plan.Projects.ToDictionary(
        p => p.Id, p => (p.Tasks ?? []).Where(t => t.Status == "running").Select(t => t.Id).ToHashSet(StringComparer.Ordinal),
        StringComparer.Ordinal);

    private void RestoreUnavailableModels()
    {
        if (!DateTimeOffset.TryParse(_now(), out var now)) return;
        foreach (var model in (_store["models"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!ModelPolicy.AvailableAgain(model, now.ToUnixTimeMilliseconds())) continue;
            model["available"] = true;
            model["unavailableReason"] = null;
            model["unavailableAt"] = null;
        }
    }

    private ExecutionBridge? SharedBridge()
    {
        var bridge = _executionBridge ?? _collaborators.ExecutionBridge ?? _collaborators.Bridge?.Invoke();
        if (bridge is not null && !ReferenceEquals(bridge.Options.Store, _store))
            throw new ArgumentException(null, nameof(ExecutionOptions.Store));
        return bridge;
    }

    private void RequireSharedRow(ExecutionBridge bridge, string projectId, string? megaTaskId)
    {
        if (!ReferenceEquals(bridge.Options.RowFor(projectId, megaTaskId), RowFor(projectId, megaTaskId)))
            throw new ArgumentException(null, nameof(ExecutionOptions.RowFor));
    }

    private static JsonObject NotebookTaskJson(NotebookTask task)
    {
        var raw = task.Raw is null ? new JsonObject() : (JsonObject)task.Raw.DeepClone();
        raw["id"] = task.Id; raw["title"] = task.Title; raw["detail"] = task.Detail;
        raw["status"] = task.Status; raw["executionVersion"] = task.ExecutionVersion;
        // Keep numeric revision IDs and Gold model field ordering from the notebook.
        if (!raw.ContainsKey("revision_id")) raw["revision_id"] = task.RevisionId;
        if (!raw.ContainsKey("timing")) raw["timing"] = task.Timing;
        if (!raw.ContainsKey("deadlineAt")) raw["deadlineAt"] = task.DeadlineAt;
        if (!raw.ContainsKey("source") && !string.IsNullOrEmpty(task.Source)) raw["source"] = task.Source;
        if (!raw.ContainsKey("goldModel") && task.GoldModel is not null)
        {
            raw["goldModel"] = new JsonObject
            {
                ["id"] = task.GoldModel.Id,
                ["label"] = task.GoldModel.Label,
                ["provider"] = task.GoldModel.Provider
            };
        }
        return raw;
    }

    private static JsonObject NotebookItemJson(NotebookProject item)
    {
        var raw = item.Raw is null ? new JsonObject() : (JsonObject)item.Raw.DeepClone();
        if (raw["project"] is not JsonObject) raw["project"] = new JsonObject();
        var project = (JsonObject)raw["project"]!;
        project["id"] = item.Id; project["name"] = item.Name; project["folder"] = item.Folder; project["github"] = item.Github;
        if (raw["revisions"] is not JsonArray)
        {
            raw["revisions"] = new JsonArray((item.Revisions ?? []).Select((r, idx) => (JsonNode)new JsonObject
            {
                ["id"] = r.Id ?? (idx + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["title"] = r.Title,
                ["mode"] = r.Mode,
                ["direction"] = r.Direction,
                ["state"] = r.State,
                ["open"] = new JsonArray((r.Open ?? []).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["closed"] = new JsonArray((r.Closed ?? []).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
            }).ToArray());
        }
        if (raw["home"] is not JsonObject && item.Notes is not null)
        {
            raw["home"] = new JsonObject { ["notes"] = new JsonArray(item.Notes.Select(n => (JsonNode)new JsonObject { ["text"] = n }).ToArray()) };
        }
        raw["tasks"] = new JsonArray((item.Tasks ?? []).Select(t => (JsonNode?)NotebookTaskJson(t)).ToArray());
        return raw;
    }

    public static string JobFingerprint(NotebookTask task)
    {
        var job = NotebookTaskJson(task);
        var revision = job["revision_id"];
        var input = new JsonArray(task.Id, task.Title, task.Detail,
            Execution.Truthy(revision) ? revision?.DeepClone() : null);
        if (Execution.Truthy(job["goldModel"])) input.Add(job["goldModel"]!.DeepClone());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToJsonString(JsonSerializationOptions))));
    }

    private string ResultText(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        try { return _collaborators.ReadResult?.Invoke(path) ?? File.ReadAllText(path, Encoding.UTF8); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    private static string ResearchTail(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var raw = File.ReadAllText(path, Encoding.UTF8);
            return raw[Math.Max(0, raw.Length - 1600)..].Replace("\r", "", StringComparison.Ordinal).Trim();
        }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    public void RefreshResearch()
    {
        using var _ = EnterGate();
        var execResults = (_store["execution"]?["results"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        foreach (var record in (_store["researches"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (IntOrNull(record["executionVersion"]) == 2)
            {
                var dispatch = (record["dispatch"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
                var part = dispatch.LastOrDefault();
                record["alive"] = dispatch.Any(p => Execution.Truthy(p["alive"]));
                record["pid"] = Execution.Truthy(part?["pid"]) ? part!["pid"]!.DeepClone() : null;
                record["folder"] = Execution.Truthy(part?["folder"]) ? part!["folder"]!.DeepClone() : null;
                if (Execution.Truthy(part?["sessionId"])) record["sessionId"] = part!["sessionId"]!.DeepClone();
                record["logPath"] = Execution.Truthy(part?["logPath"]) ? part!["logPath"]!.DeepClone() : null;
                record["workerTail"] = part is not null ? ResearchTail(StringOrNull(part["logPath"])) : "";
                var megaTaskId = StringOrNull(record["megaTaskId"]);
                var result = !string.IsNullOrEmpty(megaTaskId)
                    ? execResults.LastOrDefault(r => StringOrNull(r["megaTaskId"]) == megaTaskId)
                    : null;
                var report = StringOrNull(result?["reportPath"]);
                record["reportPath"] = string.IsNullOrEmpty(report) ? null : report;
                record["reportAvailable"] = !string.IsNullOrEmpty(report) && File.Exists(report);
                record["activity"] = StringOrNull(result?["summary"]) is { Length: > 0 } summary ? summary
                    : StringOrNull(record["doing"]) is { Length: > 0 } doing ? doing : "Awaiting MegaTask approval.";
                continue;
            }
            var pid = IntOrNull(record["pid"]);
            var alive = pid is not null && pid != 0 && (_collaborators.IsProcessAlive is not null
                ? _collaborators.IsProcessAlive(pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                : StringOrNull(record["status"]) == "watching");
            record["alive"] = alive;
            var tail = ResearchTail(StringOrNull(record["logPath"]));
            if (tail != StringOrNull(record["workerTail"])) record["updatedAt"] = _now();
            record["workerTail"] = tail;
            record["activity"] = Activity.InterpretActivity(tail, null, alive);
            var reportPath = StringOrNull(record["reportPath"]);
            var available = false;
            try
            {
                available = !string.IsNullOrEmpty(reportPath) &&
                    new FileInfo(reportPath) is { Exists: true, Length: > 0 } fi &&
                    (fi.Attributes & FileAttributes.Directory) == 0;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            record["reportAvailable"] = available;
            if (StringOrNull(record["status"]) != "watching") continue;
            var resultPath = StringOrNull(record["resultPath"]);
            var status = ResultStatus(resultPath);
            var success = Regex.IsMatch(ResultText(resultPath), @"(^|\n)\s*EXIT:\s*0\s*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
            string? next = null, reason = null;
            if (status == "COMPLETE" && success)
            {
                next = available ? "complete" : "failed";
                reason = available ? "Report and successful provider exit recorded." : "Provider exited successfully without the required report.md.";
            }
            else if (status == "FAILED") { next = "failed"; reason = "Research provider or runner failed; inspect the log and result evidence."; }
            else if (pid is not null and not 0 && _collaborators.IsProcessAlive is not null && !alive)
            { next = "failed"; reason = "Research process exited without successful completion evidence."; }
            if (next is null) continue;
            record["status"] = next; record["reason"] = reason; record["updatedAt"] = _now();
            if (StringOrNull(record["attemptId"]) is { Length: > 0 } attemptId)
                CloseAttemptSafe(attemptId, !string.IsNullOrEmpty(reason) ? reason : next);
        }
    }

    private void CloseAttemptSafe(string attemptId, string reason)
    {
        var input = new JsonObject { ["attemptId"] = attemptId, ["reason"] = reason };
        if (_collaborators.AttemptAdapter is not null)
        {
            _collaborators.AttemptAdapter.CloseAttempt(input);
        }
        else
        {
            _collaborators.Attempts?.CloseAttempt(input);
        }
    }

    private string ResultStatus(string? path)
    {
        var raw = ResultText(path);
        foreach (var status in new[] { "COMPLETE", "FAILED", "BLOCKED", "WATCHING" })
            if (Regex.IsMatch(raw, @"(^|\n)\s*STATUS:\s*" + status + @"\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)) return status;
        return raw.Length == 0 ? "MISSING" : "OTHER";
    }

    private static IReadOnlyList<string> ResultFiles(JsonObject row)
    {
        var files = (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => StringOrNull(p["resultPath"])).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (files.Count == 0 && StringOrNull(row["resultPath"]) is { Length: > 0 } file) files.Add(file);
        return files;
    }

    private IReadOnlyList<CompletedNotebookTask> CompletedTasks(NotebookPlan plan, string? projectId)
    {
        var completed = new List<CompletedNotebookTask>();
        foreach (var item in plan.Projects.Where(p => projectId is null || p.Id == projectId))
        {
            var row = EntryRow(item.Id);
            var job = item.Tasks?.FirstOrDefault(t => t.Status == "running" && t.Id == StringOrNull(row["jobId"]));
            if (job is null || StringOrNull(row["status"]) != "complete" || StringOrNull(row["jobFingerprint"]) != JobFingerprint(job)) continue;
            var files = ResultFiles(row);
            var evidence = job.ExecutionVersion == 2
                ? StringOrNull(SharedBridge()?.Find(item.Id, job.Id)?["status"]) == "COMPLETE"
                : files.Count > 0 && files.All(file => ResultStatus(file) == "COMPLETE");
            if (evidence) completed.Add(new(item.Id, job.Id, JobFingerprint(job), StringOrNull(row["attemptId"]), StringOrNull(row["megaTaskId"])));
        }
        return completed;
    }

    private void RefreshPhaseTwoResearch(NotebookPlan plan, bool schedule, string? projectId)
    {
        var bridge = SharedBridge();
        if (bridge is null) return;
        foreach (var record in (_store["researches"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(r => IntOrNull(r["executionVersion"]) == 2 && (projectId is null || StringOrNull(r["projectId"]) == projectId)))
        {
            var item = plan.Projects.FirstOrDefault(p => p.Id == StringOrNull(record["projectId"]));
            if (item is null || record["job"] is not JsonObject job) continue;
            RequireSharedRow(bridge, item.Id, StringOrNull(record["megaTaskId"]));
            bridge.Advance(NotebookItemJson(item), job, [], schedule);
        }
    }

    private void DecideProject(OrchestratorTickProject context)
    {
        var item = context.Project;
        var job = item.Tasks?.FirstOrDefault(t => t.Status == "running");
        if (job?.ExecutionVersion != 2)
        {
            ReconcileLegacyProject(context, job);
            return;
        }
        var bridge = SharedBridge();
        if (bridge is null) return;
        var existing = bridge.Find(item.Id, job.Id);
        RequireSharedRow(bridge, item.Id, StringOrNull(existing?["id"]));
        context.Row["jobId"] = job.Id; context.Row["jobTitle"] = job.Title;
        context.Row["jobFingerprint"] = JobFingerprint(job); context.Row["jobDetail"] = job.Detail;
        context.Row["jobRevisionId"] = NotebookTaskJson(job)["revision_id"]?.DeepClone();
        try
        {
            bridge.Advance(NotebookItemJson(item), NotebookTaskJson(job), _collaborators.DispatchParts?.Invoke(item, job) ?? []);
        }
        catch (InvalidOperationException error)
        {
            context.Row["status"] = "blocked_no_worker";
            context.Row["doing"] = error.Message;
            context.Row["next"] = error.Message;
        }
    }

    private bool ProcessAlive(JsonObject part) => IntOrNull(part["pid"]) is int pid &&
        (_collaborators.IsProcessAlive?.Invoke(pid.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? false);

    private void CloseLegacyAttempt(string projectId, string? attemptId, string reason, string? taskId = null)
    {
        var attempts = _collaborators.Attempts;
        var active = attempts?.GetActiveAttempt(new JsonObject { ["projectId"] = projectId, ["taskId"] = taskId });
        if (active is not null && (attemptId is null || StringOrNull(active["id"]) == attemptId))
            attempts!.CloseAttempt(new JsonObject { ["attemptId"] = active["id"]!.DeepClone(), ["reason"] = reason });
    }

    private void SettleLegacyCompletion(JsonObject row, string projectId, NotebookTask job)
    {
        row["workerAlive"] = false;
        foreach (var part in (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>()) part["alive"] = false;
        if (row["monitoring"] is not JsonObject) row["monitoring"] = new JsonObject();
        row["monitoring"]!["alive"] = 0;
        var files = ResultFiles(row);
        if (files.Count > 0 && files.All(f => ResultStatus(f) == "COMPLETE") && StringOrNull(row["attemptId"]) is { } id)
            CloseLegacyAttempt(projectId, id, "complete", job.Id);
    }

    private static bool CheckpointMatches(JsonObject checkpoint, NotebookTask job)
    {
        if (StringOrNull(checkpoint["jobId"]) is { Length: > 0 } id && id != job.Id) return false;
        if (StringOrNull(checkpoint["jobFingerprint"]) is { Length: > 0 } fingerprint) return fingerprint == JobFingerprint(job);
        if (StringOrNull(checkpoint["jobTitle"]) is { Length: > 0 } title && title != job.Title) return false;
        return StringOrNull(checkpoint["detail"]) is not { } detail || detail == job.Detail;
    }

    // Legacy worker selection, launching and stall fallback stay injectable with Decide.
    // The state-owned fallback reconciles saved evidence and never creates a new worker.
    private void ReconcileLegacyProject(OrchestratorTickProject context, NotebookTask? job)
    {
        var item = context.Project;
        var row = context.Row;
        var parts = (row["dispatch"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var completedJob = job ?? item.Tasks?.FirstOrDefault(t => t.Id == StringOrNull(row["jobId"]) && t.Status == "complete");
        var fingerprint = StringOrNull(row["jobFingerprint"]);
        if (StringOrNull(row["status"]) == "complete" && completedJob is not null &&
            StringOrNull(row["jobId"]) == completedJob.Id && (fingerprint is null || fingerprint == JobFingerprint(completedJob)))
        {
            SettleLegacyCompletion(row, item.Id, completedJob);
            return;
        }

        // Snapshot recovery in JavaScript validates a successful provider exit as well as
        // completion. Incomplete/dead old workers cannot silently become approved new work.
        if (StringOrNull(row["status"]) == "watching" && !Execution.Truthy(row["megaTaskId"]) && parts.Any(p => IntOrNull(p["pid"]) is not null))
        {
            if (_collaborators.Phase2)
            {
                ReconcileLegacyWatchingRow(item, row);
                return;
            }
            foreach (var part in parts) part["alive"] = ProcessAlive(part);
            row["workerAlive"] = parts.Any(p => BoolOrFalse(p["alive"]));
            if (!BoolOrFalse(row["workerAlive"]))
            {
                var complete = parts.All(p => ResultStatus(StringOrNull(p["resultPath"])) == "COMPLETE" &&
                    Regex.IsMatch(ResultText(StringOrNull(p["resultPath"])), @"^EXIT:\s*0\s*$", RegexOptions.Multiline | RegexOptions.ECMAScript));
                row["status"] = complete ? "complete" : "failed";
                row["holdApproval"] = true;
                row["doing"] = complete ? "Historical completion and successful provider exit recorded."
                    : "Historical worker ended without validated completion. Review the saved attempt before preparing new work.";
                if (StringOrNull(row["attemptId"]) is { } attemptId)
                    CloseLegacyAttempt(item.Id, attemptId, complete ? "legacy completion" : "technical failure: dead legacy worker without validated completion");
                return;
            }
        }

        if (_collaborators.Phase2 && job is null && !Execution.Truthy(row["megaTaskId"]) &&
            BoolOrFalse(row["holdApproval"]) && StringOrNull(row["status"]) is "complete" or "failed")
        {
            return;
        }

        if (job is not null && StringOrNull(row["jobId"]) == job.Id && fingerprint == JobFingerprint(job) &&
            !parts.Any(p => BoolOrFalse(p["stoppedByUser"])) && ResultFiles(row) is { Count: > 0 } files && files.All(f => ResultStatus(f) == "COMPLETE"))
        {
            row["status"] = "complete";
            row["doing"] = $"Evidence complete for {job.Title} on {item.Name}.";
            row["waitingFor"] = "Nothing. Evidence is complete.";
            row["next"] = "Read the result, or add a new job.";
            row["workerActivity"] = "Evidence complete.";
            row["orchestratorDoing"] = "Verified STATUS: COMPLETE and folder output.";
            SettleLegacyCompletion(row, item.Id, job);
            return;
        }

        if (job is not null && Execution.Truthy(row["jobId"]) &&
            (StringOrNull(row["jobId"]) != job.Id || fingerprint is not null && fingerprint != JobFingerprint(job)))
        {
            row["approved"] = false; row["holdApproval"] = false; row["attemptId"] = null; row["modelId"] = null;
            row["plan"] = new JsonArray(); row["dispatch"] = new JsonArray(); row["tried"] = new JsonArray(); row["resumeRequested"] = false;
            parts = [];
        }
        row["jobId"] = job?.Id; row["jobFingerprint"] = job is null ? null : JobFingerprint(job);
        row["jobTitle"] = job?.Title; row["jobDetail"] = job?.Detail; row["jobRevisionId"] = job?.RevisionId;

        if (job is not null)
        {
            var ongoing = parts.Any(p => !BoolOrFalse(p["stoppedByUser"]) && ProcessAlive(p));
            if (_collaborators.EnforceSessionBoundary && context.WasRunningAtStartup && StringOrNull(row["authorizedSessionId"]) != _sessionId && !ongoing)
            { row["approved"] = false; row["holdApproval"] = true; }
            if (row["checkpoint"] is not JsonObject && ReadCheckpointFile(item.Id) is { } disk && CheckpointMatches(disk, job))
                row["checkpoint"] = disk;
            if (row["checkpoint"] is JsonObject checkpoint && !CheckpointMatches(checkpoint, job)) row["checkpoint"] = null;
            if (row["checkpoint"] is JsonObject && !BoolOrFalse(row["resumeRequested"]) &&
                _collaborators.Attempts?.GetActiveAttempt(new JsonObject { ["projectId"] = item.Id }) is null)
            { PaintCheckpoint(row, item.Name, job.Title); return; }
            if (ongoing) { row["status"] = "watching"; row["workerAlive"] = true; return; }
            if (!BoolOrFalse(row["approved"]) || BoolOrFalse(row["holdApproval"]))
            { row["status"] = "needs_approval"; row["waitingFor"] = "Your Approve run"; }
            return;
        }

        CloseLegacyAttempt(item.Id, null, "no running job");
        row["workerPid"] = null; row["workerLog"] = null; row["workerAlive"] = false;
        row["tried"] = new JsonArray(); row["progressAt"] = null; row["progressKey"] = null;
        row["workerTail"] = ""; row["workerActivity"] = "";
        row["status"] = "idle";
        row["doing"] = Execution.Truthy(row["pendingAutoStart"]) ? $"Auto start is waiting to plan {item.Name}."
            : $"No job on {item.Name}. Tell the lead what to build, or let the crew do it.";
        row["waitingFor"] = "Auto start"; row["next"] = "Tell the lead what to build, or let the crew do it.";
        row["workerLabel"] = null; row["attemptId"] = null; row["watchingSince"] = null;
        row["facts"] = null; row["docs"] = new JsonArray(); row["dispatch"] = new JsonArray();
        row["approved"] = false; row["holdApproval"] = false;
    }

    public async Task<SetFocusResult> SetFocusAsync(string projectId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gateAcquired = await WaitGateIfNeededAsync(cancellationToken).ConfigureAwait(false);
        using var _ = PushGateScope(gateAcquired);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(projectId))
        {
            return new SetFocusResult(false, StringOrNull(_store["selectedProjectId"]), "Selected project no longer exists.");
        }

        if (_collaborators.ReadPlan is not null)
        {
            var plan = await _collaborators.ReadPlan(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!plan.Projects.Any(p => p.Id == projectId))
            {
                return new SetFocusResult(false, StringOrNull(_store["selectedProjectId"]), "Selected project no longer exists.");
            }
        }
        else if (_store["projects"] is not JsonObject projects || projects[projectId] is null)
        {
            return new SetFocusResult(false, StringOrNull(_store["selectedProjectId"]), "Selected project no longer exists.");
        }

        _store["selectedProjectId"] = projectId;
        Persist();
        return new SetFocusResult(true, projectId);
    }

    public async Task<ArchiveProjectResult> ArchiveProjectAsync(string projectId, string reason, ArchiveProjectOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(projectId)) return new ArchiveProjectResult(false, Reason: "Project not found.");
        var gateAcquired = await WaitGateIfNeededAsync(cancellationToken).ConfigureAwait(false);
        using var _ = PushGateScope(gateAcquired);
        cancellationToken.ThrowIfCancellationRequested();

        var plan = _collaborators.ReadPlan is null ? new NotebookPlan([])
            : await _collaborators.ReadPlan(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var targetProject = plan.Projects?.FirstOrDefault(p => p.Id == projectId);
        var hasAliveDispatch = false;
        if (targetProject is not null)
        {
            var snap = Snapshot(new NotebookPlan([targetProject]), projectId);
            hasAliveDispatch = snap.Projects.FirstOrDefault(p => p.Id == projectId)?.Dispatch.Any(p => p.Alive) == true;
        }
        else if (_store["projects"] is JsonObject projectsObj && projectsObj[projectId] is JsonObject existingRow)
        {
            var terminal = StringOrNull(existingRow["status"]) is "complete" or "failed" or "checkpointed";
            hasAliveDispatch = !terminal && ((existingRow["dispatch"] as JsonArray)?.OfType<JsonObject>().Any(part =>
            {
                var pid = IntOrNull(part["pid"]);
                return !BoolOrFalse(part["stoppedByUser"]) && pid is int pVal &&
                    (_collaborators.IsProcessAlive?.Invoke(pVal.ToString()) ?? BoolOrFalse(part["alive"]));
            }) == true);
        }

        if (hasAliveDispatch ||
            (_store["researches"] as JsonArray ?? []).OfType<JsonObject>().Any(r => StringOrNull(r["projectId"]) == projectId && StringOrNull(r["status"]) == "watching"))
            return new ArchiveProjectResult(false, Reason: "Stop the work on this project first.");

        var execution = _store["execution"] as JsonObject ?? new JsonObject();
        bool Mine(JsonObject item) => StringOrNull(item["projectId"]) == projectId;
        var megaIds = (execution["megaTasks"] as JsonArray ?? []).OfType<JsonObject>().Where(Mine)
            .Select(m => StringOrNull(m["id"])).ToHashSet(StringComparer.Ordinal);
        bool OfMega(JsonObject item) => megaIds.Contains(StringOrNull(item["megaTaskId"]));
        static JsonArray Select(JsonNode? source, Func<JsonObject, bool> predicate) =>
            new((source as JsonArray ?? []).OfType<JsonObject>().Where(predicate).Select(x => x.DeepClone()).ToArray());
        JsonNode? ProjectValue(string key) => (_store[key] as JsonObject)?[projectId]?.DeepClone();
        var archivedExecution = new JsonObject();
        foreach (var key in new[] { "megaTasks", "subTasks", "results", "checkpoints", "timeline" })
            archivedExecution[key] = Select(execution[key], key is "subTasks" or "results" ? OfMega : Mine);
        var state = new JsonObject
        {
            ["project"] = ProjectValue("projects"), ["autonomy"] = ProjectValue("autonomy"),
            ["team"] = ProjectValue("projectTeams"), ["tools"] = ProjectValue("projectTools"),
            ["briefingBaseline"] = ProjectValue("briefingBaselines"),
            ["asks"] = Select(_store["asks"], Mine), ["researches"] = Select(_store["researches"], Mine),
            ["workerHistory"] = Select(_store["workerHistory"], x => StringOrNull(x["id"]) == projectId),
            ["execution"] = archivedExecution
        };
        var at = _now();
        var archive = new JsonObject { ["projectId"] = projectId, ["reason"] = reason, ["at"] = at,
            ["notebook"] = options?.Notebook is null ? null : options.Notebook.Raw?.DeepClone() ?? NotebookItemJson(options.Notebook), ["state"] = state };
        if (_store["projectArchive"] is not JsonArray archiveArr)
        {
            archiveArr = new JsonArray();
            _store["projectArchive"] = archiveArr;
        }
        archiveArr.Add((JsonNode)archive);
        foreach (var key in new[] { "projects", "autonomy", "briefingBaselines" }) (_store[key] as JsonObject)?.Remove(projectId);
        if (options?.KeepTeam != true)
            foreach (var key in new[] { "projectTeams", "projectTools" }) (_store[key] as JsonObject)?.Remove(projectId);
        foreach (var key in new[] { "asks", "researches", "workerHistory" })
        {
            if (_store[key] is not JsonArray items) continue;
            Func<JsonObject, bool> remove = key == "workerHistory" ? x => StringOrNull(x["id"]) == projectId : Mine;
            for (var i = items.Count - 1; i >= 0; i--) if (items[i] is JsonObject item && remove(item)) items.RemoveAt(i);
        }
        foreach (var key in new[] { "megaTasks", "subTasks", "results", "checkpoints", "timeline" })
        {
            if (execution[key] is not JsonArray items) continue;
            Func<JsonObject, bool> remove = key is "subTasks" or "results" ? OfMega : Mine;
            for (var i = items.Count - 1; i >= 0; i--) if (items[i] is JsonObject item && remove(item)) items.RemoveAt(i);
        }
        (_store["thinkingByKey"] as JsonObject)?.Remove(projectId);
        Persist();
        return new ArchiveProjectResult(true, new ArchivedSummary(at, reason, ((JsonArray)state["asks"]!).Count, ((JsonArray)archivedExecution["megaTasks"]!).Count));
    }

    public Task<InspectRepositoryResult> InspectRepositoryAsync(string projectId, RepositoryInspectOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return InspectRepositoryCoreAsync(projectId, options, cancellationToken);
    }

    private async Task<InspectRepositoryResult> InspectRepositoryCoreAsync(string projectId, RepositoryInspectOptions? options, CancellationToken cancellationToken)
    {
        var gateAcquired = await WaitGateIfNeededAsync(cancellationToken).ConfigureAwait(false);
        using var _ = PushGateScope(gateAcquired);
        cancellationToken.ThrowIfCancellationRequested();
        NotebookProject? notebookProject = null;
        if (_collaborators.ReadPlan is not null)
        {
            var plan = await _collaborators.ReadPlan(cancellationToken).ConfigureAwait(false);
            notebookProject = plan.Projects.FirstOrDefault(p => p.Id == projectId);
            if (notebookProject is null)
                return new InspectRepositoryResult(false, Reason: "project not found");
        }
        else if (_store["projects"] is not JsonObject projects || projects[projectId] is null)
        {
            return new InspectRepositoryResult(false, Reason: "project not found");
        }
        var row = EntryRow(projectId);
        var folder = StringOrNull(row["folder"]) ?? StringOrNull(row["facts"]?["folder"]) ?? notebookProject?.Folder;
        var repository = _collaborators.InspectRepository is not null
            ? await _collaborators.InspectRepository(folder ?? "", options, cancellationToken).ConfigureAwait(false)
            : await InspectRepositoryDefaultAsync(folder, cancellationToken).ConfigureAwait(false);
        row["repository"] = RepositoryToJson(repository);
        if (!string.IsNullOrEmpty(folder))
        {
            row["facts"] = DefaultInspectJson(folder);
        }
        Persist();
        var snapshot = notebookProject is null ? null : Snapshot(new NotebookPlan([notebookProject]), projectId);
        return new InspectRepositoryResult(true, repository, snapshot, null);
    }

    private void ReconcileLegacyWatchingRow(NotebookProject item, JsonObject row)
    {
        var parts = (row["dispatch"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [];
        if (parts.Length == 0 || !parts.Any(p => IntOrNull(p["pid"]) is int pid && pid > 0)) return;

        foreach (var part in parts)
        {
            var pid = IntOrNull(part["pid"]);
            part["alive"] = pid is int pVal && pVal > 0 && (_collaborators.IsProcessAlive?.Invoke(pVal.ToString()) ?? false);
        }
        if (parts.Any(p => BoolOrFalse(p["alive"]))) return;

        var complete = parts.All(part =>
        {
            var rp = StringOrNull(part["resultPath"]);
            if (string.IsNullOrEmpty(rp)) return false;
            var raw = ResultText(rp);
            return ResultStatus(rp) == "COMPLETE" && Regex.IsMatch(
                raw, @"^EXIT:\s*0\s*$",
                RegexOptions.Multiline | RegexOptions.ECMAScript);
        });

        row["status"] = complete ? "complete" : "failed";
        row["workerAlive"] = false;
        row["holdApproval"] = true;
        row["doing"] = complete
            ? "Historical completion and successful provider exit recorded."
            : "Historical worker ended without validated completion. Review the saved attempt before preparing new work.";

        var attemptId = StringOrNull(row["attemptId"]);
        if (!string.IsNullOrEmpty(attemptId))
        {
            if (_collaborators.Attempts is not null)
            {
                var active = _collaborators.Attempts.GetResult(attemptId);
                if (StringOrNull(active?["status"]) == "active" && StringOrNull(active?["projectId"]) == item.Id)
                {
                    CloseAttemptSafe(attemptId, complete ? "legacy completion" : "technical failure: dead legacy worker without validated completion");
                }
            }
            else if (_collaborators.AttemptAdapter is not null)
            {
                CloseAttemptSafe(attemptId, complete ? "legacy completion" : "technical failure: dead legacy worker without validated completion");
            }
        }
        Persist();
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
