# Planwharf on your phone

Planwharf can tell you on the phone when the crew starts, switches model, verifies, stops or finishes, and you can answer
from there. Set it up in **Settings → Phone**: ntfy (a private topic name, no account) or a Telegram bot you create with
BotFather and pair with the code shown there.

The Telegram bot speaks like a ship's crew: you are the captain, the project is the ship, the goal is the mission. It
writes in English or Italian, like the app (**Settings → Look → Language**).

Messages carry only the project name, the goal, the state, the times and the names of the models: never code, file names or paths.

## Examples

These are the messages the bot sends, on a sample project. Every message has the same short shape: the news,
the ship and the mission, the state with the progress and the time, then the crew: the lead (👑), each Build
step (🔨) and the Verify step (🔍), with ✅ done, ⚙️ working, ❌ failed. When a model took over since the
last message, the line shows who it replaced: `GPT-6 Astra → Gemini 3.8 Flash`.

**The crew starts**

```text
🏴‍☠️ AHOY, CAPTAIN! 🚀 The crew has set sail on a new mission!
🚢 Recipe app · 🎯 Add a shopping list to the recipes
🧭 Started ▶️ · □□ 0/2 · ⏳ About 25 minutes
👑 Lead Grok 4.7
🔨 Build Opus 5.5 ⚙️
🔍 Verify GPT-6 Astra
🦜 The crew is hard at work. I'll keep you posted, Captain!
```

**Another model takes over a step**

```text
🏴‍☠️ AHOY, CAPTAIN! 🔁 Change of watch!
🚢 Recipe app · 🎯 Add a shopping list to the recipes
🧭 Switched model 🔁 · ■□ 1/2 · ⏳ About 8 minutes
👑 Lead Grok 4.7
🔨 Build Opus 5.5 ✅
🔍 Verify GPT-6 Astra → Gemini 3.8 Flash ⚙️
🦜 Another sailor took over this step.
```

**The work is finished** (in Italian)

```text
🏴‍☠️ AHOY, CAPITANO! 🏆 Missione compiuta!
🚢 Recipe app · 🎯 Add a shopping list to the recipes
🧭 Fatto ✅ · ■■ 2/2 · ⏱️ 31m
👑 Lead Grok 4.7
🔨 Build Opus 5.5 ✅
🔍 Verify Gemini 3.8 Flash ✅
🍻 Il bottino è nella stiva. Controllalo e fai il push quando ti piace.
```

Other messages follow the same pattern: the crew dropped anchor (stopped), back at sea (resumed), rough seas (failed),
the crew awaits your orders (needs you), stuck on the rocks (blocked).

## Commands

At the bottom of the chat the bot keeps three buttons: **📊 Status**, **⏹ Stop** and **▶️ Resume**. The status message adds a stop or resume button for each project that can use it. You can also type:

| Command | What it does |
|---|---|
| `/status` | The state of each project: who is working, the step, the time left, and the mode (Talk or Plan; in Italian Chat or Piano) |
| `/stop <project>` | Stops the crew, after you answer "yes" |
| `/resume <project>` | Resumes the work from its checkpoint |
