# Your first project with Planwharf

This guide takes you from installation to a first result. It takes about fifteen minutes plus the crew's working time. For the screen itself see [the chat view](CHAT-VIEW.md); for details see the [user guide](USER-GUIDE.md).

## 1. Install

1. Install [Node.js](https://nodejs.org/).
2. Download `Planwharf-vX.Y.Z.zip` from the [latest release](https://github.com/benitz94/Planwharf/releases/latest) under **Assets**, and extract the whole archive.
3. Install and sign in to at least one AI coding tool: Codex (ChatGPT), Claude Code, Grok CLI or Antigravity (Gemini). Each tool has its own sign-in; Planwharf uses it and never asks for your password.

## 2. Open Planwharf

On Windows double-click `Planwharf.exe`; on macOS or Linux run `sh ./planwharf.sh`. A window opens on the chat with **Welcome aboard, captain**.

If the launcher cannot find Node.js, it says so; install Node.js and try again. Startup logs are under `%LOCALAPPDATA%\Planwharf\logs`.

## 3. Choose your team

Open **Team** (top right).

1. **Lead:** pick a model in the selector. It will talk with you and plan.
2. **Build:** press **Build** on at least one model. It will write the code.
3. **Verify:** press **Verify** on a model, ideally from a different company than the builder. It will check the work and fix it. Without a verifier the job still runs, but only the tests check it.

Models you do not use stay on the **bench**. If a tool you signed in to is missing, check that it is **On** under **AI tools** and press **Look for models again**.

## 4. Create a project

Press **+ New project** and fill in only:

- **Name**, for example `Counter`;
- **Folder on your computer**: a new, empty folder. Leave it empty to create one in the default projects folder (Settings).

Then tell the lead in the chat what to build, in plain words, with how to check it. For example: *"A local HTML page with a counter, an increment button and a reset button. No dependencies. Test both buttons."*

A chat message never starts work by itself. Under your last message, press **Use as the goal**: the message becomes the project goal.

For your first try, use a new folder rather than an important repository.

## 5. Start

Two ways:

- **Let the crew do it:** the crew plans, builds, verifies and stops when the goal is reached or a limit is hit.

If you prefer to approve each piece of work yourself, describe the work in the chat. When the lead proposes it, press **Approve and start**.

## 6. Watch the work

The **Crew at work** card shows:

- the progress bar;
- each step with its model and state;
- under **What … is doing**, what the running model is doing right now.

You can keep chatting with the lead. To stop, press **Stop work** (or **Stop the crew**): the work done so far is saved, and **Resume** continues later.

If Planwharf sees something odd, such as no progress for ten minutes or a failed step, a **Something needs a look** card appears. It is a reason to look, not proof that something broke.

## 7. Check the result

When the **Work finished** card appears, open **Files** to see what changed, then open the project folder and try the result. Planwharf never commits, pushes or publishes anything by itself.

If the result is not right, say in the chat what is wrong. The lead prepares a correction.

## Practice without touching your projects

Run a separate notebook:

```powershell
Copy-Item fixtures/starter-notebook.json practice.json
node planwharf.cjs --plan practice.json --attempts-dir practice-attempts --port 4180
```

## When something goes wrong

| What you see | What to do |
| --- | --- |
| **Let the crew do it** is greyed out | The line next to it says what is missing; the card above has a button that opens the right panel. |
| A model is missing in Team | Sign in to that tool, check that it is On, press **Look for models again**. |
| A step failed | Read the step and the warning card, then tell the lead what to change, or press **Resume**. |
| Resume asks for confirmation | The folder changed since the work stopped. Check it, then confirm. |
| Quota shows a reason instead of a number | That provider does not report quota; Planwharf does not guess. |

When asking for help, include the startup log or the error shown, and the exact model name. Never share sign-in tokens.
