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
| Engine modules ported to C# | **46 of 72** | 72 |
| JavaScript engine lines ported | **about 4,723 of 14,000** (34%) | all; the orchestrator alone is about 3,800 |
| Phases done | 0 (foundations), 1 (test bench), 2 (modules with no dependencies), 3 (first level, job 40), comparison cases for jobs 24 and 40 (job 41), plus the pipeline (jobs 32 to 34), the pipeline drawn (job 38) and the decisions inbox (job 39); next: phase 4 | 0 to 8 |
| C# engine tests | 1217, plus 610 comparison cases with the JavaScript across 31 modules | |

Ported so far: `account-folders`, `activity`, `activity-events`, `activity-evidence`, `activity-sessions`,
`atomic-rename`, `attempts`, `autonomy`, `briefing`, `browser-settings`, `claims`, `claude`,
`claude-usage`, `execution-next`, `invocations`, `model-policy`, `model-roles`, `model-routing`,
`models`, `notebook-file`, `phone-activation`, `phone-voice`, `plan`, `plan-status`, `project-checks`,
`project-export`, `project-history`, `project-workspace`, `provider-accounts`, `provider-catalog`,
`provider-diagnostics`, `provider-policy`, `provider-refusal`, `provider-runtime`, `provider-status`,
`push-email`, `quota`, `repository`, `sentinel`, `skills`, `step-outcomes`, `structured-final`,
`supervisor`, `temp-cleanup`, `time-left`, `worker-result`.

## Job 42 sheet done

GPT-6 Astra (`gpt-6-astra`, Codex) prepared the [Phase 4 sheet](../native/porting/jobs/42-phase4-processes-providers.md)
on `port/42-sheet` at refreshed base `944e1b1b322be4c30bc0d16710ab9637e4868f26`.
It covers the sixteen roadmap modules, deterministic tests, JavaScript comparisons, process and
provider risks, deferred host dependencies and Job 43's file allowlist. No engine module was ported.
Independent verification was conducted by Gemini 3.8 Flash (`gemini-flash`, Google DeepMind via Antigravity)
following Claude quota exhaustion (429). Added `engine/job42-sheet.test.cjs` covering all 16 modules,
parity/CI checks, error, recovery, concurrency and edge paths, open decisions, and mutations (8 tests).
Existing Job 23/37 sheet checks and parity checks passed (20 tests); targeted engine unit tests passed (32 tests);
job 42 sheet tests passed (8 tests); read-only recorder check passed. Sheet done; ready for review.

## Jobs done by Planwharf

The crews so far use models from Anthropic, OpenAI, Google and xAI.

| Job | Pull request | What | What the review changed |
|---|---|---|---|
| 42 | #219 | Wrote the Phase 4 processes-and-providers sheet in `native/porting/jobs/42-phase4-processes-providers.md` covering all 16 modules and additions: `runner`, `provider-models`, `external-adapters`, `api-agent`, `provider-adapters`, `provider-connections`, `antigravity`, `planUsage`, `worktrees`, `plan-file`, `pr-check`, `phone`, `phone-ollama`, `phone-handover`, `phone-handover-live`, `phone-activation-check` with C# module mappings, test coverage, parity comparison cases, and execution watch-points; implementation awaits review | Checked against module dependencies, process lifecycle, cancellation, Native AOT constraints, and deterministic test scenarios. Independent verification added `job42-sheet.test.cjs` covering concurrency, error, recovery, and edge case paths across all 16 modules, open decisions, and branch invariants. **Sheet only: build is Job 43** |
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
| 20 | #142 | Wrote the pipeline intake sheet: queue settings for `planwharf` issues and a local list, intake questions, `spec.md` with acceptance tests, file-backed job state, and the owner-approved manifest for the gate; implementation awaits review | **Sheet only: built by job 32** |
| 21 | #144 | Wrote the pipeline build sheet: routing by risk and parts touched, builder and reviewer pairs, review rounds, gate calls, and one pull request per job; implementation awaits review | Checked against sheets 18–20 and the design. **Sheet only: built by job 33** |
| 22 | #146 | Wrote the pipeline run sheet: several jobs at once in isolated worktrees within quota, the native pipeline view of stages, time, and quota, and an optional low-risk merge; implementation awaits review | Checked against sheets 18–21 and the design. **Sheet only: built by job 34** |
| 23 | #147 | Wrote the phase 2 rest sheet for `plan.js`, `quota.js`, `models.js`, `sentinel.js`, `invocations`, `notebook-file`, `project-checks`, and `project-workspace`; implementation awaits review | Checked parity cases, commands and file limits for the eight modules |
| 24 | #158 | Ported the eight phase 2 rest modules to C#: plan.js, quota.js, models.js, sentinel.js, invocations, notebook-file, project-checks, project-workspace | Built in 8 parallel parts, each in its own worktree; a notebook test expected the Windows result everywhere and was made to follow Node on each system |
| 25 | #172 | Built the worker MCP tools `ask`, `progress`, `finish`, `decision` and `handoff` on the C# engine, with the JSON result file kept for programs that have no verified MCP launch; verified independently with edge-case and concurrent-lock tests | Planwharf's server is registered for Claude and Codex whether or not the owner's MCP checkbox is on, and it never turns the owner's own servers on. Grok, Antigravity, the API agent and a configured CLI stay on the JSON file. A vendor CLI was not executed. Concurrency retry added for session locks and audit log |
| 26 | #179 | Built the C# acceptance gate. The owner-approved checks live outside the candidate worktree. The engine runs them before review and again after sync with main. A branch cannot choose or weaken those checks | A snapshot that cannot be pinned stops the gate. A shell command runs only when the owner named it. No model is called. A test script was not executable on Linux and macOS; branch brought up to date with main |
| 27 | #186 | Talk, Plan and Auto above the chat, one per project. Talk does not start the crew. Plan is the default and today's behaviour. Auto starts the goal for this session and returns to Plan after a restart. Pipeline is reserved and hidden | A pull-request fix still pushes when the worker push guard is set; verified independently with tests for concurrent remote push, clean no-change fix, and Auto-to-Talk running work preservation |
| 28 | #193 | Pull requests side panel in place of Check PRs: open pull requests with checks and conflicts, merged and closed history, a 30-day summary, filters, and Fix in the background | Verified independently, not pushed. A refused push keeps the commit and still says so when the refusal is long; one project can push while another keeps its copy. A crashed gh read stays a panel message. Timed-out checks count. An unreadable next page keeps the rows |
| 29 | #196 | Per-project saving modes: Normal, Safe and Ultra safe limit the lead and the crew to the allowed model classes, from the next step | Waiting for review, not pushed |
| 30 | #200 | Add to the running work: while work runs, an agreed addition appends one Build step before Verify and starts on an allowed model; Verify reruns after; stopped work resumes with it | Verified independently, not pushed. The work plan now records the new step. A second click does not start it twice. Ultra safe keeps a flagship off the extra step. |
| 31 | #202 | Clearer screens: Work finished shows three lines first with details closed; plain step lines and lead summaries with fallbacks; joined events; larger higher-contrast text; simpler Team cards and everyday Settings first | Verified independently, not pushed. A corrupt lead line or summary is ignored. Failed and Verify reports do not hide what was built. Repeated events join only when they sit together. A later check: `tickStep` fallback no longer depends on `prepare()`/git (that path failed under a parallel `node --test` load), and the pull-request title test now runs on Windows with a `gh.exe` stub; verified edge, recovery and revision-direction PR title cases. |
| 32 | #203 | Build pipeline part 1: queue, intake with author questions, spec with acceptance tests, file-backed job state in attempts store, and owner-approved manifest handoff to AcceptanceGate | Built PipelineIntake.cs in C# and 27 tests in PipelineIntakeTests.cs; verified defaults, label boundary, quoted requests, local list, source failure, withdraw/edit before and after approval, intake wait/questions, rejection, scope/ratings, bad manifest, owner approval, candidate folder isolation, chat goal isolation, and concurrency. Independent verification added coverage for local item edits before/after approval with drift, shell checks named in approval record and loaded by AcceptanceGate, immutable multi-revision preservation, tampered proposed manifest rejection in ApproveSpec, incremental question answering, metric persistence (time/tokens/quota), and safe malformed/null input handling |
| 33 | #204 | Build pipeline part 2: routing, build and review in pairs, a pull request per job, as `native/porting/jobs/21-pipeline-build.md` says (the sheet of job 21) | Built PipelineBuild.cs in C# and 39 tests in PipelineBuildTests.cs; verified start gate, routing by risk, parts touched, another company verifier requirement, invalid table/risk, quota blocked/unread/expired, one task at a time dependency order, builder/reviewer pairs, review rounds, gate returns, post-sync gate, merge conflicts, one pull request with Closes #, ready-local for unlinked repo, unexpected remote PR rejection, restart idempotency, quoted instructions, tamper resistance, and owner stop. Independent verification added coverage for directory traversal prevention on manifest paths, zero/negative candidate weights, model string status ("not signed in", "expired", "disabled"), multi-lane reviewer victories with separate verdicts, exact gate/review hand-back return limits, mid-build and mid-review quota switch persistence to routing.json on disk with handoff notes, gate runner failure recovery (gate-not-ported), post-sync gate non-ready rejection, ready-local immutability against retroactive PR opening, concurrent job isolation, brief markdown generation, and Unicode/special characters handling |
| 34 | #206 | Build pipeline part 3: several jobs at once, the pipeline view, the merge option, as `native/porting/jobs/22-pipeline-run.md` says (the sheet of job 22); and switching between Chat and Pipeline as the design (`docs/DESIGN-PIPELINE.md#switching-between-the-two`) says | Built PipelineRun.cs in C# and 35 tests in PipelineRunTests.cs, PipelineView.cs in Native app and 16 tests in PipelineViewTests.cs, English and Italian strings in Strings.cs, and frontend project pipeline panel and three-way mode switch in chat-ui.js, chat.css, chat.html. Verified concurrent scheduler within quota and maxConcurrentJobs limits, one invocation per model ID, worktree isolation, schedule lock, low-risk auto-merge on clean post-sync gate, pipeline view with stages/time/quota and "Needs your decision" labelling, three-way mode switch with "Send to the pipeline" and "Take it to the chat", and restart idempotency. Independent verification added coverage for cross-project shared-quota busy-model enforcement, non-integer cap handling with PipelineRunSettings.FromJson, worktree and branch reuse on restart without collision, comprehensive DefaultGateEvidenceVerifier gate evidence verification (pre-review rejection, seal tampering, commit/manifest mismatch), cleanup safety against path traversal and unmerged branches, drop protection on merged jobs, retry-merge guardrails on failure reasons, and resilient view parsing on partial stage files |
| 35 | #207 | Wrote the worker sandbox isolation sheet in `native/porting/jobs/35-sandbox.md` covering OS-level containment on Linux (bwrap/namespaces), macOS (sandbox-exec), Windows (AppContainer/Job Objects), least privilege filesystem/network boundaries, owner escalation in chat/phone, fail-closed timeouts, and escape test suites; implementation awaits review | Checked against platform security models, existing AI tool capabilities (Codex, Claude Code, Grok, Antigravity), and backward-compatible developer workflow constraints. Independent verification added coverage for concurrency paths (sibling worktree isolation, unique AppContainer profile names and SIDs per worker attempt, shared cache write-race prevention, localhost network isolation), error paths (sandbox-init-failed, access violation handling, escalation channel unreachable fail-closed behavior, worker-resource-exhausted termination), recovery paths (descendant process tree cleanup on cancellation/crash, ephemeral temp scratch deletion, engine restart recovery), and edge cases (all-provider API key sanitization, HOME/USERPROFILE neutralization, symlink/hardlink breakouts, git repository internal protection). **Sheet only: build is a later job** |
| 36 | #208 | Record step outcomes in Planwharf's data folder (`step-outcomes.jsonl`) with model, role, class, kind, size, acceptance, fixes by Verify, test outcomes, switches, refusals, duration, and quota usage. Summary metrics in Usage side panel and view snapshot. No file paths or code in the outcomes file | Built `step-outcomes.cjs`, `step-outcomes.test.cjs`, execution bridge outcome hooks in `execution.cjs`, `chat-ui.js`, `Strings.cs`, `PanelViews.cs`, and `MainWindow.cs`. Independent verification added `step-outcomes-verify.test.cjs` and expanded `PanelViewsTests.cs`, adding coverage for Unix/Windows/UNC path and code block stripping, brace removal, model object resolution, negative and malformed duration/quota/test sanitization, error recovery on corrupted JSON lines, high-concurrency atomic appends, multi-build step verification, attempt retry outcome resolution, no-verdict switch recording, and duplicate-free unverified completion hooks |
| 37 | #209 | Wrote the Phase 3 first-level engine porting sheet in `native/porting/jobs/37-phase3-first-level.md` covering all 23 modules and additions: `attempts`, `claims`, `project-export`, `execution-next`, `provider-catalog`, `provider-policy`, `model-routing`, `claude`, `claude-usage`, `repository`, `supervisor`, `activity`, `activity-sessions`, `provider-runtime`, `time-left`, `plan-status`, `provider-status`, `push-email`, `temp-cleanup`, `account-folders`, `phone-voice`, `phone-activation`, and `step-outcomes` with C# module mappings, test coverage, parity comparison cases, and execution watch-points; implementation awaits review | Checked against module dependencies, Native AOT serialization constraints, process argument vector safety, atomic locking invariants, and existing test suites. Independent verification added `job37-sheet.test.cjs` covering concurrency, error, recovery, and edge case paths across all 23 modules, open decisions, and branch invariants. **Sheet only: build is Job 40** |
| 38 | #211 | The pipeline you can see: each pipeline job drawn as a strip of its stages, the current one lit and said in plain words, review rounds as a loop, the owner's steps marked; and one picture of how the pipeline works | Built visual pipeline stages strip (Request → Questions → Spec and tests → Build ⇄ Review → Checks → Pull request → Merged), plain-words active stage highlighter, review loop counter indicator (e.g. 'round 2 of 3'), owner intervention points ('Needs you'), workflow explainer diagram drawn from job files at zero token cost, English and Italian translations in Strings.cs, and comprehensive unit and headless view tests in PipelineStripTests.cs and PipelineViewTests.cs |
| 39 | #212 | One inbox for decisions: cross-project and pipeline decisions inbox in the top bar with pending count, Auto mode for lead decisions with break highlighting, per-decision lead delegation, phone /decisions support, English and Italian translations | Built decisions inbox module in engine (`decisions-inbox.cjs`) aggregating open and answered decisions across all projects and pipeline jobs, Auto mode per-project and globally in Settings, per-decision lead delegation ('Let the lead decide'), step start locking, phone /decisions command and decision alert actions with break highlighting, Italian translations in Strings.cs, C# engine models and parser in DecisionsInbox.cs, native UI panel and top bar tab in MainWindow.cs, and automated tests in decisions-inbox.test.cjs, phone-decisions.test.cjs, DecisionsInboxTests.cs, and DecisionsInboxViewTests.cs |
| 40 | #213 | Ported twenty-three Phase 3 engine modules to C#: attempts, claims, project-export, execution-next, provider-catalog, provider-policy, model-routing, claude, claude-usage, repository, supervisor, activity, activity-sessions, provider-runtime, time-left, plan-status, provider-status, push-email, temp-cleanup, account-folders, phone-voice, phone-activation, step-outcomes | Built 23 C# modules in Planwharf.Engine and 23 xUnit test suites in Planwharf.Engine.Tests; zero compiler warnings under Native AOT analyzers (PublishAot=true, TreatWarningsAsErrors=true); full parity verification passing |
| 41 | #217 | Comparison with the JavaScript for jobs 24 and 40: recorded JavaScript comparison cases for jobs 24 and 40 modules, wired into C# ParityTests, fixed discrepancies, and added CI parity requirement guard | Recorded 346 comparison cases across 20 modules (total 593 cases across 31 modules); fixed C# discrepancies in ProviderCatalog, Claude, ClaudeUsage, PlanStatus, and Supervisor; created native/porting/parity/required.json with per-module case minimums; added CI parity requirement guard in engine/parity.test.cjs verifying file existence, case minimums, and ParityTests wiring |

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
| The job's branch named in the goal (`feat/gate-build`) was ignored, and the pull request took the crew's whole instructions as its title | #180 |
| Fixing a pull request opened a new work in the chat, and checked out its branch in the owner's folder; now it is fixed in the background, in a copy, and pushed to the same branch | #181 |
| A lead that failed twice handed over to Fable 5.2, which needed usage credits, and the chat stopped there; looking for models again brought Fable back | #182 |
| A lead that did not answer handed over to a model under its company's quota limit | #183 |
| The phone messages were long and did not say who leads, builds and verifies | #184 |
| A model under its limit kept working, and new steps could start on one, when no model was above its limit; the background fix of a pull request read the shared team instead of the project's | #187 |
| The Auto chat mode started goals by itself and turned on automatic approval for the whole app; it was removed: Talk and Plan remain | #190 |
| The phone named the shared team's lead instead of the project's, never said when the lead changed, and packed several items on one line | #192 |
| Verify was chosen at the start and later ran on a model that had fallen under its limit; with no verifier above it, the work lost its Verify step | #194 |
| The background fix of a pull request picked Fable 5.2, which needs usage credits, and stopped there | #197 |
| The bench, the limit and the Tools folds of the Team panel closed by themselves each time a model was picked | #198 |
| The owner could not choose which models may lead: every eligible model could take over | #199 |
| The crew's local checks passed while GitHub refused a blank line at the end of a file: the local run now checks the branch's commits as GitHub does | #205 |
| A fix in the background showed no progress while it ran | #214 |
| A pull request did not say which models led, built and verified the work | #216 |
| When the Verify model refused (quota), Verify went to the model that had built the work, which then could not call itself independent and stopped the work; "Verify by another company" was a rule in the plan and the pipeline, now only a preference | #218 |
| A fix in the background could not be stopped; a check GitHub cancelled was shown as red, sent to a model, and reported as fixed with nothing changed | #220 |
| The app said "stopped while starting" when an earlier engine was still running on the same data; it now connects to it, or says how to close a stuck one | #221 |
| The lead split the work without seeing the sheet the goal named (it was told not to read files), so a sixteen-module port went to one worker, who did one module; a step that stopped part way then waited for an alert instead of going back to the lead. The lead now reads the sheet and splits the rest again | #223 |
| The lead's reasons were never shown: "did not split the work", which model a part moved to and the error that stopped the crew stayed in an internal log, and the stop read only "autonomy error". One reply with a model it may not use, or more than ten parts, threw the whole split away; one error ended the crew. Now all of it is in Progress, the split is kept and the error retried | #224 |
| The lead split job 43 into 17 parts and gave 16 of them to its own company (GPT-6.1 Sol and GPT-6 Sol), leaving Grok, Gemini and the others idle. Planwharf now spreads the parts | #225 |
| The "No limit" switch of a model was hidden in a fold, and a whole company could not be set to no limit from the app. Now a chip on the card and a company button | #226 |
| The last part of job 43, which wires the others together, was stopped for changing the files of the parts it waited for, after they had finished. It may now change them | #227 |
| Every lead reply with a summary stopped the crew on "sentences is not a function": autonomy used a helper that its module did not export; the error was hidden behind "autonomy error" until #224 | #228 |
| Grok's usage service refused a saved token, and Planwharf wrote "Grok login expired" and kept every Grok model out of the crew while grok was signed in; the same could happen to Claude and ChatGPT | #? |
