# Job 14 fresh execution & verification record — attempt-131

## 1. Repository & Baseline Environment
- Branch: `fix/failing-tests`
- Baseline HEAD / `origin/main`: `3c9c492563195223c8d45d94307aec760ff33b57`
- Branch discrepancy resolution: Prompt requested `fix/failing-tests`; `.planwharf/plan.md` named `planwharf/run-job-14-afresh-in-the-project-folder`. Resolved in favor of `fix/failing-tests` matching `docs/NEXT-WORK.md` and repository branch.
- Environment: Windows 11, PowerShell, Node.js `v22.23.3`, .NET 10.0.100 SDK (net10.0), `core.autocrlf=true`.
- Preservation: `fix/failing-tests-old` worktree and attempt-130 baseline evidence untouched.

## 2. Fresh Baseline Reproduction
Commands run on clean baseline `3c9c492`:
1. `node --test` -> 384 passed, 1 failed (`engine/push-email.test.cjs:42:1`), exit code 1 (`baseline-node-test.log`)
2. `node scripts/engineering/ci.cjs` -> 1 failed (`engine/push-email.test.cjs:42:1`), exit code 1 (`baseline-engine-ci.log`)
3. `dotnet test native/Planwharf.Engine.Tests --nologo` -> 459 passed, 0 failed, exit code 0 (`baseline-native-engine.log`)
4. `dotnet test native/Planwharf.FakeCli.Tests --nologo` -> 64 passed, 0 failed, exit code 0 (`baseline-native-fakecli.log`)
5. `dotnet test native/Planwharf.Native.Tests --nologo` -> 140 passed, 0 failed, exit code 0 (`baseline-native-app.log`)

## 3. Evaluation of Candidate Fixes in Commit `02a4c80`
All 16 files modified or added in commit `02a4c80` were evaluated against the fresh baseline:
- `electron/import.test.cjs`: Fixture fallback path already merged to `origin/main` in commit `87df2f1`; 5/5 tests pass. Rejected.
- `electron/main.cjs`: Fixture fallback path already merged in `87df2f1`. Rejected.
- `engine/app-updates.test.cjs`: Passes cleanly in baseline. Rejected.
- `engine/app-version.test.cjs`: Passes cleanly in baseline. Rejected.
- `engine/atomic-rename.cjs`: `options.keepOnFailure` is unused across the codebase; atomic rename passes. Rejected.
- `engine/autonomy-crew.test.cjs`: Passes cleanly in baseline. Rejected.
- `engine/autonomy.test.cjs`: Passes cleanly in baseline. Rejected.
- `engine/commit-push.test.cjs`: Node-based delay in pre-push hook already merged in `87df2f1`; passes cleanly. Rejected.
- `engine/drop-work.test.cjs`: Passes cleanly in baseline. Rejected.
- `engine/monitor-shutdown.test.cjs`: Execution monitor shutdown safety already merged into `server.cjs` in `87df2f1`. Rejected.
- `engine/notebook-save.test.cjs`: Notebook save retry and copyFile fallback already merged into `server.cjs` in `87df2f1`. Rejected.
- `engine/repository.test.cjs`: Passes cleanly in baseline. Rejected.
- `engine/test-isolation.cjs`: Worker launch env stripping already handled inline where needed; all suites pass. Rejected.
- `engine/test-isolation.test.cjs`: Unneeded since helper is not used. Rejected.
- `engine/worker-containment.test.cjs`: Passes cleanly in baseline. Rejected.
- `server.cjs`: Plan write retries, copy fallback, and monitor shutdown guards already present in `87df2f1`. Rejected.

Conclusion: 0 fixes from `02a4c80` are justified because no failures corresponding to `02a4c80` reproduce on `origin/main`.

## 4. Single Reproduced Failure & Scope Gap
- Reproduced Failure: `engine/push-email.test.cjs:42:1` failed with `AssertionError: 'c\r\n' !== 'c\n'` at `assert.equal(fs.readFileSync(path.join(work, 'c.txt'), 'utf8'), 'c\n', 'the files are unchanged')`.
- Root Cause: Git global `core.autocrlf=true` converted LF line endings in `c.txt` to CRLF during rebase operations in disposable fixture repositories on Windows.
- Scope Gap: `push-email.test.cjs` was introduced on Nov 17 in commit `95478e5`, post-dating commit `02a4c80` (Nov 14). No hunk in `02a4c80` addresses this failure.

## 5. Implementation & Regression Coverage
- Staged and committed separately in `6a3cdf06e331365fe05b96f844a7436cc4f18e85`:
  - `git(work, ['config', 'core.autocrlf', 'false'])` in `fixture(t)` to isolate disposable fixture repos from host line-ending conversion.
  - Added CRLF preservation assertion for `windows.txt`.
  - Added regression test for dirty working tree preservation during refused email rewrite and subsequent retry.
  - Added regression test for pre-commit hook amend failure abortion, cleanup of rebase-merge state, and retry.
  - Added regression test for idempotence on already published commits (`rewritten: 0`).
- Documentation committed in `78295f9efb26f25c7c214a140a3d6aae942ecb03`:
  - Updated `docs/BUILT-WITH-PLANWHARF.md` with job 14 status (verification pending).

## 6. Final Engine CI Suite Execution & Evidence
All 5 suites run against revision `78295f9efb26f25c7c214a140a3d6aae942ecb03`:
1. `node --test` -> 388/388 passed, 0 failed, exit code 0 (`final-node-test.log`)
2. `node scripts/engineering/ci.cjs` -> 310/310 passed, 0 failed, exit code 0 (`final-engine-ci.log`, individual logs in `final-engine-ci-logs/`)
3. `dotnet test native/Planwharf.Engine.Tests --nologo` -> 459/459 passed, 0 failed, exit code 0 (`final-native-engine.log`)
4. `dotnet test native/Planwharf.FakeCli.Tests --nologo` -> 64/64 passed, 0 failed, exit code 0 (`final-native-fakecli.log`)
5. `dotnet test native/Planwharf.Native.Tests --nologo` -> 140/140 passed, 0 failed, exit code 0 (`final-native-app.log`)

## 7. Handoff for Independent Grok Reviewer (Subtask MT-5f8cfa21-959e-4819-9578-801771695765-ST-02)
To independently verify this slice:
1. Confirm branch and revision: `git rev-parse HEAD` should be `78295f9efb26f25c7c214a140a3d6aae942ecb03` on branch `fix/failing-tests`.
2. Review commit diffs:
   - `git show 6a3cdf0` (targeted test fixture isolation and email rewrite regression coverage)
   - `git show 78295f9` (job record update in `docs/BUILT-WITH-PLANWHARF.md`)
3. Run the complete engine test suites:
   - `node --test` (expect 388 pass, 0 fail)
   - `node scripts/engineering/ci.cjs` (expect 310 pass, 0 fail)
   - `dotnet test native/Planwharf.Engine.Tests --nologo` (expect 459 pass, 0 fail)
   - `dotnet test native/Planwharf.FakeCli.Tests --nologo` (expect 64 pass, 0 fail)
   - `dotnet test native/Planwharf.Native.Tests --nologo` (expect 140 pass, 0 fail)
4. Do not push or merge; preserve historical logs and records.
