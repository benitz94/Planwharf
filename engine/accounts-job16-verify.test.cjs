'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { Worker } = require('node:worker_threads');
const folders = require('./account-folders.cjs');
const { quotaSwitches, resolveUsageFallback } = require('./model-policy.cjs');
const { providerError, statusFrom } = require('./plan-status.cjs');

function temp() { return fs.mkdtempSync(path.join(os.tmpdir(), 'pw-accounts-verify-')); }

test('a stale account list cannot drop an account already saved', () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts');
    const first = folders.addAccount(file, root, 'claude', 'claude', []);
    const second = folders.addAccount(file, root, 'claude', 'claude', []);
    const saved = folders.readAccounts(file, root);
    assert.deepEqual(saved.map(row => row.id).sort(), [first.id, second.id].sort());
    assert.equal(JSON.stringify(saved).includes('token'), false);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('account files reject secrets, duplicates, escapes and broken JSON', () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts');
    const id = '11111111-1111-4111-8111-111111111111';
    const folder = path.join(root, 'claude', id);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const row = { id, adapter: 'claude', providerId: 'claude', folder };
    fs.writeFileSync(file, JSON.stringify([{ ...row, token: 'synthetic-token', email: 'person@example.com' }]));
    assert.throws(() => folders.readAccounts(file, root), /Invalid account folders file/);
    assert.throws(() => folders.addAccount(file, root, 'codex', 'codex', []), /Invalid account folders file/);
    assert.equal(fs.readFileSync(file, 'utf8').includes('synthetic-token'), true);

    fs.writeFileSync(file, JSON.stringify([row, { ...row }]));
    assert.throws(() => folders.readAccounts(file, root), /Invalid account folders file/);

    fs.writeFileSync(file, JSON.stringify([{ ...row, folder: path.join(root, 'claude', id, '..', '..', 'outside') }]));
    assert.throws(() => folders.readAccounts(file, root), /Invalid account folders file/);

    fs.writeFileSync(file, '{');
    assert.throws(() => folders.readAccounts(file, root));
    assert.throws(() => folders.addAccount(file, root, 'claude', 'claude', []), /Invalid|Unexpected|JSON/);
    assert.equal(fs.readFileSync(file, 'utf8'), '{');

    fs.rmSync(file, { force: true });
    assert.throws(() => folders.addAccount(file, root + '\nescaped', 'claude', 'claude', []));
    assert.equal(fs.existsSync(file), false);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a folder that points outside the account root is rejected', () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts'), outside = path.join(dir, 'outside');
    const id = '22222222-2222-4222-8222-222222222222';
    const folder = path.join(root, 'codex', id);
    fs.mkdirSync(outside, { recursive: true });
    fs.mkdirSync(path.dirname(folder), { recursive: true });
    try { fs.symlinkSync(outside, folder, 'junction'); }
    catch (error) { if (error.code === 'EPERM') return; throw error; }
    fs.writeFileSync(file, JSON.stringify([{ id, adapter: 'codex', providerId: 'codex', folder }]));
    assert.throws(() => folders.readAccounts(file, root), /Invalid account folders file/);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a failed save removes the new folder and keeps the previous accounts', () => {
  const dir = temp();
  try {
    const root = path.join(dir, 'accounts');
    const parent = path.join(dir, 'not-a-directory');
    fs.writeFileSync(parent, 'blocked');
    const file = path.join(parent, 'accounts.json');
    assert.throws(() => folders.addAccount(file, root, 'claude', 'claude', []));
    assert.equal(fs.existsSync(path.join(root, 'claude')), false);
    assert.equal(fs.readFileSync(parent, 'utf8'), 'blocked');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('sign-in commands keep quotes closed', () => {
  const folder = process.platform === 'win32' ? "D:\\accounts\\o'brien" : "/tmp/accounts/o'brien";
  const command = folders.commandFor('codex', folder);
  assert.match(command, process.platform === 'win32' ? /o''brien/ : /o'\\''brien/);
  assert.equal((command.match(/'/g) || []).length % 2, 0);
  assert.match(command, /CODEX_HOME/);
  assert.throws(() => folders.commandFor('claude', 'C:\\accounts\nnext'));
});

test('parallel adds keep every account', async () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts');
    const modulePath = require.resolve('./account-folders.cjs');
    const workers = Array.from({ length: 6 }, () => new Promise((resolve, reject) => {
      const worker = new Worker(`
        const { parentPort, workerData } = require('node:worker_threads');
        const folders = require(workerData.modulePath);
        try {
          const row = folders.addAccount(workerData.file, workerData.root, 'codex', 'codex', []);
          parentPort.postMessage({ id: row.id, command: row.signInCommand });
        } catch (error) { parentPort.postMessage({ error: String(error && error.message || error) }); }
      `, { eval: true, workerData: { modulePath, file, root } });
      worker.on('message', message => message.error ? reject(new Error(message.error)) : resolve(message));
      worker.on('error', reject);
    }));
    const rows = await Promise.all(workers);
    const saved = folders.readAccounts(file, root);
    assert.equal(saved.length, rows.length);
    assert.equal(new Set(saved.map(row => row.id)).size, rows.length);
    for (const row of saved) assert.equal(fs.existsSync(row.folder), true);
    assert.equal(JSON.stringify(saved).includes('token'), false);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a stale lock is recovered and a live lock fails closed', () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(file + '.lock', '');
    const old = new Date(Date.now() - 60000);
    fs.utimesSync(file + '.lock', old, old);
    const row = folders.addAccount(file, root, 'claude', 'claude', []);
    assert.equal(folders.readAccounts(file, root).some(item => item.id === row.id), true);
    assert.equal(fs.existsSync(file + '.lock'), false);

    fs.writeFileSync(file + '.lock', '');
    const started = Date.now();
    assert.throws(() => folders.addAccount(file, root, 'claude', 'claude', []), /busy/);
    assert.ok(Date.now() - started < 8000);
    assert.equal(folders.readAccounts(file, root).length, 1);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

function crew() {
  return [
    { id: 'a', cli: 'claude-sonnet-5', provider: 'claude', providerId: 'claude', accountScope: 'claude:a', companyId: 'claude', plan: 'claude', quotaPoolId: 'claude', enabled: true, available: true },
    { id: 'b', cli: 'claude-sonnet-5', provider: 'claude@b', providerId: 'claude@b', accountScope: 'claude@b', companyId: 'claude', plan: 'claude@b', quotaPoolId: 'claude@b', enabled: true, available: true },
    { id: 'd', cli: 'claude-sonnet-5', provider: 'claude@d', providerId: 'claude@d', accountScope: 'claude@d', companyId: 'claude', plan: 'claude@d', quotaPoolId: 'claude@d', enabled: true, available: true },
    { id: 'c', cli: 'gpt-6-sol', provider: 'codex', providerId: 'codex', accountScope: 'codex', companyId: 'codex', plan: 'chatgpt', quotaPoolId: 'chatgpt', enabled: true, available: true }
  ];
}
const running = [{ subTaskId: 's', modelId: 'a' }];

test('another account is used only when its own quota has room', () => {
  const models = crew();
  const plans = [
    { id: 'claude', readable: true, leftPct: 10, windows: [{ name: '5-hour', leftPct: 10 }, { name: 'weekly', leftPct: 80 }] },
    { id: 'claude@b', readable: true, leftPct: 40, windows: [{ name: '5-hour', leftPct: 40 }, { name: 'weekly', leftPct: 80 }] },
    { id: 'claude@d', readable: true, leftPct: 70, windows: [{ name: '5-hour', leftPct: 70 }, { name: 'weekly', leftPct: 80 }] },
    { id: 'chatgpt', readable: true, leftPct: 99, windows: [{ name: '5-hour', leftPct: 99 }, { name: 'weekly', leftPct: 99 }] }
  ];
  assert.equal(quotaSwitches({ running, models, plans, thresholdPercent: 35 })[0].toModelId, 'd');

  plans[1].windows[1].leftPct = 10;
  plans[1].leftPct = 10;
  assert.equal(quotaSwitches({ running, models, plans, thresholdPercent: 35 })[0].toModelId, 'd');

  plans[2].leftPct = 35;
  plans[2].windows = [{ name: '5-hour', leftPct: 35 }, { name: 'weekly', leftPct: 35 }];
  assert.equal(quotaSwitches({ running, models, plans, thresholdPercent: 35 })[0].toModelId, 'c');

  plans[2] = { id: 'claude@d', readable: false, planStatus: 'not signed in' };
  assert.equal(quotaSwitches({ running, models, plans, thresholdPercent: 35 })[0].toModelId, 'c');

  plans[1] = { id: 'claude@b', readable: false, planStatus: 'unknown' };
  plans[2] = { id: 'claude@d', readable: false, leftPct: null, planStatus: 'unknown' };
  assert.equal(quotaSwitches({ running, models, plans, thresholdPercent: 35 })[0].toModelId, 'c');

  const only = models.filter(model => model.id !== 'c');
  const unreadOnly = quotaSwitches({ running, models: only, plans, thresholdPercent: 35 });
  assert.ok(['b', 'd'].includes(unreadOnly[0].toModelId));
  plans[1] = { id: 'claude@b', readable: false, planStatus: 'expired' };
  plans[2] = { id: 'claude@d', readable: false, planStatus: 'not signed in' };
  assert.deepEqual(quotaSwitches({ running, models: only, plans, thresholdPercent: 35 }), []);
});

test('assignment skips an unread sibling and keeps a sibling that still has quota', () => {
  const models = crew().filter(model => model.id !== 'd').map(model => ({ ...model, fallbackProviderId: '@automatic' }));
  const plans = [
    { id: 'claude', readable: true, leftPct: 0, exhausted: true },
    { id: 'claude@b', readable: false, planStatus: 'unknown' },
    { id: 'chatgpt', readable: true, leftPct: 80 }
  ];
  assert.equal(resolveUsageFallback(models[0], models, plans, () => true).id, 'c');
  plans[1] = { id: 'claude@b', readable: true, leftPct: 22 };
  assert.equal(resolveUsageFallback(models[0], models, plans, () => true).id, 'b');
});

test('two account folders read two quotas and never return the tokens', async () => {
  const dir = temp();
  try {
    const alpha = 'synthetic-codex-alpha', beta = 'synthetic-codex-beta';
    for (const [name, token] of [['alpha', alpha], ['beta', beta]]) {
      const home = path.join(dir, name);
      fs.mkdirSync(home, { recursive: true });
      fs.writeFileSync(path.join(home, 'auth.json'), JSON.stringify({ tokens: { access_token: token, account_id: 'acct-' + name } }));
    }
    const { readChatGpt } = require('./planUsage.js');
    const fetchImpl = async (_url, options) => ({ status: 200, text: async () => JSON.stringify({ plan_type: 'plus', rate_limit: {
      primary_window: { used_percent: options.headers.Authorization.endsWith('alpha') ? 80 : 15, reset_at: 1800000000 },
      secondary_window: { used_percent: 5, reset_at: 1800000000 } } }) });
    const first = await readChatGpt(dir, fetchImpl, path.join(dir, 'alpha'));
    const second = await readChatGpt(dir, fetchImpl, path.join(dir, 'beta'));
    assert.equal(first.leftPct, 20);
    assert.equal(second.leftPct, 85);
    assert.equal(JSON.stringify([first, second]).includes(alpha), false);
    assert.equal(JSON.stringify([first, second]).includes(beta), false);

    const claudeSecret = 'synthetic-claude-token';
    const { createClaudeUsageReader } = require('./claude-usage.cjs');
    const denied = await createClaudeUsageReader({
      readAuth: async () => ({ token: claudeSecret, expiresAt: Date.now() + 60000 }),
      fetchImpl: async () => ({ status: 403, headers: { get() { return null; } }, body: (async function* () {
        yield Buffer.from(JSON.stringify({ error: 'subscription is not active', access_token: claudeSecret }));
      })() })
    })();
    assert.equal(denied.planStatus, 'expired');
    assert.equal(JSON.stringify(denied).includes(claudeSecret), false);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a company limit applies separately to each account quota', async () => {
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const dir = temp();
  const plan = require('../plan.js').example();
  plan.tasks = [];
  const model = (id, pool) => ({ id, label: id, provider: pool === 'claude' ? 'claude' : 'claude@b', adapter: 'claude', cli: 'claude-sonnet-5',
    plan: pool, quotaPoolId: pool, companyPoolId: 'claude', enabled: true, available: true, cost: 1, limit: 100, capabilities: { canCode: true } });
  const plans = [
    { id: 'claude', readable: true, leftPct: 70, planStatus: 'active', windows: [{ name: '5-hour', leftPct: 70 }, { name: 'weekly', leftPct: 90 }] },
    { id: 'claude@b', readable: true, leftPct: 50, planStatus: 'active', windows: [{ name: '5-hour', leftPct: 50 }, { name: 'weekly', leftPct: 90 }] }
  ];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, models: [model('a', 'claude'), model('b', 'claude@b')],
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => plans, plansTtl: 0 });
  try {
    assert.equal(orchestra.setQuotaLimit(null, { pool: 'claude', fiveHourPercent: 80, weeklyPercent: 80 }).ok, true);
    const view = async () => Object.fromEntries((await orchestra.tick()).models.map(item => [item.id, item]));
    let models = await view();
    assert.equal(models.a.lowQuota, true);
    assert.equal(models.b.lowQuota, true);
    plans[1] = { ...plans[1], leftPct: 85, windows: [{ name: '5-hour', leftPct: 85 }, { name: 'weekly', leftPct: 90 }] };
    models = await view();
    assert.equal(models.a.lowQuota, true);
    assert.equal(models.b.lowQuota, false);
    assert.equal(models.b.quotaLimit.source, 'company');
  } finally { orchestra.shutdown?.(); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('the server rejects a poisoned account file, a single-account tool, and keeps parallel adds', async () => {
  const dir = temp();
  const planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  fs.mkdirSync(attemptsDir, { recursive: true });
  const secret = 'synthetic-account-secret';
  const id = '33333333-3333-4333-8333-333333333333';
  const folder = path.join(attemptsDir, 'accounts', 'claude', id);
  fs.writeFileSync(path.join(attemptsDir, 'accounts.json'), JSON.stringify([{ id, adapter: 'claude', providerId: 'claude', folder, token: secret }]));
  const { createServer } = require('../server.cjs');
  const providers = [
    { id: 'claude', adapter: 'claude', label: 'Claude Code', enabled: true, quotaPoolId: 'claude', models: [{ id: 'claude:sonnet', label: 'Sonnet', provider: 'claude', cli: 'claude-sonnet-5', enabled: true, available: true }] },
    { id: 'grok', adapter: 'grok', label: 'Grok', enabled: true, quotaPoolId: 'grok', models: [{ id: 'grok:4', label: 'Grok', provider: 'grok', cli: 'grok-4', enabled: true, available: true }] }
  ];
  assert.throws(() => createServer({ planFile, attemptsDir, monitorIntervalMs: 60000, providers, fetchPlans: async () => [] }), error => {
    assert.match(error.message, /Invalid account folders file/);
    assert.equal(String(error.message).includes(secret), false);
    return true;
  });
  fs.rmSync(path.join(attemptsDir, 'accounts.json'));
  const server = createServer({ planFile, attemptsDir, monitorIntervalMs: 60000, providers, fetchPlans: async () => [] });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = 'http://127.0.0.1:' + server.address().port;
  try {
    const grok = await fetch(origin + '/api/accounts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ providerId: 'grok' }) });
    assert.equal(grok.status, 400);
    const extra = await fetch(origin + '/api/accounts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ providerId: 'claude', token: secret }) });
    assert.equal(extra.status, 400);
    const added = await Promise.all([0, 1, 2, 3].map(() => fetch(origin + '/api/accounts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ providerId: 'claude' }) }).then(response => response.json())));
    assert.equal(new Set(added.map(row => row.folder)).size, 4);
    const saved = JSON.parse(fs.readFileSync(path.join(attemptsDir, 'accounts.json'), 'utf8'));
    assert.equal(saved.length, 4);
    assert.equal(JSON.stringify(saved).includes(secret), false);
    assert.equal(JSON.stringify(added).includes(secret), false);
  } finally {
    if (typeof server.shutdown === 'function') await server.shutdown();
    server.closeAllConnections?.();
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('subscription errors stay classified without copying secrets', () => {
  const secret = 'synthetic-access-token-not-real';
  const expired = providerError('claude', { error: 'subscription is not active', access_token: secret }, 'Claude');
  assert.equal(expired.planStatus, 'expired');
  assert.equal(JSON.stringify(expired).includes(secret), false);
  const signedOut = providerError('grok', 'login expired for ' + secret, 'Grok');
  assert.equal(signedOut.planStatus, 'not signed in');
  assert.equal(JSON.stringify(signedOut).includes(secret), false);
  assert.equal(providerError('codex', { message: 'usage limit reached', token: secret }, 'ChatGPT'), null);
  assert.equal(statusFrom('antigravity', { readable: true, reason: 'plan expired ' + secret }), 'active');
  assert.equal(statusFrom('codex', {}), 'unknown');
});
