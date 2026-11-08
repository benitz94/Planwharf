'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { validatePolicy, usagePolicy, resolveUsageFallback } = require('./model-policy.cjs');
const { launchSpec } = require('./provider-runtime.cjs');
const { diagnoseProvider } = require('./provider-diagnostics.cjs');
const { createServer } = require('../server.cjs');
const { createOrchestrator, pickWorkers } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
const models = [
  { id: 'lead', provider: 'codex', cli: 'gpt-6-astra', plan: 'ChatGPT', enabled: true, kind: 'premium' },
  { id: 'fallback', provider: 'grok', cli: 'grok-4.7', plan: 'Grok', enabled: true, kind: 'default' }
];
const policy = { minRemainingPercent: 20, warningRemainingPercent: 30, fallbackModelId: 'fallback', alwaysApprove: false };
test('ChatGPT siblings inherit one provider policy while Grok stays independent after restart', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-independent-policies-'));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const roster = [models[0], { ...models[0], id: 'second-gpt', cli: 'gpt-6-luna' }, { ...models[1], alwaysApprove: false }];
  const plans = [{ id: 'ChatGPT', readable: true, leftPct: 25 }, { id: 'Grok', readable: true, leftPct: 60 }];
  const first = { minRemainingPercent: 30, warningRemainingPercent: 40, fallbackModelId: 'fallback', alwaysApprove: false };
  const second = { minRemainingPercent: 10, warningRemainingPercent: 15, fallbackModelId: null, alwaysApprove: true };
  const grok = { minRemainingPercent: 50, warningRemainingPercent: 70, fallbackModelId: 'second-gpt', alwaysApprove: false };
  const make = () => createOrchestrator({ dir, readPlan: async () => plan, models: roster, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => plans });
  const savedEnv = { codex: process.env.PLANWHARF_CODEX, grok: process.env.PLANWHARF_GROK };
  const promptDirs = [];
  try {
    let orchestra = make();
    assert.equal((await orchestra.snapshot()).models.find(m => m.id === 'fallback').alwaysApprove, false, 'Configured per-model permission is preserved before a UI save');
    const chatPolicy = { minRemainingPercent: 30, warningRemainingPercent: 40, fallbackProviderId: 'grok', alwaysApprove: false };
    const grokPolicy = { minRemainingPercent: 50, warningRemainingPercent: 70, fallbackProviderId: 'codex', alwaysApprove: true };
    assert.equal((await orchestra.setProviderPolicy('codex', chatPolicy)).ok, true);
    assert.equal((await orchestra.setProviderPolicy('grok', grokPolicy)).ok, true);
    let view = await orchestra.tick();
    assert.equal(view.models.find(m => m.id === 'lead').usagePolicy.state, 'limit');
    assert.equal(view.models.find(m => m.id === 'second-gpt').usagePolicy.state, 'limit', 'Every ChatGPT model inherits its provider threshold');
    assert.equal(view.models.find(m => m.id === 'fallback').usagePolicy.state, 'warning');
    assert.deepEqual(pickWorkers(view.models, { job: { title: 'Implement isolated fixture' }, plans, count: 3 }).map(m => m.id), ['fallback']);
    process.env.PLANWHARF_CODEX = process.execPath; process.env.PLANWHARF_GROK = process.execPath;
    for (const model of view.models) {
      const spec = launchSpec(model, dir, 'No inference: inspect argv only');
      if (spec.promptFile) promptDirs.push(path.dirname(spec.promptFile));
      if (model.provider === 'codex') assert.equal(spec.args.includes('--dangerously-bypass-approvals-and-sandbox'), false);
      else { assert.equal(spec.args.includes('--always-approve'), true); assert.equal(spec.args[spec.args.indexOf('--permission-mode') + 1], 'bypassPermissions'); }
    }
    const changed = { ...chatPolicy, minRemainingPercent: 20, alwaysApprove: true };
    await orchestra.setProviderPolicy('codex', changed);
    orchestra = make(); view = await orchestra.tick();
    const byId = Object.fromEntries(view.models.map(m => [m.id, m]));
    assert.deepEqual(byId.lead.policy, byId['second-gpt'].policy);
    assert.deepEqual(view.providers.find(p => p.id === 'codex').policy, changed);
    assert.deepEqual(view.providers.find(p => p.id === 'grok').policy, grokPolicy);
    assert.equal(byId.lead.usagePolicy.state, 'warning'); assert.equal(byId.fallback.alwaysApprove, true);
  } finally {
    for (const [adapter, value] of Object.entries(savedEnv)) { const key = 'PLANWHARF_' + adapter.toUpperCase(); if (value === undefined) delete process.env[key]; else process.env[key] = value; }
    for (const folder of promptDirs) fs.rmSync(folder, { recursive: true, force: true });
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
test('shared usage boundaries, unread quotas, explicit fallbacks and cycles', () => {
  const lead = { ...models[0], policy };
  const plans = [{ id: 'ChatGPT', readable: true, leftPct: 20 }, { id: 'Grok', readable: true, leftPct: 95 }];
  assert.equal(usagePolicy(lead, plans).state, 'limit');
  assert.equal(usagePolicy(lead, [{ id: 'ChatGPT', readable: true, leftPct: 30 }]).state, 'warning');
  assert.equal(usagePolicy(lead, []).state, 'unread');
  assert.equal(usagePolicy(lead, [{ id: 'ChatGPT', readable: true, leftPct: 150 }]).state, 'unread');
  assert.equal(resolveUsageFallback(lead, [lead, models[1]], plans, () => true).id, 'fallback');
  assert.equal(resolveUsageFallback(lead, [lead, models[1]], [], () => true), null);
  assert.equal(resolveUsageFallback({ ...lead, policy: { ...policy, fallbackModelId: null } }, models, plans, () => true), null);
  assert.equal(resolveUsageFallback(lead, [lead, { ...models[1], policy: { ...policy, fallbackModelId: 'lead' } }], [{ ...plans[0] }, { id: 'Grok', readable: true, leftPct: 0 }], () => true), null);
  assert.equal(resolveUsageFallback(lead, [lead, models[1]], plans, m => m.id !== 'lead'), null, 'Fallback cannot override original role permissions');
  for (const invalid of [{ minRemainingPercent: -1 }, { minRemainingPercent: '80' }, { warningRemainingPercent: 10, minRemainingPercent: 20 }, { alwaysApprove: 'yes' }, { unknown: true }]) assert.throws(() => validatePolicy(invalid));
});

test('remaining quota semantics, automatic early warning, reset and legacy migration', () => {
  const model = { ...models[0], policy: { minRemainingPercent: 20 } };
  for (const [leftPct, expected] of [[80, 'ok'], [31, 'ok'], [30, 'warning'], [21, 'warning'], [20, 'limit'], [0, 'limit'], [100, 'ok']]) {
    const status = usagePolicy(model, [{ id: 'ChatGPT', readable: true, leftPct }]);
    assert.equal(status.state, expected, leftPct + '% remaining');
    assert.equal(status.remainingPercent, leftPct); assert.equal(status.warningRemainingPercent, 30);
  }
  assert.equal(usagePolicy({ ...model, policy: { minRemainingPercent: 95 } }, [{ id: 'ChatGPT', readable: true, leftPct: 100 }]).state, 'warning');
  assert.equal(usagePolicy({ ...model, policy: { minRemainingPercent: 100 } }, [{ id: 'ChatGPT', readable: true, leftPct: 100 }]).state, 'limit');
  assert.equal(usagePolicy({ ...model, policy: { minRemainingPercent: 0 } }, [{ id: 'ChatGPT', readable: true, leftPct: 1 }]).blocked, false);
  assert.equal(usagePolicy({ ...model, policy: { minRemainingPercent: 0 } }, [{ id: 'ChatGPT', readable: true, leftPct: 0 }]).blocked, true);
  for (const invalid of [null, [], { minRemainingPercent: 20, warningRemainingPercent: 20 }, { minRemainingPercent: 20, warningRemainingPercent: 10 }, { minRemainingPercent: 20, maxUsedPercent: 80 }]) assert.throws(() => validatePolicy(invalid));
  const old = { maxUsedPercent: 80, warningUsedPercent: 70, alwaysApprove: false };
  assert.deepEqual(validatePolicy(old), { minRemainingPercent: 20, warningRemainingPercent: 30, fallbackModelId: null, alwaysApprove: false });
  assert.deepEqual(old, { maxUsedPercent: 80, warningUsedPercent: 70, alwaysApprove: false }, 'Migration does not overwrite source configuration or historical evidence');
  const { providerPolicyFor } = require('./provider-policy.cjs');
  assert.equal(providerPolicyFor({ id: 'codex' }, models, { providerPolicies: { codex: old } }).policy.minRemainingPercent, 20);
  const combined = providerPolicyFor({ id: 'codex' }, [{ ...models[0], policy: old }, { ...models[0], id: 'sibling', policy: { minRemainingPercent: 40, warningRemainingPercent: 50 } }]);
  assert.equal(combined.policy.minRemainingPercent, 40); assert.equal(combined.policy.warningRemainingPercent, 50);
});

test('automatic fallback skips sibling pools, restricted roles, disabled and limited providers', () => {
  const lead = { ...models[0], policy: { minRemainingPercent: 20 }, fallbackProviderId: '@automatic' };
  const alternative = (id, changes = {}) => ({ ...models[1], id, ...changes });
  const candidates = [lead, { ...lead, id: 'same-provider' }, alternative('same-pool', { provider: 'other', plan: 'ChatGPT' }), alternative('disabled', { enabled: false }), alternative('unavailable', { available: false }), alternative('provider-off', { providerEnabled: false }), alternative('wrong-role'), alternative('limited', { policy: { minRemainingPercent: 10 }, plan: 'Low' }), alternative('unknown-limited', { policy: { minRemainingPercent: 10 }, plan: 'Unknown' }), alternative('chosen', { alwaysApprove: false })];
  const plans = [{ id: 'ChatGPT', readable: true, leftPct: 20 }, { id: 'Grok', readable: true, leftPct: 80 }, { id: 'Low', readable: true, leftPct: 5 }];
  const eligible = model => model.id !== 'wrong-role';
  const result = resolveUsageFallback(lead, candidates, plans, eligible);
  assert.equal(result.id, 'chosen'); assert.equal(result.alwaysApprove, false, 'Target permissions remain intact');
  assert.equal(resolveUsageFallback(lead, candidates.slice(0, -1), plans, eligible), null);
  assert.equal(resolveUsageFallback(lead, candidates, [{ ...plans[0], leftPct: 21 }, ...plans.slice(1)], eligible).id, lead.id, 'No early switch');
});
test('permission restrictions and privacy-safe categorized reports', () => {
  const saved = process.env.PLANWHARF_CODEX;
  process.env.PLANWHARF_CODEX = process.execPath;
  try {
    const spec = launchSpec({ ...models[0], alwaysApprove: false }, os.tmpdir(), 'Fixture only');
    assert.ok(spec.args.includes('workspace-write'));
    assert.ok(!spec.args.includes('--dangerously-bypass-approvals-and-sandbox'));
    assert.ok(spec.args.includes('sandbox_workspace_write.network_access=false'));
    assert.throws(() => launchSpec({ id: 'agy', provider: 'antigravity', cli: 'gemini-3.1-pro-high', alwaysApprove: false }, os.tmpdir(), 'Fixture'), /PERMISSION_MODE_UNSUPPORTED/);
  } finally { if (saved === undefined) delete process.env.PLANWHARF_CODEX; else process.env.PLANWHARF_CODEX = saved; }
  const diagnostic = diagnoseProvider(models[0], 'unknown flag --old token secret personal prompt', 2);
  assert.equal(diagnostic.code, 'PROVIDER_CLI_INCOMPATIBLE');
  assert.ok(!JSON.stringify(diagnostic).includes('secret'));
  assert.equal(diagnoseProvider(models[0], 'Missing or invalid structured worker result.').code, 'PROVIDER_PROTOCOL_CHANGED');
});
test('a failed quota refresh cannot keep authorizing calls using stale percentages', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-stale-policy-'));
  const plan = require('../plan.js').example(); plan.tasks = [];
  let broken = false;
  const orchestra = createOrchestrator({ readPlan: async () => plan, models, attempts: createAttemptEngine({ dir }), plansTtl: 0,
    fetchPlans: async () => { if (broken) throw Error('API schema changed'); return [{ id: 'ChatGPT', readable: true, leftPct: 90 }]; } });
  try {
    await orchestra.setModelPolicy('lead', { ...policy, fallbackModelId: null });
    await orchestra.setOrchestrator('lead');
    assert.equal((await orchestra.tick()).orchestratorId, 'lead');
    broken = true;
    const blocked = await orchestra.tick(); assert.equal(blocked.orchestratorId, null); assert.equal(blocked.usageAlerts[0].state, 'unread');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('policy API persists, lead and approved workers use automatic fallback without inference', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-policy-'));
  const file = path.join(dir, 'notebook.json');
  const plan = structuredClone(require('../fixtures/starter-notebook.json'));
  plan.projects = [plan.projects[0]]; plan.projects[0].project = { id: 'isolated', name: 'Isolated policy', folder: dir }; plan.projects[0].tasks = [];
  fs.writeFileSync(file, JSON.stringify(plan));
  let server, base, used = 75, reply = null;
  const launches = [], live = new Set();
  async function start() {
    server = createServer({ planFile: file, attemptsDir: path.join(dir, 'attempts'), models,refreshModels: false, monitorIntervalMs: 60000,
      fetchPlans: async () => [{ id: 'ChatGPT', readable: true, leftPct: 100 - used }, { id: 'Grok', readable: true, leftPct: 95 }],
      inspectFolder: () => ({ git: false, dirty: [], names: [] }), askModel: () => { if (reply) return reply; throw new Error('Inference forbidden'); },
      spawnWorker: info => { launches.push(info); const pid = 1000 + launches.length; live.add(pid); return { pid }; }, isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid) });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = 'http://127.0.0.1:' + server.address().port;
  }
  async function request(route, value, status = 200) {
    const response = await fetch(base + route, value === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    const data = await response.json(); assert.equal(response.status, status, JSON.stringify(data)); return data;
  }
  try {
    await start();
    await request('/api/models/policy', { id: 'lead', policy: { ...policy, fallbackModelId: 'missing' } }, 400);
    await request('/api/models/policy', { id: 'lead', policy });
    await request('/api/orchestrator/lead', { id: 'lead' });
    const warning = await request('/api/orchestrator'); assert.equal(warning.usageAlerts[0].state, 'warning');
    await request('/api/providers/policy', { id: 'codex', policy: { fallbackProviderId: '@automatic' } });
    reply = { answer: 'Provider interface changed', failure: 'protocol', diagnostic: diagnoseProvider(models[0], 'PROVIDER_PROTOCOL_CHANGED'), actions: [{ type: 'dispatch_research', projectId: 'isolated', modelId: 'lead', title: 'Must not run', detail: 'Rejected provider action' }] };
    const failed = await request('/api/orchestrator/ask', { question: 'Please delegate research on this project', projectId: 'isolated' });
    assert.equal(failed.asks[0].diagnostic.code, 'PROVIDER_PROTOCOL_CHANGED');
    assert.equal(failed.snapshot.researches.length, 0, 'A failed provider response cannot create research proposals');
    assert.equal(launches.length, 0);
    reply = null;
    await new Promise(resolve => server.close(resolve)); used = 80; await start();
    const view = await request('/api/orchestrator');
    assert.deepEqual(view.models.find(m => m.id === 'lead').policy, { ...policy, fallbackModelId: null });
    assert.equal(view.providers.find(p => p.id === 'codex').policy.fallbackProviderId, '@automatic');
    assert.equal(view.orchestratorId, 'fallback'); assert.equal(view.selectedOrchestratorId, 'lead'); assert.equal(view.orchestratorFallbackFrom, 'lead');
    const proposal = await request('/api/orchestrator/propose', { projectId: 'isolated', title: 'Policy probe', goal: 'No inference', subTasks: [{ title: 'Harmless mock', detail: 'Fixture only', assignedModelId: 'lead', type: 'IMPLEMENTATION', dependsOn: [] }] }, 201);
    await request('/api/orchestrator/approve', { projectId: 'isolated', megaTaskId: proposal.snapshot.projects[0].megaTaskId });
    assert.equal(launches.length, 1); assert.equal(launches[0].worker.id, 'fallback');
    const exported = await request('/api/project-export?projectId=isolated'); assert.deepEqual(exported.settings.modelPolicies.lead, { ...policy, fallbackModelId: null });
    assert.equal(exported.settings.providerPolicies.codex.fallbackProviderId, '@automatic');
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
