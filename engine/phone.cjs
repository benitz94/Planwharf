'use strict';
const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');

// Job 12: Alerts and commands on the phone (ntfy and Telegram bot).
// Strict privacy rule: Never send prompts, code, file names or paths:
// only project name, goal title, status and times.

const FORBIDDEN_SECRET_REGEX = /(?:api[_-]?key|token|secret|password|bearer|ghp_[a-zA-Z0-9]+)\s*[:=]\s*[^\s,;]+/gi;

function sanitizeText(text) {
  if (!text || typeof text !== 'string') return '';
  // Strip file paths (Windows D:\... or Unix /... or relative ./...)
  let clean = text
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

function formatAlertMessage({ type, projectName, goalTitle, status, durationMinutes, timeLeft }) {
  const safeProject = sanitizeText(projectName) || 'Project';
  const safeGoal = sanitizeGoalTitle(goalTitle);
  let statusLabel = 'Done';
  if (type === 'failed') statusLabel = 'Failed';
  else if (type === 'needs_owner') statusLabel = 'Needs your attention';

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
      pairedUsername: null
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
        }
      }
    }
  } catch (err) {
    // Return default config if parse fails
  }

  let token = null;
  try {
    if (fs.existsSync(tokenPath)) {
      token = fs.readFileSync(tokenPath, 'utf8').trim() || null;
    }
  } catch (err) {}

  return {
    ...config,
    telegramToken: token
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
      pairedUsername: settings.telegram?.unpair ? null : (settings.telegram?.pairedUsername !== undefined ? (settings.telegram.pairedUsername ? String(settings.telegram.pairedUsername) : null) : current.telegram.pairedUsername)
    }
  };

  fs.writeFileSync(settingsPath, JSON.stringify(updated, null, 2) + '\n', { mode: 0o600 });

  if (newToken !== undefined) {
    if (newToken === null || newToken === '') {
      try { fs.unlinkSync(tokenPath); } catch {}
    } else {
      fs.writeFileSync(tokenPath, String(newToken).trim() + '\n', { mode: 0o600 });
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
    'Title': title,
    'Priority': priority
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

async function sendTelegramMessage({ token, chatId, text, apiBaseUrl = 'https://api.telegram.org', fetchImpl = globalThis.fetch }) {
  if (!token) return { ok: false, reason: 'Bot token is missing.' };
  if (!chatId) return { ok: false, reason: 'Chat ID is missing.' };

  const url = `${apiBaseUrl.replace(/\/+$/, '')}/bot${token}/sendMessage`;
  try {
    const res = await fetchImpl(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ chat_id: chatId, text })
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
  const lines = ['Planwharf Projects:'];
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
    lines.push(`• ${name}: ${state}${details}`);
  }
  return lines.join('\n');
}

function matchProject(projects = [], query = '') {
  if (!projects.length) return null;
  const q = String(query || '').trim().toLowerCase();
  if (!q) {
    // If only one project exists, default to it
    if (projects.length === 1) return projects[0];
    // If only one project is currently working, pick that
    const working = projects.filter(p => p.status === 'watching');
    if (working.length === 1) return working[0];
    return null;
  }
  // Try exact match on id or name
  let found = projects.find(p => (p.id && p.id.toLowerCase() === q) || (p.name && p.name.toLowerCase() === q));
  if (found) return found;
  // Try partial match on name or id
  found = projects.find(p => (p.id && p.id.toLowerCase().includes(q)) || (p.name && p.name.toLowerCase().includes(q)));
  return found || null;
}

function createPhoneNotifier({
  storeDir,
  getSnapshot,
  onStopProject,
  onResumeProject,
  fetchImpl = globalThis.fetch,
  apiBaseUrl = 'https://api.telegram.org',
  ntfyServerUrl = 'https://ntfy.sh'
}) {
  let settings = loadPhoneSettings(storeDir);
  let telegramOffset = 0;
  let pendingConfirmation = null; // { action: 'stop', projectId, projectName, expiresAt }
  const projectStates = new Map(); // projectId -> status

  function getPublicSettings() {
    settings = loadPhoneSettings(storeDir);
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
    settings = savePhoneSettings(storeDir, patch, newToken);
    if (patch.telegram?.unpair) {
      pendingConfirmation = null;
    }
    return getPublicSettings();
  }

  async function sendAlert({ type, projectId, projectName, goalTitle, durationMinutes, timeLeft }) {
    const fresh = loadPhoneSettings(storeDir);
    const ntfyPromise = (async () => {
      if (fresh.ntfy.enabled && fresh.ntfy.topic) {
        const msg = formatAlertMessage({ type, projectName, goalTitle, durationMinutes });
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
        const msg = formatAlertMessage({ type, projectName, goalTitle, durationMinutes, timeLeft });
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
    if (!getSnapshot) return;
    let snapshot;
    try {
      snapshot = await getSnapshot();
    } catch {
      return;
    }
    if (!snapshot || !Array.isArray(snapshot.projects)) return;

    for (const p of snapshot.projects) {
      const prev = projectStates.get(p.id);
      const curr = p.status;
      projectStates.set(p.id, curr);

      if (!prev) continue; // First observation establishes baseline
      if (prev === curr) continue;

      let alertType = null;
      if (curr === 'complete' && prev === 'watching') {
        alertType = 'done';
      } else if (curr === 'failed' && prev === 'watching') {
        alertType = 'failed';
      } else if (['waiting_on_you', 'blocked_no_worker', 'blocked_no_folder', 'checkpointed'].includes(curr) && prev === 'watching') {
        alertType = 'needs_owner';
      }

      if (alertType) {
        await sendAlert({
          type: alertType,
          projectId: p.id,
          projectName: p.name,
          goalTitle: p.goal || p.jobTitle,
          timeLeft: p.timeLeft
        });
      }
    }
  }

  async function processTelegramMessage(msg) {
    if (!msg || !msg.chat || !msg.chat.id) return;
    const chatId = String(msg.chat.id);
    const text = String(msg.text || '').trim();
    if (!text) return;

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
            text: `Failed to stop crew: ${err.message}`,
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

    // 4. Commands handling
    const snapshot = getSnapshot ? await getSnapshot().catch(() => null) : null;
    const projects = snapshot?.projects || [];

    if (text === '/status' || text.startsWith('/status ')) {
      const statusText = formatStatusMessage(projects);
      await sendTelegramMessage({ token, chatId, text: statusText, apiBaseUrl, fetchImpl });
      return;
    }

    if (text.startsWith('/stop')) {
      const arg = text.replace(/^\/stop\s*/i, '').trim();
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
        expiresAt: Date.now() + 60000
      };
      await sendTelegramMessage({
        token,
        chatId,
        text: `Stop the crew for "${sanitizeText(matched.name || matched.id)}"? Reply "yes" to confirm.`,
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    if (text.startsWith('/resume')) {
      const arg = text.replace(/^\/resume\s*/i, '').trim();
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
          text: `Failed to resume: ${err.message}`,
          apiBaseUrl,
          fetchImpl
        });
      }
      return;
    }

    if (text === '/help' || text.startsWith('/start')) {
      await sendTelegramMessage({
        token,
        chatId,
        text: 'Planwharf Commands:\n/status - View projects and progress\n/stop <project> - Stop crew (asks confirmation)\n/resume <project> - Resume crew',
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

  async function pollUpdates() {
    const fresh = loadPhoneSettings(storeDir);
    if (!fresh.telegram.enabled || !fresh.telegramToken) return;

    const res = await pollTelegramUpdates({
      token: fresh.telegramToken,
      offset: telegramOffset,
      timeout: 0,
      apiBaseUrl,
      fetchImpl
    });

    if (res.ok && res.updates.length) {
      for (const u of res.updates) {
        if (u.update_id >= telegramOffset) {
          telegramOffset = u.update_id + 1;
        }
        if (u.message) {
          await processTelegramMessage(u.message);
        }
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

module.exports = {
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
