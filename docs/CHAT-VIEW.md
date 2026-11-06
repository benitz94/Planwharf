# Chat view

Planwharf opens on the chat view at `http://127.0.0.1:<port>/`. The earlier screens are still available as the **Advanced view** at `/advanced`, linked at the bottom left.

## Who does what

**You are the captain.** You choose the goal, the team and when work starts or stops. Nothing runs that you did not set in motion, and you can stop it at any time.

| On screen | Role in settings | Job |
| --- | --- | --- |
| Lead | Orchestrator | Talks with you, plans the work and picks who does each part. |
| Build | Worker | Writes the code and the tests. |
| Verify | Verifier | Checks the builder's work independently, preferably from another provider, and fixes what fails. |
| Bench | no role | Available, but not used until you give it a role. |

Reviewer (read-only audits) and Supervisor (an AI that watches running workers) are optional. Set them in the Advanced view.

## The screen

- **Left:** your projects and **+ New project**.
- **Centre:** the conversation with the lead. Crew progress is shown in the conversation as short lines: plan updated, step started, step finished, step failed.
- **Bottom:** **Let the crew do it** starts [project autonomy](PROJECT-AUTONOMY.md). The crew plans, builds, verifies and stops when the goal is reached or a limit is hit. **Stop the crew** ends it and keeps the work done so far. When the crew cannot start yet, the line next to the button says why.
- **Top right:** buttons that open one panel beside the chat:
  - **Team:** the lead, the models on the team with their Build and Verify roles, and the bench.
  - **Progress:** the current state, the steps and the recent activity.
  - **Usage:** quota left per provider, plus calls, time and output tokens. A provider that does not report a value shows "not reported", never an estimate.
  - **Files:** the files changed since the last Git commit in the project folder.
  - **Settings:** the project folder, the default folder for new projects, approvals and the theme.

On a narrow window the panel slides over the chat. **Esc** or **✕** closes it.

## Commands in the chat

Short commands are handled by Planwharf itself, without calling a model:

| Type | Result |
| --- | --- |
| `team`, `open team`, `squadra`, `panchina` | Opens Team. |
| `progress`, `stato`, `avanzamento` | Opens Progress. |
| `usage`, `show usage`, `consumi` | Opens Usage. |
| `files`, `changed files`, `file cambiati` | Opens Files. |
| `settings`, `impostazioni` | Opens Settings. |
| `bench <model>`, `metti <model> in panchina` | Asks you to confirm, then removes the model's Lead, Build and Verify roles. |

Anything else goes to the lead.

## First run

The first screen lists what is missing, each with a button that opens the right panel:

1. Choose the lead (Team).
2. Create a project with a name, what to build and a folder.
3. Give at least one model the Build role (Team).

Then press **Let the crew do it**, or first press **Ask me questions first** to let the lead interview you about the goal.
