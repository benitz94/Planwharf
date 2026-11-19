'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const DEFAULT_OLLAMA_MODEL = 'qwen2.5:3b';
const DEFAULT_OLLAMA_URL = 'http://127.0.0.1:11434';

function samePath(left, right) {
  const normalize = value => {
    const raw = String(value || '').trim();
    if (!raw) return '';
    try {
      return path.resolve(raw).replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
    } catch {
      return raw.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase();
    }
  };
  const a = normalize(left);
  const b = normalize(right);
  return Boolean(a) && a === b;
}

function isolatedPhoneSettings() {
  return {
    ntfy: { enabled: false, topic: '', serverUrl: 'https://ntfy.sh' },
    telegram: {
      enabled: false,
      startCode: 'isolated-validation',
      pairedChatId: null,
      pairedUsername: null,
      updateOffset: 0
    },
    ollama: { enabled: true, baseUrl: DEFAULT_OLLAMA_URL, model: DEFAULT_OLLAMA_MODEL }
  };
}

function backupNames() {
  return ['phone-settings.json', 'telegram-bot.token', 'plan.json'];
}

// Live paired polling is safe only when this build would be the only getUpdates consumer.
// A second poll of the same bot steals updates from the installed listener or gets HTTP 409.
function assessPairedActivation({
  listeners = [],
  thisBuildPath = '',
  tokenPresent = false,
  isolatedHasToken = false,
  model = DEFAULT_OLLAMA_MODEL
} = {}) {
  const list = Array.isArray(listeners) ? listeners : (listeners && typeof listeners === 'object' ? [listeners] : []);
  const installed = list.filter(item => {
    return item && item.running && item.pollsTelegram && !samePath(item.path, thisBuildPath);
  }).map(item => ({
    path: String(item.path || ''),
    pid: Number.isInteger(item.pid) ? item.pid : null
  }));
  const reasons = [];
  if (!tokenPresent) reasons.push('No bot token is available, so a live Telegram exchange cannot start.');
  if (installed.length) {
    const where = installed.map(item => item.path || `pid ${item.pid}`).join('; ');
    reasons.push(`The installed Planwharf listener is already polling Telegram (${where}). A second getUpdates call would compete with it.`);
  }
  if (isolatedHasToken) {
    reasons.push('The isolated validation notebook contains a bot token. Remove it before starting that notebook so it cannot poll.');
  }
  return {
    allowed: reasons.length === 0,
    model: typeof model === 'string' && model.trim() ? model.trim() : DEFAULT_OLLAMA_MODEL,
    installedListeners: installed,
    blocker: reasons.join(' ')
  };
}

function writeIsolatedNotebook(dir, starterFile) {
  const root = path.resolve(dir);
  const attempts = path.join(root, '.attempts-plan');
  fs.mkdirSync(attempts, { recursive: true });
  fs.copyFileSync(starterFile, path.join(root, 'plan.json'));
  fs.writeFileSync(path.join(attempts, 'phone-settings.json'), JSON.stringify(isolatedPhoneSettings(), null, 2) + '\n');
  const token = path.join(attempts, 'telegram-bot.token');
  if (fs.existsSync(token)) fs.unlinkSync(token);
  return { root, attempts, plan: path.join(root, 'plan.json') };
}

function copyBackup(fromDir, toDir, attemptsDir = fromDir) {
  const destination = path.resolve(toDir);
  fs.mkdirSync(destination, { recursive: true });
  const locations = {
    'plan.json': path.join(fromDir, 'plan.json'),
    'phone-settings.json': path.join(attemptsDir, 'phone-settings.json'),
    'telegram-bot.token': path.join(attemptsDir, 'telegram-bot.token')
  };
  const resolveSource = name => {
    const primary = locations[name];
    if (primary && fs.existsSync(primary)) return primary;
    const fallbackAttempt = path.join(fromDir, '.attempts-plan', name);
    if (fs.existsSync(fallbackAttempt)) return fallbackAttempt;
    const fallbackRoot = path.join(fromDir, name);
    if (fs.existsSync(fallbackRoot)) return fallbackRoot;
    return null;
  };
  const files = [];
  for (const name of backupNames()) {
    const source = resolveSource(name);
    if (!source) continue;
    const target = path.join(destination, name);
    fs.copyFileSync(source, target);
    const bytes = fs.readFileSync(source);
    files.push({ name, sha256: crypto.createHash('sha256').update(bytes).digest('hex'), bytes: bytes.length });
  }
  fs.writeFileSync(path.join(destination, 'manifest.json'), JSON.stringify({ files }, null, 2) + '\n');
  return { destination, files };
}

// getUpdates runs only after the gate says this process would be the only listener.
async function liveExchange({ allowed, fetchImpl, token, offset = 0 } = {}) {
  if (!allowed) return { ran: false, reason: 'blocked' };
  if (!token || typeof fetchImpl !== 'function') return { ran: false, reason: 'missing' };
  try {
    const response = await fetchImpl(`https://api.telegram.org/bot${token}/getUpdates?offset=${offset}&timeout=0`);
    const data = typeof response?.json === 'function' ? await response.json().catch(() => null) : null;
    return { ran: true, ok: Boolean(response?.ok && data?.ok), updates: Array.isArray(data?.result) ? data.result.length : 0 };
  } catch (error) {
    return { ran: true, ok: false, updates: 0, reason: error?.message || 'network error' };
  }
}

function reportMarkdown({ assessment, probe, backup, exchange, build }) {
  const lines = [
    '# Phone activation',
    '',
    `Default model: ${assessment?.model || DEFAULT_OLLAMA_MODEL}`,
    `Paired-bot verification allowed: ${assessment?.allowed ? 'yes' : 'no'}`,
    `Blocker: ${assessment?.blocker || 'none'}`,
    `Local model probe: ${probe ? (probe.installed ? 'installed' : 'not installed') : 'not run'}`,
    `Live Telegram exchange: ${exchange?.ran ? 'ran' : 'not run'}`,
    `Backup files: ${(backup?.files || []).map(file => file.name).join(', ') || 'none'}`,
    `Native build: ${build || 'not built'}`,
    '',
    'This check does not call Telegram getUpdates while the installed listener is running, and it does not change the installed app.'
  ];
  return lines.join('\n') + '\n';
}

module.exports = {
  DEFAULT_OLLAMA_MODEL,
  DEFAULT_OLLAMA_URL,
  samePath,
  isolatedPhoneSettings,
  backupNames,
  assessPairedActivation,
  writeIsolatedNotebook,
  copyBackup,
  liveExchange,
  reportMarkdown
};
