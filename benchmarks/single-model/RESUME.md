# Continuation completed on the office machine

The ten remaining logical slots finished on 2026-10-21: nine accepted and one defective scheduler output. Together with the unchanged home checkpoint, all 30 logical slots now have outcomes (26 accepted, four defective). See the [final results and evidence limitations](evidence/office-completed-2026-10-21/RESULTS.md), [all 30 rows](evidence/office-completed-2026-10-21/combined-runs.json), [cohort mapping](evidence/office-completed-2026-10-21/cohorts.json) and [office native usage audit](evidence/office-completed-2026-10-21/usage-audit.json).

The pinned CLI and native capability check passed. All seven frozen source hashes matched before/after execution. The office audit validates 20 launches over ten trials, including interrupted response usage, with no missing reported receipts. The complete offline engineering suite passed with no failures or skips. Only hidden background processes were used. The old aborted attempt and its known consumption remain separate; pair 1 is excluded from paired resource analysis. Full home raw logs remain unavailable on this machine and were not newly uploaded. Keep PR #23 in draft; no merge or release was requested.

The following is the original pause checkpoint, retained for history. Its instructions to wait for the user were superseded by the explicit office continuation request.

---

# Resume checkpoint: full study paused by the operator

The user requested pausing on 2026-10-21 to leave and resume from the office. The owned benchmark process tree has stopped. **20 of 30 scheduled trials completed: 17 accepted, three defective REST outputs.** The first scheduler direct attempt was interrupted by this operator pause; its artifacts and reported usage were preserved separately. Nine trials had not started. This is not a completed 30-trial comparison.

## Saved state

- [Pause manifest](evidence/full-paused-2026-10-21/PAUSED.json), [20 completed trial rows](evidence/full-paused-2026-10-21/runs.json), analysis saved in the complete local checkpoint, [native usage audit](evidence/full-paused-2026-10-21/usage-audit.json). Full sanitized completed/partial receipts are saved in the local checkpoint commit; their bulk upload is deferred until resuming.
- [Protocol revision 2](PROTOCOL_V2.md) and [earlier grading/accounting corrections](CORRECTIONS.md). The published frozen source is `0a272e748fa0076bf1b696003fbfe9281c3b4960`, with the exact source tree and hashes in the checkpoint.
- On the original laptop, private state remains in the work folder `benchmark-full-private/study-01`, beside the repository. The private profile `benchmark-cli-private` stays outside the repository. No credentials or profile have been published.
- `benchmark-full-private/continuation-notes.txt` records the local workflow and prepared finalizer. `finalize-full.cjs` refuses publication as a complete study until 30 rows, a successful exit, unchanged frozen source and a valid accounting audit exist.

## Resume procedure

1. Read the checkpoint and preserve all 20 completed rows; do not rerun them or overwrite historical evidence silently.
2. Confirm the same exact CLI/model, authenticated account, frozen task/harness hashes and intended native task sandbox. Keep every test in the background.
3. Account explicitly for the user interruption of `scheduler-1-direct-b382c893`. Preserve its partial source/logs and numeric response receipts. This is separate from the predeclared implementation interruption. Exclude the operator break from any claim of uninterrupted completion time; do not silently subtract elapsed time or discard consumed usage.
4. If resuming on the same laptop, verify the saved native session and workspace before continuing. If restarting that interrupted trial is necessary, retain the old attempt as an operator-aborted observation and declare the continuation/replacement before running it. Do not hand-repair fresh benchmark outputs or supply private acceptance feedback.
5. If office hardware/account/profile differs, record the change and keep the resumed cohort distinguishable; do not silently pool incompatible timing samples. Unstarted task pairs must remain counterbalanced and sequential.
6. Continue the remaining work only when the user returns. After completion, independently audit native cumulative/unique-response allocation, run offline validation, sanitize the final evidence, and update the same draft PR #23. Do not merge or release.

All 36 benchmark checks passed before freezing the live experiment. Two additional independent metering-audit regressions pass. The completed 20-trial audit validates 30 CLI launches with no missing reported response receipts; provider-unreported consumption and actual cost remain UNKNOWN. Earlier pilot results remain separate.
