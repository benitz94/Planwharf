'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const PLAN_IDS = ['ChatGPT', 'Grok', 'Gemini'];
const GROK_BILLING = 'https://cli-chat-proxy.grok.com/v1/billing?format=credits';
const CHATGPT_USAGE = 'https://chatgpt.com/backend-api/wham/usage';
const CHATGPT_REFRESH = 'https://auth.openai.com/oauth/token';
const CODEX_CLIENT_ID = 'app_EMoamEEZ73f0CfXlXpSwebNA';
const GROK_REFRESH = 'https://auth.x.ai/oauth/token';

function clipPct(value) {
  const n = Number(value);
  if (!Number.isFinite(n)) return 0;
  return Math.max(0, Math.min(100, Math.round(n)));
}

function unreadPlan(id, reason) {
  const provider = id === 'ChatGPT' ? 'codex' : id === 'Grok' ? 'grok' : 'antigravity';
  return require('./plan-status.cjs').withStatus(provider, {
    id,
    label: id,
    readable: false,
    exhausted: false,
    pct: null,
    leftPct: null,
    source: null,
    reason: reason || 'Not readable',
    windows: []
  });
}

function windowOf(raw, name) {
  if (!raw || typeof raw !== 'object') return null;
  const value = raw.used_percent != null ? raw.used_percent : raw.usagePercent;
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 100) return null;
  const pct = clipPct(value);
  const resetAt = Number.isFinite(raw.reset_at)
    ? new Date(raw.reset_at * 1000).toISOString()
    : (typeof raw.end === 'string' ? raw.end : (typeof raw.resetAt === 'string' ? raw.resetAt : null));
  return {
    name,
    pct,
    leftPct: 100 - pct,
    resetAt,
    seconds: Number.isFinite(raw.limit_window_seconds) ? raw.limit_window_seconds : null
  };
}

function parseGrokCredits(body) {
  const config = body && body.config && typeof body.config === 'object' ? body.config : (body || {});
  if (typeof config.creditUsagePercent !== 'number' || !Number.isFinite(config.creditUsagePercent) || config.creditUsagePercent < 0 || config.creditUsagePercent > 100) return unreadPlan('Grok', 'No numeric usage in Grok billing response.');
  const pct = clipPct(config.creditUsagePercent);
  const period = config.currentPeriod && typeof config.currentPeriod === 'object' ? config.currentPeriod : {};
  const end = typeof period.end === 'string' ? period.end : (typeof config.billingPeriodEnd === 'string' ? config.billingPeriodEnd : null);
  const start = typeof period.start === 'string' ? period.start : (typeof config.billingPeriodStart === 'string' ? config.billingPeriodStart : null);
  const products = Array.isArray(config.productUsage)
    ? config.productUsage.map(item => ({
      name: item && item.product ? String(item.product) : 'product',
      pct: clipPct(item && item.usagePercent)
    }))
    : [];
  const weekly = { name: 'weekly', pct, leftPct: 100 - pct, resetAt: end, start };
  return {
    id: 'Grok',
    label: 'Grok',
    readable: true,
    exhausted: pct >= 100,
    pct,
    leftPct: 100 - pct,
    source: 'grok',
    planStatus: 'active',
    reason: null,
    windows: [weekly],
    products
  };
}

function parseChatGptUsage(body) {
  const rl = body && body.rate_limit && typeof body.rate_limit === 'object' ? body.rate_limit : {};
  const five = windowOf(rl.primary_window, '5-hour');
  const week = windowOf(rl.secondary_window, 'weekly');
  const windows = [five, week].filter(Boolean);
  const tight = windows.reduce((best, item) => (!best || item.pct > best.pct ? item : best), null);
  if (!tight) return unreadPlan('ChatGPT', 'No numeric usage windows in ChatGPT response.');
  const pct = tight.pct;
  const exhausted = rl.limit_reached === true || rl.allowed === false || windows.some(item => item.pct >= 100);
  return {
    id: 'ChatGPT',
    label: 'ChatGPT',
    readable: true,
    exhausted,
    pct,
    leftPct: 100 - pct,
    source: 'chatgpt',
    planStatus: 'active',
    reason: null,
    planType: body && body.plan_type ? String(body.plan_type) : null,
    windows
  };
}

function planExhausted(plans, name) {
  const row = (Array.isArray(plans) ? plans : []).find(item => item && item.id === name);
  return Boolean(row && (require('./plan-status.cjs').blockedStatus(row) || row.readable && (row.exhausted || row.leftPct === 0 || row.pct === 100)));
}

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch (_) {
    return null;
  }
}

function grokAuth(home) {
  const grokHome = (home === os.homedir() && process.env.GROK_HOME) || path.join(home, '.grok');
  const raw = readJson(path.join(grokHome, 'auth.json'));
  if (!raw || typeof raw !== 'object') return null;
  const entry = require('./provider-accounts.cjs').grokEntry(raw);
  if (!entry) return null;
  const ver = readJson(path.join(grokHome, 'version.json')) || {};
  return {
    key: typeof entry.key === 'string' ? entry.key : '',
    refresh: typeof entry.refresh_token === 'string' ? entry.refresh_token : '',
    clientId: typeof entry.oidc_client_id === 'string' ? entry.oidc_client_id : '',
    expiresAt: typeof entry.expires_at === 'string' ? entry.expires_at : '',
    version: typeof ver.version === 'string' ? ver.version : '1.0.50'
  };
}

function codexAuth(home, authHome) {
  const raw = readJson(path.join(authHome || (home === os.homedir() && process.env.CODEX_HOME) || path.join(home, '.codex'), 'auth.json'));
  if (!raw || typeof raw !== 'object') return null;
  const tokens = raw.tokens && typeof raw.tokens === 'object' ? raw.tokens : {};
  return {
    access: typeof tokens.access_token === 'string' ? tokens.access_token : '',
    refresh: typeof tokens.refresh_token === 'string' ? tokens.refresh_token : '',
    accountId: typeof tokens.account_id === 'string' ? tokens.account_id : ''
  };
}

async function httpGet(fetchImpl, url, headers, timeoutMs) {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), timeoutMs || 8000);
  try {
    const res = await fetchImpl(url, { headers, signal: ac.signal });
    const text = await res.text();
    let json = null;
    try { json = JSON.parse(text); } catch (_) {}
    return { status: res.status, json };
  } finally {
    clearTimeout(timer);
  }
}

async function httpForm(fetchImpl, url, body, timeoutMs) {
  const ac = new AbortController();
  const timer = setTimeout(() => ac.abort(), timeoutMs || 8000);
  try {
    const res = await fetchImpl(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body,
      signal: ac.signal
    });
    const text = await res.text();
    let json = null;
    try { json = JSON.parse(text); } catch (_) {}
    return { status: res.status, json };
  } finally {
    clearTimeout(timer);
  }
}

function form(pairs) {
  return Object.keys(pairs).map(key => `${encodeURIComponent(key)}=${encodeURIComponent(pairs[key] || '')}`).join('&');
}

async function grokAccess(auth, fetchImpl, now) {
  if (!auth) return '';
  const exp = auth.expiresAt ? Date.parse(auth.expiresAt) : 0;
  const soon = Number.isFinite(exp) && exp - now < 5 * 60 * 1000;
  if (auth.key && !soon) return auth.key;
  if (!auth.refresh || !auth.clientId) return auth.key || '';
  try {
    const res = await httpForm(fetchImpl, GROK_REFRESH, form({
      grant_type: 'refresh_token',
      refresh_token: auth.refresh,
      client_id: auth.clientId
    }));
    const token = res.json && (res.json.access_token || res.json.key);
    if (res.status === 200 && typeof token === 'string' && token) return token;
  } catch (_) {}
  return auth.key || '';
}

async function chatgptAccess(auth, fetchImpl) {
  if (!auth) return '';
  if (auth.access) return auth.access;
  if (!auth.refresh) return '';
  try {
    const res = await httpForm(fetchImpl, CHATGPT_REFRESH, form({
      grant_type: 'refresh_token',
      refresh_token: auth.refresh,
      client_id: CODEX_CLIENT_ID
    }));
    const token = res.json && res.json.access_token;
    if (res.status === 200 && typeof token === 'string' && token) return token;
  } catch (_) {}
  return '';
}

async function readGrok(home, fetchImpl, now) {
  const auth = grokAuth(home);
  if (!auth || (!auth.key && !auth.refresh)) return unreadPlan('Grok', 'Sign in with grok CLI to read weekly usage.');
  try {
    const token = await grokAccess(auth, fetchImpl, now);
    if (!token) return unreadPlan('Grok', 'Grok login expired.');
    const res = await httpGet(fetchImpl, GROK_BILLING, {
      Authorization: 'Bearer ' + token,
      'x-grok-client-surface': 'grok-build',
      'x-grok-client-version': auth.version
    });
    if (res.status === 200 && res.json) return parseGrokCredits(res.json);
    const planError = require('./plan-status.cjs').providerError('grok', res.json, 'Grok');
    if (planError) return { ...unreadPlan('Grok', planError.reason), planStatus: planError.planStatus };
    return unreadPlan('Grok', res.status === 401 ? 'Grok login expired.' : `Grok usage HTTP ${res.status}.`);
  } catch (err) {
    return unreadPlan('Grok', err && err.name === 'AbortError' ? 'Grok quota check timed out. CLI model access is checked separately.' : 'Grok quota service could not be reached. This does not mean the Grok CLI is unavailable.');
  }
}

async function readChatGpt(home, fetchImpl, authHome) {
  const auth = codexAuth(home, authHome);
  if (!auth || (!auth.access && !auth.refresh)) return unreadPlan('ChatGPT', 'Sign in with Codex / ChatGPT to read 5-hour and weekly usage.');
  try {
    let token = await chatgptAccess(auth, fetchImpl);
    if (!token) return unreadPlan('ChatGPT', 'ChatGPT login expired.');
    let res = await httpGet(fetchImpl, CHATGPT_USAGE, {
      Authorization: 'Bearer ' + token,
      ...(auth.accountId ? { 'ChatGPT-Account-ID': auth.accountId } : {}),
      'User-Agent': 'Planwharf'
    });
    if (res.status === 401 && auth.refresh) {
      auth.access = '';
      token = await chatgptAccess({ ...auth, access: '' }, fetchImpl);
      if (token) {
        res = await httpGet(fetchImpl, CHATGPT_USAGE, {
          Authorization: 'Bearer ' + token,
          ...(auth.accountId ? { 'ChatGPT-Account-ID': auth.accountId } : {}),
          'User-Agent': 'Planwharf'
        });
      }
    }
    if (res.status === 200 && res.json) return parseChatGptUsage(res.json);
    const planError = require('./plan-status.cjs').providerError('codex', res.json, 'ChatGPT');
    if (planError) return { ...unreadPlan('ChatGPT', planError.reason), planStatus: planError.planStatus };
    return unreadPlan('ChatGPT', res.status === 401 ? 'ChatGPT login expired.' : `ChatGPT usage HTTP ${res.status}.`);
  } catch (err) {
    return unreadPlan('ChatGPT', err && err.name === 'AbortError' ? 'ChatGPT usage timed out.' : 'ChatGPT usage unreachable.');
  }
}


async function fetchPlanUsage(options) {
  const opts = options && typeof options === 'object' ? options : {};
  const home = typeof opts.home === 'string' && opts.home.trim() ? opts.home : os.homedir();
  const fetchImpl = typeof opts.fetchImpl === 'function' ? opts.fetchImpl : fetch;
  const now = Number.isFinite(opts.now) ? opts.now : Date.now();
  const [chatgpt, grok, gemini] = await Promise.all([
    readChatGpt(home, fetchImpl),
    readGrok(home, fetchImpl, now),
    require('./antigravity.cjs').readAntigravity(opts.antigravity || {})
  ]);
  // Antigravity also reports the quota of the Claude and GPT models it runs, as a pool of its own.
  return [chatgpt, grok, gemini, ...(Array.isArray(gemini && gemini.extraPlans) ? gemini.extraPlans : [])];
}

function emptyPlans() {
  return PLAN_IDS.map(id => unreadPlan(id, 'Usage not loaded yet.'));
}

module.exports = {
  PLAN_IDS,
  parseGrokCredits,
  parseChatGptUsage,
  planExhausted,
  unreadPlan,
  fetchPlanUsage,
  emptyPlans,
  windowOf,
  readChatGpt,
  readGrok
};
