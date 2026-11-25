# Job 46e (phase 6): OrchestratorOrderParsing specification sheet

**Goal.** Specify the C# port of the order parsing, command interpretation, intent classification, and order routing subsystem extracted from `engine/orchestrator.js` into `native/Planwharf.Engine/OrchestratorOrderParsing.cs`, its unit tests in `native/Planwharf.Engine.Tests/OrchestratorOrderParsingTests.cs`, pure parity vectors in `native/porting/parity/cases.cjs`, and migration plan as Step 1 (Job 47) of Phase 6.

## 1. Context and Architectural Boundaries

In `engine/orchestrator.js`, user prompt processing, short command matching, intent classification, and order routing are embedded inside the `createOrchestrator` closure. Under the Phase 6 orchestrator decomposition approved following Job 45, this logic is factored into a dedicated, pure functional module: `OrchestratorOrderParsing`.

### 1.1 State and Seam Boundaries
- **Pure Functional Parsing**: The module is strictly stateless and side-effect-free. It performs pure string analysis, regex classification, and structure mapping against immutable snapshots of models and project rows.
- **Zero I/O and Process Isolation**: No filesystem operations, process spawning, network calls, clock queries, or mutable store access occur inside this module.
- **Explicit Error Reporting**: Ambiguous project targets, conflicting directives, or missing project recipients for scoped actions return explicit error records rather than throwing exceptions or mutating global state.
- **Composition Seam**: The orchestrator composition root (`createOrchestrator`) invokes `OrchestratorOrderParsing` to translate raw incoming user messages and session history into typed intent records (`OrderIntent`, `ParsedOrder`, `OrderRouteResult`), validates authorization and session locks, and then dispatches the action via `runOrder` or execution bridges.

---

## 2. Responsibilities and Behavioral Specifications

The module encapsulates seven core analytical capabilities:

### 2.1 `looksLikeQuestion`
- **Purpose**: Detects whether an incoming user utterance is an informational inquiry rather than an operational command or instruction.
- **Behavior**:
  - Trims whitespace from the input string.
  - Returns `true` if the string ends with a question mark (`?`).
  - Returns `true` if the string starts with interrogative prefixes (case-insensitive):
    - English: `how `, `what `, `why `, `who `, `where `, `when `, `which `, `status\b`, `quota\b`, `how far`.
    - Italian: `quanto `, `quale `, `come sta`, `come mai`, `che ne `, `che sta `, `che fa `, `chi `, `perch` (matching both `perché` and `perche`).
  - Returns `false` otherwise.

### 2.2 `looksLikeWork`
- **Purpose**: Detects whether an utterance expresses an imperative instruction to perform work, dispatch workers, or proceed with development tasks.
- **Behavior**:
  - Trims whitespace.
  - Matches exact short work trigger phrases: `^(do it|fallo|procedi|smista|go)$` (case-insensitive).
  - Matches word-boundary work keywords and phrases across English and Italian:
    - English: `implement(?:a|are)?`, `dispatch`, `assign(?:a)? `, `do the (?:job|task|work)`, `continue\b`, `go ahead`, `fix (?:the |this )`.
    - Italian: `smista`, `metti i worker`, `dai il lavoro`, `fai (?:il |la )(?:job|task|lavoro)`, `fallo(?: fare)?`, `lavora(?:ci)?(?: su)?`, `procedi`, `esegui`, `costruisci`, `continua(?:re)?`, `vai avanti`, `sistema `.
  - Returns `false` when no work keywords are present.

### 2.3 `freshTaskIntent`
- **Purpose**: Classifies whether a message introduces a new task request to be planned or prepared by the orchestrator (via `prepareTask`), distinguishing new project work from ongoing chat.
- **Behavior**:
  - Evaluates case-insensitive regex pattern:
    `^(?:please\s+|per favore\s+)?(?:(?:vorrei|voglio|puoi|can you)\s+(?:fare\s+|fai\s+|an?\s+)?)?(?:implement(?:a|are)?|build|fix|sistema|esegui|fai|audit|review|analizza|controlla|verifica)\b`
  - Handles polite courtesy prefixes (`please`, `per favore`) and modal auxiliaries (`vorrei`, `voglio`, `puoi`, `can you`).
  - Detects primary action verbs: `implement`, `implementa`, `implementare`, `build`, `fix`, `sistema`, `esegui`, `fai`, `audit`, `review`, `analizza`, `controlla`, `verifica`.

### 2.4 `checkpointTalk`
- **Purpose**: Generates deterministic user-facing descriptions and activity messages for saved checkpoints based on the interruption reason.
- **Reasons and Output Strings**:
  - `'application update'`:
    - `activity`: `"Paused for an application update. Checkpoint saved."`
    - `note`: `"Paused for an application update. Checkpoint saved. Press Resume after restart."`
    - `orch`: `"Saved unfinished work before updating."`
    - `doing(name, title)`: `$"Paused {name}: {title}. Update checkpoint saved."`
  - `'stop'` or `'shutdown'`:
    - `activity`: `"Stopped. Checkpoint saved."`
    - `note`: `"Stopped. Checkpoint saved. Press Resume to continue."`
    - `orch`: `"Stopped. Checkpoint saved."`
    - `doing(name, title)`: `$"Stopped {name} · {title}. Checkpoint saved."`
  - `'deadline'`:
    - `activity`: `"Deadline reached. Checkpoint saved."`
    - `note`: `"Deadline reached. Checkpoint saved. Resume next session."`
    - `orch`: `"Paused at deadline. Checkpoint saved."`
    - `doing(name, title)`: `$"Deadline reached on {name} · {title}. Checkpoint saved."`
  - Default / arbitrary reason `why`:
    - `activity`: `"Job ended without completing. Checkpoint saved."`
    - `note`: `$"Job ended ({why}). Checkpoint saved. Press Resume to continue."`
    - `orch`: `"Job ended without completing. Checkpoint saved."`
    - `doing(name, title)`: $"{name} · {title} ended without completing ({why}). Checkpoint saved."`

### 2.5 `researchIntent`
- **Purpose**: Identifies explicit user requests to conduct background investigation or research without full worker dispatch.
- **Behavior**:
  - Leading research verbs: starts with `research`, `investigate`, `cerca`, `ricerca`, `indaga`.
  - Delegation phrases: contains command trigger (`fai`, `fa`, `manda`, `assegna`, `chiedi`, `vorrei`, `voglio`, `puoi`, `can you`, `please`, `have`, `ask`, `assign`, `delegate`, `send`, `avvia`) followed anywhere by research target (`research`, `researching`, `ricerca`, `ricerche`, `cercare`, `cerca`, `investiga`, `indaga`, `investigate`).
  - Confirmation of previous research turn: begins with affirmative token (`fallo`, `procedi`, `vai`, `do it`, `go ahead`, `yes`, `sì`, `si`) where the immediate previous conversation turn (`history[0]`) discussed research in either the question or answer.

### 2.6 `pickAskedProject`
- **Purpose**: Deterministically resolves the target project from an explicit `projectId`, project names or IDs embedded in user text, or single-project defaults.
- **Behavior**:
  - If `projectId` is supplied and non-empty, finds the project with matching `id` in `snap.projects`.
  - Searches for project names or IDs mentioned within the user question text (case-insensitive substring match).
  - If no project is mentioned in text, and the snapshot contains exactly one project (`snap.projects.length === 1`), defaults to that project.
  - If multiple projects exist and none is matched, returns `null`.

### 2.7 `parseOrder`
- **Purpose**: Parses operational directives from user text into typed order records.
- **Classification Rules & Precedence**:
  1. Trims question; returns `null` if empty.
  2. Resolves target project row via `pickAskedProject(snap, projectId, q)`.
  3. Strips trailing punctuation (`.` or `!`).
  4. Scans for known models by `label` (length > 2) or `id`:
     - Toggle Off: If model matches and contains `off|disable|spegni|disattiva|turn off` (and not `on` tokens), produces `{ action: "model", id: model.id, enabled: false, label: model.label }`.
     - Toggle On: If model matches and contains `on|enable|accendi|attiva|turn on` (and not `off` tokens), produces `{ action: "model", id: model.id, enabled: true, label: model.label }`.
     - Set Lead: If model matches and contains `orchestrator|as lead|use as|usa come`, produces `{ action: "lead", id: model.id, label: model.label }`.
  5. Scans for project lifecycle commands:
     - `stop|ferma|kill|arresta`: `{ action: "stop", projectId, name }`.
     - `resume|riprendi`: `{ action: "resume", projectId, name }`.
     - `approve run|approva|approve`: `{ action: "approve", projectId, name }`.
     - `auto start|avvia|start`: `{ action: "start", projectId, name }`.
  6. Delegates to Saving Mode parsing:
     - English: `mode normal`, `mode safe`, `mode ultra safe`, `mode ultra-safe`.
     - Italian: `modalità normale`, `modalità sicura`, `modalità ultra sicura`.
     - Returns `{ action: "saving-mode", savingMode: "<mode>", projectId }`.
  7. Delegates to Chat Mode parsing:
     - Direct: `mode talk`, `mode chat`, `modalità chat` -> `{ action: "mode", mode: "talk", chatMode: "talk", projectId }`.
     - Direct: `mode plan`, `modalità plan`, `modalità piano` -> `{ action: "mode", mode: "plan", chatMode: "plan", projectId }`.
     - Reserved: `mode pipeline`, `modalità pipeline` -> `{ action: "reject", mode: "pipeline", projectId }`.
     - Contextual verb switches (`switch to`, `set to`, `passa a`, `passa ad`):
       - `pipeline` -> rejected.
       - `automatic|auto` (excluding `auto start`) -> `{ action: "mode", mode: "auto", projectId }`.
       - `manual|manuale` -> `{ action: "mode", mode: "manual", chatMode: "plan", projectId }`.
       - `talk|chat` -> `{ action: "mode", mode: "talk", chatMode: "talk", projectId }`.
       - `plan|piano` -> `{ action: "mode", mode: "plan", chatMode: "plan", projectId }`.
  8. Returns `null` if no recognized operational pattern matches.

### 2.8 Order Routing in `ask` and `runOrder`
- **Routing Precedence in `ask`**:
  1. **Multi-Project Ambiguity**: If no `projectId` was provided and user text references multiple distinct projects, abort with: `"Message names multiple projects. Select the intended recipient."`
  2. **Grill Command Priority**:
     - Explicit triggers: `grill me`, `grill with docs` / `grill with the docs`.
     - Stop triggers: `stop grill`, `finish grill`, `end grill`, `stop grilling`.
     - If Grill is active or requested, but project cannot be resolved: abort with `"Select the project before starting Grill."`
  3. **Research Intent Priority**:
     - If not Grill and `researchIntent` matches: require project context; if missing, abort with `"Select a project or name it explicitly before dispatching research."`
  4. **Status and Quota Queries**:
     - Quota triggers: `quota`, `quota remaining`, `remaining quota`, `how much quota is left?`, `provider status`, `model status` -> `{ action: "quota", projectId }`.
     - State triggers: `status`, `current process state`, `current state`, `current checkpoint identity`, `current checkpoint`, `selected project` -> `{ action: "state", projectId }`.
  5. **Operational Orders (`parseOrder`)**:
     - For actions requiring project recipient (`start`, `stop`, `resume`, `approve`, `skip`, `watching`): if `projectId` is missing, abort with `"Select a project or name it explicitly before issuing a command."`
     - Gold Model Constraint: If `goldModel` specified and action is not `start`, abort with `"Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active."`
  6. **Fresh Task Intent**:
     - If chatMode is not `'talk'`, no order matched, not Grill, not Research, and `freshTaskIntent` matches: require project; if missing, abort with `"Select the project for this new task."`
- **Order Dispatch in `runOrder`**:
  - Handles execution of parsed orders: `quota`, `state`, `endGrill`, `model`, `lead`, `reject`, `saving-mode`, `mode`, `grillPropose`, `stop`, `resume`, `approve`, `skip`, `start`, `watching`.

---

## 3. C# Method Signatures, Types, and Records

All C# types belong to namespace `Planwharf.Engine` in file `native/Planwharf.Engine/OrchestratorOrderParsing.cs`.

```csharp
namespace Planwharf.Engine;

public static class OrchestratorOrderParsing
{
    // Question & Work Heuristics
    public static bool LooksLikeQuestion(string? question);
    public static bool LooksLikeWork(string? question);
    public static bool FreshTaskIntent(string? question);
    public static bool ResearchIntent(string? question, IReadOnlyList<TurnHistory>? history = null);

    // Checkpoint Descriptions
    public static CheckpointTalkInfo CheckpointTalk(string? reason);

    // Project Selection
    public static ProjectSummary? PickAskedProject(
        OrchestratorSnapshot? snap,
        string? projectId,
        string? question);

    // Order Parsing
    public static ParsedOrder? ParseOrder(
        string? question,
        OrchestratorSnapshot? snap,
        string? projectId);

    // Unified Routing Evaluation
    public static OrderRouteResult RouteAsk(
        string? question,
        OrchestratorSnapshot? snap,
        string? projectId,
        IReadOnlyList<TurnHistory>? conversation,
        string? requestedGrillKind = null,
        string? goldModel = null);
}
```

### 3.1 Supporting Records and Types

```csharp
public sealed record CheckpointTalkInfo(
    string Activity,
    string Note,
    string Orch,
    string DoingTemplate)
{
    public string FormatDoing(string name, string title) =>
        DoingTemplate.Replace("{name}", name).Replace("{title}", title);
}

public sealed record TurnHistory(
    string Question,
    string Answer,
    string? ProjectId = null,
    string? ChatSessionId = null,
    string? GrillKind = null);

public abstract record ParsedOrder(string Action, string? ProjectId)
{
    public string? GoldModel { get; init; }
}

public sealed record ModelToggleOrder(string Id, bool Enabled, string Label)
    : ParsedOrder("model", null);

public sealed record LeadSelectOrder(string Id, string Label)
    : ParsedOrder("lead", null);

public sealed record LifecycleOrder(string Action, string? ProjectId, string? Name)
    : ParsedOrder(Action, ProjectId); // "start", "stop", "resume", "approve"

public sealed record SavingModeOrder(string SavingMode, string? ProjectId)
    : ParsedOrder("saving-mode", ProjectId);

public sealed record ChatModeOrder(string Mode, string? ChatMode, string? ProjectId)
    : ParsedOrder("mode", ProjectId);

public sealed record SystemQueryOrder(string Action, string? ProjectId)
    : ParsedOrder(Action, ProjectId); // "quota", "state", "endGrill"

public sealed record RejectOrder(string Mode, string? ProjectId)
    : ParsedOrder("reject", ProjectId);

public sealed record OrderRouteResult(
    bool Ok,
    string? Reason = null,
    ParsedOrder? Order = null,
    string? GrillKind = null,
    bool ResearchRequested = false,
    bool FreshTask = false,
    ProjectSummary? TargetProject = null);
```

---

## 4. State Boundaries, Preserved Syntax, and Error Text

### 4.1 Pure Functional Boundaries
- **Immutability**: All input snapshots (`OrchestratorSnapshot`, `ProjectSummary`, `ModelCatalogEntry`) are treated as read-only. Methods return freshly allocated records.
- **Side-Effect Free**: No logging, no mutations of `store.asks`, no file persistence, and no process execution.
- **Fail-Closed on Ambiguity**: Ambiguous input yields a typed failure result containing the exact canonical error message.

### 4.2 Exact Error Text Preservation
The C# port must preserve the exact error strings from JavaScript character-for-character:
1. Multi-project ambiguity:
   `"Message names multiple projects. Select the intended recipient."`
2. Missing recipient on Grill:
   `"Select the project before starting Grill."`
3. Missing recipient on Research:
   `"Select a project or name it explicitly before dispatching research."`
4. Missing recipient on Command:
   `"Select a project or name it explicitly before issuing a command."`
5. Invalid Gold Model constraint:
   `"Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active."`
6. Missing recipient on Fresh Task:
   `"Select the project for this new task."`
7. Pipeline rejection:
   `"Pipeline is not available."`
8. Missing project on saving mode order:
   `"project not found"`

### 4.3 Preserved Command Syntax
Both English and Italian commands must be parsed with strict regex equivalence:
- **Model Toggles**:
  - Disable: `off`, `disable`, `spegni`, `disattiva`, `turn off`.
  - Enable: `on`, `enable`, `accendi`, `attiva`, `turn on`.
  - Lead: `orchestrator`, `as lead`, `use as`, `usa come`.
- **Project Lifecycle**:
  - Stop: `stop`, `ferma`, `kill`, `arresta`.
  - Resume: `resume`, `riprendi`.
  - Approve: `approve run`, `approva`, `approve`.
  - Start: `auto start`, `avvia`, `start`.
- **Saving Modes**:
  - Normal: `mode normal`, `modalità normale`, `modalita normale`.
  - Safe: `mode safe`, `modalità sicura`, `modalita sicura`.
  - Ultra Safe: `mode ultra safe`, `mode ultra-safe`, `modalità ultra sicura`, `modalita ultra sicura`.
- **Chat Modes**:
  - Talk: `mode talk`, `mode chat`, `modalità chat`, `modalita chat`.
  - Plan: `mode plan`, `modalità plan`, `modalita plan`, `modalità piano`, `modalita piano`.
  - Auto/Manual: `switch to automatic`, `switch to manual`, `passa a automatico`, `passa a manuale`.

---

## 5. Mapped JavaScript Tests

The test suite in `native/Planwharf.Engine.Tests/OrchestratorOrderParsingTests.cs` maps directly to scenarios across the following JavaScript test suites:

| JavaScript Test Suite | Source Cases Mapped | Coverage & Invariants |
| --- | --- | --- |
| `engine/orchestrator.test.cjs` | Lines 110–130, 357 | `parseOrder` null on questions ("What is Luna doing?"), model disable/enable ("Turn off Luna"), lifecycle commands ("Stop Demo", "Start Demo"), lead selection ("Use Grok as orchestrator"), ignore deprecated commands ("Skip grill on Demo", "Grill off"), mode switches ("Switch to automatic", "Switch to manual"), rejection of inline task text ("Implement the parser on Demo"). |
| `engine/project-recipient.test.cjs` | Lines 38–52 | Prevention of implicit resume from work request; rejection of missing/ambiguous recipients ("Resume", "Resume Fixture alpha" to beta); immunity of conversational statements ("Do not resume Fixture alpha", "The start button should resume work.", "Stop should only affect one worker"). |
| `engine/add-to-work.test.cjs` & `add-to-work-edge.test.cjs` | Full suites | In-flight work detection, distinction between Talk conversation and addition commands, `looksLikeWork` matching for iterative additions without disrupting running workers. |
| `engine/saving-mode.test.cjs` & `saving-modes.test.cjs` | Full suites | Exact English and Italian order parsing (`mode safe`, `modalità sicura`), fallback handling, preservation of target project binding. |
| `engine/start-over.test.cjs` | Full suite | Reset and restart directives; ensuring start and restart commands cleanly isolate new work intents from previous conversation turns. |
| `engine/phase2.test.cjs` & `phase2-git.test.cjs` | Integration suites | Ordering between manual approval commands (`approve run`) and automatic worker scheduling. |
| `engine/phone-decisions.test.cjs` | Decision chat turns | Ensuring decision responses in chat are not misclassified as operational lifecycle commands. |
| `engine/pr-fix.test.cjs` & `pr-fix-*.test.cjs` | PR repair flows | Classification of PR fix directives under `freshTaskIntent` vs general conversation. |

---

## 6. Comparison Vectors in `cases.cjs` under Rule 12

Under Porting Rule 12 (deterministic pure parity), `native/porting/parity/cases.cjs` will be extended with an `orchestrator-order-parsing` export block. The pure recorder (`record.cjs`) will evaluate both JavaScript and C# implementations across comprehensive vector matrices.

### 6.1 `parseOrder` Vectors
Inputs comprise `[question, snapshot, projectId]`:
1. `["Turn off Luna", { models: [{ id: "luna", label: "Luna" }], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "model", id: "luna", enabled: false, label: "Luna" }`
2. `["Turn on luna", { models: [{ id: "luna", label: "Luna" }], projects: [] }, null]` -> `{ action: "model", id: "luna", enabled: true, label: "Luna" }`
3. `["Use Grok as orchestrator", { models: [{ id: "grok", label: "Grok" }], projects: [] }, null]` -> `{ action: "lead", id: "grok", label: "Grok" }`
4. `["Stop Alpha", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, null]` -> `{ action: "stop", projectId: "p1", name: "Alpha" }`
5. `["riprendi", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "resume", projectId: "p1", name: "Alpha" }`
6. `["approva", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "approve", projectId: "p1", name: "Alpha" }`
7. `["avvia", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "start", projectId: "p1", name: "Alpha" }`
8. `["mode safe", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "saving-mode", savingMode: "safe", projectId: "p1" }`
9. `["modalità ultra sicura", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "saving-mode", savingMode: "ultra-safe", projectId: "p1" }`
10. `["mode talk", { models: [], projects: [{ id: "p1", name: "Alpha" }] }, "p1"]` -> `{ action: "mode", mode: "talk", chatMode: "talk", projectId: "p1" }`
11. `["Switch to automatic", { models: [], projects: [] }, null]` -> `{ action: "mode", mode: "auto", projectId: null }`
12. `["Switch to manual", { models: [], projects: [] }, null]` -> `{ action: "mode", mode: "manual", chatMode: "plan", projectId: null }`
13. `["mode pipeline", { models: [], projects: [] }, null]` -> `{ action: "reject", mode: "pipeline", projectId: null }`
14. `["Do not resume Fixture alpha", { models: [], projects: [{ id: "p1", name: "Fixture alpha" }] }, "p1"]` -> `null`
15. `["What is Luna doing?", { models: [{ id: "luna", label: "Luna" }], projects: [] }, null]` -> `null`
16. `["", null, null]` -> `null`
17. `[null, null, null]` -> `null`

### 6.2 `looksLikeWork` Vectors
Inputs comprise `[question]`:
- Truthy: `"do it"`, `"fallo"`, `"procedi"`, `"smista"`, `"go"`, `"implement the auth parser"`, `"implementa il sistema"`, `"dispatch workers"`, `"metti i worker"`, `"dai il lavoro"`, `"vai avanti con la build"`, `"fix the failing test"`, `"sistema il bug"`.
- Falsy: `"What is the current status?"`, `"How far did we get?"`, `"Tell me about the architecture"`, `"Can we discuss the plan?"`, `""`, `null`.

### 6.3 `looksLikeQuestion` Vectors
Inputs comprise `[question]`:
- Truthy: `"Is the build passing?"`, `"status"`, `"quota"`, `"how far along are we"`, `"what is the lead model"`, `"come sta il worker"`, `"perché fallisce"`, `"quanto manca"`, `"quale modello è attivo"`.
- Falsy: `"Proceed with implementation"`, `"Fallo subito"`, `"Start the job"`, `"Turn off Luna"`, `""`, `null`.

---

## 7. Migration Plan (Job 47 as Step 1)

`OrchestratorOrderParsing` is designated as **Step 1** of the Phase 6 orchestrator migration in **Job 47**.

### 7.1 Rationale for Step 1
- Zero dependencies on unported orchestrator components.
- Completely pure and side-effect-free: verified through fast, deterministic parity tests.
- Establishes the typed intent data structures required by Step 2 (`OrchestratorTeamModels`, Job 48) and Step 3 (`OrchestratorJobBuilding`, Job 49).

### 7.2 Job 47 Scope and Deliverables
1. Implement `native/Planwharf.Engine/OrchestratorOrderParsing.cs` with full Native AOT compatibility (`net10.0`).
2. Implement unit tests in `native/Planwharf.Engine.Tests/OrchestratorOrderParsingTests.cs`.
3. Add pure comparison vectors to `native/porting/parity/cases.cjs` and record golden fixture `native/porting/parity/orchestrator-order-parsing.json`.
4. Update `ParityTests.cs` and `required.json` with the new module floor.
5. Verify zero warnings under `dotnet build native/Planwharf.Engine -c Release`.
