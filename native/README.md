# Planwharf native app

A native window for Planwharf, built with [Avalonia](https://avaloniaui.net/) on .NET 8, for Windows, macOS and Linux. It is not a web page in a browser shell: the interface is drawn with Avalonia's own controls.

It starts the same local Planwharf server as the other versions (`planwharf.cjs`, unchanged) and talks to it over `http://127.0.0.1:<port>/`. Projects are kept in the same data folder as the desktop app (`%APPDATA%\Planwharf\data`, `~/.config/Planwharf/data`, `~/Library/Application Support/Planwharf/data`): use one Planwharf at a time.

## What it does today
- Lists your projects with their status, and shows the installed version next to the name.
- Shows the chat with the lead and lets you write to it.
- Shows the crew's work: title, progress, each step with its role, model and status, and what was done.
- One clear next action: **Let the crew do it** (with the goal agreed in the chat, after a confirmation), **Stop the crew**, **Approve and start**, **Resume**; and why the crew stopped, in plain English.
- Closing the window, or the system ending the app, saves unfinished work and stops the server.

Not yet in the native app (use the web or desktop version meanwhile): Team, Usage, Settings, the model menu on each step, Commit and push, new projects, installers and in-app updates.

## Build and run
```sh
dotnet test native/Planwharf.Native.Tests
dotnet run --project native/Planwharf.Native
```
A development build finds Planwharf's files up the folder tree and uses the Node.js installed on the computer. A published build carries them next to the program, in `app` (see `prepare-app.cjs`) and `node`; the **Native app** workflow builds such a folder for Windows, macOS (Apple silicon and Intel) and Linux on every change.
