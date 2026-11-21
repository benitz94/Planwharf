# Your first project with Planwharf

This guide takes you from installation to a first result. It takes about fifteen minutes plus the crew's working time. For the screen itself see [the chat view](CHAT-VIEW.md); for details see the [user guide](USER-GUIDE.md).

## 1. Install

1. Download the native app for your system from the [latest release](https://github.com/benitz94/Planwharf/releases/latest) under **Assets** (`Planwharf-native-<system>-vX.Y.Z.zip`), and extract the whole archive.
2. Install and sign in to at least one AI coding tool: Codex (ChatGPT), Claude Code, Grok CLI or Antigravity (Gemini). Each tool has its own sign-in; Planwharf uses it and never asks for your password.

## 2. Open Planwharf

Open `Planwharf` (`Planwharf.exe` on Windows) in the extracted folder. A window opens on the chat with **Welcome aboard, captain**.

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

Above the chat, the switch starts on **Plan**. In Talk and Plan a chat message never starts work by itself. In Plan, under your last message, press **Use as the goal**: the message becomes the project goal. Talk offers neither that nor **Let the crew do it**.

For your first try, use a new folder rather than an important repository.

## 5. Start

On **Plan**, two ways:

- **Let the crew do it:** the crew plans, builds, verifies and stops when the goal is reached or a limit is hit.
- **Auto** on the switch: the crew starts that goal itself and goes on for this session. After a restart the project is Plan again.

If you prefer to approve each piece of work yourself, stay on Plan (or say `mode manual`) and describe the work in the chat. When the lead proposes it, press **Approve and start**. Moving the switch does not stop work already running; **Stop the crew** does.

## 6. Watch the work

The **Crew at work** card shows:

- the progress bar;
- each step with its model and state;
- under **What … is doing**, what the running model is doing right now.

You can keep chatting with the lead. To stop, press **Stop work** (or **Stop the crew**): the work done so far is saved, and **Resume** continues later.

If Planwharf sees something odd, such as no progress for ten minutes or a failed step, a **Something needs a look** card appears. It is a reason to look, not proof that something broke.

## 7. Check the result

When the **Work finished** card appears, open **Files** to see what changed, then open the project folder and try the result. The crew commits on the work's own branch and never pushes; when you are happy, press **Push**: the branch goes to GitHub and the pull request opens. Nothing reaches `main` until you merge it ([commits and pushes](USER-GUIDE.md#commits-and-pushes)).

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
