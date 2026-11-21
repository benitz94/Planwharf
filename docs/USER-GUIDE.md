# Planwharf user guide

Planwharf runs a team of AI models on your computer to build what you describe. **You are the captain**: you set the goal, choose the team, and start or stop the work. A **lead** model talks with you and plans; **builders** write the code and tests; **verifiers** check that work and fix it.

This guide describes the **native app**, the version developed from now on. The desktop app (Electron) and the web app are deprecated: they still work, see [Desktop app and web app](DESKTOP.md). For a two-minute tour of the screen, read [the chat view](CHAT-VIEW.md); to build something right away, follow the [first project guide](HOW-TO.md).

## Install and launch

1. Install and sign in to at least one AI coding tool: Codex (ChatGPT), Claude Code, Grok CLI or Antigravity (Gemini). Planwharf uses those sign-ins and never asks for a password.
2. Download the native app for your system from the [latest release](https://github.com/benitz94/Planwharf/releases/latest): `Planwharf-native-win-x64-vX.Y.Z.zip` (or `…-win-arm64-…`), `…-linux-x64-…` (or `…-linux-arm64-…`), `…-osx-arm64-…` or `…-osx-x64-…`.
3. Extract the zip anywhere and open `Planwharf` (`Planwharf.exe` on Windows). Nothing else to install: the zip carries what it needs.

Your projects and settings are kept in your user folder (`%APPDATA%\Planwharf\data`, `~/.config/Planwharf/data`, `~/Library/Application Support/Planwharf/data`), never in the app folder, so updating or moving the app does not touch them. Use one Planwharf at a time on the same data.

**Updates:** **Settings → Updates → Update now** downloads the new version, checks it against the digest GitHub publishes, saves running work, and restarts. After the restart, press **Resume** if work was running.

**Language:** **Settings → Look → Language**: English (default) or Italiano. Texts written by the engine (statuses, reasons, the crew's answers) stay in English for now; the lead answers in the language you write in.

## Projects and folders

**+ New project** asks for a name and a folder; leave the folder empty to create one inside the projects folder (**Settings**). The project list on the left shows the project you used last first, with its status and who is working. **Settings → Remove project** removes it from Planwharf; its folder stays on disk.

Workers run only inside the project folder. This is a check on where work starts, not an operating-system sandbox for the AI tools.

## The team

Open **Team**. Each project has its own team.

| On screen | Job |
| --- | --- |
| Lead (menu at the top) | Talks with you, plans, picks who does each step. The menu lists the leads from the most capable down; if the lead runs out of quota, or does not answer, the next one takes over. The quota limits hold for the lead too: a model under its limit takes over only when no other can lead. |
| Build | Writes the code and its tests. |
| Verify | Checks the builder's work independently and fixes what fails. A strong model is preferred: a light one verifies only when nobody else can. |
| Bench | Available, but never used until you give it a role. |

Each model card shows its quota left (5-hour and weekly windows), its **weight** (light, default, premium: light parts of the work go to light models, heavy parts to strong ones), and:

- **Limit**: the quota left at which Planwharf stops giving the model new work and moves its running step to another model. **Save for all <company>** sets it for every model of that company (all Claude models share one quota); **Only this model** sets an exception. The general limit, 35% of both windows, is in **Settings**.
- **Tools**: MCP servers, browser, hooks and memory for that model, or the same as the project.

**Find models** filters by company, weight and state, with **All / None** to give or take a role for every model shown. **AI tools** turns each program on or off, and **Look for models again** rereads the models of the accounts signed in on this computer. Models of an expired plan or a signed-out account are hidden, with one line saying why. A model that answers it needs usage credits (outside your plan) is hidden for a day, and the lead and the crew move on to the next model. Where a program allows it (Claude Code, Codex), **Add another account** uses a second subscription of the same company before moving to another company.

**Connect a model by API** adds an OpenAI-compatible or Anthropic Messages API (DeepSeek, Kimi…): the key stays in an environment variable, Planwharf stores only its name. See [external providers](EXTERNAL-PROVIDERS.md).

## Working with the lead

Write in the chat what you want; ordinary conversation never starts work by itself. Under each of your messages, **Copy** copies it and **Use again** puts it back in the box. Short orders such as `usage`, `files`, `settings`, `bench <model>`, `stop` are handled by Planwharf without calling a model ([the chat view](CHAT-VIEW.md#commands-in-the-chat)).

When the lead suggests a goal, **Let the crew do it** starts it. The crew then works on its own ([project autonomy](PROJECT-AUTONOMY.md)): the lead splits the work, approves its own steps within limits, and stops when the goal is reached or a limit is hit. **Stop the crew** stops it at any time.

## What happens after "Let the crew do it"

1. **Safe start.** Planwharf checks that the project folder has no uncommitted changes from something else, fetches GitHub, and creates the work's branch from the latest `main` (or goes on with the work's own branch). It writes the work plan, `.planwharf/plan.md`, kept out of git unless you choose **Settings → Keep the work plan in git**.
2. **Split.** The lead divides the work into parts that can run at the same time, each with its own files and weight. Parallel parts each work in their own copy of the folder (a git worktree), and are joined at the end.
3. **Build, then Verify.** Each part is built; then another model checks the whole work and fixes what fails. A step is done only when the worker leaves a valid result and exits cleanly.
4. **Quota watch.** A model that falls under its limit hands its step to another model with a handoff (files changed, tests, summary). A step that ends with no result is tried once more, then moves to the next model. A lead that runs out of quota hands over to the next lead at once.
5. **Decisions.** What only you can decide is shown highlighted as **Needs your decision**, with a suggestion and buttons to answer A decision is asked only under the work that raised it: a dropped work, or a new proposal, does not carry it.
6. **Finished.** The **Work finished** card shows the summary, the time, what each model used, and the git state.

The **Crew at work** card shows the steps, their models and state, what the running model is doing, and **about how long is left** (from how long past steps took). **Progress**, **Usage** and **Files** are in the panel on the right.

## Commits and pushes

The crew commits on the work's branch and never pushes: every worker runs with a git setting that makes any push fail. You publish: **Push** sends the branch to GitHub under its own name, opens the pull request (named after the work), and never pushes `main`. If GitHub refuses a private email address in the commits, they get your GitHub noreply address and the push is tried again. You merge the pull request on GitHub. **Check PRs** in the app lists the open pull requests of the project; **Fix them** fixes conflicts and failing checks in the background, on a copy of the folder, and pushes to the same pull request. No work opens in the chat: a line says when it starts and when it is done.

## Alerts

- **On the computer:** when a work ends, fails or needs you: a notification, the taskbar flashing and a short sound, each switchable in **Settings → Look**.
- **On the phone:** **Settings → Phone**: ntfy (a private topic name, no account), or a Telegram bot you create with BotFather and pair with the code shown there. The bot reports stops, resumes, verification and model switches, and answers `/status`, `/stop <project>` (after "yes") and `/resume <project>`. Each message names the lead, the Build and Verify models, with an arrow when one took over. Messages carry only the project name, the goal, the state, the times and the model names: never code, file names or paths. Examples of the messages: [Planwharf on your phone](PHONE.md).

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
| The push is refused | The message under the card says why; a private email is fixed by itself when the GitHub CLI (`gh`) is signed in. |

More: [the chat view](CHAT-VIEW.md) · [FAQ](FAQ.md) · [technical reference](REFERENCE.md) · [Built with Planwharf](BUILT-WITH-PLANWHARF.md).
