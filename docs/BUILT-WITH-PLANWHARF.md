# Built with Planwharf

Planwharf is moving its own engine from JavaScript to C# (see [the plan](NEXT-WORK.md)), and it does
the work itself: the owner gives the lead one line, "Read docs/NEXT-WORK.md and continue.", the lead
picks the next job, a crew of models builds and verifies it, and every pull
request is reviewed before it is merged. Using Planwharf on itself every day also shows where it falls
short; those findings are fixed along the way and listed here.

**Every pull request updates this file**: the job row or the improvement it adds, and the progress
numbers below.

## Progress of the C# engine

| | Now | At the end |
|---|---|---|
| Engine modules ported to C# | **23 of 55** (15 of 55 before this job) | 55 |
| JavaScript engine lines ported | **about 1,856 of 10,300** (18%) | all; the orchestrator alone is about 3,800 |
| Phases done | 0 (foundations), 1 (test bench), 2 (modules with no dependencies) | 0 to 8 |
| C# engine tests | 547, plus 149 comparison cases with the JavaScript | |

Ported so far: `activity-events`, `activity-evidence`, `atomic-rename`, `autonomy`, `briefing`,
`browser-settings`, `invocations`, `model-policy`, `model-roles`, `models`, `notebook-file`,
`plan`, `project-checks`, `project-history`, `project-workspace`, `provider-accounts`,
`provider-diagnostics`, `provider-refusal`, `quota`, `sentinel`, `skills`, `structured-final`,
`worker-result`.

## Jobs done by Planwharf

The crews so far use models from Anthropic, OpenAI, Google and xAI.

| Job | Pull request | What | What the review changed |
|---|---|---|---|
| 01 | #64 | First port: skills, structured final output, worker results | — |
| 02 | #74 | Model roles and policy, provider accounts, browser settings, activity limits | A test found the real Chrome on the macOS runners; made it independent of the machine |
| 03 | #86 | Fake AI programs (claude, codex, grok, agy) that replay recorded answers, 64 tests | Two tests depended on macOS paths and on running as administrator |
| 04 | #90 | Six modules: refusals, diagnostics, briefing, project history, evidence, autonomy; 149 comparison cases | A commit outside the job was left out and reviewed on its own |
| — | #92 | Crew switches use the project's team; a step moves when its model loses its role | Branch rebuilt on main; notes name roles as Team shows them |
| 05 | #97 | The work plan file, and checks before the crew starts | Split from fixes for Windows tests (#98) |
| 06 | #101 | The Push button pushes the branch where it belongs, never main | — |
| 07 | #104 | Remove the Review role | Jobs 05 and 06 marked done in the plan |
| 08 | #110 | An estimate of the time left | — |
| 09 | #114 | The native app updates itself | — |
| 10 | #118 | Tools (MCP, browser, hooks, memory) chosen per model, or the same as the project | — |
| 11 | #118 | An alert on the computer when work ends or needs the owner: notification, taskbar flash, sound, each switchable | — |
| 12 | #118 | Alerts and commands on the phone: ntfy, and a Telegram bot with /status, /stop, /resume | — |
| 13 | #121 | The native logo follows the main colour; optionally keep the work plan in git | Existing unmarked ignore rules remain under the user's control |
| 14 | #122 | Reviewed the failing-test candidate; isolated Git line endings in the email rewrite fixture | Independent Grok 4.6 verification reproduced the origin/main CRLF failure, added error/recovery/concurrency coverage, and ran full engine CI |
| 15 | #124 | Parallel build parts run in isolated Git worktrees; part branches merge in order with conflict handoff and safe cleanup | Independent Grok 4.7 verification added edge, error, recovery and concurrency coverage. Drop now removes dirty and locked worktrees; a missing worktree pauses integration; completion still deletes part branches after the folder is gone |
| 16 | #133 | Multiple accounts per provider, subscription status (active, expired, not signed in), eligible account switching before provider switching, and hiding expired models | Independent Grok 4.7 verification added edge, error, recovery and concurrency coverage. A second account does not inherit this process's login. A live lock is not stolen, and a dead owner is cleared at once. An expired or signed-out plan stays in force when a later quota read is unknown, and the team does not treat those models as a ready lead or builder |
| 17 | #132 | English and Italian in the native app, with a saved language picker, localized dates and a presentation-literal guard | Restored the completed job commit onto `feat/languages` from refreshed main; strengthened placeholder coverage |
| 18 | #138 | Wrote the worker MCP protocol sheet for `ask`, `progress`, `finish`, `decision`, `handoff` and JSON fallback; implementation awaits review | Independent verification named the design limits the sheet left implicit: ask stays in the same session without rereading the project, progress is not guessed from program output, finish fields are checked at once, and a decision is shown highlighted and never cut |
| 19 | #140 | Wrote the engine-run acceptance gate sheet, including pinned checks outside the candidate branch and evidence bound to the exact worktree snapshot; implementation awaits review | Independent verification by claude-sonnet-4-6: the engine runs the checks before review and again before a pull request is ready, and a branch cannot change those checks. Added that only the Planwharf engine may start the gate; a candidate CI workflow, webhook, or other branch process cannot trigger or satisfy it |
| 20 | #142 | Wrote the pipeline intake sheet: queue settings for `planwharf` issues and a local list, intake questions, `spec.md` with acceptance tests, file-backed job state, and the owner-approved manifest for the gate; implementation awaits review | Independent verification pending |
| 21 | #144 | Wrote the pipeline build sheet: routing by risk and parts touched, builder and reviewer pairs, review rounds, gate calls, and one pull request per job; implementation awaits review | Independent verification by Gemini: verified risk-based routing, builder/reviewer pairs, review rounds, gate integration, and single PR rules against sheets 18–20 and the pipeline design. Added comprehensive edge, error, recovery, and concurrency test coverage for quota switches, gate/review return counters, bad trees, invalid table rows, branch name validation, and unexpected PR states |
| 22 | #146 | Wrote the pipeline run sheet: several jobs at once in isolated worktrees within quota, the native pipeline view of stages, time, and quota, and an optional low-risk merge; implementation awaits review | Independent verification by Gemini: verified concurrent worktree isolation, model-id invocation caps, quota thresholds, native view bounds, and merge commit limits against sheets 18–21 and DESIGN-PIPELINE.md. Added comprehensive edge, error, recovery, and concurrency test coverage for schedule locks, worktree/branch collisions, merge crash recovery, drop handling, and forbidden rollbacks. This Grok 4.7 review left that Gemini sentence and commit a50520a unchanged. The archived work plan still checks Verify (claude-sonnet-4-6) on that same commit; this review does not replace either claim. The sheet test now also locks shared quota across projects, no second builder, expired and signed-out plans, cleanup with `git branch -d`, and quoted text that cannot raise a cap or authorize a merge |
| 23 | #147 | Wrote the phase 2 rest sheet for `plan.js`, `quota.js`, `models.js`, `sentinel.js`, `invocations`, `notebook-file`, `project-checks`, and `project-workspace`; implementation awaits review | Independent verification by Gemini: verified parity cases, check commands, deferred dependencies, and file bounds across all eight modules against their JavaScript implementations, tests, and porting rules 1–12. Added comprehensive edge, error, recovery, and concurrency test coverage for schema migrations, note limits, token integer safety, sentinel verdict precedence, worktree containment, git sanitization, and execution isolation. This Grok 4.7 review left that Gemini sentence and commit 61cdb5b unchanged. The archived work plan still checks Verify (claude-sonnet-4-6) on that same commit. That Claude run stopped on quota before review and did not write 61cdb5b. This review does not replace the Gemini claim |
| 18 | #? | Wrote the worker MCP protocol sheet for `ask`, `progress`, `finish`, `decision`, `handoff` and JSON fallback; implementation awaits review | Independent verification named the design limits the sheet left implicit: ask stays in the same session without rereading the project, progress is not guessed from program output, finish fields are checked at once, and a decision is shown highlighted and never cut |
| 19 | #? | Wrote the engine-run acceptance gate sheet, including pinned checks outside the candidate branch and evidence bound to the exact worktree snapshot; implementation awaits review | Independent verification by claude-sonnet-4-6: the engine runs the checks before review and again before a pull request is ready, and a branch cannot change those checks. Added that only the Planwharf engine may start the gate; a candidate CI workflow, webhook, or other branch process cannot trigger or satisfy it |
| 20 | #? | Wrote the pipeline intake sheet: queue settings for `planwharf` issues and a local list, intake questions, `spec.md` with acceptance tests, file-backed job state, and the owner-approved manifest for the gate; implementation awaits review | Independent verification pending |
| 21 | #? | Wrote the pipeline build sheet: routing by risk and parts touched, builder and reviewer pairs, review rounds, gate calls, and one pull request per job; implementation awaits review | Independent verification by Gemini: verified risk-based routing, builder/reviewer pairs, review rounds, gate integration, and single PR rules against sheets 18–20 and the pipeline design. Added comprehensive edge, error, recovery, and concurrency test coverage for quota switches, gate/review return counters, bad trees, invalid table rows, branch name validation, and unexpected PR states |
| 22 | #? | Wrote the pipeline run sheet: several jobs at once in isolated worktrees within quota, the native pipeline view of stages, time, and quota, and an optional low-risk merge; implementation awaits review | Independent verification by Gemini: verified concurrent worktree isolation, model-id invocation caps, quota thresholds, native view bounds, and merge commit limits against sheets 18–21 and DESIGN-PIPELINE.md. Added comprehensive edge, error, recovery, and concurrency test coverage for schedule locks, worktree/branch collisions, merge crash recovery, drop handling, and forbidden rollbacks. This Grok 4.7 review left that Gemini sentence and commit a50520a unchanged. The archived work plan still checks Verify (claude-sonnet-4-6) on that same commit; this review does not replace either claim. The sheet test now also locks shared quota across projects, no second builder, expired and signed-out plans, cleanup with `git branch -d`, and quoted text that cannot raise a cap or authorize a merge |
| 24 | #? | Ported the eight phase 2 rest modules to C#: plan.js, quota.js, models.js, sentinel.js, invocations, notebook-file, project-checks, project-workspace | — |

## Improvements found while Planwharf worked on itself

| Found | Fixed in |
|---|---|
| The lead menu was not ordered from the best lead down | #72, #84 |
| Strong models drained the shared Claude quota: limits on the 5-hour and weekly windows, light parts to light models | #75 |
| A new request started the oldest goal instead of the newest | #73 |
| The lead's own assignment ignored quota limits; "Work stopped" showed after Resume | #85 |
| The commit message offered after a work was too long | #87 |
| The main colour of the app could not be changed | #88 |
| A message pasted on several lines kept only its first line | #91 |
| The quota switch looked at the shared team, not the project's | #92 |
| A worker created its branch from the wrong place; a finished goal was offered again | #97 |
| Engine tests failed on Windows (temp folders still held, locked notebook) | #98 |
| The project list was not ordered by use, so a goal went to the wrong project | #99 |
| Work branches tracked main, so the Push button failed | #100, #101 |
| "The crew is working" stayed after a work ended | #102 |
| Quota limits could be set only per model, not per company | #103 |
| Review never ran and confused the Team panel | #104 |
| Workers could push when autonomy was off; Verify's open questions were buried in its summary | #106 |
