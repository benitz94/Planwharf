'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const activation = require('./phone-activation.cjs');
const { copyBackup } = activation;
const { loadPhoneSettings, savePhoneSettings, pollTelegramUpdates, createPhoneNotifier } = require('./phone.cjs');
const handover = require('./phone-handover.cjs');
const { runHandover } = handover;
const exchangeEvidence = activation.exchangeEvidence || handover.exchangeEvidence;

const root = path.resolve(__dirname, '..');
const installedAddress = process.env.PLANWHARF_INSTALLED_URL || 'http://127.0.0.1:4173';
const buildPath = path.join(root, 'validation', 'phone-activation-native', 'Planwharf.exe');
const backupTo = path.join(root, 'validation', 'phone-activation-backup');
const dataFolder = path.join(root, 'validation', 'phone-activation-handover-notebook');
const reportMd = path.join(root, 'validation', 'phone-activation-handover-report.md');
const reportJson = path.join(root, 'validation', 'phone-activation-handover-report.json');
const installedExe = 'C:\\Users\\benit\\Downloads\\Planwharf-native-win-x64-v0.2.85\\Planwharf-native-win-x64-v0.2.85\\Planwharf.exe';

function redact(value) {
  return String(value || '').replace(/\b\d{5,}:[A-Za-z0-9_-]{6,}\b/g, '[redacted]');
}

function projectsFromPlan(plan) {
  const items = plan && plan.schema === 2 ? plan.projects : [plan];
  return (items || []).filter(Boolean).map(item => ({
    id: item.project?.id || item.id,
    name: item.project?.name || item.name,
    status: item.status || 'idle',
    goal: item.goal || item.direction || ''
  }));
}

async function readJson(url, options) {
  const response = await fetch(url, options);
  const body = await response.json().catch(() => null);
  if (!response.ok) throw new Error(redact(body?.error || `HTTP ${response.status}`));
  return body;
}

function wait(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }

function pollFailureText(value) {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' && Number.isFinite(value) && value !== 0) return String(value);
  if (value instanceof Error) return value.message || '';
  if (value && typeof value === 'object') {
    if (typeof value.message === 'string' && value.message) return value.message;
    if (typeof value.description === 'string' && value.description) return value.description;
    if (typeof value.reason === 'string' && value.reason) return value.reason;
  }
  return '';
}

function createLivePoller({ stored, pollTelegramUpdatesImpl = pollTelegramUpdates, waitImpl = wait } = {}) {
  let pendingAck = 0;
  return {
    getPendingAck: () => pendingAck,
    pollOnce: async () => {
      let last = null;
      for (let attempt = 0; attempt < 8; attempt += 1) {
        try {
          last = await pollTelegramUpdatesImpl({ token: stored.telegramToken, offset: stored.telegram.updateOffset || 0, timeout: 0 });
        } catch (error) {
          return { updates: [], error: redact(error?.message || 'Telegram poll failed.') };
        }
        if (last && last.ok) {
          const ids = (last.updates || []).map(item => Number(item.update_id)).filter(Number.isInteger);
          pendingAck = ids.length ? Math.max(...ids) + 1 : 0;
          return last;
        }
        const reason = pollFailureText(last?.reason) || pollFailureText(last?.error);
        if (!/409|conflict/i.test(reason)) return { updates: [], error: redact(reason || 'Telegram poll failed.') };
        await waitImpl(500);
      }
      const exhausted = pollFailureText(last?.reason) || pollFailureText(last?.error) || 'The installed listener is still polling.';
      return { blocked: true, updates: [], error: redact(exhausted) };
    }
  };
}

async function main() {
  const app = await readJson(`${installedAddress}/api/app`);
  const before = await readJson(`${installedAddress}/api/settings/phone`);
  const attempts = app.attemptsDir;
  const backup = copyBackup(path.dirname(app.planFile), backupTo, attempts);
  const stored = loadPhoneSettings(attempts);
  const installed = { address: installedAddress, pid: app.pid, path: installedExe };
  let paused = false;
  let child = null;
  const poller = createLivePoller({ stored });
  let result;
  async function liveReply(message) {
    const sent = [];
    const fetchImpl = async (url, options = {}) => {
      const response = await fetch(url, options);
      if (String(url).includes('/sendMessage') && options.body) {
        const payload = JSON.parse(options.body);
        const data = await response.clone().json().catch(() => null);
        sent.push({ text: payload.text, messageId: data?.result?.message_id ?? null });
      }
      return response;
    };
    const plan = await readJson(`${installedAddress}/api/plan`);
    const notifier = createPhoneNotifier({
      storeDir: attempts,
      fetchImpl,
      getSnapshot: async () => ({ projects: projectsFromPlan(plan.plan) }),
      onStopProject: async () => { throw new Error('stop is not part of handover'); },
      onResumeProject: async () => { throw new Error('resume is not part of handover'); }
    });
    await notifier.processTelegramMessage({ chat: { id: stored.telegram.pairedChatId }, text: message.text });
    const pendingAck = poller.getPendingAck();
    if (pendingAck) await pollTelegramUpdates({ token: stored.telegramToken, offset: pendingAck, timeout: 0 });
    return sent.at(-1) || { text: '', messageId: null };
  }
  try {
    result = await runHandover({
      installed,
      updatedBuild: buildPath,
      backupOk: backup.files.length >= 3 && Boolean(stored.telegramToken),
      token: stored.telegramToken,
      pausePolling: async () => {
        const body = await readJson(`${installedAddress}/api/settings/phone`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ telegram: { enabled: false } })
        });
        paused = body?.telegram?.enabled === false;
        await wait(1000);
        return { ok: paused, paired: Boolean(body?.telegram?.paired) };
      },
      pollOnce: poller.pollOnce,
      askWhenIdle: async () => liveReply({ text: 'What is happening?' }),
      replyTo: async message => liveReply(message),
      startBuild: async () => {
        fs.rmSync(dataFolder, { recursive: true, force: true });
        fs.mkdirSync(dataFolder, { recursive: true });
        fs.copyFileSync(app.planFile, path.join(dataFolder, 'plan.json'));
        savePhoneSettings(path.join(dataFolder, '.attempts-plan'), {
          ntfy: { enabled: false },
          telegram: {
            enabled: true,
            startCode: stored.telegram.startCode,
            pairedChatId: stored.telegram.pairedChatId,
            pairedUsername: stored.telegram.pairedUsername,
            updateOffset: stored.telegram.updateOffset
          },
          ollama: stored.ollama
        }, stored.telegramToken);
        const logFile = path.join(dataFolder, 'native.log');
        child = spawn(buildPath, [], {
          env: { ...process.env, PLANWHARF_DATA: dataFolder },
          windowsHide: true,
          stdio: 'ignore'
        });
        const deadline = Date.now() + 20000;
        let address = '';
        while (Date.now() < deadline && !address) {
          if (fs.existsSync(logFile)) {
            const match = fs.readFileSync(logFile, 'utf8').match(/http:\/\/127\.0\.0\.1:\d+/);
            if (match) address = match[0];
          }
          if (!address) await wait(200);
        }
        if (!address) return null;
        const phone = await readJson(`${address}/api/settings/phone`);
        return { address, model: phone?.ollama?.model || '', paired: Boolean(phone?.telegram?.paired) };
      },
      sendAlert: async started => {
        const alert = await readJson(`${started.address}/api/settings/phone/test`, { method: 'POST' });
        const message = alert?.result?.telegram?.data?.result;
        return { messageId: message?.message_id ?? null, text: message?.text || '' };
      },
      stopBuild: async () => {
        if (!child || child.pid == null) return;
        spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
        await wait(800);
        child = null;
      },
      resumePolling: async () => {
        const body = await readJson(`${installedAddress}/api/settings/phone`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ telegram: { enabled: true } })
        });
        paused = false;
        return { ok: body?.telegram?.enabled === true && body?.telegram?.paired === before.telegram?.paired };
      }
    });
  } finally {
    if (child) spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    if (paused) {
      await readJson(`${installedAddress}/api/settings/phone`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ telegram: { enabled: true } })
      }).catch(() => {});
    }
    try { fs.rmSync(path.join(dataFolder, '.attempts-plan', 'telegram-bot.token'), { force: true }); } catch { /* already gone */ }
  }
  const after = await readJson(`${installedAddress}/api/settings/phone`).catch(() => null);
  const evidence = result?.evidence || exchangeEvidence({});
  const lines = [
    '# Phone handover',
    '',
    `Updated build: ${buildPath}`,
    `Installed listener kept running: yes`,
    `Polling restored: ${result?.restored ? 'yes' : 'no'}`,
    `Paired before: ${Boolean(before.telegram?.paired)}`,
    `Paired after: ${Boolean(after?.telegram?.paired)}`,
    `Telegram enabled after: ${Boolean(after?.telegram?.enabled)}`,
    `Model on updated build: ${result?.started?.model || 'not started'}`,
    `Updated build address: ${result?.started?.address || 'not started'}`,
    `Blocker: ${result?.blocker || result?.plan?.blocker || 'none'}`,
    '',
    'Outbound redesigned message:',
    evidence.outbound?.text || 'not sent',
    evidence.outbound?.messageId != null ? `Telegram message id: ${evidence.outbound.messageId}` : 'Telegram message id: none',
    '',
    'Inbound paired message:',
    evidence.inbound?.text || 'none pending',
    '',
    'Natural-language reply:',
    evidence.reply?.text || 'not sent',
    evidence.reply?.messageId != null ? `Telegram message id: ${evidence.reply.messageId}` : 'Telegram message id: none',
    ''
  ];
  fs.mkdirSync(path.dirname(reportMd), { recursive: true });
  fs.writeFileSync(reportMd, lines.join('\n'));
  fs.writeFileSync(reportJson, JSON.stringify({
    restored: Boolean(result?.restored),
    ok: Boolean(result?.ok),
    model: result?.started?.model || '',
    address: result?.started?.address || '',
    pairedBefore: Boolean(before.telegram?.paired),
    pairedAfter: Boolean(after?.telegram?.paired),
    enabledAfter: Boolean(after?.telegram?.enabled),
    blocker: result?.blocker || result?.plan?.blocker || '',
    evidence
  }, null, 2) + '\n');
  process.stdout.write(redact(`${result?.ok ? 'HANDED' : 'STOPPED'}\n${result?.blocker || result?.plan?.blocker || 'no blocker'}\n`));
  if (!result?.restored) process.exitCode = 1;
}

if (require.main === module) {
  main().catch(error => {
    process.stderr.write(`${redact(error && error.message ? error.message : error)}\n`);
    process.exitCode = 1;
  });
}

module.exports = { main, redact, projectsFromPlan, readJson, createLivePoller };
