# Planwharf complete user guide

This guide describes the current application, including model role permissions. Start with the [quick guide](QUICKSTART.md) if you want to create your first project immediately. The application, current documentation and product messages use English; personal content and original historical evidence keep their original language.

## Planwharf and Orchestrator

Planwharf answers **"What is happening with my project?"** Orchestrator answers **"What are the AIs doing?"** They share one local backend, notebook and durable execution state.

Home is the project overview. Orchestrator contains chat, model/provider settings, processes, worker evidence, remaining quota and AI usage. Opening Home does not invoke a model. Deterministic commands such as Stop, model toggles and quota/status inquiries use local paths; reasoning and clarification may invoke your selected lead.

A Planwharf project is a local notebook entry linked to a source folder and optionally a GitHub repository. A GitHub repository stores code and releases; GitHub Projects is a separate optional issue-planning board and is not required to use Planwharf.

## Installation, launch and windows

Install Node.js and a modern browser such as Firefox, Brave, Opera, Edge or Chrome. Planwharf has no npm dependencies. For a normal installation, open the [latest GitHub Release](https://github.com/benitz94/Planwharf/releases/latest), expand **Assets**, and download `Planwharf-vX.Y.Z.zip`. This is the packaged application release and the same ZIP is used on Windows, macOS and Linux. Extract the entire archive; do not run the launcher from inside the ZIP. The accompanying `planwharf-update.json.gz` file is update metadata, not the application package. GitHub's automatically generated **Source code (zip/tar.gz)** files are repository snapshots; cloning the repository remains an alternative for development.

The executable is a launcher and needs the neighboring files. Windows is the verified desktop platform. Linux/macOS launcher paths exist, but their desktop behavior has not been verified by the Windows acceptance.

On Windows, open `Planwharf.exe` or `planwharf.cmd`. From a terminal, use `node planwharf.cjs`. On macOS/Linux, use the same extracted release ZIP and run `sh ./planwharf.sh` or `node ./planwharf.cjs`. The local server binds to `127.0.0.1`, normally on port 4173, and the desktop launcher can select another port when necessary.

Normal launch opens one window with **Show Orchestrator** / **Show Planwharf** to switch views. Both views share one backend, selected project and local UI state. Opening the same notebook again reuses its backend and focuses or reopens the application.

The desktop window starts in full-screen mode. Press **F11** to toggle full-screen mode. If desktop launch falls back to your default browser, that browser controls its window size.

Closing the desktop window checkpoints work and shuts down the backend, just like Quit application. In the default-browser fallback, tab close is detected after a short grace period to allow page reloads. **Quit application** or Ctrl+C requests the checkpoint/shutdown path. Quit and relaunch after updating application files so the backend loads the new code.

The theme button switches between light and dark appearance. Theme and other local view preferences are retained by the browser profile. Expandable technical panels keep detailed controls available while Home stays focused on project progress.

For terminal use, `node planwharf.cjs projects` lists notebook projects and `node planwharf.cjs status` reads recorded status without dispatching work. `--plan`, `--attempts-dir` and `--port` choose the local notebook, evidence directory and port. `--browser` chooses a detected browser name, `default`, or an absolute executable path. `--no-window` starts the backend without opening a desktop window. The `ask` command sends a message to the running backend; ordinary questions and explicit execution commands follow the same chat/approval rules described below.

## Projects and folders

**Add project** records a name, goal and linked local folder. **Projects folder** is an existing absolute base directory for new project folders. **Create a new subfolder using the project name** creates and links a fresh directory; existing names receive a numbered suffix. You can instead link an existing folder.

**Set place** changes the linked folder and optional GitHub repository URL. Changing the base directory affects future projects and does not move existing source. **Remove project** removes the notebook entry while retaining the filesystem folder.

Always check the selected project and chat recipient before issuing commands. A command for one project must not dispatch another project's work. Name another project explicitly only when you intend to target it.

The default notebook, `plan.json`, is local data excluded from Git. First launch creates it from the starter fixture only if missing; existing notebooks are not replaced. For an isolated practice session, copy a valid starter notebook to a new file, then run:

```powershell
Copy-Item fixtures/starter-notebook.json practice.json
node planwharf.cjs --plan practice.json --attempts-dir practice-attempts --port 4180
```

Use a new practice filename. Explicit notebook paths must already contain valid Planwharf data.

## Living Plan, revisions and decisions

The Living Plan records the goal, current state, decisions and open questions. Saving an edit appends a revision rather than replacing historical revisions. **History**, **Decisions**, **Revisions** and **What changed** expose that record. A task retains its originating revision even when the plan changes later.

Grill asks questions to clarify an idea. **Grill me** uses the goal; **Grill with the docs** can use linked project documentation. Save answers to update the plan. **Skip grill** bypasses clarification when appropriate. Turning Grill off does not bypass approval of new work.

The technical notebook import/editor controls validate their own data before saving. They are distinct from project export: a project export bundle does not have an automatic import/restore workflow.

## Personal notes

Home notes support add, pin/unpin and delete. Notes persist per project independently of tasks and revisions.

**AI relevant: No** is the default and keeps the note as a user reference. **AI relevant: Yes** permits the note to enter the selected project's compact AI briefing. A note never automatically becomes a task, decision or approval.

## Providers, exact models and role permissions

Supported execution adapters are Codex / ChatGPT, Grok CLI and Antigravity / Gemini. Install and authenticate the CLI separately. Planwharf uses its existing account; it has no accounts/login system and no paid API dependency.

Enable the provider and select each model's **Allowed roles**:

| Role | What it allows | What it does not authorize |
| --- | --- | --- |
| Orchestrator | Reason, inspect and coordinate as the selected lead. | Dispatching that model for implementation or review when those roles are disabled. |
| Worker | Approved implementation and supported research work. | Acting as the selected lead or reviewer when those roles are disabled. |
| Reviewer | Approved review/audit work and findings. | Implementation when Worker is disabled, or coordination when Orchestrator is disabled. |

Roles are independent permissions, not rankings. Several can be enabled together, or all can be disabled. Changes affect subsequent assignments; a role toggle does not terminate work already running. Use Stop if you need to interrupt it.

Compatibility and user permission are separate. A model can be technically suited to a task yet excluded because you assigned it another role. An unsupported Orchestrator checkbox is disabled, and the backend rejects attempts to enable incompatible roles. Eligibility uses configured capabilities rather than an automatic quality benchmark. Models still need provider availability and sufficient remaining quota.

Choosing the Orchestrator flag permits use as a lead; the **Orchestrator model** selector chooses the actual lead. Removing that role from the selected lead clears the selection without silently choosing another model. There is no separate model On/Off switch. With no roles selected, the model receives no assignments. Unavailable models and exhausted quota are reported separately.

Reviewer is the English term used in the UI. It maps to existing **AUDIT** SubTasks. Reviewers receive instructions to inspect and report without implementing source changes. Enabling Reviewer does not automatically insert an extra review after every coding task: request and approve a review when needed.

**Find models** searches provider/name/exact ID. **Model role** filters by enabled role; **All roles** removes that filter. **Show all models** expands large provider catalogs. Role filters show models with that role selected, including unavailable models, so you can manage their permissions independently of availability.

Provider enablement, allowed roles and catalog expansion survive restart. All models sharing a provider pool reference the same quota. Exact provider/model identity is retained in assignments and results; there is no hidden brand preference or silent substitution.

At every backend startup, Planwharf checks enabled providers before publishing the first model snapshot or executing model work. **Refresh models** repeats this check manually. The UI shows each provider's check time and result. Successful discovery replaces old availability decisions; a failed check shows **Availability unread** and retains the last known availability without claiming it is current. Status polling does not repeat provider checks. Model discovery reports the active account catalog when a verified non-inference listing is available. Antigravity retains explicitly configured exact IDs when enumeration is unavailable. Failed discovery preserves prior configuration and reports the limitation rather than inventing access.

## Remaining quota and AI usage

### Personal and Business accounts

Normal accounts keep the existing provider and model names. Confirmed team, Business or Enterprise accounts add **Business**: for example, **Grok Business** and **GPT-6 Luna Business**. A paid personal subscription is not automatically a Business account. If the provider does not expose account classification, the existing name is shown without an extra label. This is a display default, not confirmation of a personal plan; Planwharf does not infer Business from an email domain or exhausted credits.

The account is the CLI login used by workers, which may differ from the account open in your browser. For Grok, stop active work before changing accounts, use the CLI's logout/login flow, then use **Refresh models** or reopen Planwharf. Account changes invalidate old availability and remaining-quota readings. Roles are saved separately using a local opaque account identifier; returning to a recognized account restores its role flags. Existing settings are adopted by the first recognized account; a newly seen account starts with no roles selected and needs explicit configuration.

Only the current CLI login is usable; Planwharf does not keep both accounts signed in or silently fall back between them. Codex classification uses selected-workspace plan metadata; Grok classification uses the CLI's team principal metadata. Antigravity currently exposes no verified account-type/identity interface in this integration, so it keeps its existing names and automatic per-account role separation is unavailable. No credentials, email addresses or raw login profiles are copied into exports or invocation evidence.

### Reading remaining quota

Quota always communicates the percentage **REMAINING** in the provider's shared pool:

| Remaining | Display |
| --- | --- |
| 75–100% | Green |
| 50–74% | Orange |
| 25–49% | Yellow |
| 1–24% | Red |
| 0% | Red, EXHAUSTED |
| Unread | Grey, REMAINING UNREAD |

Reset times appear when reliably available. Unread does not mean full or exhausted. Quota readings are cached, and provider interfaces/account access may limit their availability. Ask **quota remaining** for a deterministic quota response.

**AI usage** shows orchestrator calls by provider/model, durations and recent invocations. Worker usage shows attempts, durations and COMPLETE / FAILED / BLOCKED outcomes. The local ledger records exact identity, project/task/attempt links, reason, timestamps and available process exit. Provider-reported lead tokens appear only when available reliably; unknown counts remain absent. This is operational evidence, not a billing estimate.

## Chat and skills

Chat uses the selected project recipient. Ordinary discussion does not automatically dispatch implementation. A new work request prepares a fresh assignment; an explicit Resume continues the existing checkpointed assignment.

The lead receives a compact project briefing, recent relevant results, checkpoint, permitted model roles and AI-relevant notes. Six bounded recent chat turns are sent; full historical chat remains archived. The lead can inspect linked files and saved evidence when more context is necessary. It coordinates; workers implement.

Recorded chat sessions remain available through the session selector. **Copy output** copies one answer; **Copy displayed conversation** copies the visible exchanges. Stop for a pending chat response cancels that model response; project Stop is the execution/checkpoint control.

[Matt Pocock's skills](https://github.com/mattpocock/skills) can supply instructions to leads and workers. **Use Matt skills for all models** persists per notebook. **Check skills updates** checks the upstream catalog. A failed update retains a prior complete cache. If no complete cache is available, enablement requires a successful check or explicit opt-out before model execution.

## MegaTasks, SubTasks, Attempts and Results

A **MegaTask** is the overall approved assignment. A **SubTask** is one bounded piece. An **Attempt** is one actual worker execution. A **Result** records outcome, summary, changed files, reported tests, blockers and next action. A **Checkpoint** records where continuation should begin.

Use Auto start or a concrete new request to prepare work. Review the proposed goal, models, types and dependencies, then **Approve MegaTask**. Approval is bound to that exact MegaTask; an old approval cannot authorize new work. Manual and Automatic modes both require approval for new MegaTasks.

Default inspect/implement/test slices follow dependencies. Explicit independent slices may run in parallel within the existing three-worker limit. Waiting dependencies do not start processes. Roles are checked before assignment and again before launch. A disabled or incompatible exact assignment blocks; Planwharf does not silently replace it.

Workers execute in the linked source folder and modify actual project files. `.attempts-*` holds operational evidence and checkpoints. Research has isolated evidence folders. `validation/*` contains retained product test runs, not your normal implementation destination.

Completion requires the structured execution evidence expected by the current backend. A PID, a planned result path or an optimistic chat message alone does not prove completion. Review reported tests and verify outputs in the project folder. Planwharf does not automatically commit, push or publish them.

## Home, history and changes since last visit

Home shows **Goal**, **Progress**, **Now**, **Next**, local changes, notes and project history. Progress can expand into completed/pending slices, results, files, reported tests and blockers. Next follows durable execution state, including active work, dependencies, recovery and the final result's next action.

History groups related execution events into concise milestones. Expand technical evidence to see individual starts, results, checkpoints and Git observations. **Since your last visit** compares saved project data and local Git observations, combining revisions, notes, execution milestones, commits and local changes where comparison is reliable. Opening Home does not invoke AI.

## Worker monitoring and interventions

Orchestrator's worker board supports project, session and state filters. Previous sessions can be expanded without confusing them with current live work. The legend reports active processes even when filters hide them. Inspect activity and raw output separately from validated results.

**Stop worker** targets one process and suspends further scheduling according to the checkpoint policy; other active slices can finish. Unhealthy/dead worker evidence is reconciled by the backend. Current role restrictions apply to future attempts, including retries. A blocked assignment requires an eligible enabled model and explicit recovery rather than silent substitution.

The technical claim-test panel records an attempt for testing; it is not evidence of a real coding process. Use the approved MegaTask workflow for actual execution.

## Stop, graceful pause, deadlines and Resume

**Pause after current tasks** prevents new SubTasks from starting while active work finishes. Remaining work is saved in a checkpoint; if every slice completes, the MegaTask completes.

**Stop** terminates current workers and saves a checkpoint. It preserves partial files and evidence; it does not undo source changes. **Finish by** uses the existing deadline policy: stop scheduling, reconcile/terminate work according to that policy, and checkpoint unfinished work. There is no general-purpose scheduler.

Before **Resume**, select the explicit checkpoint if several are available. Home shows repository/plan changes since it was saved. Changed HEAD, local files, changed plan revisions or incomplete comparison may require confirmation. Review the evidence before confirming continuation.

Resume retains completed SubTasks and creates fresh Attempts only for remaining work. Choosing a different lead does not replay completed slices. Backend restart preserves history and does not silently launch unfinished work. If an assigned model's enabled roles no longer allow the SubTask, restore the appropriate role or explicitly reassign the remaining work to an exact permitted model.

## Repository inspection and research

**Check repository** compares the linked local repository and authenticated GitHub access, with branches, ahead/behind commits, uncommitted paths and check time. GitHub access uses the existing `gh` login. Inspection may fetch into dedicated inspection refs; it does not merge, push, switch branches or overwrite working files. Access failures and unavailable comparisons remain explicit.

A research request proposes separately approved work with a retained report. Current source-protected research is supported by the verified Codex adapter; other adapters are rejected for that workflow. Use **Read research report**, Stop and Resume on its card. Research requires the Worker role plus the applicable research capability; Reviewer is for AUDIT work.

## Notifications, export and backup

Optional **Notifications: On** requests local browser permission for completion, failures, saved checkpoints and attention events where supported. No external notification service is used. Notifications require an open UI, and preferences are local to the browser profile.

**Export project** downloads schema 1 JSON containing project metadata, Living Plan revisions, notes, tasks, structured Results, Checkpoints, timeline and relevant operational settings, including allowed model roles. A readable Markdown summary is included. Source contents, provider authentication and giant raw logs are excluded.

Export is for metadata review/backup. Automatic re-import/restore is not supported. For complete recovery, preserve the notebook, its settings file and its attempts directory; keep source backups separately. Do not copy provider authentication into project exports or public GitHub evidence.

## Troubleshooting and safe updates

| Symptom | Check |
| --- | --- |
| Launcher cannot find Node.js | Install Node.js and keep the complete application folder together. The error dialog identifies its launch log. |
| Model is unavailable | Verify the exact CLI model ID, installed executable, account sign-in and provider catalog. |
| No eligible worker/reviewer | Check provider enablement, the required allowed role, capability, quota and the exact proposed assignment. |
| Model cannot be selected as lead | Check Orchestrator compatibility, its role flag, provider enablement and quota. |
| REMAINING UNREAD | Check provider sign-in/interface support; do not infer a fake percentage. |
| Skills cache blocks execution | Check skills updates or explicitly turn off skills for new requests. |
| Resume needs confirmation | Review repository and plan changes against the saved checkpoint. |
| UI still shows old behavior after an update | Quit application and relaunch to load the updated backend. |
| Historical logs use another language | Original evidence and user-authored data are preserved; current product text is English. |

Keep TLS verification enabled. Existing operator CA settings remain respected, and provider environment adjustments are scoped to child processes. See [project status](PROJECT-STATUS.md) for verified platforms/providers and limitations, and [provider adapters](PROVIDER-ADAPTERS.md) for the developer extension contract.

See [third-party acknowledgments](THIRD-PARTY-NOTICES.md) for upstream credits and license notices.

## Application settings and browser

Use **Launch browser** in the header. Select a detected Firefox, Brave, Opera, Edge, Chrome or Chromium installation, the system default, or a custom executable and family. Save, then Quit and relaunch to apply. `--browser default` overrides an unavailable saved browser. **Quit application** remains in the header.
