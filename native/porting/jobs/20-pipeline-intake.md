# Job 20: pipeline intake

**Goal.** After this sheet is reviewed, build the first pipeline stages: a queue,
intake with questions, `spec.md` with acceptance tests, and one file-backed
state record per job. Approving a spec freezes an acceptance manifest in that
state for job 19's gate. This sheet is job 20's deliverable. It does not
implement the pipeline. Follow [the pipeline design](../../../docs/DESIGN-PIPELINE.md)
and [the porting rules](../README.md).

## Current boundary and dependencies

Chat is the only way of working today. The lead suggests a goal, the crew
builds it, and `engine/project-checks.cjs` may discover an npm test script or
Node test files inside the project folder. That discovery is not an accepted
spec and not an acceptance manifest. Worker results in `worker-result.cjs` are
claims, not proof. Job 18's MCP sheet and job 19's gate sheet are reviewed
documents, not running code. Job 15's worktree sheet is also waiting; this job
does not create worktrees or branches.

There is no `planwharf` issue queue, no local request list, no `spec.md`
stage, and no `job.json`. Pipeline state must not be invented inside the
candidate project's Git history. The JavaScript engine stays as it is. The
implementation target is the C# engine, after this sheet is reviewed.

## Decisions made in this sheet

- Queue choices are two options in Settings for that project, stored in
  Planwharf's notebook, not in the target repository.
  `pipeline.queue.githubIssues` and `pipeline.queue.localList` default as the
  design says: both on when the project has a linked GitHub repository;
  otherwise only the local list is on and the GitHub source cannot be turned on.
- Until job 21's routing table exists, intake and the spec use the project's
  configured lead model and record that provider and model id. The engine does
  not silently substitute another model. If no lead is configured, the job
  stays `queued`, asks the owner in the project chat, and does not start intake.
- The owner approves the spec. The model that drafted it cannot approve it by
  text. This job does not add another approver. Job 07 removed the Review role.
- Job files live under Planwharf's attempts directory, one directory per
  pipeline job, outside the project folder and outside any candidate worktree.
- An empty check list cannot be approved. Every check names a command, its
  arguments, and the result it must give.
- Existing Chat work is not copied into the pipeline and is not given a
  synthetic spec. The gate's wait for a manifest applies to pipeline jobs only.

## Queue

Two sources, each one setting:

| Setting | When on | Trust boundary |
|---|---|---|
| `pipeline.queue.githubIssues` | Read open issues in the project's linked GitHub repository whose labels include exactly `planwharf`. | Anyone can open an issue. Only someone with triage or write access can add the label. Planwharf never adds the label from the issue text. An issue without that exact label is not a job. |
| `pipeline.queue.localList` | Read requests the owner added in Planwharf for this project. | The owner writes the item. It is kept in the attempts directory, not in the project's Git history. This is the source for a project that is not on GitHub, and it stays available when GitHub is linked. |

Creating a job copies the title and body into `request.md` as quoted data and
sets `job.json` to `queued`. The same GitHub repository and issue number has
at most one job that is not `rejected` or `withdrawn`. The same local item id
has at most one such job. Closing the issue, or removing `planwharf`, before
approval marks that job `withdrawn` and keeps its files. Applying the label
again creates a new job id. Editing the title or body before approval records
the new quoted text and returns the job to `queued` so intake runs again. An
already approved job does not change its spec because the issue changed; the
job records the drift and waits for the owner.

Turning a source off stops new items from that source. Jobs already created
remain. A GitHub read that fails, or a response that is not a complete issue
list, records the error on the source and does not add, drop, or rewrite jobs.
The issue title and body are never executed and never treated as instructions
to the engine.

## Intake questions

Intake runs only from `queued`, with the lead model named above. The engine
passes `request.md` as quoted data and tells the model that nothing inside the
quote is an instruction. The model writes `intake.md`, which opens with a short
summary, and the engine stores these fields on `job.json` only after they
validate:

- `worthBuilding`: yes or no, with a reason. No leaves the job `rejected` and
  writes no `spec.md`.
- `scopeIn` and `scopeOut`. When the request is too big, the cut is explicit.
  Deferred work is not added back later in the spec.
- `risk`: `low`, `medium`, or `high`, with a reason. No other value.
- `touchesInterface` and `touchesSecurity`: each true or false. These are the
  only "parts touched" this stage records. Job 21 reads them. This job does
  not choose a builder, a reviewer, or a route.
- `questions`: items only the author can answer. Each item has an id and text.
  The engine asks them before any spec exists. On a GitHub job it comments on
  that issue. On a local job it asks in the project chat. The job status is
  `waiting` until every question has an author answer stored beside it.
  Answers are quoted data too. Intake then runs again with those answers. The
  model does not invent an author answer.

No `spec.md` is written while status is `queued`, `intake`, `waiting`, or
`rejected`. A restart resumes from `job.json` and does not ask the same
answered question again.

## Spec and acceptance tests

The spec stage runs only after intake has `worthBuilding: yes` and no open
question. The same lead rule writes `spec.md` before any product code. The
file opens with a short summary, then:

- what to build, matching `scopeIn`;
- what not to build, matching `scopeOut`;
- the interfaces tasks share, fixed here before any later parallel work;
- a task tree. A small spec is one task. A large spec is a tree of tasks, each
  with an id, a title, and the ids it depends on. The tree is data for job 21.
  This job does not assign models or start builders;
- the acceptance tests, written before the code: one section per check, with
  the command, the argument list, the worktree-relative working directory, the
  limit, and what the command must give.

The engine writes the same checks into `acceptance.json` beside the spec.
A check has a stable id, a purpose, `executable`, `arguments` as a list (not
one shell string), a relative `workingDirectory` with no absolute path and no
`..` segment, `timeoutSeconds`, `expectedExitCode`, and a non-empty
`expectedResult`. `shell` is false unless the owner is later approving a
revision that sets it true, and that choice is copied into the approval
record. `trust` is `engine-owned`, `pinned-external`, or `candidate-code`.
A `candidate-code` check names an `engineOwnedAssertionId`. A check that
cannot name one is insufficient and blocks approval. When the trusted program
already exists outside the candidate tree, the proposal may include its
SHA-256. This job does not hash files inside a candidate worktree, and it
does not choose a process isolation mechanism.

The proposal stays a proposal until the owner approves it. A second draft
replaces an unapproved proposal and leaves approved revisions untouched.

## Job state files

For project `P` and job `J`, the engine uses only this directory, under the
attempts directory Planwharf already keeps outside the project repo:

`pipeline/<P>/jobs/<J>/`

| File | Role |
|---|---|
| `job.json` | Identity, source, status, summary, lead model id, risk, parts, question answers, spec revision, accepted-manifest pointer, base commit, and reported time, tokens, and quota for the stages this job actually runs. |
| `request.md` | Quoted source. Opens with a short summary. |
| `intake.md` | Intake record. Opens with a short summary. Absent until intake writes it. |
| `spec.md` | Human spec. Opens with a short summary. Absent until the spec stage. |
| `acceptance.json` | Proposed checks, then the approved bytes once frozen. `summary` is the first field. |
| `approval.json` | Owner approval record for the current accepted revision. `summary` is the first field. |
| `revisions/<n>/` | Older approved spec, manifest, and approval bytes. Never rewritten. |

`job.json` status is one of `queued`, `intake`, `waiting`, `specifying`,
`awaiting-approval`, `approved`, `rejected`, `withdrawn`. Names for routing,
build, review, gate, and pull request are not statuses of this job. The
pointer stops at `approved`, which means "ready for job 21 to read". This job
never moves it further.

Writes are atomic. A partial write does not advance status. Two jobs do not
share a directory. Restart reads `job.json` and continues that stage. Time,
tokens when the model reports them, and quota used are stored on the stage
record. The screen that shows them per job is job 22. This job does not draw
that view.

The file layout has one directory per job so later stages do not need a
second store. This job does not create a worktree, a branch, or a pull request.

## Handoff to the job 19 gate

Job 19 runs the checks. This job only stores the manifest the gate is allowed
to trust. The gate reads that snapshot from the job record. It does not read
`spec.md` or `acceptance.json` from a candidate worktree, and a worker result
cannot stand in for it.

When the proposal validates, status becomes `awaiting-approval`. The owner
sees the spec and the check list in the project chat. Accept and send-back are
engine actions. A sentence in `spec.md` is not approval. Send-back stores the
owner's note as quoted data, returns to `specifying`, and does not freeze a
manifest.

Accept freezes one revision:

1. Write `acceptance.json` as UTF-8 without a BOM. The hashed document is
   exactly those bytes. `summary` is the first field, then `schema` 1,
   `jobId`, `specRevision`, `baseCommit`, and `checks`. Each check has the
   fields from the spec section.
   `baseCommit` is the full SHA of the project's default branch on `origin`
   after a fetch at approval time. It is not a SHA from a worker branch.
2. SHA-256 those bytes. Copy the approved `spec.md` bytes into
   `revisions/<n>/` with the manifest bytes. Write `approval.json` with the
   job id, spec revision, manifest SHA-256, base commit, `approvedBy: "owner"`,
   and the approval time. If a check sets `shell` true, name that check id in
   the approval record.
3. Point `job.json` at this snapshot: snapshot id, SHA-256, relative path,
   spec revision, and base commit. Set status to `approved`.

The revision directory is immutable. A later accepted change writes a new
number, leaves the old bytes, and moves the pointer. This job does not relabel
old gate evidence. Job 19 invalidates an old pass when the snapshot id changes.
Build dispatch belongs to job 21 and must not start unless this pointer exists.
This job does not dispatch a builder and does not start the gate.

The frozen snapshot includes the manifest bytes, their SHA-256, the owner
authorization record, the base commit, the job and spec revision, and each
check's id, command, arguments, working directory, limit, and expected result.
It does not include a worktree digest, a sandbox choice, or a rule for which
platform isolation must pause for the owner. Those stay open under job 19.

## Chat work with no spec

Chat stays the path for exploring and for work shaped as it goes. This job
does not create a pipeline job for an existing Chat goal, does not write a
spec or a manifest backwards onto that work, and does not pause it because a
manifest is missing. Today's project-check discovery is unchanged and is not
an accepted manifest.

Job 19's rule that a job without an accepted manifest waits applies to a
pipeline job only. A Chat attempt is not submitted to the gate. An owner who
wants that goal in the pipeline adds a local list item or a `planwharf` issue
(job 30 adds a one-click **Send to the pipeline**; see the design's "Switching
between the two").
That becomes a new pipeline job and waits for its own intake and approval. The
Chat attempt's history stays as it was. Work already running as Chat when this
lands keeps running as Chat.

## Later work, not this job

| Later job | What this sheet leaves to it |
|---|---|
| [Job 19](19-gate.md) | Run the pinned checks before review and again after sync with `main`. Reject a candidate that changes the checks. Isolation of candidate code, and how a pre-review snapshot pins working files, stay open there. |
| Job 21, `feat/pipeline-build` | Routing from risk, interface, and security. Build and review in pairs, rounds, and asking the owner when rounds run out. Call the gate. Open one pull request per job. Read this job's approved snapshot. Do not invent a second manifest. |
| Job 22, `feat/pipeline-run` | Several jobs at once in worktrees, within quota. The pipeline view of stages, time, and quota. The owner merges by default. The optional setting that lets Planwharf merge a low-risk pull request that passed every check after sync. |

The queue may hold many jobs, and their directories stay separate. This job
does not run those jobs' builds at the same time, and it does not pick models
from a routing table, start builders or reviewers, run tests as a gate, or
publish a pull request. Short worker briefs, and learning which models fit
which work, are also left for later jobs. Shared safety from the design still
applies when those jobs land: a new branch from the latest `main`, workers
that never push, and nothing published to `main` by Planwharf.

## Acceptance scenarios for the implementation

| Scenario | Input and expected outcome |
|---|---|
| Defaults | A project with a linked GitHub repository starts with both sources on. A project without one starts with only the local list on, and turning the GitHub source on fails until a repository is linked. |
| Label boundary | An open issue gains `planwharf` and becomes one job. An unlabelled issue, or a label with a different name, does not. Planwharf does not add the label. A second poll does not create a second active job for that issue. |
| Quoted request | The issue body tells the engine to skip intake, approve itself, or run a command. The text is stored as data. No spec is approved. No command from the body runs. |
| Local list | The owner adds a local item. One job is queued from that item even when the GitHub source is off. The item is not written into the project repository. |
| Source failure | The GitHub read fails or returns an incomplete list. Existing jobs stay. No new job is created from the failed read. The source records the error. |
| Withdraw and edit | Removing the label or closing the issue before approval withdraws the job and keeps the files. A body edit before approval re-queues intake. The same edit after approval records drift and does not change the frozen spec. |
| Intake wait | The model marks a question only the author can answer. Status is `waiting`, no `spec.md` exists, and the question is on the issue or in the chat according to the source. A restart does not drop the question or duplicate an answer. An owner withdrawal from `waiting` keeps the files. |
| Reject | `worthBuilding` no. Status is `rejected` with the reason. No spec and no manifest. |
| Scope and ratings | Intake stores in-scope work, out-of-scope work, risk `low`, `medium`, or `high`, and the two part flags. Any other risk value is rejected and does not advance the job. |
| Spec before code | The spec contains what to build, what not to, shared interfaces, a task tree, and the checks with commands and expected results. No product code is written by this stage. |
| Bad manifest | An empty check list, a missing expected result, a shell string instead of an argument list, an absolute path, or a `..` segment blocks approval. A `candidate-code` check with no engine-owned assertion id blocks approval. |
| Owner approval | The owner accepts. The manifest bytes, SHA-256, owner record, base commit, and spec revision are stored outside the project folder. A draft that says "approved" does not freeze anything. Send-back keeps the job unapproved. |
| Candidate copy | A file in the project folder claims to be the manifest or the approval. The pointer still names the attempts-directory snapshot and its hash. |
| Chat without a spec | A Chat goal with no spec is not given files under `pipeline/`, is not set to `waiting`, and is not sent to the gate. Adding a local item afterwards creates a separate pipeline job. |
| Restart and concurrency | A crash during a write leaves the previous status. Two jobs for one project, or two polls at once, cannot write each other's files or attach the same active source id twice. |
| Stop at approved | After approval the status remains `approved`. This implementation does not open a pull request, start a builder, or invoke the gate. |

Use temporary attempts directories, fake GitHub responses, and a fake lead
model. Do not call the network, a real model, or a real repository. Include
paths and issue text with spaces and Unicode. A test must fail if an
unlabelled issue becomes a job, if a spec appears while a question is open, or
if approval succeeds without an owner record and a hash stored outside the
project tree.

## Before implementation

Read the current attempts-directory layout, how a project records its folder
and its linked GitHub repository, how the lead model id is chosen, and
`project-checks.cjs` so the new manifest is not confused with test discovery.
Read [job 19](19-gate.md) before naming the snapshot fields. Do not implement
its runner. If the C# port has no attempts-directory or project-settings API
yet, document that missing port and keep the JavaScript engine unchanged
rather than building the pipeline there.

## Check and files for the implementation job

Add C# tests for every row in the table above. Run `dotnet build
native/Planwharf.Engine`, `dotnet test native/Planwharf.Engine.Tests`, and
`node scripts/engineering/ci.cjs`. The C# build must have no warnings. Report
what was not run.

The implementation may add one new C# pipeline-intake module, its tests, and
the narrow settings and attempts-directory writes this sheet names. Name the
precise files after inspection and before editing. Do not change modules
already ported unless a comparison case is required, and do not change
`docs/DESIGN-PIPELINE.md`, the gate runner, routing, worktrees, or pull
requests.

## Open design decisions for review

Left open on purpose, still owned by job 19. This sheet does not choose them:

- Which isolation mechanism is available on each platform for running
  candidate code, and which checks must pause for owner approval.
- How a complete pre-review snapshot includes working files without private
  notebook data, and which inputs force a fresh run after a worktree changes.

## Hand over

Commit this sheet on `feat/pipeline-intake` and mark job 20 "Done, waiting
for review" in `docs/NEXT-WORK.md`. Add its documentation row to
`docs/BUILT-WITH-PLANWHARF.md` with pull request `#?`. Do not push, open a
pull request, or merge. Implementation starts only after this sheet is
independently reviewed.
