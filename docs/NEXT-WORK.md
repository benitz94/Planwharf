# The whole plan, in order

The owner only says: **"Read docs/NEXT-WORK.md and continue."** This file holds the whole plan:
Planwharf moves to one native app with its engine in C# (the plan drawn up with the owner on 12 and
13 November 2026; the details of each phase are in [native/porting/PLAN.md](../native/porting/PLAN.md)
and the porting rules in [native/porting/README.md](../native/porting/README.md)). Planwharf does the
jobs itself, one at a time, in the order below; each one is reviewed before it is merged.

## For the lead: how to continue

1. Read this file on the refreshed `origin/main` (`git fetch`, then `git show origin/main:docs/NEXT-WORK.md`):
   the copy in the folder may be older.
2. Take the **first job of the list below whose status is "To do"**. Skip a job whose branch already
   exists on `origin` (`git branch -r`): it is done and waiting for review; say so to the owner.
3. If the folder has uncommitted changes, stop and tell the owner which files: do not start.
4. Create the job's branch (its name is in the list) from the refreshed `origin/main`, whatever branch
   the folder is on: `git fetch origin`, then `git switch -c <branch> origin/main`.
5. **A job with a sheet:** plan it as its sheet says (the sheets are in `native/porting/jobs/` and in
   the sections at the end of this file): build, then an independent verification by a model of
   another company.
   **A job without a sheet yet:** the job is only to write its sheet, in `native/porting/jobs/`, with the
   shape of `04-briefing-refusals-autonomy.md`: the modules of that phase still to port, their tests,
   what to watch, the comparison cases, the check and the files allowed. Read each module and its tests
   before writing. Then stop: the owner has the sheet reviewed before the port starts.
   Writing a sheet is not doing the work: mark that row "Sheet done", never "Done"; the work itself
   is a row of its own ("Port them…", "Build…").
6. In the same branch, change the job's status in the list below to **"Done, waiting for review"**,
   so that once merged, the next reading starts from the next job.
7. Commit on the branch. Do not push and do not merge: the owner pushes with `git push -u origin HEAD`
   and asks for a review.
8. Stop and ask the owner when a phase says so, when the two engines cannot be kept equal, or when a job
   needs a decision.
9. Do only the job you took. A test that already fails on `origin/main`, before your change, is not
   yours to fix: name it to the owner in your answer and in the commit message, and go on with the job
   (job 14 collects those failures).

## The list

Jobs are numbered in order; the porting jobs 01 to 04 come first.

| # | Phase | Job | Branch | Status |
|---|---|---|---|---|
| — | 0. Foundations | .NET 10, engine and test projects, porting rules, README | — | Done (#61, #79) |
| — | 1a. Test bench | Fake AI programs (job 03) | — | Done (#86) |
| — | 1b. Test bench | Module-by-module comparison with the JavaScript | — | Done (#81) |
| — | 2. Modules with no dependencies | Jobs 01, 02, 04: skills, worker results, structured final, model roles and policy, accounts, browser settings, activity events, refusals, diagnostics, briefing, project history, evidence, autonomy | — | Done (#64, #74, #90) |
| — | ARM builds | Windows and Linux ARM | — | Done (#80) |
| 05 | Before the porting goes on | The work plan file, and checks before the crew starts (sheet below) | `fix/plan-file` | Done (#97) |
| 06 | Before the porting goes on | The Push button pushes the branch where it belongs (sheet below) | `fix/push-button` | Done (#101) |
| 07 | Before the porting goes on | Remove the Review role (sheet below) | `fix/remove-review` | Done (#104) |
| 08 | Before the porting goes on | An estimate of the time left for the work (sheet below) | `fix/time-left` | Done (#110) |
| 09 | Before the porting goes on | The native app updates itself (sheet below) | `feat/native-update` | Done (#114) |
| 10 | Before the porting goes on | Tools chosen per model (sheet below) | `feat/tools-per-model` | Done (#118) |
| 11 | Before the porting goes on | An alert on the computer when work ends (sheet below) | `feat/pc-alert` | Done (#118) |
| 12 | Before the porting goes on | Alerts and commands on the phone (sheet below) | `feat/phone` | Done (#118) |
| 13 | Before the porting goes on | The logo follows the main colour; the work plan file can be kept in git (sheet below) | `fix/logo-plan-option` | Done (#121) |
| 14 | Before the porting goes on | Review the commit "16 failing test files" (sheet below) | `fix/failing-tests` | Done (#122) |
| 15 | Before the porting goes on | Each parallel worker in its own copy of the folder (git worktree) (sheet below) | `feat/worktrees` | Done (#124) |
| 16 | Before the porting goes on | More than one account per company, and plans that expired or are signed out (sheet below) | `feat/accounts` | Done (#133) |
| 17 | Before the porting goes on | The native app in English and Italian, with the language chosen in Settings (sheet below) | `feat/languages` | Done (#132) |
| 18 | Protocol and pipeline | Planwharf's own MCP tools for workers: `ask`, `progress`, `finish` (checked at once), `decision`, `handoff`; the JSON file stays for programs without MCP. [Sheet](../native/porting/jobs/18-mcp-protocol.md) written from [the design](DESIGN-PIPELINE.md); implementation waits for review | `feat/mcp-protocol` | Sheet done (#138) |
| 19 | Protocol and pipeline | Acceptance tests and the gate: the engine runs the checks itself, before review and before a work counts as done; a branch cannot change its own checks. [Sheet](../native/porting/jobs/19-gate.md) written from [the design](DESIGN-PIPELINE.md); implementation waits for review | `feat/gate` | Sheet done (#140) |
| 20 | Protocol and pipeline | Pipeline, part 1: the queue (GitHub issues labelled `planwharf` and a local list, each an option), intake with questions, spec with acceptance tests, job state in files. [Sheet](../native/porting/jobs/20-pipeline-intake.md) written from [the design](DESIGN-PIPELINE.md); implementation waits for review | `feat/pipeline-intake` | Sheet done (#142) |
| 21 | Protocol and pipeline | Pipeline, part 2: routing by risk and parts touched, build and review in pairs per task with rounds, gate, a pull request per job. [Sheet](../native/porting/jobs/21-pipeline-build.md) written from [the design](DESIGN-PIPELINE.md); implementation waits for review | `feat/pipeline-build` | Sheet done (#144) |
| 22 | Protocol and pipeline | Pipeline, part 3: several jobs at once in their worktrees within the quota, the pipeline view in the native app (stages, time, quota per job), and the merge option (you merge by default; optionally Planwharf merges low-risk pull requests that pass every check). [Sheet](../native/porting/jobs/22-pipeline-run.md) written from [the design](DESIGN-PIPELINE.md); implementation waits for review | `feat/pipeline-run` | Sheet done (#146) |
| 23 | 2. Modules with no dependencies | Sheet for the rest of phase 2: `plan.js`, `quota.js`, `models.js`, `sentinel.js`, `invocations`, `notebook-file`, `project-checks`, `project-workspace`. [Sheet](../native/porting/jobs/23-phase2-rest.md); the port is job 24 and waits for review | `port/23-sheet` | Done (#147) |
| 24 | 2. | Port them, as sheet 23 says | `port/24-phase2-rest` | Done (#158) |
| 25 | Protocol and pipeline | Build Planwharf's own MCP tools for workers, as `native/porting/jobs/18-mcp-protocol.md` says (the sheet of job 18) | `feat/mcp-build` | Done (#172) |
| 26 | Protocol and pipeline | Build the acceptance tests and the gate, as `native/porting/jobs/19-gate.md` says (the sheet of job 19) | `feat/gate-build` | Done (#179) |
| 27 | Native app | Three chat modes, one click apart: **Talk** (only talk with the lead, to think ideas through), **Plan** (the lead shapes a goal for the crew; you start it) and **Auto** (the crew starts by itself). Sheet below; the switch makes room for a fourth position, Pipeline, added by job 34 | `feat/chat-modes` | Done (#186); Auto removed afterwards, Talk and Plan remain |
| 28 | Native app | **Pull requests**, a panel like Team, Progress and Usage in place of the Check PRs button: the open pull requests with their checks and conflicts, the history of merged and closed ones to analyse, and Fix in the background per pull request. Sheet below | `feat/pr-panel` | Done (#193) |
| 29 | Native app and engine | **Saving modes**, per project: **Normal** (as today: Planwharf picks the models), **Safe** (no flagship models of the AI companies: only their mid and light models) and **Ultra safe** (light models only), for the lead and the crew. Sheet below | `feat/saving-modes` | Done (#196) |
| 30 | Native app and engine | **Add to this work**: while the crew works, an addition agreed in the chat becomes one more step of the running work, before Verify, instead of waiting for the next goal. Sheet below | `feat/add-to-work` | Done (#200) |
| 31 | Native app | **Clearer screens**: Work finished says first what was done, whether it works and what to do now; steps and events in plain words, the technical text in the details; larger, higher-contrast text; Team and Settings with everyday and advanced parts; new screenshots and a README that shows the app right after the diagram. Sheet below | `feat/clearer-screens` | To do |
| 32 | Protocol and pipeline | Build pipeline part 1: queue, intake, spec, job state, as `native/porting/jobs/20-pipeline-intake.md` says (the sheet of job 20) | `feat/pipeline-intake-build` | To do |
| 33 | Protocol and pipeline | Build pipeline part 2: routing, build and review in pairs, a pull request per job, as `native/porting/jobs/21-pipeline-build.md` says (the sheet of job 21) | `feat/pipeline-build-build` | To do |
| 34 | Protocol and pipeline | Build pipeline part 3: several jobs at once, the pipeline view, the merge option, as `native/porting/jobs/22-pipeline-run.md` says (the sheet of job 22); and switching between Chat and Pipeline as [the design](DESIGN-PIPELINE.md#switching-between-the-two) says: the Pipeline position of the switch, Send to the pipeline, Take it to the chat, both running at once. Tests: switch back and forth with work running in each, and after a restart | `feat/pipeline-run-build` | To do |
| 35 | Safety | **Sandbox sheet**: how each worker runs isolated on Linux, macOS and Windows (what it can read and write, the network, the commands that need the owner's OK), built on what the AI tools already offer. Sheet only; the build is a job of its own after review. Sheet below | `docs/sandbox-sheet` | To do |
| 36 | Engine | **Step outcomes**: for every finished step, keep the model, the kind of work, its size, whether Verify accepted it or had to fix it, the tests, the time and the quota used, as the base for choosing models by results later. Sheet below | `feat/step-outcomes` | To do |
| 37 | 3. First level | Write the sheet: `attempts`, `claims`, `project-export`, `execution-next`, `provider-catalog`, `provider-policy`, `model-routing`, `claude`, `claude-usage`, `repository`, `supervisor`, `activity`, `activity-sessions`, `provider-runtime`, and the modules added after the plan: `time-left`, `plan-status`, `provider-status`, `push-email`, `temp-cleanup`, `account-folders`, `phone-voice`, `phone-activation` (split in two jobs if it is too large) | `port/37-sheet` | To do |
| 38 | 3. | Port them, as sheet 37 says | `port/38-phase3` | To do |
| 39 | 4. Processes and providers | Write the sheet: `runner` (start and stop the AI programs, read their output, close process trees on Windows), `provider-models`, `external-adapters`, `api-agent`, `provider-adapters`, `provider-connections`, `antigravity`, `planUsage`, and the modules added after the plan: `worktrees`, `plan-file`, `pr-check`, `phone`, `phone-ollama`, `phone-handover`, `phone-handover-live`, `phone-activation-check`; the tests use the fake AI programs of job 03 | `port/39-sheet` | To do |
| 40 | 4. | Port them, as sheet 39 says. Then **stop**: the owner tests on Windows with the real AI programs | `port/40-phase4` | To do |
| 41 | 5. Execution | Write the sheet: `execution.cjs`, `project-home`, and a proposal for splitting the orchestrator by concern (team and models, job building, the lead's questions, state, order parsing) | `port/41-sheet` | To do |
| 42 | 5. | Port them, as sheet 41 says. Then **stop**: the owner approves the orchestrator split | `port/42-phase5` | To do |
| 43 | 6. Orchestrator | Write the sheets for the orchestrator, one per part of the approved split | `port/43-sheets` | To do |
| 44+ | 6. | One job per part, then one that puts them together and passes the orchestrator tests | `port/44-…` | To do |
| — | 7. The native app runs the C# engine | First as an option for at least one release, then the default; Node leaves the zip | — | To do |
| — | 8. Removal | **Stop first:** a full comparison of the native app with the web app and Electron, in the code and on screen (pictures of both with sample data), and the list of what the native app still lacks, with a proposal; the owner decides. Then the web app, Electron, the JavaScript engine and `server.cjs` are deleted together | — | To do |
| — | After the port | **Benchmarks**, repeated on small, medium and large tasks: reliability, defects, time and quota, Planwharf against each AI tool alone; no saving claimed that the numbers do not show | — | To do |
| — | After the port | **Models chosen by results**: the step outcomes of job 36 and the benchmarks rank the models per kind of work, once there are enough examples; the owner's roles and limits still decide | — | To do |

Every phase ends with the review done, CI green on Windows, macOS and Linux, and the comparison showing
no difference for its modules. Until phase 7 Planwharf keeps running on the Node engine.

Rules for every job:

- A new branch from the refreshed `origin/main`; never commit or push to `main` (a push to `main`
  makes a release).
- Do not change modules already ported to C# (`model-policy`, `model-roles`, `provider-accounts`,
  `structured-final`, `activity-events`, `skills`, `worker-result`, `browser-settings`,
  `atomic-rename`, `provider-refusal`, `provider-diagnostics`, `briefing`, `project-history`,
  `activity-evidence`, `autonomy`) unless the job says so; if one must change, record its comparison
  cases again and change the C# in the same branch (porting rule 12).
- The web app and Electron get no new features (they are being retired). A change there is allowed
  only where the job says so, to keep them working.
- Tests that do not depend on the computer (porting rule 11), and the full engine CI green:
  `node scripts/engineering/ci.cjs`.
- English in code, comments, tests and commit messages.
- A new engine module gets its phase in [native/porting/PLAN.md](../native/porting/PLAN.md) in the same branch,
  so the port does not leave it behind.
- Update [BUILT-WITH-PLANWHARF.md](BUILT-WITH-PLANWHARF.md) in the same branch: the job's row (pull
  request number left as `#?`), and the progress numbers when modules are ported.

---

## Job 05. The work plan file, and checks before the crew starts

**Why.** The crew must not depend on a model remembering the plan. Three things went wrong:
a worker created its branch from the job 04 branch although the plan said `origin/main`; after a work
finished, the chat offered the same goal again with "Let the crew do it" turned on; and nothing stopped
the crew from starting in a folder with uncommitted changes of another job.

**What to build** (engine first, then the native app; the web app only as noted):

1. **The plan file.** When work starts (`startAutoTask` and `approve` in `engine/orchestrator.js`),
   the engine writes `.planwharf/plan.md` in the project folder. It is kept out of git without
   touching the project's `.gitignore`: add `/.planwharf/` to `.git/info/exclude` when it is not
   there. The file says, in this order:
   - the goal (the text the user approved);
   - the base: the remote default branch and its commit at start (for example `origin/main 8f4029e`);
   - the work branch (see point 2);
   - the steps, one per line with a check box: `- [ ] Build (Grok 4.7 Business)`;
   - the rules: no push or merge, files outside the goal are not touched;
   - a "Done" section, empty at start.
   One file per project; a new goal replaces the old file, and the old one is kept as
   `.planwharf/done/<date>-<short goal>.md`.
2. **The engine creates the branch, not the worker.** Before the first step: `git fetch`, then
   `git switch -c <branch> origin/<default>`. The branch name comes from the lead's plan when it names
   one (for example `port/04-briefing-refusals-autonomy`), else `planwharf/<short-goal>`. When the
   branch already exists (a work resumed), switch to it and check that it contains the base commit.
3. **Checks before the crew starts.** Do not start, and say why in the chat in one sentence with what
   to do, when:
   - the folder has uncommitted changes ("Commit or stash the changes in the folder first: …, then
     press Let the crew do it again.");
   - the remote cannot be read (offline): start anyway on the current commit, and say so in the plan
     file and in the chat;
   - the folder is not a git repository: no branch, no check, as today.
4. **The file follows the work.** When a step ends verified, the engine ticks its box and adds the
   commit (or "not committed"). When the work ends, the "Done" section gets the summary, the branch,
   and whether it was pushed.
5. **The lead reads the file.** Every question to the lead in that project includes the plan file
   (in the briefing, `engine/briefing.cjs` is ported: add it in `orchestrator.js` where the briefing is
   built, not in `briefing.cjs`). The lead's instructions say: read the plan file; never suggest again a
   goal that is in "Done"; after a stop, continue from the first step not ticked.
6. **No finished goal offered again.** In the native app (`TeamViews.GoalReady`, `MainWindow.Chat.cs`)
   and, as a fix, in the web app (`chat-ui.js`, the "Nothing has started yet" card): a "Suggested next
   goal" from a lead reply older than the start of the last work is not offered.
7. **Show it.** The native app's Progress panel gets a "Work plan" line that opens the file.

**Tests.** A new `engine/plan-file.test.cjs` with a real temporary git repository and a bare remote:
the file is written with base, branch and steps; `.git/info/exclude` gets the line once; the branch is
created from `origin/main` even when the folder was on another branch; a dirty folder blocks the start
with the message; an existing branch is reused; a step done ticks its box; a finished goal goes to
"Done" and to `done/`. Native: a test for point 6.

---

## Job 06. The Push button pushes the branch where it belongs

**Why.** The crew's branch was created from `origin/main` and kept it as its upstream, so the Push
button failed: "The upstream branch of your current branch does not match the name of your current
branch". Git then suggests `git push origin HEAD:main`, which would push to `main` and make a release.

**What to build.** In `server.cjs`, route `/api/repository/commit` with `push`:

1. Push the current branch to a branch of the same name: `git push -u origin HEAD`, which also fixes
   the upstream for later pushes.
2. Never push the remote default branch from the button: when the current branch is `main` (or the
   default branch), refuse with "This is main: Planwharf does not push it. Push it yourself if you
   mean to." The native app shows the message as it shows a push error today.
3. Never show git's own advice in the error (it suggests `HEAD:main`): show the first line of git's
   error and "Push failed. The commit is saved; nothing was sent."

**Tests.** In `engine/commit-push.test.cjs`, with a bare remote: a branch whose upstream is
`origin/main` is pushed to its own name and gets that upstream; on `main` the push is refused and the
remote's `main` is unchanged.

---

## Job 07. Remove the Review role

**Why.** Review never runs in normal work (only when a chat message is classified as an audit), and
the Team panel shows it as if it did. The owner decided to remove it.

**What to build:**

1. `server.cjs`: a chat message always makes an implementation task (remove the audit
   classification). The proposal route keeps accepting `audit`, for work saved before.
2. Native app: remove the Review chip of each model, Review from "All / None", and "Review is
   optional" from the Team text; a model counts as benched when it has none of lead, Build and Verify
   (`TeamView.cs`, `MainWindow.Team.cs`); `ModelsFor` and `ToolsNeedingOk` no longer look at Review.
   Old work with an AUDIT step still shows "Review".
3. Web app, to keep it consistent (allowed: it removes a control that does nothing): the same
   removals in `chat-ui.js`.
4. Do not change `engine/model-roles.cjs` (ported): the role stays in the engine, unused; it goes when
   the orchestrator is ported.

**Tests.** Native `TeamViewTests`: a model with only `reviewer` is on the bench, no Review chip; engine:
a chat message with "review" makes an implementation task.

---

---

## Job 08. An estimate of the time left

**Why.** The owner wants to know, while the crew works, about how long is left before the work ends,
to decide whether to wait, stop for the night, or move models.

**What to build:**

1. **Remember how long steps take.** When a step ends (`collect` in `engine/execution.cjs`), the engine
   appends one line to `step-times.jsonl` in its data folder: role (Build, Verify), model id, size or
   weight of the part when known, number of files changed, minutes from start to end, and whether it
   ended done or failed. Keep the last 500 lines. No prompt, path or project name in it.
2. **Estimate.** A new `engine/time-left.cjs`, data only (it will be ported, so no files or clock inside:
   the caller passes the history, the steps and the current time):
   - a step waiting: the median of the past steps of the same role and model, else of the same role,
     else 20 minutes for Build and 10 for Verify;
   - a step working: that median minus the minutes already spent, never under 2 minutes; past the
     median, "longer than usual";
   - steps that run at the same time count once (the longest); steps in sequence add up;
   - the result: minutes left, the expected end time, and how sure it is ("rough" with fewer than 5 past
     steps of that role, else "based on N past steps").
3. **Show it.** The engine adds `timeLeft` to the work in the snapshot. The native app shows it in the
   chat under "The crew is working" and in Progress: "About 25 minutes left (ends around 17:40) ·
   rough". Hidden when no step is running or waiting. The web app does not get it (frozen).
4. Do not change modules already ported to C#.

**Tests.** `engine/time-left.test.cjs`: no history (defaults), history of the same model, a working step
past its median, parallel steps counted once, sequence added. The history file: a line per step, cut at
500 lines. Native: the line shown and hidden.

---

## Job 09. The native app updates itself

**Why.** Today "Update available" opens the releases page and the owner downloads the zip by hand.

**Start from** `docs/drafts/NativeUpdater.cs`, a draft that is not built yet: it finds the latest
release, downloads `Planwharf-native-<rid>-<tag>.zip`, checks its SHA-256 against the digest GitHub
publishes, unpacks it next to the installed folder in `.planwharf-update-<tag>`, and starts the new
copy with `--finish-update <installed folder> <pid>`; the new copy waits for the old one to close,
copies itself over the installed folder, starts it again and removes the staging folders.

**What to build:**

1. Move the draft to `native/Planwharf.Native/Core/NativeUpdater.cs` and make it build with no warning.
2. `Program.cs`: handle `--finish-update` before the window opens.
3. Settings → Updates: "Update now" when a newer release exists, with progress and a clear error that
   leaves the installed app as it was (bad digest, no write access, offline).
4. Before quitting for the update, the engine saves a checkpoint of running work (the same as Stop the
   crew); after the restart the owner presses Resume. Refuse the update while a step is running
   unless the owner confirms.
5. Projects and settings are in another folder and are never touched.

**Tests.** Native tests with a local fake release (a zip and its digest served from a temporary folder):
digest wrong → nothing changes; finish step copies over a folder with spaces and non-ASCII names;
staging folders are cleaned. Runs on Windows, macOS and Linux (rule 11).

---

## Job 10. Tools chosen per model

**Why.** The tools (MCP, Browser, Hooks, Memory) are set per project for every model at once. The owner
wants them per model: for example Browser for one model only.

**What to build:**

1. The engine keeps, per project, the tools of each model: `store.projectTools[projectId]` becomes
   `{ default: {mcp, browser, hooks, memory}, models: { <modelId>: {...} } }`. A saved project of the old
   shape reads as `default` and is saved again in the new shape.
2. A worker or the lead gets the tools of its own model, else the project default (where the tools
   reach the launch today: `engine/claude.cjs`, `engine/provider-runtime.cjs`, `engine/runner.cjs`).
3. `POST /api/projects/tools` accepts an optional `modelId`.
4. Native Team panel: each model card gets a "Tools" fold with the four boxes and "Same as the project".
   The web app does not get it (frozen); it keeps working with the default.

**Tests.** Engine: old shape migrated; a model with its own tools launches with them, another with the
default; the lead gets its own. Native: the fold reads and saves.

---

## Job 11. An alert on the computer when work ends

**What to build** (native app only): when a work ends (done, failed, or stopped waiting for the owner),
a system notification ("Planwharf: <goal> is done" / "needs you"), the taskbar icon flashes until the
window is opened, and a short sound. Settings → Look gets "Alerts: notification, flash, sound", each on
or off, all on by default. Windows, macOS and Linux, with what each system offers; no NuGet package if
the base library or Avalonia covers it.

**Tests.** The decision of when to alert (done, failed, needs you; not on every step) as a pure function
with tests; the system calls stay thin.

---

## Job 12. Alerts and commands on the phone

**What to build:** two channels, each optional, set in Settings → Phone:

1. **ntfy** (no account): the owner picks a private topic name; Planwharf posts to `https://ntfy.sh/<topic>`
   when work ends, fails, or needs the owner. The owner installs the ntfy app and subscribes.
2. **Telegram bot**: the owner creates a bot with BotFather and pastes its token; Planwharf keeps it in
   the system's secure store when there is one, else in a file readable only by the user, never in the
   project. Planwharf answers only the chat that sends the start code shown in Settings. Messages: the
   same alerts, plus the estimate of job 08. Commands: `/status` (each project: working, steps, time
   left), `/stop <project>` (Stop the crew, after "yes"), `/resume <project>`.
3. The engine runs the channels (they must work with the window closed but Planwharf running).
   Never send prompts, code, file names or paths: only project name, goal title, status and times.

**Tests.** Engine tests with a fake HTTP server for ntfy and Telegram: what is sent, nothing private in
it, a message from another chat is ignored, `/stop` asks for "yes".

---

## Job 13. The logo follows the main colour; the plan file can be kept in git

1. The native logo (the compass, `Look.cs`) takes the main colour chosen in Settings → Look instead of
   staying green. Test: the logo's colour after changing the main colour.
2. Settings gets "Keep the work plan in git" (off by default). When on, the engine of job 05 does not
   add `/.planwharf/` to `.git/info/exclude` and removes the line it added; the plan file is committed
   with the work. Test in `engine/plan-file.test.cjs`.

   Planwharf marks new exclusion rules so that it can remove only its own. Older unmarked rules and
   project or global ignores remain untouched; if one still ignores the plan, the user must adjust it
   before git can include the plan. Turning the option off does not untrack an already tracked file.

---

## Job 14. Review the commit "16 failing test files"

The commit `02a4c80` on `fix/failing-tests-review` fixes engine tests that failed on the owner's Windows
computer, but it changes `server.cjs`, Electron and engine files together.

1. Run `node --test` on refreshed `origin/main` and list what fails, with the reason.
2. Take from `02a4c80` only what fixes a failure that still happens, one fix per commit, with the
   reason in the message. A change to the program (not to a test) needs its reason written in the
   pull request. Electron: only if a test of it fails.
3. Do not take the rest. Full engine CI green.

---

## Job 15. Each parallel worker in its own copy of the folder

**Why.** Parallel parts share one project folder today and are kept apart only by the files each part
may change: a worker that steps outside its files overwrites another's work. With a git worktree per
part, each worker has its own folder and branch, and the parts are joined at the end.

**What to build:**

1. When a work has two or more parts that run at the same time, the engine creates one worktree per part
   from the work branch (job 05): branch `<work branch>--part-<n>`, folder outside the project folder,
   in Planwharf's data folder (`worktrees/<project>/<part>`), so the project folder is not cluttered.
   A work with one part, and every Verify step, runs in the project folder as today.
2. The worker of a part runs in its worktree; its scope check (`scopeCheck` in `engine/execution.cjs`)
   still applies there.
3. When the parts are done, the engine merges each part branch into the work branch, in order, with
   `git merge --no-ff`. On a conflict it stops the merge (`git merge --abort`), and the integration part
   (or a new one when there is none) gets the conflict to resolve: which branches, which files.
4. The worktrees and part branches are removed when the work is complete; kept when it fails, so the
   owner can look, and removed by "Drop this work". On start, worktrees of works that no longer exist
   are cleaned (`git worktree prune`).
5. A folder that is not a git repository runs as today, all parts in the one folder.

**Tests.** `engine/worktrees.test.cjs` with a real repository: two parts get two worktrees on their own
branches; their commits are merged into the work branch; a conflict is handed to the integration part
and leaves the work branch clean; worktrees are removed at the end and kept on failure; a non-git
folder runs as today. Paths with spaces and non-ASCII names (Windows).

---

## Job 16. More than one account per company, and plans that expired

**Why.** The owner may have two subscriptions of one company (two Claude accounts) and wants the second
used before switching company. And a plan can expire while its program stays installed and signed in:
today its models stay in the list and a worker fails later with an unclear error.

**What to build:**

1. **Plan status.** For each account, the engine keeps a status: active, expired, or not signed in. It is
   read with the quota (`engine/claude-usage.cjs`, `engine/planUsage.js`, the Antigravity usage) and from
   a refusal that says the subscription or plan is not active. Each program says it differently: handle
   each one on its own, from real messages recorded in `native/porting/fakes/` (job 03), and keep
   "unknown" when a program does not say.
2. **Models of an expired or signed-out account** are never assigned and are hidden in the Team panel,
   with one line at the bottom: "4 models hidden: Claude plan expired. Renew it or sign in again, then
   look for models again." Looking for models again reads the status again.
3. **Several accounts of one company**, where the program allows a separate settings folder
   (Claude Code: `CLAUDE_CONFIG_DIR`; Codex: `CODEX_HOME`). Settings → AI tools → "Add another account":
   Planwharf makes the folder and shows the exact sign-in command to run with it. Each account has its own
   quota, limit (the company limit of #103 applies to each account) and status. Where the program has a
   single account, the button is not shown.
4. **Switching:** when an account is under its limit, a step moves first to the same model on another
   account of the same company with room, then to another company, as the quota switch does today.
5. No credential, token or email is read or stored: only the settings folder path and what the usage
   call returns (porting rule, `engine/provider-accounts.cjs` keeps this rule).

**Tests.** Plan status from recorded expired, signed-out and active answers of each program; hidden models
and the line; two Claude accounts with their own quota and a switch to the second before another company;
nothing private stored.

---

## Job 17. The native app in English and Italian

**Why.** The app is in English. The owner wants an Italian version too, chosen in Settings; English stays
the default.

**What to build** (native app only; the web app and Electron are frozen):

1. **One place for the words.** `native/Planwharf.Native/Strings.cs`: every text the native app shows
   (buttons, titles, hints, explanations, tooltips, dialogs, errors written by the app) gets a key, with
   its English and its Italian text, in two dictionaries. No reflection and no NuGet package (Native
   AOT): plain C# dictionaries, and a `T("key")` lookup, with placeholders like `{0}` for numbers and
   names. Every text written in the app's code today moves there; the English text stays exactly as it is.
2. **The Italian text.** Natural Italian, short as the English, with the same capital letters, the
   product names unchanged (Planwharf, Lead, Build, Verify, Team, Push and the names of the AI programs and
   models stay as they are, since the engine and the docs use them).
3. **Settings → Look → Language:** English, Italiano. Saved in `native-window.json` with the theme and the
   colours (`Prefs.cs`); the window is built again at once, as for the theme. English when nothing is saved.
4. **Dates and times** follow the chosen language (days and months in Italian).
5. **Texts that come from the engine** (statuses, reasons, the lead's and the crew's answers) stay as the
   engine writes them in this job. List in the pull request the engine sentences the owner sees most, for
   a later job.
6. Local chat commands already understand Italian ("apri team", "mostra impostazioni"): keep them, in
   both languages.

**Tests.** The two dictionaries have the same keys and the same placeholders; no key is empty; the
window draws in Italian (a headless picture like the existing ones) with no text cut off; the language is
saved and read again; a test that fails when the app's code shows a literal English sentence instead of a
key (a scan of the UI files for text given straight to `Text =`, `Content =`, tooltips and dialogs).

## Job 27. Three chat modes, one click apart

**Why.** The chat does three different things today, and switching between them takes a trip to
Settings or a typed order: thinking an idea through with the lead, shaping a goal for the crew, and
letting the crew go on by itself. The owner wants to pick one with one click, in the chat.

**What.**

1. Above the chat box, a three-way switch, per project, saved, with a one-line hint under it:
   - **Talk:** only a conversation with the lead, to think ideas through. The lead does not suggest goals
     for the crew, and no "Use as the goal" or "Let the crew do it" is offered. Nothing starts.
   - **Plan** (the default, today's behaviour): the lead shapes a goal for the crew; you press **Let the
     crew do it**.
   - **Auto:** the crew starts the goal by itself, and goes on with the next one within the limits of
     [project autonomy](PROJECT-AUTONOMY.md). It is today's automatic mode with the project's autonomy on,
     in one click; it lasts the session, as autonomy does now.
2. Switching never stops running work. Leave room for a fourth position, **Pipeline**, that job 34 adds
   ([switching between the two](DESIGN-PIPELINE.md#switching-between-the-two)); until then it is not shown. Moving to **Talk** or **Plan** while the crew works ends Auto
   after the current goal; **Stop the crew** stays the way to stop at once.
3. The chat orders still work (`mode auto`, `mode manual`), and `mode talk` / `modalità chat` and
   `mode plan` select the other two; the lead's answer and the switch always agree.
4. The phone bot's `/status` says the mode of each project.
5. English and Italian for every new text (the presentation-literal guard of job 17 must pass); the user
   guide and [the chat view](CHAT-VIEW.md) describe the three modes.

**Tests.** Engine: in Talk the lead's prompt forbids goal suggestions and an answer that still carries one
creates no proposal; Plan behaves as today; Auto approves and starts a proposal by itself and stops at the
autonomy limits; the mode is saved per project and read again after a restart (Auto returns as Plan, since
autonomy lasts one session); switching during running work does not stop it. Native: the switch shows the
saved mode, and each click calls the engine once.

## Job 28. The Pull requests panel

**Why.** Check PRs is a button that answers once in a dialog. The owner wants the project's pull requests
where the other panels are, with their history, to see how the work went and what keeps failing.

**What.**

1. **Pull requests** in the top bar, next to Team, Progress, Usage, Files and Settings, opens a side
   panel; it replaces the Check PRs button.
2. **Open:** each open pull request with its number, title, branch, when it was opened and last updated,
   its checks (each one green, red or running, by name), whether it has conflicts, and **Open on GitHub**.
   A pull request with conflicts or red checks has **Fix in the background** (as Check PRs does now, one
   pull request at a time) and shows the fix while it runs, then its result.
3. **History:** the merged and closed pull requests of the project, newest first, 30 at a time with
   **Show more**: number, title, merged or closed, when, how long from opened to merged, and whether
   Planwharf fixed it in the background.
4. **What Planwharf knows of its own:** for a pull request opened by the Push button, the work behind it
   (its goal, its time, the lead and the models of its steps), from the work record. The engine keeps the
   pull request number on the work when it opens it.
5. **To analyse:** at the top, for the last 30 days: pull requests merged, the median time to merge, how
   many needed a fix, and the checks that failed most often.
6. **Filters:** open, merged, closed; opened by Planwharf or by others.
7. Everything comes from `gh` (no token kept by Planwharf); without `gh` the panel says what to install,
   and the rest of the app works as before. English and Italian for every text.

**Tests.** Engine: the list and the history read from a fake `gh` (opened, merged, closed, with and without
checks), paging, the numbers of the summary, the link from a pull request to its work, no `gh`. Native: the
panel draws the three states, the fix shows while it runs, and the filters change the list.

## Job 29. Saving modes: Normal, Safe, Ultra safe

**Status.** Done, waiting for review. Not pushed.

**Why.** The owner wants to spend fewer tokens and less quota when the work allows it, with one choice,
instead of changing the roles of every model in Team.

**What.**

1. A choice per project, saved, in Team at the top (and in the chat orders `mode normal`, `mode safe`,
   `mode ultra safe`, `modalità sicura`, `modalità ultra sicura`):
   - **Normal** (the default, today's behaviour): Planwharf picks the models as it does now (light parts
     to light models, a strong model for Verify, the lead by ranking).
   - **Safe:** no **flagship** model of any company, for the lead and the crew: only mid and light models.
   - **Ultra safe:** only light models, for the lead and the crew.
2. The class of a model is its place in its company's line-up, the same one Planwharf already uses to rank
   the leads (`leadTier` in `engine/orchestrator.js`):
   - **Flagship (high):** each company's top model, for example Claude Fable and Opus, GPT-6 Astra, Grok 4.7,
     Gemini Pro; a model Team weighs premium.
   - **Mid:** Claude Sonnet, GPT-6.1 Sol and Terra, the companies' default models.
   - **Light (low):** Claude Haiku, Gemini Flash, GPT mini, nano or Luna, the light ones.
   The effort of a run ("(High)", "medium", "low" in a variant's name) does not change the class: Gemini 3.8
   Flash (High) is a light model. A weight the owner sets in Team takes precedence.
3. The mode limits every choice: the proposal, the lead's split and crew choice, every switch and
   replacement (quota, refusal, no result), the lead hand-over and the background fix of a pull request.
   The quota limits still hold inside it.
4. Nobody allowed: the step does not start (or the work stops with a checkpoint), and the chat says which
   models the mode leaves out and which are under their limit, as for the quota limits. The lead, with no
   model allowed, keeps the best one of the lowest class available and says so, so the chat never goes
   silent.
5. Verify in Safe uses a default model; in Ultra safe a light one verifies (the "strong model for Verify"
   rule gives way to the mode, and the chat says so once).
6. The mode shows next to the project name, in the Crew at work card, and in the phone messages and
   `/status`. Changing it never stops running work: it applies from the next step.
7. English and Italian for every new text; the user guide describes the three modes.

**Tests.** Engine: in each mode the proposal, the split, a quota switch, a refusal replacement and the lead
hand-over pick only allowed classes; flagship, mid and light are classed as `leadTier` ranks them, an effort variant keeps its model's class, and a weight set in Team wins; nobody
allowed stops the work with the reasons; the mode is saved per project and read again after a restart;
changing it during running work does not stop it. Native: the choice shows the saved mode and each click
calls the engine once.

## Job 30. Add to the running work

**Why.** While the crew works, the owner can talk with the lead, but an addition only becomes a "Suggested
next goal" that waits until the work ends: the crew at work never gets it. The owner wants to add it to the
running work.

**What.**

1. While a work runs (not in Talk), the lead's answer about something to add ends with one line that
   starts exactly with "Addition to this work:" and the addition in one or two sentences, instead of a
   suggested next goal. Under that answer the chat shows **Add to this work** (and the phone does not
   start anything by itself).
2. **Add to this work** appends one Build step to the running work, before Verify, with the addition as
   its detail and the files it may change when the lead names them. It follows every rule of the other
   steps: the project's team and roles, the quota limits and the saving mode (job 29), the lead's choice
   of model. It starts as soon as a model is free; a step already running on the same files goes first.
3. Verify checks the whole work, the addition included. If Verify already started, it is run again after
   the new step, on the same branch.
4. The work plan (`.planwharf/plan.md`), the Crew at work card, the time left and the phone messages show
   the new step; the chat says "Added to this work: …" with the model.
5. A stopped work takes the addition too and resumes from its checkpoint with the new step; a finished
   work does not (the addition is a new goal, as today).
6. English and Italian for every new text; the user guide says how to add to a running work.

**Tests.** Engine: an addition during running work adds one step before Verify and starts it on an allowed
model; Verify already running runs again after it; two steps on the same files run one after the other; a
stopped work resumes with the addition; a finished work turns it into a new goal; Talk never offers it.
Native: the button shows only under an addition while work runs, and one click calls the engine once.

## Job 31. Clearer screens

**Why.** The look of Planwharf works (the dark theme, the mint green, the panels, the Crew at work card); the
way it speaks does not always: some screens read like a report for a developer, not news for the captain.
Nothing here changes the identity of the app: it changes the order of what is shown, and the words.

**What.**

1. **Work finished**, first. At the top, three lines and nothing else:
   - **What was done**, in one or two plain sentences written by the lead from the results (not the raw
     worker reports);
   - **Does it work**: the tests and the Verify verdict, in one line (green, or what failed);
   - **What to do now**: try it, Push, answer a decision, or fix a failed check, with the button for it.
   Below, closed by default and opened with one click: the steps and their models, what each worker reported,
   tests, files changed, git state, time and usage ("What this work used").
2. **Steps and events in plain words.** Each step shows one short line the lead writes for it ("Opus is building
   the weather page"); the instruction given to the worker stays in the step's details. The event list joins
   repeated lines, shows one short line per event, and keeps the long text behind "more".
3. **Readable text.** Slightly larger body text and higher contrast for secondary text (events, side panels),
   in both themes; no text under the size the app uses for its body.
4. **Team:** one short sentence per role and a simpler model card (name, quota, role buttons); the limit, the
   tools and the account details open on demand. **Settings:** everyday settings first (folders, language, look,
   phone, updates); advanced ones (MCP, hooks, memory, browser, permissions) in a section closed by default,
   each with one plain sentence that says what it is for.
5. **Status in a few words.** The same set of icons everywhere for working, waiting, needs you, done, failed;
   short reassuring progress lines; a decision that needs the owner stands out. The sea voice stays in the
   messages (the phone, the alerts), not as decoration.
6. **New screenshots and the README.** New pictures of today's app on a sample project with sample data (the
   headless pictures the native tests already make), with the version they show. In the README the **diagram
   stays first**, then **one large screenshot of the app**, then the rest; the Team, Usage and Settings pictures
   one under the other (not side by side), each readable on a phone.
7. English and Italian for every new text; the user guide follows the new screens.

**Tests.** Native: Work finished shows the three lines first and the details closed; a step shows its plain line
and keeps the worker instruction in its details; repeated events are joined; Settings shows the advanced
section closed; text sizes and contrast stay above the minimum in both themes; the screenshots are made from
sample data and contain no real paths. Engine: the lead's plain summary and step lines are produced and kept
short, with a fallback when the lead gives none.

## Job 35. The sandbox sheet

**Why.** Workers start inside the project folder, but that is a check on where they start, not an
operating-system barrier: a model that decides to can read or delete files outside the project, read
credentials, or run a dangerous command. The owner wants each worker isolated, with risky commands asking
first, and no way around it.

**What the sheet covers** (a sheet only, in `native/porting/jobs/35-sandbox.md`; the build is a later job
after the owner's review):

1. What a worker may do: read and write in its worktree; read the project's own toolchain; the network
   only when the work needs it; nothing in the owner's home, other projects, or credential stores.
2. How, on each system: Linux (bubblewrap or namespaces), macOS (sandbox-exec profiles), Windows
   (AppContainer or a low-rights user), and what each AI tool already enforces (Codex's sandbox modes, Claude
   Code's permission modes, Grok and Antigravity), so Planwharf uses one consistent level whatever the tool.
3. Commands that need the owner's OK (deleting outside the worktree, installing system packages, network
   to unknown hosts), how the request reaches the owner (chat, phone) and what happens with no answer.
4. What stays allowed today so nothing breaks (tests, builds, git on the worktree), how to measure the cost,
   and how the owner turns it off per project if a tool cannot work inside it.
5. Tests the build will need, on the three systems, including a worker that tries to escape.

## Job 36. Step outcomes

**Why.** Choosing models by real results needs data first. Planwharf already keeps the time of each step;
it does not keep how the step went.

**What.** For every finished step, one line in an outcomes file in Planwharf's data folder: the model, its
company and class, the role, the kind and size of the work, whether Verify accepted the step as it was or had
to fix it (and how much), the tests reported and passed, a refusal or a switch, the time and the quota used.
Nothing about the code itself, no file names or paths. **Usage** shows a small summary per model (steps,
accepted as they were, fixed by Verify). Nothing is chosen from it yet: that waits for enough examples and
the benchmarks, after the port.

**Tests.** One line per finished step with every field; a step that was switched or refused is recorded as
such; no path or code in the file; the summary counts match the lines.
