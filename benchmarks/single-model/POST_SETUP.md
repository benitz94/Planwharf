# Post-setup pilot: native access restored, six trials recorded

On 2026-10-21, the repaired Windows setup passed native editing and Node execution checks. The original `0.163.0-alpha.10.2` CLI and exact `gpt-5.6-luna` model then ran all six scheduled trials through the unchanged sealed tasks. This is a feasibility pilot, not a full 30-run study. All unfavorable, failed and timed-out outcomes remain in the dataset.

## Executive answers

1. **Faster? Not established generally.** 2 accepted pair(s) have comparable wall time. The descriptive median paired Planwharf-minus-direct difference is 98.6%. Fewer than five accepted pairs provide no supported bootstrap interval.
2. **Fewer tokens? Not established generally.** 1 accepted pair(s) expose comparable complete usage. The descriptive median paired difference is -8.1%. Interrupted attempts without complete receipts remain UNKNOWN. Cached input is a subset, and lower total tokens alone does not establish lower uncached input or billed cost.
3. **Less human effort? UNKNOWN.** Approval and recovery were scripted; actual prompting, investigation, follow-up and recovery time were not independently timed.
4. **More reliable? Inconclusive.** Direct accepted 2/3 and Planwharf accepted 2/3. One trial per task and different implementation-interruption mechanisms cannot establish general reliability.
5. **Better quality? Inconclusive.** Acceptance below is independent, deterministic behavior testing, including real application crash/restart checks. Passing the same checks is the comparability gate; maintainability, security review and test coverage quality remain UNKNOWN.
6. **When worse?** The per-task table exposes longer accepted completion times, defects and timeouts. These observations apply to this single-model Whole-job workflow and local environment; attribution to a specific planning, polling or context cost would require separate profiling.
7. **Actual financial savings? UNKNOWN.** No attributable billing receipts exist. Shared quota, subscription price and cached-token counts are not per-task cash measurements.

## Every trial

| Task | Arm | Outcome | Checks | Wall seconds | Launches | Input | Cached input | Output | Reasoning |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| config | direct | accepted | 6/6 | 57.7 | 1 | 90,593 | 79,232 | 3,173 | 310 |
| config | planwharf | accepted | 6/6 | 167.5 | 1 | 82,768 | 47,744 | 3,382 | 354 |
| rest-api | planwharf | defective | 5/8 | 218.4 | 2 | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| rest-api | direct | defective | 7/8 | 102.6 | 2 | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| scheduler | direct | accepted | 10/10 | 123.4 | 2 | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| scheduler | planwharf | accepted | 10/10 | 131.8 | 2 | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |

Wall includes workflow, cleanup and independent grading; evaluation time is separately retained in `runs.json`. UNKNOWN is not zero. Token totals include all measured lead/worker/recovery launches; a missing interrupted receipt makes that metric incomplete. Calibration is separate from arm totals. For the small pair, uncached input was 11,361 tokens for direct and 35,024 for Planwharf; its lower total-token count does not establish lower provider cost.

## Category aggregates

| Category | Direct accepted | Planwharf accepted | Comparable accepted pairs | Median wall difference, Planwharf minus direct |
| --- | --- | --- | ---: | ---: |
| small | 1/1 | 1/1 | 1 | 190.4% |
| medium | 0/1 | 0/1 | 0 | UNKNOWN |
| complex | 1/1 | 1/1 | 1 | 6.8% |

Independent failed checks:

- rest-api / planwharf: crud, parallel-writes, corrupt-store.
- rest-api / direct: parallel-writes.

Full per-category distributions, unknown counts, paired differences and interval status are in [analysis.json](evidence/completed-pilot-2026-10-21/analysis.json). The [generated report](evidence/completed-pilot-2026-10-21/REPORT.txt) retains all seven comparison dimensions. No confidence or universal savings claim follows from this small sample.

## Repair, controls and prior interruptions

The operator completed Windows sandbox setup in a separate private runtime profile and signed in. The desktop agent sandbox itself still reported an inaccessible sandbox helper directory; an approved background process under the Windows operator could use the intended native elevated task sandbox. No sandbox bypass, global ACL change, weakened task networking, production engine change or visible test window was used. The operator-created private login remains outside the repository; no credentials or profile are exported. An initial post-setup network timeout is retained as calibration, not a task outcome.

The earlier [overnight interrupted attempt](evidence/interrupted-2026-10-21/operator-context.json) contains two accepted small-task rows and one REST timeout with an overnight gap. Its raw elapsed time is preserved, not subtracted or pooled into this pilot. A [second partial attempt](evidence/partial-2026-10-21/operator-context.json) contains one direct row and incomplete Planwharf artifacts. Detached runner startup attempts did not survive the host tool lifetime; saved PowerShell execution policy was left unchanged. The completed pilot used a managed background session with a temporary sleep request. These operator/environment interruptions are distinct from the predeclared implementation interruptions.

Both arms used the same CLI, profile, exact model, task text, deterministic starting commits, limits and native task sandbox. Account/profile caches are shared and provider latency varies. Medium/complex direct forced termination plus native resume and Planwharf Stop/checkpoint/Resume are different implementation recovery mechanisms; application-process crash checks are identical. Failed private tests were never fed back to the agents. The sealed grader hash remains `d3f2d6fb9e31cc7f6f88843726f62d36ffa49b8df90f461ccdde80bbfb6dc359`.

## Evidence and validation

- [Environment, protocol and schedule](evidence/completed-pilot-2026-10-21/manifest.json), [capability receipt](evidence/completed-pilot-2026-10-21/preflight.json), [all trial records](evidence/completed-pilot-2026-10-21/runs.json), and [sanitized commands, timing and acceptance receipts](evidence/completed-pilot-2026-10-21/receipts).
- [All 31 offline checks passed](evidence/completed-pilot-2026-10-21/validation.txt), including correct oracles, defective mutants, real application crashes, account completeness, gate checks, background cleanup and signed-link redaction. [Full repository CI passed all 331 checks](evidence/completed-pilot-2026-10-21/full-ci.txt); no production engine source changed.
- Reproduce using [README.md](README.md); default/offline modes spend no provider access, and live work remains explicitly opt-in, bounded and sequential.

## Expansion and product recommendations

The target 30-run comparison was not run: both medium-task outputs failed acceptance and complete token receipts for interrupted runs are unavailable in this protocol. Repeating the same incomplete accounting would not establish the requested all-class resource comparison. This pilot must first support reliable completion and complete usage accounting for the recovery classes; missing interrupted receipts and any failed acceptance cannot be repaired by treating UNKNOWN as zero or pooling partial studies. A larger, separately declared run needs a practical time/quota budget and sufficient accepted comparable pairs. This pilot does not establish a universal advantage.

Publication tooling now parses JSONL before redaction and excludes intentionally corrupt acceptance scratch stores while preserving independent reports. The CI helper scopes Git trust to its own checkout under its isolated test home and hides child windows; it changes no global settings. Raw receipt paths are explicitly whitespace-preserving in `.gitattributes`, while code and report patch checks remain enabled. These publication and validation repairs occurred after the pilot and do not alter task results.

Freeze these baseline records before optimization. Profile the measured small-task wrapper/context, worker completion processing and interruption recovery separately. This normal Whole-job path dispatched no gratuitous planning or review calls, so removing hypothetical calls is not an evidence-based fix. Improve complete interrupted-call usage collection and durable benchmark continuation in a separate tooling phase, preserving this dataset. Keep any multi-model routing experiment separate; it has not been run.
