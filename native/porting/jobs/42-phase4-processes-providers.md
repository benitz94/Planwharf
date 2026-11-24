# Job 42 (phase 4): processes and providers specification sheet

**Status: Sheet done.** Job 43 implements this sheet on
`port/43-phase4`; this branch contains documentation only. Stop before Job 43.

## Goal and evidence

Port all sixteen modules named by Job 42 in [NEXT-WORK](../../../docs/NEXT-WORK.md)
and phase 4 of [PLAN](../PLAN.md), following all twelve [porting rules](../README.md).
JavaScript is the behavioral specification; proposed C# seams below are design decisions,
not claims that those implementations exist. Preserve exact messages, JSON fields, defaults,
validation order, account/model identity and errors. No new engine packages or reflection.

The base was refreshed with `git fetch origin main` on 2026-11-23:
`944e1b1b322be4c30bc0d16710ab9637e4868f26`. The existing clean `port/42-sheet`
already pointed to that exact commit; no branch reset, recreation or merge was needed.
The historical briefing was checked against the repository, not treated as current evidence.

## Port

Each target is in `native/Planwharf.Engine/`; its matching test is in
`native/Planwharf.Engine.Tests/`. Begin each implementation with its exact source port comment.

| JavaScript source | C# target | Test target |
|---|---|---|
| `engine/runner.cjs` | `Runner.cs` | `RunnerTests.cs` |
| `engine/provider-models.cjs` | `ProviderModels.cs` | `ProviderModelsTests.cs` |
| `engine/external-adapters.cjs` | `ExternalAdapters.cs` | `ExternalAdaptersTests.cs` |
| `engine/api-agent.cjs` | `ApiAgent.cs` | `ApiAgentTests.cs` |
| `engine/provider-adapters.cjs` | `ProviderAdapters.cs` | `ProviderAdaptersTests.cs` |
| `engine/provider-connections.cjs` | `ProviderConnections.cs` | `ProviderConnectionsTests.cs` |
| `engine/antigravity.cjs` | `Antigravity.cs` | `AntigravityTests.cs` |
| `engine/planUsage.js` | `PlanUsage.cs` | `PlanUsageTests.cs` |
| `engine/worktrees.cjs` | `Worktrees.cs` | `WorktreesTests.cs` |
| `engine/plan-file.cjs` | `PlanFile.cs` | `PlanFileTests.cs` |
| `engine/pr-check.cjs` | `PrCheck.cs` | `PrCheckTests.cs` |
| `engine/phone.cjs` | `Phone.cs` | `PhoneTests.cs` |
| `engine/phone-ollama.cjs` | `PhoneOllama.cs` | `PhoneOllamaTests.cs` |
| `engine/phone-handover.cjs` | `PhoneHandover.cs` | `PhoneHandoverTests.cs` |
| `engine/phone-handover-live.cjs` | `PhoneHandoverLive.cs` | `PhoneHandoverLiveTests.cs` |
| `engine/phone-activation-check.cjs` | `PhoneActivationCheck.cs` | `PhoneActivationCheckTests.cs` |

## Responsibilities, interfaces and acceptance criteria

The method names below identify existing JavaScript entry points. Proposed C# public methods
use PascalCase, `JsonNode` for extensible records, `Task` for asynchronous operations and explicit
injected process/HTTP/filesystem/clock dependencies. Keep these dependency interfaces in their
owning module file. Do not reproduce Node's module-loader cycles as static initialization cycles.

### 1. Runner

Observed: `parseArgs`, `promptFor`, `askOrchestrator`, `askPrompt`, `askCliArgs`, `parseAskLine`,
`createAskStream`, `spawnWorker`, `run`, `whenFinished`, `killWorker`, `isProcessAlive`,
`abortAsk`, `abortSupervisor`, `patchStatus`, `cliModelOf`, `dropThinkingPrefix` and
`toolRequestRule` cover prompts, streaming, invocation, completion and termination.
Dependencies: ProviderRuntime/adapter dispatch, ModelRouting, ProjectWorkspace,
StructuredFinal, WorkerResult, ProviderDiagnostics, SavingMode, Repository and ChatMode.
Proposed: instance-owned keyed lead calls and separate supervisor slot; injectable process host,
clock and worker-host launch descriptor, returning launch receipts and structured completion.
Map `askOrchestrator` to `AskOrchestratorAsync`: it owns keyed lead/supervisor calls,
empty-question handling, streamed progress and the answer/thinking/duration/failure result.

Acceptance: exact argv/stdin/cwd/account environment; `--job-file` validation and legacy args;
exclusive worker-input file and append-only log; reject escaped cwd before spawning. A new lead
call aborts only its own key; an old completion cannot delete its replacement. Supervisor admits
one API advisory call with no tools, rejecting other transports without fallback. Empty questions
return empty answer/thinking and zero duration. Streaming must preserve fragmented UTF-8/JSONL,
CRLF, final unterminated lines, ignored malformed rows, tool deduplication, usage and exact Claude
model/final-result validation. Preserve `PROVIDER_PROTOCOL_CHANGED` and identity mismatch errors.
Read both pipes concurrently; handle stdin EPIPE and spawn failure without crashing the caller.

Lead silence limits are 180000 ms with cwd, 90000 without; hard limits 900000/300000 ms.
Either output stream resets silence, never the hard limit. Stderr retains the last 4000 characters;
structured output uses StructuredFinal's `MAX_OUTPUT_CHARS` (the JS chunk append can overshoot it).
`whenFinished` drains until close or 5000 ms after exit even when a descendant holds a pipe open;
terminal processing must be idempotent across error/exit/close/cancel races. Worker `run` has no
independent execution timeout. Structured COMPLETE requires valid assigned result identity, claimed
files/report evidence and successful provider exit. Antigravity requires final status SUCCESS;
Claude requires protocol evidence. Preserve usage, diagnostic, exit and process sidecar schemas.
Legacy unstructured exit-0 completion is observed behavior, not proof of structured completion.

### 2. ProviderModels

Observed: `createProviderModels` returns `refresh`/`snapshot`, plus catalog `invalidate` and
`observeScope`; `listGrok` discovers exact IDs. Dependencies: ProviderCatalog, ProviderAdapters,
ProviderRuntime, Antigravity, filesystem, SHA-256 and caller clock. Proposed: discovery service
with async delegate and serialized cache persistence. Acceptance: 60000 ms TTL, forced refresh,
legacy single-flight versus queued catalog refresh, independent providers, disabled/not-installed
states, deduplicated exact IDs and new models disabled by default. A failed discovery retains
previous same-scope availability without claiming fresh verification. Account/configuration hash
and generation prevent stale in-flight results granting access, including switch-away-and-back.
Persist invalidated scopes across restart; failed strict invalidation keeps access blocked. Cache
schema 1, exclusive temporary write/rename, corrupted cache and failed rename all need tests.
Grok listing uses 15000 ms and 1 MiB, ANSI removal and distinct policy-lock/login diagnostics.

### 3. ExternalAdapters

Observed: `apiConfig`, `apiHeaders`, `configuredCommand`, `cliSpec`,
`registerExternalAdapters`. Dependencies: Runner termination, filesystem, environment and HTTP.
Proposed: adapter factories with supplied environment/executable lookup and HTTP transport.
Acceptance: API HTTPS or HTTP loopback only; reject URL credentials/query/fragment; retain exact
authEnv validation and OpenAI/Anthropic headers. Defaults: 4096 output tokens, 30 rounds, 90000 ms;
positive integer maxima: 65536 tokens, 100 rounds, 300000 ms. Configured native executable and
argument arrays must include `{model}`, explicit permission-mode args, `{cwd}` substitution and
one of codex-jsonl/messages-jsonl/antigravity-jsonl. Preserve stdin envelopes, exact provider/model
identity, unsupported research rejection and structured-result requirement for API workers.
API `/models` uses 10000 ms, rejects redirects and discovers models without granting tool roles.
Unknown quota stays unread; observed tokens must never become invented remaining quota.

### 4. ApiAgent

Observed: `runAgent`, `executeTool`, `workspacePath`, `responseJson`; standalone stdin host emits
JSONL and exits 1 on errors. Dependencies: ExternalAdapters headers, WorkerResult validation,
filesystem, process and HTTP. Proposed: asynchronous service with injected request/event emitter;
native host transport is a separate unresolved integration boundary below.
Acceptance: both request formats and tool-result conversations, exact model mismatch rejection,
only nonnegative safe-integer token usage, malformed JSON/assistant/tool-call failures, filtered or
truncated output, empty answer and turn exhaustion. At most 32 calls per turn; response bound
4 MiB, stdin bound 16 MiB. HTTP error bodies never enter diagnostics. Advisory offers no tools
and rejects returned calls; lead cannot write files/results or run commands. Worker commands
require alwaysApprove and argv, timeout 60000 ms, buffer 65536 bytes, tails 24000/8000 characters.
Files: read 64 KiB, write/result 1 MiB, list 1000 entries. Resolve existing ancestors to reject
symlink/junction escape, sibling-prefix/traversal and control-file writes (`.git`, `.codex`,
`.agents`); validate assigned result identity before its exceptional out-of-workspace write.
Tools denied at execution become tool error records; protocol failures reject the turn.

### 5. ProviderAdapters

Observed: registry `adapters`, `adapterFor`, `registerAdapter`; required executable, availability,
execute, environment, terminate, quota, discover and resultIdentity functions plus nativeModel regex.
Dependencies: ProviderRuntime, ProviderAccounts, Claude, ClaudeUsage, PlanUsage, Antigravity,
ProviderModels, ExternalAdapters and Runner. Proposed: explicitly composed registry with delegate
contracts; no reflective plugin loading. Acceptance: duplicate/missing-method/invalid-regex errors,
unknown adapter lookup, built-in and external registrations, exact account-aware identity and
permissions. Codex discovery reads the selected auth folder and existing catalog URL with 10000 ms
timeout; test missing auth, HTTP failures and invalid catalog. Preserve the distinction between
reported canResearch capabilities and sourceProtectedResearch (only Codex is source-protected).

### 6. ProviderConnections

Observed: pure `connection(input)` builds a provider and configured models via `apiConfig`.
Proposed: `Connection(JsonNode)` returning validated JSON. Acceptance: reject unknown fields/secrets,
ID outside lowercase pattern (64 chars), blank/over-100 label, unknown adapter, absent/duplicate/
whitespace model IDs (1..100 IDs, at most 200 chars each), nonboolean toolsEnabled, missing explicit
CLI argument arrays/model placeholder/output protocol. New provider is enabled with alwaysApprove
false; models preserve exact IDs and roles but availability remains unverified. Test every boundary,
both adapter branches and error ordering. Configuration alone is never a successful inference probe.

### 7. Antigravity

Observed: `poolOf`, `parseUsage`, `parseModels`, `readAntigravity`, `listAntigravity`,
`CLAUDE_GPT_POOL`. Dependencies: PlanUsage unread state, PlanStatus and executable/process lookup.
Proposed: pure parsers and injected process calls. Acceptance: Gemini and Claude/GPT pools remain
separate; only claude-/gpt-oss- prefixes select the latter. Valid finite fractions 0..1, rounding,
tightest raw fraction and exact-zero exhaustion, absent buckets, provider errors and extraPlans.
Usage argv includes `-p /usage --output-format json --print-timeout 15s`; actual process limit
20000 ms and 1 MiB for usage/listing. Parse BOM, ANSI, duplicate IDs and first readable label;
unknown format never guesses aliases. Missing executable, killed process and login errors differ.

### 8. PlanUsage

Observed source is **`engine/planUsage.js`**, not a `.cjs` module. Public functions:
`parseGrokCredits`, `parseChatGptUsage`, `windowOf`, `planExhausted`, `unreadPlan`,
`emptyPlans`, `readChatGpt`, `readGrok`, `fetchPlanUsage`; constant `PLAN_IDS`.
Dependencies: ProviderAccounts, PlanStatus, Antigravity, auth files and HTTP. Proposed: pure
parsers plus injected home/env/clock/HTTP readers. Acceptance: numeric-only percentages, JS rounding,
reset timestamps, strictest quota window, blocked plans independent of readability; empty and
malformed responses stay unread. Preserve settingsFolder/CODEX_HOME/GROK_HOME precedence and
separate account header. Requests abort after 8000 ms. Grok refresh within five minutes of expiry;
ChatGPT retries once after 401 when refresh exists. Refreshed tokens are used in memory, not written
back to auth files. Test refresh failures, fallback key, timeout versus unreachable and provider
plan-status errors. Parallel aggregation preserves ChatGPT/Grok/Gemini/extraPlans order.

### 9. Worktrees

Observed: `repository`, `branchFor`, `worktreePath`, `create`, `integrate`, `cleanup`, `git`,
`sweepOrphans`. Dependency: ProjectWorkspace validation/git environment. Proposed: service with
injected argv-based Git and filesystem. Acceptance: the `git()` helper uses 30000 ms/8 MiB;
direct check-ref-format, show-ref and MERGE_HEAD probes have no explicit timeout/maxBuffer.
Test failed probes and simulated hangs through the command seam. Require 1..100 character
identity components, safe integer part index and Git branch validation. Reuse only the registered
matching worktree/branch; preserve occupied destinations. Dirty root or any dirty/missing part
blocks integration before merging. Ordered no-ff merges retain earlier successful merges if a
later merge fails; abort only the active merge and preserve branches. Cleanup verifies exact work
path, retains dirty work normally, and force deletion is only the explicit discard operation.
Orphan sweep is destructive based on supplied live IDs; it must not run during inspection/tests
against personal data. Test invalid identity, spaces/Unicode, restoration and concurrent parts.

### 10. PlanFile

Observed exports include `isNoise`, `dirtyMessage`, `namedBranch`, `branchName`,
`pullRequestTitle`, `crewSection`, `shortGoal`, `prepare`, `writePlan`, `read`, `parse`,
`tickStep`, `finish`, `commitNote`, `currentBranch`, `pushed`, `planPath` and constants.
Dependency: ProjectWorkspace git environment. Proposed: pure text helpers plus injected Git/files/
clock for plan lifecycle. Acceptance: exact Markdown/heading escaping, CRLF parsing, duplicate-step
labels, title-before-role-before-first-unchecked ticking, same-goal progress retention and archives
without collisions. Managed exclude marker must not remove user rules; keepWorkPlan only removes
its own marker. Prepare supports non-Git folders, offline current HEAD, refreshed default branch,
existing local/remote-only branches and merge-base preservation. Observed prepare stashes dirty
work on another branch; this is a port contract, not authorization to invoke it on this checkout.
Git defaults 20000 ms/8 MiB, fetch/default remote up to 30000 ms. Uncommitted/unchanged base must
remain `not committed`; recorded completion does not fabricate push or verification evidence.

### 11. PrCheck

Observed: `listPrs`, `resolveConflicts`, `prepareFix`, `prepareWorktree`, `finishFix`,
`removeWorktree`; dependency ProjectWorkspace git environment, Git/gh, files and process host.
Proposed: injected command runner for every entry point, preserving command/result order.
Acceptance: both check conclusions and legacy status reds, malformed/closed PR, invalid branch,
fetch/add/merge failures, detached repair worktree and preserved conflict list. Reject remaining
conflicts/markers before commit/push; no-op avoids push. Non-force push targets the exact PR branch;
refusal returns kept:true and preserves the local commit. Test remote advancing, resume and two
independent folders with fake Git/gh or disposable local repositories only. Source run has no
timeout/output bound. `resolveConflicts` reads conflicts after abort: preserve/flag this limitation,
do not silently improve it. Product push/merge functions are specified, never executed by Job 42.

### 12. Phone

Observed: settings/token persistence, sanitation/formatting, ntfy/Telegram sends, polling and
`createPhoneNotifier` (getSettings, updateSettings, sendAlert, checkTransitions, pollUpdates,
processTelegramMessage, probeOllama). Dependencies: PhoneOllama, PhoneVoice, SavingMode, files,
HTTP and injected snapshot/stop/resume/decision callbacks. Proposed: notifier instance with two
serialized lanes (transitions and commands), private store and injected transport/clock/randomness.
Acceptance: exact English/Italian product strings, HTML escaping, step zero, crew replacement,
status/step deduplication, redacted secrets/paths, token preview only in public settings. Preserve
private phone-settings.json and separate telegram-bot.token, corrupt recovery, unpair/reset behavior.
Pair by start code; ignore foreign chats; ambiguous project selection must not act. Stop confirmation
expires at 60000 ms by default; callbacks and decision delegation use current paired identity.
Persist next offset **before** handling each accepted update; restart must not replay commands.
Failed handlers must not poison the serialized lane. Test both channels independently, concurrent
polls/alerts/pairing, unavailable snapshot, language changes, stale confirmations and decision errors.
Observed network helpers have no client timeout; Telegram's timeout query is not a request deadline.

### 13. PhoneOllama

Observed: defaults/normalization, escaping/greetings/confirmation recognition, chat memory,
projectFacts/buildReplyMessages, askOllama/listInstalledModels. Dependencies: PhoneVoice/files/HTTP.
Proposed: pure formatting and memory object, injected filesystem and HTTP. Acceptance: HTTP loopback
only (127.0.0.1/localhost/::1), no credentials/query/fragment, default qwen2.5:3b, model regex/120-char
limit. Memory defaults 8 messages, caps configured limit at 20, each normalized text 500 characters,
independent chats and atomic temporary rename; corrupt file starts empty. Facts cap 12 projects,
prompts cap 2400 characters plus bounded history/question. `/api/chat` uses stream:false,
keep_alive:5m, temperature 0.2, num_predict 180; 45000 ms per attempt, exactly one retry after
failure; `/api/tags` 4000 ms. Test cancellation, empty/invalid JSON, HTTP error, HTML model output,
isolated concurrent replies and clear-memory commands without executing model-suggested actions.

### 14. PhoneHandover

Observed: `runHandover`, `handoverPlan`, `exchangeEvidence`; depends on PhoneActivation.
Proposed: callbacks for pause/poll/reply/start/alert/stop/resume and one shared handover gate.
Acceptance: backup/token/different build/restore-address prerequisites before pause; reject a second
handover while the first is active. Pause failure does not start or resume. After successful pause,
blocked/error/ok:false poll forbids build and idle reply; restoration still runs. Stop failure cannot
skip resume. Success needs restored polling and nonempty outbound text. Missing/throwing resume is
reported unrestored. Always release the busy gate. Redact token-shaped evidence, cap text at 600.

### 15. PhoneHandoverLive

Observed: `main`, `redact`, `projectsFromPlan`, `readJson`, `createLivePoller`; dependencies
PhoneActivation, Phone, PhoneHandover, files, native process and legacy app HTTP endpoints.
Proposed: explicitly supplied installation/build/data/report paths and host callbacks; no personal
hardcoded path in portable defaults. Preserve orchestration ordering through tests. Poll retries
409/conflict at most eight times with 500 ms waits; other errors stop immediately. Pending ack is
max integer update ID + 1 only on successful poll, not failed retries. Live workflow backs up,
pauses listener, uses isolated notebook, waits up to 20000 ms in 200 ms intervals for startup,
stops only the test build, restores polling and removes copied token in finally. Test every failure
stage, absent startup address, cleanup failure and redacted report. Do not run its live main here.

### 16. PhoneActivationCheck

Observed: argument-driven script with no exports and unconditional `main()` execution; dependencies
PhoneActivation, PhoneOllama and filesystem. Proposed: callable check service receiving argument
array and injected probes/report writer; do not import the JS script into the parity recorder.
Acceptance: --from/--attempts/--backup-to/--notebook/--starter/--report/--listeners/--build;
isolated notebook and backup evidence, token-presence assessment without exposing token, model-list
probe 4000 ms and optional installed-model reply 60000 ms. Only `--live yes` requests liveExchange,
and assessment.allowed remains its gate. Preserve report fields and ALLOWED/BLOCKED output;
BLOCKED itself is not an exception exit, thrown failures exit 1. Report JSON path replacing `.md`
can collide with Markdown for a non-.md name: record as an observed risk, not a silent port fix.

## Tests to port and deterministic scenarios

Keep each existing test's meaning and record every omitted case with its dependency/reason in
Job 43's handover. These are source test families, not a claim that Job 42 ran them all.

| Target test | JavaScript evidence and required coverage |
|---|---|
| RunnerTests | `runner.test.cjs`, `runner-exit.test.cjs`, `worker-containment.test.cjs`, `grill-chat.test.cjs`, `final.test.cjs`, `claude.test.cjs`, `supervisor-transport.test.cjs`: prompts, exact model, split streams, exit grace, structured result and keyed cancellation |
| ProviderModelsTests | `provider-models.test.cjs`: TTL, unread retention, missing executable, account/config generation races, restart and failed durable invalidation |
| ExternalAdaptersTests / ApiAgentTests | `external-adapters.test.cjs`, `supervisor-transport.test.cjs`: both protocols, child-process transport, tool permissions, containment, secret-free argv, usage and malformed responses |
| ProviderAdaptersTests | `providers.test.cjs`, `external-adapters.test.cjs`, `accounts-job16.test.cjs`, `accounts-job16-verify.test.cjs`, `accounts-job16-verify2.test.cjs`: registry, identity, account isolation, quota/catalog selection |
| ProviderConnectionsTests | `provider-connections.test.cjs`: validation and roles; replace server persistence setup with isolated provider records for unit cases |
| AntigravityTests | `providers.test.cjs`, `grill-chat.test.cjs`: model and usage parsers, fixtures `fixtures/antigravity-usage.json`, `fixtures/antigravity-lead.jsonl` |
| PlanUsageTests | `providers.test.cjs`, `accounts-job16.test.cjs`, `accounts-job16-verify.test.cjs`, `final.test.cjs`: quotas, refresh, auth folder and unavailable service versus unavailable model |
| WorktreesTests | `worktrees.test.cjs`: isolated registered trees, ordered integration, dirty/missing/occupied paths, conflict recovery and simultaneous part commits |
| PlanFileTests | `plan-file.test.cjs`, `plan-file-verify.test.cjs`, `plan-file-tick.test.cjs`, `plan-option.test.cjs`: branch/base, preserved work, archives, heading injection, tick precedence and tracked-plan option |
| PrCheckTests | `pr-fix.test.cjs`, `pr-fix-verify.test.cjs`, `pr-panel-http.test.cjs`: prepared tree, no-op, markers, non-force push refusal and retained commit |
| PhoneTests | `phone.test.cjs`, `phone-edge.test.cjs`, `phone-verification.test.cjs`, `phone-captain.test.cjs`, `phone-decisions.test.cjs`: exact formatting, pairing, private persistence, commands, decisions, deduplication and error recovery |
| PhoneOllamaTests | `phone-ollama.test.cjs`, `phone-activation-verification.test.cjs`: loopback, bounded memory, one retry, isolated concurrent answers and model identity |
| PhoneHandoverTests / PhoneHandoverLiveTests | `phone-handover.test.cjs`: every failure after pause, restoration, busy gate, eight 409 retries, pending ack and subsequent recovery |
| PhoneActivationCheckTests | `phone-activation-gate.test.cjs`, `phone-activation-verification.test.cjs`: reuse assessment/backup cases through injected check service; add wrapper argument/report/error cases (no direct wrapper unit suite exists) |

All filenames in this table are under `engine/` unless prefixed otherwise. Integration cases in
worktrees/execution, server HTTP phone/connections/PR panels, orchestrator and PR model-switching
tests stay deferred to phases 5–7 when those consumers exist. Their module-level behaviors above
remain mandatory now through injected callbacks. No fake assertion may claim actual OS isolation.

Use Job 03's existing `native/Planwharf.FakeCli` and `native/porting/fakes/{claude,codex,grok,agy}/`.
Set PLANWHARF_CLAUDE/CODEX/GROK/ANTIGRAVITY to the fake executable and use PLANWHARF_FAKE_AS,
PLANWHARF_FAKE_SCENARIO and PLANWHARF_FAKE_LOG per isolated test. Scenarios match argv, stdin or
call number; support pause, stdout/stderr, files and exit code. The call log proves arguments,
stdin, cwd and selected environment. The fake reads stdin to EOF before responding, so the caller
must close its input. Use synthetic credentials only: its environment log is not a secret vault.
Reuse lead-normal, worker-success/fail, refusal, model and usage recordings, checking actual protocol
identity fields against the chosen scenario; legacy recordings are not proof of current CLI access.

Required scenarios: valid result + exit 0; exit 0 without result; wrong attempt/model; valid COMPLETE
+ nonzero exit; BLOCKED result; malformed final row; output split inside Unicode/JSON; pipe held after
parent exit; stdin closed early; spawn error; silence and hard deadlines; cancel before start/during
write/after exit; two lead keys; same-key replacement; descendant kill and failed kill confirmation.
Use injected stream/time/process doubles for exact chunking and races. Existing FakeCli does not
provide a descendant-tree or arbitrary stream-chunk scheduler: any added native fake mode must have
its own FakeCli test, or use a test-owned OS process fixture. Do not assume replay alone proves tree kill.
HTTP tests use fake handlers or test-owned loopback endpoints; Git tests use fixtures and a local bare
remote, never this checkout's origin. No bot pairing, real AI inference or user credentials in CI.

## Comparison (rule 12)

The existing recorder is synchronous and JSON-only: do not record a Promise as `{}` or add imports
that execute a live script. Add a `planUsage` PATH_MAP entry for `engine/planUsage.js`.
Use the following pure comparison surface (valid, empty/missing, invalid/boundary cases for each):

| Module | Committed JavaScript comparison cases |
|---|---|
| runner | legacy `parseArgs` (not job-file IO), `dropThinkingPrefix`, `toolRequestRule`, `cliModelOf`, `parseAskLine` with explicit accumulator; tool events, usage, final/mismatch and malformed rows |
| external-adapters | `apiConfig`, `apiHeaders` with explicit synthetic env; schemes, limits, missing auth and exact errors |
| provider-connections | `connection`: valid API/CLI and each validation failure; compare full generated model objects |
| antigravity | `poolOf`, `parseUsage`, `parseModels`: pool separation, fraction edges, errors, ANSI/duplicate/label cases |
| planUsage | all exported pure parsers, `windowOf`, `planExhausted`, `unreadPlan`, `emptyPlans`; null/string/number percentage distinctions and timestamps |
| plan-file | `isNoise`, `dirtyMessage`, `namedBranch`, `branchName`, `pullRequestTitle`, `crewSection`, `shortGoal`, `parse`; escape/CRLF/Unicode and ordered crew cases |
| phone | exported pure alert/crew/format/sanitize/status/match/mask functions; exact English/Italian text, hostile HTML, zero steps, ambiguous names |
| phone-ollama | defaults/normalize, escapeHtml/withCaptain, isClearMemory/isBareConfirmation, projectFacts/buildReplyMessages (no function-valued sanitize), unavailableReply |
| phone-handover | handoverPlan/exchangeEvidence with platform-independent paths and sanitized synthetic evidence |
| phone-handover-live | redact/projectsFromPlan only; never main |

For each row, add vectors in `cases.cjs`, generated `<module>.json`, real executable delegates in
`ParityTests.Ports`, and a nonzero case floor in `required.json` derived from the approved vectors.
No lowering the existing 31-module floors. Compare result/error text, null versus absent fields,
array ordering, JS coercion/rounding/regex and UTF-16 slicing. Mutated accumulators require separate
post-call state assertions. Registry and async services (`provider-models`, `provider-adapters`,
`api-agent`, `worktrees`, `pr-check`, `phone-activation-check`) use deterministic scenario comparison
through their module test files: same recorded requests, simulated replies, effects and final state
on both sides. The current allowlist has no JavaScript scenario recorder or generated fixtures
for these six modules; independently authored C# tests cannot prove this comparison. Therefore
side-effect comparison approval is a prerequisite to Job 43: review must authorize a concrete
recorder, fixture paths and replay/check command, or an explicit rule-12 exception. Until then,
these six modules' comparison plan is blocked, not an approved exemption or completed coverage.

**Owner decision (24 November 2026): a scenario recorder is approved, not a rule-12 exception.** Job 43
adds `native/porting/parity/scenarios.cjs`. For each of the six modules (`provider-models`,
`provider-adapters`, `api-agent`, `worktrees`, `pr-check`, `phone-activation-check`) it runs the JavaScript
through fixed scenarios with simulated replies only (a fake `spawn`/`spawnSync`, a fake `fetch`, a temporary
folder or a throwaway git repository; no real AI program, network or credentials), and writes
`native/porting/parity/scenarios/<module>.json`: every request made (command and arguments, or method and
URL with no secret), the reply given, the effects on files and git, and the final result or error.
`ScenarioParityTests` in `Planwharf.Engine.Tests` replays each file against the C# port with the same
simulated replies and must produce the same requests, effects and result. The check command is
`node native/porting/parity/scenarios.cjs --check` (fails when a recorded file differs from a fresh run) and
`dotnet test --filter ScenarioParityTests`. `required.json` lists the six modules with a nonzero scenario
floor. A scenario that cannot be made deterministic is named in the sheet and asked to the owner; it is
never dropped silently.
`branchFor` invokes git check-ref-format, so it belongs to fixture tests, not the pure recorder.

## Platform risks and decisions that remain open

1. **Native worker/API host.** The engine is a net10.0 library; Node uses runner.cjs/api-agent.cjs
   as executable entry points. Proposed services accept a host-launch delegate and can be tested
   using FakeCli; this does not establish a deployable detached host. Job 43 must resolve the native
   host packaging/file scope before claiming full lifecycle parity. No Node fallback or app/server
   integration is authorized by this sheet. Phase 7 remains direct in-process engine integration.
2. **Dependency gaps.** `providers.cjs` is a dispatch facade outside the sixteen-name list. Compose
   its route validation/dispatch inside ProviderAdapters, reuse ProviderRuntime for built-ins;
   do not silently add a seventeenth port. Runner's ChatMode dependency has no ChatMode.cs in this
   base. Its prompt text can be supplied through an explicit callback; full default prompt parity
   is blocked until that dependency's port/scope is approved. ProviderRuntime's current launch record
   lacks external outputProtocol; keep the extended descriptor in ExternalAdapters/Runner rather
   than rewriting the prior port. If existing signatures cannot compose, report a scope blocker.
3. **Tree termination.** JS Windows uses taskkill /F /T (8000 ms), then terminate-tree.ps1
   (15000 ms) and checks root liveness. POSIX targets the process group, waits 2000 ms after TERM,
   then 1000 ms after KILL; ps ignores zombies and falls back to root if unavailable. Proposed native
   backend must prove equivalent descendant cleanup, including parent-exited cases; cancellation of
   WaitForExitAsync alone does not stop a process. Shell-free argv and hidden Windows launches are
   mandatory. Exact Job Objects/process-group mechanism needs cross-platform implementation evidence;
   do not label root-only kill as equivalent. AOT-compatible platform interop stays in Runner.cs.
4. **Bounded operations versus observed gaps.** Worktrees has unbounded Git probes outside its
   bounded `git()` helper (check-ref-format, show-ref and MERGE_HEAD); exercise probe errors/hangs
   with injected results, without leaving real hung subprocesses. PrCheck and Phone network helpers lack explicit
   deadlines/output bounds; runner lead accumulation also lacks a total bound. New limits would
   change behavior. Retain this discrepancy for review; require explicit bounded host configuration
   before exposing new production execution, with no invented default claimed as JS parity. Preserve
   specified per-operation limits where they exist; cancellation must still clean up owned resources.
5. **Portable live validation.** PhoneHandoverLive contains a personal installedExe and Windows-only
   build/taskkill assumptions. Proposed path/process injection is testable, but its real installed-app
   HTTP control cannot be claimed ported for the final no-server architecture. Keep this live wiring
   deferred to owner Windows testing and later host integration. No automatic live main on import.
6. **Data and safety gaps.** Worktrees.sweepOrphans deletes directories from a supplied live-ID set;
   there is no demonstrated durable lock against a concurrent new work. Phone offsets advance before
   handling (at-most-once recovery, possible lost action on crash), and settings serializers discard
   unknown fields. Rule 5's unknown-field preservation conflicts with that observed behavior: retain
   raw-file fixtures and seek a reviewed resolution before calling round-trip parity complete.
   API symlink checks do not prove race-free OS sandboxing. Job 35's sandbox design is not implemented
   or newly authorized here. No broader sandbox feature or changed permissions in Job 43.

Cross-platform tests must cover paths with spaces/non-ASCII, Windows case/drive/UNC/junctions,
POSIX permissions and signals, line endings, UTF-8 split decoding, JS date/number/regex semantics,
owner-only token ACLs versus Unix mode 0600, rename contention and two independent test stores.
Windows/Linux ARM are supported targets; do not infer hand-tested ARM behavior from compilation.
Reviewer must decide whether the explicit deferred host seams are acceptable for Job 43 or require
a separate scoped host job. None of the blockers above is silently resolved by this draft.

## Check

Job 42: run existing sheet/documentation checks and read-only parity checks, plus `git diff --check`
and the Phase 4 sheet test suite:
`node --test engine/job42-sheet.test.cjs`
This test suite independently validates that all sixteen modules, mappings, parity commands,
error paths, recovery paths, concurrency paths, edge cases, and open design decisions are covered.

Job 43 (requirements, not execution claims for this sheet):

```text
node native/porting/parity/record.cjs
node native/porting/parity/record.cjs --check
node --test engine/parity.test.cjs
node scripts/engineering/ci.cjs
dotnet build native/Planwharf.Engine -c Release
dotnet test native/Planwharf.Engine.Tests -c Release
dotnet build native/Planwharf.FakeCli -c Release
dotnet test native/Planwharf.FakeCli.Tests -c Release
```

Require zero warnings, real assertions over each scenario, and Native app CI on Windows/macOS/Linux.
After Job 43, stop for the owner's Windows hand test with real programs: exact invocation and account,
lead/worker success, refusal, cancellation, descendants, result evidence, Unicode paths and restart.
Fake results cannot attest real model availability, authentication, quota or OS sandbox protection.

## Explicit Job 43 file allowlist

- Exactly the sixteen C# implementation files and sixteen test files enumerated in **Port** above.
  Supporting interfaces/records belong inside those files, not a blanket engine-directory allowance.
- `native/Planwharf.Engine.Tests/ParityTests.cs` for the specified comparison delegates.
- `native/porting/parity/cases.cjs`, `native/porting/parity/required.json`,
  `native/porting/parity/record.cjs` (only the planUsage mapping), and generated
  `runner.json`, `external-adapters.json`, `provider-connections.json`, `antigravity.json`,
  `planUsage.json`, `plan-file.json`, `phone.json`, `phone-ollama.json`, `phone-handover.json`,
  `phone-handover-live.json` within that parity directory.
- `native/Planwharf.FakeCli/Program.cs`, `native/Planwharf.FakeCli.Tests/FakeCliCoreTests.cs`,
  `native/Planwharf.FakeCli.Tests/FakeCliEdgeTests.cs` and
  `native/Planwharf.FakeCli.Tests/FakeCliRecoveryTests.cs` only if required process scenarios
  exceed existing replay. No dependency or project-file change is implied.
- `docs/NEXT-WORK.md`, `docs/BUILT-WITH-PLANWHARF.md`, `.planwharf/plan.md` for honest progress/evidence.

Do not modify JavaScript engine modules/tests, app/server, workflows, packages, prior C# ports,
personal data, historical evidence or other job sheets. New executable projects, host entry points,
additional fake/test filenames or unlisted dependencies require a revised reviewed scope first.

## Scope and hand over

Job 42 may change this canonical sheet, `engine/job42-sheet.test.cjs`, `docs/NEXT-WORK.md`,
`docs/BUILT-WITH-PLANWHARF.md` and `.planwharf/plan.md` only. The structured attempt result
is separate execution evidence. No source, fake fixture, skill, workflow or historical log
changes belong to this sheet job.

The configured independent reviewer was provider `antigravity`, CLI model
`claude-sonnet-4-6` (roster ID `antigravity:claude-sonnet-4-6`), in dependent slice ST-02.
Following Claude quota exhaustion (429), verification was routed to Gemini 3.8 Flash
(`gemini-flash`, Google DeepMind via Antigravity) for cross-company verification.

### Draft validation and reviewer handoff (2026-11-23)

- `git diff --check`: passed during drafting.
- `node native/porting/parity/record.cjs --check`: passed, no generated fixtures changed.
- `node --test engine/job23-sheet.test.cjs engine/job37-sheet.test.cjs engine/parity.test.cjs`:
  20 passed, zero failures. These validate existing sheets/parity, not completeness of Job 42.
- No C# build/test, full engineering CI, live provider/bot test or independent review was run.
- Startup `python .agents/check-updates.py` succeeded and found upstream updates and local
  adaptations; none was installed or overwritten.
- A read-only Python assertion check matched all 16 module names from the Job 42 roadmap row
  to existing source paths and 16 contract sections; checked sheet links, branch/base and the
  documentation-only changed-file set. It passed. This is structural checking, not independent review.
- `.planwharf/plan.md` is ignored in this checkout (`git check-ignore` confirmed it). It is on disk;
  finalization must explicitly stage that authorized file with `git add -f .planwharf/plan.md`
  if including the work record in the requested commit. Do not change ignore rules or stage archives.

### Verification attempt 69 (2026-11-23)

The caller rerouted ST-02 to Codex / gpt-6-astra. Its standards/specification checks
corrected the Runner interface list, Worktrees subprocess bounds, Job 43 CI checklist
and the unresolved recording scope for six side-effect modules. Existing sheet/parity
tests passed 20 cases; targeted discovery, connections, handover and exit tests passed
32 cases. The work plan contains an additional reproducible documentation regression
check, observed failing before correction and passing afterwards, with deletion controls.

A direct review request to `antigravity:claude-sonnet-4-6` failed with quota exhaustion
(RESOURCE_EXHAUSTED, HTTP 429), returning no review. Codex findings do not satisfy the
cross-company requirement. Status remains draft; finalization and commit remain pending.

### Verification attempt 70 (2026-11-23)

Independent verification conducted by `gemini-flash` (`gemini-3.8-flash-high`, Google DeepMind via Antigravity)
following Anthropic API quota exhaustion on `claude-sonnet-4-6` (HTTP 429). Google DeepMind is an independent
cross-company model verifying the Codex / `gpt-6-astra` draft.

Verification actions:
1. Re-verified all existing tests:
   - `python .agents/check-updates.py`: passed; reports upstream updates and local adaptations without installing.
   - `node native/porting/parity/record.cjs --check`: passed; zero parity fixtures changed.
   - `node --test engine/job23-sheet.test.cjs engine/job37-sheet.test.cjs engine/parity.test.cjs`: passed 20 tests.
   - `node --test engine/provider-models.test.cjs engine/provider-connections.test.cjs engine/phone-handover.test.cjs engine/runner-exit.test.cjs`: passed 32 tests.
2. Built `engine/job42-sheet.test.cjs` with 8 comprehensive automated tests:
   - Verifies all 16 roadmap modules, C# target files, and xUnit test files.
   - Verifies parity files, parity checks, and required engineering CI commands.
   - Verifies 20 specific error paths across Phase 4 modules.
   - Verifies 19 specific recovery paths.
   - Verifies 10 specific concurrency paths.
   - Verifies 22 specific edge case boundaries.
   - Verifies all 7 open design decisions.
   - Verifies mutation rejections (dropping required modules, checks, error/recovery/concurrency/edge paths, or decisions).
   - Verifies documentation integrity and absence of unauthorized C# implementations on `port/42-sheet`.
   - All 8 tests passed (`node --test engine/job42-sheet.test.cjs`).
3. Checked whitespace and formatting: `git diff --check` passed cleanly.
4. Updated documentation to **Sheet done** in `docs/NEXT-WORK.md`, `docs/BUILT-WITH-PLANWHARF.md`, and `.planwharf/plan.md`.
