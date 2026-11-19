# Job 19: engine-run acceptance gate

**Goal.** After this sheet is reviewed, build a C# engine gate that runs the
approved checks itself before review and again after syncing with `main`, before
a pipeline pull request is ready. A candidate branch cannot select or weaken
the checks that judge it. This sheet is job 19's deliverable; the gate is not
implemented by this job. Follow [the pipeline design](../../../docs/DESIGN-PIPELINE.md)
and [the porting rules](../README.md).

## Current boundary and dependencies

The running JavaScript engine's `engine/project-checks.cjs` discovers an npm
test script or Node test files *inside the project folder*, runs one command
there with a ten-minute timeout, and returns a short output tail. The
orchestrator calls it only after the whole work; `autonomy.cleanFinish` treats
two complete slices plus a passing run as a clean finish. No recognized tests
return `null`. This is not a pinned acceptance gate: a branch can edit its
`package.json`, test files, and other inputs. `worker-result.cjs` and the
ported C# `WorkerResult.cs` accept worker test entries as structured claims,
not independent proof. The C# orchestrator, process runner, repository sync,
and project-checks module remain later porting work. Job 18's MCP `finish`
contract is also only a reviewed sheet; it explicitly defers verification of
test claims to this gate. Do not describe either job as implemented.

## Build after this sheet is reviewed

| Part | Proposed contract | Implementation checks |
|---|---|---|
| Accepted definition | An owner-approved acceptance manifest lives in Planwharf's job state outside the candidate worktree. Freeze its complete bytes and SHA-256, authorization record, base commit, job/spec revision, check IDs, commands, arguments, working directories, limits, and expected results before build dispatch. The engine reads this snapshot, never the candidate's manifest or a worker result. | Mutate each candidate-side copy of the spec, manifest, config, scripts and tests; the selected manifest hash and check IDs stay fixed. Missing or corrupt pinned data blocks the gate. |
| Runner | The C# engine starts each check as a bounded process with separate executable and argument list, explicit working directory in the isolated job worktree, restricted environment, no shell unless a specifically approved command requires one, and no model call. Capture exit, timeout, start failure, bounded output, and cancellation. | A fake executable covers pass, nonzero exit, spawn error, timeout, cancellation, and child cleanup on supported platforms. |
| Evidence | Persist an append-only gate-run record outside project history, bound to job ID, stage, attempt, manifest hash, trusted base SHA, candidate commit/tree SHA and working-input digest, worktree identity, command ID, start/end times, outcome, and output digest. Seal the full result set before reporting a pass. | Restart, partial write, duplicate run, and changed candidate input cannot reuse or overwrite a pass. Logs remain bounded and redact credentials. |
| Decisions | A gate passes only when every required check ran for this exact snapshot and candidate and met its declared expected result. A missing, skipped, failed, errored, timed-out, or stale result blocks progression. | A green worker report or process exit 0 without all expected assertions cannot promote the work. Failure goes to the builder before review; failure after sync blocks PR readiness. |

## Trusted inputs and authorization

1. At spec approval, the engine saves the accepted `spec.md` revision and a
   machine-readable check manifest in job state outside the candidate Git
   history. An owner or separately authorized reviewer accepts the manifest;
   the candidate builder and its branch cannot grant that authorization.
   Record an immutable snapshot ID and hash. The engine resolves the snapshot
   from the job record, then verifies both its hash and authorization at every
   gate run. A job without an accepted manifest waits; test discovery is not a
   substitute for this decision.
2. Each check has a stable ID, purpose, exact executable and argument vector,
   worktree-relative working directory, required fixtures or paths, timeout,
   expected exit code, and an explicit output or artifact assertion when exit
   zero alone cannot establish acceptance. Declare whether its program and
   assertions are trusted engine-owned code, a pinned external tool, or code
   from the candidate worktree. Run candidate code as untrusted; its exit code
   alone cannot prove that an acceptance assertion ran.
3. The manifest pins trusted test and assertion bytes outside the candidate
   tree. Before each run, the engine verifies their hashes and invokes those
   pinned bytes against the candidate worktree. A command such as `npm test`
   may remain a supplementary project check, but cannot be the sole authority
   when `package.json` and the test runner are candidate-controlled. If a
   check necessarily calls candidate code, an engine-owned assertion must
   independently inspect its result or artifact; otherwise the manifest must
   mark that check insufficient and the gate blocks.
4. A legitimate check change creates a new manifest revision through a
   separate approval path, records who authorized it and why, and invalidates
   old gate passes. The builder may propose a revision, but cannot authorize
   it. A revised manifest requires a fresh gate run for the same candidate;
   an approval does not relabel old evidence as passing.

## Execution and evidence boundary

The engine takes an immutable candidate snapshot for each gate run, including
the tracked tree and any uncommitted or untracked files supplied as build
inputs. It records a digest of the exact inputs used. A change during a run
aborts it; a later change invalidates its pass. The post-sync gate uses the
new integrated commit and tree, after the sync has completed, and cannot reuse
the pre-review result. The implementation must decide how to represent a
snapshot across Windows, macOS and Linux without copying personal notebook
data or including ignored/private files. If an input cannot be pinned safely,
stop and ask the owner rather than claiming a pass.

Resolve every manifest path against the isolated job worktree or the trusted
check store as declared. Reject traversal, symlink escape, unexpected absolute
paths, and a check that tries to reach another project or attempt. Give checks
only the environment and permissions they need; do not pass Planwharf tokens,
provider credentials, Git push credentials, or the trusted manifest store to
the candidate process. Bound wall time, output size and process trees, and
close descendants on timeout or cancellation. This is an execution boundary,
not a claim that a subprocess sandbox makes hostile repository code safe.
Where isolation cannot enforce the declared boundary, require owner review
before running that check.

Gate evidence is an engine record, not text from stdout, an MCP `finish`, a
JSON result file, a modified CI workflow, or an artifact placed in the branch.
The engine saves each command's observed exit or execution error and verifies
the required assertion. The pass record references only successful, complete
command records with matching snapshot, manifest and stage IDs. Preserve
historical raw execution evidence; append a superseding run after a change.
Summaries shown to a model or owner must say what actually ran and retain a
link to the original engine evidence. Do not infer success from a command
string, claimed test count, truncated log, or exit zero without assertions.

## Acceptance scenarios for the implementation

| Scenario | Input and expected outcome |
|---|---|
| Successful pre-review gate | Approved manifest A has two pinned checks and assertions; candidate snapshot C satisfies both. Engine records A/C and two observed passes, then releases the reviewer. No model is invoked by the gate. |
| Successful post-sync gate | `main` sync changes C to D. Both checks rerun on D, and only a new complete pass can mark the pull request ready. The earlier C pass remains historical evidence. |
| Failed or absent check | One assertion fails, one command exits nonzero, or a required check is omitted. The gate is blocked; before review it returns the failing check and bounded log to the builder. After sync it prevents PR readiness and flags repair or owner decision. |
| Candidate tampering | Candidate edits `package.json`, test files, a check command, a manifest copy, CI config, or a JSON/MCP result claiming pass. A stays selected; edits to the trusted check copy or evidence are rejected. A candidate-controlled runner that only prints a green result cannot satisfy an independent pinned assertion. |
| Execution failure | Executable missing, spawn denied, invalid path, process crash, or evidence persistence failure produces a distinct error and no pass. A retry creates another run record; it does not overwrite the failure. |
| Timeout or cancellation | A check exceeds its limit or the job is cancelled. Engine stops its process tree, records timeout/cancellation, and blocks progression even if partial output says pass. |
| Stale or mismatched evidence | An old manifest hash, wrong job/stage/attempt, different tree, changed worktree during run, or incomplete record is rejected. Restart reads the pinned manifest and may resume only verified completed checks bound to the exact snapshot, or reruns them. |

Use fake programs, disposable repositories and injected clocks/process hooks for
the cases above. Verify concurrent jobs cannot read or reuse one another's
manifest or evidence. Include path and argument cases with spaces and Unicode,
and Windows child-tree cleanup. A test must fail if a required check is silently
skipped or a result from another snapshot is accepted.

## Before implementation

Read the complete current `project-checks.cjs`, `autonomy.cjs` check handling,
orchestrator call site, worktree and sync paths, worker-result validation, and
their tests. Identify the C# runner and repository APIs once their porting
jobs land; document any still-missing port before implementation. Specify the
manifest schema, check store, owner-authorization mechanism, snapshot method,
and error states in that implementation's change. Review any command that
executes candidate code or uses a shell. Job 20 owns intake and `spec.md`;
job 21 owns build/review routing and PR stage transitions. Integrate with
those contracts when they exist rather than inventing a second pipeline.

## Check and files for the implementation job

Add C# unit and integration tests for all table scenarios, including
independent assertions over candidate output. Run
`dotnet build native/Planwharf.Engine`,
`dotnet test native/Planwharf.Engine.Tests`, and
`node scripts/engineering/ci.cjs`; report observed results and platform cases
not run. The C# build must have no warnings. When a ported JavaScript module
changes, follow porting rule 12 and update its comparison cases.

Implementation files may include the C# engine and its tests, the narrow job
state and runner integration, and parity cases required by a changed port.
Name the precise files after inspection and before editing. Keep the old
JavaScript engine behavior unless an independently authorized job changes it.
Do not use a candidate branch's CI configuration as the gate authority.

## Open design decisions for review

- How the owner approves and stores a manifest before job 20 supplies its spec
  workflow, including a migration path for existing Chat work with no spec.
- Which isolation mechanism is available on each platform for running
  candidate code, and which checks must pause for owner approval.
- How a complete pre-review snapshot includes working files without private
  notebook data, and which inputs force a fresh run after a worktree changes.

## Hand over

Commit this sheet on `feat/gate` and mark job 19 "Done, waiting for review" in
`docs/NEXT-WORK.md`. Add its documentation row to
`docs/BUILT-WITH-PLANWHARF.md` with pull request `#?`. Job 18 remains deferred;
job 19 implementation starts only after this sheet is independently reviewed.
