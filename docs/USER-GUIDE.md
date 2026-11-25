# Planwharf user guide

Planwharf runs a team of AI models on your computer to build what you describe. **You are the captain**: you set the goal, choose the team, and start or stop the work. A **lead** model talks with you and plans; **builders** write the code and tests; **verifiers** check that work and fix it.

This guide describes the **native app**, the version developed from now on. The desktop app (Electron) and the web app are deprecated: they still work, see [Desktop app and web app](DESKTOP.md). For a two-minute tour of the screen, read [the chat view](CHAT-VIEW.md); to build something right away, follow the [first project guide](HOW-TO.md).

## Install and launch

1. Install and sign in to at least one AI coding tool: Codex (ChatGPT), Claude Code, Grok CLI or Antigravity (Gemini). Planwharf uses those sign-ins and never asks for a password.
2. Download the native app for your system from the [latest release](https://github.com/benitz94/Planwharf/releases/latest): `Planwharf-native-win-x64-vX.Y.Z.zip` (or `…-win-arm64-…`), `…-linux-x64-…` (or `…-linux-arm64-…`), `…-osx-arm64-…` or `…-osx-x64-…`.
3. Extract the zip anywhere and open `Planwharf` (`Planwharf.exe` on Windows). Nothing else to install: the zip carries what it needs.

Your projects and settings are kept in your user folder (`%APPDATA%\Planwharf\data`, `~/.config/Planwharf/data`, `~/Library/Application Support/Planwharf/data`), never in the app folder, so updating or moving the app does not touch them. Use one Planwharf at a time on the same data.

**Updates:** **Settings → Updates → Update now** downloads the new version, checks it against the digest GitHub publishes, saves running work, and restarts. After the restart, press **Resume** if work was running.

**Settings:** Everyday settings (new project folders, look, phone alerts, updates) are shown first. Advanced settings (approvals, automatic model switches, project tools, skills, project moving) start closed in an **Advanced settings** section.

**Language:** the app is in English.

## Projects and folders

**+ New project** asks for a name and a folder; leave the folder empty to create one inside the projects folder (**Settings**). The project list on the left shows the project you used last first, with its status and who is working. **Settings → Remove project** removes it from Planwharf; its folder stays on disk.

Workers run only inside the project folder. This is a check on where work starts, not an operating-system sandbox for the AI tools.

## The team

Open **Team**. Each project has its own team.

At the top of Team, **Saving** chooses which models this project may use. The choice is saved, shown beside the project name and on the Crew at work card, and it applies from the next step. Work that is already running keeps its model.

| Mode | Who may work |
| --- | --- |
| **Normal** | Today's picking. Flagship, mid and light models can work. This is the default. |
| **Safe** | No flagship model of any company. Mid and light only, for the lead and the crew. |
| **Ultra safe** | Light models only, for the lead and the crew. A light model verifies, and the chat says so once. |

A model's class comes from its company's line-up (the same ranking as the lead). Gemini Flash (High) stays light. A weight you set on the model in Team wins over that line-up. Quota limits still hold inside the mode. If the mode leaves every model out, the step does not start and the chat says which models were left out and which are under their limit. If no lead is allowed, Planwharf keeps the best model of the lowest class that can still answer, and says so once.

The same choice can be typed: `mode normal`, `mode safe`, `mode ultra safe`, or in Italian `modalità normale`, `modalità sicura`, `modalità ultra sicura`.

| On screen | Job |
| --- | --- |
| Lead (menu at the top) | Talks with you, plans, picks who does each step. The menu lists the leads from the most capable down; if the lead runs out of quota, or does not answer, the next one takes over. The quota limits hold for the lead too: a model under its limit takes over only when no other can lead. The **Lead** button on each model says whether it may lead this project: switch it off and the model is left out of the menu and of every hand-over. |
| Build | Writes the code and its tests. |
| Verify | Checks the builder's work independently and fixes what fails. A strong model is preferred: a light one verifies only when nobody else can. |
| Bench | Available, but never used until you give it a role. |

Each model card shows its quota left (5-hour and weekly windows), its **weight** (light, default, premium: light parts of the work go to light models, heavy parts to strong ones), and:

- **Limit and tools**: model limits and tools (MCP servers, browser, hooks, memory) are kept in a section that opens on demand to keep model cards simple. The general limit, 35% of both windows, is in **Settings**.
- **No limit** (on the model card, next to the roles): the model ignores the quota limit and works until its company reports the quota spent. It only removes the limit: the model is not preferred to the others, and the parts are still spread over the crew. Click it again to give the model back its limit. **No limit for all …** in **Limit and tools** does the same for every model of a company that has no limit of its own.

**Find models** filters by company, weight and state, with **All / None** to give or take a role for every model shown. **AI tools** turns each program on or off, and **Look for models again** rereads the models of the accounts signed in on this computer. Models of an expired plan or a signed-out account are hidden, with one line saying why. A model that answers it needs usage credits (outside your plan) is hidden for a day, and the lead and the crew move on to the next model. Where a program allows it (Claude Code, Codex), **Add another account** uses a second subscription of the same company before moving to another company.

**Connect a model by API** adds an OpenAI-compatible or Anthropic Messages API (DeepSeek, Kimi…): the key stays in an environment variable, Planwharf stores only its name. See [external providers](EXTERNAL-PROVIDERS.md).

## Working with the lead

Above the chat, a switch chooses how this project talks to the lead. **Plan** is the default; the choice is saved for the project.

- **Talk.** A conversation, to think an idea through. The lead does not suggest a goal for the crew, and the chat offers neither **Use as the goal** nor **Let the crew do it**. Nothing starts.
- **Plan.** Today's behaviour. The lead shapes a goal, and you press **Let the crew do it**.

Moving the switch leaves running work going. **Stop the crew** is what stops the work at once. Work never starts by itself: only **Let the crew do it** starts it.

The same positions can be typed: `mode talk` or `modalità chat`, `mode plan`, `mode auto`, and `mode manual` (Plan, and you approve each start). The lead's answer names the same position as the switch.

Write in the chat what you want. In Talk and Plan, ordinary conversation never starts work by itself. Under each of your messages, **Copy** copies it and **Use again** puts it back in the box. Short orders such as `usage`, `files`, `settings`, `bench <model>`, `stop` are handled by Planwharf without calling a model ([the chat view](CHAT-VIEW.md#commands-in-the-chat)).

In Plan, when the lead suggests a goal, **Let the crew do it** starts it. The crew then works on its own: the lead splits the work, approves its own steps within limits, and stops when the goal is reached or a limit is hit. **Stop the crew** stops it at any time.

While work is running, if you talk with the lead about something to add, the lead's answer ends with a line starting with "Addition to this work:" instead of a suggested next goal. Under that answer, click **Add to this work**: Planwharf appends one Build step before Verify with the addition as its detail. The new step follows the project team, quota limits and saving mode, and starts as soon as an allowed model is free (a step running on the same files goes first). If Verify had already started, it is run again after the new step on the same branch. A stopped work takes the addition too and resumes from its checkpoint; once work is finished, the addition becomes a new goal proposal as usual.

## What happens after "Let the crew do it"

1. **Safe start.** Planwharf checks that the project folder has no uncommitted changes from something else, fetches GitHub, and creates the work's branch from the latest `main` (or goes on with the work's own branch). It writes the work plan, `.planwharf/plan.md`, kept out of git unless you choose **Settings → Keep the work plan in git**.
2. **Split.** The lead divides the work into parts that can run at the same time, each with its own files and weight. Parallel parts each work in their own copy of the folder (a git worktree), and are joined at the end.
3. **Build, then Verify.** Each part is built; then another model checks the whole work and fixes what fails. A step is done only when the worker leaves a valid result and exits cleanly.
4. **Quota watch.** A model that falls under its limit hands its step to another model with a handoff (files changed, tests, summary). When no other model can take the step (no model with that role above its limit), the work stops with a checkpoint, and the chat says why each other model cannot take it; **Resume** continues once quota is back. A step never starts on a model under its limit: a step still waiting its turn (Verify, usually) moves to a model above its limit before it starts, or waits for quota. A step that ends with no result is tried once more, then moves to the next model. A lead that runs out of quota hands over to the next lead at once.
5. **Decisions and the Decisions inbox.** Decisions only you can decide appear in the chat under their work and in **Decisions** in the top bar (with a badge showing how many are open across all projects and pipeline jobs). Each card shows the project and work, the question in plain words, the suggestion, whether a wrong choice could break something (highlighted with an alert icon), and buttons to answer (**Yes**, **No**, custom answer, or **Let the lead decide** for just that decision). Answered decisions move to "Answered decisions" with who answered and when; an answer can be changed until the step that needs it starts. In the inbox (per project) or in **Settings** (as a default), turn on **Auto** to let the project's lead decide each new decision itself with a recorded reason so work continues without waiting; breaking decisions decided under Auto remain highlighted so you can review and override them if needed.
6. **Finished.** The **Work finished** card shows three lines at the top: what was done, whether it works, and what to do now, with the matching action button (such as try it in the project folder, Push commits, or answer an open decision). Below, detail sections start closed and open with one click: steps and their models, what each worker reported, tests, files changed, git state, time and what the work used.

The **Crew at work** card shows the steps with plain lines from the lead ("Opus is building the weather page"), with worker instructions tucked inside closed details. The event timeline joins repeated events (showing a multiplier like `(×3)`) and keeps long text behind a "more" button. **Progress**, **Usage**, **Files** and **Pull requests** are in the panel on the right.

## Commits and pushes

The crew commits on the work's branch and never pushes: every worker runs with a git setting that makes any push fail. You publish: **Push** sends the branch to GitHub under its own name, opens the pull request (named after the work, with a **Crew** section naming the lead and the models that built and verified each step), and never pushes `main`. If GitHub refuses a private email address in the commits, they get your GitHub noreply address and the push is tried again. You merge the pull request on GitHub. **Pull requests**, next to Team, Progress, Usage, Files and Settings, lists the open pull requests (number, title, branch, dates, named checks and conflicts) and the merged and closed ones, 30 at a time. The top of the panel summarises the last 30 days. **Fix in the background** on a pull request with conflicts or red checks fixes that one pull request on a copy of the folder and pushes to the same pull request. While it runs, the pull request's card shows who is fixing it, for how long, the stage, and what it did last. **Stop the fix** ends it: the model stops and nothing is pushed. A check GitHub cancelled (no free machine) shows as **Not run**; Fix in the background runs it again without calling a model. If the push is refused, that copy and its commit stay on this computer. No work opens in the chat: a line says when it starts and when it is done, and while it runs the pull request's card in **Pull requests** shows who is fixing it, for how long, the stage and what it did last. Without the GitHub CLI (`gh`), the panel says what to install and the rest of the app keeps working.

## Alerts

- **On the computer:** when a work ends, fails or needs you: a notification, the taskbar flashing and a short sound, each switchable in **Settings → Look**.
- **On the phone:** **Settings → Phone**: ntfy (a private topic name, no account), or a Telegram bot you create with BotFather and pair with the code shown there. The bot reports stops, resumes, verification, decisions and model switches, and answers `/status`, `/decisions` (listing open decisions with quick answer options or lead delegation), `/stop <project>` (after "yes") and `/resume <project>`. Each message has one item per line and names the project's lead and the Build and Verify models, with an arrow when one took over; a new lead sends a message of its own. Messages carry only the project name, the goal, the state, the times and the model names: never code, file names or paths. Examples of the messages: [Planwharf on your phone](PHONE.md).

## Stop and resume

**Stop the crew** ends the running workers and saves a checkpoint; partial files stay. **Resume** continues from it, keeping finished steps. If the folder or the plan changed meanwhile, Planwharf says so and asks you to confirm. After a restart, unfinished work waits for you.

## Quota and usage

**Usage** shows the quota left for each company and window, with reset times, the status of each company, and the calls, time and tokens of the lead and the crew. A provider that does not report a value shows "not reported", never a guess.

## Move a project to another computer

**Settings → Move a project → Save … to a file** saves the project's plan, notes, tasks, results, checkpoints and history as JSON (never source code, raw logs or credentials). **Open a project file** adds it as a new project. See [Continue work](CONTINUE-WORK.md).

## Troubleshooting

| Symptom | Check |
| --- | --- |
| "Let the crew do it" is disabled | The line next to it says what is missing: a lead, a builder, the folder, or a goal. |
| "Commit or stash the changes in the folder first" | Files in the folder do not belong to this work. Commit them, or set them aside with `git stash push -u`. |
| A model is missing | Sign in to that tool, check it is **On** in Team, then **Look for models again**. A hidden model may have an expired plan. |
| No model can take a step | Give a model the role the step needs (Build or Verify), and check its quota and limit. |
| A quota shows a reason instead of a number | The provider did not report it; Planwharf does not guess. |
| "An earlier Planwharf is stuck" at start | A Planwharf engine from before (after an update, or a window closed without Quit) still holds your data. In Task Manager, Details, end `node.exe`, then open Planwharf again. An engine that still answers is reused by itself. |
| The push is refused | The message under the card says why; a private email is fixed by itself when the GitHub CLI (`gh`) is signed in. |

More: [the chat view](CHAT-VIEW.md) · [FAQ](FAQ.md) · [technical reference](REFERENCE.md) · [Built with Planwharf](BUILT-WITH-PLANWHARF.md).
