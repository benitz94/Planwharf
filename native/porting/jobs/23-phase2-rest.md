# Job 23 (phase 2): sheet for the rest of the modules with no engine dependencies

**Goal.** Describe the port of eight JavaScript modules to C#, with their tests and their
comparison cases, following `native/porting/README.md` (rules 1 to 12). See [the plan](../PLAN.md),
phase 2, and the shape of [job 04](04-briefing-refusals-autonomy.md). This sheet is the deliverable
of job 23. It does not port the modules. Job 24 (`port/24-phase2-rest`) ports them after a model
from another company reviews this sheet. The JavaScript stays the specification.

The eight modules do not import each other and do not import a module already ported to C#. They
can be ported at the same time, each worker changing only its own C# file, its own test file, and
its own block at the end of the comparison list.

## Port

| JavaScript | Lines | C# | What the tests cover |
|---|---|---|---|
| `plan.js` | 379 | `Plan.cs` | Pure notebook edits. Call sites that do not start a server: `engine/start-over.test.cjs` (`createProject`, `removeProject`, `resetProject`), `engine/project-export.test.cjs` (the `createProject` setup), `engine/gitea.test.cjs` (`isGithubUrl` only), `engine/project-teams.test.cjs` (`createProject` only). `example()` is the fixture, not a user's notebook. |
| `quota.js` | 18 | `Quota.cs` | `engine/phase1.test.cjs`, the test `quota remaining bands and explicit text including shared provider pools`, and the `quota.js` text check in `engine/claude-usage.test.cjs`. |
| `models.js` | 107 | `Models.cs` | No Node test calls this file. It is the browser helper `PlanwharfModels`, not `models.json` and not `engine/provider-catalog.cjs`. Job 24 adds the C# tests from the contract below. |
| `engine/sentinel.js` | 82 | `Sentinel.cs` | `engine/attempts.test.cjs`: `sentinel: written COMPLETE ends waiting even if process is alive`, `sentinel: missing verdict closes the attempt`, `sentinel: does not open a second attempt while the first is active`. Supply a fake attempt engine. Do not port `attempts.js`. |
| `engine/invocations.cjs` | 79 | `Invocations.cs` | `engine/final.test.cjs`: `ledger retains exact identity, durations, terminal states and reliable tokens across restart`. `engine/supervisor-role.test.cjs`: `Supervisor usage is separate and unfinished calls become blocked after restart`. |
| `engine/notebook-file.cjs` | 12 | `NotebookFile.cs` | The `ensureDefaultNotebook` checks at the end of `engine/task-completion.test.cjs`. The HTTP task loop in that file is not this module. |
| `engine/project-checks.cjs` | 67 | `ProjectChecks.cs` | `engine/autonomy-crew.test.cjs`: `project checks run node --test when test files exist and report a failure`, for `testCommand` and `testFiles`. `runChecks` is specified below and is not run against the host's npm. |
| `engine/project-workspace.cjs` | 72 | `ProjectWorkspace.cs` | The folder checks in `engine/worker-containment.test.cjs` and `engine/worktrees.test.cjs` that call `requireWorkerWorkspace`, using a temporary repository the test creates. `gitEnvironment` callers in `engine/commit-push.test.cjs`, `engine/plan-option.test.cjs`, and `engine/repository.test.cjs` stay those modules' tests. |

The C# files go in `native/Planwharf.Engine/`. The first line is the port comment, for example
`// Port of plan.js.` Tests go in `native/Planwharf.Engine.Tests/` (`PlanTests.cs`, `QuotaTests.cs`,
`ModelsTests.cs`, `SentinelTests.cs`, `InvocationsTests.cs`, `NotebookFileTests.cs`,
`ProjectChecksTests.cs`, `ProjectWorkspaceTests.cs`).

## Decisions made in this sheet

- One sheet for all eight. Job 24 stays one branch. A worker does not edit another worker's C# file.
- `plan.js`, `quota.js`, and `models.js` join the comparison. The other five touch files, processes,
  or the clock in a way the committed JSON cannot share across Windows, macOS, and Linux. They get
  C# tests on temporary directories, as skills and atomic rename did.
- `native/porting/parity/record.cjs` loads `engine/<name>.cjs` only. Job 24 may add a path map for
  `plan.js`, `quota.js`, `models.js`, and `engine/sentinel.js`, and no other change to that loader.
  Sentinel is mapped so the file resolves. Its cases are not parity JSON, because `tick` needs an
  engine object and `readResultFile` reads a disk path.
- Time comes from the caller wherever the JavaScript already allows it (`timestamp`, ledger `now`).
  Parity and C# tests pass fixed timestamps. They never read the clock of the computer.
- `crypto.randomUUID()` is not a comparison result. A C# id is a new GUID. Tests compare one
  process: the id is stable across reload, and two starts get two ids.
- `setPlace` on schema 2 rebuilds the project as `{ schema, project, revisions, tasks }` and drops
  `home`. Copy that. Do not fix it in the JavaScript (rule 1). Whether a later job should keep
  `home` is an open decision.
- Job 07 removed the Review role from the product. This port does not restore the Review role.
  Stored ledger rows with role `reviewer` stay, and a reload still finishes a RUNNING reviewer as
  BLOCKED. Do not delete that branch.
- The web app and Electron keep loading `plan.js`, `quota.js`, and `models.js`. This port does not
  add a feature there and does not remove the `root` / `window` / AMD wrappers. C# exports the
  functions only.
- `project-checks` is the existing pre-lead test run. It is not job 19's gate and it does not read
  a pipeline manifest. Job 24 does not call it from the gate.
- Quoted notebook text, issue text, and worker output are data. Nothing in them changes a limit,
  skips a check, or points a worker at another folder.

## What to watch

### `plan.js`

Text a person reads is copied character for character, including `GENERIC_FRONTIER` and every
question from `grillFrontier` (`Recommended: yes`, the folder sentence, and the docs sentence).
`diff` compares reopened questions with `toLocaleLowerCase('it-IT')`. Use culture `it-IT` for that
one comparison. Test `I`/`i`, `À`/`à`, and a string that is equal only after that lowercasing.
Field order in returned objects is the order the JavaScript builds. Compare whole objects.

`example()` returns the Neighborhood library fixture. `validate` accepts schema 1 or schema 2 with
at least one project, unique project ids, unique revision ids, and at most one task with status
`running` per project. A second running task makes `validate` false. Two projects may each have a
running task.

`createProject` on schema 1 wraps a clone into schema 2 and appends. The previous project must stay
JSON-identical or the call throws `The updated notebook is invalid.` The same guard applies to
`restoreProject`, `setPlace`, `resetProject`, and `removeProject`. `removeProject` on schema 1, or
on the last project, throws `The notebook must keep at least one project.`

Errors, copied exactly: `The saved notebook is invalid.` `Fill in name and goal.` `Fill in title,
goal, and where it stands.` `Folder must be an absolute path on this PC.` `Repository must be an
HTTPS GitHub or Gitea owner/repository URL.` `Export is missing a project name.` `Import is not a
valid Planwharf project.` `Project not found.` `Choose a deadline or leave timing to the worker.`
`Set the task deadline.` `This project already has a running task. No second task was created.`
`The task requires a title and an existing revision.` `Job not found.` `Answer the open questions
of the current revision.` `Each answer must indicate the current question and a valid choice.`
`Choose only one new direction per round.` `Invalid import JSON.` `Import an object with title,
goal, where it stands, decided decisions, and open questions.` `No docs in the folder to grill
with.`

`projectIdFromName` trims, lowercases, NFKD-normalizes, keeps Unicode letters and numbers, cuts the
base at 48 characters, and uses `project` when nothing remains. A collision adds `-2`, `-3`, and so
on. The suffix is not cut back to 48, so the id may exceed 48. Test `Å` (NFKD drops the mark) and
a name that is only punctuation.

`normalizeFolder` strips quotes and `file://`, and drops a leading slash before a drive letter
(`/C:` becomes `C:`). `normalizeGithub` rewrites `git@github.com:` and `http(s)://github.com/`
to `https://github.com/owner/repo`. `isGithubUrl` rejects `https://user:secret@forge.example/owner/repo`,
`.` and `..` path parts, and a missing host. A Gitea URL with a port and extra path segments is
accepted when each segment matches. `isFolderPath` accepts a drive path, a UNC path, and a POSIX
absolute path longer than one character. It rejects empty, longer than 500, and NUL.

`startGrill` keeps the notebook when the latest revision already has open questions, unless
`context.replace` is set or `isGenericFrontier` is true. `isGenericFrontier` is exact equality with
`GENERIC_FRONTIER`, not a trimmed comparison. `grill` appends first, then sets `responses` on the
new revision. `validate` does not know `responses`. Copy that order. `importRevision` rejects extra
keys. `restoreProject` copies a running task as `paused`, keeps only known task and note fields,
and omits `home` when no note survives. `createTask` trims title and detail. An empty detail is
allowed. The id is `task-1`, then `task-2`, skipping ids that exist. `taskTiming` returns `{}`
when timing is omitted, `discretion` with no deadline, or `deadline` with `deadlineAt` rewritten
through `toISOString`. Pass the timestamp into every call that has one.

### `quota.js`

`describe` uses `leftPct` when it is finite, otherwise `100 - pct` when that is finite, and only
when `readable` is true. Otherwise `{ remaining: null, band: 'grey', text: 'REMAINING UNREAD' }`.
A finite value is rounded, then clamped from 0 to 100. Bands: `green` at 75 and above, `orange` at
50 to 74, `yellow` at 25 to 49, `red` below 25. Text is `${remaining}% REMAINING`, and remaining 0
appends ` · EXHAUSTED` (middle dot, spaces). `html` returns
`<span class="quota-remaining quota-${band}">${text}</span>` with no extra escaping. The text is
the band sentence, so a plan object cannot inject markup. Shared pool: two models on one plan
string show the same sentence. Unread is not a number. A negative percent clamps to 0. A value
above 100 clamps to 100.

### `models.js`

`getEnabledModels` returns a new array of shallow copies where `enabled === true` and
`available !== false`. Anything else is omitted, including a null entry. The input array and
objects stay unchanged. `setModelEnabled` also copies. A null entry stays null, not a copy.
`enabled` is stored as a boolean, and `available === false` forces `enabled` false even when the
caller asked for true. `validateWorkerClaim` checks in this order: no enabled and available model
returns `{ ok: false, reason: 'no model enabled' }` even when the id is also missing; then a
missing id returns `model not present`; then `enabled !== true` or `available === false` returns
`model disabled`; otherwise `{ ok: true, reason: null }`. The repeated comment in the JavaScript
is not a second reason. `attempts.js` shares `no model enabled` and `model not present`, then
returns `model not eligible` and does not look at `available`. That file is phase 3. Do not change
it, and do not make `models.js` return `model not eligible`.

### `engine/sentinel.js`

`readResultFile` returns null for a non-string or blank path, when the file is
missing, and when the read throws. A line matching
`STATUS: COMPLETE` (spaces allowed, any case) returns `{ status: 'COMPLETE', raw }`.
`STATUS: FAILED` returns `FAILED` only when no COMPLETE line matched. COMPLETE
wins if both lines exist. Any other text returns `{ status: null, raw }`.
`STATUS: COMPLETED` is not COMPLETE. The browser branch where `require` is
missing returns null. C# has no that branch. An empty path still returns null.

`tick` with a missing engine or attempt returns
`{ waiting: false, complete: false, closed: false, reassigned: false, reason: 'invalid data' }`.
A COMPLETE file, or `resultStatus === 'COMPLETE'` on the recorded attempt,
returns reason `result file` with `complete: true` even when `isAlive` is true.
The attempt is not closed. When the process is dead and the file has no
COMPLETE or FAILED status, and the recorded status is `active`, call
`closeAttempt({ attemptId, reason: 'no verdict' })` and return `closed: true`,
reason `no verdict`. Do not close a row that is not `active`. When
`getActiveAttempt` still returns an id, return reason `claim still valid`,
`waiting: true`, `reassigned: false`. That is the case "does not open a second
attempt while the first is active". Reassign only when `extra.candidates` and
`extra.modelId` are set and `engine.claim` exists. On `next.ok`, reason
`reassigned` and pass `attempt` through. Otherwise reason is `next.reason` or
`not reassigned`. With nothing to do, reason is `waiting`. Hooks default:
`readResult` falls back to `readResultFile`, `isAlive` defaults to false.

The three attempt tests use `createAttemptEngine`. Port them with a fake
engine that records `closeAttempt`, `claim`, `getResult`, and
`getActiveAttempt`. The real `attempts.js` store is a deferred case.

### `engine/invocations.cjs`

The file is `invocations.jsonl` in the directory the caller passes. Metadata
only: prompts, output, environment, and credentials never enter this file.
`start` stores `role`, ids, `provider`, `model`, `modelId`, `accountKey`,
`accountType` (default `unread`), and `reason`. Unknown fields on the input,
including `env` and a token, are dropped. `update` copies only `pid`,
`exitPath`, and `resultPath`, and only while status is `RUNNING`. `finish`
ignores a missing id and a row that is not `RUNNING`, so a second finish
appends nothing. `processExit` is kept only when it is an integer. Otherwise
null. Tokens are stored only when `input` and `output` are safe integers.
`cachedInput` is added only when it is a safe integer. Unsafe token integers
are omitted. `durationMs` is `max(0, end - start)` from the injected clock.

`reconcile` looks only at `worker` rows that are `RUNNING`. A usage file
`resultPath + '.usage.json'` is optional. A missing usage file does not fail
reconcile. A matching `snapshot.execution.results` row finishes with that
status and `exitCode`. Otherwise a missing `pid`, a missing `isAlive`, or
`isAlive(pid) === true` leaves the row RUNNING. A dead worker reads `exitPath`
and the first 8192 characters of `resultPath`. A leading `STATUS:` of
`CHECKPOINTED` is stored as `BLOCKED`. `COMPLETE`, `FAILED`, and `BLOCKED`
are stored as read. No status becomes `BLOCKED`.

Creating the ledger finishes every still-RUNNING `orchestrator`, `reviewer`,
and `supervisor` row as `BLOCKED`. A RUNNING `worker` is not blocked merely
by reload. A truncated historical line is skipped and the rest load. If the
file does not end in a newline, append one before new records. A crash during
append may leave a partial line. Reload skips it. Writes pass mode `0o600`.
Windows may ignore that mode. The test must not depend on the ACL. Assert the
requested mode where the platform stores it, and on every platform assert that
the JSON line has no secret.

`view(projectId)` hides other projects. Groups are orchestrator, workers, and
supervisors, keyed by provider plus model. Counts are calls, durationMs, and
`COMPLETE`, `FAILED`, `BLOCKED`, `RUNNING`. Token sums count only rows that
have tokens. `recent` is the last 20 rows, newest first. Two starts in one
process both remain. Two workers do not share a ledger row. There is one
ledger file per directory. The JavaScript does not lock two processes. Do not
add a lock that changes the line format. A concurrent test in one process
appends two lines and reloads both.

### `engine/notebook-file.cjs`

`ensureDefaultNotebook(file, root)` copies `fixtures/starter-notebook.json`
onto `file` only when `path.resolve(file)` is `path.join(path.resolve(root), 'plan.json')`.
The copy uses `COPYFILE_EXCL`. `EEXIST` does not overwrite, so a second call
leaves personal notebook bytes in place. Any other error is thrown, including a missing parent directory of the
default `plan.json`. The function does not create directories. Another
path, including a missing `missing.json`, is a no-op. The C# test builds its own root and its own starter fixture.
It does not read or write the checkout's `plan.json`.

### `engine/project-checks.cjs`

`TIMEOUT_MS` is 10 minutes. `testFiles` walks no deeper than 4 and stops when
`found.length > 200` at the start of a directory. It can pass 200 inside one
directory. Skip names that start with `.` and skip `node_modules`. A file
matches `(test|tests|__tests__)/….(c|m)?js` or `.(test|spec).(c|m)?js`, any
case. `testCommand` returns null for an empty folder. A `package.json` script
`test` that is a non-empty string and does not match `no test specified`
returns label `npm test`. On Windows the command is `ComSpec` or `cmd.exe`
and the args are `/d`, `/s`, `/c`, `npm test`. Elsewhere the command is `npm`
and the args are `test`. `shell` is not true. Arguments are a list. When
there is no such script and test files exist, the command is the current
runtime with args `--test` and label `node --test`.

`runChecks` merges `options.env` over the process environment, then forces
`CI` to `1`, then deletes `NODE_TEST_CONTEXT`. Options cannot keep that
variable and cannot clear `CI`. The child cwd is the folder. stdio ignores
stdin and pipes both outputs. While reading, the output is tailed to 20000
characters. The result tails to 3000 characters, with a leading `...` when
cut. `seconds` is one decimal. A double `close` resolves once. A spawn error
finishes with `exitCode` null and the message appended. On timeout, Windows
runs `taskkill` with `/PID`, `/T`, `/F`. Elsewhere the child gets `SIGKILL`.
`timedOut` is true.

The JavaScript has no process hook. The C# port may take an optional starter
used only by tests. The default starter must match the spawn above. Tests
pass the hook and do not launch the host's npm. Live `npm test`, live
`node --test`, and live `taskkill` are deferred cases. The hook test still
checks the command, the args, the cwd, `CI=1`, the deleted context, the
20000 and 3000 tails, a non-zero exit, a timeout flag, and a null exit on
start error. `testFiles` and `testCommand` are pure reads of a temporary
folder, including a folder whose name has spaces and Unicode.

### `engine/project-workspace.cjs`

`linkedWorkspace` requires a non-empty absolute path, then `realpath`. A
missing path, a file, or a throw returns null. `within(root, candidate)` is
true when the relative path is empty or is not absolute, is not `..`, and
does not start with `..` plus the separator. `..\\hidden` and `../hidden`
are outside. This function follows the OS separator, so it is not a parity
JSON case. Test both a path inside and a sibling.

`requireWorkerWorkspace` for kind `research` (on `info.job` or `info`) uses
`info.folder` as the root and returns `linkedFolder: ''`. Any other kind
requires `info.linkedFolder`. A missing root throws `A real linked project
folder is required before worker launch.` A folder that is not that root,
and is not a registered worktree, throws `Worker launch folder must stay
inside the linked project folder.` `cwd` or `job.folder`, when set, must
realpath to the same folder or the call throws `Worker cwd and job folder
must match the linked project folder.` A sibling folder is rejected. A
directory symlink or junction that leaves the root is rejected.

`registeredWorktree` is false unless `safeGitWorkspace(root)` returns the
root, `resultPath` or `result` is absolute, and `projectId`, `megaTaskId`,
and `subTaskId` each match `^[A-Za-z0-9_-]{1,100}$`. The expected directory
is `path.join(dirname(dirname(result)), 'worktrees', projectId, megaTaskId, subTaskId)`
and must realpath to `folder`. Git is `worktree list --porcelain -z` with
`gitEnvironment()`, timeout 10000, and a 1 MiB buffer. A record must start
with `worktree ` and realpath to `folder`. Any throw is false. The test
creates the repository. It does not inspect the Planwharf checkout.

`safeGitWorkspace` returns null, and does not throw, when `.git` is not a
directory, when a metadata symlink or realpath leaves `.git`, when the walk
passes 100000 entries, when `commondir`, `objects/info/alternates`, or
`objects/info/http-alternates` exists, or when config matches an include or
includeIf section, or a `worktree=` or `fsmonitor=` line. External worktrees
and shared object stores stay out. Do not follow them.

`gitEnvironment` drops every key whose uppercase form starts with `GIT_`,
then sets `GIT_CONFIG_NOSYSTEM` to `1`, `GIT_CONFIG_GLOBAL` to `NUL` on
Windows and `/dev/null` elsewhere, and `GIT_TERMINAL_PROMPT` to `0`. It is
not a parity JSON case. The C# test passes a dictionary. It does not read
the developer machine's Git configuration. A value already in that
dictionary that is not a `GIT_` key is kept.

## Comparison (rule 12)

Add cases only for `plan`, `quota`, and `models` in `native/porting/parity/cases.cjs`.
Run `node native/porting/parity/record.cjs` and wire each exported function in
`ParityTests.Ports`. Each worker appends its own block at the end of the
module list and of `Ports`, and touches no other line of those two files
except the path map in `record.cjs` described above.

`plan` cases, all with a fixed timestamp, include: `example`; valid and
invalid schema 1 and schema 2; two running tasks in one project; one running
task in each of two projects; `createProject` from schema 1; a duplicate
name; a folder and a GitHub URL; bad folder; bad URL; `user:secret`;
`git@github.com:owner/repo.git`; quotes and `file://`; `/C:`; NUL; a 501
character path; NFKD `Å`; a 48 character base plus `-2`; `restoreProject`
pausing a running task and dropping an unknown field; `setPlace` dropping
`home` on schema 2; `resetProject` leaving the other project identical;
`removeProject` of the last project; `appendRevision`; `startGrill` kept,
replaced, and generic frontier; `startGrillDocs` with no docs; `grill` with
keep, close, and two direction changes; `importRevision` with an extra key
and with invalid JSON; `createTask` while one is running, with an empty
detail, and with a bad deadline; `removeTask`; `diff` reopening a question
under `it-IT`; `taskTiming` omitted, `discretion`, and `deadline`.

`quota` cases include the nine band boundaries from the phase 1 test (90,
75, 60, 50, 40, 25, 10, 1, 0), unread, `pct` without `leftPct`, a negative
number, a number above 100, and `html` for red exhausted and for grey.
`models` cases include a null entry, `enabled: true` with `available: false`,
a copied object that does not alias the input, `setModelEnabled` refusing to
turn an unavailable model on, `no model enabled`, `model not present`, and
`model disabled`.

Do not put `within`, `gitEnvironment`, ledger ids, or `readResultFile` in
the JSON. `engine/parity.test.cjs` must keep failing when a recorded
JavaScript result changes and the JSON was not written again.

## Deferred cases

Do not port these in job 24. Name them in the pull request.

- `attempts.js`, `createAttemptEngine`, and every sentinel test that needs the real store. Phase 3 owns attempts. The fake-engine cases above are in scope.
- `createServer`, `createOrchestrator`, `spawnWorker`, `runner.cjs`, and the HTTP loops in `task-completion.test.cjs`, `grill-chat.test.cjs`, `project-export.test.cjs`, `project-teams.test.cjs`, `start-over.test.cjs` (`archiveProject`), and `worker-containment.test.cjs` (launch artifacts and provider cwd).
- Live `npm test`, live `node --test`, and live `taskkill`. The hook covers the decision.
- Byte-identical `randomUUID` values across JavaScript and C#.
- `gitEnvironment` and `within` as parity JSON.
- The browser `root` / `window` / AMD wrappers, `models.json`, and `provider-catalog`.
- A second process appending to `invocations.jsonl` at the same time. One process is in scope.
- Reading the checkout's `plan.json` or `.git`.

## Check

```
node native/porting/parity/record.cjs
node engine/parity.test.cjs
dotnet build native/Planwharf.Engine
dotnet test native/Planwharf.Engine.Tests
node scripts/engineering/ci.cjs
```

All must pass with no warning. Tests do not depend on the computer (rule 11):
temporary directories, a dictionary for the environment, and the process hook.
Do not call the network, a real model, or a real repository. The temporary
git repository is one the test creates.

## Files job 24 may change

`native/Planwharf.Engine/`, `native/Planwharf.Engine.Tests/`,
`native/porting/parity/cases.cjs`, the path map in
`native/porting/parity/record.cjs`, and the `.json` files that `record.cjs`
writes for `plan`, `quota`, and `models`. Nothing else: not the JavaScript
(rule 1), not modules already ported, and not the web app or Electron.

## Files this sheet job may change

`native/porting/jobs/23-phase2-rest.md`, `engine/job23-sheet.test.cjs`,
`docs/NEXT-WORK.md`, and `docs/BUILT-WITH-PLANWHARF.md`. No `Plan.cs`,
`Quota.cs`, `Models.cs`, `Sentinel.cs`, `Invocations.cs`, `NotebookFile.cs`,
`ProjectChecks.cs`, or `ProjectWorkspace.cs` on this branch.

## Open design decisions

- Whether `setPlace` should keep `home`. Job 24 copies the drop.
- Whether job 24 should be split if the `plan.js` port overruns one review. This sheet keeps one branch.
- Whether the C# process hook for `runChecks` should later be added to the JavaScript. Not in this port.

## Hand over

Job 23 commits on `port/23-sheet` and stops. It does not push or merge. Job 24
commits on `port/24-phase2-rest` with a message that lists what was ported,
the deferred cases and why, the number of comparison cases per module, and
any difference from the JavaScript found on the way. The `home` drop is one
of those differences. A model from another company reviews the port before
merge, as it reviews this sheet before the port starts.
