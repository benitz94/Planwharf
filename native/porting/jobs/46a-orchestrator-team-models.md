# Job 46a: OrchestratorTeamModels Specification Sheet

**Goal.** Specify the Phase 6 orchestrator sub-module `OrchestratorTeamModels`, responsible for model normalization, roster composition, quota tracking, role and policy enforcement, lead selection/ranking, worker picking, dispatch planning, saving mode filtering, fleet status reflection, and crew reassignment.
**Target C# File:** `native/Planwharf.Engine/OrchestratorTeamModels.cs`
**Target C# Test File:** `native/Planwharf.Engine.Tests/OrchestratorTeamModelsTests.cs`
**Migration Step:** Job 48 (`port/48-team-models`)
**JavaScript Source:** Portions of `engine/orchestrator.js` (lines ~185–380, ~665–670, ~806–860, ~1315–1425, ~1735–1785, ~1825–1885, ~1970–2100, ~2175–2200, ~3630, ~3965–4085, ~4700–4725).

---

## 1. Architectural Role and State Boundaries

In the Phase 6 orchestrator decomposition, `OrchestratorTeamModels` isolates all model evaluation, selection, policy application, and team roster calculation logic into a functional, AOT-compliant engine service.

### State Ownership and Invariants
1. **Immutable Inputs**: Accepts immutable provider catalogs, usage records, policy configurations, project team settings, and exact requested IDs.
2. **Pure Decision Outputs**: Returns candidate eligibility, ranked candidate lists, dispatch plans, fleet status items, and proposed role transitions.
3. **No Direct State Commits**: The composition root (`createOrchestrator` / `Orchestrator.cs`) alone commits team changes, policy modifications, or cache mutations to `store` and durable storage (`orchestrator.json`).
4. **No Model Substitution or Fabricated Evidence**:
   - Exact requested model, provider, and CLI IDs must be strictly preserved without silent replacement or fallback substitution.
   - When a requested model (e.g. Gold model or explicit order target) is unavailable, disabled, or lacks the required role, the module returns an explicit refusal reason (`ok: false`), never substituting an unrequested candidate.
   - Never fabricate completion evidence, quota availability, or model execution status.
5. **No Cross-Module Store Coupling**: Does not import peer modules' mutable storage; consumes typed snapshots and produces deterministic evaluation results.

---

## 2. Core Responsibilities & Behavioral Requirements

### 2.1 `normalizeModels`
Normalizes incoming model definitions from provider catalogs and disk fixtures into canonical `JsonObject` structures:
- Validates model structure: requires non-null object with valid string `id`. Non-conforming items are filtered out.
- Resolves `label`: defaults to `id` if missing or whitespace.
- Route validation: for models with explicit `provider` or `cli`, validates routing with `ModelRouting.ModelRouteError`. If routing is invalid, marks `available: false`, records `unavailableReason = routeError`, and marks `neverUsable: true`.
- Resolves flags:
  - `enabled`: `item.enabled === true && (item.providerCatalog || (item.available !== false && !routeError))`.
  - `available`: `item.available !== false && !routeError`.
  - `neverUsable`: `item.catalogExcluded === true || (item.available !== false && Boolean(routeError))`.
  - `availabilityVerified`: preserved from input.
  - `availabilityWarning`: preserved from input or `null`.
- Standard defaults:
  - `plan`: defaults to `"Unnamed plan"`.
  - `kind`: `"premium"` or `"local"`, otherwise `"default"`.
  - `cost`: finite number `>= 0`, defaults to `1`.
  - `limit`: finite number `> 0`, defaults to `50`.
  - `cli`: trimmed string if non-empty, otherwise omitted (`undefined`).

### 2.2 `usageOf`
Extracts and computes quota usage metrics for a model or plan item:
- Inputs: `item` with optional `used`, `limit`, `remaining`.
- `used`: finite number `>= 0`, defaults to `0`.
- `limit`: finite number `> 0`, defaults to `0`.
- `remaining`: finite number if present, otherwise `Math.Max(0, limit - used)`.
- `pct`: if `limit > 0`, `Math.Min(100, Math.Round((used * 100) / limit))`, else `0`.
- `leftPct`: if `limit > 0`, `Math.Max(0, 100 - pct)`, else `100`.
- Returns `{ used, limit, remaining, pct, leftPct }`.

### 2.3 `leadTier`, `leadRanking`, and `leadQuality`
Models are evaluated for lead roles using a tiered hierarchy:
- **Tiers**: `0` = Premium / Flagship, `1` = Default / Mid, `2` = Local / Light.
- **Claude Family Recognition**:
  - Identified by case-insensitive matching in model name (`label`, `cli`, or `id`):
    - `fable`: tier `0`, order `0`.
    - `opus`: tier `0`, order `1`.
    - `sonnet`: tier `1`, order `2`.
    - `haiku`: tier `2`, order `3`.
- **General Rules**:
  - If catalog specifies `kind` of `"premium"` -> tier `0`, `"local"` -> tier `2`.
  - If model name matches `\b(?:flash|mini|lite|nano|luna)\b` -> tier `2`.
  - Otherwise defaults to tier `1`.
- **`leadRanking(list, plans)`**:
  - Sorts candidates for lead failover/handover:
    1. Stronger tier first (`tier ASC`).
    2. Within the same family, newer version first (`newer(a, b)`).
    3. Known quota before unknown quota (`a.left === null` after known).
    4. More remaining quota first (`b.left - a.left DESC`).
    5. At equal quota, Claude family order (e.g. Fable before Opus).
- **`leadQuality(list)`**:
  - Sorts candidate models for the lead selection menu strictly by inherent quality, irrespective of temporary quota state:
    1. Stronger tier first (`tier ASC`).
    2. Claude family hierarchy (`claudeOrder ASC`).
    3. Within the same family, newer version first.
    4. Stable fallback to original catalog order index (`index ASC`).

### 2.4 `pickWorker` and `pickWorkers`
Deterministic selection of eligible workers for task execution:
- **`pickWorker(models, options)`**:
  - Filters candidates from normalized roster:
    - Must be `enabled` and `available !== false`.
    - Must permit assignment for the target job via `ModelRoles.PermitsAssignment`.
    - Checks required capabilities:
      - `reviewer` / `AUDIT`: `canUseTools !== false`, `canInspectFilesystem !== false`.
      - `research`: `canResearch !== false`.
      - `build`: `canCode !== false`.
    - Provider must be enabled (`providerEnabled !== false`).
    - Excludes models in `skip` list.
    - Usage policy must not be blocked (`usagePolicy.blocked === false`).
    - Quota sufficiency: `providerCatalog || usedOf(usage, id) + cost <= limit`.
    - Plan not exhausted: `!planExhausted(plans, quotaPoolId || plan)`.
    - Allowed by active project saving mode (`allowedBySavingMode(savingMode, item)`).
  - Quota threshold: if `options.low` function provided, drops models where `low(item) === true`.
  - Verifier specialization: if assignment role is `verifier`, applies `ableToVerify(fit)` so stronger non-light models verify first.
  - Generates reason annotation: `chosen.why = $"{kind} job · {chosen.label} on {chosen.plan}; user catalog order"`.
- **`pickWorkers(models, options)`**:
  - Computes desired slice count: `Math.Max(1, count || (building ? 2 : sliceCount(job, facts, eligible)))`.
  - Build jobs default to `2` slices when a second worker is available (builder + independent verifier).
  - Diversity enforcement: when picking the verifier slice (slice index `> 0` for build), sorts remaining pool to prefer models from a different provider than the primary builder.
  - Deducts usage iteratively across picked slices (`usage[chosen.id].used += chosen.cost`) so multi-slice dispatch accurately tracks cumulative allocation.

### 2.5 `dispatchPlan`
Generates concrete dispatch items for execution:
- Slices for `reviewer`:
  1. `"Review the approved scope and report findings."`
  2. `"Review correctness and edge cases; report findings."`
  3. `"Review tests and verification evidence; report findings."`
- Slices for `worker`:
  - Single worker: `["Whole job"]`.
  - Multi-worker:
    1. Implementation slice (referencing uncommitted changes if `facts.dirty.length > 0`).
    2. Independent verification slice: starts with `VERIFY_SLICE` (`"Verify the work independently against the job: read the requirements and run the tests, then go straight to what the tests do not cover..."`).
- Produces dispatch records: `{ modelId, label, slice, why, pid: null, logPath: null }`.

### 2.6 `fleetOf`
Constructs the multi-project fleet status snapshot across all active projects:
- Maps each model to its active assignments across projects.
- Determines model runtime state:
  - `"working"`: any assignment has `status === "watching"`.
  - `"unavailable"`: `available === false`.
  - `"provider-disabled"`: `providerEnabled === false`.
  - `"restricted"`: `!enabled`.
  - `"assigned"`: has assignments and not working.
  - `"idle"`: no active assignments.
- Exposes full role capabilities and quota status for UI and supervisor inspection.


### 2.7 `roster`
Calculates the dynamic, project-aware model roster snapshot:
- **Account & Scope Binding**:
  - Connects models to provider definitions and active accounts (`account.key`, `settingsFolder`).
  - Account scope: `provider.settingsFolder ? provider.id : account.key ? provider.id + ":" + account.key : null`.
  - Automatic account model role initialization: when a new account scope is encountered, initializes `accountModelRoles[scope]` based on existing model roles or default empty permissions, preserving configuration without losing user choices.
- **Permission Resolution Hierarchy**:
  1. Project team roles: `team.roles[teamKey(scope, model.id)]` (if working in project team context).
  2. Account-scoped roles: `accountModelRoles[scope][model.id]` (if account scope active).
  3. Legacy global model roles: `store.modelRoles[model.id]`.
- **Policy & Limit Merging**:
  - Validates and migrates policy: merges model policy with provider-shared policy (`minRemainingPercent`, `warningRemainingPercent`, `alwaysApprove`, `fallbackModelId`).
  - Evaluates quota windows (`fiveHour`, `weekly`) and quota limits from `ModelPolicy.QuotaLimit`.
  - Determines low quota status (`isLow`) against configured or default thresholds (`35%`).
- **Saving Mode Class & Weight Resolution**:
  - Determines base `leadTier` (0, 1, or 2).
  - Evaluates team weight overrides from `store.modelWeights[item.id]`. Team weight (`premium`, `default`, `light`) overrides the line-up tier.
  - Derives `modelClass`: `"flagship"`, `"mid"`, or `"light"`.
- **Active Refusals & 24-Hour Credit Lock**:
  - Checks active refusals in `store.refusals`. If refused, marks `available: false` and sets `unavailableReason` from `refusalSentence`.
  - Marks models requiring usage credits (`creditsMarked`): models marked with `creditsNeeded` are locked out for 24 hours (`CREDITS_DAY = 24 * 60 * 60 * 1000`), marking `available: false`, `neverUsable: true`, `creditsNeeded: true`, preventing endless retry loops.
- **Lead Allowed Status**:
  - Checks if model is in the excluded lead set (`store.notLead` or `team.notLead`).

### 2.8 `selectLead` and Lead Handover
Pure evaluation of lead eligibility and failover:
- Checks currently selected lead ID (`leadId()`).
- Determines quota exhaustion or refusal:
  - If lead is out of quota, below quota threshold (`isLow`), plan exhausted, or has an active refusal, selects the next best candidate via `leadRanking`.
  - Generates clear explanation sentence for handover:
    - On refusal: `"{label} refused the work (credits or quota)."`
    - On low quota: `"quota of {label} is at {left}% (switch at {threshold})."`
    - Generic: `"{label} cannot lead now."`
- Evaluates fallback routes via `resolveUsageFallback` if primary lead is blocked by policy or out of usage.
- Cross-provider failover: if lead provider refused work, selects eligible lead candidate from an alternative provider.

### 2.9 `setLeadAllowed` and `setOrchestrator`
Configuration transitions for project and global lead assignments:
- **`setLeadAllowed(id, allowed, projectId)`**:
  - Validates input: non-empty string `id`, boolean `allowed`.
  - Rejects unknown model: `ok: false, reason: "model not present"`.
  - Protects active lead: if `allowed === false` and model is the current lead, rejects with `"This model is the lead now: choose another lead first."`
  - Updates `notLead` set on project team or global store.
- **`setOrchestrator(id, projectId)`**:
  - If `id` is null or empty string, clears active lead (`orchestratorId = null`).
  - Otherwise validates presence in roster (`"model not present"`).
  - Validates eligibility via `canOrchestrate` (`"model not eligible"`).
  - Updates `orchestratorId` on project team or global store.

### 2.10 Role Policy Enforcement
Enforces distinct agent roles defined in `ModelRoles`:
- **Role IDs**: `orchestrator`, `worker`, `verifier`, `reviewer`, `supervisor`.
- **Capability Rules**:
  - `orchestrator`: requires tool and filesystem inspection capabilities; excluded for local models unless explicitly enabled.
  - `worker`: requires `workerEligible !== false` and `capabilities.workerEligible !== false`.
  - `verifier`: requires `workerEligible !== false` and coding capability (`canCode !== false`).
  - `reviewer`: requires tool and filesystem capabilities, `reviewerEligible !== false`.
  - `supervisor`: strictly limited to models using the API adapter (`(model.adapter || model.provider) === "api"`). Other providers (CLI, local) cannot be granted the supervisor role.
- **Role Mutation Operations**:
  - `setModelRoles(id, roles, projectId)`: validates eligibility for each requested role before setting.
  - `setRoleForAll(role, enabled, projectId)`: bulk updates a role across all models, strictly constrained by each model's `roleEligibility`.
  - `setRolesForMany(updates, projectId)`: applies multi-model batch updates.

### 2.11 Saving Mode Enforcement
Provides cost control across three distinct saving modes:
- **Modes**:
  - `Normal` (`normale`): allows all classes (`flagship`, `mid`, `light`).
  - `Safe` (`sicura`): drops `flagship` models; allows `mid` and `light`.
  - `Ultra safe` (`ultra-safe`, `ultra sicura`): allows `light` models only.
- **Model Classification**:
  - Class is determined by `SavingMode.ModelClass(model, tier)`.
  - An explicit team weight set by the owner (`premium`, `default`, `light`) overrides the catalog tier ranking.
- **Enforcement Points**:
  - Task worker picking (`pickWorker`, `pickWorkers`).
  - PR fix routing (`fixPullRequest`).
  - Research worker dispatch (`researchModel`).
  - Returns clear descriptive errors when saving mode forbids a requested model.

### 2.12 Quota Tracking & Auto-Switch
- Monitors 5-hour and weekly quota windows from provider accounts.
- Evaluates quota thresholds (`fiveHourPercent`, `weeklyPercent`, default `35%`).
- Identifies candidates for auto-switching when quota runs low during execution (`quotaSwitches`).
- Enforces 24-hour lockout for models requiring external usage credits (`creditsMarked`).
- Identifies plan exhaustion across shared quota pools.

### 2.13 Crew Reassignment
Handles dynamic reassignment of models assigned to remaining unstarted steps:
- Validates request payload: requires `type: "reassign_crew"`, matching `projectId`, valid `assignments` map, optional `why`.
- Verifies project state: requires proposed or stopped work (`megaTaskId` present).
- Calls Execution bridge `reassignRemaining(projectId, megaTaskId, assignments, why, by)`.
- Handles status-dependent responses:
  - If `READY_FOR_APPROVAL`: returns text `"Crew changed. Approve the work to start it."`
  - If `RUNNING`: triggers tick to launch pending steps with new models.
  - If checkpointed: returns resume prompt with confirmation requirements if repo changed.


---

## 3. Exact C# Types, Records, and Method Signatures

All types and methods are located in namespace `Planwharf.Engine` in file `native/Planwharf.Engine/OrchestratorTeamModels.cs`. The module utilizes AOT-compatible `System.Text.Json.Nodes` types (`JsonObject`, `JsonArray`, `JsonValue`) and immutable typed records.

### 3.1 Records and Data Transfer Objects

```csharp
namespace Planwharf.Engine;

public sealed record TeamRosterContext
{
    public required JsonNode? Models { get; init; }
    public required JsonObject Store { get; init; }
    public required JsonArray? ProviderDefinitions { get; init; }
    public required JsonArray? PlansLive { get; init; }
    public Func<string>? CurrentProjectId { get; init; }
    public Func<string>? LeadId { get; init; }
    public Func<string>? Now { get; init; }
    public Action? Persist { get; init; }
    public Func<JsonObject, bool>? IsLow { get; init; }
    public Func<Task<JsonObject>>? Tick { get; init; }
    public ExecutionBridge? ExecutionBridge { get; init; }
}

public sealed record UsageStats(
    double Used,
    double Limit,
    double Remaining,
    double Pct,
    double LeftPct
);

public sealed record DispatchPlanItem(
    string ModelId,
    string Label,
    string Slice,
    string Why,
    int? Pid,
    string? LogPath
);

public sealed record FleetAssignment(
    string ProjectId,
    string Project,
    string TaskTitle,
    string? AttemptId,
    bool HistoricalAssignment,
    string Status,
    string Slice,
    int? Pid,
    bool Alive,
    string Activity
);

public sealed record FleetItem(
    string Id,
    string Label,
    string Plan,
    string? ProviderId,
    string QuotaPoolId,
    bool OrchestratorEligible,
    bool WorkerEligible,
    bool ReviewerEligible,
    bool SupervisorEligible,
    JsonObject Roles,
    JsonObject RoleEligibility,
    bool ProviderEnabled,
    string Kind,
    bool Enabled,
    string State,
    IReadOnlyList<FleetAssignment> Assignments
);

public sealed record CrewReassignRequest(
    string Type,
    string ProjectId,
    JsonObject Assignments,
    string? Why
);

public sealed record RoleTransitionProposal(
    string ModelId,
    string Role,
    bool Enabled,
    bool Allowed,
    string? RejectionReason
);
```

### 3.2 Exact Method Signatures in `OrchestratorTeamModels`

```csharp
public static class OrchestratorTeamModels
{
    // Pure Normalization and Calculation Helpers
    public static JsonArray NormalizeModels(JsonNode? models);
    public static JsonObject UsageOf(JsonObject? item);
    public static UsageStats CalculateUsage(JsonObject? item);
    public static int LeadTier(JsonObject? model);
    public static string FamilyOf(JsonObject? model);
    public static JsonArray LeadRanking(JsonArray? list, JsonArray? plans = null);
    public static JsonArray LeadQuality(JsonArray? list);
    public static bool CanOrchestrate(JsonObject? item);
    public static bool AllowedBySavingMode(string? mode, JsonObject? item);
    public static string JobKind(JsonObject? job);
    public static int SliceCount(JsonObject? job, JsonObject? facts, int eligible);

    // Worker and Dispatch Selection
    public static JsonObject? PickWorker(JsonArray? models, JsonObject? options = null);
    public static JsonArray PickWorkers(JsonArray? models, JsonObject? options = null);
    public static JsonArray DispatchPlan(JsonArray? workers, JsonObject? job, JsonObject? facts = null);
    public static JsonArray FleetOf(JsonArray? models, JsonArray? projects, string? leadId, JsonArray? plans = null);

    // Contextual Roster and Team Evaluation
    public static JsonArray BuildRoster(TeamRosterContext context);
    public static JsonObject? SelectLead(TeamRosterContext context);
    public static JsonObject ResearchModel(TeamRosterContext context, string modelId);

    // Team Role, Lead, and Quota Mutations (Proposals & Execution Bridge delegation)
    public static JsonObject SetLeadAllowed(TeamRosterContext context, string id, bool allowed, string? projectId = null);
    public static JsonObject SetOrchestrator(TeamRosterContext context, string? id, string? projectId = null);
    public static JsonObject SetModelRoles(TeamRosterContext context, string id, JsonObject roles, string? projectId = null);
    public static JsonObject SetRoleForAll(TeamRosterContext context, string role, bool enabled, string? projectId = null);
    public static JsonObject SetRolesForMany(TeamRosterContext context, JsonArray updates, string? projectId = null);
    public static JsonObject SetGoldModel(TeamRosterContext context, string? id);

    // Crew Reassignment
    public static Task<JsonObject> ReassignCrew(TeamRosterContext context, JsonObject action, JsonObject row, string by = "lead");
    public static Task<JsonObject> ChangeCrew(TeamRosterContext context, string projectId, JsonObject assignments);
}
```

---

## 4. Mapped JavaScript Unit Tests

The test suite in `native/Planwharf.Engine.Tests/OrchestratorTeamModelsTests.cs` maps directly to the behaviors tested across existing JavaScript suites:

| JavaScript Suite | Covered Behaviors | C# Test Focus in `OrchestratorTeamModelsTests.cs` |
|---|---|---|
| `model-roles.test.cjs` | Role eligibility calculation, tool/filesystem gating, supervisor API restriction, fallback role inheritance. | `RoleEligibility_EvaluatesCapabilitiesStrictly`, `SupervisorRole_RestrictedToApiAdapter`, `WithModelRoles_PreservesLegacyFallback`. |
| `model-policy.test.cjs` | Policy schema migration, quota limits, 5-hour and weekly window thresholds, fallback resolution. | `MigratePolicy_ConvertsLegacyKeys`, `QuotaWindows_ExtractsMinLeftPctPerWindow`, `ResolveUsageFallback_IdentifiesAlternativeModel`. |
| `model-routing.test.cjs` | Route error validation, missing provider or CLI detection, neverUsable flag assignment. | `NormalizeModels_FlagInvalidRoutesAsNeverUsable`, `NormalizeModels_SetsDefaultValues`. |
| `saving-mode.test.cjs`<br>`saving-modes.test.cjs`<br>`saving-mode-run.test.cjs`<br>`saving-mode-switch.test.cjs`<br>`saving-mode-edge.test.cjs`<br>`saving-mode-race.test.cjs`<br>`saving-mode-recover.test.cjs`<br>`saving-mode-pr.test.cjs` | Normal, Safe, Ultra safe enforcement; team weight overrides; PR fix candidate filtering; class evaluation. | `AllowedBySavingMode_NormalAllowsAllClasses`, `AllowedBySavingMode_SafeExcludesFlagship`, `AllowedBySavingMode_UltraSafeKeepsLightOnly`, `SavingMode_TeamWeightOverridesLineupTier`. |
| `quota.test.cjs`<br>`quota-switch.test.cjs`<br>`quota-no-switch.test.cjs`<br>`quota-wait.test.cjs` | Quota exhaustion, threshold-triggered auto-switch, wait-for-quota when pool under limit, 24-hr credit lock. | `PickWorker_WaitsWhenAllModelsUnderQuotaLimit`, `SelectLead_TriggersHandoverWhenQuotaExhausted`, `CreditsMarked_LocksModelFor24Hours`. |
| `project-teams.test.cjs` | Per-project team overrides, account scope role persistence, multi-project role isolation. | `BuildRoster_AppliesProjectTeamOverrides`, `BuildRoster_IsolatesAccountScopeRoles`. |
| `lead.test.cjs`<br>`lead-allowed.test.cjs`<br>`lead-switch.test.cjs` | Lead ranking by quality and quota, lead handover, refusal sentence generation, `notLead` restriction. | `LeadRanking_SortsByTierThenQuota`, `LeadQuality_SortsByTierAndClaudeFamily`, `SetLeadAllowed_PreventsDisablingActiveLead`. |
| `gold-model.test.cjs` | Gold model worker requirement, exact provider/cli matching, rejection of substitutes. | `PermitsAssignment_GoldModelEnforcesExactProviderAndCli`, `SetGoldModel_RejectsModelLackingWorkerRole`. |
| `crew-reassign.test.cjs`<br>`crew-split.test.cjs`<br>`crew-switch-watch.test.cjs`<br>`resume-crew.test.cjs` | Reassigning remaining steps, status-dependent responses (READY_FOR_APPROVAL, RUNNING, resume confirmation). | `ReassignCrew_UpdatesRemainingStepAssignments`, `ReassignCrew_RejectsInvalidProjectOrMissingWork`. |

---

## 5. Pure Comparison Vectors and Scenario Parity (Rule 12)

### 5.1 Pure Comparison Vectors (`cases.cjs`)
Deterministic pure vectors are added to `native/porting/parity/cases.cjs` under module `'orchestrator-team-models'`:
1. `normalizeModels`:
   - Empty array, null/undefined entries, malformed objects without `id`.
   - Legacy roster model (no provider/cli).
   - Valid provider and CLI model with custom costs and limits.
   - Model with invalid route error -> verified `available: false`, `neverUsable: true`.
2. `usageOf`:
   - Zero usage, typical partial usage (`used: 15, limit: 50`), exhausted usage (`used: 50, limit: 50`), over-limit usage (`used: 60, limit: 50`).
3. `leadTier`:
   - Claude Fable, Opus, Sonnet, Haiku models.
   - Non-Claude models with `kind: "premium"`, `kind: "local"`, and `kind: "default"`.
   - Models matching light regex (`"gemini-flash"`, `"gpt-4o-mini"`, `"luna-v1"`).
4. `leadRanking` & `leadQuality`:
   - Diverse candidate lists containing mixed Claude models, Gemini, and GPT variants with known and unknown quotas.
5. `pickWorker` & `pickWorkers`:
   - Single slice selection for `build`, `verify`, and `reviewer` roles.
   - Multi-worker selection verifying independent verifier selection and provider diversity.
6. `dispatchPlan`:
   - Single-worker implementation plan.
   - Dual-worker build + independent verification plan with clean and dirty repository facts.
   - Multi-worker review plan with specialized review slices.
7. `allowedBySavingMode` & `modelClass`:
   - Normal, Safe, Ultra safe across flagship, mid, and light model entries with and without team weight overrides.

### 5.2 Scenario Parity (`scenarios.cjs`)
Scenario parity fixtures recorded under `native/porting/parity/scenarios/orchestrator-team-models.json`:
- **Scenario 1: Project Team Role Isolation & Account Scope**:
  - Sets up two distinct projects with conflicting role assignments for the same underlying model.
  - Verifies that `BuildRoster` produces isolated, project-accurate role permissions without leaking across projects.
- **Scenario 2: Lead Automatic Handover on Quota Depletion**:
  - Replays active lead encountering 5-hour quota depletion and triggers deterministic failover to next eligible candidate with exact explanation text.
- **Scenario 3: 24-Hour Credit Lockout**:
  - Asserts that a model marked with `requires usage credits` remains locked out across multiple roster evaluations within 24 hours.
- **Scenario 4: Saving Mode Candidate Filtering**:
  - Verifies that a project transitioning from `Normal` to `Safe` automatically excludes flagship models from worker picking.

---

## 6. Migration Plan (Job 48)

Job 48 (`port/48-team-models`) implements the specification detailed in this sheet:
1. Create `native/Planwharf.Engine/OrchestratorTeamModels.cs` matching all specified records, interfaces, and methods.
2. Implement pure helpers (`normalizeModels`, `usageOf`, `leadTier`, `leadRanking`, `leadQuality`, `pickWorker`, `pickWorkers`, `dispatchPlan`, `fleetOf`).
3. Implement team roster resolution, saving mode enforcement, and quota auto-switch evaluation.
4. Add comprehensive unit tests in `native/Planwharf.Engine.Tests/OrchestratorTeamModelsTests.cs`.
5. Integrate pure parity vectors in `native/porting/parity/cases.cjs` and generate `native/porting/parity/orchestrator-team-models.json`.
6. Add scenario parity in `native/porting/parity/scenarios.cjs` and verify via `dotnet test native/Planwharf.Engine.Tests -c Release --filter ScenarioParityTests`.
7. Enforce read-only verification: `record.cjs --check` and `scenarios.cjs --check` must pass.
