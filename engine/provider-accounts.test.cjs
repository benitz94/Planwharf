'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { codexAccount, grokAccount, accountLabel } = require('./provider-accounts.cjs');
const { createServer } = require('../server.cjs');
const jwt = value => 'fixture.' + Buffer.from(JSON.stringify(value)).toString('base64url') + '.fixture';

test('provider metadata distinguishes business from paid personal accounts without exposing credentials', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-account-metadata-'));
  try {
    const write = value => fs.writeFileSync(path.join(home, 'auth.json'), JSON.stringify(value));
    write({ tokens: { account_id: 'workspace', id_token: jwt({ sub: 'fixture-user', 'https://api.openai.com/auth': { chatgpt_account_id: 'workspace', chatgpt_plan_type: 'enterprise' } }) } });
    const business = codexAccount({ id: 'codex' }, { home });
    assert.equal(business.type, 'business'); assert.equal(accountLabel('GPT fixture', business), 'GPT fixture Business');
    write({ tokens: { account_id: 'workspace', id_token: jwt({ sub: 'fixture-user', 'https://api.openai.com/auth': { chatgpt_account_id: 'workspace', chatgpt_plan_type: 'pro' } }) } });
    const normal = codexAccount({ id: 'codex' }, { home });
    assert.equal(normal.type, 'normal'); assert.equal(normal.key, business.key); assert.equal(accountLabel('GPT fixture', normal), 'GPT fixture');
    write({ scope: { key: 'fixture-token', user_id: 'fixture-user', principal_type: 'Team', team_id: 'fixture-team' } });
    const team = grokAccount({ id: 'grok' }, { home });
    assert.equal(team.type, 'business');
    write({ scope: { key: 'rotated-fixture-token', user_id: 'fixture-user', principal_type: 'Team', team_id: 'fixture-team' } });
    assert.equal(grokAccount({ id: 'grok' }, { home }).key, team.key, 'Token rotation must not change account scope');
    write({ scope: { key: 'fixture-token', user_id: 'fixture-user', principal_type: 'User', email: 'someone@company.example' } });
    const personal = grokAccount({ id: 'grok' }, { home });
    assert.equal(personal.type, 'normal'); assert.notEqual(personal.key, team.key);
    assert.doesNotMatch(JSON.stringify([business, normal, team, personal]), /fixture-user|fixture-token|company\.example|id_token/);
    write({ scope: { key: 'opaque-token', email: 'someone@company.example' } });
    assert.equal(grokAccount({ id: 'grok' }, { home }).type, 'unread');
    const previousHome = process.env.CODEX_HOME;
    process.env.CODEX_HOME = home;
    try {
      assert.throws(() => require('./provider-runtime.cjs').launchSpec({ id: 'fixture', provider: 'codex', cli: 'gpt-fixture', accountKey: 'previous-account' }, home, 'Fixture'), /account changed before launch/i);
    } finally { if (previousHome === undefined) delete process.env.CODEX_HOME; else process.env.CODEX_HOME = previousHome; }
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});

test('account switching isolates roles, invalidates quota and stale access, and survives restart', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-account-switch-'));
  const planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  let account = { key: 'fixture-personal', type: 'normal' }, catalogOk = true, discoveryCalls = 0, aiCalls = 0, server, origin;
  async function start() {
    server = createServer({ planFile, attemptsDir, skillsEnabled: false, monitorIntervalMs: 60000,
      models: [{ id: 'grok-fixture', label: 'Grok fixture', provider: 'grok', cli: 'grok-fixture', enabled: true, available: true }],
      readAccount: () => account, discoverModels: async () => { discoveryCalls++; return catalogOk ? { ok: true, ids: ['grok-fixture'] } : { ok: false, reason: 'Fixture listing unavailable' }; },
      fetchPlans: async () => [{ id: 'Grok', readable: true, leftPct: account.type === 'business' ? 0 : 82, exhausted: account.type === 'business' }],
      askModel: () => { aiCalls++; throw new Error('Unexpected AI call'); } });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); origin = 'http://127.0.0.1:' + server.address().port;
  }
  async function request(route, value) {
    const r = await fetch(origin + route, value ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) } : {});
    const body = await r.json(); assert.equal(r.status, 200, JSON.stringify(body)); return body;
  }
  const model = state => state.models.find(item => item.id === 'grok-fixture');
  try {
    await start();
    const personal = { orchestrator: false, worker: true, reviewer: false };
    await request('/api/models/roles', { id: 'grok-fixture', roles: personal });
    await request('/api/orchestrator');
    account = { key: 'fixture-business', type: 'business' };
    let state = await request('/api/orchestrator?readOnly=1');
    assert.match(model(state).label, /Business$/); assert.equal(model(state).enabled, false, 'New account needs explicit role permissions');
    assert.equal(state.plans.find(pool => pool.id === 'Grok').readable, false, 'Previous personal quota cannot leak into Business');
    state = await request('/api/catalog/settings', { providerId: 'grok', allModelsEnabled: true });
    assert.equal(model(state).enabled, true, 'Bulk configuration targets the active account roles');
    const business = { orchestrator: false, worker: false, reviewer: true };
    await request('/api/models/roles', { id: 'grok-fixture', roles: business });
    account = { key: 'fixture-personal', type: 'normal' };
    state = await request('/api/orchestrator?readOnly=1');
    assert.deepEqual(model(state).roles, personal); assert.equal(model(state).label, 'Grok fixture');
    const beforePoll = discoveryCalls;
    await request('/api/orchestrator?readOnly=1'); await request('/api/orchestrator?readOnly=1');
    assert.equal(discoveryCalls, beforePoll, 'Unchanged login polling does not spawn catalog checks');
    catalogOk = false; account = { key: 'fixture-business', type: 'business' };
    state = await request('/api/orchestrator?readOnly=1');
    assert.deepEqual(model(state).roles, business); assert.equal(model(state).available, false, 'A failed new-account check must not inherit old-account model access');
    assert.match(model(state).availabilityWarning, /listing unavailable/);
    account = { key: 'fixture-personal', type: 'normal' };
    await request('/api/orchestrator?readOnly=1');
    account = { key: 'fixture-business', type: 'business' };
    await new Promise(resolve => server.close(resolve)); await start();
    state = await request('/api/orchestrator?readOnly=1'); assert.deepEqual(model(state).roles, business);
    assert.equal(model(state).available, false, 'A login change while the backend is stopped also invalidates old account access');
    await new Promise(resolve => server.close(resolve)); await start();
    state = await request('/api/orchestrator?readOnly=1');
    assert.equal(model(state).available, false, 'Repeated restart during failed discovery cannot resurrect a historical catalog for the unchanged invalidated account');
    assert.deepEqual(model(state).roles, business, 'Access invalidation never rewrites account-scoped role choices');
    assert.equal(aiCalls, 0);
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('failed cache invalidation does not commit switched account identity and retries safely', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-account-durable-')), planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  let account = { key: 'fixture-account-a', type: 'normal' }, catalogOk = true, server, origin;
  async function start() {
    server = createServer({ planFile, attemptsDir, skillsEnabled: false, refreshModels: false, monitorIntervalMs: 60000,
      models: [{ id: 'grok-fixture', provider: 'grok', cli: 'grok-fixture', enabled: true, available: true }], readAccount: () => account,
      discoverModels: async () => catalogOk ? { ok: true, ids: ['grok-fixture'] } : { ok: false, reason: 'Synthetic outage' }, fetchPlans: null,
      askModel: () => { throw Error('No inference authorized'); } });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); origin = 'http://127.0.0.1:' + server.address().port;
  }
  async function request(route, post = false) { const response = await fetch(origin + route, post ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' } : {}); return { status: response.status, body: await response.json() }; }
  const cache = path.join(attemptsDir, 'provider-model-catalogs.json'), backup = cache + '.fixture-backup';
  try {
    await start(); assert.equal((await request('/api/orchestrator?readOnly=1')).status, 200); assert.equal((await request('/api/models/refresh', true)).status, 200);
    fs.renameSync(cache, backup); fs.mkdirSync(cache); account = { key: 'fixture-account-b', type: 'business' }; catalogOk = false;
    assert.equal((await request('/api/orchestrator?readOnly=1')).status, 500);
    const stateFile = path.join(attemptsDir, 'orchestrator.json');
    assert.equal(JSON.parse(fs.readFileSync(stateFile, 'utf8')).lastAccountKeys.grok, 'fixture-account-a', 'Durability failure cannot commit the new account binding');
    fs.rmdirSync(cache); fs.renameSync(backup, cache);
    const retried = await request('/api/orchestrator?readOnly=1'); assert.equal(retried.status, 200); assert.equal(retried.body.models.find(model => model.id === 'grok-fixture').available, false);
    assert.equal(JSON.parse(fs.readFileSync(stateFile, 'utf8')).lastAccountKeys.grok, 'fixture-account-b');
    await new Promise(resolve => server.close(resolve)); await start();
    const restarted = await request('/api/orchestrator?readOnly=1'); assert.equal(restarted.status, 200); assert.equal(restarted.body.models.find(model => model.id === 'grok-fixture').available, false);
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
