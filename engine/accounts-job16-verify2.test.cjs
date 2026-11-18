'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const folders = require('./account-folders.cjs');

function temp() { return fs.mkdtempSync(path.join(os.tmpdir(), 'pw-accounts-verify2-')); }
function unusedPid() {
  for (let pid = 1000000000; pid > 100000; pid -= 997) {
    try { process.kill(pid, 0); }
    catch (error) { if (error.code === 'ESRCH' || error.code === 'EINVAL') return pid; }
  }
  throw new Error('Could not find an unused pid.');
}

test('an extra account does not inherit another login from the environment', async () => {
  const { accountEnvironment } = require('./provider-runtime.cjs');
  const folder = path.join(os.tmpdir(), 'pw-claude-account');
  const inherited = {
    PATH: 'C:\\bin', KEEP: 'yes',
    CLAUDE_CODE_OAUTH_TOKEN: 'synthetic-oauth', ANTHROPIC_API_KEY: 'synthetic-api',
    anthropic_base_url: 'https://example.invalid', OPENAI_API_KEY: 'synthetic-openai'
  };
  const claudeEnv = accountEnvironment('claude', { settingsFolder: folder }, inherited);
  assert.equal(claudeEnv.CLAUDE_CONFIG_DIR, folder);
  assert.equal(claudeEnv.CLAUDE_CODE_OAUTH_TOKEN, undefined);
  assert.equal(claudeEnv.ANTHROPIC_API_KEY, undefined);
  assert.equal(claudeEnv.anthropic_base_url, undefined);
  assert.equal(claudeEnv.KEEP, 'yes');
  assert.equal(inherited.CLAUDE_CODE_OAUTH_TOKEN, 'synthetic-oauth', 'the parent environment stays unchanged');
  const primary = accountEnvironment('claude', {}, inherited);
  assert.equal(primary.CLAUDE_CODE_OAUTH_TOKEN, 'synthetic-oauth');
  assert.equal(primary.CLAUDE_CONFIG_DIR, undefined);
  const poisoned = accountEnvironment('claude', { settingsFolder: 'C:\\accounts\nnext' }, inherited);
  assert.equal(poisoned.CLAUDE_CONFIG_DIR, undefined);
  assert.equal(poisoned.CLAUDE_CODE_OAUTH_TOKEN, 'synthetic-oauth');
  const codexFolder = path.join(os.tmpdir(), 'pw-codex-account');
  const codexEnv = accountEnvironment('codex', { settingsFolder: codexFolder }, inherited);
  assert.equal(codexEnv.CODEX_HOME, codexFolder);
  assert.equal(codexEnv.OPENAI_API_KEY, undefined);
  assert.equal(codexEnv.KEEP, 'yes');

  const usage = require('./claude-usage.cjs');
  const claude = require('./claude.cjs');
  const { adapterFor } = require('./provider-adapters.cjs');
  const seen = [];
  const originalAuth = usage.readClaudeAuth;
  const originalList = claude.listClaude;
  usage.readClaudeAuth = async options => { seen.push(options.env); return null; };
  claude.listClaude = async options => { seen.push(options.env || {}); return { ok: false, reason: 'fixture' }; };
  const previous = process.env.CLAUDE_CODE_OAUTH_TOKEN;
  process.env.CLAUDE_CODE_OAUTH_TOKEN = 'synthetic-process-oauth';
  try {
    const provider = { adapter: 'claude', settingsFolder: folder };
    await adapterFor(provider).quota(provider);
    await adapterFor(provider).discover(provider);
    assert.equal(seen.length, 2);
    for (const env of seen) {
      assert.equal(env.CLAUDE_CONFIG_DIR, folder);
      assert.equal(env.CLAUDE_CODE_OAUTH_TOKEN, undefined);
      assert.equal(env.ANTHROPIC_API_KEY, undefined);
    }
  } finally {
    usage.readClaudeAuth = originalAuth;
    claude.listClaude = originalList;
    if (previous === undefined) delete process.env.CLAUDE_CODE_OAUTH_TOKEN;
    else process.env.CLAUDE_CODE_OAUTH_TOKEN = previous;
  }
});

test('a lock held by this process is not stolen, and a dead owner is reclaimed at once', () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'accounts.json'), root = path.join(dir, 'accounts');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(file + '.lock', process.pid + ':still-running');
    const old = new Date(Date.now() - 60000);
    fs.utimesSync(file + '.lock', old, old);
    const started = Date.now();
    assert.throws(() => folders.addAccount(file, root, 'claude', 'claude', []), /busy/);
    assert.ok(Date.now() - started < 8000);
    assert.equal(fs.existsSync(file), false);
    assert.equal(fs.readFileSync(file + '.lock', 'utf8'), process.pid + ':still-running');

    fs.writeFileSync(file + '.lock', unusedPid() + ':gone');
    const row = folders.addAccount(file, root, 'codex', 'codex', []);
    assert.equal(folders.readAccounts(file, root).some(item => item.id === row.id), true);
    assert.equal(fs.existsSync(file + '.lock'), false);
    assert.ok(Date.now() - started < 12000);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a refusal records the plan even when that pool was missing, and drops any secret', () => {
  const { notePlanRefusal, mergePlans, statusFrom } = require('./plan-status.cjs');
  const secret = 'synthetic-access-token';
  assert.equal(statusFrom('claude@account', { reason: 'subscription is not active' }), 'expired');
  const noted = notePlanRefusal([], { id: 'a', provider: 'claude@abc', quotaPoolId: 'claude@abc', companyId: 'claude' }, 'subscription is not active ' + secret);
  assert.equal(noted.status, 'expired');
  assert.equal(noted.plans[0].planStatus, 'expired');
  assert.equal(noted.plans[0].readable, false);
  assert.equal(JSON.stringify(noted).includes(secret), false);
  const kept = mergePlans(noted.plans, [{ id: 'claude@abc', readable: false, planStatus: 'unknown', reason: 'HTTP 500 ' + secret }]);
  assert.equal(kept[0].planStatus, 'expired');
  assert.equal(JSON.stringify(kept).includes(secret), false);
  const omitted = mergePlans(kept, [{ id: 'ChatGPT', readable: true, leftPct: 80, planStatus: 'active' }]);
  assert.equal(omitted.find(plan => plan.id === 'claude@abc').planStatus, 'expired');
  const renewed = mergePlans(omitted, [{ id: 'claude@abc', readable: true, leftPct: 40, planStatus: 'active' }]);
  assert.equal(renewed.find(plan => plan.id === 'claude@abc').planStatus, 'active');
  assert.equal(renewed.find(plan => plan.id === 'claude@abc').leftPct, 40);
  assert.equal(notePlanRefusal(noted.plans, { provider: 'codex', quotaPoolId: 'chatgpt' }, 'usage limit reached ' + secret).status, null);
});

test('an unknown quota refresh does not clear an expired account', async () => {
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const dir = temp();
  const plan = require('../plan.js').example();
  plan.tasks = [];
  const model = { id: 'a', label: 'Sonnet', provider: 'claude@abc', adapter: 'claude', cli: 'claude-sonnet-5', plan: 'claude@abc', quotaPoolId: 'claude@abc', companyId: 'claude', enabled: true, available: true, cost: 1, limit: 100, capabilities: { canCode: true }, roles: { worker: true } };
  let phase = 'expired';
  const fetchPlans = async () => {
    if (phase === 'expired') return [{ id: 'claude@abc', readable: false, planStatus: 'expired', reason: 'claude plan expired.' }];
    if (phase === 'unknown') return [{ id: 'claude@abc', readable: false, planStatus: 'unknown', reason: 'Claude usage HTTP 500.' }];
    if (phase === 'missing') return [{ id: 'ChatGPT', readable: true, leftPct: 90, planStatus: 'active' }];
    return [{ id: 'claude@abc', readable: true, leftPct: 44, planStatus: 'active' }];
  };
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, models: [model], attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans, plansTtl: 0 });
  try {
    const status = async () => (await orchestra.tick()).models.find(item => item.id === 'a').planStatus;
    assert.equal(await status(), 'expired');
    phase = 'unknown';
    assert.equal(await status(), 'expired');
    phase = 'missing';
    assert.equal(await status(), 'expired');
    phase = 'active';
    assert.equal(await status(), 'active');
  } finally { orchestra.shutdown?.(); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('provider plan errors stay classified without copying a token', () => {
  const { parseUsage } = require('./antigravity.cjs');
  const secret = 'synthetic-agy-token';
  const parsed = parseUsage({ status: 'ERROR', result: { error: { message: 'subscription is not active', access_token: secret } } });
  assert.equal(parsed.planStatus, 'expired');
  assert.equal(JSON.stringify(parsed).includes(secret), false);
  const { parseChatGptUsage } = require('./planUsage.js');
  const usage = parseChatGptUsage({ plan_type: 'plus', rate_limit: { primary_window: { used_percent: 10, reset_at: 1800000000 }, secondary_window: { used_percent: 4, reset_at: 1800000000 } }, access_token: secret });
  assert.equal(usage.planStatus, 'active');
  assert.equal(JSON.stringify(usage).includes(secret), false);
});
