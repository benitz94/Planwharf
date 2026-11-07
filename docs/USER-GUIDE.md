# Planwharf user guide

Planwharf runs a team of AI models on your computer to build what you describe. **You are the captain**: you set the goal, choose the team, and start or stop the work. A **lead** model talks with you and plans; **builders** write the code and tests; **verifiers** check that work and fix it.

For a two-minute tour of the screen, read [the chat view](CHAT-VIEW.md). To build something right away, follow the [first project guide](HOW-TO.md).

## Install and launch

Install Node.js. Planwharf has no npm dependencies. Download `Planwharf-vX.Y.Z.zip` from the [latest GitHub Release](https://github.com/benitz94/Planwharf/releases/latest) under **Assets** and extract the whole archive. The same ZIP works on Windows, macOS and Linux. `planwharf-update.json.gz` is update metadata, not the application. GitHub's **Source code** archives are repository snapshots.

- **Windows:** open `Planwharf.exe` (or `planwharf.cmd`, or `node planwharf.cjs`).
- **macOS / Linux:** run `sh ./planwharf.sh` or `node ./planwharf.cjs`.

`Planwharf.exe` is a launcher: keep it with the rest of the folder. Windows is the verified desktop platform.

Planwharf listens on `127.0.0.1`, normally port 4173, and opens one window on the chat. Opening it again reuses the running app. Closing the window, or **Settings → Quit**, saves unfinished work and shuts Planwharf down.

Terminal options:

- `node planwharf.cjs projects` lists projects;
- `node planwharf.cjs status` reads the recorded status without starting work;
- `--plan`, `--attempts-dir` and `--port` choose the notebook, the evidence folder and the port;
- `--browser` picks a browser name, `default`, or an executable path;
- `--no-window` starts without a window.

For a separate practice notebook:

```powershell
Copy-Item fixtures/starter-notebook.json practice.json
node planwharf.cjs --plan practice.json --attempts-dir practice-attempts --port 4180
```

The default notebook, `plan.json`, is local data and is never committed. First launch creates it from the starter fixture only if it is missing.

## Projects and folders

**+ New project** asks for a name, what to build, and a folder. Leave the folder empty to create one inside the default projects folder (**Settings → New projects**). **Settings → This project** changes the folder later. **Settings → Remove project** removes the project from Planwharf; its folder stays on disk.

Workers run only inside the project folder. A missing folder blocks the work. This is a check on where work starts, not an operating-system sandbox for the AI tools.

Git evidence is unavailable for inherited parent repositories, external worktrees and redirected Git metadata, and submodules are not inspected. Planwharf reports these cases instead of guessing.

## The team and its roles

Open **Team**. Each model shows one button per role:

| Role on screen | Role name in files | Job | What it never does |
| --- | --- | --- | --- |
| Lead (selector) | Orchestrator | Talks with you, plans, picks who does each step. | Build or verify, unless you also give it those roles. |
| Build | Worker | Writes the code and its tests. | Check its own work as the verifier. |
| Verify | Verifier | Checks the builder's work independently and fixes what fails. Planwharf prefers a verifier from another company. | Build the job. With no verifier the job runs as one step and Planwharf only runs the tests. |
| Review (optional) | Reviewer | Reads the work and reports problems, without changing files. | Implementation. |

Nobody needs to be picked to watch the work: Planwharf always does it by itself and tells the lead when something stalls or fails ([Supervisor](SUPERVISOR.md)).

A model with no role sits on the **bench** and is never used. Roles are permissions, not rankings: changing them affects the next assignment, not work already running. A role a model cannot do is shown disabled.

**AI tools** lists each provider with an **On/Off** switch; a tool that is off is never used. **Look for models again** rereads the models available to the accounts signed in on this computer. Planwharf runs this check at every start too. A model that disappears from a provider's list is removed from Team; saved roles and past evidence keep its exact ID. New models start on the bench.

Supported tools: Codex / ChatGPT, Grok CLI, Antigravity / Gemini and Claude Code (see [Claude Code setup](EXTERNAL-PROVIDERS.md#claude-code-subscription-cli)). Sign in to each tool separately; Planwharf uses that login and never asks for your password.

**Connect a model by API** adds an OpenAI-compatible or Anthropic Messages API, for example DeepSeek or Kimi. Put the API key in an environment variable and give Planwharf only the variable's name. Tick **These models can use tools** only if the models can call tools; building code needs it. See [external providers](EXTERNAL-PROVIDERS.md).

### Personal and Business accounts

Confirmed team, Business or Enterprise accounts add **Business** to model names. Roles are saved per recognised account: switching back to an account restores its roles, and a new account starts with no roles. Antigravity and Claude Code do not expose account type, so their names do not change. Stop work before switching a tool's account, then press **Look for models again**.

## Working with the lead

Write in the chat what you want. Ordinary conversation never starts work by itself.

- When the lead proposes work, a card shows the steps and who does each. **Approve and start** launches it; to change it, tell the lead.
- **Let the crew do it** turns on [project autonomy](PROJECT-AUTONOMY.md). The crew then plans, approves its own steps within limits, builds, verifies and stops when the goal is reached or a limit is hit. **Stop the crew** turns it off.

Short commands such as `show usage`, `files`, `settings` or `bench <model>` are handled by Planwharf without calling a model; see [the chat view](CHAT-VIEW.md#commands-in-the-chat). Each answer shows the time it took. **How the lead got there** shows the lead's reasoning when the tool provides it.

The lead receives a short briefing on the project: goal, current work, recent results, saved point and repository state. Earlier conversation turns are sent in order; the tool's own context limit still applies.

## Following the work

While the crew works, a **Crew at work** card in the chat shows:

- a progress bar;
- each step, with its role and model, and a coloured dot for each provider;
- the state of each step: waiting, working, done, failed;
- **What … is doing**: the latest actions of the running model, in plain words.

Short lines in the chat record each milestone. **Progress** lists the steps and recent activity; **Files** lists files changed since the last Git commit.

A step is done only when the worker leaves a valid structured result and exits cleanly. A process ID or an optimistic message is not proof. When everything is done, a **Work finished** card summarises steps, time and uncommitted files. Planwharf never commits, pushes or publishes on its own: test the result in the project folder.

Terms you may meet in files and logs:

- a **MegaTask** is one approved piece of work;
- a **SubTask** is one step of it;
- an **Attempt** is one run of a model on a step;
- a **Result** records the outcome, files, tests and blockers;
- a **Checkpoint** is a saved point to resume from.

## Stop and resume

**Stop work** ends the running workers and saves a checkpoint. Partial files stay as they are. **Resume** continues from the checkpoint. It keeps finished steps and starts fresh attempts only for the rest. If the repository or plan changed since the checkpoint, Planwharf says so and asks you to confirm. Resuming does not restore a model's internal memory. After a restart, unfinished work waits for you; it never restarts on its own.

## Quota and usage

**Usage** shows the quota **left** in each provider's shared pool. Every model on the same plan shares that value. When a provider does not report quota, Planwharf shows the provider's own reason instead of a number.

Lead calls and crew work list calls, time and output tokens. A provider that does not report tokens shows "not reported", never zero or an estimate. This is operational evidence, not a bill.

## Move a project to another computer

**Settings → Move a project → Save … to a file** downloads the project as JSON. The file holds plan revisions, notes, tasks, results, checkpoints, history and a readable summary. It never includes source code, raw logs or credentials. **Open a project file** adds it as a new project; it never overwrites one. Check the folder in Settings, then press **Resume** if work was saved. See [Continue work](CONTINUE-WORK.md).

## Updates

Planwharf checks for the latest release when it starts, without downloading anything; **Check again** repeats the check. When one is available, a link appears at the bottom left. **Settings → Updates → Update and restart** installs it after a second confirmation. Unfinished work is checkpointed first; if that fails, the update stops. After the restart, press **Resume** to continue.

## Not in the chat view

These earlier controls are not available in the current screen:

- personal notes;
- the clarification interview (Grill);
- **Pause after current tasks** and **Finish by** deadlines;
- Gold model;
- per-model and per-provider usage policies (Always approve / YOLO);
- linking a GitHub URL and the remote comparison;
- the browser picker (use `--browser`).

Data they saved is kept and still applies; for example, a provider policy set earlier remains in force.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| The launcher cannot find Node.js | Install Node.js and keep the whole application folder together. The error dialog names its log file. |
| "Let the crew do it" is disabled | The line next to it says what is missing: a lead, a builder or the folder. |
| A model is missing | Sign in to that tool, check that it is **On** in Team, then press **Look for models again**. |
| No model can take a step | Give a model the role that step needs (Build or Verify), and check its quota. |
| Quota shows a reason instead of a number | The provider did not report it; Planwharf does not guess. |
| Resume asks for confirmation | The folder or plan changed since the checkpoint; review, then confirm. |
| Old behaviour after updating files by hand | Quit Planwharf and start it again. |

Keep TLS verification on. See [project status](PROJECT-STATUS.md) for verified platforms and limits, [provider adapters](PROVIDER-ADAPTERS.md) for developers, and [third-party notices](THIRD-PARTY-NOTICES.md).
