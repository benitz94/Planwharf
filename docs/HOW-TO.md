# Planwharf: from installation to your first result

See also the [quick start](QUICKSTART.md) and [complete user guide](USER-GUIDE.md), including model role permissions.

This guide describes the completed current product scope, including durable execution, checkpoint recovery, usage, project history, export and import. Start with the steps below; use the later sections when you need more detail.

Planwharf is a local project notebook and coordinator for coding workers. You keep a goal and its decisions, clarify the work, review a proposed assignment, approve it, and follow the workers' results. The lead coordinates; workers implement jobs in the linked project folder.

## First run: installation → project → approval → result

1. **Download and install.** Open the [latest GitHub Release](https://github.com/benitz94/Planwharf/releases/latest), expand **Assets**, and download `Planwharf-vX.Y.Z.zip`. Use this same release ZIP on Windows, macOS or Linux and extract the entire folder somewhere you can write to. `planwharf-update.json.gz` is update metadata, not the application package; the GitHub-generated **Source code** archives are repository snapshots. Install Node.js and a modern browser such as Firefox, Brave, Opera, Edge or Chrome separately. Use **Settings**, then **Launch browser**, after the first launch if you want to choose which one opens next time. No `npm install` is needed. On Windows keep `Planwharf.exe` with the rest of the application files; on macOS/Linux launch with `sh ./planwharf.sh` or `node ./planwharf.cjs`. Cloning the repository remains an alternative for development.
2. **Open Planwharf.** Double-click `Planwharf.exe`. You can open the notebook without a coding CLI. To generate answers or execute coding jobs, install and sign in to at least one supported CLI, then enable available models for the lead and workers.
3. **Create a project.** Choose **Add project**, enter a name and a concrete goal, set **Projects folder** to an existing absolute directory such as `C:\Projects`, and click **Save projects folder**. Leave **Create a new subfolder using the project name** selected for a new project. Saving the project creates its folder, without starting workers.
4. **Clarify the goal.** Use **Grill me** or **Grill with the docs** and save your answers. Include acceptance criteria, technology constraints, files to preserve, and how the result should be tested.
5. **Prepare the work.** Use **Auto start**, or send a new work request for the selected project in the orchestrator chat. Review the proposed MegaTask: its goal, workers, smaller SubTasks, dependencies, and order. If enabled Matt skills have no complete cache yet, wait for the skills check, retry **Check skills updates**, or explicitly turn off **Use Matt skills for all models** before model requests.
6. **Approve that assignment.** Choose **Approve MegaTask** or the approval action shown in **Next**. Some controls describe this as **Approve run**. New MegaTasks require approval in both Manual and Automatic modes. A proposal or chat reply alone does not mean a worker has started.
7. **Check the result.** Follow **Now**, **Progress**, **Next**, and **Workers**. When the assignment completes, read the reported changes, tests, blockers, and next action, then open the output in the linked project folder and test it. Completion does not automatically create a commit, push to GitHub, or publish the product.

For a small first goal, try: “Build a local HTML counter with increment and reset buttons. Keep it dependency-free, explain how to open it, and verify both buttons.” Use a new project folder for this experiment.

## What Planwharf can do

| Capability | What you get |
| --- | --- |
| Project notebook | Several projects, Living Plan revisions, decisions, questions, and personal notes. |
| Grill | Questions to clarify an idea, optionally using linked project documentation. |
| Orchestration | A lead, explicit provider/model IDs, and an assignment you review before execution. |
| Durable execution | A MegaTask split into SubTasks, with dependencies, attempts, structured results, and timeline milestones. |
| Monitoring | Recorded processes, readable activity, raw output, completion evidence, and provider quota readings when available. |
| Recovery | Stop, pause after current tasks, saved checkpoints, explicit Resume, and **Import project** to restore an export as a new notebook project. |
| Repository inspection | Local folder checks and GitHub comparison using your existing GitHub CLI authentication. |
| Research | A separately approved research assignment with a retained report and source protection for supported routes. |

A **MegaTask** is the approved overall assignment. A **SubTask** is one piece of it. An **Attempt** is one actual worker execution; retrying a SubTask creates another Attempt while retaining the original evidence. A **Checkpoint** records where to continue.

## Installation and startup

Windows is the verified platform. Node.js v22.23.3 was used in the latest launcher checks. On Windows you can also open PowerShell in the application folder and run:

```powershell
node --version
node .\planwharf.cjs
```

The executable checks for Node.js before starting. If it cannot find it, it shows **“Node.js was not found. Install Node.js, then double-click Planwharf.exe again.”** Startup logs are saved per invocation under `%LOCALAPPDATA%\Planwharf\logs`; error dialogs include the log path. `PLANWHARF_LAUNCH_LOG_DIR` can select another log directory.

The server uses `127.0.0.1`, normally port 4173, and searches for another port if the default is occupied. An explicitly requested port must be free. A failed application-window launch attempts the default browser; the local address is also recorded in the startup log. Opening `index.html` directly does not start the server or file persistence.

The launcher opens one window. **Overview** / **Workspace** / **Supervisor** switches views while preserving selection and draft. Both views share one backend. Closing the desktop window follows the same checkpoint and shutdown path as **Quit application**. Relaunching the same notebook reuses that backend. Use **Quit application** or Ctrl+C to save checkpoints and shut down. After updating the code, quit and relaunch to load the new backend.

To rebuild the Windows launcher, run `launch\build-exe.cmd`; it requires the .NET Framework compiler. On Linux/macOS, use `node planwharf.cjs` or `sh planwharf.sh`.

## Project folders and a separate practice notebook

With **Create a new subfolder using the project name** enabled, an existing project name receives a numbered sibling such as `Counter-2`; its existing folder is preserved. To work on an existing repository, uncheck that option and enter **Existing project folder**. **Set place** updates a project's folder or GitHub URL. Changing **Projects folder** affects future projects and does not move existing ones. Removing a project removes its notebook entry and keeps its source folder.

The default `plan.json` is personal local data and is excluded from Git. On first launch, Planwharf creates it from `fixtures/starter-notebook.json` only when it is absent. Explicit notebook paths must already contain valid data.

For an isolated practice notebook, run the following in the application folder. Choose another name if `demo-notebook.json` already exists:

```powershell
if (Test-Path -LiteralPath .\demo-notebook.json) { throw 'Choose a new notebook filename.' }
Copy-Item -LiteralPath .\fixtures\starter-notebook.json -Destination .\demo-notebook.json
node .\planwharf.cjs --plan .\demo-notebook.json --attempts-dir .\.attempts-demo --port 4180
```

Create a new project folder inside that notebook for your experiment. Keep tests separate from personal projects. You do not need the historical calculator notebook or its removed executable to try Planwharf.

## Planning, approval, and chat

**The plan** stores direction, current state, decisions, and open questions. Saving an edit appends a revision; previous revisions remain in history. **What changed** compares revisions. Personal notes can be added, pinned, and deleted independently of jobs. AI relevance is off by default; enable it only for notes you want included in the AI briefing. Home groups project milestones, shows what changed since your last visit and offers Export project for a readable JSON metadata backup. Export does not include source contents, raw logs or credentials. **Import project** restores that file as a new project after you confirm the folder; see [Continue work](CONTINUE-WORK.md). The plan card **Import** still pastes a single revision.

**Grill me** clarifies the idea; **Grill with the docs** also uses project documentation. In **For you**, answer questions and choose whether to keep them open, close them with a decision, or change direction. Saved answers are planning records. **Skip grill** is available when you are ready to proceed.

A new work request prepares a fresh assignment using that request. It preserves older inactive tasks and checkpoints as history. Stop active work before replacing it with a different objective. **Resume** continues the same checkpointed assignment; it does not mean “start my new request.”

The selected project and chat **Project recipient** stay synchronized. Check that selection before issuing a command. Questions about progress do not create jobs. Use explicit actions such as **Start**, **Stop**, **Approve**, **Resume**, or **Skip grill** for project operations. Approval belongs to the specific proposed MegaTask; an old approval cannot authorize a different assignment. Automatic mode does not bypass it, and backend restart does not silently launch unfinished jobs.

The usual inspect/implement/test slices run in dependency order. Explicit independent slices may run in parallel, subject to the three-worker limit. Waiting dependencies do not launch workers. Saved results and completed SubTasks remain available when work is resumed.

## Leads, providers, models, and skills

Choose the lead in **Functions** or the Orchestrator view. The lead receives project and repository context plus coordination instructions. Its CLI has local/network tool access; workers receive implementation assignments. CLI implementation jobs run without repeated interactive permission prompts. Review the linked folder and proposed work before approval.

`models.json` uses **schema 3** with a `providers` array. Each provider declares its ID, adapter, quota pool, and model entries. Each model's `cli` field is the exact model ID sent to that adapter. The installed execution adapters are:

| Adapter | Coding CLI | Optional executable override |
| --- | --- | --- |
| `codex` | Codex | `PLANWHARF_CODEX` |
| `antigravity` | Antigravity (`agy`) | `PLANWHARF_ANTIGRAVITY` |
| `grok` | Grok | `PLANWHARF_GROK` |

Install and authenticate the CLIs you intend to use. On Windows, executable overrides must point to native executables. Planwharf also checks the usual Grok and Antigravity locations under `%USERPROFILE%\.grok\bin` and `%USERPROFILE%\.gemini\bin`, and resolves Codex's native binary, including npm package installations.

A model label does not select a provider or grant account access. Enabled, available, eligible, and quota remaining are separate conditions. Planwharf rejects incompatible routes and reports account or launch failures without silently substituting another provider. Provider catalogs can refresh from the installed adapter; the current interface includes **Refresh Grok models**. A listed model is not proof of a successful inference with your account. Historical verification reports describe the account and date tested, rather than what every new user can access.

An enabled model is a candidate, not a promise that every model will run on every job. Selection uses eligibility, available quota, task needs, and failure history. Legacy model `cost`, `limit`, and `used` values are local assignment counters. The current provider catalog is kept separately from provider quota readings; those counters are not subscription percentages or token billing.

Matt skills are enabled by default for leads and workers. Startup checks the upstream revision and installs a complete catalog before activating it. A failed update retains a prior complete cache. If no complete cache exists, model execution waits for a successful check or an explicit opt-out. **Use Matt skills for all models** persists per notebook and applies to subsequent requests and launches.

## Progress, results, and quotas

Project Home shows **Goal**, **Progress**, changes **Since your last visit**, **Now**, **Next**, and your notes. Goal comes from the current Living Plan. While work runs, Next identifies active or pending work; while paused, it reflects the checkpoint; after completion, it uses the recorded result's next action.

**Workers** initially filters to the selected project and current view session. Use the project, session, and state filters to see other attempts. The legend counts active workers even when filters hide them. A process ID, activity description, and validated completion result are separate evidence. Raw logs remain available unchanged. **Copy output** copies an answer; the conversation selector exposes retained earlier exchanges.

Phase 2 completion requires a valid structured result with matching execution identities and successful provider exit evidence. A bare exit code 0 or a free-form “done” message is insufficient. Results record changes, reported tests, blockers, and the next action. You should still test the resulting product against your acceptance criteria. Worker completion does not imply a Git commit, push, or deployment.

The backend monitors active work on a periodic timer while it remains running, including when the application window is closed. The interface also polls for updates. At the first scheduling check at or after **Finish by**, Phase 2 blocks new slices, stops active worker process trees, retains completed results and partial evidence, and saves a checkpoint. The check is periodic, not an exact-time guarantee; the usual background interval is four seconds and termination can take additional time. If all slices already have valid completion evidence, the assignment completes instead. A failed termination stays visible and is retried without launching more work.

Provider cards report **REMAINING**, shared by models using the same quota pool. **REMAINING UNREAD** means no usable reading is available, not 0% or 100%. Exhausting one shared pool affects its models together. Account authentication, credits, model access, and quota readings can fail independently; consult the displayed reason.

## Stop, pause, Resume, and backups

| Action | Effect |
| --- | --- |
| **Stop** | Stops active project worker process trees and saves a checkpoint for remaining work. A failed termination is reported. |
| **Stop worker** | Stops that implementation worker. Other active slices can finish; automatic replacement is suspended until explicit recovery. |
| **Pause after current tasks** | Stops scheduling more slices, lets current workers finish, then saves a checkpoint. |
| **Resume** | Continues a selected checkpoint with fresh attempts for remaining work; completed slices stay recorded. |
| **Quit application** | Requests worker shutdown/checkpoint saving and closes the backend. |

Resume verifies the assignment and checkpoint identity, originating plan revision, repository HEAD, and dirty-file evidence. Changed or uncomparable repository state can require explicit confirmation. If multiple checkpoints are available, select the one to resume. Another eligible lead can use the durable state without recovering the previous model's internal conversation.

Checkpoints have no expiry. Notebook settings live in `<notebook>.settings.json`; the default attempts directory is `.attempts-<notebook-name>` next to the notebook, unless overridden. Back up the notebook, settings, attempts directory, and linked project folders together. Forced termination or power loss can only recover information already saved to disk.

After a deadline checkpoint, explicit **Resume** authorizes remaining work beyond the original deadline; it does not immediately stop again at that expired time. Completed slices are retained and remaining slices receive fresh attempts. To set another Finish by time, prepare a new assignment with that deadline.

## Local repositories, GitHub, and research

**Check repository** verifies the linked local folder and compares it with the linked GitHub repository using your existing `gh` login. Private repositories require that account to have access. The check shows dirty paths and compared branches/commits; it fetches into dedicated inspection refs without merging, pushing, switching branches, or overwriting source files. Authentication failures and unrelated histories remain unknown instead of appearing “in sync.”

A conversational research request proposes a separate research MegaTask. Review it and choose **Approve research MegaTask**. A completed research assignment requires its report artifact and successful execution evidence. Use **Read research report**, **Stop**, and **Resume** on its card. Source-protected research currently supports Codex's isolated workspace sandbox; other adapters are rejected for this workflow until equivalent protection exists.

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| Node.js was not found | Install Node.js, then relaunch. Check `node --version` in PowerShell. |
| Missing `planwharf.cjs` | Extract or clone the complete repository; keep the executable beside the application files. |
| No application window | Read the latest startup log in `%LOCALAPPDATA%\Planwharf\logs` and try its local address. Quit an older backend before relaunching updated code. |
| Notebook owner is not responding | Quit that instance and retry. A live owner is retained; avoid deleting its lock or killing unrelated processes. |
| Explicit port occupied | Choose another port, or omit `--port` to allow default-port fallback. |
| Cannot save projects folder | Use an existing absolute directory accessible to your Windows account. |
| Waiting for Grill | Answer and save the questions, or choose **Skip grill**. |
| Proposed MegaTask has not started | Review and approve that exact MegaTask, including in Automatic mode. |
| Skills block model execution | Retry **Check skills updates**, or explicitly disable **Use Matt skills for all models**. |
| No eligible worker / Unavailable | Check the model's exact route, CLI login, availability, enabled state, eligibility, and shared quota. |
| Native CLI executable not found | Install the CLI or set its executable override to the native binary. |
| Paused or failed | Read results and attempt logs, resolve the cause, then choose the matching checkpoint to **Resume**. |
| Deadline reached | Review the saved partial results and checkpoint. Explicit **Resume** continues beyond the original deadline. |
| Deadline termination failed | Check the reported live process and termination error. Scheduling stays paused while the backend retries. |
| Resume requests confirmation | Review the changed plan/repository facts before continuing that checkpoint. |
| Worker appears idle | Inspect its PID, raw log, and files; a quiet CLI is not proof that it has stalled. |
| Completed assignment produces a broken product | Record the observed failure and prepare a new correction assignment with concrete acceptance tests. |

When asking for help, include the startup error or attempt log, exact provider/model ID, and relevant notebook/attempts paths. Keep login tokens and authentication files private.

## Technical details and verification

Read the [Phase 2 execution contract](PLANWHARF-PHASE-2.md) for schemas, APIs, approval binding, and reconciliation. The [Phase 2.1 report](PLANWHARF-PHASE-2.1-ACCEPTANCE.md) records real provider acceptance; the [Phase 2.2 report](PLANWHARF-PHASE-2.2-ACCEPTANCE.md) covers the corrected Next state. The [startup verification report](STARTUP-VERIFICATION-2026-10-13.md) records executable and browser checks. The [10 October report](VERIFICATION-REPORT-2026-10-10.md) retains earlier historical evidence.

Run `node validation/phase1-suite.cjs` for the repository's isolated engine and browser checks, and `node validation/launcher-smoke.cjs` for the Windows executable checks. These fixtures do not establish access to your provider accounts. Contributors should follow [AGENTS.md](../AGENTS.md), preserve personal data and raw execution evidence, and use isolated notebooks and attempts.
