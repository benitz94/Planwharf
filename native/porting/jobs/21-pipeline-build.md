# Job 21: pipeline build

> **Update (owner, 23 November 2026):** a reviewer from another company is a **preference**, not a rule. The routing tries another company first; with none free, any available model reviews, and a job never waits only for that.

**Goal.** After this sheet is reviewed, build the pipeline stages that follow an
approved spec: routing by risk and parts touched, one worktree and one branch
per job, build and review in pairs with rounds, calls into the job 19 gate, and
one pull request per job. This sheet is job 21's deliverable. It does not
implement those stages. Follow [the pipeline design](../../../docs/DESIGN-PIPELINE.md),
[job 20](20-pipeline-intake.md), [job 19](19-gate.md), [job 18](18-mcp-protocol.md),
and [the porting rules](../README.md).

## Current boundary and dependencies

Job 20 stops at `approved`. That status means the attempts-directory snapshot
is ready for this job to read: `job.json`, the frozen `acceptance.json` bytes,
their SHA-256, `approval.json` with `approvedBy: "owner"`, and `spec.md` with
its task tree. This job does not invent a second manifest and does not approve
a spec. Chat work with no pipeline job stays outside this path.

Job 19 owns the gate runner. This job calls that runner and reads its evidence.
It does not run `project-checks.cjs` as a substitute, and it does not implement
the runner. Job 18 owns `ask`, `progress`, `finish`, `decision`, and `handoff`.
Until that server exists, a builder or reviewer writes today's UTF-8 JSON
result file. A finish claim is not a gate pass and not a review accept. Job 07
removed the Review role. This job does not restore the Review role. Reviewers
are verifier-role models. Job 15's worktree rules are for parallel Chat parts.
This job asks for one pipeline worktree per job and does not schedule several
jobs at once. Job 22 owns that, the pipeline view, and any merge.

The JavaScript engine stays as it is. The implementation target is the C#
engine, after this sheet is reviewed. There is no routing table, no pipeline
builder, and no pull request opened by the engine today.

## Decisions made in this sheet

- The engine, not a model, picks the models from a routing table the owner can
  edit. The table lives in the project notebook as `pipeline.routing`, not in
  the target repository. Routing copies the rows it used into the job directory
  as `routing.json`. Later edits to the notebook do not change an in-flight job.
- Risk is `low`, `medium`, or `high`, already stored by job 20. `touchesInterface`
  and `touchesSecurity` are the only part flags. This job does not rerate them
  and does not infer them from spec text.
- `reviewer-code` runs for every task. `reviewer-interface` runs only when
  `touchesInterface` is true. `reviewer-security` runs only when
  `touchesSecurity` is true. Code always; interface and security when those
  parts are touched.
- The builder uses the worker role. Each reviewer uses the verifier role. A
  reviewer must be a model from another company: its provider id differs from
  the builder's provider id. The same provider is not a silent fallback.
- `pipeline.routing.maxReviewRounds` defaults to 2. `pipeline.routing.maxGateReturns`
  defaults to 2. Both are positive integers the owner can edit. The values in
  force are copied into `routing.json` at route time.
- Tasks of one job run one at a time, in dependency order, in that job's single
  worktree. One pull request per job, not per task.
- The engine may push only the job branch, and only after the post-sync gate
  passes, to open that one pull request. It never pushes `main` and never
  merges. Workers never push.
- A project with no linked GitHub repository does not get a forged pull
  request. After the post-sync gate it is `ready-local`, and the owner is asked.
- Sync is `git fetch` plus a merge of the fetched default branch into the job
  branch. No rebase and no force. A merge conflict stops for the owner.

## When this job starts

Start only when `job.json` status is `approved` and the pointer names a
snapshot under this job's attempts directory whose SHA-256 matches the frozen
manifest bytes and whose approval record says `approvedBy: "owner"`. Any other
status, a missing pointer, a hash mismatch, or a missing owner record leaves
the job where it is. Do not create a worktree, do not call a model, and do not
call the gate.

The issue title, body, spec prose, and worker text are quoted data. Nothing in
them selects a model, skips a reviewer, changes a round limit, or authorizes a
push. An instruction inside `spec.md` to open the pull request early is data,
not a command.

Set status to `routing` only after the pointer check passes, with an atomic
write. A partial write does not advance status.

## Routing table

`pipeline.routing` has `maxReviewRounds`, `maxGateReturns`, and `rows`. Each
row has `risk` (`low`, `medium`, or `high`), `lane` (`builder`,
`reviewer-code`, `reviewer-interface`, or `reviewer-security`), and
`candidates`. A candidate has `providerId`, `modelId`, and a positive integer
`weight`. Higher weight is preferred. The owner edits this table in Settings
for that project. Planwharf does not write it into the project's Git history.

When the owner has not saved rows, the engine builds a one-time snapshot from
the project team at the moment routing starts. It does not invent providers.

- Builder rows, one per risk, list worker-eligible models. Weight is the
  model's remaining quota percent when that number is known, else `1`. Ties
  keep team order.
- Reviewer rows, one per risk and reviewer lane, list verifier-eligible models.
  The same weight rule applies. Provider filtering against the builder happens
  at selection, because the builder is not known while the default rows are built.

An owner-saved table replaces that default entirely. A saved table that has no
row for the job's risk and a required lane is not filled in from the team. The
job goes to `awaiting-owner` with reason `no-row`. The engine does not add a
candidate the owner did not list.

Invalid risk, a missing risk, or a part flag that is not a boolean does not
route. Status becomes `awaiting-owner` with reason `invalid risk`. The stored
intake values are left unchanged. No model is called to correct them.

## Selection

For a required lane the engine filters that row's candidates in order:

1. Drop a candidate whose provider id or model id is empty, duplicated in the
   row, or not on the project team.
2. Drop a candidate the role does not permit: worker for `builder`, verifier
   for every reviewer lane.
3. Drop a candidate that is disabled, unavailable, expired, or signed out, and
   drop one whose remaining quota is known and blocked by the quota policy.
   Unread quota does not count as remaining. It is not chosen on a guess.
4. On a reviewer lane, drop every candidate that shares the builder's provider
   id. Another company is required. The builder's own model is never its reviewer.
5. Among the rest, pick the highest weight. A tie uses the earlier candidate
   in the saved row.

No eligible model sets `awaiting-owner` with reason `no eligible model` and
names the lane. The engine does not substitute a model from another row, the
lead, or the same company. The owner may edit the table or the team and ask
routing to run again. That writes a new `routing.json` only while status is
`awaiting-owner` or `routing` and no task has started. After the first builder
starts, the snapshot stays.

`routing.json` records the job id, risk, both part flags, the round limits,
every row used, the chosen provider and model id per required lane, and why
the others were dropped. `summary` is the first field. Optional lanes that the
flags turn off are recorded as `skipped`, not as a chosen model. The same
verifier model may fill two required reviewer lanes when it wins both and is
another company from the builder. The two verdicts stay separate records.

Time, tokens when the model reports them, and quota used are stored on the
stage record. The screen that shows them per job is job 22.

## One worktree and branch

After `routing.json` is written, create one worktree for the job under
Planwharf's data folder at `worktrees/<P>/jobs/<J>`, and one branch
`pipeline/<J>` from the fetched default branch on `origin`. `<J>` must match
`^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$` or the job stops at `awaiting-owner` with
reason `branch-name`. Do not invent a safer name that might collide.

The base is the approval record's `baseCommit` only when that commit is still
the tip of the fetched default branch. If `origin` has moved, the worktree
starts at the new tip and `job.json` records both the approved base and the
start commit. The manifest does not change. Job 19 decides whether a moved
base invalidates a later gate; this job still passes that base and the start
commit to the gate and does not edit the manifest.

The worktree is the only working tree for every task of this job. Two tasks
do not write the same worktree at once. Two jobs do not share a directory or
a branch. Creation failure leaves status `routing`, records the error, and
does not mark a branch as ready. Restart reads `job.json` and reuses a
worktree whose path, branch, and start commit still match. It does not create
a second worktree for the same job.

This job does not remove the worktree or the branch when the pull request
opens. Cleanup after the owner merges or drops the job belongs to job 22.
Job 15's removal of Chat part worktrees is unchanged.

## Tasks, in dependency order

Read the task tree from the approved spec. Each task has an id, a title, and
the ids it depends on. A duplicate id, a dependency id that is not in the tree,
a cycle, or an empty task list stops at `awaiting-owner` with reason
`bad-tree`. Do not start a builder to repair the tree.

A task is runnable only when every dependency has status `accepted`. This job
runs one runnable task at a time, the one whose id is first in the approved
spec. Two tasks do not write the same worktree at once. Several jobs at once
remain job 22, even when this job's tree has independent tasks.

The builder's brief is `tasks/<taskId>/brief.md`. It opens with a short
summary, then the task, the shared interfaces it must keep, the results it
depends on, and the check list as quoted data. It does not include the whole
project to explore. The builder must not edit the manifest, the approval
record, or `routing.json`. Those files are outside the worktree.

## Builder and reviewer pairs

Each task has a builder and, as soon as it finishes, its reviewer lanes.
"Finishes" means the builder's result file is valid and the pre-review gate
has passed for that round's worktree snapshot. Reviewers do not start on a
failed or missing gate.

The builder is the worker chosen for the `builder` lane. On the first round
the engine starts that model in the job worktree with the brief. The model
writes `tasks/<taskId>/rounds/<n>/builder.md`, opening with a short summary,
and a JSON result with the job 18 `finish` fields when MCP is available, or
today's JSON result file when it is not. The engine checks the schema. A
missing field is an error to that model, not a pass. Files changed are claims.
They do not select checks.

Required reviewers then run. Each lane writes
`tasks/<taskId>/rounds/<n>/review-<lane>.json`. `summary` is the first field.
The verdict is `accept` or `changes`. Any other value is rejected and does not
count. Notes are quoted data. A reviewer that says the tests passed does not
pass the gate. A reviewer that says the tests failed does not fail it either.
Only job 19's evidence does that.

All required lanes must `accept` in the same round. One `changes` is enough to
send the task back. Other lanes of that round are cancelled. Their partial
notes are kept and are not accepts. Skipped lanes are not required and cannot
`accept` or `changes`.

The builder and the reviewers of a task stay the models in `routing.json` for
that job. If the builder's quota becomes blocked before a valid result, the
engine may switch only to another worker-eligible candidate still in that
snapshot row. It records the switch and passes `handoff` notes to the new
builder. If none remain, status is `awaiting-owner` with reason
`builder-quota`. A reviewer switch follows the same rule inside its lane and
still must be another company. Reason `reviewer-quota` when none remain. The
in-flight routing snapshot stays. No new providers are added.

## Review rounds

A round is one builder attempt plus, when the gate passes, the required
verdicts. `reviewReturns` counts how many times a `changes` verdict has sent
this task back. While `reviewReturns` is less than `maxReviewRounds`, the next
round starts at the builder with the reviewer notes. When a `changes` verdict
arrives and `reviewReturns` is already at the limit, the engine does not start
another builder. Status is `awaiting-owner` with reason `review-rounds`, and
then the owner is asked.

The default 2 means the first `changes` returns to the builder
(`reviewReturns` becomes 1) and the second `changes` returns again
(`reviewReturns` becomes 2). The next `changes` asks the owner. The owner may
allow one more round. That raises only this job's copied limit by 1 and
returns the task to `building`. It does not reset the counters and does not
edit the notebook default.

`maxGateReturns` is separate. A pre-review failure does not increment
`reviewReturns`. Reviewers are not charged for a red build.

## Gate integration

Call the job 19 runner. Do not copy it and do not invent a second manifest.
Pass the job id, the approved snapshot id, the manifest SHA-256, the stage,
and the worktree identity. No model is invoked by the gate.

Before any reviewer starts, the stage is `pre-review`. A failing build goes
straight back to the builder with the log: the failing check ids and the
bounded engine log, not a model summary standing in for them. Each time that
log is handed back, increment `gateReturns`. While `gateReturns` was below
`maxGateReturns`, the builder continues. When it is already at the limit, the
failure sets `awaiting-owner` with reason `gate-returns` and does not start
another builder. The default 2 allows two hand-backs, then asks the owner on
the next failure.

Again after syncing with `main`, before the pull request is ready, the stage
is `post-sync`. The earlier pre-review pass cannot mark the pull request ready.
A branch cannot change the checks it is judged by. Candidate edits to
`package.json`, tests, a manifest copy, or a JSON or MCP result do not select
the checks. Only the Planwharf engine may start the gate. A candidate CI
workflow, webhook, or other branch process cannot trigger, substitute for, or
satisfy it.

Gate evidence missing, a pass for another job, another manifest hash, another
stage, or a stale worktree snapshot blocks. This job does not overwrite a
failed gate run. A retry asks job 19 for a new run record. If the C# runner is
not ported yet, stop at `awaiting-owner` with reason `gate-not-ported`. Do not
call `project-checks.cjs` and do not claim a pass. `shell` is false on every
command this job starts itself. The gate keeps the `shell` flag job 20 froze
in the manifest.

## One pull request per job

When every task is `accepted`, set `syncing` and merge the fetched default
branch into the job branch. No rebase and no force. A merge conflict sets
`awaiting-owner` with reason `merge conflict` and lists the conflicted paths.
The owner resolves them in the worktree. This job does not auto-resolve. After
a clean tree, run the post-sync gate on the merge commit.

If that gate passes and the project has a linked GitHub repository, set
`opening-pr` and push only the job branch. Never push `main`. Workers never
push. The push uses the project's existing Git credentials and must not print
tokens. `push failed` leaves `opening-pr`, records the error, and stores no
pull request number.

Then open one pull request for that branch. The body names the job id, the
spec revision, and the manifest SHA-256. When the source is a GitHub issue,
the body also contains `Closes #<number>` for that issue. A local-list job on
a linked repository has no `Closes` line. Write `pull-request.json` with
`summary` first, then the url, number, head branch, and head SHA. Set status
to `pull-request`. One pull request per job, not per task.

Restart reads `job.json`. If `pull-request.json` already has a number, it
does not open a second pull request. If the remote has a pull request for this
head that this job did not record, stop at `awaiting-owner` with reason
`unexpected-pr`. Do not close or replace it.

A project with no linked GitHub repository does not get a pull request. After
the post-sync gate the status is `ready-local`, `pull-request.json` records
null, and the reason is `no linked GitHub repository`. The owner is asked.
This job does not open one later if a repository is linked afterwards.

This job never merges. The owner merges by default. The option for Planwharf
to merge a low-risk pull request that passed every check after sync is job 22.

## Files and statuses this job adds

Same directory as job 20: `pipeline/<P>/jobs/<J>/`.

| File | Role |
|---|---|
| `routing.json` | Snapshot of the table, limits, flags, and chosen models. `summary` is first. |
| `tasks/<taskId>/brief.md` | Builder brief. Opens with a short summary. |
| `tasks/<taskId>/rounds/<n>/builder.md` | Builder record for that round. |
| `tasks/<taskId>/rounds/<n>/review-<lane>.json` | One verdict per required lane. |
| `pull-request.json` | The one pull request, or null when `ready-local`. |

Gate run files stay where job 19 stores them. This job keeps the returned run
id on the round or on the sync record. It does not rewrite job 19's evidence.

`job.json` may move from `approved` to `routing`, `building`, `gating`,
`reviewing`, `awaiting-owner`, `syncing`, `opening-pr`, `pull-request`,
`ready-local`, or `stopped`. It must not return to `queued`, `intake`,
`waiting`, `specifying`, or `awaiting-approval`. A task status is `pending`,
`building`, `gating`, `reviewing`, `accepted`, `awaiting-owner`, or `stopped`.
The owner stopping the job sets `stopped`, keeps the files, and does not push.

Writes are atomic. A partial write does not advance status. Two jobs do not
share a directory. Restart reads `job.json` and continues the stage recorded
there. Reported time, tokens, and quota are stored on the stage record.

## Error, recovery, and concurrency

- Invalid risk, a missing flag, a bad task tree, no row, or no eligible model
  stops before a worktree exists, except a worktree that was already recorded.
- Gate evidence missing, or evidence for the wrong snapshot, blocks reviewers
  and blocks the pull request.
- A persistence failure on `routing.json`, a verdict, or `pull-request.json`
  does not advance status.
- `push failed` and `merge conflict` are recorded reasons, not successes.
- Restart reads `job.json`, reuses a matching worktree, and does not open a
  second pull request. The in-flight routing snapshot stays after the first
  builder starts.
- A gate retry is a new job 19 run. This job does not overwrite the failure.
- One worktree per job. Two tasks do not write the same worktree at once. Two
  jobs do not share a directory or open each other's pull requests.

Include paths, review notes, and commit subjects with spaces and Unicode in
the tests. A test must fail if a same-company model is chosen as reviewer, if
a reviewer starts while the pre-review gate is red, or if a second pull
request is opened for the same job.

## Acceptance scenarios for the implementation

| Scenario | Input and expected outcome |
|---|---|
| Start gate | Status is not `approved`, or the manifest hash or owner record does not match. No worktree, no model call, no gate call. |
| Route by risk | Risk `low` uses the low rows. `medium` and `high` use their own. A higher weight wins. A tie keeps row order. |
| Parts touched | Both flags false: only `reviewer-code` is required. Interface true adds `reviewer-interface`. Security true adds `reviewer-security`. A false flag does not start that lane. |
| Another company | The strongest verifier shares the builder's provider. It is dropped. A verifier from another company is chosen. If none remain, reason `no eligible model`. |
| Invalid table | Invalid risk, a non-boolean flag, an empty candidate, or a saved table with no row for a required lane does not invent a model. Reason `invalid risk` or `no-row`. |
| Quota | A blocked or unread quota is not chosen. An expired or signed-out model is not chosen. A mid-build block switches only inside the snapshot, or asks the owner. |
| One task at a time | Two tasks with no dependency still do not run together. A task whose dependency is not `accepted` does not start. A cycle or unknown dependency is `bad-tree`. |
| Pairs | The builder writes round 1. Reviewers do not start. The pre-review gate passes. Each required lane writes `accept` or `changes`. A finish claim that says the tests passed does not release the reviewers by itself. |
| Rounds | The first `changes` starts round 2. With the default limit, the `changes` after two returns asks the owner and does not start round 4. The owner allowing one more round raises the copied limit by 1. |
| Gate returns | A red pre-review gate returns the log to the builder and does not increment `reviewReturns`. After `maxGateReturns` hand-backs, the next red gate asks the owner. |
| Post-sync | All tasks accepted. Fetch and merge. The post-sync gate runs on the merge commit. The pre-review pass does not mark the pull request ready. A conflict asks the owner and does not open a pull request. |
| One pull request | A linked repository gets one pull request whose body has the manifest SHA-256. A GitHub issue source also has `Closes #<number>`. Restart does not open a second. `push failed` stores no number. |
| No forge | No linked GitHub repository ends at `ready-local` after the post-sync gate. No remote is created. |
| Quoted instructions | The spec says to skip review, push `main`, or merge. None of those happen. |
| Tamper | The worktree contains another manifest and a green JSON result. The gate still receives the approved snapshot id. This job does not edit that snapshot. |
| Restart and concurrency | A crash during a write keeps the previous status. Two jobs cannot share a worktree, a branch, or a pull request number. |
| Stop | The owner stops the job. Status is `stopped`. Files remain. Nothing is pushed. |

Use temporary directories, a fake team, a fake gate runner, and a fake forge.
Do not call the network, a real model, or a real repository. The fake gate
must be able to pass, fail, and return evidence for the wrong job.

## Before implementation

Read job 20's `job.json` pointer fields, job 19's gate request and evidence
ids, and job 18's `finish` and `handoff` fields before naming new ones. Read
`model-roles.cjs` for worker and verifier eligibility, and `model-policy.cjs`
for quota blocking. Read job 15's worktree path rules so a pipeline worktree
is not removed as a Chat part. If the C# port has no attempts directory, team,
quota, worktree, or gate API yet, document each missing port and keep the
JavaScript engine unchanged rather than building this pipeline there.

## Check and files for the implementation job

Add C# tests for every row in the table above. Run `dotnet build
native/Planwharf.Engine`, `dotnet test native/Planwharf.Engine.Tests`, and
`node scripts/engineering/ci.cjs`. The C# build must have no warnings. Report
what was not run. When a ported module changes, follow porting rule 12.

The implementation may add one new C# pipeline-build module, its tests, and
the narrow notebook and attempts-directory writes this sheet names. Name the
precise files after inspection and before editing. Do not change modules
already ported unless a comparison case is required. Do not change
`docs/DESIGN-PIPELINE.md`, the gate runner, intake, or the job 22 merge.

## Open design decisions for review

Left open on purpose. This sheet does not choose them:

- Which isolation mechanism job 19 uses on each platform for candidate code,
  and which checks must pause for the owner.
- How a complete pre-review snapshot includes working files without private
  notebook data. Job 19 still owns that.
- Whether Planwharf later merges a low-risk pull request that passed every
  check after sync. Job 22 owns that option. This job never merges.
- How long a `ready-local` branch waits if the project later gains a linked
  GitHub repository. This job does not open a pull request retroactively.

## Later work, not this job

| Later job | What this sheet leaves to it |
|---|---|
| [Job 19](19-gate.md) | Run the pinned checks. Store gate evidence. Reject a candidate that changes the checks. Isolation and the pre-review snapshot stay open there. |
| Job 22, `feat/pipeline-run` | Several jobs at once in their worktrees, within quota. The pipeline view of stages, time, and quota. The owner merges by default. The optional low-risk merge. Cleanup of worktrees after merge or drop. |

Job 22 is not written yet. Its sheet will be `native/porting/jobs/22-pipeline-run.md`.

## Hand over

Commit this sheet on `feat/pipeline-build` and mark job 21 "Done, waiting
for review" in `docs/NEXT-WORK.md`. Add its documentation row to
`docs/BUILT-WITH-PLANWHARF.md` with pull request `#?`. Do not push, open a
pull request, or merge. Implementation starts only after this sheet is
independently reviewed.
