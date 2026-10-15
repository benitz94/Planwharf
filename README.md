# Planwharf

A local project notebook and coordinator for AI coding workers across models and providers. Keep decisions and revisions, clarify an idea with Grill, approve a plan, and monitor execution with the AI models you use.

Planwharf includes native Codex, Grok and Antigravity CLI adapters, direct OpenAI-compatible and Anthropic Messages API connections, and a configurable CLI adapter for compatible structured protocols. Add connections from **Usage and permissions ? Add API / CLI connection**. DeepSeek, Kimi and other providers can use compatible APIs without pretending to be another provider. See [connection setup and limitations](docs/EXTERNAL-PROVIDERS.md). Exact model access, tool support and credentials remain account-specific; installing an arbitrary CLI does not guarantee protocol compatibility. Live access to every provider/model has not been verified. Please [report missing integrations](https://github.com/benitz94/Planwharf/issues) with the OS, CLI/protocol version, exact model ID and sanitized error.

Planwharf is still under active development and is provided **as is**. Features and behavior may change, and bugs or limitations may remain. Feedback, development suggestions, and contributions are welcome. Please share bug reports and ideas through GitHub Issues.

Planwharf can use [Matt Pocock's open-source skills](https://github.com/mattpocock/skills) to guide its coding agents. Thanks to Matt and the contributors; see [third-party acknowledgments and license notices](docs/THIRD-PARTY-NOTICES.md).

[Quick start](docs/QUICKSTART.md) | [FAQ](docs/FAQ.md) | [Complete user guide](docs/USER-GUIDE.md) | [Project status](docs/PROJECT-STATUS.md) | [First project guide](docs/HOW-TO.md) · [Phase 2 execution contract](docs/PLANWHARF-PHASE-2.md) · [Startup verification](docs/STARTUP-VERIFICATION-2026-10-13.md)

## Why Planwharf?

AI coding CLIs can implement tasks, but running several of them separately leaves you to track decisions, assignments, progress and interrupted work yourself. Planwharf adds a **persistent project notebook and a coordinated execution workflow** around the AI tools you already use.

- **Keep the project, not just the conversation:** goals, decisions, notes and revisions remain connected to project history.
- **Coordinate supported AI connections:** approve a MegaTask, see its SubTasks, and control which exact provider/model can coordinate, implement or review.
- **Know what actually happened:** inspect worker attempts, reported tests, changed files, blockers and execution evidence instead of assuming that a process exit means success.
- **Resume deliberately:** Stop and Pause preserve partial work and checkpoints; Resume checks project state and avoids automatically repeating finished SubTasks.
- **Work locally:** the notebook and backend run on your computer; AI provider connectivity and credentials remain subject to the chosen integration.

Planwharf does not promise perfect model compatibility or complete worker sandboxing. Windows has the strongest end-to-end verification so far. For a concise introduction, see the [FAQ](docs/FAQ.md).

## Download the latest release

For normal installation, open the [latest GitHub Release](https://github.com/benitz94/Planwharf/releases/latest) and download the release asset named `Planwharf-vX.Y.Z.zip` under **Assets**. The same release ZIP is used on Windows, macOS and Linux.

- **Windows:** extract the ZIP and launch `Planwharf.exe` (or `planwharf.cmd` / `node planwharf.cjs`).
- **macOS / Linux:** extract the same ZIP and run `sh ./planwharf.sh` or `node ./planwharf.cjs`.
- `planwharf-update.json.gz` is update metadata, not the application download.
- GitHub's automatically generated **Source code (zip/tar.gz)** archives are repository snapshots; prefer the packaged `Planwharf-vX.Y.Z.zip` release asset for normal installation.

Node.js and any provider CLIs are installed separately.

## Start here

1. Download `Planwharf-vX.Y.Z.zip` from the [latest GitHub Release](https://github.com/benitz94/Planwharf/releases/latest), extract the complete application folder, install Node.js, and on Windows open `Planwharf.exe`.
2. Choose **Add project**, write a concrete goal, and link a new or existing project folder.
3. For AI work, authenticate a supported coding CLI or add an API connection with authorized exact model IDs, then enable available lead/worker models. Chat subscriptions do not automatically grant API or CLI access. Use Grill to clarify the goal, then **Auto start** or chat to prepare an assignment.
4. Review the proposed MegaTask, workers, and dependencies. Choose **Approve MegaTask**; Automatic mode also requires approval of new work.
5. Follow **Progress**, **Now**, **Next**, and **Workers**, then test the output in the project folder. Completion does not automatically commit, push, or publish it.

The [first project guide](docs/HOW-TO.md) explains each step, a separate practice notebook, checkpoints, providers, and troubleshooting. The remaining README sections provide reference details.

## Understand your project and AI work

**Planwharf** answers "What is happening with my project?" Its Home shows the goal, completed work, current state, next action, notes and concise history. **Orchestrator** answers "What are the AIs doing?" It shows approved work, worker processes, exact provider/model identities, remaining quota and AI usage. Both read the same durable state.

Model recognition and provider integration are extensible. The currently implemented local coding CLI adapters are Codex, Grok and Antigravity; these are the available integrations today, rather than the scope of Planwharf's model support goal. Codex, Grok and Antigravity discover exact model IDs from the active CLI login's catalog, including Gemini, Claude and GPT-OSS families supported by Antigravity. Additional model IDs can be configured when the selected CLI supports them; a different provider's CLI requires its own adapter implementation. Please report missing integrations or model-loading failures through [GitHub Issues](https://github.com/benitz94/Planwharf/issues). See [Providers and execution](#providers-and-execution).

Use the providers you have: no provider is preferred behind the scenes. In Orchestrator, enable a provider and set each exact model's Allowed roles (Orchestrator, Worker, Reviewer), and choose an eligible orchestrator. These role flags are assignment restrictions: a Reviewer-only model cannot implement code, and an Orchestrator-only model cannot be dispatched as a worker or reviewer. Unsupported Orchestrator roles cannot be selected. The Model role filter shows each class or All roles. Search finds models even in a large catalog; **Show all models** expands a provider. These choices survive restart. An unavailable model is reported explicitly and never silently replaced.

Quota always means **REMAINING** in the provider's shared pool. Every model sharing that pool sees the same value. Green is 75?100%, orange 50?74%, yellow 25?49%, and red 1?24%. Zero is red and **EXHAUSTED**; grey **REMAINING UNREAD** means the provider could not report a reliable value. Reset times appear when available. **AI usage** shows observed calls, attempts, durations and results; it is not a monetary estimate.

Review and approve a MegaTask before workers execute its SubTasks. **Pause after current tasks** prevents new slices from starting while active work finishes. **Stop** terminates current workers and saves a checkpoint. Before **Resume**, Home previews repository and plan changes; changed or incompletely comparable state requires explicit confirmation. Resume retains completed slices, even if you choose a different orchestrator. Existing **Finish by** deadlines follow the checkpoint policy rather than a general scheduler.

Project history groups execution events into milestones. Expand technical evidence for individual starts, results, checkpoints and Git observations. **Since your last visit** uses saved project data and local Git inspection; opening Home never invokes an AI. Notes stay notes: choose **AI relevant: Yes** only for guidance you want included in the briefing.

Use **Export project** on Home to download readable JSON containing project metadata, revisions, notes, tasks, results, checkpoints, history and a Markdown summary. Source contents, raw worker logs and provider credentials are excluded. Export is for review and metadata backup; automatic import/restore is not supported. Keep the original notebook and attempts directory for full recovery.

Optional **Notifications: On** requests local browser permission for completion, failures and saved checkpoints. Support depends on the OS/browser; notifications require an open UI and are off by default. No external notification service is used.

Use **Quit application** to checkpoint and shut down safely. After updating application files, Quit and relaunch to load the new code. Closing the desktop window follows the same checkpoint and shutdown path.

## Requirements and launch

Install Node.js and a Chromium browser such as Edge or Chrome. Planwharf has no npm dependencies. Install and authenticate the coding CLIs you want to use separately.

**CLI authentication and model access are required for AI work.** Complete sign-in or credential setup in each supported CLI before using it in Planwharf. Signing in to a provider's chat website or app does not authenticate its CLI. Your account must also include access to the requested model through that CLI: a plan that includes only chat access is insufficient. Listing or configuring a model in Planwharf does not grant access, credits or quota. Planwharf uses the CLI's existing authentication; it does not turn a chat subscription into CLI access.

For a first Windows launch after downloading from GitHub:

1. Download the latest GitHub Release ZIP, or clone the repository / use **Code → Download ZIP**, and extract the entire archive. Each push to `main` publishes the next patch release (`v0.1.1`, `v0.1.2`, …).
2. Install Node.js separately if it is missing; the download does not include or install Node.js.
3. Keep the application files together and double-click `Planwharf.exe` in the extracted folder. No `npm install` step is required.
4. To run coding workers, separately install and sign in to the provider CLIs you want to use. They are not required just to open the notebook.

The executable checks for Node.js before starting the application. If it cannot find it, it shows: **“Node.js was not found. Install Node.js, then double-click Planwharf.exe again.”** The dialog also includes a startup log path. If `planwharf.cjs` is missing, download and extract the complete application folder again.

On Windows, double-click `Planwharf.exe` or `planwharf.cmd`, or run:

```powershell
node .\planwharf.cjs
```

To rebuild the Windows launcher, run `launch\build-exe.cmd`; it requires the .NET Framework compiler.

### Windows, macOS and Linux

| OS | Launch from the application folder | Verification status |
| --- | --- | --- |
| Windows | Double-click `Planwharf.exe` or `planwharf.cmd`, or run `node .\planwharf.cjs` in PowerShell. | Verified platform; includes the Windows executable launcher. |
| macOS | Run `node ./planwharf.cjs` or `sh ./planwharf.sh` in Terminal. | Launch paths are implemented; end-to-end operation has not been verified on macOS. |
| Linux | Run `node ./planwharf.cjs` or `sh ./planwharf.sh` in a terminal. | Launch paths are implemented; end-to-end operation has not been verified on Linux. |

On macOS and Linux, install Node.js, extract or clone the complete application folder, then launch from that folder:

```sh
cd /path/to/Planwharf
sh ./planwharf.sh
```

No `npm install` step is required. The shell launcher forwards command-line options, including `--plan`, `--attempts-dir` and `--port`. The Windows `.exe` and `.cmd` launchers are specific to Windows.

On every OS, install and authenticate each coding CLI in the same user environment that starts Planwharf. On macOS/Linux, CLI discovery looks for `codex`, `grok` and `agy` on `PATH`; for a custom location, set `PLANWHARF_CODEX`, `PLANWHARF_GROK` or `PLANWHARF_ANTIGRAVITY` to the executable's absolute path before launch. Each CLI must support the requested model and Planwharf's execution options on that OS; model recognition alone does not establish OS compatibility.

Planwharf attempts to open a Chromium application window and falls back to the default browser (`open` on macOS, `xdg-open` on Linux). If a browser cannot be launched, open the printed local address, normally `http://127.0.0.1:4173`, in a browser on the same computer. Opening `index.html` directly does not start file persistence. Keep the server process running while using the notebook; use **Quit application** or Ctrl+C to shut down safely.

`Planwharf.exe` is a launcher: keep it with the complete application folder, including `planwharf.cjs`, `engine`, and `fixtures`, and install Node.js on each computer. Startup diagnostics are saved per launch under `%LOCALAPPDATA%\Planwharf\logs`; an error dialog includes the log path. `PLANWHARF_LAUNCH_LOG_DIR` can select another log directory. Browser launch failures attempt to open the same notebook in the default browser.

Closing and immediately reopening the application clears stale window state. If native focus fails, the launcher requests a new window instead of silently accepting an old heartbeat. Interrupted startup locks recover automatically while preserving malformed lock bytes. A live, unresponsive notebook owner is retained and reported within approximately ten seconds. After updating the application, use **Quit application** before relaunching so the backend loads the fixes. See [startup verification](docs/STARTUP-VERIFICATION-2026-10-13.md) for the isolated launcher and browser checks.

The server binds to `127.0.0.1`, normally on port 4173, and chooses another port when necessary. Normal desktop launch opens one window with **Show Orchestrator** / **Show Planwharf** to switch views while preserving the draft and recipient. Both views share one backend. Older Orchestrator links open this page with the dashboard selected. Relaunching the same notebook reuses its backend and focuses or reopens the application. Closing the desktop window follows the same checkpoint and shutdown path as **Quit application**. **Quit application** or Ctrl+C requests shutdown and checkpoint saving. Browser heartbeats expire after 15 seconds if close events are unavailable.

## Start a project

Choose **Add project**, enter a name and goal, and set **Projects folder** to an existing absolute base directory. Click **Save projects folder**. With **Create a new subfolder using the project name** selected, Planwharf creates and links a new folder. Existing names receive a numbered suffix. Uncheck the option to link an existing project folder instead.

**Set place** updates a project's local folder and `https://github.com/owner/repo` URL. The base-directory setting persists in `<notebook>.settings.json`. Changing it affects future projects and does not move existing folders. **Remove project** removes the notebook entry while keeping its filesystem folder.

Use a separate notebook for experiments:

```powershell
node .\planwharf.cjs --plan .\my-notebook.json --attempts-dir .\my-attempts --port 4180
```

Explicit notebook paths must already contain valid Planwharf data. The default `plan.json` is local data, excluded from Git. On first launch only, the application creates it from `fixtures/starter-notebook.json` if it does not exist. Existing notebooks are never replaced. Preserve the notebook and its attempts directory when updating the application.

## Notebook, revisions, and Grill

- **Project Home** shows the current Living Plan goal, progress, changes since your last visit, current work, next known action, and personal notes. It renders without model inference.
- **Your notes** supports add, delete, and pin. **AI relevant: Yes/No** controls whether a note enters the AI briefing. The default is No. Notes and per-project visit snapshots persist inside each project?s optional `home` field in the notebook; they are never converted into tasks, revisions, or decisions.
- **Living Plan / History / Decisions / Revisions / Runs / Checkpoints / Technical details** expands the existing controls and history.
- **The plan** shows the selected revision's goal, current state, decisions, and open questions.
- **What changed** shows revision differences or the cumulative change since your last visit. Previous revisions remain immutable.
- **For you** contains questions awaiting answers. **Grill me** refines the goal; **Grill with the docs** includes project documentation.
- **Now** shows progress and actions such as Approve, Stop, and Resume. **All projects** switches the active notebook project.

An edit starts from the current revision and appends a new one. A Grill answer can leave a question open, close it with a decision, or close it and change direction. Existing decisions are preserved. Import accepts a complete revision object:

```json
{
  "title": "First month ready",
  "direction": "Open the library two mornings each week.",
  "state": "Two volunteers confirmed.",
  "closed": ["Loans are free."],
  "open": ["Which extra day should we add?"]
}
```

Malformed JSON, unknown fields, and invalid types are rejected without changing the notebook. Supported revision modes are `manual`, `grill`, and `import`. A task keeps its originating `revision_id` even after later edits. Each project can have at most one running task.

## Local files and private GitHub repositories

**Check repository** verifies local read/write access and authenticated GitHub access. It shows the compared branches, commits ahead/behind, uncommitted paths, and the time of the check. GitHub authentication uses the existing `gh` login, including private repositories accessible to that account.

The comparison fetches the linked repository's current branch history into dedicated inspection refs. It does not merge, push, switch branches, or overwrite working files. When the local branch is unpublished, the comparison explicitly uses GitHub's default branch as its baseline. Diverged histories and failures remain visible; old tracking refs are not reported as a current GitHub check.

The lead receives a compact briefing for the selected project, its active work, recent results, checkpoint, AI-relevant notes and repository delta. Older conversation exchanges stay in the local archive; only six bounded recent turns are sent. Registered project paths support explicit recipient selection; the lead must inspect only the selected project unless the current request names another. By default its CLI retains the existing full filesystem/network permissions without interactive approvals; the per-provider Always approve / YOLO policy can restrict future launches. Its prompt keeps it in a coordination role: workers implement jobs. A role instruction is not a filesystem sandbox.

The account must already have repository access. A URL alone cannot grant it. Check `gh auth status` or use `gh auth login` if authentication is missing. Credentials are not embedded in notebooks or model prompts.

## Providers and execution

`models.json` uses schema 3 with a `providers` array; each provider declares its adapter, quota pool, and model entries. Model entries retain explicit provider and CLI model IDs. The runner launches that adapter and exact model; it rejects incompatible combinations and never silently routes GPT or Gemini labels to Grok. The configuration examples below describe individual entries inside a provider's `models` array.

Model recognition is extensible through configuration. The installed adapters are `codex`, `antigravity`, `grok`, `api`, and `configured-cli`; a model name such as Claude, Qwen, or Kimi is not an adapter. Native model families retain their compatibility guard. For a different exact model ID that your selected CLI has explicitly been configured to accept, add `"modelRouting": "configured"`. This removes the family-name restriction while retaining the exact provider and CLI ID. It does not install a provider, verify account access, or change a CLI's supported models. Unknown adapters remain unavailable with a clear error.

For example, this is a disabled template for a Qwen route through a separately configured compatible Antigravity CLI. Replace the placeholder with the exact ID accepted by your CLI; use the same pattern for Kimi or any future model. Claude IDs already match Antigravity's native family guard, but still require actual account access. Leave unverified entries disabled and unavailable until you have checked the selected CLI and account:

```json
{
  "id": "my-qwen",
  "label": "Qwen (configured route)",
  "provider": "antigravity",
  "cli": "EXACT_MODEL_ID_FROM_YOUR_CLI",
  "modelRouting": "configured",
  "enabled": false,
  "available": false,
  "unavailableReason": "This exact provider/model route has not been verified.",
  "plan": "My configured account",
  "kind": "default",
  "cost": 1,
  "limit": 50
}
```

Labels are for display; they never select a provider. Configuration errors disable the roster entry and show the reason. A compatible declaration permits dispatch only after you enable the entry and mark it available; the CLI may still reject model access at runtime, and Planwharf reports that failure without substituting another model.

On the verified account, GPT-6 Astra, GPT-6 Luna, GPT-6 Sol, GPT-6.1 Sol, GPT-5.6 Luna, and GPT-5.5 completed inference checks. Gemini Pro and Flash were used through Antigravity. GPT-5 and GPT-5 mini were explicitly rejected by Codex with this ChatGPT account and remain **Unavailable**. Grok 4.6 is switched off after a credit/spending-limit HTTP 403. These checks describe one account and date, rather than universal model availability.

Choose a lead in **Functions** or the Orchestrator view. **Auto start** inspects the project, proposes Grill when configured, prepares a job, and waits for **Approve run**. Auto-start jobs require that approval even in Automatic mode. New jobs propose a persistent MegaTask and its dependent slices before execution. Approval binds to the exact MegaTask; Automatic mode does not bypass that approval. Worker tool permissions do not require repeated interactive confirmation.

Enabled models are candidates, not a requirement to use every model for every task. Selection considers task type, provider quota, local counters, and failures. Implementation/difficult work may be split across up to three workers. Slice ownership and integration still require care.

The dashboard reports process IDs, logs, interpreted activity, and provider usage. The **Project recipient** in the Orchestrator view stays synchronized with project selection in Planwharf, including after reload. Each chat request carries that project's explicit ID. A missing or deleted recipient, or a message naming a different project, cannot execute a project command. Chat never chooses a project based on which one is busiest.

Project actions require an explicit command such as **Start**, **Resume**, **Stop**, **Approve**, or **Skip grill**, optionally followed by the selected project's name. A new work request such as “Audit Sayaku” creates a fresh task with the exact current prompt and waits for approval. Previous inactive tasks are retained as superseded; their checkpoints are archived unchanged. An active job must be stopped before replacing it. Questions do not create tasks, and a new request never implicitly resumes a previous checkpoint. Research assignments are also checked against the selected recipient. **Copy output** sits directly below each generated answer and copies that answer.

Matt skills are checked at startup against the latest `main` revision of `mattpocock/skills`. The visible status reports the revision, update result and skill count. Planwharf downloads the complete skills tree into an immutable revision folder before activating it; a failed update keeps the prior complete version and reports the failure. Every Codex, Grok and Antigravity lead/worker prompt receives the catalog and applicable skill instructions. **Use Matt skills for all models** is on by default and persists per notebook; turning it off applies to subsequent requests and launches. If enabled skills have no complete cache, model execution waits for a successful check or an explicit opt-out.

After updating Planwharf code, use **Quit application** and relaunch to load the new backend. Closing the desktop window quits that backend; the next launch starts a fresh process. The UI identifies older backends that lack a catalog count and never presents a missing count as “0 skills” verified successfully. A matching commit SHA is insufficient: the complete catalog and its files must also be present; incomplete caches are rebuilt even when the SHA has not changed.

The skills status separates the upstream revision date, local installation time and last check time. Checking an unchanged revision does not overwrite its installation time. The switch saves the setting on the backend, and an older polling response cannot revert the displayed choice.

**Refresh models** queries supported provider catalogs for the active CLI login, replacing old account availability without substituting models or automatically assigning roles. A fresh check runs at backend startup before model selection and dispatch. Models absent from a successfully read catalog disappear from model cards and selectors, while saved roles, pinned selections and historical execution evidence retain their exact IDs. Newly discovered IDs start with no assigned roles. The last successful catalog is saved locally per provider configuration and recognized account; a failed login, network or malformed-catalog check retains it and reports verification as unread. An explicitly empty catalog removes every model from that provider. Unchanged-login status polling does not invoke provider CLIs. Detected account changes trigger a new catalog check and invalidate cached quota. Confirmed organizational accounts add **Business** to their labels; normal accounts keep their names. Role flags are saved separately for recognized accounts. Authentication and policy failures are shown as verification failures, not as invented account access.

A failed model-list check preserves previously known availability and shows a warning; it does not fabricate access to other IDs. An unreadable Grok quota endpoint is separate from access to the Grok CLI and does not itself disable models.

Chat requests accept up to 1 MiB of JSON. Provider prompts avoid the Windows command-line length limit: Codex uses standard input, Antigravity uses its documented JSON user-event stream on standard input, and Grok reads a unique UTF-8 prompt file through its native `--prompt-file` option. Prompt files are kept outside linked project folders. Saved exchanges are retained without a fixed turn limit. Each lead answer receives the full retained history once in chronological order, together with the current notebook briefing. The provider's context limit still applies; automatic history compaction is not implemented.

You can ask the lead to delegate a research task conversationally, naming a worker such as GPT-6 Luna. The lead proposes a structured research assignment; the notebook validates the exact enabled model and its usage, then returns a persistent MegaTask proposal. Review the slices and approve that research MegaTask before any worker starts. Dispatch records then report actual process IDs or launch failures. Research uses a separate attempt folder, preserves existing implementation tasks, and writes findings and citations to `report.md`. The Workers view shows its process, activity, report, Stop, and Resume actions. A completed report requires both the report artifact and successful provider exit evidence.

Source-protected research currently uses Codex with its workspace write sandbox and network access. Linked project files are available for reading; only the isolated research workspace is writable. Other providers are rejected for this workflow until equivalent source protection is supported. Resume creates a new attempt while keeping prior research evidence. A lead's proposed assignment is not evidence that a worker has started; the notebook's dispatch receipt is authoritative.

## Stop, Resume, and quotas

Stop terminates worker process trees and saves a checkpoint. Phase 2 also preserves remaining work after worker failures, pause, and shutdown/restart. At the first scheduling check at or after **Finish by**, it blocks new slices, terminates active worker process trees, and saves a deadline checkpoint with completed results and partial evidence retained. Valid completion of every slice takes precedence over interruption. Termination failures remain visible and are retried; they do not falsely mark live workers as stopped. Checkpoints have no expiry and remain **Paused / Checkpoint saved / Resume** across restarts. Resume verifies the job identity and creates fresh attempts for remaining work. Explicit Resume after a deadline continues beyond the original expired time. It does not restore the interrupted model's internal memory. See [deadline verification](docs/DEADLINE-VERIFICATION-2026-10-13.md).

**Stop worker** on a running implementation worker card terminates only that process tree. Other slices continue. Automatic replacement for that project is suspended until an explicit resume; when the other slices finish, Planwharf saves a checkpoint instead of declaring the stopped slice complete. This pause survives backend restoration. Research cards have their own independent **Stop** action.

The Workers section starts clean on each page opening. Default filters show the selected project and workers launched during the current view session. Project, session and state filters expose previous attempts when wanted; the legend always reports the total number of active processes, including hidden sessions. Existing tasks cannot start automatically after backend startup, even when Automatic mode was saved; they require fresh authorization. Workers already running continue to be monitored.

The session selector lists recorded sessions by date and ID, with a separate entry for historical records from before session tracking. Chat also opens clean and passes a new conversation ID to the lead; old conversations remain available through the session selector. Each answer has **Copy output**, and **Copy displayed conversation** beneath the thread copies every visible exchange.

Running activity translates structured Codex, Grok and Antigravity events into readable actions such as “Reading Sayaku.ts” or “Running tests for Sayaku.test.ts”. It follows the latest action and distinguishes a completed command from proof that tests passed. Raw logs remain unchanged in the output details. Project Progress supplements the original Living Plan text with the last work item, recent readable worker actions, full reported findings and changed file names. Since your last visit includes commit subjects, edited files and newly recorded decisions rather than counts alone.

Keep the notebook, settings, attempts directory, and project files together in backups. Forced termination or power loss cannot guarantee saving the last unsaved operation. The running backend monitors active work on a periodic timer, including with the window closed; the interface also polls for updates.

When all worker slices report completion, Planwharf closes the assignment and marks the notebook task `complete`. Later polls and restarts retain that terminal state. Historical process IDs remain recorded, with inactive worker flags; completed tasks cannot resume from an older checkpoint.

To inspect older inconsistent state without dispatching workers, run:

```powershell
node validation/reconcile-attempts.cjs --plan plan.json --attempts-dir .attempts-plan
```

After quitting the application, add `--apply` to repair verified completions. The tool requires matching job identity and `STATUS: COMPLETE` plus `EXIT: 0` for every recorded slice. It backs up the notebook, registry, and orchestrator state before writing, archives state for removed projects, and preserves raw logs, results, and checkpoints. Browser profiles under `windows/` and validation artifacts stay local and are excluded from Git.

Provider quota readings are separate from synthetic model assignment counters. ChatGPT uses local Codex usage windows; Grok uses local billing; Antigravity reads numeric Gemini buckets through `agy -p /usage --output-format json`. All quota UI reports **REMAINING**, shared by every model on the same provider plan. Bands are green at 75?100%, orange at 50?74%, yellow at 25?49%, and red at 0?24%; 0% also says **EXHAUSTED**. Missing or invalid readings show grey **REMAINING UNREAD**, rather than an invented percentage.

## Validation and historical evidence

```powershell
node .\engine\prompt-transport.test.cjs
node .\engine\provider-transport.test.cjs
node .\engine\chat-request.test.cjs
node .\engine\conversation-history.test.cjs
node .\engine\research-dispatch.test.cjs
node .\engine\project-recipient.test.cjs
node .\engine\runtime-integration.test.cjs
node .\engine\worker-ui.test.cjs
node .\engine\worker-startup.test.cjs
node .\validation\worker-board-browser.cjs
node .\validation\project-recipient-browser.cjs
node .\validation\verify-work.cjs
node .\validation\project-folder-ui.cjs
```

Tests cover notebook persistence, revisions, Grill, task references, filesystem settings, provider routing, checkpoints, shutdown/restart, Git comparison graphs, and errors. Test notebooks and attempts are isolated from personal projects. `verify-ui.cjs` is a DOM simulation; the folder UI check uses a real headless browser.

A previous real orchestrator run produced a Windows calculator through Gemini workers and passed 15 GUI checks plus five engine self-tests. Its project was subsequently removed at the owner's request. Preserved local execution evidence documents that historical run; it is not a currently installed calculator.

Historical `handoff` payloads and execution logs preserve their original language as evidence. Current product documentation is in English. Personal notebook content and test fixture text are user/sample data, rather than translated product documentation.

Phase 1 validation: `node validation/phase1-suite.cjs` runs every engine test, root verification script, browser folder/worker checks, and real app-window checks. Each run writes a new evidence directory and verifies that the personal notebook is unchanged. The isolated browser tests use mocked quotas and no model dispatch. Chromium sandbox restrictions in the validation environment require `--no-sandbox` for tests only; normal launch retains browser defaults.

## Usage policies and repository integrations

See [usage and integration policies](docs/USAGE-AND-INTEGRATION-POLICIES.md) for model usage thresholds, popup notices, explicit fallback routes, per-provider Always approve / YOLO controls, Gitea authentication, periodic commit checks, and provider error reports. Repository checks update information and inspection refs only; project source is preserved.

## License

Planwharf is free software licensed under the **GNU General Public License version 3 only (GPL-3.0-only)**. See [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).

You may use, study, modify, and redistribute Planwharf, including for commercial use, paid distribution, and paid support, when you comply with GPL-3.0-only. No automatic royalty is required for compliant commercial distribution. Private modifications that are not conveyed do not by themselves require source publication.

A separate license from the maintainer is required only to distribute Planwharf or a covered derivative without complying with the applicable GPL obligations, for example under incompatible proprietary terms. Possible commercial terms can be negotiated later; no such agreement or royalty rate is established here.

External contributions require the applicable [Individual CLA](CLA.md) or [Entity CLA](CLA-ENTITY.md); contributors retain copyright, and the public project remains GPL. See [CONTRIBUTING.md](CONTRIBUTING.md) and [licensing details](docs/LICENSING-PROPOSAL.md). The GPL does not grant rights to present a fork as the official Planwharf project or as endorsed by its maintainer. Third-party notices and licenses remain applicable.

Model roles offer Select all and Deselect all for Orchestrator, Worker and Reviewer. These controls include models hidden by dashboard filters and skip incompatible roles.

Choose a preferred worker in the Gold model section, then select Gold model below the orchestrator chat for a new work request or Start. The selector returns to Default after a successful send. Gold saves the exact provider/model with that task and its implementation or research jobs, including across approval, pause and restart; it never falls back to another worker. Worker permission and provider quota limits still apply. Reviewers retain their configured settings. Use Default to approve or resume an existing task; its saved Gold constraint remains active.

Drag the separators between the projects sidebar and content, or between orchestrator chat and dashboard, to adjust panel widths. Focus a separator and use Left/Right arrows (Shift for larger steps), Home or End for keyboard resizing. Double-click a separator to reset that panel, or choose Reset layout to restore all widths. Sizes are saved in this browser. Narrow windows automatically stack panels and hide width handles; saved proportions return when space is available. Layout preferences do not change notebook data.

Horizontal separators adjust conversation height and individual dashboard section heights. Use Up/Down arrows for keyboard control. Resized sections scroll internally, keep their saved heights across dashboard refreshes, and return to natural heights on narrow screens. Reset layout also restores section heights.

Planwharf checks the latest stable GitHub release at desktop startup without downloading or installing it. **Check updates** repeats that check; **Update available** opens release details. Updating always requires a click. When tasks are unfinished, the dialog offers **Update anyway** and **No, I'll do it later**. Every unfinished task is checkpointed before installation; a failed checkpoint or worker stop cancels the update. After restart, use Resume to continue saved work.

For in-app updates, download and extract the **Planwharf-vX.Y.Z.zip** portable asset from a release that includes this feature. Older releases and GitHub source ZIPs need that first manual download. Git checkouts show the release link and never replace local source files automatically. Portable installations verify the official package checksum, refuse to overwrite locally modified application files, preserve notebooks/settings/credentials/execution evidence, and keep backups under `.planwharf-updates`. Installation errors restore previous application files and are reported on restart. Node.js remains a separate prerequisite.

**Grill me** in the Orchestrator dashboard starts a Matt grilling interview directly in the selected project's chat, including projects without a local folder. Replies remain clarification until an explicit Start; the resulting task retains the discussion and still needs approval before workers run. Enable Use Matt skills and select an available orchestrator first. Stop grill ends that chat interview without changing global preferences. Antigravity availability is refreshed through `agy models`; configured model IDs remain unverified until the active CLI lists them, and rejected model IDs are never replaced silently.
