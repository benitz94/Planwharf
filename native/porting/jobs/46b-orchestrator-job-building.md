# Job 46b (phase 6): OrchestratorJobBuilding specification

**Goal.** Specify the C# port of the job-building and plan-formation responsibilities of `engine/orchestrator.js` into `native/Planwharf.Engine/OrchestratorJobBuilding.cs`, its test suites in `native/Planwharf.Engine.Tests/OrchestratorJobBuildingTests.cs`, its pure and scenario parity contracts, and its migration in Job 49.

This specification sheet is part of the approved five-way split of the 4,883-line JavaScript orchestrator defined in Job 44:
1. `46a-orchestrator-team-models.md` (Job 48)
2. `46b-orchestrator-job-building.md` (Job 49 - this specification)
3. `46c-orchestrator-lead-questions.md` (Job 50)
4. `46d-orchestrator-state.md` (Job 51)
5. `46e-orchestrator-order-parsing.md` (Job 47)
6. `46-phase6-orchestrator.md` (Job 52 composition root facade)

## Goal and evidence

Source base: `port/46-sheets` branched from refreshed `origin/main` after merged pull requests #236 (Job 44) and #241 (Job 45). Apply the twelve porting rules in [README.md](../README.md), Native AOT compatibility (`net10.0`), zero runtime warnings, and exact behavior parity with `engine/orchestrator.js`. This sheet permits no runtime change, no dependency addition, and no premature worker launches.

## Port mapping

| JavaScript source | C# in `native/Planwharf.Engine/` | Tests in `native/Planwharf.Engine.Tests/` | Migration job |
| --- | --- | --- | --- |
| `engine/orchestrator.js` (job building, plan formation, slice sizing, task preparation, autonomy split/assign consumption) | `OrchestratorJobBuilding.cs` (first line `// Port of orchestrator job building and work plan formation.`) | `OrchestratorJobBuildingTests.cs` | **Job 49** (Step 3 of Phase 6 orchestrator port) |

## State boundaries and interface contracts

`OrchestratorJobBuilding` is a cohesive domain module focused on translating notebook project state, inspection facts, timing constraints, and crew capabilities into executable job proposals, slice breakdowns, and work plan documents.

### Strict boundary rules:
1. **Inputs accepted**:
   - Project revision snapshots (direction, open questions, revision IDs)
   - Canonical inspection facts (`facts.dirty`, `facts.recent`, `facts.lastCommit`, `facts.lastSubject`, `facts.git`)
   - Eligible crew rosters and model candidate snapshots
   - Timing and deadline constraints (`timing: 'discretion'` | `'deadline'`, `deadlineAt`)
   - Injected adapters for lead model interaction (`ILeadModelAdapter`), execution bridge (`IExecutionBridge`), and workspace inspection (`IFolderInspector`)
2. **Outputs returned**:
   - Proposed job specifications (`ProposedJob`: title, detail, revision ID, timing, gold model)
   - Slice breakdowns and subtask proposals (`IReadOnlyList<SubTaskProposal>`)
   - Formatted plan lines (`IReadOnlyList<string>`)
   - Work plan updates (`WorkPlanResult`, `PlanStepEntry`)
   - Decision and advancement outcomes (`DecideOutcome`)
3. **Composition root ownership**:
   - The composition root (`Orchestrator.cs`) holds sole authority for durable `store` persistence (`persist()`, `orchestrator.json`), approval gates (`approved`, `holdApproval`), and invoking the `Execution` bridge dispatch.
   - The composition root owns file system mutations outside the project's `.planwharf/plan.md`.
4. **No direct worker launches**:
   - `OrchestratorJobBuilding` **never** launches processes, never invokes worker CLIs, and never interacts with process trees.
   - Worker execution is strictly delegated through the `Execution` bridge upon explicit authorization and composition root coordination.

## Responsibilities and behavior

### 1. `jobKind`
Determines the general classification of a task based on its title and detail strings.
- **Rule**: Normalizes `(job?.title ?? "") + " " + (job?.detail ?? "")` to lower-case.
- **Matching Precedence**:
  1. `/\b(hard|difficult|escalat\w*|premium|complex)\b/` => returns `"hard"`.
  2. `/\b(implement|code|fix|bug|test|build|write)\b/` => returns `"build"`.
  3. Fallback => returns `"inspect"`.
- **Pure Function**: Deterministic, side-effect free, string evaluation.

### 2. `sliceCount`
Determines how many parallel worker slices to allocate for an implementation or inspection job given the task classification, repository state, and number of eligible workers.
- **Rule**:
  1. Evaluates task classification via `jobKind(job)`.
  2. Counts dirty paths: `facts?.dirty?.length ?? 0`.
  3. Validates eligible pool count: `pool = (Number.isFinite(eligible) && eligible > 0) ? eligible : 1`.
  4. If `pool <= 1`: returns `1`.
  5. If `kind === "hard"`: returns `Math.Min(2, pool)`.
  6. If `kind === "build"`: returns `Math.Min(2, pool)`.
  7. If `dirty >= 4`: returns `Math.Min(2, pool)`.
  8. Default: returns `1`.
- **Pure Function**: Pure vector calculation. Caps slice count to at most 2 parallel workers in the standard split unless explicitly instructed otherwise.

### 3. `dispatchPlan` consumption
Translates selected worker candidates and job parameters into concrete worker dispatch slices.
- **Behavior**:
  - If workers list is empty: returns empty array `[]`.
  - For reviewer role (`assignmentRole(job) === 'reviewer'`): generates three specialized review slices:
    1. `"Review the approved scope and report findings."`
    2. `"Review correctness and edge cases; report findings."`
    3. `"Review tests and verification evidence; report findings."`
  - For a single worker: generates a single slice with `"Whole job"`.
  - For two or more workers (builder + independent verifier):
    - First slice (builder): if dirty changes exist (`facts.dirty.length > 0`), `"Implement the whole job in the project folder, starting from the uncommitted changes, with automated tests. Run the tests before you finish."`; otherwise `"Implement the whole job in the project folder, with automated tests. Run the tests before you finish."`
    - Second slice (verifier): `VERIFY_SLICE + ': read the requirements and run the tests, then go straight to what the tests do not cover (edge cases, error, recovery and concurrency paths), add tests for it and fix what fails. Do not rewrite code that works. If you find something you must not decide or fix yourself, write one line: "Needs your decision: <plain words a non-programmer understands> | suggestion: <what you recommend> | breaks: yes" only if the wrong choice could lose work or break the app, otherwise "breaks: no".'`
  - Additional slices (index >= 2): `"Parallel slice {index + 1}"`.
  - Maps to dispatch objects containing `modelId`, `label`, `slice`, `why`, `pid: null`, `logPath: null`.

### 4. `jobFromFacts`
Constructs a proposed task (title and structured markdown detail) from the latest notebook project revision and canonical repository inspection facts.
- **Title resolution**:
  - `goal = revision?.direction?.trim() ?? ""`
  - `subject = facts?.lastSubject ?? "latest repo changes"`
  - `title = clip(goal, 120) || clip("Continue: " + subject, 120) || "Continue from the repo"`
- **Detail assembly**:
  - Goal section: `goal ? "Goal: " + clip(goal, 4000) : ""`
  - Commit line: `facts?.lastCommit ? "Latest commit: " + facts.lastCommit : (facts?.git ? "Git present." : "No git history.")`
  - Recent commits: formatted up to 5 commits if present.
  - Uncommitted changes: formatted up to 12 dirty paths if present, else `"Working tree clean."`
  - Direction closing: `goal ? "Work toward the goal. Read only what that needs." : "Read the repo, take the latest changes, and proceed with the notebook plan."`
  - All sections joined with double newlines (`\n\n`).

### 5. `planLines`
Generates human-readable progress and instructional plan lines for display and UI synchronization.
- **Deterministic sequence of lines**:
  1. `"Stay inside {folder}."`
  2. Notebook goal: if goal exists, `"Notebook goal: {clip(goal, 180)}"`.
  3. Git status:
     - If git present: `"Latest git: {lastCommit || head || 'present'}{on branch}."`
     - Dirty count: if dirty paths exist, `"Uncommitted: {count} path(s). Read those changes."`
     - Otherwise: `"Working tree clean. Proceed from the latest commit."`
     - If git absent: `"Read-only inspect ({count} top-level names)."`
  4. `"Proceed with the plan from those changes."`
  5. Slice allocation:
     - If multiple dispatch parts: `"Split into {count} slices for parallel workers."`, followed by each part: `"Slice {i}: {label || modelId} — {slice}."`
     - If single worker: `"{Assigned | Pick} {label || id}{why}."`
  6. Timing constraint:
     - If deadline specified: `"Finish by {deadlineAt}. At that time the orchestrator delivers the output or writes a checkpoint."`
     - Otherwise: `"Timing is at the worker’s discretion."`
  7. Checkpoint indicator: if checkpoint path exists, `"Resume from checkpoint {path}."`
  8. Sentinel rule: `"Watch the worker process, the result file (STATUS: COMPLETE), and the project folder."`

### 6. `decide`
Evaluates the project state, inspects working files when necessary, determines whether to advance or settle work, and coordinates state transitions.
- **Execution v2 Handling**:
  - Detects `job?.executionVersion === 2`.
  - Maintains job fingerprinting (`jobFingerprint(job)`).
  - Skips redundant filesystem inspection when work is unapproved or paused.
  - Builds dispatch parts either from `job.proposedSubTasks` or via `dispatchPlan(pickWorkers(...))`.
  - Calls `bridge.advance(item, job, parts)` and handles `blocked_no_worker` gracefully.
- **Completion Detection**:
  - Inspects recorded result files for all active dispatch parts.
  - Verifies each result file exists and has `STATUS: COMPLETE`.
  - Confirms no part was stopped by user and job fingerprint matches.
  - Transitions project row to `complete` and settles completed attempt.
  - Never redispatches completed assignments on subsequent evaluation ticks.
- **Job Mutation and Approval Reset**:
  - Detects if job ID or job fingerprint changed.
  - Automatically resets `approved = false`, `holdApproval = false`, clears attempt and dispatch arrays, and logs notification: `"Job changed. Approval reset."`
- **Session Boundary Enforcement**:
  - Enforces startup authorization session boundary when enabled.

### 7. `planByLead`
Asynchronously orchestrates lead model intervention to split large tasks into parallel subtask slices.
- **Preconditions**:
  - Lead model is configured and available with `askModel` capability.
  - At least two builder models and at least one verifier model are available (considering roles, saving mode, and quota limits).
  - Task does not specify a Gold model and does not contain non-implementation steps.
- **Context Collection**:
  - Collects goal direction, project files list, named documents, previous user/lead remarks in chat (`said`), and attached `.planwharf/plan.md`.
- **Two-Round Interaction**:
  - Formulates prompt via `autonomy.splitPrompt`.
  - Sends query via `opts.askModel` (with `allowActions: false`).
  - If initial response fails parsing or breaks rules, queries once more stating the error.
  - Handles provider refusals and records refusal reason.
- **Post-Processing and Load Balancing**:
  - Fits weights: light parts assigned to lighter models via `autonomy.fitWeights`.
  - Spreads parts across models and providers via `autonomy.spreadParts`.
  - Synthesizes verification slice across all implementation parts.
  - Re-plans via `bridge.replan(projectId, megaTaskId, parts, split.why)`.

### 8. `assignByLead`
Asynchronously asks the lead model to allocate crew members to subtask slices when multiple candidate models qualify for each slice.
- **Preconditions**:
  - Subtask slices >= 2, lead model available, and multiple qualifying candidates exist across slices.
  - If each slice has exactly one candidate, user role settings already dictate assignment and lead invocation is bypassed.
- **Execution**:
  - Formats query via `autonomy.assignmentPrompt` including candidate models per slice.
  - Executes single lead inquiry via `opts.askModel`.
  - Parses result via `autonomy.parseAssignment`.
  - Updates assignment via `bridge.reassign(projectId, megaTaskId, choice.assign, choice.why)`.

### 9. `startAutoTask`
Initiates automated planning and task preparation from the project revision and repository facts.
- **Workflow**:
  1. Validates project existence and linked workspace folder.
  2. Resolves timing options (`discretion` or `deadline`).
  3. Inspects canonical repository facts (`inspect(folder)`).
  4. Releases any existing running job cleanly (`releaseRunningJob`).
  5. Determines if clarification interview (`grill me` or `grill with docs`) is needed; if so, composes questions and yields `phase: 'grill'`.
  6. Initializes work plan file (`openWorkPlan`).
  7. Constructs proposed task via `jobFromFacts`.
  8. Configures row state: `holdApproval = true`, `approved = false`, pending task payload ready for user approval.
  9. Yields `{ ok: true, phase: 'plan', job: payload, facts, clearJob }`.

### 10. `openWorkPlan`
Prepares the local repository's `.planwharf/plan.md` file prior to executing automated tasks.
- **Behavior**:
  - Formats goal and steps via `PlanFile.Prepare(folder, { goal, steps, keepWorkPlan })`.
  - Performs safe Git maintenance when inside a valid Git repository: executes `git worktree prune` and orphan sweep (`worktrees.sweepOrphans`).
  - Emits timeline autonomy events and chat notes upon formatting or cleanup warnings.

### 11. `planSteps`
Maps subtasks into human-readable work plan step entries.
- **Mapping logic**:
  - Role determination:
    - `AUDIT` => `"Review"`
    - `RESEARCH` => `"Research"`
    - Matches `/^Verify the work independently/` => `"Verify"`
    - Default => `"Build"`
  - Model label resolution: matches `assignedModelId` or `modelId` against roster; falls back to raw ID or `"unassigned"`.
  - Title: extracts `sub.title` or `sub.slice`.

## Exact C# method signatures, types, and records

In `native/Planwharf.Engine/OrchestratorJobBuilding.cs`:

```csharp
// Port of orchestrator job building and work plan formation.
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Planwharf.Engine;

/// <summary>
/// Domain model for task classification and building.
/// </summary>
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
);

/// <summary>
/// Proposed job specification built from inspection facts.
/// </summary>
public record ProposedJob(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("revision_id")] string? RevisionId = null,
    [property: JsonPropertyName("timing")] string Timing = "discretion",
    [property: JsonPropertyName("deadlineAt")] string? DeadlineAt = null,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("goldModel")] ModelCandidate? GoldModel = null
);

/// <summary>
/// Extra parameters passed when calculating planLines.
/// </summary>
public record PlanExtras(
    [property: JsonPropertyName("dispatch")] IReadOnlyList<DispatchPart>? Dispatch = null,
    [property: JsonPropertyName("job")] JobDescriptor? Job = null,
    [property: JsonPropertyName("checkpoint")] CheckpointRecord? Checkpoint = null
);

/// <summary>
/// Individual worker dispatch slice assignment.
/// </summary>
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

/// <summary>
/// Subtask proposal for multi-slice execution.
/// </summary>
public record SubTaskProposal(
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("slice")] string Slice,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("dependsOn")] IReadOnlyList<int> DependsOn,
    [property: JsonPropertyName("size")] string? Size = null,
    [property: JsonPropertyName("leadLine")] string? LeadLine = null,
    [property: JsonPropertyName("scope")] IReadOnlyList<string>? Scope = null
);

/// <summary>
/// Formatted work plan step entry for markdown plan file.
/// </summary>
public record PlanStepEntry(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("title")] string Title
);

/// <summary>
/// Result of opening and preparing .planwharf/plan.md.
/// </summary>
public record WorkPlanResult(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("chat")] string? Chat = null
);

/// <summary>
/// Result of evaluation tick decide().
/// </summary>
public record DecideOutcome(
    [property: JsonPropertyName("row")] ProjectRow Row,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("advanced")] bool Advanced = false,
    [property: JsonPropertyName("error")] string? Error = null
);

/// <summary>
/// Options for initiating startAutoTask.
/// </summary>
public record StartAutoTaskOptions(
    [property: JsonPropertyName("timing")] string Timing = "discretion",
    [property: JsonPropertyName("deadlineAt")] string? DeadlineAt = null,
    [property: JsonPropertyName("grillContext")] string? GrillContext = null,
    [property: JsonPropertyName("goldModel")] ModelCandidate? GoldModel = null
);

/// <summary>
/// Result yielded by startAutoTask.
/// </summary>
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

/// <summary>
/// Main domain service for job classification, planning, and task construction.
/// </summary>
public static class OrchestratorJobBuilding
{
    public const string VerifySliceTitle = "Verify the work independently";

    /// <summary>
    /// Pure classification of task into "hard", "build", or "inspect".
    /// </summary>
    public static string JobKind(JobDescriptor? job);
    public static string JobKind(string? title, string? detail);

    /// <summary>
    /// Pure calculation of parallel worker slice count.
    /// </summary>
    public static int SliceCount(JobDescriptor? job, InspectionFacts? facts, int eligible);

    /// <summary>
    /// Consumes worker candidates and facts to generate concrete dispatch slices.
    /// </summary>
    public static IReadOnlyList<DispatchPart> BuildDispatchPlan(
        IReadOnlyList<ModelCandidate> workers,
        JobDescriptor? job,
        InspectionFacts? facts
    );

    /// <summary>
    /// Constructs a proposed job from latest revision and repository facts.
    /// </summary>
    public static ProposedJob JobFromFacts(Revision? revision, InspectionFacts? facts);

    /// <summary>
    /// Pure generation of plan lines for status display.
    /// </summary>
    public static IReadOnlyList<string> PlanLines(
        ProjectItem item,
        InspectionFacts? facts,
        ModelCandidate shown,
        bool active,
        PlanExtras? extras = null
    );

    /// <summary>
    /// Maps subtasks into human-readable plan step entries.
    /// </summary>
    public static IReadOnlyList<PlanStepEntry> PlanSteps(
        IReadOnlyList<SubTaskProposal>? subs,
        IReadOnlyList<ModelCandidate> roster
    );

    /// <summary>
    /// Prepares .planwharf/plan.md and runs safe worktree cleanup.
    /// </summary>
    public static WorkPlanResult OpenWorkPlan(
        string projectId,
        ProjectItem item,
        IReadOnlyList<PlanStepEntry>? steps,
        IGitOperations? git,
        IExecutionBridge? bridge,
        bool keepWorkPlan = false
    );

    /// <summary>
    /// Evaluates project progress, completion evidence, or triggers bridge advance.
    /// </summary>
    public static DecideOutcome Decide(
        ProjectItem item,
        ProjectRow row,
        IExecutionBridge bridge,
        ICrewSelector crewSelector,
        IFolderInspector inspector,
        IProcessLiveness liveness,
        DecideOptions options
    );

    /// <summary>
    /// Invokes lead model to split task into parallel subtasks with verification.
    /// </summary>
    public static Task<bool> PlanByLeadAsync(
        string projectId,
        ProjectItem item,
        string megaTaskId,
        ILeadModelAdapter leadAdapter,
        IExecutionBridge bridge,
        IAutonomyOperations autonomyOps,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Invokes lead model to allocate crew to subtasks when multiple candidates qualify.
    /// </summary>
    public static Task AssignByLeadAsync(
        string projectId,
        ProjectItem item,
        string megaTaskId,
        ILeadModelAdapter leadAdapter,
        IExecutionBridge bridge,
        IAutonomyOperations autonomyOps,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Prepares automated task, checks grill status, and builds proposed plan.
    /// </summary>
    public static Task<StartAutoTaskResult> StartAutoTaskAsync(
        string projectId,
        StartAutoTaskOptions? options,
        IProjectReader projectReader,
        IFolderInspector inspector,
        IWorkPlanManager workPlanMgr,
        IExecutionBridge bridge,
        IGrillComposer grillComposer,
        CancellationToken cancellationToken = default
    );
}
```

## Mapped JavaScript tests

The behavior specified for `OrchestratorJobBuilding` maps directly to established test suites in `engine/`. In Job 49, unit test coverage will be implemented in `native/Planwharf.Engine.Tests/OrchestratorJobBuildingTests.cs`, while the original JavaScript test files continue serving as integration verification gates:

| JavaScript test suite | Ported coverage in `OrchestratorJobBuildingTests.cs` | Tested behaviors and invariants |
| --- | --- | --- |
| `job-title.test.cjs` | `JobFromFacts_GoalPropagation_Tests` | - Job titled after goal agreed with user, even in Git repos.<br>- Complete goal preserved in task detail up to 4,000 chars.<br>- Fallback to `Continue: {subject}` or `Continue from the repo` when goal is absent.<br>- Up to 12 dirty paths and 5 recent commits formatted accurately. |
| `orchestrator.test.cjs` | `JobKind_SliceCount_PlanLines_Tests` | - Pure evaluation of `jobKind` (`hard`, `build`, `inspect`).<br>- Calculation of `sliceCount` based on kind, dirty paths, and eligible models.<br>- Deterministic `planLines` formatting for single and multi-slice workflows.<br>- Handling of timing deadlines vs worker discretion. |
| `phase1.test.cjs` | `LegacyPhase1_JobFlow_Tests` | - Phase 1 legacy task inspection and plan synthesis.<br>- Release of previous running jobs upon new task start.<br>- Proper population of `holdApproval` and `approved` flags. |
| `phase2.test.cjs` | `ExecutionV2_Decide_Tests` | - Recognition of `executionVersion: 2` jobs in `decide()`.<br>- Translation of `proposedSubTasks` or `dispatchPlan` into bridge proposals.<br>- Bridge advancement coordination without redundant inspections. |
| `phase2-git.test.cjs` | `OpenWorkPlan_GitContainment_Tests` | - Inspection facts integration with Git status.<br>- `openWorkPlan` invoking Git worktree pruning and orphan sweeping.<br>- Fail-closed behavior on Git workspace errors. |
| `task-completion.test.cjs` | `Decide_CompletionVerification_Tests` | - Verification that all slice result files exist and contain `STATUS: COMPLETE`.<br>- Partial completion (e.g. 1 of 2 slices complete) leaves task in running state.<br>- Once all slices are complete, transitions project row and task to `complete`.<br>- Completed assignments are settled once and never redispatched on later polls. |
| `always-approve.test.cjs` | `ApprovalState_Transitions_Tests` | - Auto mode vs manual approval boundaries.<br>- `holdApproval` preventing premature automated dispatch.<br>- Reset of approval upon job ID or fingerprint mutation. |
| `plan-file.test.cjs` (and newline/tick/verify variants) | `OpenWorkPlan_PlanSteps_Tests` | - Preparation of `.planwharf/plan.md` via `openWorkPlan`.<br>- Accurate role mapping in `planSteps` (`Review`, `Research`, `Verify`, `Build`).<br>- Model label resolution against roster with fallback to unassigned. |
| `saving-mode-pr.test.cjs` | `SavingMode_JobBuilding_Tests` | - Integration of saving mode constraints (Normal, Safe, Ultra safe) during task sizing and verifier slice allocation.<br>- Verifier slice isolation and instructions parity. |

## Comparison and parity (Rule 12)

### Pure comparison vectors
In accordance with Rule 12, pure deterministic calculations must be compared byte-for-byte against JavaScript execution using `native/porting/parity/cases.cjs` and recorded in `native/porting/parity/orchestrator-job-building.json`.

#### 1. `sliceCount` pure vectors
The comparison suite must exercise a comprehensive combinatorial matrix:
- **Task classification**:
  - `hard`: e.g. `{ title: "escalate urgent bug", detail: "complex problem" }`
  - `build`: e.g. `{ title: "implement new feature", detail: "write unit tests" }`
  - `inspect`: e.g. `{ title: "review documentation", detail: "check status" }`
- **Dirty file counts**:
  - `0` dirty paths (clean working tree)
  - `3` dirty paths (below threshold)
  - `4` dirty paths (at threshold)
  - `12` dirty paths (high change volume)
- **Eligible worker pool**:
  - `eligible = 0` -> returns `1`
  - `eligible = 1` -> returns `1`
  - `eligible = 2` -> returns `2` (when `hard`, `build`, or `dirty >= 4`), else `1`
  - `eligible = 5` -> returns `2` (capped at max 2 standard slices)
- **Invariants**:
  - When `eligible <= 1`, slice count is strictly `1`.
  - When classification is `inspect` and `dirty < 4`, slice count is strictly `1`.
  - Result is capped at `Math.Min(2, pool)`.

#### 2. `planLines` pure vectors
The comparison suite must assert exact character-level parity across diverse scenario inputs:
- **Matrix dimensions**:
  - Git repository present vs non-git directory (`fileCount` inspect fallback).
  - Clean working tree vs dirty tree (`Uncommitted: N path(s). Read those changes.`).
  - Single worker shown (`Assigned Model (why)` vs `Pick Model (why)`).
  - Multi-worker dispatch (`Split into N slices for parallel workers.`, with indexed slices).
  - Timing constraints: discretionary vs explicit deadline timestamp (`Finish by {deadlineAt}. At that time the orchestrator delivers the output or writes a checkpoint.`).
  - Active vs inactive task status.
  - Presence or absence of checkpoint path (`Resume from checkpoint {path}.`).
- **Exact typography**:
  - Exact English sentence structure, punctuation, capitalization, and Unicode apostrophes (`’` in `worker’s discretion`).

### Scenario parity
Async and stateful interactions (`planByLead`, `assignByLead`, `startAutoTask`, `openWorkPlan`, `decide`) must achieve scenario parity recorded in `native/porting/parity/scenarios.cjs` and replayed in `ScenarioParityTests.cs`:

1. **Controlled Environment**:
   - Synthetic fixed timestamp (`now()`) and deterministic test project directories.
   - Injected `ILeadModelAdapter` providing recorded model responses (including simulated provider refusals and recovery prompts).
   - Injected `IExecutionBridge` capturing proposals, replanning, reassignment, and completion events.
   - Injected `IFolderInspector` and `IGitOperations` simulating Git branches, worktree states, and commit metadata.
2. **Deterministic Replay**:
   - Replay tests in `ScenarioParityTests.cs` must assert that every injected call is consumed in expected sequence with matching payloads.
   - Exact outcome and state transition validation:
     - `planByLead`: verifies prompt composition, weights fitting, part spreading, and resulting bridge replan.
     - `assignByLead`: verifies candidate filtering, role validation, prompt composition, and bridge reassign.
     - `startAutoTask`: verifies grill trigger detection, work plan opening, job creation, and payload generation.
     - `openWorkPlan`: verifies `.planwharf/plan.md` creation and worktree orphan sweeping.
     - `decide`: verifies completion evidence check across multiple result files, approval resets on job mutation, and bridge advance triggers.
3. **Omission and Mutation Resistance**:
   - Omitting any required subtask slice, changing a role assignment, or modifying verification slice instructions must fail scenario validation.
   - Any unhandled exception or missing result file must block task completion transition.

## Migration step: Job 49

The implementation of this specification sheet is assigned to **Job 49**, which represents Step 3 of the Phase 6 orchestrator port.

### Migration Sequence:
1. **Job 47 (Step 1)**: `OrchestratorOrderParsing` (`native/Planwharf.Engine/OrchestratorOrderParsing.cs`)
2. **Job 48 (Step 2)**: `OrchestratorTeamModels` (`native/Planwharf.Engine/OrchestratorTeamModels.cs`)
3. **Job 49 (Step 3 - this module)**: `OrchestratorJobBuilding` (`native/Planwharf.Engine/OrchestratorJobBuilding.cs`)
4. **Job 50 (Step 4)**: `OrchestratorLeadQuestions` (`native/Planwharf.Engine/OrchestratorLeadQuestions.cs`)
5. **Job 51 (Step 5)**: `OrchestratorState` (`native/Planwharf.Engine/OrchestratorState.cs`)
6. **Job 52 (Step 6)**: `Orchestrator` Facade composition root (`native/Planwharf.Engine/Orchestrator.cs`)

### Job 49 Allowed Scope:
When Job 49 executes, it may modify strictly:
- `native/Planwharf.Engine/OrchestratorJobBuilding.cs`
- `native/Planwharf.Engine.Tests/OrchestratorJobBuildingTests.cs`
- `native/porting/parity/cases.cjs` (pure vectors for `sliceCount` and `planLines`)
- `native/porting/parity/orchestrator-job-building.json`
- `native/porting/parity/scenarios.cjs` (scenario definitions for job building)
- `native/porting/parity/scenarios/orchestrator-job-building.json`
- `native/Planwharf.Engine.Tests/ParityTests.cs` and `ScenarioParityTests.cs` (registration of Job 49 suites)
- `engine/parity.test.cjs` and `native/porting/parity/required.json` (floor updates)
- Relevant documentation progress trackers (`docs/NEXT-WORK.md`, `native/porting/PLAN.md`, etc.)

Job 49 must not modify JavaScript engine sources, rewrite peer C# modules, or add external dependencies.
