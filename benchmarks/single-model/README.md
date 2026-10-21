# Controlled single-model benchmark

Compare direct Codex CLI with the actual Planwharf lifecycle. [All 30 logical trial slots now have outcomes](evidence/office-completed-2026-10-21/RESULTS.md): 26 accepted and four defective. The original 20 home rows were preserved without rerunning them; ten scheduler slots completed on the office machine. Home and office resource comparisons remain separate, and the operator-replacement pair is excluded from paired resource analysis. Protocol revision 2 corrects REST grading and recovery usage accounting; historical pilots remain separate.

## Run without provider access

Node 24 and Git are required; no npm dependencies. From the repository root:

```sh
node --test benchmarks/single-model/*.test.cjs
node benchmarks/single-model/run.cjs
node benchmarks/single-model/run.cjs --offline-demo --output ../benchmark-offline-new
```

The default command prints help and starts no subprocesses. The offline demonstration creates disposable repositories and verifies that unimplemented starters fail acceptance. It makes **zero provider invocations** and is not a model-performance result. Normal CI runs only offline `.test.cjs` files; it never runs `--live`.

## Explicit live pilot

Use an existing authenticated Codex profile, an accessible native CLI and an exact model available to that account. No login credentials are copied. The optional existing `PLANWHARF_CODEX` executable override applies to both arms; record it in the manifest. The harness never opens browser, terminal or desktop windows.

```sh
node benchmarks/single-model/run.cjs --live --model gpt-5.6-luna --pairs 1 --timeout-ms 480000 --max-calls 6 --output ../benchmark-private-new
```

This first opts into one bounded capability call (at most 90 seconds), which must demonstrate native file editing, successful Node execution and an independently checked receipt. A successful model message alone does not pass. A blocked check saves its measured usage and diagnostic, submits zero task trials and stops the CLI with a nonzero status. Do not retry around an environment restriction. Calibration is separate from both arms and its consumption is retained in `preflight.json` and native receipts.

After a passed check, this opts into sequential provider use: six runs, each with an eight-minute implementation budget and at most six agent CLI launches. Cleanup and independent acceptance add bounded time after implementation; acceptance is at most ten seconds per check. Maximum permitted configuration is five pairs per category, fifteen minutes per implementation and twelve launches per run. Do not expand until the pilot provides useful accepted pairs and a realistic usage estimate. No automatic retries outside the predeclared recovery are performed by the harness. Native internal agent turns are not separately counted as CLI launches.

`--pairs 5` explicitly requests the target 30-run study; it is never the default or automatic. Output must be a new directory. Runs are sequential, use unique IDs and retain blocked, failed and timed-out outcomes. Keep the whole private directory outside the repository. Review sanitized output before publishing:

```sh
node benchmarks/single-model/export.cjs ../benchmark-private-new ../benchmark-sanitized-new
```

The exporter excludes workspaces, Git directories, symlinks, unsupported/big files, credentials and acceptance scratch databases. It preserves independent acceptance reports, parses JSONL before redaction, and redacts signed URL queries. Sanitization is best effort; manually review the output. Do not publish a profile, environment dump or private study directory.

## Protocol frozen before trial execution

`tasks.cjs` contains three common prompts and external deterministic acceptance checks: configuration utility (6 checks), persistent REST service (8) and recoverable dependency scheduler (10). `setupTask` creates identical starting bytes. Independent repositories use the same author, timestamp and starting commit per pair. No solutions or decomposition are supplied. The manifest records specification and grader hashes before launching either arm, and detects grader changes between runs. Acceptance stays outside workspaces and is not a hostile-code security sandbox.

Each pair runs A/B or B/A, alternating by category and repetition. Both use the same exact model, account, native executable, inherited inference defaults, workspace-write sandbox, never-ask approval policy and disabled tool-network access. Native CLI self-planning, editing and self-tests are allowed. Direct receives the full specification; Planwharf receives that same text as project direction, then its own normal worker wrapper. No competing workspace or source artifacts are reused. The profile and provider caches are shared, a limitation rather than guaranteed cache independence. The server has an isolated notebook and attempts directory; personal notebooks and projects are never dispatched.

The Planwharf host uses `server.cjs` directly, not the desktop launcher. It exposes only a loopback ephemeral port. Its roster contains one enabled Codex model for all AI roles; substitution is rejected at every instrumented launch and fallback is absent. Skills, roster refresh and automatic quota refresh are disabled for this controlled variant; native dispatch and persistence remain unchanged. Mandatory MegaTask approval is submitted and counted separately. In the baseline single-model Start path, Planwharf builds its normal deterministic Whole-job plan; no AI planning call is forced to inflate overhead. The fully specified task skips optional clarification (`propose:false`). Worker self-review and tests are requested; standalone review roles are only measured if the real lifecycle dispatches them. Do not generalize this variant to every conversational planning/review workflow.

For medium/complex implementation trials, interrupt once after the first source digest change, without advance model notice. Direct terminates its owned CLI and resumes its native session with a fixed continuation prompt; Planwharf uses Stop, checkpoint and Resume. These are **different recovery mechanisms**, so this measures interruption through each product workflow, not equivalent unexpected-process-crash resilience. Both get one scripted recovery opportunity within the same time/call limits. The private acceptance checks separately inject identical actual application-process crashes before/after REST commits and scheduler claims/completions. If implementation never starts, fault injection is not reached and recovery remains untested.

## Outcomes and measurement

Accepted requires successful transport and all independent checks. Completed transport with failed checks is defective; provider/environment blocks, process failures and deadlines are distinct. Independent grading receives no arm label and never feeds failed private tests back to either model. Tests of the grader include known-correct local oracles and deliberately broken implementations; those oracles are never supplied in live workspaces.

Wall time includes workflow setup, approvals, implementation, shutdown and independent grading, which is separately timed. CLI elapsed time and launch overhead are recorded where observable. Pure inference, provider waiting/cooldown, human follow-up/investigation/recovery time, coverage quality, maintainability and security review remain UNKNOWN without independent measurements. Scripted actions are not measured human effort. Documentation and self-test presence are a minimum acceptance gate, not a maintainability assessment.

Every actual lead and worker launch is recorded, including recovery and failed attempts. Direct JSON `turn.completed` receipts are summed; missing/failed receipts remain UNKNOWN. Planwharf worker receipts use the matching native session's latest cumulative token-count event; missing/failed-session receipts remain UNKNOWN. Lead receipts are included if invoked. Cached input and reasoning are subsets, never added twice to input+output totals. Provider-internal inference requests are UNKNOWN. Shared account quota snapshots cannot establish attributable per-task decrement. Subscription price is not per-task billed cost; no API-equivalent or cash estimate is invented.

Analysis retains every trial and compares time/tokens only for complete accepted pairs. It reports category success rates, measured-subset medians, min/max variability, unknown counts and paired differences. Deterministic paired bootstrap intervals require at least five accepted pairs with comparable nonzero measurements; otherwise results are inconclusive. Intervals are exploratory and conditional on successful pairs, with selection bias and multiple comparisons explicitly uncorrected.

## Baseline inspection

Authoritative upstream `main` was `d9b4c21b3c9045c6069d99b2f18686a6da53fe8d`, merge of PR #21 (head `468eae821494b5fd0e218acd65a8f2739777f628`). The baseline was inspected in `server.cjs`, `engine/orchestrator.cjs`, `engine/runner.cjs`, `engine/provider-runtime.cjs`, `engine/provider-adapters.cjs` and native execution/checkpoint persistence. Harness hooks observe the existing server and runner; no production engine behavior is optimized. The upstream offline CI discovers tracked test files automatically.

Official [Codex noninteractive documentation](https://learn.chatgpt.com/docs/non-interactive-mode) describes `exec --json` and completion usage events. Exact supported flags and event contents must be checked against the recorded CLI version; model catalog access alone does not prove execution succeeds.
