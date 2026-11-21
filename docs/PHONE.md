# Planwharf on your phone

Planwharf can tell you on the phone when the crew starts, switches model, verifies, stops or finishes, and you can answer
from there. Set it up in **Settings → Phone**: ntfy (a private topic name, no account) or a Telegram bot you create with
BotFather and pair with the code shown there.

The Telegram bot speaks like a ship's crew: you are the captain, the project is the ship, the goal is the mission. Each
message also has one plain sentence, in italics in Telegram, that says what happened without the sea talk. It writes in English
or Italian, like the app (**Settings → Look → Language**).

Messages carry only the project name, the goal, the state and times: never code, file names or paths.

## Examples

These are the messages the bot sends, on a sample project.

**The crew starts**

```text
🏴‍☠️ AHOY, CAPTAIN!

🚀 The crew has set sail on a new mission!
The crew has started.

🚢 Ship
Recipe app

🎯 Mission
Add a shopping list to the recipes

🧭 Status
Started ▶️

⏳ Time left
About 25 minutes · based on 40 past steps

🦜 The crew is hard at work. I'll keep you posted, Captain!
```

**Another model takes over a step**

```text
🏴‍☠️ AHOY, CAPTAIN!

🔁 Change of watch!
The crew changed who is doing this step.

🚢 Ship
Recipe app

🎯 Mission
Add a shopping list to the recipes

🧭 Status
Switched model 🔁

■□ 1/2  The crew is on step 1 of 2.

🦜 Another sailor took over this step.
```

**A second model checks the work**

```text
🏴‍☠️ AHOY, CAPTAIN!

🔭 A second pair of eyes is checking the work.
A different model is checking the work.

🚢 Ship
Recipe app

🎯 Mission
Add a shopping list to the recipes

🧭 Status
Verify started 🔎

⏳ Time left
About 8 minutes

■□ 1/2  The crew is on step 1 of 2.

🦜 Another model is inspecting every plank.
```

**The work is finished** (in Italian)

```text
🏴‍☠️ AHOY, CAPITANO!

🏆 Missione compiuta!
Il lavoro è finito.

🚢 Nave
Recipe app

🎯 Missione
Add a shopping list to the recipes

🧭 Stato
Fatto ✅

⏱️ Durata del viaggio
31m

🍻 Il bottino è nella stiva. Controllalo e fai il push quando ti piace.
```

Other messages follow the same pattern: the crew dropped anchor (stopped), back at sea (resumed), rough seas (failed),
the crew awaits your orders (needs you), stuck on the rocks (blocked).

## Commands

At the bottom of the chat the bot keeps three buttons: **📊 Status**, **⏹ Stop** and **▶️ Resume**. The status message adds a stop or resume button for each project that can use it. You can also type:

| Command | What it does |
|---|---|
| `/status` | The state of each project: who is working, the step, the time left, and the mode (Talk, Plan or Auto; in Italian Chat, Piano or Auto) |
| `/stop <project>` | Stops the crew, after you answer "yes" |
| `/resume <project>` | Resumes the work from its checkpoint |
