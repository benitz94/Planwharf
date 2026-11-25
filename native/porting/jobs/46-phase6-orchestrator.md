# Job 46 (phase 6): Orchestrator Master Specification Sheet

**Goal.** Specify the complete architectural decomposition, module synthesis, dependency hierarchy, migration sequence, and acceptance criteria for porting `engine/orchestrator.js` (~4,883 lines) into native C# (`net10.0`, Native AOT).

This master specification synthesizes the five approved sub-module specification sheets into a unified composition root architecture:
1. `native/porting/jobs/46a-orchestrator-team-models.md` -> `OrchestratorTeamModels.cs` (Job 48)
2. `native/porting/jobs/46b-orchestrator-job-building.md` -> `OrchestratorJobBuilding.cs` (Job 49)
3. `native/porting/jobs/46c-orchestrator-lead-questions.md` -> `OrchestratorLeadQuestions.cs` (Job 50)
4. `native/porting/jobs/46d-orchestrator-state.md` -> `OrchestratorState.cs` (Job 51)
5. `native/porting/jobs/46e-orchestrator-order-parsing.md` -> `OrchestratorOrderParsing.cs` (Job 47)
6. Master composition root & facade: `native/Planwharf.Engine/Orchestrator.cs` (Job 52)

---

## 1. Goal and Evidence Base

Source base: `port/46-sheets` branched from refreshed `origin/main` recording merged pull requests:
- **PR #236**: Job 44 Phase 5 Execution & ProjectHome Specification Sheet
- **PR #241**: Job 45 Phase 5 Execution Bridge & ProjectHome Implementation

Apply the twelve porting rules in [README.md](../README.md):
- Exact JavaScript behavioral parity is the normative behavioral specification.
- Native AOT compatibility (`net10.0`), trim-safe, zero reflection in hot loops.
- No new external NuGet dependencies, dynamic code emission, or shell process workarounds.
- Exact provider, account, CLI, and model IDs must be preserved; never substitute requested models or fabricate completion evidence.
- A test that cannot fail is not evidence. State what was not run.

---

## 2. Master Composition Root Architecture (`Orchestrator.cs`)

The JavaScript `engine/orchestrator.js` exports `createOrchestrator(opts)`. In native C#, this corresponds to `native/Planwharf.Engine/Orchestrator.cs` exporting `Orchestrator.Create(OrchestratorOptions options)` (or `createOrchestrator`).

```
                              +-------------------------------------------+
                              |         Orchestrator Composition Root     |
                              |   (Orchestrator.cs / createOrchestrator)  |
                              +-------------------------------------------+
                                    |              |               |
             +----------------------+              |               +-----------------------+
             |                                     |                                       |
             v                                     v                                       v
  +----------------------+              +----------------------+              +-------------------------+
  | OrchestratorOrderParsing |          | OrchestratorTeamModels |            | OrchestratorJobBuilding |
  | (Job 47, Step 1)     |              | (Job 48, Step 2)     |              | (Job 49, Step 3)        |
  | Pure functional      |              | Role, roster, quota, |              | Proposals, sliceCount,  |
  | Intent, orders, cmds |              | rank, saving modes   |              | plan formation, steps   |
  +----------------------+              +----------------------+              +-------------------------+
             |                                     |                                       |
             +----------------------+              |               +-----------------------+
                                    |              |               |
                                    v              v               v
                              +-------------------------------------------+
                              |         Orchestrator Composition Root     |
                              +-------------------------------------------+
                                    |                              |
             +----------------------+                              +-----------------------+
             |                                                                             |
             v                                                                             v
  +-------------------------+                                                 +-------------------------+
  | OrchestratorLeadQuestions |                                               |    OrchestratorState    |
  | (Job 50, Step 4)        |                                                 | (Job 51, Step 5)        |
  | Prompts, grill parse,   |                                                 | Sole store authority,   |
  | state pack, lead env    |                                                 | orchestrator.json, tick |
  +-------------------------+                                                 +-------------------------+
             |                                                                             |
             +----------------------+                              +-----------------------+
                                    |                              |
                                    v                              v
                              +-------------------------------------------+
                              |         Orchestrator Composition Root     |
                              |   - Coordinates Execution Bridge          |
                              |   - Coordinates ProjectHome views         |
                              |   - Isolates AsyncLocal & Cancel tokens   |
                              +-------------------------------------------+
                                           |                     |
                                           v                     v
                             +--------------------+     +-------------------+
                             |  Execution.cs      |     |  ProjectHome.cs   |
                             |  (Bridge dispatch, |     |  (Containment,    |
                             |   MegaTask state)  |     |   Git inspection) |
                             +--------------------+     +-------------------+
```

### 2.1 Dependency Directions & Invariants
1. **Unidirectional Data Flow Through Composition Root**:
   - Sub-modules do not call or reference each other directly. All cross-module coordination flows strictly through `Orchestrator.cs`.
   - Data flows into sub-modules as immutable parameter snapshots (`NotebookPlan`, `ProjectSnapshot`, `InspectionFacts`, `IReadOnlyList<ModelDefinition>`).
   - Results flow back from sub-modules as pure data records (`ParsedOrder`, `DispatchPlan`, `ProposedJob`, `GrillRoundResult`, `OrchestratorSnapshot`).
   - The composition root receives these return records, determines authorization and business flow, updates mutable state, and triggers persistence or bridge actions.

2. **No Peer Module Store Imports**:
   - No sub-module imports or accesses another sub-module's private mutable state or store dictionaries.
   - `OrchestratorState` is the sole custodian of `store` and `orchestrator.json`.
   - Neither `OrchestratorOrderParsing`, `OrchestratorTeamModels`, `OrchestratorJobBuilding`, nor `OrchestratorLeadQuestions` may instantiate, hold, or directly mutate `store` or write to disk.

3. **Isolated Per-Project AsyncLocal and Cancellation Tokens**:
   - Project-scoped operations use `AsyncLocal<string?> CurrentProjectId` to guarantee isolation across asynchronous flows.
   - Per-project ask cancellation tokens (`ConcurrentDictionary<string, CancellationTokenSource> _projectAskCts`) ensure that stopping, cancelling, or timing out a chat or command in Project A never cancels or interferes with Project B.
   - Dedicated locks (`ConcurrentDictionary<string, SemaphoreSlim> _projectGates`) serialize concurrent orders and mutations per project while permitting full parallelism across different projects.

4. **Execution Bridge and ProjectHome Integration**:
   - The composition root instantiates and maintains the `Execution` bridge (`Execution.CreateExecutionBridge`).
   - Durable store rows are shared directly between `OrchestratorState` and `Execution.cs` via the `rowFor: (id, megaTaskId) => store.researches.find(...) || state.Entry(id)` callback. There is no duplicate copy or split-brain representation of project rows or MegaTasks.
   - `ProjectHome.cs` is invoked by the composition root to generate home views (`ProjectHome.Build`), passing state views and repository inspection data without leaking orchestrator internals.


---

## 3. Sub-Module Syntheses and Interface Contracts

### 3.1 Sub-Module 1: `OrchestratorOrderParsing` (Step 1, Job 47)
- **Specification Sheet**: `native/porting/jobs/46e-orchestrator-order-parsing.md`
- **Target Source**: `native/Planwharf.Engine/OrchestratorOrderParsing.cs`
- **Target Test File**: `native/Planwharf.Engine.Tests/OrchestratorOrderParsingTests.cs`
- **Responsibilities**:
  - `looksLikeQuestion`: Classifies informational inquiry vs command via trailing `?` and English/Italian interrogative prefixes (`how `, `what `, `why `, `quanto `, `quale `, `come sta`, `perch`, etc.).
  - `looksLikeWork`: Detects imperative work trigger phrases (`do it`, `fallo`, `procedi`, `implement`, `build`, `fix`, `sistema`, `smista`, etc.).
  - `freshTaskIntent`: Classifies new task intent vs general conversation using modal patterns (`can you`, `vorrei`, `puoi`, `please`) and action verbs (`build`, `fix`, `audit`, `review`).
  - `checkpointTalk`: Produces deterministic checkpoint activity strings, notes, and doing messages for `'application update'`, `'stop'`, `'shutdown'`, `'deadline'`, or generic reasons.
  - `researchIntent`: Recognizes background investigation directives, delegation phrases, and previous-turn confirmations.
  - `pickAskedProject`: Deterministically maps target project from explicit ID, embedded project names/IDs, or single-project defaults.
  - `parseOrder`: Extracts operational directives into typed records (`model` on/off, `lead`, `stop`, `resume`, `approve`, `start`, `saving-mode`, `mode`).
  - Order routing in `ask` / `runOrder`: Multi-project ambiguity aborts (`"Message names multiple projects. Select the intended recipient."`), Grill priority, Research priority, and Gold model constraints.
- **C# Method Signatures & Types**:
  ```csharp
  namespace Planwharf.Engine;

  public static class OrchestratorOrderParsing
  {
      public static bool LooksLikeQuestion(string? question);
      public static bool LooksLikeWork(string? text);
      public static bool FreshTaskIntent(string? text);
      public static CheckpointTalkStrings CheckpointTalk(string reason, string? why = null);
      public static bool ResearchIntent(string? text, IReadOnlyList<ConversationTurn>? history = null);
      public static ProjectSnapshot? PickAskedProject(OrchestratorSnapshot snap, string? explicitProjectId, string? userText);
      public static ParsedOrder? ParseOrder(string? text, OrchestratorSnapshot snap, string? explicitProjectId);
      public static OrderRouteResult RouteAsk(string? question, OrchestratorSnapshot snap, string? explicitProjectId, AskRoutingOptions? options = null);
  }

  public record CheckpointTalkStrings(string Activity, string Note, string Orch, Func<string, string, string> Doing);
  public record ParsedOrder(string Action, string? ProjectId = null, string? TargetId = null, string? Label = null, bool? Enabled = null, string? Mode = null, string? ChatMode = null, string? SavingMode = null);
  public record OrderRouteResult(bool Handled, string? Error = null, ParsedOrder? Order = null, OrderRouteKind Kind = OrderRouteKind.PassToLead);
  public enum OrderRouteKind { PassToLead, DirectOrder, AbortAmbiguousProject, RejectGoldModel, ProposeGrill, DispatchResearch }
  ```
- **State Boundaries & Invariants**:
  - Strictly pure functional and stateless: zero I/O, no filesystem, process, network, or clock queries.
  - Explicit error returns instead of exceptions or store mutations.
  - Exact preservation of English and Italian command syntax, classification precedence, and error text.
- **Mapped JavaScript Tests**:
  - `orchestrator.test.cjs`, `project-recipient.test.cjs`, `add-to-work.test.cjs`, `saving-mode.test.cjs`, `start-over.test.cjs`, `phase2.test.cjs`, `phone-decisions.test.cjs`, `pr-fix.test.cjs`.

---

### 3.2 Sub-Module 2: `OrchestratorTeamModels` (Step 2, Job 48)
- **Specification Sheet**: `native/porting/jobs/46a-orchestrator-team-models.md`
- **Target Source**: `native/Planwharf.Engine/OrchestratorTeamModels.cs`
- **Target Test File**: `native/Planwharf.Engine.Tests/OrchestratorTeamModelsTests.cs`
- **Responsibilities**:
  - `normalizeModels`: Standardizes incoming model definitions into valid objects, checks route errors (`ModelRouting.ModelRouteError`), sets `enabled`, `available`, `neverUsable`, `kind`, `cost`, `limit`.
  - `usageOf`: Computes used, limit, remaining, percentage used, percentage left (`pct`, `leftPct`).
  - `leadTier`, `leadRanking`, `leadQuality`: Evaluates lead tiers (0=premium, 1=default, 2=local/light), sorts by quota and capability, family ranking (Fable, Opus, Sonnet, Haiku).
  - `pickWorker` & `pickWorkers`: Selects eligible workers based on roles (`PermitsAssignment`), tools (`canUseTools`, `canInspectFilesystem`), coding, research, quota, saving mode (`allowedBySavingMode`), and provider diversity for verifiers.
  - `dispatchPlan`: Formats reviewer slices (scope, correctness, tests) or worker slices (whole job, builder + independent verifier).
  - `fleetOf`: Generates multi-project fleet status snapshots across all models (`working`, `unavailable`, `provider-disabled`, `restricted`, `assigned`, `idle`).
  - `roster`: Resolves model permissions across project team roles, account-scoped roles, and legacy global roles; merges quota policies and saving mode weights.
  - `selectLead`, `setLeadAllowed`, `setOrchestrator`: Proposes lead selections, toggles allowed status, and proposes orchestrator lead updates.
  - Role policy & saving mode: Enforces `Normal`, `Safe`, and `Ultra safe` filtering; tracks quota usage and plans crew reassignments.
- **C# Method Signatures & Types**:
  ```csharp
  namespace Planwharf.Engine;

  public static class OrchestratorTeamModels
  {
      public static IReadOnlyList<NormalizedModel> NormalizeModels(IReadOnlyList<ModelDefinition> models);
      public static QuotaUsage UsageOf(ModelQuotaItem item);
      public static int LeadTier(ModelDefinition model);
      public static IReadOnlyList<NormalizedModel> LeadRanking(IReadOnlyList<NormalizedModel> models, IReadOnlyList<PlanItem> plans);
      public static IReadOnlyList<NormalizedModel> LeadQuality(IReadOnlyList<NormalizedModel> models);
      public static PickWorkerResult PickWorker(IReadOnlyList<NormalizedModel> models, PickWorkerOptions options);
      public static IReadOnlyList<WorkerAssignment> PickWorkers(IReadOnlyList<NormalizedModel> models, PickWorkersOptions options);
      public static IReadOnlyList<DispatchPart> DispatchPlan(IReadOnlyList<WorkerAssignment> workers, JobKind kind, InspectionFacts facts);
      public static FleetSnapshot FleetOf(IReadOnlyList<NormalizedModel> models, IReadOnlyList<ProjectEntry> activeProjects);
      public static RosterSnapshot Roster(RosterResolutionContext context);
      public static ProposedLeadChange SelectLead(string modelId, RosterSnapshot roster);
      public static ProposedPolicyChange SetLeadAllowed(string modelId, bool allowed, RosterSnapshot roster);
  }

  public record NormalizedModel(string Id, string Label, bool Enabled, bool Available, bool NeverUsable, string Kind, double Cost, double Limit, string? Provider, string? Cli, string? UnavailableReason);
  public record QuotaUsage(double Used, double Limit, double Remaining, int Pct, int LeftPct);
  public record PickWorkerResult(bool Ok, NormalizedModel? Chosen, string? Reason);
  public record WorkerAssignment(NormalizedModel Model, string Slice, string Why);
  public record ProposedLeadChange(bool Ok, string? ModelId, string? Reason);
  public record ProposedPolicyChange(bool Ok, string Key, JsonElement Value);
  ```
- **State Boundaries & Invariants**:
  - Accepts immutable catalogs, snapshots, and exact requested IDs.
  - Strict preservation of requested IDs: never substitute requested models or fabricate completion evidence.
  - Returns proposals and candidate lists; composition root alone commits team or policy changes to `store`.
- **Mapped JavaScript Tests**:
  - `model-roles.test.cjs`, `model-policy.test.cjs`, `model-routing.test.cjs`, `saving-modes.test.cjs`, `saving-mode-*.test.cjs`, `quota-*.test.cjs`, `team-bulk.test.cjs`, `project-teams.test.cjs`, `lead-*.test.cjs`, `gold-model.test.cjs`, `crew-reassign.test.cjs`.

---

### 3.3 Sub-Module 3: `OrchestratorJobBuilding` (Step 3, Job 49)
- **Specification Sheet**: `native/porting/jobs/46b-orchestrator-job-building.md`
- **Target Source**: `native/Planwharf.Engine/OrchestratorJobBuilding.cs`
- **Target Test File**: `native/Planwharf.Engine.Tests/OrchestratorJobBuildingTests.cs`
- **Responsibilities**:
  - `jobKind`: Classifies tasks as `"hard"`, `"build"`, or `"inspect"` using regex precedence on task title and detail.
  - `sliceCount`: Pure vector determining parallel slice allocation (1 or 2) from task kind, dirty path count (`facts.dirty`), and eligible crew pool.
  - `dispatchPlan` consumption: Translates candidate models and job parameters into concrete builder and independent verifier slices.
  - `jobFromFacts`: Synthesizes proposed job title and markdown detail from notebook project revision direction and repository facts (`facts.dirty`, `facts.lastCommit`, `facts.recent`).
  - `planLines`: Formats clean markdown work plan lines from job title, detail, dispatch slices, and execution status.
  - `decide`: Analyzes project state and returns structured advancement decision (`DecideOutcome`).
  - `planByLead` & `assignByLead`: Assembles lead model instructions and processes proposed planning or assignment directives.
  - `startAutoTask`: Prepares an automatic implementation task proposal from uncommitted changes and notebook goals.
  - `openWorkPlan` & `planSteps`: Renders the full structured work plan document (`.planwharf/plan.md`) with task breakdown and status markers.
- **C# Method Signatures & Types**:
  ```csharp
  namespace Planwharf.Engine;

  public static class OrchestratorJobBuilding
  {
      public static string JobKind(JobDescriptor? job);
      public static int SliceCount(JobDescriptor? job, InspectionFacts? facts, int eligiblePoolCount);
      public static IReadOnlyList<DispatchPart> BuildDispatch(IReadOnlyList<NormalizedModel> workers, JobDescriptor job, InspectionFacts facts);
      public static ProposedJob JobFromFacts(ProjectRevision? revision, InspectionFacts? facts);
      public static IReadOnlyList<string> PlanLines(JobDescriptor job, IReadOnlyList<DispatchPart> dispatch, string status);
      public static DecideOutcome Decide(ProjectEntry row, InspectionFacts facts, IReadOnlyList<NormalizedModel> crew);
      public static WorkPlanResult OpenWorkPlan(string projectFolder, ProposedJob job, IReadOnlyList<DispatchPart> dispatch);
      public static IReadOnlyList<PlanStepEntry> PlanSteps(string planContent);
  }

  public record JobDescriptor(string Title, string Detail, string? RevisionId = null, string? Timing = null, string? GoldModel = null);
  public record ProposedJob(string Title, string Detail, string? RevisionId, string Timing, string? GoldModel);
  public record DecideOutcome(string NextAction, string Status, string Reason, bool RequiresApproval);
  public record WorkPlanResult(bool Ok, string PlanPath, IReadOnlyList<string> Lines);
  public record PlanStepEntry(int StepNumber, string Title, string Status, string? WorkerLabel);
  ```
- **State Boundaries & Invariants**:
  - Pure analytical formation: consumes revision, facts, crew, and timing; produces typed proposals and formatted markdown.
  - No direct worker launches: worker execution is strictly coordinated through the `Execution` bridge.
  - The composition root owns file writes and durable approval states.
- **Mapped JavaScript Tests**:
  - `job-title.test.cjs`, `orchestrator.test.cjs`, `phase1.test.cjs`, `phase2.test.cjs`, `phase2-git.test.cjs`, `task-completion.test.cjs`, `always-approve.test.cjs`, `plan-file.test.cjs`, `saving-mode-pr.test.cjs`.


---

### 3.4 Sub-Module 4: `OrchestratorLeadQuestions` (Step 4, Job 50)
- **Specification Sheet**: `native/porting/jobs/46c-orchestrator-lead-questions.md`
- **Target Source**: `native/Planwharf.Engine/OrchestratorLeadQuestions.cs`
- **Target Test File**: `native/Planwharf.Engine.Tests/OrchestratorLeadQuestionsTests.cs`
- **Responsibilities**:
  - `listDocs` & `collectDocs`: Discovers root project docs (`README.md`, `AGENTS.md`, `CLAUDE.md`, `SKILL.md`) and `docs/*.md` within folder containment, capped at 12 entries.
  - `parseGrillQuestions`: Pure parser extracting a JSON array of 3 to 6 question strings or parsing bulleted lines containing `"recommended:"` or `?`, capped at 8 entries.
  - `grillPrompt`: Generates deterministic prompt instructing the lead model to interview the user about open decisions at the frontier before planning.
  - `grillDone`: Detects completed grill interview mode in project revisions.
  - `answerQuestion`: Deterministic fast-path responder answering queries about fleet status, specific workers, slices, quotas, process liveness, next actions, and log tails without invoking AI.
  - `packState`: Assembles project-scoped prompt context, strictly isolating executions, researches, focus context, and public metadata of concurrent projects.
  - `leadEnvelope`: Unpacks lead model JSON responses into typed answer text and structured actions (`{ answer: string, actions: any[] }`), rejecting unknown fields.
  - `composeGrillRound`: Coordinates clarification interviews for project revisions before planning.
  - `askLeadDecision` & autonomy/research review: Manages lead decision consultations, autonomy supervisor approvals, and research task evaluations.
- **C# Method Signatures & Types**:
  ```csharp
  namespace Planwharf.Engine;

  public static class OrchestratorLeadQuestions
  {
      public static IReadOnlyList<string> ListDocs(string folder);
      public static IReadOnlyList<string> CollectDocs(InspectionFacts facts);
      public static IReadOnlyList<string> ParseGrillQuestions(string? text);
      public static string GrillPrompt(ProjectRevision revision, InspectionFacts facts, IReadOnlyList<string> docs);
      public static bool GrillDone(ProjectRevision revision);
      public static AnswerQuestionResult AnswerQuestion(string question, OrchestratorSnapshot snap, string? projectId, IReadOnlyList<string>? askHistory = null);
      public static PackedState PackState(ProjectEntry row, OrchestratorSnapshot snap, InspectionFacts facts);
      public static LeadEnvelopeResult LeadEnvelope(string? rawOutput);
      public static Task<GrillRoundResult> ComposeGrillRoundAsync(ProjectRevision revision, InspectionFacts facts, string kind, ILeadModelAdapter adapter, CancellationToken cancellationToken);
      public static Task<LeadDecisionResult> AskLeadDecisionAsync(LeadDecisionRequest request, ILeadModelAdapter adapter, CancellationToken cancellationToken);
  }

  public record AnswerQuestionResult(bool Ok, string Answer, string? ProjectId, string? Reason = null);
  public record PackedState(string ProjectId, JsonObject ExecutionHandoff, IReadOnlyList<ResearchRecord> Researches, JsonObject Focus, IReadOnlyList<JsonObject> Others, FleetSnapshot Fleet, IReadOnlyList<PlanItem> Plans);
  public record LeadEnvelopeResult(string Answer, IReadOnlyList<JsonObject> Actions, string? Error = null);
  public record GrillRoundResult(bool Ok, IReadOnlyList<string> Questions, string? Error = null);
  public record LeadDecisionResult(bool Ok, string Answer, IReadOnlyList<JsonObject> Actions, string? Error = null);
  ```
- **State Boundaries & Invariants**:
  - Consumes project-scoped conversation snapshot and injected lead adapter.
  - Composition root owns per-project ask cancellation tokens, invocation ledger, recipient resolution, and durable chat mutations.
  - Zero cross-project data leakage: prompt state is strictly filtered to the target project.
- **Mapped JavaScript Tests**:
  - `orchestrator.test.cjs`, `project-context.test.cjs`, `project-recipient.test.cjs`, `conversation-history.test.cjs`, `grill-chat.test.cjs`, `autonomy-*.test.cjs`, `parallel-projects.test.cjs`, `research-dispatch.test.cjs`, `supervisor-role.test.cjs`.

---

### 3.5 Sub-Module 5: `OrchestratorState` (Step 5, Job 51)
- **Specification Sheet**: `native/porting/jobs/46d-orchestrator-state.md`
- **Target Source**: `native/Planwharf.Engine/OrchestratorState.cs`
- **Target Test File**: `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs`
- **Responsibilities**:
  - `emptyEntry`: Creates canonical empty project row with all default fields (`status: 'idle'`, `doing: ''`, `plan: []`, `facts: null`, etc.).
  - `stepOf`: Maps project status to high-level pipeline step (`check`, `grill`, `plan`, `approve`, `assign`, `watch`, `done`).
  - `pipelineOf`: Returns ordered 5-stage pipeline states (`todo`, `now`, `done`, `skip`).
  - `crewOf`: Assembles visible crew roster with Orchestrator lead row, worker/reviewer dispatch slices, and active doing text.
  - `progressKey`: Generates change-detection fingerprint string `${fileCount}|${names}|${dirty}|${logs}`.
  - `defaultInspect`: Bounded directory inspection querying file count, names, git status, branch, HEAD commit, recent commits, and dirty files.
  - `orchestrator.json` load/persist: Loads, deserializes, validates legacy shapes, and performs atomic persistence via temporary file and `renameWithRetry`.
  - Entry lookup: `entry(id)` gets or initializes mutable project row.
  - Snapshot generation: `snapshot(plan, projectId)` builds complete immutable views, enriching rows with repository status, crew, decisions, and activity.
  - Tick transitions: `tick(projectId)` runs periodic loop, updates project rows, checks research/decision progress, persists store, and returns updated snapshot.
  - Checkpoint hydration: `hydrateCheckpoints()` scans `checkpoints/*.json` on startup and re-links interrupted tasks.
  - Research and activity refresh: Monitors running background research processes, tail logs, and completion evidence.
  - Archives: `archiveProject` safely preserves deleted/restarted project history in `store.projectArchive`.
  - Multi-project focus: `setFocus` validates and sets active project focus.
- **C# Method Signatures & Types**:
  ```csharp
  namespace Planwharf.Engine;

  public sealed class OrchestratorState
  {
      public static ProjectEntry EmptyEntry();
      public static string StepOf(ProjectEntry? row);
      public static IReadOnlyList<PipelineStepState> PipelineOf(ProjectEntry? row);
      public static IReadOnlyList<CrewMember> CrewOf(ProjectEntry? row, string? leadLabel, ExecutionState? execution = null);
      public static string ProgressKey(InspectionFacts? facts, string? logPath, IReadOnlyList<DispatchPart>? dispatch);
      public static InspectionFacts DefaultInspect(string folder);

      public static OrchestratorState Load(string? dir, OrchestratorStateCollaborators collaborators);
      public void Persist();

      public ProjectEntry Entry(string projectId);
      public ProjectEntry? TryGetEntry(string projectId);

      public OrchestratorSnapshot Snapshot(NotebookPlan plan, string? projectId = null);
      public Task<OrchestratorSnapshot> TickAsync(string? projectId = null, CancellationToken cancellationToken = default);
      public void HydrateCheckpoints();
      public void RefreshResearch();
      public void ArchiveProject(string projectId, string reason);
      public void SetFocus(string? projectId);
  }

  public record PipelineStepState(string Step, string State);
  public record CrewMember(string Role, string ModelId, string Label, string Doing, bool Working);
  public record OrchestratorSnapshot(string StoreVersion, IReadOnlyList<ProjectSnapshot> Projects, FleetSnapshot Fleet, IReadOnlyList<DecideOutcome> Decisions, IReadOnlyList<ActivitySession> Sessions, string? SelectedProjectId);
  ```
- **State Boundaries & Invariants**:
  - Sole authority: `OrchestratorState` is the only component allowed to read and write `orchestrator.json`.
  - Immutable views: Callers receive detached snapshot copies; mutations occur strictly through validated state transition methods.
  - Atomic persistence: Writes to `<path>.<uuid>.tmp` and renames with retry under Windows file locking semantics.
  - Zero state duplication: Durable rows are shared directly with the `Execution` bridge via injected delegate (`rowFor`), eliminating duplicate state representations.
- **Mapped JavaScript Tests**:
  - `lifecycle.test.cjs`, `worker-snapshot.test.cjs`, `progress-detail.test.cjs`, `activity-sessions.test.cjs`, `reconcile-attempts.test.cjs`, `parallel-projects.test.cjs`, `home-containment.test.cjs`, `project-export.test.cjs`, `runtime-integration.test.cjs`.


---

## 4. Phase 6 Migration Sequence (Job 47 to Job 52)

The migration of `engine/orchestrator.js` into C# proceeds in six incremental, dependency-ordered steps designed to minimize risk and maintain test verification at every stage:

```
[Job 47: OrderParsing] -> [Job 48: TeamModels] -> [Job 49: JobBuilding] -> [Job 50: LeadQuestions] -> [Job 51: State] -> [Job 52: Facade & Pass]
(Pure parsing, no I/O)    (Roster, quota, rank)  (Proposals, plans)      (Prompts, envelopes)        (Store, persist)   (Full composition root)
```

### 4.1 Step 1: OrderParsing (Job 47)
- **Scope**: Port order parsing, command matching, intent classification, and order routing logic into `native/Planwharf.Engine/OrchestratorOrderParsing.cs` and tests in `native/Planwharf.Engine.Tests/OrchestratorOrderParsingTests.cs`.
- **Pure Parity**: Add deterministic comparison vectors to `native/porting/parity/cases.cjs` for `looksLikeQuestion`, `looksLikeWork`, `freshTaskIntent`, `checkpointTalk`, and `parseOrder`. Record pure vector outputs in `cases.cjs`.
- **Invariants**: Strictly pure functions with zero I/O; preserves English and Italian syntax; returns explicit error structures on ambiguity.

### 4.2 Step 2: TeamModels (Job 48)
- **Scope**: Port model normalization, roster calculation, quota tracking, lead evaluation, and worker selection into `native/Planwharf.Engine/OrchestratorTeamModels.cs` and tests in `native/Planwharf.Engine.Tests/OrchestratorTeamModelsTests.cs`.
- **Pure Parity**: Add deterministic vectors for `normalizeModels`, `usageOf`, `leadTier`, `leadRanking`, `leadQuality`, `pickWorker`, and `dispatchPlan` to `cases.cjs`.
- **Invariants**: Pure evaluation over immutable catalogs and usage snapshots; strict preservation of exact requested model, provider, and CLI IDs without substitution; composition root alone commits team or policy changes.

### 4.3 Step 3: JobBuilding (Job 49)
- **Scope**: Port task classification, slice sizing, task markdown generation, and plan formation into `native/Planwharf.Engine/OrchestratorJobBuilding.cs` and tests in `native/Planwharf.Engine.Tests/OrchestratorJobBuildingTests.cs`.
- **Pure Parity**: Add deterministic vectors for `jobKind`, `sliceCount`, and `planLines` to `cases.cjs`.
- **Invariants**: Generates task proposals and work plan updates from inspection facts and crew roster; never directly spawns processes; worker execution is strictly delegated through the `Execution` bridge.

### 4.4 Step 4: LeadQuestions (Job 50)
- **Scope**: Port doc collection, prompt generation, grill interview parsing, deterministic query answering, context packing, and lead action envelope decoding into `native/Planwharf.Engine/OrchestratorLeadQuestions.cs` and tests in `native/Planwharf.Engine.Tests/OrchestratorLeadQuestionsTests.cs`.
- **Parity**: Deterministic vectors for `parseGrillQuestions`, `grillPrompt`, and `leadEnvelope`; scenario tests for `packState`, `answerQuestion`, and `composeGrillRoundAsync` with injected lead model adapters.
- **Invariants**: Isolates prompt state to the active project; enforces schema validation on lead responses; composition root owns per-project ask cancellation tokens.

### 4.5 Step 5: State (Job 51)
- **Scope**: Port state container, `orchestrator.json` load/persistence, project row access, view snapshots, tick loop transitions, and checkpoint hydration into `native/Planwharf.Engine/OrchestratorState.cs` and tests in `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs`.
- **Parity**: Golden state snapshots, restart recovery fixtures, scenario replay, and byte-level persistence parity under Rule 12 (`renameWithRetry`).
- **Invariants**: Sole authority for `orchestrator.json`; callers receive immutable detached views; atomic persistence via temporary file swap; shares durable rows with `Execution` bridge via delegate without duplicating state.

### 4.6 Step 6: Facade Composition and Full Test Pass (Job 52)
- **Scope**: Implement `native/Planwharf.Engine/Orchestrator.cs` (`createOrchestrator`) composing all five sub-modules, the `Execution` bridge, and `ProjectHome.cs`. Implement comprehensive integration tests in `native/Planwharf.Engine.Tests/OrchestratorTests.cs`.
- **Verification**: Complete test pass across all unit, parity, and integration suites in both C# and JavaScript. Retire duplicated helpers from earlier porting phases.

---

## 5. Acceptance Criteria, Test Gates, Allowlists, and Boundaries

### 5.1 Acceptance Criteria for Job 46 (Master Specification Sheet)
1. **Sheet Completeness**:
   - Master sheet `native/porting/jobs/46-phase6-orchestrator.md` synthesizes all five sub-module sheets (`46a` through `46e`) into a coherent composition root architecture.
   - Defines strict unidirectional dependency directions through `Orchestrator.cs`.
   - Explicitly prohibits peer module store imports or cross-module state mutation.
   - Defines per-project `AsyncLocal` context and independent `CancellationTokenSource` isolation.
   - Specifies single durable state truth shared with `Execution.cs` bridge and integration with `ProjectHome.cs`.
   - Specifies exact 6-step migration order from Step 1 (Job 47) through Step 6 (Job 52).
2. **Sheet Verification Test Suite**:
   - `engine/job46-sheet.test.cjs` validates all requirement patterns across `46*.md` sheets.
   - Implements durable omission and mutation checks: each requirement must fail when removed from sheet text.
   - Proves empty and truncated sheets fail.
   - Passes all existing sheet test suites (`job44`, `job42`, `job37`, `job23`).

### 5.2 Acceptance Criteria for Subsequent Phase 6 Jobs (Jobs 47-52)
1. **Rule 12 Pure & Scenario Parity**:
   - Pure functions must have matching vectors in `native/porting/parity/cases.cjs` and recorded outputs in `cases.cjs`.
   - Stateful and I/O operations must have scenario fixtures in `native/porting/parity/scenarios.cjs` and recorded replays in `ScenarioParityTests.cs`.
   - Nonzero pure and scenario floors enforced in `native/porting/parity/required.json` and `engine/parity.test.cjs`.
2. **Native AOT & Compilation Standards**:
   - Target framework `net10.0`, zero compilation warnings, zero Native AOT trimming warnings.
   - Clean execution of `dotnet publish native/Planwharf.Engine -c Release -r win-x64 -p:PublishAot=true -p:NativeLib=Shared`.
3. **Exact Identity & Non-Substitution**:
   - Never substitute requested models, providers, CLIs, or accounts.
   - Never fabricate completion evidence, quota status, or execution results.

### 5.3 Test Gates
Every Phase 6 implementation must pass these standard test gates before submission:

```bash
# 1. Sheet specification verification
node --test engine/job46-sheet.test.cjs
node --test engine/job44-sheet.test.cjs engine/job42-sheet.test.cjs engine/job37-sheet.test.cjs engine/job23-sheet.test.cjs

# 2. Pure parity vector recording and verification
node native/porting/parity/record.cjs
node native/porting/parity/record.cjs --check

# 3. Scenario parity recording and verification
node native/porting/parity/scenarios.cjs
node native/porting/parity/scenarios.cjs --check
node --test engine/parity.test.cjs

# 4. Native C# build and test pass
dotnet build native/Planwharf.Engine -c Release
dotnet test native/Planwharf.Engine.Tests -c Release
dotnet test native/Planwharf.Engine.Tests -c Release --filter ScenarioParityTests

# 5. Native AOT publish gate
dotnet publish native/Planwharf.Engine -c Release -r win-x64 -p:PublishAot=true -p:NativeLib=Shared

# 6. Repository formatting and CI checks
node scripts/engineering/ci.cjs
git diff --check
```

### 5.4 Allowlists and Ownership Boundaries

#### Job 46 Allowlist (This Sheet Job):
- `native/porting/jobs/46-phase6-orchestrator.md` (Master orchestrator specification sheet - owned by this slice)
- `native/porting/jobs/46a-orchestrator-team-models.md`
- `native/porting/jobs/46b-orchestrator-job-building.md`
- `native/porting/jobs/46c-orchestrator-lead-questions.md`
- `native/porting/jobs/46d-orchestrator-state.md`
- `native/porting/jobs/46e-orchestrator-order-parsing.md`
- `engine/job46-sheet.test.cjs` (owned by downstream test slice ST-07)
- `docs/NEXT-WORK.md`, `docs/BUILT-WITH-PLANWHARF.md`, `native/porting/PLAN.md`, `.planwharf/plan.md` (owned by progress/commit slice ST-08)

#### Phase 6 Implementation Allowlists (Jobs 47-52):
- **Job 47**: `native/Planwharf.Engine/OrchestratorOrderParsing.cs`, `native/Planwharf.Engine.Tests/OrchestratorOrderParsingTests.cs`, `cases.cjs`.
- **Job 48**: `native/Planwharf.Engine/OrchestratorTeamModels.cs`, `native/Planwharf.Engine.Tests/OrchestratorTeamModelsTests.cs`, `cases.cjs`.
- **Job 49**: `native/Planwharf.Engine/OrchestratorJobBuilding.cs`, `native/Planwharf.Engine.Tests/OrchestratorJobBuildingTests.cs`, `cases.cjs`.
- **Job 50**: `native/Planwharf.Engine/OrchestratorLeadQuestions.cs`, `native/Planwharf.Engine.Tests/OrchestratorLeadQuestionsTests.cs`, `cases.cjs`, `scenarios.cjs`.
- **Job 51**: `native/Planwharf.Engine/OrchestratorState.cs`, `native/Planwharf.Engine.Tests/OrchestratorStateTests.cs`, `scenarios.cjs`.
- **Job 52**: `native/Planwharf.Engine/Orchestrator.cs`, `native/Planwharf.Engine.Tests/OrchestratorTests.cs`, full wiring and retirement of temporary shims.

#### Strict Boundaries:
- No edits to JavaScript production code in `engine/` during specification jobs.
- No modifications to personal project files or notebooks in `.attempts-plan/`.
- No introduction of external dependencies or packages.
- No git push or branch merge from individual worker slices.
