# Job 46d (phase 6): OrchestratorState specification

**Goal.** Specify the C# port of the orchestrator state management subsystem in `engine/orchestrator.js` into `native/Planwharf.Engine/OrchestratorState.cs` and `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs`. This is a specification sheet job under Phase 6. Migration Step: **Job 51** implements `OrchestratorState.cs`.

## Port and ownership

| JavaScript source | C# implementation | C# tests | Migration step |
| --- | --- | --- | --- |
| `engine/orchestrator.js` (state concerns) | `native/Planwharf.Engine/OrchestratorState.cs` (first line `// Port of engine/orchestrator.js state management.`) | `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs` | Job 51 |

## Responsibilities

`OrchestratorState` is the stateful container and single authority for orchestrator-level persistence, per-project state entries, view generation, checkpoint hydration, research tracking, and lifecycle transitions.

1. **`emptyEntry`**: Generates a clean default per-project entry dictionary containing all required attributes:
   - `status`: `'idle'`
   - `doing`: `''`
   - `plan`: `[]`
   - `facts`: `null`
   - `jobId`: `null`, `jobTitle`: `null`, `attemptId`: `null`, `modelId`: `null`
   - `approved`: `false`, `waitingFor`: `''`, `next`: `''`, `workerLabel`: `null`
   - `resultPath`: `null`, `watchingSince`: `null`, `holdApproval`: `false`, `grillSkipped`: `false`
   - `docs`: `[]`, `log`: `[]`, `pendingAutoStart`: `null`, `checkpoint`: `null`
   - `dispatch`: `[]`, `resumeRequested`: `false`, `deadlineHonored`: `false`
   - `workerPid`: `null`, `workerLog`: `null`, `workerAlive`: `false`, `tried`: `[]`
   - `progressAt`: `null`, `progressKey`: `null`, `workerTail`: `''`, `workerActivity`: `''`
2. **`stepOf`**: Pure function mapping project row status to a high-level pipeline step:
   - `blocked_no_folder` -> `'check'`
   - `propose_grill`, `waiting_on_you` -> `'grill'`
   - `idle` -> `pendingAutoStart ? 'plan' : 'idle'`
   - `needs_approval` -> `'approve'`
   - `blocked_no_worker` -> `'assign'`
   - `watching`, `checkpointed`, `failed` -> `'watch'`
   - `complete` -> `'done'`
   - fallback -> `row.status || 'idle'`
3. **`pipelineOf`**: Pure function returning the ordered 5-stage pipeline (`['check', 'grill', 'plan', 'approve', 'assign', 'watch']`) mapped to states: `'todo'`, `'now'`, `'done'`, or `'skip'` (for skipped grill).
4. **`crewOf`**: Pure function assembling the visible crew roster (Orchestrator lead row + worker/reviewer dispatch slices) with current roles, model display names, and descriptive `doing` text derived from `orchestratorDoing` and dispatch activities.
5. **`progressKey`**: Pure hashing helper generating a change-detection fingerprint string from file counts, top directory names, dirty file count, and worker log stats: `${fileCount}|${names}|${dirty}|${logs}`.
6. **`defaultInspect`**: Pure/bounded folder inspection querying directory file count, names (capped at 24), documentation list, git workspace status, branch, HEAD commit ID, commit subject, recent commits (up to 5), porcelain status dirty files (up to 20), and diffstat (clipped to 400 chars).
7. **`orchestrator.json` load/persist**:
   - `loadState`: Reads and parses `orchestrator.json` from the attempts directory, populates missing defaults, repairs invalid or legacy shapes, preserves unknown fields, initializes sessions, and hydrates project tables.
   - `persist`: Atomically serializes `store` to a unique temporary file (`orchestrator.json.<uuid>.tmp`) and invokes `renameWithRetry` to replace `orchestrator.json` safely under Windows concurrent file access.
8. **Entry lookup (`entry(id)`)**: Retrieves the existing mutable project row by ID or initializes it with `emptyEntry()` on demand.
9. **Snapshot generation (`snapshot(plan, projectId)`)**:
   - Invokes `bridge.observe(item)` for each matching project.
   - Assembles immutable, complete view snapshots containing execution state, active sessions, worker history, decisions inbox, autonomy settings, models roster, researches, asks, tool settings, and project rows.
   - Enriches each project row with git repository status, goal progress, formatted crew, open decisions, next actions, resumable checkpoints, paginated activity sessions, and monitoring metrics.
10. **Tick transitions (`tick(projectId)`)**:
    - Executes the periodic orchestrator state loop: refreshes temporary model unavailabilities, refreshes notebook plans, updates repository metadata, executes decisions, refreshes phase 2 and research statuses, detects completed tasks, persists updated state, and returns a fresh snapshot.
11. **Checkpoint hydration (`hydrateCheckpoints()`)**:
    - Scans `checkpoints/` directory for `<id>.json` disk files on initialization.
    - Hydrates missing or unlinked row checkpoints, links disk paths, and transitions interrupted rows to `checkpointed` status with `paintCheckpoint`.
12. **Research and activity refresh (`refreshResearch()`)**:
    - Monitors active research records (versions 1 and 2), checks process liveness with `opts.isProcessAlive`, reads log tails, verifies `report.md` generation and exit code 0 on `result.txt`, and transitions finished research to `complete` or `failed`.
13. **Archives (`archiveProject`)**:
    - Archives deleted or restarted projects to `store.projectArchive` with timestamp, reason, and full snapshot of execution, asks, researches, autonomy, and tools; purges active project state while preserving historical records.
14. **Multi-project focus (`setFocus`)**:
    - Tracks and persists `selectedProjectId` in the orchestrator store, ensuring focus transitions validate project existence in the notebook plan and isolate cross-project contexts.

## State boundaries

- **Sole authority**: `OrchestratorState` is the sole authority for loading, mutating, and persisting `orchestrator.json` and its per-project rows. No external module may write directly to `orchestrator.json`.
- **Immutable views**: Callers receive immutable, detached snapshot records (`OrchestratorSnapshot`, `ProjectSnapshot`). Mutations must occur strictly through validated state transition methods.
- **Atomic state transitions**: Every disk mutation writes to a temporary file in the same directory (`.tmp`) and executes an atomic replacement using `renameWithRetry` to prevent corruptions during process interruption or Windows file locking.
- **Composition root bridge sharing**: The composition root (`createOrchestrator`) shares the durable `store` rows directly with the `Execution` bridge (`rowFor: (id, megaTaskId) => store.researches.find(...) || entry(id)`). There is no duplicated state cache between the orchestrator state manager and the execution bridge.


## C# Architecture, method signatures, and types

The C# port resides in `native/Planwharf.Engine/OrchestratorState.cs` under the `Planwharf.Engine` namespace. It adheres strictly to Native AOT requirements and uses strongly typed immutable records for external consumers.

```csharp
namespace Planwharf.Engine;

// Port of engine/orchestrator.js state management.
public sealed class OrchestratorState
{
    // Pure helpers
    public static ProjectEntry EmptyEntry();
    public static string StepOf(ProjectEntry? row);
    public static IReadOnlyList<PipelineStepState> PipelineOf(ProjectEntry? row);
    public static IReadOnlyList<CrewMember> CrewOf(ProjectEntry? row, string? leadLabel, ExecutionState? execution = null);
    public static string ProgressKey(InspectionFacts? facts, string? logPath, IReadOnlyList<DispatchPart>? dispatch);
    public static InspectionFacts DefaultInspect(string folder);

    // Lifecycle, load, and atomic persistence
    public static OrchestratorState Load(string? dir, OrchestratorStateCollaborators collaborators);
    public void Persist();

    // Entry and project access
    public ProjectEntry Entry(string projectId);
    public ProjectEntry? TryGetEntry(string projectId);

    // View and transitions
    public OrchestratorSnapshot Snapshot(NotebookPlan plan, string? projectId = null);
    public Task<OrchestratorSnapshot> TickAsync(string? projectId = null, CancellationToken cancellationToken = default);
    public void HydrateCheckpoints();
    public void RefreshResearch();

    // Activity sessions and archives
    public Task<PagedActivitySessions> ActivitySessionPageAsync(string projectId, int offset = 0, int limit = 20, CancellationToken cancellationToken = default);
    public Task<ArchiveProjectResult> ArchiveProjectAsync(string projectId, string reason, ArchiveProjectOptions? options = null, CancellationToken cancellationToken = default);
    public Task<SetFocusResult> SetFocusAsync(string projectId, CancellationToken cancellationToken = default);
    public Task<InspectRepositoryResult> InspectRepositoryAsync(string projectId, RepositoryInspectOptions? options = null, CancellationToken cancellationToken = default);
}
```

### Exact Records and Types

```csharp
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

public record PipelineStepState(string Id, string Label, string State);

public record CrewMember(string Role, string Name, string Doing);

public record InspectionFacts(
    string Folder,
    int FileCount,
    IReadOnlyList<string> Names,
    IReadOnlyList<DocInfo> Docs,
    bool Git,
    string Head,
    string Branch,
    string LastCommit,
    string? CommitId,
    string LastSubject,
    IReadOnlyList<string> Recent,
    IReadOnlyList<string> Dirty,
    string DiffStat,
    string? Error = null
);

public record OrchestratorSnapshot(
    string UpdatedAt,
    string SessionId,
    string? SelectedProjectId,
    ExecutionState? Execution,
    IReadOnlyList<SessionRecord> Sessions,
    IReadOnlyList<WorkerHistoryRecord> WorkerHistory,
    string Mode,
    IReadOnlyDictionary<string, AutoDecideSettings> AutoDecide,
    bool DefaultAutoDecide,
    DecisionsInbox DecisionsInbox,
    IReadOnlyDictionary<string, AutonomySettings> Autonomy,
    bool GrillPropose,
    string GrillKind,
    IReadOnlyList<ModelInfo> Models,
    IReadOnlyList<ResearchRecord> Researches,
    IReadOnlyList<AskRecord> Asks,
    IReadOnlyDictionary<string, object> Thinking,
    IReadOnlyDictionary<string, object> ThinkingByProject,
    IReadOnlyDictionary<string, ToolSettings> ProjectTools,
    IReadOnlyDictionary<string, ToolSettings> ProjectToolSettings,
    bool Asking,
    string? OrchestratorId,
    string? SelectedOrchestratorId,
    string? TeamProjectId,
    bool OwnTeam,
    string? GoldModelId,
    string? OrchestratorUnavailableReason,
    string OrchestratorLabel,
    IReadOnlyList<OrchestratorChoice> Orchestrators,
    IReadOnlyList<ProjectSnapshot> Projects
);

public record ProjectSnapshot(
    string Id,
    string Name,
    string? Folder,
    string? Github,
    RepositoryInfo? Repository,
    string Goal,
    bool GoalStarted,
    string ChatMode,
    string SavingMode,
    IReadOnlyList<string> Notes,
    string StateArt,
    IReadOnlyList<string> Open,
    string Status,
    string Label,
    string Doing,
    IReadOnlyList<string> Plan,
    InspectionFacts? Facts,
    string? GoldModel,
    string? JobTitle,
    string? JobId,
    string? AttemptId,
    string? MegaTaskId,
    MegaTaskRecord? MegaTask,
    IReadOnlyList<PlainSubTask> SubTasks,
    PlainWorkSummary Plain,
    IReadOnlyList<PlainResultSummary> Results,
    string? CheckpointId,
    IReadOnlyList<CheckpointRecord> ResumableCheckpoints,
    string? ModelId,
    string? WorkerLabel,
    string WaitingFor,
    IReadOnlyList<DecisionRecord> OpenDecisions,
    string Next,
    string Step,
    IReadOnlyList<PipelineStepState> Pipeline,
    string LeadLabel,
    IReadOnlyList<CrewMember> Crew,
    string? ResultPath,
    string? WatchingSince,
    bool Approved,
    string? SessionId,
    bool CanStart,
    bool CanResume,
    bool CanStop,
    bool PendingAutoStart,
    IReadOnlyList<DispatchPart> Dispatch,
    ActivityPaginationInfo ActivitySessionPagination,
    IReadOnlyList<ActivitySession> ActivitySessions,
    CheckpointRecord? Checkpoint,
    string? Timing,
    string? DeadlineAt,
    bool CanGrillDocs,
    string DocsAvailability,
    IReadOnlyList<DocInfo> Docs,
    bool GrillSkipped,
    IReadOnlyList<ProjectLogLine> Log,
    int? WorkerPid,
    bool WorkerAlive,
    string? WorkerLog,
    string WorkerTail,
    string WorkerActivity,
    string OrchestratorDoing,
    string? OrchestratorId,
    string OrchestratorLabel,
    MonitoringMetrics? Monitoring
);

public record DispatchPart(
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
    string? RecordedSubTaskStatus = null,
    string? ValidatedResultStatus = null,
    JsonElement? Diagnostic = null
);

public record ResearchRecord(
    string Id,
    string ProjectId,
    string? MegaTaskId,
    string? AttemptId,
    string? ModelId,
    string? Provider,
    string? Cli,
    string Status,
    string? Doing,
    string? Activity,
    string? Reason,
    string? Folder,
    string? ReportPath,
    string? ResultPath,
    string? LogPath,
    int? Pid,
    bool Alive,
    bool ReportAvailable,
    string WorkerTail,
    string? SessionId,
    string StartedAt,
    string UpdatedAt,
    int ExecutionVersion = 2,
    IReadOnlyList<DispatchPart>? Dispatch = null
);

public record ProjectArchiveRecord(
    string ProjectId,
    string Reason,
    string At,
    NotebookProject? Notebook,
    ArchivedProjectState State
);

public record PagedActivitySessions(
    string ProjectId,
    int Total,
    int Offset,
    int Limit,
    IReadOnlyList<ActivitySession> Sessions
);

public record SetFocusResult(bool Ok, string? SelectedProjectId, string? Reason = null);

public record ArchiveProjectResult(bool Ok, ArchivedSummary? Archived, string? Reason = null);

public record ArchiveProjectOptions(bool KeepTeam = false, NotebookProject? Notebook = null);
```


## Mapped JavaScript tests

The C# port in Job 51 will map tests from the following JavaScript test suites in `engine/` to `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs`:

| JavaScript test suite | Target C# test class / methods | Coverage and verified scenarios |
| --- | --- | --- |
| `lifecycle.test.cjs` | `OrchestratorStateLifecycleTests` | State transitions (`idle` -> `needs_approval` -> `watching` -> `checkpointed` / `complete`), `stepOf` mapping, `pipelineOf` progression, and approval flags. |
| `worker-snapshot.test.cjs` | `OrchestratorStateSnapshotTests` | Full orchestrator snapshot generation, project enrichment, active vs historical dispatch formatting, diagnostic file loading, and crew assembly. |
| `progress-detail.test.cjs` | `OrchestratorStateProgressTests` | Fingerprinting with `progressKey`, file counts, dirty paths, log stat updates, and timeline note event generation. |
| `activity-sessions.test.cjs` | `OrchestratorStateActivityTests` | Paged activity sessions (`activitySessionPage`), reading recorded log tails, applying `SESSION_LIMIT = 20`, and merging worker history. |
| `reconcile-attempts.test.cjs` | `OrchestratorStateReconcileTests` | Cold restart reconciliation: detecting alive vs dead worker PIDs, closing orphaned active attempts, and preventing double-collection. |
| `parallel-projects.test.cjs` | `OrchestratorStateParallelTests` | Independent multi-project entries, concurrent project snapshotting, and `selectedProjectId` / `setFocus` isolation. |
| `home-containment.test.cjs` | `OrchestratorStateContainmentTests` | `defaultInspect` path boundary containment, handling unlinked workspaces, symbolic link resolution, and safe Git verification. |
| `project-export.test.cjs` | `OrchestratorStateArchiveTests` | Project archiving via `archiveProject`, moving execution, asks, and researches to `store.projectArchive`, and purging live state safely. |
| `runtime-integration.test.cjs` | `OrchestratorStateRuntimeTests` | End-to-end integration: `orchestrator.json` persistence round trips, `Execution` bridge `rowFor` coordination, and checkpoint file hydration. |

## Parity and verification under Rule 12

Porting `OrchestratorState` must strictly conform to Rule 12 (deterministic parity between the JavaScript engine and native C# implementations):

1. **Byte-level persistence parity**:
   - Serialization of `store` to `orchestrator.json` must produce byte-level equivalence with Node.js `JSON.stringify(store) + '\n'` under canonical key ordering and formatting.
   - Unknown and legacy fields present in existing `orchestrator.json` files must be retained verbatim without data loss during read-modify-write cycles.
   - All atomic file replacements must use `renameWithRetry` on temporary files to match Node.js atomic rename semantics on Windows.
2. **Golden state snapshots**:
   - Golden JSON snapshots captured from Node.js runs across various lifecycle states (fresh notebook, multiple parallel projects, in-progress dispatch, checkpointed work, and archived projects) are verified against C# `Snapshot()` output using normalized semantic AST comparisons.
3. **Restart recovery fixtures**:
   - Test fixtures simulate abrupt process crashes by loading persisted state directories into uninitialized `OrchestratorState` instances.
   - Assert that:
     - Checkpoint files in `checkpoints/<id>.json` hydrate properly into project rows.
     - Dead PIDs are marked `alive = false` without altering completed historical evidence.
     - Known session registries and active session IDs remain consistent.
4. **Scenario replay**:
   - Deterministic execution traces recorded in `native/porting/parity/scenarios.cjs` are replayed through `ScenarioParityTests.cs`.
   - Injected filesystem, clock, and process liveness adapters ensure replay without relying on live child processes or host environment variance.

## Migration step: Job 51

Job 51 implements the `OrchestratorState` module in Step 5 of the Phase 6 migration order:

1. **Step 1 (Job 47)**: `OrchestratorOrderParsing` (pure intent parsing).
2. **Step 2 (Job 48)**: `OrchestratorTeamModels` (pure model and crew resolution).
3. **Step 3 (Job 49)**: `OrchestratorJobBuilding` (pure job proposal and slice calculations).
4. **Step 4 (Job 50)**: `OrchestratorLeadQuestions` (pure lead envelope and question extraction).
5. **Step 5 (Job 51)**: `OrchestratorState` (`native/Planwharf.Engine/OrchestratorState.cs` and `OrchestratorStateTests.cs`).
6. **Step 6 (Job 52)**: Facade composition in `native/Planwharf.Engine/Orchestrator.cs` and full integration pass.

### Job 51 Acceptance Criteria

- `native/Planwharf.Engine/OrchestratorState.cs` and `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs` pass all unit and parity tests.
- Zero state duplication with `Execution.cs`: `rowFor` callback links durable rows directly.
- Atomic writes with `renameWithRetry` pass simulated Windows file sharing violations.
- Pure helper functions (`StepOf`, `PipelineOf`, `ProgressKey`, `EmptyEntry`) achieve 100% vector parity with `cases.cjs`.
- Project archiving and focus switching preserve multi-project integrity under concurrent operations.
