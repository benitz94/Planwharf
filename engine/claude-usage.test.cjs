'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { parseClaudeUsage, readClaudeAuth, createClaudeUsageReader } = require('./claude-usage.cjs');
const { loadCatalog, providerViews } = require('./provider-catalog.cjs');
const quotaUI = require('../quota.js');
const usage = { five_hour: { utilization: 18, resets_at: '2030-01-01T05:00:00Z' }, seven_day: { utilization: 16, resets_at: '2030-01-07T00:00:00Z' } };
const now = () => Date.parse('2030-01-01T00:00:00Z');
const auth = { token: 'fixture-private-token', expiresAt: now() + 86400000 };
const json = (body, options) => new Response(JSON.stringify(body), options);

test('actual reader feeds readable quota through provider views and remaining UI', async () => {
  let calls = 0;
  const reader = createClaudeUsageReader({ now, readAuth: async () => auth, fetchImpl: async (url, options) => {
    calls++; assert.equal(url, 'https://api.anthropic.com/api/oauth/usage');
    assert.equal(options.redirect, 'error'); assert.equal(options.headers.Authorization, 'Bearer ' + auth.token);
    assert.ok(options.signal); return json(usage);
  } });
  const quota = await reader(); assert.equal(quota.readable, true); assert.equal(quota.leftPct, 82);
  assert.deepEqual(quota.windows.map(w => w.leftPct), [82, 84]);
  assert.equal(quotaUI.describe(quota).text, '82% REMAINING');
  const catalog = loadCatalog({ schema: 3, providers: [{ id: 'claude', adapter: 'claude', quotaPoolId: 'Claude', models: [{ id: 'fixture', cli: 'claude-fixture', available: true }] }] });
  const view = providerViews(catalog.providers, catalog.models, [quota])[0];
  assert.equal(view.availability, 'AVAILABLE'); assert.equal(view.remainingPercent, 82);
  assert.doesNotMatch(JSON.stringify([quota, view]), /fixture-private-token/);
  quota.leftPct = 0; assert.equal((await reader()).leftPct, 82, 'Callers cannot mutate cached percentages');
  assert.equal(calls, 1);
});

test('global windows determine shared remaining quota; scoped windows cannot exhaust siblings', () => {
  assert.equal(parseClaudeUsage({ ...usage, seven_day_opus: { utilization: 100 } }).exhausted, false);
  assert.equal(parseClaudeUsage({ five_hour: { utilization: 0 }, seven_day: { utilization: 100 } }).exhausted, true);
  assert.equal(parseClaudeUsage({ five_hour: null, seven_day: { utilization: 0 } }).leftPct, 100);
  assert.equal(parseClaudeUsage({ five_hour: { utilization: 99.9 } }).exhausted, false);
  for (const value of ['18', null, -1, 101, NaN, Infinity]) assert.equal(parseClaudeUsage({ five_hour: { utilization: value }, seven_day: usage.seven_day }).readable, false);
  for (const body of [null, {}, { five_hour: {} }, { five_hour: { utilization: 18, resets_at: 'invalid' } }]) assert.equal(parseClaudeUsage(body).readable, false);
});

test('file credentials respect CLI auth method, config directory and expiry without rewriting files', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-claude-auth-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, '.credentials.json');
  fs.writeFileSync(file, JSON.stringify({ claudeAiOauth: { accessToken: auth.token, refreshToken: 'unused-refresh-token', expiresAt: auth.expiresAt } }), { mode: 0o600 });
  const before = fs.readFileSync(file);
  let method = 'claude.ai';
  const options = { env: {}, now, command: 'fixture', run: (_command, args, opts, done) => {
    assert.deepEqual(args, ['auth', 'status']); assert.ok(opts.timeout > 0);
    done(null, JSON.stringify({ loggedIn: true, authMethod: method, configDirectory: dir }));
  } };
  assert.deepEqual(await readClaudeAuth(options), auth);
  assert.equal(await readClaudeAuth({ ...options, now: () => auth.expiresAt + 1 }), null);
  assert.equal(await readClaudeAuth({ ...options, env: { ANTHROPIC_API_KEY: 'fixture-api-key' } }), null);
  assert.equal(await readClaudeAuth({ ...options, env: { CLAUDE_CONFIG_DIR: path.join(dir, 'missing') } }), null);
  method = 'api_key'; assert.equal(await readClaudeAuth(options), null);
  method = 'oauth_token'; assert.equal(await readClaudeAuth(options), null);
  assert.deepEqual(await readClaudeAuth({ ...options, env: { CLAUDE_CODE_OAUTH_TOKEN: auth.token } }), { token: auth.token, expiresAt: null });
  assert.equal(await readClaudeAuth({ ...options, env: { CLAUDE_CODE_OAUTH_TOKEN: 'bad\ntoken' } }), null);
  assert.ok(fs.readFileSync(file).equals(before));
});

test('missing/expired authentication never issues a network request', async () => {
  for (const supplied of [null, { token: '' }, { ...auth, expiresAt: now() - 1 }]) {
    const read = createClaudeUsageReader({ now, readAuth: async () => supplied, fetchImpl: () => { throw Error('Must not fetch'); } });
    assert.equal((await read()).readable, false);
  }
});

test('failed responses clear earlier success and never disclose raw errors or credentials', async () => {
  let time = now(), response = () => json(usage);
  const read = createClaudeUsageReader({ now: () => time, ttlMs: 10, readAuth: async () => auth, fetchImpl: async () => response() });
  assert.equal((await read()).readable, true);
  for (const failure of [() => json({ private: auth.token }, { status: 401 }), () => json({ private: auth.token }, { status: 403 }), () => json({ future_protocol: true }), () => new Response('bad JSON ' + auth.token), () => { throw Error(auth.token); }, () => new Response('x'.repeat(65537))]) {
    time += 11; response = failure;
    const quota = await read(); assert.equal(quota.readable, false); assert.equal(quota.leftPct, null);
    assert.doesNotMatch(JSON.stringify(quota), /fixture-private-token|bad JSON/);
  }
});

test('rate limiting honors retry-after and coalesces repeated requests', async () => {
  let time = now(), calls = 0;
  const read = createClaudeUsageReader({ now: () => time, ttlMs: 1000, readAuth: async () => auth,
    fetchImpl: async () => { calls++; return json({}, { status: 429, headers: { 'retry-after': '120' } }); } });
  const values = await Promise.all([read(), read(), read()]);
  assert.ok(values.every(q => !q.readable && /rate limited/.test(q.reason))); assert.equal(calls, 1);
  time += 119999; await read(); assert.equal(calls, 1);
  time++; await read(); assert.equal(calls, 2);
});

test('cache is scoped to current credentials and ends at the earliest reset', async () => {
  let time = now(), current = auth, calls = 0;
  const read = createClaudeUsageReader({ now: () => time, readAuth: async () => current,
    fetchImpl: async () => { calls++; return json({ five_hour: { utilization: 18, resets_at: new Date(time + 1000).toISOString() } }); } });
  await read(); await read(); assert.equal(calls, 1);
  time += 1000; await read(); assert.equal(calls, 2);
  current = { ...auth, token: 'different-account-token' }; await read(); assert.equal(calls, 3);
  current = null; assert.equal((await read()).readable, false);
});

test('account changes while a response is pending cannot publish old quota', async () => {
  let current = auth, release, started;
  const entered = new Promise(resolve => { started = resolve; });
  const read = createClaudeUsageReader({ now, readAuth: async () => current, fetchImpl: async () => {
    started(); await new Promise(resolve => { release = resolve; }); return json(usage);
  } });
  const request = read(); await entered; current = { ...auth, token: 'new-account-token' }; release();
  const quota = await request; assert.equal(quota.readable, false); assert.match(quota.reason, /login changed/);
});
