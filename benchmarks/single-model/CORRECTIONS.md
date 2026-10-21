# Corrections to the earlier pilot

The original pilot datasets remain unchanged as historical observations. Protocol revision 2 corrects two measurement defects before the full study; its results are a separate dataset.

## REST acceptance

The original `parallel-writes` probe compared the stored item array directly with a sorted HTTP list. The public contract required sorted GET responses but did not require disk-array ordering. Both pilot implementations retained all items, so this assertion was stricter than the task.

The direct CLI output's only recorded failing check was that assertion. Its original defective label must therefore not be used as evidence of a genuine REST implementation defect. The original raw row is preserved, with this qualification linked from the report.

The historical Planwharf REST output also had two genuine problems: it returned queued mutation promises without awaiting them inside the request handler's catch boundary, and delayed corrupt-store validation until a request. The offline regression fixture repairs those defects and passes all eight checks, including parallel durability and actual before/after-commit process crashes. The full study receives only the common public requirements, never this fixture or private acceptance feedback.

## Recovery token accounting

The original harness required successful process exit before accepting worker usage, and direct CLI turns terminated before `turn.completed` had no event receipt. Native `token_usage_record` entries already contained per-response provider usage for those interrupted processes.

Revision 2 reads those numeric response receipts regardless of exit status. It also deduplicates response IDs across direct CLI resume, where later session receipts include earlier usage. Separate Planwharf recovery sessions both contribute their receipts. Missing/conflicting or provider-unreported usage remains UNKNOWN. Cached input and reasoning stay subsets, never additive extras; no billing or subscription-savings estimate is inferred.

## Validation and scope

All 36 offline benchmark checks passed before freezing revision 2. This includes a regression that failed under the original disk-order assertion, the repaired historical REST fixture, interrupted-call receipt retention, cumulative resume deduplication and missing/conflicting receipt handling. Original evidence bytes are retained. Production Planwharf engine behavior is unchanged.
