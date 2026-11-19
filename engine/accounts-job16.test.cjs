'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { statusFrom } = require('./plan-status.cjs');
const { planExhausted } = require('./planUsage.js');
const { quotaSwitches, usagePolicy } = require('./model-policy.cjs');
const folders = require('./account-folders.cjs');

test('subscription state is explicit; recorded quota refusals are not misclassified as expiry', () => {
  const fake = provider => fs.readFileSync(path.join(__dirname, '..', 'native', 'porting', 'fakes', provider, 'refusal-quota.' + ({ claude: 'jsonl', codex: 'jsonl', grok: 'txt', antigravity: 'jsonl' }[provider] || 'jsonl')), 'utf8');
  for (const [adapter, fixture] of [['claude', 'claude'], ['codex', 'codex'], ['grok', 'grok'], ['antigravity', 'agy']]) {
    assert.equal(statusFrom(adapter, { reason: fake(fixture) }), 'unknown');
    assert.equal(statusFrom(adapter, { readable: true }), 'active');
  }
  assert.equal(statusFrom('claude', { reason: 'Claude subscription is not active.' }), 'expired');
  assert.equal(statusFrom('codex', { reason: 'ChatGPT login expired.' }), 'not signed in');
  assert.equal(statusFrom('grok', { reason: 'Grok plan expired.' }), 'expired');
  assert.equal(statusFrom('antigravity', { reason: 'Sign in to agy.' }), 'not signed in');
  assert.equal(statusFrom('codex', { reason: 'HTTP 500' }), 'unknown');
});

test('expired and signed-out plans block assignment without confusing unknown quota', () => {
  const model = { id: 'a', plan: 'claude', policy: {} };
  for (const planStatus of ['expired', 'not signed in']) {
    const plans = [{ id: 'claude', planStatus, readable: false }];
    assert.equal(planExhausted(plans, 'claude'), true);
    assert.equal(usagePolicy(model, plans).blocked, true);
  }
  assert.equal(planExhausted([{ id: 'claude', planStatus: 'unknown', readable: false }], 'claude'), false);
});

test('same exact model on another account wins before a different company; quotas stay isolated', () => {
  const models = [
    { id: 'claude-a', cli: 'claude-sonnet-5', provider: 'claude', accountScope: 'claude:a', plan: 'claude', enabled: true, available: true },
    { id: 'claude-b', cli: 'claude-sonnet-5', provider: 'claude@b', companyId: 'claude', accountScope: 'claude@b', plan: 'claude@b', enabled: true, available: true },
    { id: 'codex', cli: 'gpt-6-sol', provider: 'codex', plan: 'chatgpt', enabled: true, available: true }
  ];
  const plans = [{ id: 'claude', readable: true, leftPct: 10 }, { id: 'claude@b', readable: true, leftPct: 55 }, { id: 'chatgpt', readable: true, leftPct: 95 }];
  const run = () => quotaSwitches({ running: [{ subTaskId: 's', modelId: 'claude-a' }], models, plans, thresholdPercent: 35 });
  assert.equal(run()[0].toModelId, 'claude-b');
  plans[1] = { id: 'claude@b', readable: false, planStatus: 'expired' };
  assert.equal(run()[0].toModelId, 'codex');

  // Verify runtime model shapes (without explicit accountScope) also prioritize same model on another account
  const runtimeModels = [
    { id: 'claude:sonnet', cli: 'claude-sonnet-5', providerId: 'claude', provider: 'claude', plan: 'claude', quotaPoolId: 'claude', enabled: true, available: true },
    { id: 'claude:sonnet@b', cli: 'claude-sonnet-5', providerId: 'claude@b', provider: 'claude@b', companyId: 'claude', plan: 'claude@b', quotaPoolId: 'claude@b', enabled: true, available: true },
    { id: 'codex:gpt', cli: 'gpt-6-sol', providerId: 'codex', provider: 'codex', plan: 'chatgpt', quotaPoolId: 'chatgpt', enabled: true, available: true }
  ];
  const runtimePlans = [{ id: 'claude', readable: true, leftPct: 15 }, { id: 'claude@b', readable: true, leftPct: 60 }, { id: 'chatgpt', readable: true, leftPct: 90 }];
  const runtimeRun = () => quotaSwitches({ running: [{ subTaskId: 's', modelId: 'claude:sonnet' }], models: runtimeModels, plans: runtimePlans, thresholdPercent: 35 });
  assert.equal(runtimeRun()[0].toModelId, 'claude:sonnet@b');
});

test('account folders save paths only and reject unsupported providers', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-accounts-job16-'));
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts');
    const first = folders.addAccount(file, root, 'claude', 'claude', []);
    const second = folders.addAccount(file, root, 'claude', 'claude', folders.readAccounts(file, root));
    assert.notEqual(first.folder, second.folder);
    assert.match(first.signInCommand, /CLAUDE_CONFIG_DIR/);
    assert.match(first.signInCommand, /claude login/);
    assert.deepEqual(folders.readAccounts(file, root).map(row => row.folder), [first.folder, second.folder]);
    assert.deepEqual(Object.keys(JSON.parse(fs.readFileSync(file, 'utf8'))[0]).sort(), ['adapter', 'folder', 'id', 'providerId']);
    assert.throws(() => folders.addAccount(file, root, 'grok', 'grok', []));
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('hidden models and explain lines match company and subscription state', () => {
  const models = [
    { id: 'c1', cli: 'sonnet', adapter: 'claude', planStatus: 'expired', catalogExcluded: false },
    { id: 'c2', cli: 'opus', adapter: 'claude', planStatus: 'expired', catalogExcluded: false },
    { id: 'c3', cli: 'haiku', adapter: 'claude', planStatus: 'expired', catalogExcluded: false },
    { id: 'c4', cli: 'sonnet-3-5', adapter: 'claude', planStatus: 'expired', catalogExcluded: false },
    { id: 'g1', cli: 'grok-4', adapter: 'grok', planStatus: 'not signed in', catalogExcluded: false },
    { id: 'ok', cli: 'gpt-6', adapter: 'codex', planStatus: 'active', catalogExcluded: false }
  ];
  const offered = models.filter(m => !m.catalogExcluded && !['expired', 'not signed in'].includes(m.planStatus));
  assert.deepEqual(offered.map(m => m.id), ['ok']);

  const groups = Object.entries(Object.groupBy(models.filter(m => !m.catalogExcluded && ['expired', 'not signed in'].includes(m.planStatus)), m => `${m.adapter}|${m.planStatus}`));
  const lines = groups.map(([key, rows]) => {
    const [adapter, status] = key.split('|');
    const company = { claude: 'Claude', codex: 'ChatGPT', grok: 'Grok', antigravity: 'Gemini' }[adapter] || adapter;
    return `${rows.length} models hidden: ${company} ${status === 'expired' ? 'plan expired. Renew it or sign in again' : 'is not signed in. Sign in'}, then look for models again.`;
  });
  assert.deepEqual(lines, [
    '4 models hidden: Claude plan expired. Renew it or sign in again, then look for models again.',
    '1 models hidden: Grok is not signed in. Sign in, then look for models again.'
  ]);
});

test('server /api/accounts creates account folder and returns sign-in command without storing secrets', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-srv-accounts-job16-'));
  const planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  const { createServer } = require('../server.cjs');
  const server = createServer({
    planFile,
    attemptsDir,
    monitorIntervalMs: 60000,
    storeDir: dir,
    providers: [
      { id: 'claude', adapter: 'claude', label: 'Claude Code', enabled: true, quotaPoolId: 'claude',
        models: [{ id: 'claude:sonnet', label: 'Claude Sonnet', provider: 'claude', cli: 'claude-sonnet-5', enabled: true, available: true }] },
      { id: 'codex', adapter: 'codex', label: 'Codex', enabled: true, quotaPoolId: 'chatgpt',
        models: [{ id: 'codex:gpt', label: 'GPT-6', provider: 'codex', cli: 'gpt-6-sol', enabled: true, available: true }] }
    ],
    fetchPlans: async () => [
      { id: 'claude', readable: true, leftPct: 80, planStatus: 'active' },
      { id: 'chatgpt', readable: true, leftPct: 90, planStatus: 'active' }
    ]
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = 'http://127.0.0.1:' + server.address().port;
  try {
    // Unsupported provider rejected
    const bad = await fetch(origin + '/api/accounts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ providerId: 'unknown' }) });
    assert.equal(bad.status, 400);

    // Supported provider adds account
    const res = await fetch(origin + '/api/accounts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ providerId: 'claude' }) });
    assert.equal(res.status, 200);
    const body = await res.json();
    assert.equal(body.providerId, 'claude');
    assert.match(body.signInCommand, /CLAUDE_CONFIG_DIR/);
    assert.ok(fs.existsSync(body.folder));

    // Verify stored accounts file contains no secrets
    const accounts = JSON.parse(fs.readFileSync(path.join(attemptsDir, 'accounts.json'), 'utf8'));
    assert.equal(accounts.length, 1);
    assert.deepEqual(Object.keys(accounts[0]).sort(), ['adapter', 'folder', 'id', 'providerId']);

    // Snapshot has the added provider and model
    const addedProvider = body.snapshot.providers.find(p => p.id.startsWith('claude@'));
    assert.ok(addedProvider);
    assert.equal(addedProvider.companyId, 'claude');
    const addedModel = body.snapshot.models.find(m => m.id.startsWith('claude:sonnet@'));
    assert.ok(addedModel);
    assert.equal(addedModel.companyId, 'claude');
  } finally {
    if (server.settled) await server.settled.catch(() => {});
    if (typeof server.shutdown === 'function') await server.shutdown();
    server.closeAllConnections?.();
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
