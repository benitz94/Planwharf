# Pilot: execution blocked, comparison inconclusive

Historical pilot retained unchanged below. See [the subsequent repair and rerun](RERUN.md) for the initialization fix and remaining Windows sandbox blocker.

On 2026-10-20, one paired trial in each of three categories was attempted against upstream `d9b4c21b3c9045c6069d99b2f18686a6da53fe8d` (merged PR #21). All **six runs were blocked before implementation** by the installed native CLI. No full 30-run study was attempted because repeating the same environment failure would not estimate useful provider usage or accepted completion time.

The account was authenticated with ChatGPT, and its authenticated catalog contained the explicitly requested `gpt-5.6-luna`. The CLI version was `0.163.0-alpha.10.2`. No model resolved-version receipt or completed inference usage receipt was returned. Authentication/catalog success did not establish working inference access.

## Executive answers

1. **Faster? Inconclusive.** No accepted pairs exist. Elapsed failure times below are not completion-time comparisons.
2. **Fewer tokens? UNKNOWN.** Both arms lack successful token receipts. UNKNOWN is not zero.
3. **Fewer human interventions? UNKNOWN.** Planwharf required its normal initial approval, replayed by the harness once per run. Human prompt, investigation and recovery time were not measured.
4. **More reliable completion? Inconclusive.** Both arms were blocked in all three attempts; this tests environment availability, not engineering reliability.
5. **Better quality? Inconclusive.** All unchanged starters failed acceptance; no model-produced implementation existed to judge. Security, maintainability and test coverage quality are UNKNOWN.
6. **When worse? Not established.** Planwharf took longer to reach and classify the same failure in this setup, but these six observations cannot identify valid-task orchestration overhead or circumstances where it performs worse.
7. **Actual financial savings? UNKNOWN and not demonstrated.** No attributable billing/cost receipts exist. Subscription price and shared quota are not per-task expenditure.

## Every attempted run

All runs had a 60,000 ms implementation deadline and at most three CLI launches. Each actually launched once and exited with code 1. Wall time includes independent evaluation; category checks are 6, 8 and 10 respectively.

| Order | Task | Arm | Outcome | Elapsed ms | Passed checks | CLI launches | Initial approvals |
| --- | --- | --- | --- | ---: | --- | ---: | ---: |
| 1 | Small config | Direct | Blocked | 1532 | 0/6 | 1 | 0 |
| 2 | Small config | Planwharf | Blocked | 6492 | 0/6 | 1 | 1 |
| 3 | Medium REST | Planwharf | Blocked | 6688 | 0/8 | 1 | 1 |
| 4 | Medium REST | Direct | Blocked | 2087 | 0/8 | 1 | 0 |
| 5 | Complex scheduler | Direct | Blocked | 3927 | 0/10 | 1 | 0 |
| 6 | Complex scheduler | Planwharf | Blocked | 7567 | 0/10 | 1 | 1 |

Each category has 0/1 accepted runs per arm and one blocked run per arm. Median time to valid completion, measured tokens, attributable quota, actual cost and human interventions per successful task are UNKNOWN. There are zero comparable accepted pairs; no percent savings or confidence interval is reported. No injected implementation interruption was reached, so recovery rates are UNKNOWN. The deterministic application-crash acceptance machinery was validated separately against known-correct test oracles; that validation is not evidence of model recovery performance.

## Failure and evidence

The fatal installed-CLI error was `failed to initialize in-process app-server client: Access is denied. (os error 5)`. Additional warnings reported inaccessible CLI temporary aliases and state databases. A separate `--ignore-user-config` calibration probe failed with the same app-server initialization error. The existing CLI profile path was supplied explicitly and permissions were requested, but no sandbox bypass or authenticated profile copy was used. No visible windows were opened.

A portable official CLI diagnostic was considered, but its npm metadata fetch failed with native EACCES before an HTTP response. No archive or executable was obtained, so that version was **not evaluated** and no alternate CLI was silently substituted. Calibration receipts are retained separately from the six trial records.

The [manifest](evidence/pilot-2026-10-20/manifest.json) records hardware, OS, runtime, exact model, CLI version, limits, counterbalanced schedule, baseline and pre-execution grader/specification hashes. [All six raw measurements](evidence/pilot-2026-10-20/runs.json), [statistical summary](evidence/pilot-2026-10-20/analysis.json), [generated report](evidence/pilot-2026-10-20/REPORT.txt), and [sanitized native receipts](evidence/pilot-2026-10-20/receipts) are retained. Starting Git commits match within every pair. There were no completed inference receipts, so the six measured CLI launches must not be described as six completed model calls.

Acceptance was frozen at SHA-256 `d3f2d6fb9e31cc7f6f88843726f62d36ffa49b8df90f461ccdde80bbfb6dc359` before execution. Harness cleanup and resolved lead-failure receipt handling were hardened during/following this blocked pilot; production engine files and frozen acceptance were unchanged. The checked-in harness is the corrected rerun implementation, not a claim that every cleanup fix affected already-loaded pilot processes. These changes do not change observed outcomes or historical timing records.

## Independent checks and limitations

Separate review checked experimental fairness, usage completeness, outcome classification and bounded process handling. Findings corrected before delivery include native lead adapter naming, permissions parity, structured CLI failures, direct recovery launch limit, HTTP deadlines, host/worker cleanup, escaped Windows-path redaction and UNKNOWN accounting. Offline tests verify known-correct acceptance oracles, defective mutants, deterministic independent Git starters, real application-process crash/restart probes, counterbalancing and no-spend defaults. Reviewer identities/arm assignments are not supplied to acceptance processes.

This is a **blocked feasibility pilot**, not an accepted performance baseline. Neither pure inference nor provider waiting time is separable from the evidence. Shared profile/cache and inherited inference defaults are recorded rather than assumed identical invisible server state. Single-model Planwharf Start used its default Whole-job plan, without a separate AI planning/reviewer call. Optional conversational planning, full review workflows and unexpected orchestration crashes were not evaluated. Native Stop/checkpoint recovery and direct forced-termination/session-resume are different mechanisms; future results must preserve that limitation.

## Evidence-based next steps

First resolve native CLI initialization under the same intended permissions and repeat a one-pair pilot. Record the exact CLI/profile/environment change for both arms. Estimate time and complete all-role usage from accepted runs before expanding to five pairs per category. Human effort needs a separately timed user study; scripted approval/recovery actions cannot establish it.

No production speed optimization is justified by these blocked runs. After an accepted baseline, profile the default small-task workflow, optional planning/review calls, status polling and legitimate context reuse separately, then propose changes with new paired evidence. Keep a possible multi-model routing study separate; it has not been run and cannot explain this single-model dataset.
