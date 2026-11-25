# Job 46c (phase 6): OrchestratorLeadQuestions specification sheet

**Goal.** Specify the C# port of lead questioning, prompt generation, grill questionnaire parsing, lead envelopes, and advisory review from `engine/orchestrator.js` into `native/Planwharf.Engine/OrchestratorLeadQuestions.cs`. This is a Phase 6 specification sheet. Job 50 will implement this module and its tests. The JavaScript engine remains the behavioral specification. This sheet permits no runtime change and no new dependency.

## Goal and evidence

Source base: `port/46-sheets` from refreshed `origin/main`. Existing `.planwharf/plan.md` edits and earlier phase sheets precede this document and must be preserved. Apply the twelve porting rules in [README.md](../README.md), including exact JavaScript output and Native AOT compatibility.

Job 46c defines the fourth module of the approved five-way orchestrator split (Job 44, Job 46):
1. `OrchestratorOrderParsing` (Job 47)
2. `OrchestratorTeamModels` (Job 48)
3. `OrchestratorJobBuilding` (Job 49)
4. `OrchestratorLeadQuestions` (Job 50) — **this specification sheet**
5. `OrchestratorState` (Job 51)
6. `Orchestrator` facade composition root (Job 52)

## Port mapping

| JavaScript source | C# target in `native/Planwharf.Engine/` | Tests in `native/Planwharf.Engine.Tests/` | Owning evidence |
| --- | --- | --- | --- |
| `engine/orchestrator.js` (lead questioning, grill prompt/parsing, state packing, envelopes, decisions) | `OrchestratorLeadQuestions.cs` (first line `// Port of orchestrator lead questions from engine/orchestrator.js.`) | `OrchestratorLeadQuestionsTests.cs` | Prompt generation, questionnaire parsing, action envelopes, handover cascades, advisory reviews, deterministic vectors and scenario tests below |

## Responsibilities and boundaries

`OrchestratorLeadQuestions` encapsulates the reasoning, prompt composition, response unpacking, and clarification workflows between Planwharf and the configured orchestrator lead model.

### 1. Document discovery and collection: `listDocs` and `collectDocs`

- `listDocs(folder)`: Scans a project folder for root markdown documentation files matching `isDocName` (`README.md`, `README`, `AGENTS.md`, `CLAUDE.md`, `SKILL.md`, `CONTRIBUTING.md`) and any `docs/*.md` files within the realpath boundary of `folder`. Ignores `.` and `..`, avoids directory traversal out of bounds via `within`, caps results at 12 entries, and returns an empty list on filesystem or realpath failure.
- `collectDocs(facts)`: Extracts documentation candidates from inspection facts. If `facts.docs` exists and has entries, trims and filters empty items and caps at 12. Otherwise, filters `facts.names` matching `isDocName` and caps at 12.

### 2. Grill questionnaire extraction: `parseGrillQuestions`

- Pure parser converting raw model output text into an array of 0 to 8 question strings.
- Attempts JSON array extraction first via regex `/\[[\s\S]*\]/`. If valid JSON array:
  - String elements are trimmed.
  - Object elements are formatted as `"{title}: {body}"` (or whichever exists), appended with `" Recommended: {recommend}"` if recommendation is present.
  - Non-string/non-object elements are filtered out.
- Fallback for non-JSON or malformed JSON: splits on newlines, strips numbering/bullets/emoji prefixes (`^\s*(?:\d+[.)]|[-*]|❓\s*\*\*Q\d+\*\*\s*[-—–]?)\s*`), removes markdown bold (`**`), trims lines, and selects lines containing `"recommended:"` (case-insensitive) or `?`, capped at 8 entries.

### 3. Grill prompt generation and lifecycle: `grillPrompt` and `grillDone`

- `grillPrompt(item, facts, docs)`: Deterministic prompt generator instructing the lead to interview the user about open decisions at the frontier before planning.
  - Output sections (double-newline separated):
    1. `"You are interviewing the user about this Planwharf project before any planning."`
    2. `"Ask only what blocks a good plan: the open decisions at the frontier, in one round. Word each question so yes accepts your recommended answer."`
    3. `"Project: {item.project.name}"`
    4. Optional `"Goal: {latest.direction}"`
    5. Optional `"Folder: {item.project.folder}"`
    6. Optional `"Latest commit: {facts.lastCommit}"`
    7. Optional `"Top files: {facts.names.slice(0, 12).join(', ')}"`
    8. Optional `"Docs: {docs.join(', ')}"`
    9. `"Return ONLY a JSON array of 3 to 6 strings."`
    10. `"Each string is one frontier question about THIS project, with \"Recommended: ...\" in the same string."`
    11. `"Do not ask for facts listed above. Do not implement."`
- `grillDone(item)`: Checks whether any revision in `item.revisions` has `mode === 'grill'` or title `'Grill me'` or `'Grill with the docs'`.

### 4. Deterministic question answering and project briefing: `answerQuestion`

- Fast path answering user queries without invoking an AI lead model when the query is purely informational or serves as a fallback.
- Returns `{ ok, answer, projectId, reason? }`.
- Rules and query classifications:
  1. Empty question: returns `{ ok: false, reason: 'empty question', answer: '', projectId: null }`.
  2. Fleet or all-project status (`/\b(all projects|tutti|fleet|ogni progetto)\b/i`): renders `briefRow` for every notebook project separated by newlines.
  3. Single project target resolution via `pickAskedProject(snap, projectId, q)`. If no projects exist or target cannot be matched, returns `"{lead}: no projects in the notebook."`.
  4. Specific worker query: matches worker label or model ID against `row.dispatch`. Returns worker slice title, interpreted activity, process PID, and alive/stopped state.
  5. Slice/split inquiry (`/\b(split|spacchett|slice|why two|perch[eé]|quanti worker)\b/i`): reports whether work was divided into multiple slices or assigned to a single worker.
  6. Quota inquiry (`/\b(quota|remaining|residu|left|utilizzo|usage|percent)\b/i`): summarizes plan quota windows, percent used, percent remaining, and exhaustion states.
  7. Process monitoring inquiry (`/\b(monitor|vivo|alive|stall|watch|controll)\b/i`): returns orchestrator activity status, live process count vs worker count, and waiting-for reason.
  8. Next action inquiry (`/\b(next|prossimo|waiting|aspet)\b/i`): returns `waitingFor` and `next` queues.
  9. Log output inquiry (`/\b(log|output|detto|said)\b/i`): returns the last interpreted activity line from `row.workerTail`.
  10. Default fallback: returns structured summary via `briefRow(row, lead)`.

### 5. Context isolation and state packing: `packState`

- Assembles the project-scoped prompt state passed to the lead model.
- Strictly bounds project data:
  - `executionHandoff`: contains only `megaTasks`, `subTasks`, `results`, and `checkpoints` where `projectId === row.id`. Precludes leaking executions from other projects.
  - `researches`: filters to `research.projectId === row.id`.
  - `focus`: builds rich context for the active project: folder, status, label, goal, open decisions (capped at 12), plan lines (capped at 16), latest checkpoint, dirty files (capped at 12), worker process statuses, monitoring summaries, and tail log activity.
  - `others`: provides minimal unprivileged metadata (`id`, `name`, `status`, `job`) for concurrent notebook projects, strictly omitting other projects' private discussions, research findings, or file lists.
  - `fleet` and `plans`: supplies available model capabilities, quota windows, and role eligibility.

### 6. Structured lead action parsing: `leadEnvelope`

- Pure parser unpacking lead responses into an `answer` text and an array of structured `actions`.
- Strips surrounding markdown JSON fences (```json ... ```).
- Validates that text starting with `{` conforms strictly to the schema `{ answer: string, actions: any[] }`.
- Unknown properties beyond `answer` and `actions` cause rejection with `{ answer: text, actions: [], error: 'Invalid lead action envelope; no workers were dispatched.' }`.
- JSON parse errors produce `{ answer: text, actions: [], error: 'Unreadable lead action envelope; no workers were dispatched.' }`.
- Responses not starting with `{` are treated as pure text: `{ answer: text, actions: [] }`.

### 7. Clarification interview rounds: `composeGrillRound`

- Coordinates a grill clarification round for a project revision before planning work begins.
- Inputs: project revision item, canonical inspection facts, and interview kind (`'me'` or `'docs'`).
- If `kind === 'docs'`, invokes `collectDocs(facts)` to supply documentation paths to the prompt.
- When an active lead model and `opts.askModel` are available:
  - Constructs `grillPrompt(item, facts, docs)` with attached work plan.
  - Dispatches query with `clarificationKind: kind` and `allowActions: false`.
  - Unpacks text with `leadEnvelope` and extracts questions via `parseGrillQuestions`.
  - If valid questions are extracted, returns them directly.
- Fallback: on lead error, network failure, unparseable output, or missing lead, delegates deterministically to `Plan.grillFrontier`.

### 8. Autonomous decisions and supervisor nudges: `askLeadDecision`

- Facilitates autonomous decision-making loops (evaluation prompts, supervisor nudges, stalled-work continuation).
- Resilience and handover cascade:
  - Up to 3 retry rounds across distinct providers: if a model fails, times out, or produces unreadable output, autonomy does not abort while another eligible lead exists.
  - Unreadable decision responses trigger exactly one strict retry with the same lead via `autonomy.strictDecisionPrompt(question)` requesting JSON only.
  - If a lead refuses work, records the refusal with timestamp, logs quota notes, and falls back to an alternate provider lead.
- Parsing and effects:
  - Parses decision payload using `autonomy.parseDecision(text)`.
  - Captures `leadSummary` on the evaluated MegaTask.
  - Records up to 50 latest assumptions in `settings.assumptions`.
  - If `decision.done` is true, stops autonomy with `GOAL_REACHED`.
  - If `decision.next` is present, validates `maxMegaTasks` limit and prepares the next task via `opts.prepareTask`.

### 9. Autonomy/research review and research dispatch authorization

- Autonomy/research review (`reviewSnapshot`):
  - Dispatches an advisory question to a configured supervisor model.
  - Enforces supervisor eligibility: model must be enabled, available, possess `supervisor` role, pass quota checks, and be supported by supervisor capabilities.
  - Never substitutes a fallback model; throws explicit error if the requested supervisor is unavailable.
  - Incurs model usage cost, dispatches query with `allowActions: false` and `purpose: 'supervisor'`, and records completion in the execution ledger.
- Research authorization enforcement in `ask`:
  - Enforces that actions of type `dispatch_research` are only accepted when the user's message explicitly requests research (`researchIntent`).
  - Unsolicited research actions are rejected with `"Research was not dispatched: this message did not authorize research work."`.

## C# Method signatures, types, and records

The C# port resides in `native/Planwharf.Engine/OrchestratorLeadQuestions.cs` within namespace `Planwharf.Engine`. It relies exclusively on Native AOT compatible structures (`System.Text.Json.Nodes`, explicit records, typed delegates).

### Method signatures

```csharp
namespace Planwharf.Engine;

public static class OrchestratorLeadQuestions
{
    // Pure document scanning and collection
    public static string[] ListDocs(string? folder, IFileSystem? fs = null);
    public static string[] CollectDocs(InspectionFacts? facts);

    // Grill questionnaire parsing and prompt generation
    public static string[] ParseGrillQuestions(string? text);
    public static string GrillPrompt(NotebookProject item, InspectionFacts? facts, IReadOnlyList<string>? docs);
    public static bool GrillDone(NotebookProject? item);

    // Fast deterministic question answering
    public static DeterministicAnswer AnswerQuestion(string question, OrchestratorSnapshot snap, string? projectId);

    // State packing and envelope parsing
    public static JsonObject PackState(OrchestratorSnapshot snap, ProjectRow? row, string? stateFile = null);
    public static LeadEnvelope LeadEnvelope(string? answer, JsonArray? actions = null);

    // Asynchronous lead interaction workflows
    public static ValueTask<IReadOnlyList<string>> ComposeGrillRoundAsync(
        NotebookProject item,
        InspectionFacts facts,
        string kind,
        ILeadModelAdapter? leadAdapter,
        CancellationToken cancellationToken = default);

    public static ValueTask<LeadDecisionResult> AskLeadDecisionAsync(
        string projectId,
        NotebookProject item,
        AutonomySettings settings,
        string question,
        ILeadModelAdapter leadAdapter,
        IReadOnlyList<ModelCatalogEntry> roster,
        string? evalMegaTaskId = null,
        CancellationToken cancellationToken = default);

    public static ValueTask<LeadAskTurnResult> AskAsync(
        LeadAskTurnRequest request,
        ILeadModelAdapter leadAdapter,
        CancellationToken cancellationToken = default);

    public static ValueTask<SupervisorReviewResult> ReviewSnapshotAsync(
        SupervisorReviewRequest request,
        ILeadModelAdapter leadAdapter,
        CancellationToken cancellationToken = default);
}
```

### Injected lead adapter contract

```csharp
public interface ILeadModelAdapter
{
    ValueTask<LeadSpokenResponse> AskModelAsync(LeadAskInvocation invocation, CancellationToken cancellationToken = default);
}

public sealed record LeadAskInvocation(
    string Question,
    ModelCatalogEntry Model,
    string? ProjectId,
    string? AskKey,
    string? Folder,
    string? Github,
    JsonObject State,
    bool AllowActions,
    string? ChatMode = null,
    bool WorkRunning = false,
    string? ClarificationKind = null,
    string? Purpose = null,
    Action<LeadAskProgress>? OnProgress = null);

public sealed record LeadAskProgress(
    string? Thinking,
    string? Answer,
    long Ms);

public sealed record LeadSpokenResponse(
    string Answer,
    string? Thinking = null,
    long Ms = 0,
    JsonArray? Actions = null,
    string? Failure = null,
    DiagnosticInfo? Diagnostic = null,
    int? ProcessExit = null,
    TokenUsageInfo? Tokens = null);
```

### Records and data structures

```csharp
public sealed record DeterministicAnswer(
    bool Ok,
    string Answer,
    string? ProjectId,
    string? Reason = null);

public sealed record LeadEnvelope(
    string Answer,
    IReadOnlyList<LeadAction> Actions,
    string? Error = null);

public sealed record LeadAction(
    string Type,
    string? ProjectId,
    string? ModelId,
    string? Title,
    string? Detail,
    JsonArray? Assignments = null,
    JsonObject? Metadata = null);

public sealed record LeadDecisionResult(
    bool Ok,
    AutonomyDecision? Decision,
    string? FailureReason = null,
    string? StopReason = null);

public sealed record LeadAskTurnRequest(
    string Question,
    string? ProjectId,
    string? ChatSessionId,
    string RequestMode,
    string? RequestedGrillKind,
    GoldModelConstraint? GoldModel,
    OrchestratorSnapshot Snapshot,
    ProjectRow? TargetProject,
    IReadOnlyList<AskTurn> ConversationHistory,
    IReadOnlyList<ModelCatalogEntry> Roster,
    Func<bool> IsCurrent);

public sealed record LeadAskTurnResult(
    bool Ok,
    string Answer,
    string? Thinking,
    long Ms,
    DiagnosticInfo? Diagnostic,
    ModelCatalogEntry? InvokedLead,
    IReadOnlyList<LeadActionResult> ActionResults,
    string? NewTaskId,
    string? Reason = null);

public sealed record LeadActionResult(
    bool Ok,
    string? Text,
    string? Reason,
    LeadAction Action,
    JsonObject? Payload = null);

public sealed record SupervisorReviewRequest(
    string ProjectId,
    string ModelId,
    string Question,
    JsonObject Facts,
    JsonArray History,
    IReadOnlyList<ModelCatalogEntry> Roster);

public sealed record SupervisorReviewResult(
    string Answer,
    string Provider,
    string Model);
```

## State boundaries and composition root separation

To guarantee unidirectional data flow and prevent split-brain store mutations:

1. **Input boundaries:**
   - `OrchestratorLeadQuestions` accepts a project-scoped conversation snapshot (`OrchestratorSnapshot`, `NotebookProject`, `InspectionFacts`, `ProjectRow`, `IReadOnlyList<AskTurn>`) and an injected lead adapter (`ILeadModelAdapter`).
   - It possesses **no** direct access to mutable filesystem singletons, active database stores, or global config files.
   - It performs zero direct mutation on `orchestrator.json`.

2. **Output boundaries:**
   - Returns structured lead queries, proposed actions, and parsed responses (`LeadEnvelope`, `LeadAskTurnResult`, `DeterministicAnswer`, `LeadDecisionResult`).
   - Does not dispatch workers, spawn child processes, or mutate Git worktrees.

3. **Composition root ownership (in `Orchestrator.cs`):**
   - **Per-project ask cancellation tokens:** Composition root owns per-project ask cancellation tokens (`askTokens`, `askEpoch`, `askCurrent(token)`). A cancellation in project A never cancels active thinking or lead calls in project B.
   - **Invocation ledger:** Composition root owns the invocation ledger, starting and finishing ledger records (`ledger.start`, `ledger.finish`) to track token usage, cost, and execution duration.
   - **Recipient resolution and routing:** Composition root owns recipient resolution, resolving project names, enforcing command precedence (`looksLikeQuestion`, `looksLikeWork`, `parseOrder`), and verifying recipient scoping before passing control to lead questions.
   - **Durable chat mutations:** Composition root owns durable chat mutations, recording chat history (`store.asks.push`), updating briefing baselines (`store.briefingBaselines`), committing autonomy settings, updating project notes, and calling atomic `persist()` with `renameWithRetry`.

## Mapped JavaScript tests

The C# unit test suite `OrchestratorLeadQuestionsTests.cs` maps directly to behavior verified across the JavaScript test files:

| JavaScript test file | Mapped behaviors and assertions in C# | Test characteristics |
| --- | --- | --- |
| `engine/orchestrator.test.cjs` | - `parseGrillQuestions` handles single-line and multiline arrays, recommended suffix parsing, and question caps.<br>- `answerQuestion` answers quota inquiries with percent used/left and window details; process monitoring status; worker slice details.<br>- `ORCH_NAME` constant parity.<br>- Lead action envelopes unpack valid JSON payloads or report unreadable errors without throwing. | Fast deterministic and table-driven unit tests. |
| `engine/project-context.test.cjs` | - `packState` isolates focused project history, preventing leakage of private discussions or notes from other projects in multi-project notebooks.<br>- `contextArchive` is null for scoped projects.<br>- Researches and handoffs are strictly filtered to the focused project ID.<br>- Cross-project discussion boundaries are preserved even under shared chat session IDs. | Isolated in-memory snapshot fixtures; asserts non-leakage via regex and field matching. |
| `engine/project-recipient.test.cjs` | - Commands explicitly naming other projects are rejected before lead dispatch.<br>- Ambiguous project references or queries matching multiple projects fail safely.<br>- State packing reflects target recipient folder and metadata. | Parameterized recipient resolution tests. |
| `engine/conversation-history.test.cjs` | - `ask` preserves chronological ordering of retained questions/answers.<br>- Unicode question/answer text (Japanese, accented Italian characters) is preserved without mangling.<br>- History length bounding and earlier turn accounting. | UTF-8 integrity and boundary tests. |
| `engine/grill-chat.test.cjs` | - Grill mode starts a real lead turn with grill instructions.<br>- Grill follow-up decisions remain in chat without requiring an existing source folder.<br>- Missing lead is reported in the conversation.<br>- Explicit Start after chat grilling prepares the discussed task without reopening the questionnaire. | Interactive grill conversation simulations with fake lead adapter. |
| `engine/autonomy.test.cjs` | - Autonomous step decisions (`askLeadDecision`) handle evaluation, continue prompts, and nudges.<br>- Stalled steps with repeated errors trigger owner decisions.<br>- Evaluation cleanly skips lead calls on clear test and verification evidence (`cleanFinish`).<br>- Retries on refused or unreadable lead answers with strict JSON fallback. | State machine transition and lead fallback scenarios. |
| `engine/parallel-projects.test.cjs` | - Two projects querying the lead concurrently execute without mutual interference.<br>- Per-project cancellation tokens ensure stopping or asking in project A does not abort or mutate project B. | Multi-threaded async stress tests with concurrent fake lead delays. |
| `engine/research-dispatch.test.cjs` | - Structured research actions (`dispatch_research`) in lead envelopes require explicit research intent from the user.<br>- Unsolicited research actions are rejected and workers are not dispatched.<br>- Exact model constraints (e.g. Gold models) are enforced without silent substitution. | Action validation and security containment tests. |

## Comparison and parity (Rule 12)

### Pure parity and deterministic vectors

Detail deterministic prompt generation and grill questionnaire parsing vectors under Rule 12:

1. **Grill questionnaire parsing vectors** (`parseGrillQuestions` in `cases.cjs`):
   - Valid JSON array of question strings with `"Recommended: yes"`.
   - Valid JSON array of structured objects (`{ title, body, recommend }`).
   - Markdown list with bullets, numbers, and `❓ **Q1**` prefixes.
   - Text containing mixed non-question statements and frontier questions with `?`.
   - Text exceeding 8 questions (verifying exact 8-element truncation).
   - Empty, whitespace, and non-string inputs.
2. **Deterministic prompt generation** (`grillPrompt` and autonomy prompts):
   - `grillPrompt`: full synthetic notebook project with direction, folder, last commit, top files, and doc list; minimal project with only project name (verifying omission of empty optional lines).
   - Autonomy prompts: `evaluationPrompt`, `nudgePrompt`, `continuePrompt`, `splitPrompt`, `assignmentPrompt`, and `strictDecisionPrompt` generated identically to JavaScript outputs.
3. **Document collection vectors** (`collectDocs`):
   - Facts containing explicit `docs` array.
   - Facts containing only `names` array with markdown files (`README.md`, `CLAUDE.md`, `docs/spec.md`) and non-doc files (`index.js`, `package.json`).
   - Caps results at 12 items.
4. **Lead envelope vectors** (`leadEnvelope`):
   - Plain text response without JSON fences.
   - Markdown fenced JSON `{ "answer": "text", "actions": [...] }`.
   - Malformed JSON string triggering envelope error.
   - Schema violation (unexpected root keys).

### Scenario parity

Extend `native/porting/parity/scenarios.cjs` with scenario fixtures capturing:
- Complete lead questioning turn with streamed progress (`onProgress`) and final envelope parsing.
- Handover cascade:
  - Lead 1 returns process failure -> retried once with same lead -> fails again -> switches to next allowed lead from another provider.
  - Lead 1 returns `PROVIDER_USAGE_LIMIT` or `CREDITS_NEEDED` -> hands over immediately to alternate company lead.
  - Lead returns unreadable reply in autonomy loop -> triggers strict decision prompt -> parses valid JSON on second attempt.
  - User cancels request mid-turn -> `askCurrent` returns false -> request terminates cleanly without persisting corrupt state.
- Scenario replays in `native/Planwharf.Engine.Tests/ScenarioParityTests.cs` must consume every recorded reply, assert ordered events, and verify exact byte-level parity with JavaScript engine outputs.

## Job 50 migration step

Job 50 is Step 4 of the Phase 6 orchestrator porting plan. It implements `OrchestratorLeadQuestions.cs` and integrates it into the engine.

### Files owned by Job 50

Job 50 may edit exactly:
- `native/Planwharf.Engine/OrchestratorLeadQuestions.cs`
- `native/Planwharf.Engine.Tests/OrchestratorLeadQuestionsTests.cs`
- `native/porting/parity/cases.cjs`, `record.cjs`, `required.json`, and generated `native/porting/parity/orchestrator-lead-questions.json`
- `native/porting/parity/scenarios.cjs`, and generated `native/porting/parity/scenarios/orchestrator-lead-questions.json`
- `native/Planwharf.Engine.Tests/ScenarioParityTests.cs` and `native/Planwharf.Engine.Tests/ParityTests.cs`
- Progress tracking files: `docs/NEXT-WORK.md`, `docs/BUILT-WITH-PLANWHARF.md`, `native/porting/PLAN.md`, and `.planwharf/plan.md`

### Acceptance gates for Job 50

Before landing Job 50, all of the following commands must execute cleanly:

```text
node native/porting/parity/record.cjs
node native/porting/parity/record.cjs --check
node native/porting/parity/scenarios.cjs
node native/porting/parity/scenarios.cjs --check
node --test engine/orchestrator.test.cjs
node --test engine/project-context.test.cjs
node --test engine/project-recipient.test.cjs
node --test engine/conversation-history.test.cjs
node --test engine/grill-chat.test.cjs
node --test engine/autonomy.test.cjs
node --test engine/parallel-projects.test.cjs
node --test engine/research-dispatch.test.cjs
dotnet test native/Planwharf.Engine.Tests -c Release --filter OrchestratorLeadQuestionsTests
dotnet test native/Planwharf.Engine.Tests -c Release --filter ScenarioParityTests
dotnet publish native/Planwharf.Engine -c Release -r win-x64 -p:PublishAot=true -p:NativeLib=Shared
git diff --check
```
