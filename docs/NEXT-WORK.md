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
6. In the same branch, change the job's status in the list below to **"Done, waiting for review"**,
   so that once merged, the next reading starts from the next job.
7. Commit on the branch. Do not push and do not merge: the owner pushes with `git push -u origin HEAD`
   and asks for a review.
8. Stop and ask the owner when a phase says so, when the two engines cannot be kept equal, or when a job
   needs a decision.

## The list

Jobs are numbered in order; the porting jobs 01 to 04 come first.

| # | Phase | Job | Branch | Status |
|---|---|---|---|---|
| — | 0. Foundations | .NET 10, engine and test projects, porting rules, README | — | Done (#61, #79) |
| — | 1a. Test bench | Fake AI programs (job 03) | — | Done (#86) |
| — | 1b. Test bench | Module-by-module comparison with the JavaScript | — | Done (#81) |
| — | 2. Modules with no dependencies | Jobs 01, 02, 04: skills, worker results, structured final, model roles and policy, accounts, browser settings, activity events, refusals, diagnostics, briefing, project history, evidence, autonomy | — | Done (#64, #74, #90) |
| — | ARM builds | Windows and Linux ARM | — | Done (#80) |
| 05 | Fixes before going on | The work plan file, and checks before the crew starts (sheet below) | `fix/plan-file` | To do |
| 06 | Fixes before going on | The Push button pushes the branch where it belongs (sheet below) | `fix/push-button` | To do |
| 07 | Fixes before going on | Remove the Review role (sheet below) | `fix/remove-review` | To do |
| 08 | 2. Modules with no dependencies | Write the sheet for the rest of phase 2: `plan.js`, `quota.js`, `models.js`, `sentinel.js`, `invocations`, `notebook-file`, `project-checks`, `project-workspace` | `port/08-sheet` | To do |
| 09 | 2. | Port them, as sheet 08 says | `port/09-phase2-rest` | To do |
| 10 | 3. First level | Write the sheet: `attempts`, `claims`, `project-export`, `execution-next`, `provider-catalog`, `provider-policy`, `model-routing`, `claude`, `claude-usage`, `repository`, `supervisor`, `activity`, `activity-sessions`, `provider-runtime` (split in two jobs if it is too large) | `port/10-sheet` | To do |
| 11 | 3. | Port them, as sheet 10 says | `port/11-phase3` | To do |
| 12 | 4. Processes and providers | Write the sheet: `runner` (start and stop the AI programs, read their output, close process trees on Windows), `provider-models`, `external-adapters`, `api-agent`, `provider-adapters`, `provider-connections`, `antigravity`, `planUsage`; the tests use the fake AI programs of job 03 | `port/12-sheet` | To do |
| 13 | 4. | Port them, as sheet 12 says. Then **stop**: the owner tests on Windows with the real AI programs | `port/13-phase4` | To do |
| 14 | 5. Execution | Write the sheet: `execution.cjs`, `project-home`, and a proposal for splitting the orchestrator by concern (team and models, job building, the lead's questions, state, order parsing) | `port/14-sheet` | To do |
| 15 | 5. | Port them, as sheet 14 says. Then **stop**: the owner approves the orchestrator split | `port/15-phase5` | To do |
| 16 | 6. Orchestrator | Write the sheets for the orchestrator, one per part of the approved split | `port/16-sheets` | To do |
| 17+ | 6. | One job per part, then one that puts them together and passes the orchestrator tests | `port/17-…` | To do |
| — | 7. The native app runs the C# engine | First as an option for at least one release, then the default; Node leaves the zip | — | To do |
| — | 8. Removal | **Stop first:** the list of what the native app still lacks compared with the web app and Electron, with a proposal; the owner decides. Then the web app, Electron, the JavaScript engine and `server.cjs` are deleted together | — | To do |

Every phase ends with the review done, CI green on Windows, macOS and Linux, and the comparison showing
no difference for its modules. Until phase 7 Planwharf keeps running on the Node engine.

Not in the plan yet; the owner decides if and when: alerts on the computer when work ends, an estimate
of the time left, phone notifications (Telegram), the native app updating itself, tools chosen per
model, and the commit "16 failing test files" kept on `fix/failing-tests-review`.

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
