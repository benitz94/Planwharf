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
| Engine modules ported to C# | **15 of 55** | 55 |
| JavaScript engine lines ported | **about 1,040 of 10,300** (10%) | all; the orchestrator alone is about 3,800 |
| Phases done | 0 (foundations), 1 (test bench), part of 2 | 0 to 8 |
| C# engine tests | 459, plus 149 comparison cases with the JavaScript | |

Ported so far: `activity-events`, `activity-evidence`, `atomic-rename`, `autonomy`, `briefing`,
`browser-settings`, `model-policy`, `model-roles`, `project-history`, `provider-accounts`,
`provider-diagnostics`, `provider-refusal`, `skills`, `structured-final`, `worker-result`.

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
