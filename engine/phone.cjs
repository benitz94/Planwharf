'use strict';
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const {
  defaultOllamaSettings,
  normalizeOllamaSettings,
  withCaptain,
  isClearMemory,
  isBareConfirmation,
  loadChatMemory,
  saveChatMemory,
  buildReplyMessages,
  askOllama,
  listInstalledModels
} = require('./phone-ollama.cjs');
const {
  languageOrDefault,
  languageName,
  say,
  telegramAlertLabel,
  plainAlert,
  stateLabel,
  isDetailsRequest,
  parseLanguageCommand,
  fitTelegram
} = require('./phone-voice.cjs');

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
  restarted: 'Step restarted 🔁',
  updated: 'Updated •'
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

function statusKeyboard(projects = [], language) {
  const lang = languageOrDefault(language);
  const isIt = lang === 'it';
  const rows = [];
  for (const p of projects) {
    if (!p || p.id == null || p.id === '') continue;
    const name = sanitizeText(p.name || p.id).slice(0, 24);
    const id = String(p.id);
    if (id.length > 48) continue;
    if (p.status === 'watching') rows.push([{ text: isIt ? `⏹ Ferma ${name}` : `⏹ Stop ${name}`, callback_data: `stop:${id}` }]);
    else if (p.status === 'checkpointed' || p.status === 'failed') rows.push([{ text: isIt ? `▶️ Riprendi ${name}` : `▶️ Resume ${name}`, callback_data: `resume:${id}` }]);
  }
  rows.push([{ text: isIt ? '🔄 Aggiorna' : '🔄 Refresh', callback_data: 'status' }]);
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

function escapeHtml(text) {
  if (text == null) return '';
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function formatProgressBar(step, total) {
  let s, t;
  if (typeof step === 'object' && step !== null) {
    s = Number(step.step);
    t = Number(step.total);
  } else if (typeof step === 'string' && step.includes('/')) {
    const parts = step.split('/');
    s = Number(parts[0]);
    t = Number(parts[1]);
  } else {
    s = Number(step);
    t = Number(total);
  }
  if (!Number.isFinite(s) || !Number.isFinite(t) || t <= 0 || s < 0) return '';
  const current = Math.min(s, t);
  const maxBlocks = t <= 10 ? t : 10;
  const filledExact = t <= 10 ? current : (current / t) * maxBlocks;
  const width = Math.floor(maxBlocks);
  const filled = Math.max(0, Math.min(width, Math.round(filledExact)));
  const empty = Math.max(0, width - filled);
  if (filled + empty <= 0) return '';
  return `${'■'.repeat(filled)}${'□'.repeat(empty)} ${s}/${t}`;
}

function progressCaption(language, step) {
  const bar = formatProgressBar(step);
  const fraction = bar.match(/(\d+(?:\.\d+)?)\/(\d+(?:\.\d+)?)$/);
  if (!fraction) return '';
  return say(language, 'progressStep', { current: fraction[1], total: fraction[2] });
}

function formatAlertMessage({ type, projectName, goalTitle, status, durationMinutes, timeLeft, detail, language }) {
  const lang = languageOrDefault(language);
  const safeProject = sanitizeText(projectName) || 'Project';
  const safeGoal = sanitizeGoalTitle(goalTitle);
  const known = Object.prototype.hasOwnProperty.call(ALERT_LABELS, type);
  const statusLabel = lang === 'it'
    ? (known ? telegramAlertLabel('it', type) : 'Aggiornato')
    : (ALERT_LABELS[type] || 'Updated');
  const goalWord = lang === 'it' ? 'Obiettivo' : 'Goal';
  const statusWord = lang === 'it' ? 'Stato' : 'Status';

  let msg = `Planwharf: ${safeProject}\n${goalWord}: ${safeGoal}\n${statusWord}: ${statusLabel}`;

  if (type === 'done' && durationMinutes != null) {
    const dur = formatDuration(durationMinutes);
    if (dur) msg += `\n${lang === 'it' ? 'Durata' : 'Duration'}: ${dur}`;
  }

  if (timeLeft && (timeLeft.minutes != null || timeLeft.endsAt)) {
    let timeText = '';
    if (timeLeft.minutes != null) {
      timeText = lang === 'it' ? `Circa ${timeLeft.minutes}m rimasti` : `About ${timeLeft.minutes}m left`;
      if (timeLeft.longerThanUsual) timeText += lang === 'it' ? ' (più del solito)' : ' (longer than usual)';
      if (timeLeft.sure) timeText += ` · ${timeLeft.sure}`;
    }
    if (timeText) msg += `\n${lang === 'it' ? 'Stima' : 'Estimate'}: ${timeText}`;
  }
  if (detail) msg += `\n${sanitizeText(detail)}`;
  const plain = plainAlert(lang, known ? type : 'updated');
  if (plain) msg += `\n${plain}`;

  return msg;
}

// The headline and the sign-off of each alert, in the voice of a ship's crew.
const BUCCANEER = {
  en: {
    ahoy: 'AHOY, CAPTAIN!',
    project: 'Ship', mission: 'Mission', status: 'Status', timeLeft: 'Time left', duration: 'Voyage time',
    minutes: n => `About ${n} minute${n === 1 ? '' : 's'}`,
    lines: {
      started: ['🚀 The crew has set sail on a new mission!', '🦜 The crew is hard at work. I\'ll keep you posted, Captain!'],
      resumed: ['⛵ Back at sea!', '🦜 The crew picked up where it left off.'],
      done: ['🏆 Mission accomplished!', '🍻 The work is in the hold. Inspect the treasure and push it when you like it.'],
      failed: ['🌊 Rough seas: the mission hit a snag.', '🛟 The crew has stopped. Have a look when you can, Captain.'],
      needs_owner: ['📯 The crew awaits your orders!', '🗺️ Open Planwharf and tell them which way to go.'],
      stopped: ['⚓ The crew has dropped anchor.', '🦜 Say /resume when you want to sail again.'],
      blocked: ['🪨 Stuck on the rocks!', '🛠️ The crew needs a worker and a place to work.'],
      verify: ['🔭 A second pair of eyes is checking the work.', '🦜 Another model is inspecting every plank.'],
      switched: ['🔁 Change of watch!', '🦜 Another sailor took over this step.'],
      restarted: ['🔁 Same step, fresh start.', '🦜 The crew is trying again.'],
      updated: ['🧭 News from the deck.', '🦜 I\'ll keep you posted, Captain!']
    }
  },
  it: {
    ahoy: 'AHOY, CAPITANO!',
    project: 'Nave', mission: 'Missione', status: 'Stato', timeLeft: 'Tempo rimasto', duration: 'Durata del viaggio',
    minutes: n => `Circa ${n} minut${n === 1 ? 'o' : 'i'}`,
    lines: {
      started: ['🚀 L\'equipaggio ha preso il largo per una nuova missione!', '🦜 La ciurma è al lavoro. Ti tengo aggiornato, Capitano!'],
      resumed: ['⛵ Di nuovo in mare!', '🦜 La ciurma riparte da dove si era fermata.'],
      done: ['🏆 Missione compiuta!', '🍻 Il bottino è nella stiva. Controllalo e fai il push quando ti piace.'],
      failed: ['🌊 Mare grosso: la missione si è incagliata.', '🛟 La ciurma si è fermata. Dai un\'occhiata quando puoi, Capitano.'],
      needs_owner: ['📯 La ciurma aspetta i tuoi ordini!', '🗺️ Apri Planwharf e indica la rotta.'],
      stopped: ['⚓ La ciurma ha gettato l\'ancora.', '🦜 Scrivi /resume quando vuoi ripartire.'],
      blocked: ['🪨 Incagliati sugli scogli!', '🛠️ Alla ciurma servono un marinaio e un posto dove lavorare.'],
      verify: ['🔭 Un secondo paio d\'occhi controlla il lavoro.', '🦜 Un altro modello ispeziona ogni asse.'],
      switched: ['🔁 Cambio della guardia!', '🦜 Un altro marinaio ha preso questo passo.'],
      restarted: ['🔁 Stesso passo, nuova partenza.', '🦜 La ciurma riprova.'],
      updated: ['🧭 Notizie dal ponte.', '🦜 Ti tengo aggiornato, Capitano!']
    }
  }
};

// Text written with Markdown code marks (`main`) shows them raw in Telegram's HTML: turn them into <code>.
function htmlWithCode(text) {
  return escapeHtml(text).replace(/`([^`\n]+)`/g, '<code>$1</code>');
}

// A mission title for someone who does not program: the first sentence, without branch and commit talk.
function missionTitle(goalTitle) {
  const first = sanitizeGoalTitle(goalTitle).split(/(?<=[.!?:;])\s/)[0];
  return first
    .replace(/[,;]?\s+(?:as|following|according to|seguendo|come)\b[^`]*`.*$/i, '')
    .replace(/\s*(?:on|from|in|su|da)\s+(?:a\s+new\s+branch\s+)?`[^`]+`.*$/i, '')
    .replace(/[,;:]\s*$/, '').trim() || 'Work';
}

// The crew of a project as the phone shows it: the lead, then each Build and Verify step with its model and
// state. A model that took over since the last message shows as "before → now".
const STEP_STATE = { COMPLETE: '✅', RUNNING: '⚙️', FAILED: '❌', BLOCKED: '⚠️' };
function crewOfProject(project, models = [], previous = {}) {
  if (!project) return null;
  const label = id => (models.find(model => model && model.id === id) || {}).label || id || '';
  const lead = (Array.isArray(project.crew) ? project.crew : []).find(member => member && member.role === 'Orchestrator')?.name || '';
  const steps = (Array.isArray(project.subTasks) ? project.subTasks : []).filter(Boolean).map(sub => {
    const name = label(sub.assignedModelId);
    const verify = /^Verify the work independently/i.test(String(sub.title || '')) || /^check\b/i.test(String(sub.title || ''));
    const from = previous[sub.id] && previous[sub.id] !== name ? previous[sub.id] : '';
    return { id: sub.id, role: verify ? 'verify' : 'build', name, from, state: STEP_STATE[sub.status] || '' };
  });
  return { lead, leadFrom: previous.__lead && previous.__lead !== lead ? previous.__lead : '', build: steps.filter(step => step.role === 'build'), verify: steps.filter(step => step.role === 'verify') };
}

function crewLines(crew) {
  if (!crew) return [];
  const who = step => `${step.from ? escapeHtml(sanitizeText(step.from)) + ' → ' : ''}${escapeHtml(sanitizeText(step.name))}${step.state ? ' ' + step.state : ''}`;
  const lines = [];
  if (crew.lead) lines.push(`👑 <b>Lead</b> ${crew.leadFrom ? escapeHtml(sanitizeText(crew.leadFrom)) + ' → ' : ''}${escapeHtml(sanitizeText(crew.lead))}`);
  if (crew.build.length) lines.push(`🔨 <b>Build</b> ${crew.build.map(who).join(' · ')}`);
  if (crew.verify.length) lines.push(`🔍 <b>Verify</b> ${crew.verify.map(who).join(' · ')}`);
  return lines;
}

function formatTelegramAlertMessage({ type, projectName, goalTitle, durationMinutes, timeLeft, detail, step, language, crew }) {
  const lang = languageOrDefault(language);
  const voice = BUCCANEER[lang] || BUCCANEER.en;
  const known = Object.prototype.hasOwnProperty.call(ALERT_LABELS, type);
  const kind = known ? type : 'updated';
  const [headline, signoff] = voice.lines[kind] || voice.lines.updated;
  const safeProject = escapeHtml(sanitizeText(projectName) || 'Project');

  // One line each: the news, where, how far, who.
  const status = [telegramAlertLabel(lang, kind)];
  const bar = formatProgressBar(step);
  if (bar) status.push(bar);
  if (kind === 'done' && durationMinutes != null) {
    const dur = formatDuration(durationMinutes);
    if (dur) status.push('⏱️ ' + dur);
  }
  if (timeLeft && timeLeft.minutes != null) {
    let timeText = voice.minutes(timeLeft.minutes);
    if (timeLeft.longerThanUsual) timeText += lang === 'it' ? ' (più del solito)' : ' (longer than usual)';
    status.push('⏳ ' + escapeHtml(timeText));
  }
  const lines = [
    `🏴‍☠️ <b>${voice.ahoy}</b> ${headline}`,
    `🚢 <b>${safeProject}</b> · 🎯 ${htmlWithCode(missionTitle(goalTitle))}`,
    `🧭 ${status.join(' · ')}`,
    ...crewLines(crew)
  ];
  if (detail) lines.push(htmlWithCode(sanitizeText(detail)));
  lines.push(signoff);
  return lines.join('\n');
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
    },
    ollama: defaultOllamaSettings(),
    language: 'en'
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
        if (raw.ollama && typeof raw.ollama === 'object') config.ollama = normalizeOllamaSettings(raw.ollama);
        if (raw.language !== undefined) config.language = languageOrDefault(raw.language);
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
    },
    ollama: normalizeOllamaSettings(settings.ollama === undefined ? current.ollama : {
      enabled: settings.ollama.enabled !== undefined ? settings.ollama.enabled : current.ollama.enabled,
      baseUrl: settings.ollama.baseUrl !== undefined ? settings.ollama.baseUrl : current.ollama.baseUrl,
      model: settings.ollama.model !== undefined ? settings.ollama.model : current.ollama.model
    }),
    language: settings.language !== undefined ? languageOrDefault(settings.language) : languageOrDefault(current.language)
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

async function sendTelegramMessage({ token, chatId, text, parseMode = 'HTML', replyMarkup, apiBaseUrl = 'https://api.telegram.org', fetchImpl = globalThis.fetch }) {
  if (!token) return { ok: false, reason: 'Bot token is missing.' };
  if (!chatId) return { ok: false, reason: 'Chat ID is missing.' };

  const url = `${apiBaseUrl.replace(/\/+$/, '')}/bot${token}/sendMessage`;
  const payload = { chat_id: chatId, text: fitTelegram(text), reply_markup: replyMarkup || mainKeyboard() };
  if (parseMode) payload.parse_mode = parseMode;
  try {
    const res = await fetchImpl(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
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

function formatStatusMessage(projects = [], language) {
  const lang = languageOrDefault(language);
  if (!projects || projects.length === 0) return say(lang, 'statusEmpty');
  const sections = [say(lang, 'statusHeader')];
  const statusWord = lang === 'it' ? 'Stato' : 'Status';
  for (const p of projects) {
    const name = escapeHtml(sanitizeText(p.name || p.id || 'Project'));
    let state = 'Idle';
    if (p.status === 'watching') state = 'Working';
    else if (p.status === 'complete') state = 'Done';
    else if (p.status === 'failed') state = 'Failed';
    else if (p.status === 'checkpointed') state = 'Stopped';
    else if (p.status === 'waiting_on_you') state = 'Waiting on you';
    else if (p.status === 'blocked_no_worker' || p.status === 'blocked_no_folder') state = 'Blocked (no usage)';
    else if (p.status === 'idle') state = 'Idle';
    else if (p.status) state = sanitizeText(p.status);

    const icon = statusEmoji(state);
    let details = '';
    if (p.status === 'watching' && p.timeLeft?.minutes != null) {
      details = lang === 'it'
        ? ` · circa ${escapeHtml(p.timeLeft.minutes)}m rimasti`
        : ` · ~${escapeHtml(p.timeLeft.minutes)}m left`;
    }
    const bar = formatProgressBar(p.step);
    let item = `<b>${name}</b>\n${icon} <b>${statusWord}:</b> ${escapeHtml(stateLabel(lang, state))}${details}`;
    if (bar) item += `\n${bar}`;
    const caption = progressCaption(lang, p.step);
    if (caption) item += `\n${escapeHtml(caption)}`;
    sections.push(item);
  }
  return sections.join('\n\n');
}

function matchProject(projects = [], query = '') {
  if (!projects.length) return null;
  const q = String(query || '').trim().toLowerCase();
  if (!q) {
    // Only one project at all: it is the obvious target. Several projects: ask, even if only one is working.
    if (projects.length === 1) return projects[0];
    return null;
  }
  const named = value => (value != null && String(value).toLowerCase() === q);
  const exact = projects.filter(p => named(p.id) || named(p.name));
  if (exact.length === 1) return exact[0];
  if (exact.length > 1) return null;
  const partial = projects.filter(p => (p.id != null && String(p.id).toLowerCase().includes(q)) || (p.name != null && String(p.name).toLowerCase().includes(q)));
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
  let lastTechnical = null;

  function rememberTechnical(code, error) {
    const raw = error && typeof error === 'object' ? error.message : error;
    const note = sanitizeText(raw) || 'No further note was given.';
    lastTechnical = { code, note: note.slice(0, 300) };
  }

  function technicalReply(language) {
    if (!lastTechnical) return say(language, 'detailsNone');
    return say(language, 'details', {
      code: escapeHtml(lastTechnical.code),
      note: escapeHtml(lastTechnical.note)
    });
  }
  const projectStates = new Map();
  const stepStates = new Map(); // projectId -> status
  const crewBefore = new Map(); // projectId -> { subTaskId: model label, __lead: lead label } at the last message
  const lanes = { transitions: Promise.resolve(), commands: Promise.resolve() };
  const chatMemory = loadChatMemory(storeDir);
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
      },
      ollama: {
        enabled: settings.ollama.enabled,
        baseUrl: settings.ollama.baseUrl,
        model: settings.ollama.model
      },
      language: languageOrDefault(settings.language)
    };
  }

  function updateSettings(patch = {}, newToken) {
    const telegram = {};
    if (patch.telegram && typeof patch.telegram === 'object') {
      if (patch.telegram.enabled !== undefined) telegram.enabled = patch.telegram.enabled;
      if (patch.telegram.unpair) telegram.unpair = true;
      if (patch.telegram.resetStartCode) telegram.resetStartCode = true;
    }
    const ollama = patch.ollama && typeof patch.ollama === 'object' ? {
      enabled: patch.ollama.enabled,
      baseUrl: patch.ollama.baseUrl,
      model: patch.ollama.model
    } : undefined;
    settings = savePhoneSettings(storeDir, { ntfy: patch.ntfy, telegram, ollama, language: patch.language }, newToken);
    if (telegram.unpair) pendingConfirmation = null;
    return getPublicSettings();
  }

  async function sendAlert({ type, projectId, projectName, goalTitle, durationMinutes, timeLeft, detail, step, crew }) {
    const fresh = loadPhoneSettings(storeDir);
    const ntfyPromise = (async () => {
      if (fresh.ntfy.enabled && fresh.ntfy.topic) {
        const safeTitle = `Planwharf: ${sanitizeText(projectName)}`;
        const priority = type === 'failed' ? 'high' : type === 'needs_owner' ? 'urgent' : 'default';
        const tags = type === 'failed' ? ['warning'] : type === 'needs_owner' ? ['bell'] : ['white_check_mark'];
        const plainMsg = formatAlertMessage({ type, projectName, goalTitle, durationMinutes, timeLeft, detail, language: fresh.language });
        return await sendNtfyAlert({
          topic: fresh.ntfy.topic,
          serverUrl: fresh.ntfy.serverUrl || ntfyServerUrl,
          message: plainMsg,
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
        const tgMsg = formatTelegramAlertMessage({ type, projectName, goalTitle, durationMinutes, timeLeft, detail, step, crew, language: fresh.language });
        return await sendTelegramMessage({
          token: fresh.telegramToken,
          chatId: fresh.telegram.pairedChatId,
          text: tgMsg,
          parseMode: 'HTML',
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
      // Who held each step and the lead before: a change shows with an arrow in the next message.
      const before = crewBefore.get(p.id) || {};
      const crew = crewOfProject(p, Array.isArray(snapshot.models) ? snapshot.models : [], before);
      const alertType = alertForStatus(prev, curr) || (curr === 'watching' ? alertForStep(prevStep, step) : null);
      if (!alertType) {
        // No message now: keep the first model of each changed step, so the arrow shows it later.
        const kept = { ...before };
        for (const item of [...(crew?.build || []), ...(crew?.verify || [])]) if (!kept[item.id]) kept[item.id] = item.name;
        if (!kept.__lead && crew?.lead) kept.__lead = crew.lead;
        crewBefore.set(p.id, kept);
        continue;
      }
      const now = { __lead: crew?.lead || '' };
      for (const item of [...(crew?.build || []), ...(crew?.verify || [])]) now[item.id] = item.name;
      crewBefore.set(p.id, now);
      const detail = alertType === 'restarted' ? 'The same step started again.' : '';
      await sendAlert({
        type: alertType,
        projectId: p.id,
        projectName: p.name,
        goalTitle: p.goal || p.jobTitle,
        timeLeft: p.timeLeft,
        detail,
        step: p.step,
        crew
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
    const text = (rawText === '📊 Status' || rawText === '📊 Stato') ? '/status'
      : (rawText === '⏹ Stop' || rawText === '⏹ Ferma') ? '/stop'
      : (rawText === '▶️ Resume' || rawText === '▶️ Riprendi') ? '/resume'
      : rawText;
    if (!text) {
      if (msg.document || msg.photo || msg.video || msg.voice || msg.audio || msg.sticker) {
        const fresh = loadPhoneSettings(storeDir);
        if (fresh.telegramToken && String(fresh.telegram.pairedChatId) === chatId) {
          await sendTelegramMessage({
            token: fresh.telegramToken,
            chatId,
            text: say(languageOrDefault(fresh.language), 'attachments'),
            apiBaseUrl,
            fetchImpl
          });
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
          text: say(languageOrDefault(fresh.language), 'pairing'),
          replyMarkup: mainKeyboard(),
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
      if (isBareConfirmation(text)) {
        const target = pendingConfirmation;
        pendingConfirmation = null;
        try {
          if (typeof onStopProject === 'function') {
            await onStopProject(target.projectId);
          }
          if (lastTechnical && lastTechnical.code === 'stop-failed') lastTechnical = null;
          await sendTelegramMessage({
            token,
            chatId,
            text: say(languageOrDefault(fresh.language), 'crewStopped', {
              name: escapeHtml(sanitizeText(target.projectName))
            }),
            apiBaseUrl,
            fetchImpl
          });
        } catch (err) {
          rememberTechnical('stop-failed', err);
          await sendTelegramMessage({
            token,
            chatId,
            text: say(languageOrDefault(fresh.language), 'couldNotCompleteStop'),
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
          text: say(languageOrDefault(fresh.language), 'stopCancelled'),
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
    const lang = languageOrDefault(fresh.language);
    const command = text.replace(/^\/([a-z0-9]+)(?:@[A-Za-z0-9_]+)?/i, (_, verb) => '/' + verb.toLowerCase());

    if (isDetailsRequest(text) || isDetailsRequest(command)) {
      await sendTelegramMessage({
        token, chatId, text: technicalReply(lang), replyMarkup: mainKeyboard(), apiBaseUrl, fetchImpl
      });
      return;
    }

    const languageCommand = parseLanguageCommand(text);
    if (languageCommand) {
      if (languageCommand.action === 'set') {
        savePhoneSettings(storeDir, { language: languageCommand.language });
        await sendTelegramMessage({
          token,
          chatId,
          text: say(languageCommand.language, 'languageSet', {
            languageName: languageName(languageCommand.language, languageCommand.language)
          }),
          replyMarkup: mainKeyboard(),
          apiBaseUrl,
          fetchImpl
        });
      } else if (languageCommand.action === 'unknown') {
        await sendTelegramMessage({
          token, chatId, text: say(lang, 'languageUnknown'), replyMarkup: mainKeyboard(), apiBaseUrl, fetchImpl
        });
      } else {
        await sendTelegramMessage({
          token,
          chatId,
          text: say(lang, 'languageShow', { languageName: languageName(lang, lang) }),
          replyMarkup: mainKeyboard(),
          apiBaseUrl,
          fetchImpl
        });
      }
      return;
    }

    let snapshot = null;
    let snapshotFailed = false;
    let snapshotError = null;
    if (getSnapshot) {
      try { snapshot = await getSnapshot(); }
      catch (error) { snapshotFailed = true; snapshotError = error; }
    }
    if (snapshotFailed) {
      rememberTechnical('projects-unreadable', snapshotError);
      await sendTelegramMessage({
        token,
        chatId,
        text: say(lang, 'couldNotReadProjects'),
        apiBaseUrl,
        fetchImpl
      });
      return;
    }
    if (lastTechnical && lastTechnical.code === 'projects-unreadable') lastTechnical = null;
    const projects = snapshot?.projects || [];

    if (command === '/status' || command === '/stato' || command.startsWith('/status ') || command.startsWith('/stato ')) {
      const statusText = formatStatusMessage(projects, lang);
      await sendTelegramMessage({ token, chatId, text: statusText, replyMarkup: statusKeyboard(projects, lang), apiBaseUrl, fetchImpl });
      return;
    }

    if (command.startsWith('/stop') || command.startsWith('/ferma')) {
      const arg = command.replace(/^\/(?:stop|ferma)\s*/i, '').trim();
      const matched = matchProject(projects, arg);
      if (!matched) {
        if (projects.length === 0) {
          await sendTelegramMessage({
            token,
            chatId,
            text: say(lang, 'stopNone'),
            apiBaseUrl,
            fetchImpl
          });
        } else {
          const names = projects.map(p => escapeHtml(sanitizeText(p.name || p.id))).join(', ');
          await sendTelegramMessage({
            token,
            chatId,
            text: say(lang, 'stopSpecify', { names }),
            apiBaseUrl,
            fetchImpl
          });
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
        text: say(lang, 'stopConfirm', { name: escapeHtml(sanitizeText(matched.name || matched.id)) }),
        replyMarkup: { inline_keyboard: [[{ text: lang === 'it' ? '✅ Sì, ferma' : '✅ Yes, stop', callback_data: `yes:${matched.id}` }, { text: lang === 'it' ? '❌ Annulla' : '❌ Cancel', callback_data: 'cancel' }]] },
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    if (command.startsWith('/resume') || command.startsWith('/riprendi')) {
      const arg = command.replace(/^\/(?:resume|riprendi)\s*/i, '').trim();
      const matched = matchProject(projects, arg);
      if (!matched) {
        if (projects.length === 0) {
          await sendTelegramMessage({
            token,
            chatId,
            text: say(lang, 'resumeNone'),
            apiBaseUrl,
            fetchImpl
          });
        } else {
          const names = projects.map(p => escapeHtml(sanitizeText(p.name || p.id))).join(', ');
          await sendTelegramMessage({
            token,
            chatId,
            text: say(lang, 'resumeSpecify', { names }),
            apiBaseUrl,
            fetchImpl
          });
        }
        return;
      }
      try {
        if (typeof onResumeProject === 'function') {
          await onResumeProject(matched.id);
        }
        if (lastTechnical && lastTechnical.code === 'resume-failed') lastTechnical = null;
        await sendTelegramMessage({
          token,
          chatId,
          text: say(lang, 'crewResumed', { name: escapeHtml(sanitizeText(matched.name || matched.id)) }),
          apiBaseUrl,
          fetchImpl
        });
      } catch (err) {
        rememberTechnical('resume-failed', err);
        await sendTelegramMessage({
          token,
          chatId,
          text: say(lang, 'couldNotCompleteResume'),
          apiBaseUrl,
          fetchImpl
        });
      }
      return;
    }

    if (command === '/help' || command === '/aiuto' || command === '/guida' || command.startsWith('/start')) {
      await sendTelegramMessage({
        token,
        chatId,
        text: say(lang, 'help'),
        replyMarkup: mainKeyboard(),
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    if (isClearMemory(text) || isClearMemory(command)) {
      chatMemory.clear(chatId);
      try { saveChatMemory(storeDir, chatMemory); } catch { /* A full disk must not block the reply. */ }
      await sendTelegramMessage({
        token,
        chatId,
        text: say(lang, 'memoryCleared'),
        replyMarkup: mainKeyboard(),
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    const ollama = normalizeOllamaSettings(fresh.ollama);
    if (!ollama.enabled || isBareConfirmation(text)) {
      await sendTelegramMessage({
        token,
        chatId,
        text: say(lang, 'unrecognized'),
        replyMarkup: mainKeyboard(),
        apiBaseUrl,
        fetchImpl
      });
      return;
    }

    const messages = buildReplyMessages({
      question: text,
      projects,
      history: chatMemory.history(chatId),
      sanitize: sanitizeText,
      model: ollama.model,
      language: lang
    });
    chatMemory.remember(chatId, 'user', sanitizeText(text));
    const reply = await askOllama({
      baseUrl: ollama.baseUrl,
      model: ollama.model,
      messages,
      fetchImpl,
      timeoutMs: 45000,
      language: lang
    });
    if (!reply.ok) rememberTechnical('companion-offline', reply.reason);
    if (reply.ok) chatMemory.remember(chatId, 'assistant', sanitizeText(reply.text));
    try { saveChatMemory(storeDir, chatMemory); } catch { /* Keep the answer even if memory cannot be stored. */ }
    await sendTelegramMessage({
      token,
      chatId,
      text: reply.text,
      replyMarkup: mainKeyboard(),
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
      const lang = languageOrDefault(loadPhoneSettings(storeDir).language);
      let projName = id;
      if (getSnapshot) {
        try {
          const snap = await getSnapshot();
          const found = (snap?.projects || []).find(p => String(p?.id) === id);
          if (found?.name) projName = found.name;
        } catch {}
      }
      pendingConfirmation = { action: 'stop', projectId: id, projectName: projName, expiresAt: Date.now() + confirmWindowMs };
      await sendTelegramMessage({
        token, chatId, text: say(lang, 'stopConfirmTap'),
        replyMarkup: { inline_keyboard: [[{ text: lang === 'it' ? '✅ Sì, ferma' : '✅ Yes, stop', callback_data: `yes:${id}` }, { text: lang === 'it' ? '❌ Annulla' : '❌ Cancel', callback_data: 'cancel' }]] },
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

  async function probeOllama() {
    const fresh = loadPhoneSettings(storeDir);
    const listed = await listInstalledModels({ baseUrl: fresh.ollama.baseUrl, fetchImpl, timeoutMs: 4000 });
    return {
      ok: listed.ok,
      enabled: fresh.ollama.enabled,
      baseUrl: fresh.ollama.baseUrl,
      model: fresh.ollama.model,
      models: listed.models,
      installed: listed.models.includes(fresh.ollama.model)
    };
  }

  return {
    getSettings: getPublicSettings,
    updateSettings,
    sendAlert,
    checkTransitions,
    pollUpdates,
    processTelegramMessage,
    probeOllama
  };
}

module.exports = { alertForStatus, alertForStep, crewOfProject,
  escapeHtml,
  formatProgressBar,
  sanitizeText,
  sanitizeGoalTitle,
  formatDuration,
  formatAlertMessage,
  formatTelegramAlertMessage,
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
