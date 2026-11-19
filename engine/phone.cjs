'use strict';
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

function restrictToOwner(file) {
  try { fs.chmodSync(file, 0o600); } catch { /* Some systems ignore Unix modes. */ }
  if (process.platform !== 'win32') return;
  let user = '';
  try { user = os.userInfo().username || ''; } catch { /* userInfo can fail on a locked account. */ }
  if (!user) user = process.env.USERNAME || '';
  if (!user) return;
  try {
    spawnSync('icacls', [file, '/inheritance:r', '/grant:r', `${user}:(R,W)`], { windowsHide: true, stdio: 'ignore' });
  } catch { /* Leave the file in the user profile if the ACL tool is missing. */ }
}

function writePrivate(file, contents) {
  fs.writeFileSync(file, contents, { mode: 0o600 });
  restrictToOwner(file);
}

// Job 12: Alerts and commands on the phone (ntfy and Telegram bot).
// Strict privacy rule: Never send prompts, code, file names or paths:
// only project name, goal title, status and times.

const FORBIDDEN_SECRET_REGEX = /(?:api[_-]?key|token|secret|password|bearer|ghp_[a-zA-Z0-9]+)\s*[:=]\s*[^\s,;]+/gi;

function sanitizeText(text) {
  if (!text || typeof text !== 'string') return '';
  // Strip file paths (Windows D:\... or Unix /... or relative ./...)
  let clean = text
    .replace(/[\u0000-\u001f\u007f]+/g, ' ')
    .replace(/[a-zA-Z]:\\[^\s"'<>]+/g, '[path]')
    .replace(/(?:^|\s)(?:\/|\.\/)[^\s"'<>]+/g, ' [path]')
    // Strip file extensions like .js, .cjs, .cs, .ts, .json, .md
    .replace(/\b[\w-]+\.(?:cjs|mjs|js|ts|cs|json|md|py|sh|html|css|txt)\b/gi, '[file]')
    // Strip potential tokens/passwords
    .replace(FORBIDDEN_SECRET_REGEX, '[redacted]')
    .trim();
  return clean;
}

function sanitizeGoalTitle(title) {
  if (!title || typeof title !== 'string') return 'Work';
  const firstLine = title.split(/\r?\n/)[0].trim();
  const sanitized = sanitizeText(firstLine);
  return sanitized.length > 80 ? sanitized.slice(0, 77) + '...' : (sanitized || 'Work');
}

function formatDuration(minutes) {
  if (minutes == null || isNaN(minutes) || minutes < 0) return null;
  const mins = Math.round(minutes);
  if (mins < 60) return `${mins}m`;
  const h = Math.floor(mins / 60);
  const m = mins % 60;
  return m > 0 ? `${h}h ${m}m` : `${h}h`;
}

const ALERT_LABELS = {
  done: 'Done ✅',
  failed: 'Failed ❌',
  needs_owner: 'Needs your attention 🔔',
  stopped: 'Stopped ⏹',
  started: 'Started ▶️',
  resumed: 'Resumed ▶️',
  blocked: 'Blocked ⚠️',
  verify: 'Verify started 🔎',
  switched: 'Switched model 🔁',
  restarted: 'Step restarted 🔁'
};

const STATE_EMOJI = {
  Working: '🟢', Done: '✅', Failed: '❌', Stopped: '⏹', 'Waiting on you': '🔔', 'Blocked (no usage)': '⚠️', Idle: '⚪'
};

function statusEmoji(state) {
  return STATE_EMOJI[state] || '•';
}

function mainKeyboard() {
  return { keyboard: [[{ text: '📊 Status' }, { text: '⏹ Stop' }, { text: '▶️ Resume' }]], resize_keyboard: true, is_persistent: true };
}

function statusKeyboard(projects = []) {
  const rows = [];
  for (const p of projects) {
    if (!p || p.id == null || p.id === '') continue;
    const name = sanitizeText(p.name || p.id).slice(0, 24);
    const id = String(p.id);
    if (id.length > 48) continue;
    if (p.status === 'watching') rows.push([{ text: `⏹ Stop ${name}`, callback_data: `stop:${id}` }]);
    else if (p.status === 'checkpointed' || p.status === 'failed') rows.push([{ text: `▶️ Resume ${name}`, callback_data: `resume:${id}` }]);
  }
  rows.push([{ text: '🔄 Refresh', callback_data: 'status' }]);
  return { inline_keyboard: rows };
}

function alertForStatus(prev, curr) {
  if (!prev || prev === curr) return null;
  if (curr === 'complete') return 'done';
  if (curr === 'failed') return 'failed';
  if (curr === 'checkpointed') return 'stopped';
  if (curr === 'watching') return prev === 'checkpointed' || prev === 'failed' ? 'resumed' : 'started';
  if (curr === 'waiting_on_you') return 'needs_owner';
  if (curr === 'blocked_no_worker' || curr === 'blocked_no_folder') return 'blocked';
  return null;
}

function runningStep(project) {
  const parts = Array.isArray(project?.dispatch) ? project.dispatch : [];
  const part = parts.find(item => item && (item.recordedSubTaskStatus === 'RUNNING' || item.alive));
  if (!part) return null;
  const title = String(part.slice || part.title || '');
  return {
    role: /verify/i.test(title) ? 'Verify' : 'Build',
    model: String(part.label || part.modelId || ''),
    attempt: String(part.attemptId || '')
  };
}

function alertForStep(prev, curr) {
  if (!prev || !curr) return null;
  if (prev.role !== 'Verify' && curr.role === 'Verify') return 'verify';
  if (prev.model && curr.model && prev.model !== curr.model) return 'switched';
  if (prev.attempt && curr.attempt && prev.attempt !== curr.attempt) return 'restarted';
  return null;
}

function formatAlertMessage({ type, projectName, goalTitle, status, durationMinutes, timeLeft, detail }) {
  const safeProject = sanitizeText(projectName) || 'Project';
  const safeGoal = sanitizeGoalTitle(goalTitle);
  let statusLabel = ALERT_LABELS[type] || 'Updated';

  let msg = `Planwharf: ${safeProject}\nGoal: ${safeGoal}\nStatus: ${statusLabel}`;

  if (type === 'done' && durationMinutes != null) {
    const dur = formatDuration(durationMinutes);
    if (dur) msg += `\nDuration: ${dur}`;
  }

  if (timeLeft && (timeLeft.minutes != null || timeLeft.endsAt)) {
    let timeText = '';
    if (timeLeft.minutes != null) {
      timeText = `About ${timeLeft.minutes}m left`;
      if (timeLeft.longerThanUsual) timeText += ' (longer than usual)';
      if (timeLeft.sure) timeText += ` · ${timeLeft.sure}`;
    }
    if (timeText) msg += `\nEstimate: ${timeText}`;
  }
  if (detail) msg += `\n${sanitizeText(detail)}`;

  return msg;
}

function generateStartCode() {
  const digits = crypto.randomInt(100000, 999999);
  return `pw-${digits}`;
}

function maskToken(token) {
  if (!token || typeof token !== 'string') return null;
  const trimmed = token.trim();
  if (trimmed.length <= 8) return '***';
  return `${trimmed.slice(0, 4)}...${trimmed.slice(-4)}`;
}

function loadPhoneSettings(storeDir) {
  const settingsPath = path.join(storeDir, 'phone-settings.json');
  const tokenPath = path.join(storeDir, 'telegram-bot.token');

  let corrupt = false;
  let config = {
    ntfy: {
      enabled: false,
      topic: '',
      serverUrl: 'https://ntfy.sh'
    },
    telegram: {
      enabled: false,
      startCode: generateStartCode(),
      pairedChatId: null,
      pairedUsername: null,
      updateOffset: 0
    }
  };

  try {
    if (fs.existsSync(settingsPath)) {
      const raw = JSON.parse(fs.readFileSync(settingsPath, 'utf8'));
      if (raw && typeof raw === 'object') {
        if (raw.ntfy && typeof raw.ntfy === 'object') {
          config.ntfy.enabled = Boolean(raw.ntfy.enabled);
          config.ntfy.topic = typeof raw.ntfy.topic === 'string' ? raw.ntfy.topic.trim() : '';
          config.ntfy.serverUrl = typeof raw.ntfy.serverUrl === 'string' && raw.ntfy.serverUrl.trim() ? raw.ntfy.serverUrl.trim() : 'https://ntfy.sh';
        }
        if (raw.telegram && typeof raw.telegram === 'object') {
          config.telegram.enabled = Boolean(raw.telegram.enabled);
          if (typeof raw.telegram.startCode === 'string' && raw.telegram.startCode.trim()) {
            config.telegram.startCode = raw.telegram.startCode.trim();
          }
          config.telegram.pairedChatId = raw.telegram.pairedChatId != null ? String(raw.telegram.pairedChatId) : null;
          config.telegram.pairedUsername = raw.telegram.pairedUsername != null ? String(raw.telegram.pairedUsername) : null;
          const offset = Number(raw.telegram.updateOffset);
          config.telegram.updateOffset = Number.isInteger(offset) && offset >= 0 ? offset : 0;
        }
      }
    }
  } catch (err) {
    corrupt = true;
  }

  let token = null;
  try {
    if (fs.existsSync(tokenPath)) {
      token = fs.readFileSync(tokenPath, 'utf8').trim() || null;
    }
  } catch (err) {}

  return {
    ...config,
    telegramToken: token,
    corrupt
  };
}

function savePhoneSettings(storeDir, settings, newToken) {
  fs.mkdirSync(storeDir, { recursive: true });
  const settingsPath = path.join(storeDir, 'phone-settings.json');
  const tokenPath = path.join(storeDir, 'telegram-bot.token');

  const current = loadPhoneSettings(storeDir);

  const updated = {
    ntfy: {
      enabled: settings.ntfy?.enabled !== undefined ? Boolean(settings.ntfy.enabled) : current.ntfy.enabled,
      topic: settings.ntfy?.topic !== undefined ? String(settings.ntfy.topic).trim() : current.ntfy.topic,
      serverUrl: settings.ntfy?.serverUrl !== undefined && String(settings.ntfy.serverUrl).trim()
        ? String(settings.ntfy.serverUrl).trim() : current.ntfy.serverUrl
    },
    telegram: {
      enabled: settings.telegram?.enabled !== undefined ? Boolean(settings.telegram.enabled) : current.telegram.enabled,
      startCode: settings.telegram?.resetStartCode
        ? generateStartCode()
        : (settings.telegram?.startCode ? String(settings.telegram.startCode).trim() : current.telegram.startCode),
      pairedChatId: settings.telegram?.unpair ? null : (settings.telegram?.pairedChatId !== undefined ? (settings.telegram.pairedChatId ? String(settings.telegram.pairedChatId) : null) : current.telegram.pairedChatId),
      pairedUsername: settings.telegram?.unpair ? null : (settings.telegram?.pairedUsername !== undefined ? (settings.telegram.pairedUsername ? String(settings.telegram.pairedUsername) : null) : current.telegram.pairedUsername),
      updateOffset: settings.telegram?.updateOffset !== undefined && Number.isInteger(Number(settings.telegram.updateOffset))
        ? Math.max(0, Number(settings.telegram.updateOffset))
        : (Number(current.telegram.updateOffset) || 0)
    }
  };

  writePrivate(settingsPath, JSON.stringify(updated, null, 2) + '\n');

  if (newToken !== undefined) {
    if (newToken === null || newToken === '') {
      try { fs.unlinkSync(tokenPath); } catch {}
    } else {
      writePrivate(tokenPath, String(newToken).trim() + '\n');
    }
  }

  return loadPhoneSettings(storeDir);
}

async function sendNtfyAlert({ topic, serverUrl = 'https://ntfy.sh', message, title = 'Planwharf', priority = 'default', tags = [], fetchImpl = globalThis.fetch }) {
  if (!topic || typeof topic !== 'string' || !topic.trim()) {
    return { ok: false, reason: 'Topic is missing.' };
  }
  const base = serverUrl.replace(/\/+$/, '');
  const url = `${base}/${encodeURIComponent(topic.trim())}`;
  const headers = {
    'Title': String(title || '').replace(/[\r\n]+/g, ' ').slice(0, 200),
    'Priority': String(priority || 'default').replace(/[\r\n]+/g, '')
  };
  if (tags && tags.length) headers['Tags'] = tags.join(',');

  try {
    const res = await fetchImpl(url, {
      method: 'POST',
      headers,
      body: message
    });
    if (!res.ok) {
      return { ok: false, status: res.status, reason: `HTTP ${res.status}` };
    }
    return { ok: true, status: res.status };
  } catch (err) {
    return { ok: false, reason: err.message };
  }
}

async function sendTelegramMessage({ token, chatId, text, replyMarkup, apiBaseUrl = 'https://api.telegram.org', fetchImpl = globalThis.fetch }) {
  if (!token) return { ok: false, reason: 'Bot token is missing.' };
  if (!chatId) return { ok: false, reason: 'Chat ID is missing.' };

  const url = `${apiBaseUrl.replace(/\/+$/, '')}/bot${token}/sendMessage`;
  try {
    const res = await fetchImpl(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ chat_id: chatId, text, reply_markup: replyMarkup || mainKeyboard() })
    });
    const data = await res.json().catch(() => null);
    if (!res.ok || (data && !data.ok)) {
      return { ok: false, status: res.status, reason: data?.description || `HTTP ${res.status}` };
    }
    return { ok: true, data };
  } catch (err) {
    return { ok: false, reason: err.message };
  }
}

async function pollTelegramUpdates({ token, offset = 0, timeout = 0, apiBaseUrl = 'https://api.telegram.org', fetchImpl = globalThis.fetch }) {
  if (!token) return { ok: false, updates: [], reason: 'Bot token is missing.' };

  const url = `${apiBaseUrl.replace(/\/+$/, '')}/bot${token}/getUpdates?offset=${offset}&timeout=${timeout}`;
  try {
    const res = await fetchImpl(url);
    const data = await res.json().catch(() => null);
    if (!res.ok || !data?.ok) {
      return { ok: false, updates: [], reason: data?.description || `HTTP ${res.status}` };
    }
    return { ok: true, updates: Array.isArray(data.result) ? data.result : [] };
  } catch (err) {
    return { ok: false, updates: [], reason: err.message };
  }
}

function formatStatusMessage(projects = []) {
  if (!projects || projects.length === 0) {
    return 'Planwharf: No projects.';
  }
  const lines = ['📋 Planwharf Projects:'];
  for (const p of projects) {
    const name = sanitizeText(p.name || p.id || 'Project');
    let state = 'Idle';
    if (p.status === 'watching') state = 'Working';
    else if (p.status === 'complete') state = 'Done';
    else if (p.status === 'failed') state = 'Failed';
    else if (p.status === 'checkpointed') state = 'Stopped';
    else if (p.status === 'waiting_on_you') state = 'Waiting on you';
    else if (p.status === 'blocked_no_worker') state = 'Blocked (no usage)';
    else if (p.status) state = sanitizeText(p.status);

    let details = '';
    if (p.status === 'watching') {
      if (p.step?.step && p.step?.total) {
        details += ` (Step ${p.step.step}/${p.step.total})`;
      }
      if (p.timeLeft?.minutes != null) {
        details += ` · ~${p.timeLeft.minutes}m left`;
      }
    }
    lines.push(`${statusEmoji(state)} ${name}: ${state}${details}`);
  }
  return lines.join('\n');
}

function matchProject(projects = [], query = '') {
  if (!projects.length) return null;
  const q = String(query || '').trim().toLowerCase();
  if (!q) {
    // Only one project at all: it is the obvious target. Several projects: ask, even if only one is working.
    if (projects.length === 1) return projects[0];
    return null;
  }
  const named = value => (value && value.toLowerCase() === q);
  const exact = projects.filter(p => named(p.id) || named(p.name));
  if (exact.length === 1) return exact[0];
  if (exact.length > 1) return null;
  const partial = projects.filter(p => (p.id && p.id.toLowerCase().includes(q)) || (p.name && p.name.toLowerCase().includes(q)));
  return partial.length === 1 ? partial[0] : null;
}

function createPhoneNotifier({
  storeDir,
  getSnapshot,
  onStopProject,
  onResumeProject,
  fetchImpl = globalThis.fetch,
  apiBaseUrl = 'https://api.telegram.org',
  ntfyServerUrl = 'https://ntfy.sh',
  confirmWindowMs: confirmWindowMsOption = 60000
}) {
  let settings = loadPhoneSettings(storeDir);
  let telegramOffset = Number(settings.telegram.updateOffset) || 0;
  let pendingConfirmation = null; // { action: 'stop', projectId, projectName, expiresAt }
  const projectStates = new Map();
  const stepStates = new Map(); // projectId -> status
  const lanes = { transitions: Promise.resolve(), commands: Promise.resolve() };
  const confirmWindowMs = Number.isFinite(confirmWindowMsOption) && confirmWindowMsOption > 0 ? confirmWindowMsOption : 60000;

  function enqueue(lane, fn) {
    const run = lanes[lane].then(() => fn(), () => fn());
    lanes[lane] = run.then(() => {}, () => {});
    return run;
  }

  function rememberOffset(next) {
    telegramOffset = next;
    savePhoneSettings(storeDir, { telegram: { updateOffset: next } });
  }

  function getPublicSettings() {
    settings = loadPhoneSettings(storeDir);
    if (settings.corrupt) settings = savePhoneSettings(storeDir, {});
    return {
      ntfy: {
        enabled: settings.ntfy.enabled,
        topic: settings.ntfy.topic,
        serverUrl: settings.ntfy.serverUrl
      },
      telegram: {
        enabled: settings.telegram.enabled,
        startCode: settings.telegram.startCode,
        paired: Boolean(settings.telegram.pairedChatId),
        pairedChatId: settings.telegram.pairedChatId,
        pairedUsername: settings.telegram.pairedUsername,
        tokenConfigured: Boolean(settings.telegramToken),
        tokenPreview: maskToken(settings.telegramToken)
      }
    };
  }

  function updateSettings(patch = {}, newToken) {
    const telegram = {};
    if (patch.telegram && typeof patch.telegram === 'object') {
      if (patch.telegram.enabled !== undefined) telegram.enabled = patch.telegram.enabled;
      if (patch.telegram.unpair) telegram.unpair = true;
      if (patch.telegram.resetStartCode) telegram.resetStartCode = true;
    }
    settings = savePhoneSettings(storeDir, { ntfy: patch.ntfy, telegram }, newToken);
    if (telegram.unpair) pendingConfirmation = null;
    return getPublicSettings();
  }

  async function sendAlert({ type, projectId, projectName, goalTitle, durationMinutes, timeLeft, detail }) {
    const fresh = loadPhoneSettings(storeDir);
    const msg = formatAlertMessage({ type, projectName, goalTitle, durationMinutes, timeLeft, detail });
    const ntfyPromise = (async () => {
      if (fresh.ntfy.enabled && fresh.ntfy.topic) {
        const safeTitle = `Planwharf: ${sanitizeText(projectName)}`;
        const priority = type === 'failed' ? 'high' : type === 'needs_owner' ? 'urgent' : 'default';
        const tags = type === 'failed' ? ['warning'] : type === 'needs_owner' ? ['bell'] : ['white_check_mark'];
        return await sendNtfyAlert({
          topic: fresh.ntfy.topic,
          serverUrl: fresh.ntfy.serverUrl || ntfyServerUrl,
          message: msg,
          title: safeTitle,
          priority,
          tags,
          fetchImpl
        });
      }
      return null;
    })();

    const tgPromise = (async () => {
      if (fresh.telegram.enabled && fresh.telegramToken && fresh.telegram.pairedChatId) {
        return await sendTelegramMessage({
          token: fresh.telegramToken,
          chatId: fresh.telegram.pairedChatId,
          text: msg,
          apiBaseUrl,
          fetchImpl
        });
      }
      return null;
    })();

    const [ntfyRes, tgRes] = await Promise.all([ntfyPromise, tgPromise]);
    return { ntfy: ntfyRes, telegram: tgRes };
  }

  async function checkTransitions() {
    return enqueue('transitions', () => checkTransitionsNow());
  }

  async function checkTransitionsNow() {
    if (!getSnapshot) return;
    let snapshot;
    try {
      snapshot = await getSnapshot();
    } catch {
      return;
    }
    if (!snapshot || !Array.isArray(snapshot.projects)) return;

    for (const p of snapshot.projects) {
      if (p?.id == null || p.id === '') continue;
      const prev = projectStates.get(p.id);
      const curr = p.status;
      projectStates.set(p.id, curr);

      if (!prev) continue; // First observation establishes baseline
      const step = runningStep(p);
      const prevStep = stepStates.get(p.id) || null;
      stepStates.set(p.id, step);
      const alertType = alertForStatus(prev, curr) || (curr === 'watching' ? alertForStep(prevStep, step) : null);
      if (!alertType) continue;
      const detail = alertType === 'switched' && step ? `Now: ${step.model}` : alertType === 'verify' ? 'A different model is checking the work.' : alertType === 'restarted' ? 'The same step started again.' : '';
      await sendAlert({
        type: alertType,
        projectId: p.id,
        projectName: p.name,
        goalTitle: p.goal || p.jobTitle,
        timeLeft: p.timeLeft,
        detail
      });
    }
  }

  async function processTelegramMessage(msg) {
    return enqueue('commands', () => handleTelegramMessage(msg));
  }

  async function handleTelegramMessage(msg) {
    if (!msg || !msg.chat || !msg.chat.id) return;
    const chatId = String(msg.chat.id);
    const rawText = String(msg.text || '').trim();
    const text = rawText === '📊 Status' ? '/status' : rawText === '⏹ Stop' ? '/stop' : rawText === '▶️ Resume' ? '/resume' : rawText;
    if (!text) {
      if (msg.document || msg.photo || msg.video || msg.voice || msg.audio || msg.sticker) {
        const fresh = loadPhoneSettings(storeDir);
        if (fresh.telegramToken && String(fresh.telegram.pairedChatId) === chatId) {
          await sendTelegramMessage({ token: fresh.telegramToken, chatId, text: '📎 Attachments are not accepted. Use the buttons.', apiBaseUrl, fetchImpl });
        }
      }
      return;
    }

    const fresh = loadPhoneSettings(storeDir);
    const token = fresh.telegramToken;
    if (!token) return;

    // 1. Pairing check: if not paired yet
    if (!fresh.telegram.pairedChatId) {
      const match = text.match(/^\/?start\s*([a-zA-Z0-9_-]*)$/i);
      const enteredCode = match ? match[1] : text;
      if (enteredCode && enteredCode.toLowerCase() === fresh.telegram.startCode.toLowerCase()) {
        // Successful pairing!
        fresh.telegram.pairedChatId = chatId;
        fresh.telegram.pairedUsername = msg.chat.username || null;
        savePhoneSettings(storeDir, {
          telegram: {
            pairedChatId: chatId,
            pairedUsername: msg.chat.username || null
          }
        });
        await sendTelegramMessage({
          token,
          chatId,
          text: 'Planwharf connected!\nYou can now use:\n/status - projects and progress\n/stop <project> - stop the crew (requires confirmation)\n/resume <project> - resume work',
          apiBaseUrl,
          fetchImpl
        });
      }
      // If code does not match, ignore! "a message from another chat is ignored"
      return;
    }

    // 2. Chat check: if message comes from any other chat, ignore!
    if (chatId !== String(fresh.telegram.pairedChatId)) {
      return;
    }

    // 3. Handle confirmation if pending
    if (pendingConfirmation && Date.now() < pendingConfirmation.expiresAt) {
      const lower = text.toLowerCase();
      if (lower === 'yes' || lower === 'sì' || lower === 'si') {
        const target = pendingConfirmation;
        pendingConfirmation = null;
        try {
          if (typeof onStopProject === 'function') {
            await onStopProject(target.projectId);
          }
          await sendTelegramMessage({
            token,
            chatId,
            text: `The crew for "${sanitizeText(target.projectName)}" has been stopped.`,
            apiBaseUrl,
            fetchImpl
          });
        } catch (err) {
          await sendTelegramMessage({
            token,
            chatId,
            text: 'Planwharf could not complete that.',
            apiBaseUrl,
            fetchImpl
          });
        }
        return;
      } else {
        // Any other message cancels the pending confirmation
        pendingConfirmation = null;
        await sendTelegramMessage({
          token,
          chatId,
          text: 'Stop cancelled.',
          apiBaseUrl,
          fetchImpl
        });
        // If this message was another command (like /status), continue processing it below
        if (!text.startsWith('/')) return;
      }
    } else {
      pendingConfirmation = null;
    }

    // 4. Commands handling. Telegram may append @BotName and any letter case.
    let snapshot = null;
    let snapshotFailed = false;
    if (getSnapshot) {
      try { snapshot = await getSnapshot(); }
      catch { snapshotFailed = true; }
    }
    if (snapshotFailed) {
      await sendTelegramMessage({
        token,
        chatId,
        text: 'Planwharf could not read the projects. Try again.',
        apiBaseUrl,
        fetchImpl
      });
      return;
    }
    const projects = snapshot?.projects || [];
    const command = text.replace(/^\/([a-z0-9]+)(?:@[A-Za-z0-9_]+)?/i, (_, verb) => '/' + verb.toLowerCase());

    if (command === '/status' || command.startsWith('/status ')) {
      const statusText = formatStatusMessage(projects);
      await sendTelegramMessage({ token, chatId, text: statusText, replyMarkup: statusKeyboard(projects), apiBaseUrl, fetchImpl });
      return;
    }

    if (command.startsWith('/stop')) {
      const arg = command.replace(/^\/stop\s*/i, '').trim();
      const matched = matchProject(projects, arg);
      if (!matched) {
        if (projects.length === 0) {
          await sendTelegramMessage({ token, chatId, text: 'No projects available to stop.', apiBaseUrl, fetchImpl });
        } else {
          const names = projects.map(p => sanitizeText(p.name || p.id)).join(', ');
          await sendTelegramMessage({ token, chatId, text: `Please specify a project: /stop <name>\nProjects: ${names}`, apiBaseUrl, fetchImpl });
        }
        return;
      }
      pendingConfirmation = {
        action: 'stop',
        projectId: matched.id,
        projectName: matched.name || matched.id,
        expiresAt: Date.now() + confirmWindowMs
      };
      await sendTelegramMessage({
        token,
        chatId,
        text: `⏹ Stop the crew for "${sanitizeText(matched.name || matched.id)}"? Reply "yes" to confirm.`,
        replyMarkup: { inline_keyboard: [[{ text: '✅ Yes, stop', callback_data: `yes:${matched.id}` }, { text: '❌ Cancel', callback_data: 'cancel' }]] },
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    if (command.startsWith('/resume')) {
      const arg = command.replace(/^\/resume\s*/i, '').trim();
      const matched = matchProject(projects, arg);
      if (!matched) {
        if (projects.length === 0) {
          await sendTelegramMessage({ token, chatId, text: 'No projects available to resume.', apiBaseUrl, fetchImpl });
        } else {
          const names = projects.map(p => sanitizeText(p.name || p.id)).join(', ');
          await sendTelegramMessage({ token, chatId, text: `Please specify a project: /resume <name>\nProjects: ${names}`, apiBaseUrl, fetchImpl });
        }
        return;
      }
      try {
        if (typeof onResumeProject === 'function') {
          await onResumeProject(matched.id);
        }
        await sendTelegramMessage({
          token,
          chatId,
          text: `The crew for "${sanitizeText(matched.name || matched.id)}" has resumed.`,
          apiBaseUrl,
          fetchImpl
        });
      } catch (err) {
        await sendTelegramMessage({
          token,
          chatId,
          text: 'Planwharf could not complete that.',
          apiBaseUrl,
          fetchImpl
        });
      }
      return;
    }

    if (command === '/help' || command.startsWith('/start')) {
      await sendTelegramMessage({
        token,
        chatId,
        text: '📋 Planwharf\nUse the buttons, or:\n/status — projects\n/stop <project> — stop (asks confirmation)\n/resume <project> — resume',
        replyMarkup: mainKeyboard(),
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    // Default response for unrecognized text
    await sendTelegramMessage({
      token,
      chatId,
      text: 'Unrecognized command. Use /status, /stop <project>, or /resume <project>.',
      apiBaseUrl,
      fetchImpl
    });
  }

  async function answerCallback(token, id, text) {
    try {
      await fetchImpl(`${apiBaseUrl.replace(/\/+$/, '')}/bot${token}/answerCallbackQuery`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ callback_query_id: id, text: text || '' })
      });
    } catch { /* a missed acknowledgement only leaves the button spinning */ }
  }

  async function handleCallback(query) {
    if (!query || !query.message || !query.message.chat) return;
    const chatId = String(query.message.chat.id);
    const fresh = loadPhoneSettings(storeDir);
    if (!fresh.telegramToken || chatId !== String(fresh.telegram.pairedChatId)) {
      if (fresh.telegramToken && query.id) await answerCallback(fresh.telegramToken, query.id, '');
      return;
    }
    const data = String(query.data || '');
    const token = fresh.telegramToken;
    if (query.id) await answerCallback(token, query.id, '');
    if (data === 'status' || data === 'cancel') {
      if (data === 'cancel') pendingConfirmation = null;
      await handleTelegramMessage({ chat: { id: chatId }, text: '/status' });
      return;
    }
    const [verb, id] = data.split(':');
    if (!id) return;
    if (verb === 'stop') {
      pendingConfirmation = { action: 'stop', projectId: id, projectName: id, expiresAt: Date.now() + confirmWindowMs };
      await sendTelegramMessage({
        token, chatId, text: '⏹ Stop this crew?',
        replyMarkup: { inline_keyboard: [[{ text: '✅ Yes, stop', callback_data: `yes:${id}` }, { text: '❌ Cancel', callback_data: 'cancel' }]] },
        apiBaseUrl, fetchImpl
      });
      return;
    }
    if (verb === 'yes' && pendingConfirmation && pendingConfirmation.projectId === id) {
      await handleTelegramMessage({ chat: { id: chatId }, text: 'yes' });
      return;
    }
    if (verb === 'resume') {
      await handleTelegramMessage({ chat: { id: chatId }, text: `/resume ${id}` });
    }
  }

  async function pollUpdates() {
    return enqueue('commands', () => pollUpdatesNow());
  }

  async function pollUpdatesNow() {
    const fresh = loadPhoneSettings(storeDir);
    if (!fresh.telegram.enabled || !fresh.telegramToken) return;
    telegramOffset = Number(fresh.telegram.updateOffset) || telegramOffset;

    const res = await pollTelegramUpdates({
      token: fresh.telegramToken,
      offset: telegramOffset,
      timeout: 0,
      apiBaseUrl,
      fetchImpl
    });

    if (res.ok && res.updates.length) {
      for (const u of res.updates) {
        const id = Number(u.update_id);
        if (!Number.isInteger(id) || id < telegramOffset) continue;
        rememberOffset(id + 1);
        if (u.callback_query) await handleCallback(u.callback_query);
        else if (u.message) await handleTelegramMessage(u.message);
      }
    }
  }

  return {
    getSettings: getPublicSettings,
    updateSettings,
    sendAlert,
    checkTransitions,
    pollUpdates,
    processTelegramMessage
  };
}

module.exports = { alertForStatus, alertForStep,
  sanitizeText,
  sanitizeGoalTitle,
  formatDuration,
  formatAlertMessage,
  generateStartCode,
  maskToken,
  loadPhoneSettings,
  savePhoneSettings,
  sendNtfyAlert,
  sendTelegramMessage,
  pollTelegramUpdates,
  formatStatusMessage,
  matchProject,
  createPhoneNotifier
};
