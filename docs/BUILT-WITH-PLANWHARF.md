# Built with Planwharf

Planwharf is moving its own engine from JavaScript to C# (see [the work list](NEXT-WORK.md)), and it does
the work itself: the owner gives the lead one line, "Read docs/NEXT-WORK.md and continue.", the lead
picks the next job, a crew of models builds and verifies it, and every pull
request is reviewed before it is merged. Using Planwharf on itself every day also shows where it falls
short; those findings are fixed along the way and listed here.

**Every pull request updates this file**: the job row or the improvement it adds, and the progress
numbers below.

## Progress of the C# engine

| | Now | At the end |
|---|---|---|
| Engine modules ported to C# | **23 of 71** | 71 |
| JavaScript engine lines ported | **about 1,856 of 14,000** (13%) | all; the orchestrator alone is about 3,800 |
| Phases done | 0 (foundations), 1 (test bench), 2 (modules with no dependencies); next: the acceptance gate (job 26), the chat modes (job 27), the pipeline (jobs 28 to 30), then phase 3 | 0 to 8 |
| C# engine tests | 603, plus 247 comparison cases with the JavaScript | |

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
| 14 | #122 | Reviewed the failing-test candidate; isolated Git line endings in the email rewrite fixture | Reproduced on `main` the line-ending failure; added error, recovery and concurrency coverage |
| 15 | #124 | Parallel build parts run in isolated Git worktrees; part branches merge in order with conflict handoff and safe cleanup | Added edge, error, recovery and concurrency coverage; Drop removes dirty and locked worktrees; a missing worktree pauses integration |
| 16 | #133 | Multiple accounts per provider, subscription status (active, expired, not signed in), eligible account switching before provider switching, and hiding expired models | A second account does not inherit this login; a live lock is not stolen; an expired or signed-out plan stays in force when a later quota read is unknown |
| 17 | #132 | English and Italian in the native app, with a saved language picker, localized dates and a presentation-literal guard | Restored the completed job commit onto `feat/languages` from refreshed main; strengthened placeholder coverage |
| 18 | #138 | Wrote the worker MCP protocol sheet for `ask`, `progress`, `finish`, `decision`, `handoff` and JSON fallback; implementation awaits review | Named the limits the sheet left implicit. **Sheet only: built by job 25** |
| 19 | #140 | Wrote the engine-run acceptance gate sheet, including pinned checks outside the candidate branch and evidence bound to the exact worktree snapshot; implementation awaits review | Only the engine may start the gate; a branch cannot trigger or satisfy it. **Sheet only: built by job 26** |
| 20 | #142 | Wrote the pipeline intake sheet: queue settings for `planwharf` issues and a local list, intake questions, `spec.md` with acceptance tests, file-backed job state, and the owner-approved manifest for the gate; implementation awaits review | **Sheet only: built by job 28** |
| 21 | #144 | Wrote the pipeline build sheet: routing by risk and parts touched, builder and reviewer pairs, review rounds, gate calls, and one pull request per job; implementation awaits review | Checked against sheets 18–20 and the design. **Sheet only: built by job 29** |
| 22 | #146 | Wrote the pipeline run sheet: several jobs at once in isolated worktrees within quota, the native pipeline view of stages, time, and quota, and an optional low-risk merge; implementation awaits review | Checked against sheets 18–21 and the design. **Sheet only: built by job 30** |
| 23 | #147 | Wrote the phase 2 rest sheet for `plan.js`, `quota.js`, `models.js`, `sentinel.js`, `invocations`, `notebook-file`, `project-checks`, and `project-workspace`; implementation awaits review | Checked parity cases, commands and file limits for the eight modules |
| 24 | #158 | Ported the eight phase 2 rest modules to C#: plan.js, quota.js, models.js, sentinel.js, invocations, notebook-file, project-checks, project-workspace | Built in 8 parallel parts, each in its own worktree; a notebook test expected the Windows result everywhere and was made to follow Node on each system |
| 25 | #172 | Built the worker MCP tools `ask`, `progress`, `finish`, `decision` and `handoff` on the C# engine, with the JSON result file kept for programs that have no verified MCP launch; verified independently with edge-case and concurrent-lock tests | Planwharf's server is registered for Claude and Codex whether or not the owner's MCP checkbox is on, and it never turns the owner's own servers on. Grok, Antigravity, the API agent and a configured CLI stay on the JSON file. A vendor CLI was not executed. Concurrency retry added for session locks and audit log |
| 26 | #179 | Built the C# acceptance gate. The owner-approved checks live outside the candidate worktree. The engine runs them before review and again after sync with main. A branch cannot choose or weaken those checks | A snapshot that cannot be pinned stops the gate. A shell command runs only when the owner named it. No model is called. A test script was not executable on Linux and macOS; branch brought up to date with main |

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
| Antigravity's Claude and GPT models were judged by Gemini's quota; quota refusals were raw program errors | #111, #112 |
| A light model was given Verify and failed it | #113 |
| A private email address blocked the push; a decision line was cut from the summary | #115 |
| Your own messages could not be copied or sent again in one click | #117 |
| A stopped work could not resume: the crew's own changes and a moved `main` blocked it | #119, #120 |
| A finished step with no verdict, or no result, stopped the work; a lead that died mid-answer stopped it too | #123, #125, #126, #131 |
| A cache folder blocked the start; a stale lock was obeyed | #127, #139 |
| The phone bot did not report stops, resumes and model switches, and had no buttons | #128, #129, #130, #154 |
| A session stopped after a few objectives; the lead menu followed today's quota | #134, #135 |
| A new work did not start by itself, and a push did not open the pull request | #136 |
| A lead out of quota did not hand over to another company | #137 |
| Decisions raised by the workers were lost, and could not be answered with a button | #141, #143, #152, #155 |
| A quota error was reported as a sign-in error | #148 |
| Open pull requests could not be checked from the app; company status was not in Usage | #151, #157 |
| Pull requests were named "push"; the plan read "Done" for jobs that had only written their sheet | #159 |
| An open decision of an earlier work was asked again under every new proposal | #175 |
| A work with one build step was announced as "split into 0 parts", as if the builder had nothing to do | #176 |
