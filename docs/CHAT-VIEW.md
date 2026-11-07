# The chat view

Planwharf has one screen: a chat with your lead, with your projects on the left and one detail panel at a time on the right.

## Who does what

**You are the captain.** You choose the goal, the team, and when work starts or stops. Nothing runs unless you set it in motion, and you can stop it at any time.

| On screen | Job |
| --- | --- |
| Lead | Talks with you, plans the work and picks who does each step. |
| Build | Writes the code and the tests. |
| Verify | Checks the builder's work independently, preferably from another provider, and fixes what fails. |
| Review (optional) | Reads the work and reports problems, without changing files. |
| Bench | Available, but not used until you give it a role. |

## The screen

- **Left:** your projects, **+ New project**, the day and time, and an update link when a new version is out (not shown in a Git checkout).
- **Centre:** the conversation with the lead. The work appears here as it happens:
  - one short line for each milestone;
  - a **proposal card** with the steps and who does each, and **Approve and start**;
  - a **Crew at work** card with a progress bar, each step's model and state, and what the running model is doing, in plain words;
  - a **Work finished** card with steps done, time taken, files not yet committed, and **What this work used**: per model the time, tokens and quota before → after ("not reported" when the provider does not say);
  - under your last message, while nothing runs, **Use as the goal**. A chat message never starts work by itself; this button makes it the project goal.
  - a **Something needs a look** card when Planwharf's own checks see stalled, failed or conflicting work. These checks use no model. **Ignore** hides one warning.
- **Bottom:** one main button, depending on the state:
  - **Let the crew do it** starts [project autonomy](PROJECT-AUTONOMY.md);
  - **Approve and start** launches proposed work;
  - **Stop work** or **Stop the crew** ends the work and keeps it saved;
  - **Resume** continues from the saved point.

  The line next to the button says why when something is missing.
- **Top right:** buttons that open one panel beside the chat:
  - **Team:** the team of the selected project. Each project has its own lead and roles; a new project starts from the shared team. **On the team** and **Bench** are shown apart. The panel also has the AI tools with an On/Off switch, **Look for models again**, and **Connect a model by API**.
  - **Progress:** the current state, the steps and recent activity.
  - **Usage:** quota left per provider, plus calls, time and output tokens. A value the provider does not report shows "not reported", never an estimate.
  - **Files:** files changed since the last Git commit in the project folder.
  - **Settings:**
    - the project folder, an optional GitHub repository link, and the default folder for new projects;
    - approvals;
    - the look: dark (the default) or light, and with the dark theme a text colour: thirteen presets or any colour you pick, remembered by this browser;
    - **Move a project** (save it to a file, or open a saved one);
    - updates, **Remove project** and **Quit**.

On a narrow window the panel slides over the chat. **Esc** or **✕** closes it. Buttons that remove or quit ask for a second press.

## Commands in the chat

Short commands are handled by Planwharf itself, without calling a model:

| Type | Result |
| --- | --- |
| `team`, `open team`, `squadra`, `panchina` | Opens Team. |
| `progress`, `stato`, `avanzamento` | Opens Progress. |
| `usage`, `show usage`, `consumi` | Opens Usage. |
| `files`, `changed files`, `file cambiati` | Opens Files. |
| `settings`, `impostazioni` | Opens Settings. |
| `bench <model>`, `metti <model> in panchina` | Asks you to confirm, then removes all of the model's roles. |
| `stop`, `fermati`, `fermati adesso` | Stops the work now; it stays saved. |
| `stop in 10 minutes`, `fermati tra 10 minuti` | Stops the work at that time. A countdown with **Cancel** shows by the main button. It runs while the Planwharf window is open. |

Anything else goes to the lead.

## First run

The first screen lists what is missing, each with a button that opens the right panel:

1. choose the lead (Team);
2. create a project with a name and a folder;
3. tell the lead what to build, then press **Use as the goal**;
4. give at least one model the Build role (Team).

Then press **Let the crew do it**.

The [user guide](USER-GUIDE.md) explains each part in detail.
