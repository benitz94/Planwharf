# Plan: Planwharf on a C# engine, native app only

Updated on 13 November 2026 with the owner's three decisions. The porting rules are in
[README.md](README.md); each piece of work has a job sheet in [jobs/](jobs/).

## The decisions

1. **At the end there is one Planwharf: the native app in C#**, for Windows, Linux and macOS. The
   web app, the desktop app (Electron) and the whole JavaScript engine are removed.
   - The native app runs the engine **inside itself**: no HTTP server in C#.
   - Node and C# are compared **module by module**: the same inputs to the JavaScript function and to
     the C# one must give the same results.
2. **The web app and Electron are being retired from now on.** They get only fixes for serious errors,
   no new features. They stay in every release, as a fallback, until they are removed.
3. **ARM PCs:** the native app is also built for Windows ARM (`win-arm64`) and Linux ARM
   (`linux-arm64`). A small, separate change that does not hold up the engine work.

Already decided earlier: one C# engine, compiled ahead of time (Native AOT), one download per system,
and Planwharf itself does the porting jobs, with every pull request reviewed before it is merged.

## Phases

| Phase | What | Status |
|---|---|---|
| **0. Foundations** | .NET 10, the `Planwharf.Engine` and `Planwharf.Engine.Tests` projects, the porting rules, the README saying the native app is recommended, that it still includes the Node.js engine, and that the web app and Electron are being retired. | Done (#61; README updated with this plan) |
| **1. Test bench** | 1a: fake AI programs (claude, codex, grok, agy) that replay recorded answers, for the later phases. 1b: **module-by-module comparison** (below), run in CI on the three systems. | 1b done (cases for the 5 pure modules ported so far); 1a to do |
| **2. Modules with no dependencies** | `plan.js`, `notebook-file`, `atomic-rename`, `project-history`, `model-policy`, `model-roles`, `provider-refusal`, `provider-diagnostics`, `provider-accounts`, `structured-final`, `activity-events`, `activity-evidence`, `invocations`, `briefing`, `autonomy`, `sentinel`, `quota`, `models`, `project-checks`, `project-workspace`, `skills`, `browser-settings`. | Started: jobs 01 and 02 (#64, #74) |
| **3. First level** | `attempts`, `claims`, `project-export`, `execution-next`, `worker-result` (done in job 01), `provider-catalog`, `provider-policy`, `model-routing`, `claude`, `claude-usage`, `repository`, `supervisor`, `activity`, `activity-sessions`, `provider-runtime`. | To do |
| **4. Processes and providers** (the riskiest) | `runner` (start and stop the AI programs, read what they write, close process trees on Windows); `provider-models`, `external-adapters`, `api-agent`, `provider-adapters`, `provider-connections`, `antigravity`, `planUsage`. Ends with a hand test on Windows with the real programs. | To do |
| **5. Execution** | `execution.cjs`, `project-home`; at the same time, the design of how to split the orchestrator. | To do |
| **6. Orchestrator** | The 3,750-line file, split by that design, then put together. | To do |
| **7. The native app runs the C# engine** | The window calls the engine directly instead of `server.cjs`. First as an **option** for at least one release, then the **default**; Node leaves the zip. | To do |
| **8. Removal, in one go** | Before it: the list of what the native app still lacks compared with the web app and Electron, with a proposal of what to bring over; **the owner decides**. Then the web app, Electron, the JavaScript engine and `server.cjs` are deleted together, with their workflows and release files. | To do |

Removed from the earlier plan: the C# HTTP server (old phase 7) and moving the web app and Electron
onto the C# engine (old phase 9).

## Module-by-module comparison (phase 1b)

The HTTP server is no longer the place where Node and C# meet, so each module is compared on its own:

- `native/porting/parity/cases.cjs` lists the inputs given to each ported function (taken from its
  tests, plus edge cases). `node native/porting/parity/record.cjs` runs them through the JavaScript and
  writes each input and its result to `native/porting/parity/<module>.json`.
- `ParityTests` (C#) reads the same files, calls the C# function with the same inputs, and must give the
  same result: same JSON, same text, same error messages.
- `engine/parity.test.cjs` records the results again from the JavaScript and fails if they changed
  without the files being written again, so a later change to a JavaScript module cannot leave its port
  behind. (`model-policy.cjs` changed after its port, in #75; its C# was brought up to date with this.)
- Modules that read or write files (skills, worker results, accounts, browser settings, atomic rename)
  keep their own C# tests on real files; the comparison covers the functions that work on data alone.

## How every phase ends

The review is done, CI is green on Windows, macOS and Linux, and the comparison shows no difference
for the modules of that phase. Only then does the next phase start. Until phase 7 Planwharf keeps
running on the Node engine, so it can be used normally all along.

## What the native app needs before the others go (input for phase 8)

Known today, to be checked again then:

| Feature | Web app / Electron | Native app |
|---|---|---|
| Light theme, text colour | Yes | Yes (#78) |
| Working indicator (turning compass) | Yes | Yes (#78) |
| Updates itself | Web app: yes; Electron: new installer | Being added |
| Installer for Windows | Electron: yes | No: a zip to extract |
| Installer or package for Linux | Electron: AppImage, .deb | No: a zip to extract |
| macOS | Web app only | Yes (zip) |
| Code signing | No | No |

## ARM builds

`win-arm64` and `linux-arm64` are added to the native app's CI and release builds and to the README
download table, in their own pull request. GitHub offers ARM machines for public repositories, so the
automatic tests also run on ARM; nobody has tried them by hand on an ARM PC with the real AI programs.
