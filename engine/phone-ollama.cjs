'use strict';

const fs = require('node:fs');
const path = require('node:path');

const DEFAULT_OLLAMA_URL = 'http://127.0.0.1:11434';
const DEFAULT_OLLAMA_MODEL = 'qwen2.5:3b';
const MAX_MEMORY_MESSAGES = 8;
const MEMORY_FILE = 'phone-chat-memory.json';

function defaultOllamaSettings() {
  return { enabled: true, baseUrl: DEFAULT_OLLAMA_URL, model: DEFAULT_OLLAMA_MODEL };
}

function loopbackUrl(value) {
  try {
    const url = new URL(String(value || '').trim());
    const host = url.hostname.replace(/^\[|\]$/g, '');
    if (url.protocol !== 'http:') return null;
    if (host !== '127.0.0.1' && host !== 'localhost' && host !== '::1') return null;
    if (url.username || url.password || url.search || url.hash) return null;
    return `${url.protocol}//${url.host}`;
  } catch {
    return null;
  }
}

function normalizeOllamaSettings(raw) {
  const base = defaultOllamaSettings();
  if (!raw || typeof raw !== 'object') return base;
  const model = typeof raw.model === 'string' ? raw.model.trim() : '';
  return {
    enabled: raw.enabled === undefined ? base.enabled : Boolean(raw.enabled),
    baseUrl: loopbackUrl(raw.baseUrl) || base.baseUrl,
    model: /^[\w.:@+/-]{1,120}$/.test(model) ? model : base.model
  };
}

function escapeHtml(text) {
  if (text == null) return '';
  return String(text)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function withCaptain(body) {
  const text = String(body || '').trim();
  if (!text) return '⚓ Captain';
  if (text.startsWith('⚓ Captain')) return text;
  return `⚓ Captain\n\n${text}`;
}

function isClearMemory(text) {
  const value = String(text || '').trim().toLowerCase().replace(/^(\/(?:clear|forget))(?:@[a-z0-9_]+)?$/i, '$1');
  return value === '/clear' || value === '/forget' || value === 'clear memory' || value === 'forget this chat';
}

function isBareConfirmation(text) {
  return /^(yes|si|sì)$/i.test(String(text || '').trim());
}

function createChatMemory({ maxMessages = MAX_MEMORY_MESSAGES } = {}) {
  const limit = Number.isInteger(maxMessages) && maxMessages > 0 ? Math.min(maxMessages, 20) : MAX_MEMORY_MESSAGES;
  const chats = new Map();
  return {
    remember(chatId, role, text) {
      const id = String(chatId || '');
      const clean = String(text || '').replace(/\s+/g, ' ').trim().slice(0, 500);
      if (!id || !clean || (role !== 'user' && role !== 'assistant')) return;
      const list = chats.get(id) || [];
      list.push({ role, text: clean });
      chats.set(id, list.slice(-limit));
    },
    history(chatId) {
      return (chats.get(String(chatId || '')) || []).map(item => ({ ...item }));
    },
    clear(chatId) {
      chats.delete(String(chatId || ''));
    },
    snapshot() {
      return [...chats.entries()].map(([chatId, items]) => ({
        chatId,
        items: items.map(item => ({ role: item.role, text: item.text }))
      }));
    }
  };
}

function loadChatMemory(storeDir, options) {
  const memory = createChatMemory(options);
  try {
    const raw = JSON.parse(fs.readFileSync(path.join(storeDir, MEMORY_FILE), 'utf8'));
    const chats = Array.isArray(raw) ? raw : [];
    for (const chat of chats) {
      for (const item of chat?.items || []) memory.remember(chat.chatId, item.role, item.text);
    }
  } catch {
    /* A missing or broken memory file starts empty. Commands keep working. */
  }
  return memory;
}

function saveChatMemory(storeDir, memory) {
  fs.mkdirSync(storeDir, { recursive: true });
  const file = path.join(storeDir, MEMORY_FILE);
  const nonce = `${Date.now()}.${Math.random().toString(36).slice(2)}`;
  const next = `${file}.${process.pid}.${nonce}.tmp`;
  fs.writeFileSync(next, JSON.stringify(memory.snapshot(), null, 2) + '\n');
  fs.renameSync(next, file);
}

function projectFacts(projects = [], sanitize) {
  const clean = typeof sanitize === 'function' ? sanitize : (value => String(value || ''));
  return (Array.isArray(projects) ? projects : []).slice(0, 12).map(project => {
    const goalSource = String(project?.goal || project?.jobTitle || '').split(/\r?\n/)[0];
    const minutes = Number(project?.timeLeft?.minutes);
    return {
      name: clean(project?.name || project?.id || 'Project').slice(0, 80) || 'Project',
      goal: clean(goalSource).slice(0, 80),
      status: clean(project?.status || 'idle').slice(0, 40) || 'idle',
      step: project?.step?.step && project?.step?.total ? `${Number(project.step.step)}/${Number(project.step.total)}` : '',
      minutes: Number.isFinite(minutes) && minutes >= 0 ? Math.round(minutes) : null,
      waiting: project?.status === 'waiting_on_you'
    };
  });
}

function buildReplyMessages({ question, projects, history, sanitize, model } = {}) {
  const lines = projectFacts(projects, sanitize).map(item => {
    const bits = [`${item.name}: ${item.status}`];
    if (item.goal) bits.push(`goal ${item.goal}`);
    if (item.step) bits.push(`step ${item.step}`);
    if (item.minutes != null) bits.push(`about ${item.minutes}m left`);
    if (item.waiting) bits.push('waiting on the captain');
    return bits.join('; ');
  });
  const system = [
    "You are Captain, Planwharf's harbor companion.",
    "Reply in the user's language, in at most 4 short sentences.",
    'Speak as a Captain-facing harbor companion.',
    'Use only the project facts below. Do not invent files, paths, commands, or actions.',
    'You cannot stop, resume, or change work. Tell the captain to use /status, /stop, or /resume for that.',
    'If the facts do not answer the question, say so.',
    `The local model answering this message is ${String(model || DEFAULT_OLLAMA_MODEL)}. Do not claim another name.`,
    lines.length ? `Facts:\n${lines.join('\n')}` : 'Facts: no projects.'
  ].join('\n').slice(0, 2400);
  const prior = (Array.isArray(history) ? history : []).slice(-MAX_MEMORY_MESSAGES).map(item => ({
    role: item.role === 'assistant' ? 'assistant' : 'user',
    content: String(item.text || '').slice(0, 500)
  }));
  return [
    { role: 'system', content: system },
    ...prior,
    { role: 'user', content: String(question || '').slice(0, 500) }
  ];
}

function unavailableReply() {
  return [
    '⚓ Captain',
    '',
    '<b>Harbor Companion Offline</b>',
    '⚠️ The local model is not answering right now. Deck commands still work:',
    '• /status',
    '• /stop &lt;project&gt;',
    '• /resume &lt;project&gt;'
  ].join('\n');
}

async function askOllamaOnce({ baseUrl, model, messages, fetchImpl, timeoutMs }) {
  const settings = normalizeOllamaSettings({ baseUrl, model, enabled: true });
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), Math.max(1, Number(timeoutMs) || 45000));
  try {
    const response = await fetchImpl(`${settings.baseUrl}/api/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model: settings.model,
        messages: Array.isArray(messages) ? messages : [],
        stream: false,
        keep_alive: '5m',
        options: { temperature: 0.2, num_predict: 180 }
      }),
      signal: controller.signal
    });
    const data = typeof response?.json === 'function' ? await response.json().catch(() => null) : null;
    const text = data?.message?.content || '';
    if (!response?.ok || !String(text).trim()) {
      return { ok: false, model: settings.model, text: unavailableReply(), reason: response?.ok ? 'empty' : `HTTP ${response?.status || 0}` };
    }
    return { ok: true, model: settings.model, text: withCaptain(escapeHtml(String(text).trim())) };
  } catch (error) {
    return { ok: false, model: settings.model, text: unavailableReply(), reason: error?.name === 'AbortError' ? 'timeout' : 'unreachable' };
  } finally {
    clearTimeout(timer);
  }
}

async function askOllama(options = {}) {
  const fetchImpl = options.fetchImpl || globalThis.fetch;
  const first = await askOllamaOnce({ ...options, fetchImpl });
  if (first.ok) return first;
  return askOllamaOnce({ ...options, fetchImpl });
}

async function listInstalledModels({ baseUrl, fetchImpl = globalThis.fetch, timeoutMs = 4000 } = {}) {
  const settings = normalizeOllamaSettings({ baseUrl, enabled: true });
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), Math.max(1, Number(timeoutMs) || 4000));
  try {
    const response = await fetchImpl(`${settings.baseUrl}/api/tags`, { signal: controller.signal });
    const data = typeof response?.json === 'function' ? await response.json().catch(() => null) : null;
    if (!response?.ok || !Array.isArray(data?.models)) return { ok: false, models: [] };
    return { ok: true, models: data.models.map(item => String(item?.name || '')).filter(Boolean) };
  } catch {
    return { ok: false, models: [] };
  } finally {
    clearTimeout(timer);
  }
}

module.exports = {
  DEFAULT_OLLAMA_URL,
  DEFAULT_OLLAMA_MODEL,
  MEMORY_FILE,
  defaultOllamaSettings,
  normalizeOllamaSettings,
  escapeHtml,
  withCaptain,
  isClearMemory,
  isBareConfirmation,
  createChatMemory,
  loadChatMemory,
  saveChatMemory,
  projectFacts,
  buildReplyMessages,
  unavailableReply,
  askOllama,
  listInstalledModels
};
