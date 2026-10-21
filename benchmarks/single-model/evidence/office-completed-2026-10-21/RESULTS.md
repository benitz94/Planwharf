# Completed trial slots, with separate machine cohorts

All 30 logical slots now have an outcome: the original 20 home rows were preserved byte-for-byte in their checkpoint and were not rerun; ten scheduler slots ran on the office machine. This is not a single-machine 30-trial timing comparison.

| Cohort / task | Direct accepted | Planwharf accepted |
| --- | ---: | ---: |
| Home / config | 5/5 | 5/5 |
| Home / rest-api | 4/5 | 3/5 |
| Office / scheduler | 4/5 | 5/5 |

Every defective, blocked, failed or timed-out result remains in runs.json; acceptance requires completed transport and all sealed checks. Generated implementations were not hand-repaired, and private grader feedback was not supplied to the model.

## Environment and continuity

Office: Intel(R) Core(TM) Ultra 9 275HX; 24 logical CPUs; v24.18.0; Windows 10.0.26200. The native CLI 0.163.0-alpha.10.2, requested model gpt-5.6-luna, source tree and all seven frozen raw source hashes were verified. A separate capability call demonstrated native editing and Node execution before any trial. Its usage is calibration, outside trial totals.

Home used a Xeon E3-1505M v5, eight logical CPUs, Node v24.19.0 and Windows 10.0.19045. Office uses an isolated authenticated profile with notification hooks disabled. Profile/hardware differ; exact account continuity and immutable provider-resolved model snapshot are UNKNOWN. Both office arms use the same office profile and executable. No overall time/token savings or monetary claim is made.

## Operator-aborted observation and replacement

The original scheduler-1-direct-b382c893 remains operator-aborted, with reported input 43,641, output 5,033, cached input 38,528 and reasoning 1,706 tokens. Additional in-flight consumption is UNKNOWN. Its workspace/native session was unavailable on this machine. scheduler-1/direct is a declared fresh replacement, not a resumed native session. Both arms of scheduler-1 are excluded from comparative resource analysis; their outcomes, elapsed time and measured usage remain published.

The office report analyzes only scheduler pairs 2..5. At most four comparable pairs remain, below the five-pair bootstrap threshold. Home results are analyzed by task separately. Costs, attributable quota decrement and measured human effort remain UNKNOWN. Cached input and reasoning are subsets, never added again to input + output.

## Evidence coverage

The independent office native audit checked 10 trials, 20 measured launches and 0 launches with unavailable receipts. Unique response allocation and cumulative native totals were checked, including interrupted responses. Unreported consumption remains UNKNOWN.

Office sanitized call logs, native numeric receipts and acceptance reports are published here; workspaces and the authenticated profile are excluded. The existing home numeric audit is carried forward unchanged in the original checkpoint. Full home raw logs and the old aborted workspace were not available here and have not been independently reaudited or newly uploaded. Earlier pilots remain separate.

Execution used hidden child processes and the background server, with visible-window callbacks disabled. No desktop launcher or browser was opened. Production engine behavior and the frozen trial functions were unchanged. The continuation loader only exposes the frozen lexical trial functions in memory; the original files remain intact.
