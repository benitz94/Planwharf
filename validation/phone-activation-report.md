# Phone activation

Default model: qwen2.5:3b
Paired-bot verification allowed: no
Blocker: The installed Planwharf listener is already polling Telegram (C:\Users\benit\Downloads\Planwharf-native-win-x64-v0.2.85\Planwharf-native-win-x64-v0.2.85\Planwharf.exe). A second getUpdates call would compete with it.
Local model probe: installed
Live Telegram exchange: not run
Backup files: phone-settings.json, telegram-bot.token, plan.json
Native build: validation/phone-activation-native/Planwharf.exe

This check does not call Telegram getUpdates while the installed listener is running, and it does not change the installed app.
Local model reply ok: true
Local model: qwen2.5:3b
Local reply:
⚓ Captain
Ready.
