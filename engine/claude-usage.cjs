'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { execFile } = require('node:child_process');
const { createHash } = require('node:crypto');
const { unreadPlan } = require('./planUsage.js');
const USAGE_URL = 'https://api.anthropic.com/api/oauth/usage';
const MAX_BYTES = 65536;
const unread = reason => unreadPlan('Claude', reason);

function parseClaudeUsage(body) {
  const windows = [];
  for (const [key, name] of [['five_hour', '5-hour'], ['seven_day', 'weekly']]) {
    const row = body?.[key];
    if (row == null) continue;
    if (typeof row.utilization !== 'number' || !Number.isFinite(row.utilization) || row.utilization < 0 || row.utilization > 100
      || row.resets_at != null && (typeof row.resets_at !== 'string' || !Number.isFinite(Date.parse(row.resets_at)))) return unread('Claude usage response contains an invalid quota window.');
    windows.push({ name, pct: row.utilization, leftPct: 100 - row.utilization, resetAt: row.resets_at || null });
  }
  if (!windows.length) return unread('Claude usage response contains no numeric subscription quota windows.');
  const tight = windows.reduce((a, b) => a.leftPct <= b.leftPct ? a : b);
  return { id: 'Claude', label: 'Claude', readable: true, exhausted: windows.some(w => w.pct === 100), pct: tight.pct,
    leftPct: tight.leftPct, resetAt: tight.resetAt, source: 'Claude subscription OAuth usage', reason: null, windows };
}

async function readClaudeAuth({ env = process.env, home = os.homedir(), command = require('./providers.cjs').executable('claude'), run = execFile, now = Date.now } = {}) {
  if (!command) return null;
  const status = await new Promise(resolve => run(command, ['auth', 'status'], { windowsHide: true, timeout: 10000, maxBuffer: MAX_BYTES }, (error, stdout) => {
    try { resolve(error ? null : JSON.parse(stdout)); } catch { resolve(null); }
  }));
  // Do not attribute a stored subscription to a CLI currently using an API/gateway.
  if (!status?.loggedIn || !['claude.ai', 'oauth_token'].includes(status.authMethod)
    || ['ANTHROPIC_API_KEY', 'ANTHROPIC_AUTH_TOKEN', 'CLAUDE_CODE_USE_BEDROCK', 'CLAUDE_CODE_USE_VERTEX', 'CLAUDE_CODE_USE_FOUNDRY', 'ANTHROPIC_BASE_URL'].some(key => env[key])) return null;
  if (status.authMethod === 'oauth_token') {
    const token = env.CLAUDE_CODE_OAUTH_TOKEN;
    return typeof token === 'string' && token.trim() && !/[\r\n\0]/.test(token) ? { token, expiresAt: null } : null;
  }
  try {
    const file = path.join(env.CLAUDE_CONFIG_DIR || status.configDirectory || path.join(home, '.claude'), '.credentials.json');
    if (fs.statSync(file).size > MAX_BYTES) return null;
    const auth = JSON.parse(fs.readFileSync(file, 'utf8')).claudeAiOauth;
    if (typeof auth?.accessToken !== 'string' || !auth.accessToken.trim() || /[\r\n\0]/.test(auth.accessToken)
      || !Number.isFinite(auth.expiresAt) || auth.expiresAt <= now()) return null;
    return { token: auth.accessToken, expiresAt: auth.expiresAt };
  } catch { return null; }
}

function createClaudeUsageReader({ readAuth = readClaudeAuth, fetchImpl = fetch, now = Date.now, ttlMs = 300000 } = {}) {
  let cached, pending;
  const scope = auth => auth && typeof auth.token === 'string' && auth.token.trim() ? createHash('sha256').update(auth.token).digest('hex') : null;
  return async function read() {
    let auth;
    try { auth = await readAuth(); } catch { auth = null; }
    const key = scope(auth);
    if (!key || auth.expiresAt != null && auth.expiresAt <= now()) { cached = null; return unread('Claude subscription credentials are unavailable or expired. Open Claude Code to refresh the login, then refresh usage. API and gateway logins do not provide this subscription quota.'); }
    if (cached?.key === key && now() < cached.until) return structuredClone(cached.result);
    if (pending?.key === key) return structuredClone(await pending.promise);
    const request = (async () => {
      let result, retryMs = ttlMs;
      try {
        const response = await fetchImpl(USAGE_URL, { headers: { Authorization: 'Bearer ' + auth.token, 'anthropic-beta': 'oauth-2025-04-20' },
          redirect: 'error', signal: AbortSignal.timeout(10000) });
        if (response.status !== 200) {
          const seconds = Number(response.headers.get('retry-after'));
          if (response.status === 429 && Number.isFinite(seconds) && seconds > 0) retryMs = Math.max(ttlMs, Math.min(seconds * 1000, 3600000));
          await response.body?.cancel();
          result = unread(response.status === 401 ? 'Claude subscription login expired. Open Claude Code to refresh it.'
            : response.status === 429 ? 'Claude usage endpoint is rate limited. A later refresh will retry; previous quota is not treated as current.'
            : 'Claude usage HTTP ' + response.status + '.');
        } else {
          const chunks = []; let size = 0;
          for await (const chunk of response.body) {
            size += chunk.length;
            if (size > MAX_BYTES) throw Error('Oversized usage response');
            chunks.push(chunk);
          }
          result = parseClaudeUsage(JSON.parse(Buffer.concat(chunks).toString('utf8')));
        }
      } catch { result = unread('Claude subscription usage could not be read: connection, timeout or response format error.'); }
      // A login switch during a request cannot publish the previous account's quota.
      let current; try { current = await readAuth(); } catch { current = null; }
      if (scope(current) !== key || current?.expiresAt != null && current.expiresAt <= now()) return unread('Claude login changed or expired during the usage check. Refresh usage for the current login.');
      const checkedAt = new Date(now()).toISOString();
      const resetTimes = (result.windows || []).map(w => Date.parse(w.resetAt)).filter(time => Number.isFinite(time) && time > now());
      const until = Math.min(now() + retryMs, current?.expiresAt || Infinity, ...resetTimes);
      result = { ...result, checkedAt };
      cached = { key, result, until };
      return result;
    })();
    pending = { key, promise: request };
    try { return structuredClone(await request); } finally { if (pending?.promise === request) pending = null; }
  };
}
const readClaudeUsage = createClaudeUsageReader();
module.exports = { parseClaudeUsage, readClaudeAuth, createClaudeUsageReader, readClaudeUsage };
