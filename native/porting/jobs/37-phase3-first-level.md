# Job 37 (phase 3): first-level engine porting specification sheet

**Goal.** Describe the port of the twenty-three Phase 3 engine modules from JavaScript/Node.js to C#,
with their test coverage, comparison parity cases, data structures, and execution watch-points,
following [native/porting/README.md](../README.md) (rules 1 to 12) and [PLAN.md](../PLAN.md) (phase 3).
This document is the deliverable of Job 37. It specifies the architecture, interfaces, and invariants;
Job 38 (`port/38-phase3`) implements the port following owner review. The JavaScript stays the specification.

---

## 1. Porting Inventory & Module Mapping

Phase 3 transitions the first level of Planwharf's engine core from JavaScript to C#. These modules depend
on foundational primitives already ported in Phase 0–2 (`AtomicRename`, `ModelPolicy`, `ModelRoles`,
`ProviderAccounts`, `ActivityEvents`, `ActivityEvidence`, `WorkerResult`, `StructuredFinal`, `SavingMode`),
while preparing the foundation for Phase 4 (process runners, sandbox isolation, and provider connections).

| # | JavaScript Module | Lines | C# Target Class | Target Test File | Parity Mode |
|---|---|---|---|---|---|
| 1 | `engine/attempts.js` | 244 | `Planwharf.Engine.Attempts` | `AttemptsTests.cs` | Isolated Disk/File Lock Suite |
| 2 | `engine/claims.js` | 21 | `Planwharf.Engine.Claims` | `ClaimsTests.cs` | In-Memory Engine Parity |
| 3 | `engine/project-export.cjs` | 103 | `Planwharf.Engine.ProjectExport` | `ProjectExportTests.cs` | Parity JSON (`cases.cjs`) |
| 4 | `engine/execution-next.cjs` | 60 | `Planwharf.Engine.ExecutionNext` | `ExecutionNextTests.cs` | Parity JSON (`cases.cjs`) |
| 5 | `engine/provider-catalog.cjs` | 63 | `Planwharf.Engine.ProviderCatalog` | `ProviderCatalogTests.cs` | Parity JSON (`cases.cjs`) |
| 6 | `engine/provider-policy.cjs` | 27 | `Planwharf.Engine.ProviderPolicy` | `ProviderPolicyTests.cs` | Parity JSON (`cases.cjs`) |
| 7 | `engine/model-routing.cjs` | 41 | `Planwharf.Engine.ModelRouting` | `ModelRoutingTests.cs` | Parity JSON (`cases.cjs`) |
| 8 | `engine/claude.cjs` | 62 | `Planwharf.Engine.Claude` | `ClaudeTests.cs` | Parity JSON (args/parser) & Process Suite |
| 9 | `engine/claude-usage.cjs` | 105 | `Planwharf.Engine.ClaudeUsage` | `ClaudeUsageTests.cs` | Parity JSON (parser) & Mock HTTP Suite |
| 10 | `engine/repository.cjs` | 181 | `Planwharf.Engine.Repository` | `RepositoryTests.cs` | Parity JSON (URLs/relations) & Mock Git Suite |
| 11 | `engine/supervisor.cjs` | 144 | `Planwharf.Engine.Supervisor` | `SupervisorTests.cs` | Isolated State Suite & Parity Facts |
| 12 | `engine/activity.cjs` | 102 | `Planwharf.Engine.Activity` | `ActivityTests.cs` | Parity JSON (`cases.cjs`) |
| 13 | `engine/activity-sessions.cjs` | 145 | `Planwharf.Engine.ActivitySessions` | `ActivitySessionsTests.cs` | Isolated Disk Log & Parity Slice |
| 14 | `engine/provider-runtime.cjs` | 163 | `Planwharf.Engine.ProviderRuntime` | `ProviderRuntimeTests.cs` | Parity JSON (env/launch specs) |
| 15 | `engine/time-left.cjs` | 177 | `Planwharf.Engine.TimeLeft` | `TimeLeftTests.cs` | Parity JSON (`cases.cjs`) |
| 16 | `engine/plan-status.cjs` | 69 | `Planwharf.Engine.PlanStatus` | `PlanStatusTests.cs` | Parity JSON (`cases.cjs`) |
| 17 | `engine/provider-status.cjs` | 27 | `Planwharf.Engine.ProviderStatus` | `ProviderStatusTests.cs` | Mock HTTP Network Suite |
| 18 | `engine/push-email.cjs` | 37 | `Planwharf.Engine.PushEmail` | `PushEmailTests.cs` | Parity JSON & Mock Git Rebase Suite |
| 19 | `engine/temp-cleanup.cjs` | 39 | `Planwharf.Engine.TempCleanup` | `TempCleanupTests.cs` | Platform Filesystem Suite |
| 20 | `engine/account-folders.cjs` | 99 | `Planwharf.Engine.AccountFolders` | `AccountFoldersTests.cs` | Isolated Lock & Account Store Suite |
| 21 | `engine/phone-voice.cjs` | 539 | `Planwharf.Engine.PhoneVoice` | `PhoneVoiceTests.cs` | Parity JSON (`cases.cjs`) |
| 22 | `engine/phone-activation.cjs` | 158 | `Planwharf.Engine.PhoneActivation` | `PhoneActivationTests.cs` | Isolated Notebook & Probe Suite |
| 23 | `engine/step-outcomes.cjs` | 261 | `Planwharf.Engine.StepOutcomes` | `StepOutcomesTests.cs` | Isolated JSONL Store & Parity Summaries |

All C# source files belong to namespace `Planwharf.Engine` in `native/Planwharf.Engine/`. Each file begins with
the mandatory header comment (e.g. `// Port of engine/attempts.js.`).
All test files belong to namespace `Planwharf.Engine.Tests` in `native/Planwharf.Engine.Tests/`.

---

## 2. Core Lifecycle & State Management Subsystems

### 2.1 `attempts.js` -> `Attempts.cs`

#### A. Module Mapping
- **C# Types**: `public sealed class AttemptEngine`, `public record AttemptStore`, `public record AttemptEntry`, `public record ClaimInput`, `public record ClaimResult`, `public record CloseInput`, `public record AttachResultInput`.
- **Primary Methods**:
  - `public static AttemptEngine Create(string directory)`
  - `public static AttemptEngine Create(IAttemptIo io)`
  - `public ClaimResult Claim(JsonElement input)`
  - `public IReadOnlyList<JsonObject> GetAttempts()`
  - `public JsonObject? GetActiveAttempt(JsonObject query)`
  - `public JsonObject CloseAttempt(JsonObject input)`
  - `public JsonObject AttachResult(JsonObject input)`
  - `public JsonObject? GetResult(string attemptId)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/attempts.test.cjs` (53 tests including transactional concurrency, retry on locked store, invalid candidate validation, megaTask/subTask isolation, crash recovery).
- **Target C# Tests** (`AttemptsTests.cs`):
  1. `Claim_ValidCandidate_AllocatesAttemptWithIncrementalId`
  2. `Claim_ModelNotEligibleOrDisabled_RejectsWithExplicitReason`
  3. `Claim_ConcurrentSubTasks_MegaTaskDisallowsSecondActiveUnlessDifferentSubTaskId`
  4. `CloseAttempt_ValidActive_MarksClosedWithCustomReason`
  5. `AttachResult_ValidAttempt_BindsPathStatusAndExitCode`
  6. `WithLock_Concurrency_ContendedWritersAcquireOrTimeoutAt5000ms`
  7. `EmptyOrCorruptStore_RecoversToEmptyStoreWithNextId1`

#### C. Parity Comparison Cases
- State JSON shape: `{ "nextId": int, "attempts": [...] }`.
- Attempt record fields: `id` (`attempt-1`), `taskId`, `projectId`, `modelId`, `status` (`active` | `closed`), `megaTaskId` (optional), `subTaskId` (optional), `research` (optional boolean), `closedReason` (string), `resultPath` (string), `resultStatus` (string), `exitCode` (int).
- Error strings: `"invalid data"`, `"attempt store directory required"`, `"no model enabled"`, `"model not present"`, `"model not eligible"`, `"task already assigned"`, `"project already assigned"`, `"attempt not found"`, `"attempt not active"`, `"attempt store locked"`.

#### D. Execution Watch-Points
- **Filesystem Locking**: Uses `attempts.lock` via atomic open with `FileMode.CreateNew` (`wx` equivalent). Timeout is 5000ms with 20ms spin-wait.
- **Atomic Persistence**: Stored via temporary file `attempts.json.<uuid>.tmp` and renamed using `AtomicRename.RenameWithRetry`.
- **AOT / JSON**: Strict JSON clone semantics using `JsonNode.DeepClone()` to avoid mutating in-flight returned references.

---

### 2.2 `claims.js` -> `Claims.cs`

#### A. Module Mapping
- **C# Types**: `public sealed class ClaimEngine`
- **Primary Methods**:
  - `public static ClaimEngine Create()`
  - `public JsonObject? GetActiveAttempt(JsonObject query)`
  - `public JsonObject Claim(JsonObject input)`
  - `public IReadOnlyList<JsonObject> GetAttempts()`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/claim.test.cjs` (compatibility checks for legacy single-project / global task ID namespace).
- **Target C# Tests** (`ClaimsTests.cs`):
  1. `GetActiveAttempt_RequiresNonEmptyTaskIdAndProjectId`
  2. `Claim_GlobalTaskIdActiveCollision_RejectsWithTaskAlreadyAssigned`
  3. `Claim_DelegatesToUnderlyingAttemptEngine`

#### C. Parity Comparison Cases
- Returns `{ "ok": false, "reason": "task already assigned" }` when another active attempt has the same `taskId`, regardless of `projectId`.

#### D. Execution Watch-Points
- Pure memory adapter over `AttemptEngine` with null-op lock. No disk IO or synchronization hazards.

---

### 2.3 `project-export.cjs` -> `ProjectExport.cs`

#### A. Module Mapping
- **C# Types**: `public static class ProjectExport`
- **Primary Methods**:
  - `public static JsonNode Sanitize(JsonNode node)`
  - `public static JsonObject ExportProject(JsonObject project, JsonObject execution, JsonObject? settings = null)`
  - `public static JsonObject RemapExecution(JsonObject execution, string projectId)`
  - `public static JsonObject ContinueFrom(JsonObject execution)`
  - `public static JsonObject ImportProject(JsonObject notebook, JsonObject bundle, JsonObject? place = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/project-export.test.cjs`, `engine/final.test.cjs`.
- **Target C# Tests** (`ProjectExportTests.cs`):
  1. `Sanitize_RedactsBearerTokensAndApiKeys`
  2. `Sanitize_StripsForbiddenKeys_CredentialsAuthSecretsLogs`
  3. `ExportProject_FiltersExecutionByProjectId_ProducesSchema1Bundle`
  4. `RemapExecution_RegeneratesUUIDsForMegaSubCheckpointsAndTimeline`
  5. `RemapExecution_RebindsDependenciesAndCheckpointsCorrectly`
  6. `ContinueFrom_PausedWithCheckpoint_ResolvesResumeAction`
  7. `ImportProject_RestoresProjectIntoNotebookAndRemapsExecution`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Redaction regex: `\bBearer\s+[A-Za-z0-9._~+\/-]+` -> `Bearer [REDACTED]`, `\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,})\b` -> `[REDACTED]`.
- Forbidden key regex: `^(?:credentials?|auth|authEnv|access.?token|refresh.?token|tokens?|api.?key|password|secret|environment|env|workerTail|thinking|log)$` (case-insensitive).
- Export header: `format: "planwharf-project-export"`, `schema: 1`, `restoreSupported: true`.

#### D. Execution Watch-Points
- **Deterministic Field Ordering**: Matches Node.js JSON output order.
- **Guid Generation**: In parity tests, GUID generators must accept a deterministic seed or provider to guarantee identical comparison.
- **Status Normalization**: MegaTask status remapped to `COMPLETE`, `FAILED`, `CANCELLED`, `READY_FOR_APPROVAL`, or defaults to `PAUSED`.

---

### 2.4 `execution-next.cjs` -> `ExecutionNext.cs`

#### A. Module Mapping
- **C# Types**: `public static class ExecutionNext`
- **Primary Methods**:
  - `public static string GetExecutionNext(JsonObject? mega, JsonObject? durable = null)`
  - `public static string GetHomeNext(JsonObject item, JsonObject? row = null, JsonObject? durable = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/execution-next.test.cjs`, `engine/home-containment.test.cjs`, `engine/phase2-deadline.test.cjs`.
- **Target C# Tests** (`ExecutionNextTests.cs`):
  1. `ExecutionNext_ReadyForApproval_ReturnsApprovalPrompt`
  2. `ExecutionNext_Running_ReturnsActiveSubTaskOrPrerequisites`
  3. `ExecutionNext_BlockedOrFailed_ReturnsRecoveryBlockersOrCheckpointAction`
  4. `HomeNext_UnlinkedFolder_PromptsRelink`
  5. `HomeNext_MegaTaskPendingApproval_PromptsApproveMegaTask`
  6. `HomeNext_CanResume_PromptsResumeFromCheckpoint`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Exact string literals:
  - `"Review the proposed slices and approve this MegaTask."`
  - `"Work in progress: {title} ({id})."`
  - `"Next SubTask: {title} ({id})."`
  - `"Waiting for prerequisite {deps} before {title} ({id})."`
  - `"Resolve blockers: {blockers}"`
  - `"Review {title} ({id}) and resume from an explicit checkpoint."`
  - `"Review completed evidence."`
  - `"Review remaining work and resume this checkpoint."`
  - `"Reconciling completed work."`
  - `"Relink the project folder on this PC."`
  - `"Approve the prepared MegaTask."`
  - `"Resume from checkpoint."`
  - `"Tell the lead what to build."`
  - `"Review the Living Plan."`

#### D. Execution Watch-Points
- Presentation only: must never mutate the input durable graph or attempt scheduling.
- Safely handles missing/null nested subtasks, checkpoints, and results without NullReferenceException.

---

### 2.5 `temp-cleanup.cjs` -> `TempCleanup.cs`

#### A. Module Mapping
- **C# Types**: `public static class TempCleanup`
- **Primary Methods**:
  - `public static async Task<bool> RemoveTempDirAsync(string target, int waitMs = 15000, int stepMs = 100)`
  - `public static async Task<bool> WaitForExitAsync(Func<int, bool> isAlive, int pid, int waitMs = 5000, int stepMs = 25)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/temp-cleanup.test.cjs`, `engine/worker-containment.test.cjs`.
- **Target C# Tests** (`TempCleanupTests.cs`):
  1. `RemoveTempDir_OutsideTempDirectory_ThrowsSecurityException`
  2. `RemoveTempDir_FolderLockedByProcess_RetriesUntilFreed`
  3. `RemoveTempDir_NonExistentPath_ReturnsTrue`
  4. `WaitForExit_ProcessExitsPromptly_ReturnsTrue`
  5. `WaitForExit_ProcessStuck_TimesOutAndReturnsFalse`

#### C. Parity Comparison Cases
- Non-parity JSON (filesystem/process dependent).
- Path guard: Path must start with `Path.GetTempPath()` (case-insensitive on Windows, case-sensitive on Linux/macOS). Reject sibling escapes like `/tmp-evil`.
- Error code handling: Retries on `IOException` (Win32 sharing violation, Linux `EBUSY`, `EACCES`, `ENOTEMPTY`).

#### D. Execution Watch-Points
- **Windows File Locks**: Processes terminating asynchronously hold directories open. The retry loop must use high-resolution delays and abort strictly at `waitMs`.


---

## 3. Provider Ecosystem, Catalog & Routing Subsystems

### 3.1 `provider-catalog.cjs` -> `ProviderCatalog.cs`

#### A. Module Mapping
- **C# Types**: `public static class ProviderCatalog`, `public record CatalogResult(IReadOnlyList<JsonObject> Providers, IReadOnlyList<JsonObject> Models)`.
- **Primary Methods**:
  - `public static JsonObject ProviderConfiguration(JsonObject provider)`
  - `public static CatalogResult LoadCatalog(JsonNode config)`
  - `public static string NotInstalledReason(JsonObject provider)`
  - `public static IReadOnlyList<JsonObject> ProviderViews(IReadOnlyList<JsonObject> providers, IReadOnlyList<JsonObject> models, IReadOnlyList<JsonObject>? plans = null, JsonObject? settings = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/claude-usage.test.cjs`, `engine/claude.test.cjs`, `engine/external-adapters.test.cjs`, `engine/final.test.cjs`, `engine/missing-tools.test.cjs`, `engine/orchestrator.test.cjs`, `engine/supervisor.test.cjs`.
- **Target C# Tests** (`ProviderCatalogTests.cs`):
  1. `LoadCatalog_Schema3Explicit_PreservesProvidersAndBindsModels`
  2. `LoadCatalog_LegacyFlatList_SynthesizesProvidersWithDefaultPools`
  3. `LoadCatalog_DuplicateProviderOrModelIds_ThrowsValidationException`
  4. `ProviderViews_ComputesAvailability_InstalledAvailableExhaustedUnread`
  5. `NotInstalledReason_FormatsOfficialToolInstallHint`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Default fallback mapping:
  - `codex` -> `["ChatGPT", "ChatGPT"]`
  - `grok` -> `["Grok", "Grok"]`
  - `antigravity` -> `["Gemini", "Gemini"]`
  - `claude` -> `["Claude Code", "Claude"]`
- Install hints:
  - `claude`: `"Install it with npm install -g @anthropic-ai/claude-code, then run claude once to sign in."`
  - `codex`: `"Install it with npm install -g @openai/codex, then run codex once to sign in."`
  - `grok`: `"Install the Grok CLI and sign in, then look for models again."`
  - `antigravity`: `"Install Antigravity and sign in, then look for models again."`
- Availability states: `NOT_INSTALLED`, `UNAVAILABLE`, `EXHAUSTED`, `UNREAD`, `AVAILABLE`.

#### D. Execution Watch-Points
- Antigravity quota pool resolution: Antigravity model pools resolve via `Antigravity.PoolOf(cli, provider.QuotaPoolId)`.
- Immutability: When mapping `providerViews`, original provider model collections must not be mutated in-place.

---

### 3.2 `provider-policy.cjs` -> `ProviderPolicy.cs`

#### A. Module Mapping
- **C# Types**: `public static class ProviderPolicy`
- **Primary Methods**:
  - `public static JsonObject ValidateProviderPolicy(JsonNode? value)`
  - `public static (JsonObject Policy, string Source) ProviderPolicyFor(JsonObject provider, IReadOnlyList<JsonObject> models, JsonObject? settings = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/model-policy.test.cjs`.
- **Target C# Tests** (`ProviderPolicyTests.cs`):
  1. `ValidateProviderPolicy_UnknownKeys_ThrowsInvalidProviderPolicyFields`
  2. `ValidateProviderPolicy_InvalidFallback_ThrowsExplicitProviderIdOrNull`
  3. `ProviderPolicyFor_SavedSettings_ReturnsSavedPolicy`
  4. `ProviderPolicyFor_LegacyModelPolicies_AggregatesMinAndWarningThresholds`
  5. `ProviderPolicyFor_Default_ReturnsZeroedDefaults`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Defaults: `{ minRemainingPercent: null, warningRemainingPercent: null, fallbackProviderId: null, alwaysApprove: false }`.
- Sources: `"saved"`, `"configured"`, `"legacy"`, `"default"`.
- Throws: `"Invalid provider policy fields."`, `"Fallback must be an explicit provider ID or null."`.

#### D. Execution Watch-Points
- Delegates to `ModelPolicy.ValidatePolicy` and `ModelPolicy.MigratePolicy`. Strict integer/null parsing for percentage thresholds.

---

### 3.3 `model-routing.cjs` -> `ModelRouting.cs`

#### A. Module Mapping
- **C# Types**: `public static class ModelRouting`
- **Primary Methods**:
  - `public static string? ModelRouteError(JsonObject? model)`
  - `public static string? ResolveCliModel(JsonObject? model)`
  - `public static string? SavingModeError(JsonObject? model, string? mode, string? tier = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/model-routing.test.cjs`, `engine/claude.test.cjs`, `engine/saving-modes.test.cjs`.
- **Target C# Tests** (`ModelRoutingTests.cs`):
  1. `ModelRouteError_NullOrUnavailable_ReturnsReason`
  2. `ModelRouteError_UnsupportedAdapter_ReturnsMismatchMessage`
  3. `ModelRouteError_CliIdControlCharsOrTooLong_Rejects`
  4. `ModelRouteError_UnconfiguredNativeMismatch_ReturnsSetModelRoutingConfigured`
  5. `SavingModeError_UltraSafeWithFlagship_ReturnsDisallowedExplanation`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Error messages:
  - `"Model configuration is required"`
  - `"Provider/model mismatch: unsupported provider adapter {provider}"`
  - `"Provider/model mismatch: an exact nonempty CLI model ID is required for {provider}"`
  - `"CLI model ID exceeds the 512-byte configuration limit"`
  - `"Invalid modelRouting: expected \"configured\" or an omitted value"`
  - `"Provider/model mismatch: {provider}/{cli}. Set modelRouting to \"configured\" only when this exact CLI supports the model."`
  - `"{name} is {class}, and {modeLabel} does not allow it."`

#### D. Execution Watch-Points
- Byte-length limit: Uses `Encoding.UTF8.GetByteCount(cli) > 512`.
- Control character rejection: Regex `[\x00-\x1f\x7f]`.

---

### 3.4 `provider-runtime.cjs` -> `ProviderRuntime.cs`

#### A. Module Mapping
- **C# Types**: `public static class ProviderRuntime`, `public record LaunchSpecResult(string Command, IReadOnlyList<string> Args, string Cwd, string Provider, string Adapter, string Model, string? Stdin = null, string? PromptFile = null)`.
- **Primary Methods**:
  - `public static string? Executable(string provider)`
  - `public static LaunchSpecResult LaunchSpec(JsonObject model, string folder, string prompt, bool lead = false, bool research = false)`
  - `public static Dictionary<string, string> ProviderEnvironment(string provider, IDictionary<string, string>? inherited = null, string? platform = null)`
  - `public static Dictionary<string, string> AccountEnvironment(string adapter, JsonObject? model, IDictionary<string, string>? inherited = null, string? platform = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/accounts-job16-verify2.test.cjs`, `engine/always-approve.test.cjs`, `engine/model-policy.test.cjs`, `engine/project-tools.test.cjs`, `engine/provider-accounts.test.cjs`.
- **Target C# Tests** (`ProviderRuntimeTests.cs`):
  1. `AccountEnvironment_Claude_SetsClaudeConfigDirAndStripsInheritedKeys`
  2. `AccountEnvironment_Codex_SetsCodexHomeAndStripsOpenAiKey`
  3. `ProviderEnvironment_WindowsCodex_FiltersDeniedStoreShellsAndSuppliesCABundle`
  4. `LaunchSpec_Claude_BuildsStreamJsonAndPermissionArgs`
  5. `LaunchSpec_Grok_WritesPromptFileAndConfiguresMaxTurns`
  6. `LaunchSpec_Codex_ConfiguresSandboxWritableRootsAndLeadJson`
  7. `LaunchSpec_UnapprovedAntigravityOrClaude_ThrowsPermissionNeeded`

#### C. Parity Comparison Cases
- Tool configuration arguments match `claudeToolArgs`: `--no-session-persistence`, `--setting-sources user`, `--settings {"disableAllHooks":...,"autoMemoryEnabled":...}`, `--strict-mcp-config`, `--mcp-config {"mcpServers":{}}`.
- Prompt file semantics: Grok writes prompt to isolated file with mode `0600` to prevent CLI buffer overflows.

#### D. Execution Watch-Points
- **Windows Store Shell Filter**: Discovers `pwsh.exe` in `WindowsApps` and tests execution access with `-NoProfile -NonInteractive -Command "exit 0"` to prevent crashes on non-elevated user accounts.
- **ProcessStartInfo Vector**: Arguments must be passed as distinct vector elements (`ArgumentList`), never concatenated as a command string.

---

### 3.5 `claude.cjs` -> `Claude.cs`

#### A. Module Mapping
- **C# Types**: `public static class Claude`
- **Primary Methods**:
  - `public static IReadOnlyList<string> IsolatedArgs { get; }`
  - `public static IReadOnlyList<string> ClaudeToolArgs(JsonObject? tools)`
  - `public static (bool Ok, string? Reason, IReadOnlyList<JsonObject>? Models) ParseModels(JsonArray rows)`
  - `public static async Task<(bool Ok, string? Reason, IReadOnlyList<JsonObject>? Models)> ListClaudeAsync(string? command = null, IDictionary<string, string>? env = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/claude.test.cjs`, `engine/accounts-job16-verify2.test.cjs`, `engine/project-tools.test.cjs`.
- **Target C# Tests** (`ClaudeTests.cs`):
  1. `ClaudeToolArgs_AllDisabled_SetsNoChromeAndStrictMcp`
  2. `ClaudeToolArgs_McpAndBrowserEnabled_RemovesStrictMcpAndEnablesChrome`
  3. `ParseModels_ValidStreamResponse_ExtractsModelIdsAndLabels`
  4. `ParseModels_InvalidOrEmpty_ReturnsDescriptiveFailure`
  5. `ListClaudeAsync_Unauthenticated_ReturnsLoginRequiredReason`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Regex: `^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$`. Length limit: 512 characters.
- Failure strings:
  - `"Claude Code returned no model catalog."`
  - `"Claude Code returned no exact model IDs. Aliases were not guessed."`
  - `"Claude Code authentication unavailable. Run claude auth login, then refresh models."`
  - `"Claude Code model discovery failed or its control protocol changed. No substitute was selected."`

#### D. Execution Watch-Points
- Inter-process stream json communication with timeout (20s) and buffer cap (1MB) to prevent hung CLI processes.

---

### 3.6 `claude-usage.cjs` -> `ClaudeUsage.cs`

#### A. Module Mapping
- **C# Types**: `public static class ClaudeUsage`, `public record ClaudeAuthInfo(string Token, long? ExpiresAt)`
- **Primary Methods**:
  - `public static JsonObject ParseClaudeUsage(JsonElement body)`
  - `public static async Task<ClaudeAuthInfo?> ReadClaudeAuthAsync(IDictionary<string, string>? env = null, string? home = null, string? command = null, Func<long>? now = null)`
  - `public static Func<Task<JsonObject>> CreateClaudeUsageReader(Func<Task<ClaudeAuthInfo?>>? readAuth = null, Func<string, HttpRequestMessage, Task<HttpResponseMessage>>? fetchImpl = null, Func<long>? now = null, int ttlMs = 300000)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/claude-usage.test.cjs`, `engine/accounts-job16-verify.test.cjs`.
- **Target C# Tests** (`ClaudeUsageTests.cs`):
  1. `ParseClaudeUsage_FiveHourAndWeeklyWindows_ComputesTightestRemainingQuota`
  2. `ParseClaudeUsage_ExhaustedWindow_SetsExhaustedTrue`
  3. `ReadClaudeAuthAsync_CredentialsFile_ExtractsAccessTokenAndExpiry`
  4. `ReadClaudeAuthAsync_ApiGatewayOverridePresent_ReturnsNull`
  5. `CreateClaudeUsageReader_ConcurrentCalls_SharesPendingRequestAndCachesResult`
  6. `CreateClaudeUsageReader_RateLimited429_AppliesRetryAfterHeader`

#### C. Parity Comparison Cases
- Output JSON structure:
  `{ "id": "Claude", "label": "Claude", "readable": true, "exhausted": bool, "pct": double, "leftPct": double, "resetAt": string?, "source": "Claude subscription OAuth usage", "planStatus": "active", "windows": [...] }`.
- Error messages match character for character.

#### D. Execution Watch-Points
- Credentials security: Credentials file reads bounded at 64KB (`MAX_BYTES`).
- Token hash keying: SHA256 hashing of tokens for cache invalidation without retaining raw credentials in logs.

---

### 3.7 `plan-status.cjs` -> `PlanStatus.cs`

#### A. Module Mapping
- **C# Types**: `public static class PlanStatus`
- **Primary Methods**:
  - `public static string StatusFrom(string? provider, JsonObject? response)`
  - `public static (IReadOnlyList<JsonObject> Plans, string? Status) NotePlanRefusal(IReadOnlyList<JsonObject>? plans, JsonObject? model, string? reason)`
  - `public static IReadOnlyList<JsonObject> MergePlans(IReadOnlyList<JsonObject>? previous, IReadOnlyList<JsonObject>? next)`
  - `public static JsonObject WithStatus(string? provider, JsonObject response)`
  - `public static bool BlockedStatus(JsonObject? plan)`
  - `public static JsonObject? ProviderError(string? provider, JsonElement body, string label)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/accounts-job16.test.cjs`, `engine/accounts-job16-verify.test.cjs`, `engine/accounts-job16-verify2.test.cjs`.
- **Target C# Tests** (`PlanStatusTests.cs`):
  1. `StatusFrom_ReadableResponse_ReturnsActive`
  2. `StatusFrom_ExpiredPattern_ReturnsExpired`
  3. `StatusFrom_SignedOutPattern_ReturnsNotSignedIn`
  4. `NotePlanRefusal_ExpiredModel_UpdatesPoolStatusAndReason`
  5. `MergePlans_PreservesExpiredOrNotSignedInAcrossRefreshes`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Regex patterns for `expired` and `signedOut` across all 4 providers (Claude, Codex, Grok, Antigravity).

#### D. Execution Watch-Points
- Pure data transformations. Must guarantee immutability of plans array across updates.

---

### 3.8 `provider-status.cjs` -> `ProviderStatus.cs`

#### A. Module Mapping
- **C# Types**: `public static class ProviderStatus`, `public record StatusPageRecord(string Id, string Name, string Url, bool Up, string Note)`
- **Primary Methods**:
  - `public static async Task<IReadOnlyList<StatusPageRecord>> GetProviderStatusAsync(HttpClient? httpClient = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: Status check unit tests.
- **Target C# Tests** (`ProviderStatusTests.cs`):
  1. `GetProviderStatus_AllOperational_ReportsUpTrue`
  2. `GetProviderStatus_DegradedPerformance_ReportsUpFalseWithIncidentOpen`
  3. `GetProviderStatus_NetworkTimeout_ReportsUpFalseWithUnreachableNote`

#### C. Parity Comparison Cases
- Monitored pages: OpenAI (`https://status.openai.com`), Anthropic (`https://status.claude.com`), xAI (`https://status.x.ai`), Google (`https://status.cloud.google.com`).
- Incident detection: `/major outage|partial outage|investigating|degraded performance/` vs `/no incidents|all systems operational|operational/`.

#### D. Execution Watch-Points
- Bounded network timeout: 8000ms via `CancellationTokenSource`. Never throws uncaught exceptions on DNS/socket errors.

---

### 3.9 `account-folders.cjs` -> `AccountFolders.cs`

#### A. Module Mapping
- **C# Types**: `public static class AccountFolders`, `public record AccountFolderRecord(string Id, string Adapter, string ProviderId, string Folder, string? SignInCommand = null)`
- **Primary Methods**:
  - `public static string CommandFor(string adapter, string folder)`
  - `public static bool InsideRoot(string root, string folder)`
  - `public static IReadOnlyList<AccountFolderRecord> ReadAccounts(string file, string root)`
  - `public static AccountFolderRecord AddAccount(string file, string root, string adapter, string providerId, JsonArray? existing = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/accounts-job16.test.cjs`, `engine/accounts-job16-verify.test.cjs`.
- **Target C# Tests** (`AccountFoldersTests.cs`):
  1. `CommandFor_WindowsAndPosix_EscapesQuotesCorrectly`
  2. `InsideRoot_PathTraversalOrOutside_ReturnsFalse`
  3. `ReadAccounts_CorruptOrMismatchedPaths_ThrowsValidationException`
  4. `AddAccount_ValidAdapter_CreatesFolderAndRecordsAtomicRow`
  5. `WithLock_StaleLockFileFromDeadPid_ReclaimsAndCompletes`

#### C. Parity Comparison Cases
- Shell commands:
  - Windows: `$env:CLAUDE_CONFIG_DIR='<path>'; claude login` or `$env:CODEX_HOME='<path>'; codex login`.
  - POSIX: `CLAUDE_CONFIG_DIR='<path>' claude login` or `CODEX_HOME='<path>' codex login`.
- Lock format: `<pid>:<uuid>`. Lock timeout: 5000ms. Stale threshold: 10000ms.

#### D. Execution Watch-Points
- **Windows Reparse Points & Relative Paths**: Validates folder depth via resolved canonical paths (`Path.GetFullPath`).
- **Permissions**: Mode `0700` (`FileAccessRule` on Windows / POSIX permissions on Linux/macOS) for isolated credential folders.


---

## 4. Repository & Git Integration Subsystems

### 4.1 `repository.cjs` -> `Repository.cs`

#### A. Module Mapping
- **C# Types**: `public static class Repository`, `public record RepositoryAddress(string Provider, string Repository, string Origin, string BaseUrl, string Url)`, `public record RepositoryInspection(string CheckedAt, string Folder, string? Github, JsonObject Local, JsonObject Remote, JsonObject Comparison)`.
- **Primary Methods**:
  - `public static string? GithubRepo(string? value)`
  - `public static RepositoryAddress? GetRepositoryAddress(string? value)`
  - `public static string? GiteaToken(RepositoryAddress address, IDictionary<string, string>? env = null)`
  - `public static string CleanError(string? value)`
  - `public static string Relation(int ahead, int behind)`
  - `public static Func<JsonObject, bool, Task<RepositoryInspection>> CreateRepositoryInspector(Func<string, IReadOnlyList<string>, JsonObject, Task<ProcessResult>>? execute = null, int ttlMs = 60000, HttpClient? fetchRemote = null, IDictionary<string, string>? env = null)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/repository.test.cjs`, `engine/gitea.test.cjs`, `engine/home-containment.test.cjs`, `engine/worker-containment.test.cjs`.
- **Target C# Tests** (`RepositoryTests.cs`):
  1. `GithubRepo_ParsesHttpsAndSshUrlsWithOrWithoutGitSuffix`
  2. `RepositoryAddress_GiteaInstance_ParsesBaseUrlAndPathPrefix`
  3. `CleanError_RedactsPatAndBearerTokens`
  4. `Relation_AheadBehindCombinations_ReturnsDivergedAheadBehindInSync`
  5. `CreateRepositoryInspector_CachesResultsUntilTtlExpires`
  6. `CreateRepositoryInspector_DivergedHistory_DetectsCommitCountsViaMergeBase`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs` for URL normalization, token scrubbing, and status relation mappings.
- Status relations:
  - `ahead > 0 && behind > 0` -> `"diverged"`
  - `ahead > 0 && behind == 0` -> `"ahead"`
  - `ahead == 0 && behind > 0` -> `"behind"`
  - `ahead == 0 && behind == 0` -> `"in-sync"`
- Token scrub pattern: `https:\/\/[^\s/@]+:[^\s/@]+@` -> `https://[redacted]@`, `\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b` -> `[redacted]`.

#### D. Execution Watch-Points
- **Git Credentials Protection**: Custom environment sets `GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, `GH_PROMPT_DISABLED=1` to prevent hanging UI modals.
- **Inspection Reference Isolation**: Fetches remote tracking references to isolated namespace `refs/planwharf/inspection/<sha256:24>` without touching local branches or remote tracking branches.

---

### 4.2 `push-email.cjs` -> `PushEmail.cs`

#### A. Module Mapping
- **C# Types**: `public static class PushEmail`
- **Primary Methods**:
  - `public static bool PrivateEmailRefused(string? text)`
  - `public static async Task<string?> GithubNoreplyAsync(Func<IReadOnlyList<string>, Task<ProcessResult>> gh)`
  - `public static async Task<(bool Ok, int Rewritten, string? Reason)> UseNoreplyAsync(Func<IReadOnlyList<string>, Task<ProcessResult>> run, string email)`
  - `public const string HowTo = "Set your GitHub noreply email (GitHub → Settings → Emails) with: git config --global user.email \"<id>+<login>@users.noreply.github.com\", then press Push again.";`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/push-email.test.cjs`.
- **Target C# Tests** (`PushEmailTests.cs`):
  1. `PrivateEmailRefused_DetectsGH007ErrorCode`
  2. `GithubNoreplyAsync_ValidGhApiResponse_FormatsNoreplyAddress`
  3. `UseNoreplyAsync_NoUnpushedCommits_ReturnsRewritten0`
  4. `UseNoreplyAsync_UnpushedCommits_RebasesAndResetsAuthorWithoutTouchingRemote`
  5. `UseNoreplyAsync_RebaseFails_AbortsCleanlyAndReportsFailure`

#### C. Parity Comparison Cases
- GH007 error detector: `\bGH007\b`.
- GitHub noreply address regex: `^\d+\+[A-Za-z0-9-]+@users\.noreply\.github\.com$`.

#### D. Execution Watch-Points
- **Remote Commit Safety**: Only commits in `HEAD --not --remotes` may be amended. If rebase fails, it must execute `git rebase --abort` immediately to avoid corrupting repository state.

---

## 5. Supervision, Monitoring & Evidence Subsystems

### 5.1 `supervisor.cjs` -> `Supervisor.cs`

#### A. Module Mapping
- **C# Types**: `public sealed class Supervisor`, `public static class SupervisorFacts`, `public record SupervisorFactsResult(...)`, `public record SupervisorAlert(...)`.
- **Primary Methods**:
  - `public static string AdvisoryError(JsonObject? model, IReadOnlyList<JsonObject>? plans = null)`
  - `public static JsonObject ProjectFacts(JsonObject snapshot, string projectId, long nowMs)`
  - `public static Supervisor Create(string? directory, Func<JsonObject, Task<JsonObject>> review, Func<string>? now = null)`
  - `public JsonObject Observe(JsonObject snapshot)`
  - `public JsonObject View(JsonObject snapshot)`
  - `public JsonObject Settings(JsonObject input, JsonObject snapshot)`
  - `public void Ignore(string projectId, string alertId)`
  - `public Task<JsonObject> Ask(JsonObject snapshot, string projectId, string question, bool automatic = false)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/supervisor.test.cjs`, `engine/supervisor-role.test.cjs`, `engine/finish-clean.test.cjs`.
- **Target C# Tests** (`SupervisorTests.cs`):
  1. `AdvisoryError_NonSupervisorModelOrQuotaBlocked_ReturnsSpecificExplanation`
  2. `ProjectFacts_DetectsConflictingCompletion_WhenWorkersStillAlive`
  3. `ProjectFacts_DetectsThreeConsecutiveFailures_EmitsRepeatedFailuresWarning`
  4. `Supervisor_Observe_EmitsAlertsAndTriggersAutoReviewWithinBudget`
  5. `Supervisor_RestartRecovery_MarksInterruptedRunningTurnsAsFailed`
  6. `Supervisor_Schema1Migration_UpgradesToSchema2PreservingLegacyModelId`

#### C. Parity Comparison Cases
- Alert keys: SHA256 of `[projectId, megaTaskId || jobId, kind, evidence]`.
- Alert types: `conflicting-completion`, `execution-blocked`, `result-<id>`, `missing-result-<id>`, `repeated-failures`, `quiet-execution`.
- Supervisor state schema: `{ schema: 2, name: "Supervisor", configured: bool, modelId: string, autoReview: bool, alerts: [], turns: [], autoCalls: [] }`.

#### D. Execution Watch-Points
- **Turn Interruption on Restart**: Any turn with `status == "running"` on startup must be transitioned to `failed` with message `"This answer was interrupted by a backend restart. Ask again for current evidence."`.
- **Bounded Anomaly Auto-Review**: Auto-calls rate-limited to at most 3 invocations per hour (`3600000ms`).

---

### 5.2 `activity.cjs` -> `Activity.cs`

#### A. Module Mapping
- **C# Types**: `public static class Activity`
- **Primary Methods**:
  - `public static string CommandActivity(string? command)`
  - `public static string ToolActivity(string? name, JsonElement input)`
  - `public static string PlainActivity(string? text)`
  - `public static string EventActivity(JsonObject row)`
  - `public static IReadOnlyList<JsonObject> ActivityEvents(string? tail, JsonObject? context = null)`
  - `public static IReadOnlyList<string> ActivityMessages(string? tail)`
  - `public static string InterpretActivity(string? tail, JsonObject? facts, bool? alive)`
  - `public static string WorkerReport(string? tail)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/activity.test.cjs`, `engine/activity-events.test.cjs`.
- **Target C# Tests** (`ActivityTests.cs`):
  1. `CommandActivity_IdentifiesReadingSearchingTestingBuildingAndWriting`
  2. `ToolActivity_ParsesReadWriteShellSearchAndScreenshotTools`
  3. `PlainActivity_ExtractsErrorAndTestSummariesWhileFilteringJson`
  4. `InterpretActivity_AliveVersusStopped_IncludesLastRecordedAction`
  5. `WorkerReport_ExtractsFinalResponsePayloadFromStream`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Exact string templates:
  - `"Reading {target}."`
  - `"Searching project files near {target}."`
  - `"Inspecting project folders."`
  - `"Running tests for {target}."`
  - `"Building the application using {target}."`
  - `"Writing {target}."`
  - `"Reviewing source changes and Git history."`
  - `"Installing project dependencies."`
  - `"Stopped. Last recorded action: {message}"`
  - `"Worker is running. Waiting for a readable activity update."`

#### D. Execution Watch-Points
- Regex efficiency: Compiled regexes for classification patterns; ignores keyword matches inside echo quotes or source content.

---

### 5.3 `activity-sessions.cjs` -> `ActivitySessions.cs`

#### A. Module Mapping
- **C# Types**: `public static class ActivitySessions`
- **Primary Methods**:
  - `public static string ReadRecordedTail(string? file, string? root)`
  - `public static JsonObject? AssignmentSession(JsonObject options)`
  - `public static IReadOnlyList<JsonObject> ProjectActivitySessions(JsonObject options)`
  - `public static JsonObject PagedProjectActivitySessions(JsonObject options)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/activity-sessions.test.cjs`, `engine/activity-evidence.test.cjs`.
- **Target C# Tests** (`ActivitySessionsTests.cs`):
  1. `ReadRecordedTail_WithinRoot_ReadsLast32KBTruncatedToFirstNewline`
  2. `ReadRecordedTail_PathTraversalEscape_ReturnsEmptyString`
  3. `AssignmentSession_ValidWorkerResult_ValidatesIdentityAndBindsEvents`
  4. `PagedProjectActivitySessions_PaginatesMetadataWithoutReadingLogsPrematurely`

#### C. Parity Comparison Cases
- Limits: `SESSION_LIMIT = 24`, `EVENT_LIMIT = 50`, `TAIL_BYTES = 32768`.
- Session ID composite key: `encodeURIComponent(projectId) + ":" + encodeURIComponent(megaTaskId) + ":" + encodeURIComponent(attemptId)`.

#### D. Execution Watch-Points
- **Log Containment**: Uses `fs.realpathSync` equivalent (`Path.GetFullPath` / symlink resolution) to strictly ensure log files reside within the notebook root.
- **Lazy Tail Reading**: In paged queries, `readTail` is executed strictly on the slice of sessions being returned for the requested page.

---

## 6. Estimation, Metrics & Telephone Interface Subsystems

### 6.1 `time-left.cjs` -> `TimeLeft.cs`

#### A. Module Mapping
- **C# Types**: `public static class TimeLeft`, `public record TimeEstimate(int Minutes, string? EndsAt, string Sure, bool LongerThanUsual)`
- **Primary Methods**:
  - `public static string? CanonicalRole(string? role)`
  - `public static string RoleOf(JsonObject? step)`
  - `public static double TypicalMinutes(JsonObject step, IReadOnlyList<JsonObject> history)`
  - `public static TimeEstimate? Estimate(IReadOnlyList<JsonObject> history, IReadOnlyList<JsonObject> steps, string? now = null)`
  - `public static JsonObject? StepRecord(JsonObject input)`
  - `public static IReadOnlyList<JsonObject> ParseHistory(string text)`
  - `public static string HistoryText(IReadOnlyList<JsonObject> records)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/time-left.test.cjs`.
- **Target C# Tests** (`TimeLeftTests.cs`):
  1. `TypicalMinutes_SameRoleAndModel_PrefersSpecificMedian`
  2. `Estimate_ParallelIndependentSteps_TakesMaximumCriticalPath`
  3. `Estimate_DependentChainedSteps_SumsSequentialDurations`
  4. `Estimate_ActiveStepRunningLongerThanTypical_EnforcesFloorOf2Minutes`
  5. `ParseHistory_And_HistoryText_RoundTripsJsonlDeterministically`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Defaults: `{ Build: 20, Verify: 10, Review: 20, Research: 20 }`.
- `WORKING_FLOOR = 2`, `HISTORY_LIMIT = 500`.
- Confidence text: `"rough"` when any role count < 5, else `"based on {N} past steps"`.

#### D. Execution Watch-Points
- Pure data calculation: all timestamps provided by caller. Handles cyclical step dependencies without stack overflow via visited sets.

---

### 6.2 `step-outcomes.cjs` -> `StepOutcomes.cs`

#### A. Module Mapping
- **C# Types**: `public static class StepOutcomes`, `public record OutcomeRecord(...)`
- **Primary Methods**:
  - `public static string CanonicalRole(string? role)`
  - `public static string CanonicalClass(string? cls)`
  - `public static (string Company, string Class) ResolveModelMeta(JsonNode? model)`
  - `public static string? StripPathsAndCode(string? value)`
  - `public static JsonObject StepOutcomeRecord(JsonObject input)`
  - `public static void AppendStepOutcome(string dir, JsonObject record)`
  - `public static IReadOnlyList<JsonObject> ReadStepOutcomes(string dir)`
  - `public static JsonObject OutcomesSummary(IReadOnlyList<JsonObject> records)`
  - `public static string SummaryText(JsonObject row)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/step-outcomes.test.cjs`, `engine/step-outcomes-verify.test.cjs`.
- **Target C# Tests** (`StepOutcomesTests.cs`):
  1. `StripPathsAndCode_RemovesFileExtensionsCodeBlocksAndBraces`
  2. `ResolveModelMeta_ExtractsCompanyAndTierClasses`
  3. `AppendStepOutcome_AtomicAppendWithRetriesUnderConcurrency`
  4. `ReadStepOutcomes_CorruptedLines_SkipsInvalidAndLoadsValid`
  5. `OutcomesSummary_AggregatesTotalsSuccessRatesAndDurationAverages`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Sanitization: Redacts markdown blocks, backticks, `{...}`, Windows and POSIX absolute/relative paths, code filenames.
- Roles: `Build`, `Verify`, `Review`, `Research`. Classes: `flagship`, `mid`, `light`.

#### D. Execution Watch-Points
- Concurrency on `step-outcomes.jsonl`: File opens with `FileShare.ReadWrite` and retries on write collisions. Safe from corrupted partial lines.

---

### 6.3 `phone-voice.cjs` -> `PhoneVoice.cs`

#### A. Module Mapping
- **C# Types**: `public static class PhoneVoice`
- **Primary Methods**:
  - `public static string NormalizeLanguage(string? value)`
  - `public static string Say(string language, string key, IDictionary<string, string>? vars = null)`
  - `public static string TelegramAlertLabel(string language, string type)`
  - `public static string PlainAlert(string language, string type)`
  - `public static string StateLabel(string language, string state)`
  - `public static string FitTelegram(string text)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/phone-voice.test.cjs`, `engine/phone-captain.test.cjs`, `engine/phone-verification.test.cjs`.
- **Target C# Tests** (`PhoneVoiceTests.cs`):
  1. `NormalizeLanguage_EnAndIt_NormalizesVariants`
  2. `Say_SubstitutesVariablesAcrossEnAndItCatalogs`
  3. `TelegramAlertLabel_MapsStateIconsAcrossLanguages`
  4. `FitTelegram_ClosesUnclosedTagsAndTruncatesTo4096Characters`

#### C. Parity Comparison Cases
- Joins `native/porting/parity/cases.cjs`.
- Catalogs: Full English and Italian localized message catalogs across all keys (`done`, `failed`, `needs_owner`, `stopped`, `started`, `resumed`, `blocked`, `verify`, `switched`, `lead`, `restarted`, `updated`).

#### D. Execution Watch-Points
- Telegram HTML tag balancing: Properly balances `<b>`, `<i>`, `<code>`, `<pre>` when truncating long alerts to Telegram's 4096-character limit.

---

### 6.4 `phone-activation.cjs` -> `PhoneActivation.cs`

#### A. Module Mapping
- **C# Types**: `public static class PhoneActivation`, `public record ActivationAssessment(bool Allowed, string Model, IReadOnlyList<JsonObject> InstalledListeners, string Blocker)`.
- **Primary Methods**:
  - `public static bool SamePath(string? left, string? right)`
  - `public static JsonObject IsolatedPhoneSettings()`
  - `public static ActivationAssessment AssessPairedActivation(JsonObject options)`
  - `public static (string Root, string Attempts, string Plan) WriteIsolatedNotebook(string dir, string starterFile)`
  - `public static void CopyBackup(string fromDir, string toDir, string? attemptsDir = null)`
  - `public static string ReportMarkdown(JsonObject options)`

#### B. Test Coverage Requirements
- **Node Source Tests**: `engine/phone-activation.test.cjs`, `engine/phone-activation-gate.test.cjs`, `engine/phone-activation-verification.test.cjs`.
- **Target C# Tests** (`PhoneActivationTests.cs`):
  1. `SamePath_CaseInsensitiveOnWindows_MatchesNormalizedPaths`
  2. `AssessPairedActivation_ActiveInstalledListener_BlocksLiveExchange`
  3. `WriteIsolatedNotebook_GeneratesIsolatedSettingsWithoutToken`
  4. `CopyBackup_CopiesPlanPhoneSettingsAndTokenBetweenNotebooks`

#### C. Parity Comparison Cases
- Default Ollama model: `"qwen2.5:3b"`, default URL: `"http://127.0.0.1:11434"`.
- Assessment blocker reasons match character for character.

#### D. Execution Watch-Points
- Cross-platform path comparison: Resolves symlinks and normalizes directory separators (`/` vs `\`).


---

## 7. Cross-Cutting Architectural Decisions

### 7.1 Native AOT & Zero-Reflection Serialization (Rules 6 & 7)
- **Engine Project Constraints**: `native/Planwharf.Engine` compiles with Native AOT enabled (`PublishAot=true`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`). No reflection-based serialization is permitted.
- **JSON Primitives**: Modules process structured dynamic payloads via `System.Text.Json.Nodes.JsonObject`, `JsonArray`, and `JsonValue`, or strongly typed records annotated with source-generated `JsonSerializerContext`.
- **Base Library Only**: Zero third-party NuGet dependencies in the engine. All cryptographic operations (`SHA256`, `UUID/Guid`), path normalization, and process lifecycle management use standard `System.*` APIs.

### 7.2 Concurrency & Filesystem Synchronization (Rules 5 & 11)
- **Atomic Operations**: All state writes (`attempts.json`, `supervisor.json`, `account-folders.json`) write to a unique temporary file (`<target>.<uuid>.tmp`) and commit via `AtomicRename.RenameWithRetry`.
- **Lock Management**: File locks (`.lock`) open with exclusive non-sharing file streams (`FileMode.OpenOrCreate`, `FileAccess.ReadWrite`, `FileShare.None`).
- **Dead-PID Reclaim**: Stale locks left by crashed or terminated parent processes are validated against operating system PID tables before being cleaned up, preventing lock deadlocks.
- **Spin-Wait Delays**: Busy lock retry loops incorporate short spin delays (15–25ms) with strict fail-safe timeouts (5000ms).

### 7.3 Process Invocation & Subprocess Containment (Rule 9)
- **Argument Vector Hygiene**: All external command invocations (`git`, `gh`, `claude`, `codex`, `grok`, `agy`) construct argument lists strictly via `ProcessStartInfo.ArgumentList.Add(...)`. String concatenation of command lines is strictly forbidden.
- **Process Trees & Timeouts**: Subprocess wrappers enforce execution timeouts with automatic cancellation token triggers and process tree termination (`Kill(entireProcessTree: true)`).
- **Redaction**: Process stdout/stderr error streams pass through `Repository.CleanError` and `ProjectExport.Sanitize` to ensure tokens, passwords, and private SSH/HTTPS URIs are never persisted in execution ledgers.

### 7.4 Regular Expression & Text Encoding Semantics (Rules 4 & 8)
- **ECMAScript Flag**: Regular expressions handling identifier syntax, boundary assertions, and URL patterns specify `RegexOptions.ECMAScript | RegexOptions.CultureInvariant`.
- **Unicode Case Normalization**: Culture-invariant lowercasing (`ToLowerInvariant()`) is applied for system identifiers, while user-facing text comparisons follow specified cultures (e.g., `it-IT` for Italian language strings).

### 7.5 Parity Integration Strategy (Rule 12)
- **Parity JSON (`native/porting/parity/cases.cjs`)**: Pure functions in `project-export`, `execution-next`, `provider-catalog`, `provider-policy`, `model-routing`, `claude` (args/model parsing), `claude-usage` (parseClaudeUsage), `repository` (URL parsing/relations), `activity`, `time-left`, `plan-status`, `phone-voice`, `step-outcomes` join the parity runner.
- **Isolated Integration Test Fixtures**: Functions requiring real file descriptors, process spawns, or network calls (`attempts`, `account-folders`, `temp-cleanup`, `supervisor`, `activity-sessions`, `phone-activation`, `provider-status`) are exercised via comprehensive C# xUnit suites in `Planwharf.Engine.Tests` using ephemeral directory fixtures.

---

## 8. Verification Plan & CI Commands

When Job 38 implements this specification, verification must confirm that all 23 modules compile with zero warnings and pass both parity comparisons and native test suites.

```sh
# 1. Record JavaScript reference cases
node native/porting/parity/record.cjs

# 2. Verify JavaScript parity invariant
node engine/parity.test.cjs

# 3. Build C# Engine with Native AOT analyzers (zero warnings allowed)
dotnet build native/Planwharf.Engine -c Release

# 4. Run C# Engine Test Suite across all 23 ported modules and ParityTests
dotnet test native/Planwharf.Engine.Tests -c Release
```

---

## 9. Permitted File Scope for Implementation (Job 38)

Job 38 is authorized to touch only:
1. `native/Planwharf.Engine/*.cs`: The 23 C# module implementations.
2. `native/Planwharf.Engine.Tests/*Tests.cs`: The corresponding xUnit test files.
3. `native/Planwharf.Engine.Tests/ParityTests.cs`: Wiring of the new Phase 3 module functions into `ParityTests.Ports`.
4. `native/porting/parity/cases.cjs`: Appending Phase 3 function test vectors.
5. `native/porting/parity/*.json`: Output files generated by `node native/porting/parity/record.cjs`.
6. `docs/NEXT-WORK.md` and `docs/BUILT-WITH-PLANWHARF.md`: Progress updates.

The JavaScript files in `engine/*.cjs` and `engine/*.js` remain the immutable specification and must not be altered (Rule 1).
