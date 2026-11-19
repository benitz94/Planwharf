'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');

function hashFile(file) {
  return fs.existsSync(file) ? crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex') : null;
}

const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

async function main() {
  const root = path.resolve(__dirname, '..');
  const exePath = path.join(root, 'validation', 'phone-activation-native', 'Planwharf.exe');
  const notebookDir = path.join(root, 'validation', 'phone-activation-notebook');
  const logFile = path.join(notebookDir, 'native.log');
  const installedDataDir = 'C:\\Users\\benit\\AppData\\Roaming\\Planwharf\\data';

  if (!fs.existsSync(exePath)) {
    throw new Error(`Native binary not found at ${exePath}`);
  }

  // Record installed hashes before
  const installedBefore = {
    plan: hashFile(path.join(installedDataDir, 'plan.json')),
    settings: hashFile(path.join(installedDataDir, '.attempts-plan', 'phone-settings.json')),
    token: hashFile(path.join(installedDataDir, '.attempts-plan', 'telegram-bot.token'))
  };

  // Truncate or remove previous native.log so we wait for fresh address
  if (fs.existsSync(logFile)) fs.unlinkSync(logFile);

  const env = {
    ...process.env,
    PLANWHARF_DATA: notebookDir
  };

  const child = spawn(exePath, [], {
    cwd: path.dirname(exePath),
    env,
    stdio: 'ignore',
    windowsHide: true
  });

  let address = null;
  const deadline = Date.now() + 15000;
  try {
    while (Date.now() < deadline) {
      if (fs.existsSync(logFile)) {
        const text = fs.readFileSync(logFile, 'utf8');
        const match = text.match(/http:\/\/127\.0\.0\.1:\d+/);
        if (match) {
          address = match[0];
          break;
        }
      }
      await pause(200);
    }

    if (!address) {
      throw new Error('Server address not found in native.log within 15 seconds');
    }

    console.log(`Native build started, listening at: ${address}`);

    // Test GET /api/app
    const appRes = await fetch(`${address}/api/app`);
    const appData = await appRes.json();
    console.log(`App version: ${appData.version}, attemptsDir: ${appData.attemptsDir}`);

    // Test GET /api/settings/phone
    const phoneRes = await fetch(`${address}/api/settings/phone`);
    const phoneData = await phoneRes.json();
    console.log(`Phone settings: model=${phoneData.ollama?.model}, ollamaEnabled=${phoneData.ollama?.enabled}, telegramPaired=${phoneData.telegram?.paired}, tokenConfigured=${phoneData.telegram?.tokenConfigured}`);

    // Test POST /api/settings/phone/ollama
    const probeRes = await fetch(`${address}/api/settings/phone/ollama`, { method: 'POST' });
    const probeData = await probeRes.json();
    console.log(`Ollama probe: ok=${probeData.ok}, installed=${probeData.installed}, model=${probeData.model}`);

  } finally {
    try {
      child.kill('SIGTERM');
      await pause(500);
      try { process.kill(child.pid, 0); child.kill('SIGKILL'); } catch {}
    } catch {}
  }

  // Verify installed files after
  const installedAfter = {
    plan: hashFile(path.join(installedDataDir, 'plan.json')),
    settings: hashFile(path.join(installedDataDir, '.attempts-plan', 'phone-settings.json')),
    token: hashFile(path.join(installedDataDir, '.attempts-plan', 'telegram-bot.token'))
  };

  const unchanged = (
    installedBefore.plan === installedAfter.plan &&
    installedBefore.settings === installedAfter.settings &&
    installedBefore.token === installedAfter.token
  );

  console.log(`Installed files unchanged: ${unchanged}`);
  if (!unchanged) {
    throw new Error('Installed files were modified during isolated launch!');
  }
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
