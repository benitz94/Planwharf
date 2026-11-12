# Planwharf native app

A native window for Planwharf, built with [Avalonia](https://avaloniaui.net/) on .NET 8, for Windows, macOS and Linux. It is not a web page in a browser shell: the interface is drawn with Avalonia's own controls.

It starts the same local Planwharf server as the other versions (`planwharf.cjs`, unchanged) and talks to it over `http://127.0.0.1:<port>/`. Projects are kept in the same data folder as the desktop app (`%APPDATA%\Planwharf\data`, `~/.config/Planwharf/data`, `~/Library/Application Support/Planwharf/data`): use one Planwharf at a time.

## What it does today
- Lists your projects with their status, and shows the installed version next to the name.
- Shows the chat with the lead and lets you write to it.
- Shows the crew's work as the chat page does: progress and time, each step with its role, model and status, what the model at work is doing, a plain-English summary (the same lines as `work-summary.js`, checked by a test), what was done, and what the work used per model (time, tokens, quota before and after; "not reported" when the provider does not say).
- Warnings from Planwharf's own checks ("Something needs a look"), with Ignore.
- Finished work in a git folder: changed files not yet committed, **Commit**, **Commit and push**, or **Push N commits** when commits are not on GitHub yet; asks first, never force-pushes.
- One clear next action: **Let the crew do it** (with the goal agreed in the chat, after a confirmation), **Stop the crew**, **Approve and start**, **Resume**; and why the crew stopped, in plain English.
- **+ New project**: a name and an existing folder, or no folder and Planwharf creates one in the projects folder.
- **Team**, per project: choose the lead; give each model Build, Verify or Review, or put it on the bench; quota left for each model; models that are not available are shown greyed with the reason; AI tools on or off, look for models again, connect a model by API.
- The model of each step that is not done can be changed from its menu, among the team models with that role; a step at work asks first, then restarts on the new model.
- Before the crew starts, the app says what is missing: the project folder, a lead, a model that builds.
- **Progress** (status, steps, recent activity), **Usage** (quota left for each limit with its reset time, lead calls and crew work, "not reported" when a provider does not say) and **Files** (changes not yet saved in Git), in the panel on the right.
- **Settings**: the project's folder and GitHub link, the project's tools (MCP servers, browser, hooks, memory), the folder for new projects, approvals, the automatic model switch, skills and instructions, save a project to a file or open one, updates, start over, remove project, quit.
- Closing the window, or the system ending the app, saves unfinished work and stops the server.

Not yet in the native app (use the web or desktop version meanwhile): the light theme and text colours, installers. Updates: the app tells you when a new version is out and opens the download page.

## Build and run
```sh
dotnet test native/Planwharf.Native.Tests
dotnet run --project native/Planwharf.Native
```
A development build finds Planwharf's files up the folder tree and uses the Node.js installed on the computer. A published build carries them next to the program, in `app` (see `prepare-app.cjs`) and `node`; the **Native app** workflow builds such a folder for Windows, macOS (Apple silicon and Intel) and Linux on every change.
