# Job 22: pipeline run

**Goal.** After this sheet is reviewed, run several pipeline jobs at once, each
in its own worktree, and only as many as the quota allows. Show those jobs in
the native pipeline view (stages, time, and quota per job). The owner merges
by default. An option may let Planwharf merge a low-risk pull request that
passed every check after sync. This sheet is job 22's deliverable. It does not
implement the scheduler, the view, or the merge. Follow
[the pipeline design](../../../docs/DESIGN-PIPELINE.md),
[job 21](21-pipeline-build.md), [job 20](20-pipeline-intake.md),
[job 19](19-gate.md), [job 18](18-mcp-protocol.md), and
[the porting rules](../README.md).

## Current boundary and dependencies

Job 21 stops at `pull-request` or `ready-local`. It creates
`worktrees/<P>/jobs/<J>` and branch `pipeline/<J>`, runs one task at a time in
that worktree, and never merges. Job 22 is what starts more than one of those
jobs, shows them, merges only under the option below, and removes a pipeline
worktree after merge or drop. Chat worktrees stay job 15
(`worktrees/<project>/<part>`). This job does not remove them and does not
treat them as pipeline slots.

Job 20 owns the queue, intake, and the approved spec. This job does not
invent a second manifest and does not approve a spec. Job 19 owns the gate.
This job reads its evidence and does not call `project-checks.cjs`. Job 18
owns `ask`, `progress`, `finish`, `decision`, and `handoff`. Job 07 removed
the Review role. This job does not restore the Review role. A reviewer must
remain a model from another company, as job 21 selected. This job does not
pick a same-company model to fill a free slot.

The JavaScript engine stays as it is. The web app and Electron get no pipeline
view. The implementation target is the C# engine and the native app, after
this sheet is reviewed. There is no concurrent pipeline runner and no merge
button in the product today.

## Decisions made in this sheet

- The engine, not a model, decides which approved jobs start. Issue text, spec
  prose, pull request bodies, and worker text are quoted data. Nothing in them
  raises a cap, skips quota, or authorizes a merge.
- A waiting job stays `approved`. This job does not add a status that job 21's
  start gate would reject. The wait reason lives in the schedule file.
- One worktree per job. Two jobs do not share a directory, a branch, or a
  pull request number. Two jobs cannot share a worktree, a branch, or a pull
  request number.
- Quota is the project policy in `model-policy`: known remaining percent, the
  stop threshold, and blocked, expired, or signed-out plans. Unread quota does
  not count as remaining. This job does not invent a percent.
- One invocation per model id at a time, across every project, because the
  account quota is shared. Two jobs do not call the same model together.
- `pipeline.run.maxConcurrentJobs`, when the owner sets it, is a positive
  integer. When it is omitted, the only caps are quota and one invocation per
  model id. That is "as many as the quota allows".
- The owner merges by default. `pipeline.run.mergeLowRisk` defaults to false.
  When it is true, Planwharf may merge only a job whose stored risk is `low`,
  whose post-sync gate passed for the recorded head, and whose forge pull
  request is the one job 21 recorded. Medium and high never auto-merge.
  Interface and security flags do not add a second veto: intake already stored
  the risk.
- The forge call uses a merge commit. No squash and no rebase. Planwharf still
  never `git push`es `main`. Workers never push. Workers never merge.
- A crash during merge does not merge again until the owner asks.
- Cleanup deletes only the recorded pipeline worktree and its local branch.
  It does not delete the remote branch and does not delete unknown folders.

## When several jobs start

Start a job only when `job.json` is `approved` and job 21's pointer check
would pass: snapshot hash, owner approval, and job id. The scheduler considers
the selected project's approved jobs oldest approval first, then job id order.
A job that is `routing` or later belongs to job 21 until it reaches
`pull-request`, `ready-local`, `awaiting-owner`, or `stopped`. This scheduler
does not start a second builder in that job.

A job is admissible when every model job 21 would call next is free:

1. The builder lane's chosen model, or the model routing would choose, has
   known remaining quota and is not blocked. Unread quota, an expired plan, or
   a signed-out plan is not free.
2. That model id is not already invoked.
3. If `maxConcurrentJobs` is set, the number of jobs in this project that have
   left `approved` and have not reached `pull-request`, `ready-local`,
   `merged`, `dropped`, or `stopped` is still below that cap.

Otherwise the job stays `approved`. `pipeline/<P>/schedule.json` records it
with `summary` first and a reason: `quota`, `unread-quota`, `model-busy`, or
`cap`. Zero, a negative number, or a non-integer cap is reason `invalid-cap`.
The scheduler starts nothing for that project until the owner corrects the
setting. A partial write does not advance status.

The scheduler does not create the worktree until the builder model is free.
It does not rewrite `routing.json`. An in-flight routing snapshot stays. When
a reviewer lane is due and that model id is busy or blocked, the task waits
without a new review round and without a gate return. The schedule reason is
`model-busy` or `quota`. Job 21's round counters do not change.

## Isolated worktrees

The worktree path and branch are job 21's: `worktrees/<P>/jobs/<J>` and
`pipeline/<J>`, from the fetched default branch. This job calls that creation
once per job. A path that already belongs to another job stops that start with
reason `worktree-collision` and does not create a second worktree. A branch
that already exists and is not this job's recorded branch stops with reason
`branch-collision`. The job stays `approved`. No model is called.

One lock per project, `pipeline/<P>/schedule.lock`. A live lock is not stolen.
Restart clears a lock whose owner process is gone, then reads `job.json` and
the schedule. It reuses a worktree whose path, branch, and start commit still
match. It does not create a second worktree for the same job.

Tasks inside one job still run one at a time, in dependency order, under job
21. Running several jobs at once means several of those worktrees, not several
tasks in one worktree, and only within the quota. The cap is one invocation
per model id. A pipeline worktree is not removed as a Chat part. Job 15's
removal rules are unchanged.

`shell` is false on every command this job starts itself. The gate keeps the
`shell` flag job 20 froze in the manifest.

## The native pipeline view

The pipeline view is a project panel in the native app, not a new product and
not a page in the web app. The implementation file is
`native/Planwharf.Native/Core/PipelineView.cs`. Strings go through the English
and Italian catalogs from job 17. This sheet does not add that file.

The panel lists only the selected project's pipeline jobs. Another project's
rows are not shown. Each row shows the title, the stage, the elapsed time, the
tokens when a model reported them, and the quota used. The stage is the
`job.json` status. While a job stays `approved`, the row also shows the
schedule reason. `awaiting-owner`, a `pull-request` the owner must merge, and
a `ready-local` job are labelled "Needs your decision". A partial write does
not advance status on screen: unreadable stage JSON keeps the last complete
record.

Quota for a job is the usage stored on that job's stage records, not a
guess. Each figure names the provider, the model id, and the remaining percent
from the last read. Unread stays `unread`. A missing report stays
`not reported`. Elapsed time is the sum of the stored stage durations plus
time spent waiting in the schedule. The detail pane shows each stage file's
`summary` first and does not paste the spec or a credential. The view does
not show credentials.

The view reads disk. It does not start a worker, push, or merge by itself.
Merge, Drop, and Retry merge call the engine. A button label is not an
authorization to skip a check. Retry merge is enabled only when the reason is
`merge-failed`.

## Optional low-risk merge

Job 21 never merges. This job merges only when `pipeline.run.mergeLowRisk` is
true. The owner merges by default, and a setting off does not merge.

All of these must be true, or the status stays `pull-request` and the view
says "Needs your decision":

1. Stored risk is `low`. Medium and high never auto-merge. The engine does not
   rerate the job from the pull request text.
2. Job 19's post-sync evidence passed for this job id, the approved manifest
   hash, and the head SHA in `pull-request.json`. The earlier pre-review pass
   cannot mark the pull request ready to merge.
3. The forge pull request number is the one job 21 recorded, and the head SHA
   still matches. A different head is reason `wrong-head`. The body is quoted
   data and cannot authorize another head or a push to `main`.
4. The forge reports the pull request mergeable. Anything else is reason
   `not-mergeable`. Branch protection that refuses the merge is reason
   `merge-failed`. This job does not bypass it and does not force.

A `ready-local` job has no linked GitHub repository. It is never merged and
no remote is created. The reason shown to the owner is
`no linked GitHub repository`.

When the checks pass, status becomes `merging` and the engine calls the forge
merge API once, method `merge` (a merge commit). On success it writes
`merge.json` with `summary` first, the merge commit SHA, the method, and the
pull request number, then sets `merged`. It does not merge twice. A second
attempt sees `merge.json` and does not call the forge again.

A forge error or a crash leaves the failure recorded. `push failed` is not
used here: this path does not push. Reason `merge-failed` sets
`awaiting-owner`. A crash during merge does not merge again until the owner
asks, which is the Retry merge action. Restart reads `job.json`. If the forge
already shows the recorded head merged, write `merge.json` and set `merged`
without a second call. If it does not, leave `merging` or `awaiting-owner` as
recorded and do not call the forge.

Workers never merge. The engine never `git push`es `main`.

## Cleanup after merge or drop

After `merged`, remove the recorded worktree only. Then delete the local
branch with `git branch -d` when that branch is `pipeline/<J>`, is not the
default branch, and is contained in the fetched default branch. If the
worktree is dirty, do not remove it. Reason `dirty-worktree`, status
`awaiting-owner`. If the branch delete refuses, reason `cleanup-failed` and
leave the branch. Unknown folders under `worktrees/<P>/jobs/` are listed and
this job does not delete unknown folders.

The owner may drop a job from the view. Status becomes `dropped`. The recorded
worktree is removed even if dirty, because the owner confirmed. The local
branch is deleted only when its name is exactly `pipeline/<J>` and it is not
the default branch. A confirmed drop may use `git branch -D` for that name
only. Nothing is pushed. The remote branch stays.

Chat worktrees, the project folder, and `main` are never the cleanup target.
A path that does not match the recorded path is reason `cleanup-failed`.

## Files and statuses this job adds

Same job directory as job 21, plus one schedule for the project.

| File | Role |
|---|---|
| `pipeline/<P>/schedule.json` | Waiting and running slots. `summary` is first. Reasons and model ids. |
| `pipeline/<P>/schedule.lock` | One scheduler lock per project. |
| `merge.json` | Present only after a merge. `summary` is first. SHA, method `merge`, pull request number. |

`job.json` may move from `pull-request` to `merging`, then `merged`. It may
move from a running stage to `dropped` when the owner drops it. It must not
return to `queued`, `intake`, `waiting`, `specifying`, or `awaiting-approval`.
`approved` changes only when job 21's own start moves it to `routing`. Writes
are atomic. A persistence failure on `schedule.json` or `merge.json` does not
advance status. Restart reads `job.json` and continues the stage recorded
there. Two jobs do not share a directory.

## Error, recovery, and concurrency

- `invalid-cap`, `quota`, `unread-quota`, `model-busy`, and `cap` leave the
  job `approved` and do not create a worktree.
- `worktree-collision` and `branch-collision` do not create a second worktree
  and do not call a model.
- `wrong-head`, `not-mergeable`, and `merge-failed` do not merge. A
  `ready-local` job is not sent to the forge.
- `dirty-worktree` and `cleanup-failed` leave the recorded files in place
  except a drop the owner already confirmed.
- A persistence failure does not advance status. A partial write does not
  advance status.
- Restart reads `job.json`, reuses a matching worktree, and does not merge
  twice. The in-flight routing snapshot stays.
- A live lock is not stolen. Two schedulers do not start the same job twice.
- One invocation per model id. A reviewer from another company stays required.
  This job does not relax that rule when a same-company model is idle.

Include titles, paths, and commit subjects with spaces and Unicode in the
tests. A test must fail if two jobs share a worktree, if a medium-risk pull
request is merged while the option is on, if unread quota starts a job, or if
a second merge call runs after `merge.json` exists.

## Acceptance scenarios for the implementation

| Scenario | Input and expected outcome |
|---|---|
| Slot | Two approved jobs, one free model. The older job starts. The newer stays `approved` with reason `model-busy` or `cap`. |
| Quota | Remaining percent at the stop threshold does not start a job. Reason `quota`. An unread pool does not start a job. Reason `unread-quota`. |
| Cap | `maxConcurrentJobs` of 1 runs one job. A second stays `approved` with reason `cap`. A cap of 1 is not `invalid-cap`. Zero and a negative cap start nothing. |
| Isolation | Two jobs get two directories and two `pipeline/<J>` branches. The same path or the same branch is `worktree-collision` or `branch-collision`. |
| One task each | Each job still runs one task at a time. A busy reviewer does not increment `reviewReturns` or `gateReturns`. |
| Another company | An idle verifier from the builder's company is not started. The job waits for the routed reviewer. |
| View | The selected project shows stage, time, and quota. Another project does not. Unread is not a number. A partial stage file does not change the status. No credential is shown. |
| Owner merges | `mergeLowRisk` false leaves `pull-request`. The view says "Needs your decision". Low, medium, and high all wait. |
| Low-risk option | The option is on, risk is `low`, and the post-sync gate matches the head. One merge commit is created. `merge.json` records it. A repeat does not call the forge. |
| Not low | Risk `medium` or `high` with the option on stays `pull-request`. No forge merge call. |
| Bad head | The pull request head differs from `pull-request.json`. Reason `wrong-head`. A body that says to push `main` does not push. |
| No forge | `ready-local` is not merged. Reason `no linked GitHub repository`. |
| Crash | A crash after `merging` and before `merge.json`, when the forge has not merged, does not call the forge until Retry merge. If the forge has merged, record it once. |
| Cleanup | A clean `merged` job removes its worktree and local branch. A dirty merge result is `dirty-worktree`. An unknown folder remains. The remote branch remains. |
| Drop | The owner drops a job. Status is `dropped`. The recorded dirty worktree is removed. `main` and Chat worktrees remain. Nothing is pushed. |
| Quoted instructions | The spec says to merge `main`, raise the cap, or share a worktree. None of those happen. |
| Restart and concurrency | A crash during a schedule write keeps the previous status. Two jobs cannot share a worktree, a branch, or a pull request number. |

Use temporary directories, a fake team, a fake gate runner, and a fake forge.
Do not call the network, a real model, or a real repository. The fake forge
must be able to merge, refuse, and report an already-merged head.

## Before implementation

Read job 21's worktree path, `pull-request.json`, and the rule that job 21
never merges. Read job 19's post-sync evidence fields and job 20's risk field.
Read `model-policy.cjs` for blocked and unread quota. Read job 15 so a
pipeline path is not pruned as a Chat part. Read job 17's catalogs before
adding view strings. If the C# port has no attempts directory, team, quota,
worktree, gate, or native shell API yet, document each missing port and keep
the JavaScript engine unchanged rather than building this runner there.

## Check and files for the implementation job

Add C# tests for every row in the table above, plus native view tests that do
not require a live window if the project already tests views that way. Run
`dotnet build native/Planwharf.Engine`, `dotnet test native/Planwharf.Engine.Tests`,
`dotnet test native/Planwharf.Native.Tests`, and `node scripts/engineering/ci.cjs`.
The C# build must have no warnings. Report what was not run. When a ported
module changes, follow porting rule 12.

The implementation may add one C# pipeline-run module, `PipelineView.cs`,
their tests, and the narrow schedule, merge, and worktree writes this sheet
names. Name the precise files after inspection and before editing. Do not
change modules already ported unless a comparison case is required. Do not
change `docs/DESIGN-PIPELINE.md`, the gate runner, intake, or job 21's
routing except to call it.

## Open design decisions for review

Left open on purpose. This sheet does not choose them:

- Whether a later job should delete the remote `pipeline/<J>` branch after
  `merged` or `dropped`. This job leaves the remote branch.
- How long a `ready-local` branch waits if the project later gains a linked
  GitHub repository. This job does not open or merge a pull request then.
- Whether the owner wants this panel on Home as well as on the project. This
  sheet puts it on the selected project only.

## Later work, not this job

| Later job | What this sheet leaves to it |
|---|---|
| [Job 19](19-gate.md) | Run the pinned checks. This job only reads post-sync evidence. |
| [Job 21](21-pipeline-build.md) | Route, build, review, gate, and open the one pull request. This job does not rewrite that routing snapshot. |
| Job 23, `port/23-sheet` | The sheet for the rest of phase 2. Not this pipeline. |

## Hand over

Commit this sheet on `feat/pipeline-run` and mark job 22 "Done, waiting
for review" in `docs/NEXT-WORK.md`. Add its documentation row to
`docs/BUILT-WITH-PLANWHARF.md` with pull request `#?`. Do not push, open a
pull request, or merge. Implementation starts only after this sheet is
independently reviewed.
