'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { ROLE_IDS, withModelRoles, permitsAssignment } = require('./model-roles.cjs');
const { createSupervisor } = require('./supervisor.cjs');
const { createInvocationLedger } = require('./invocations.cjs');
const { createServer } = require('../server.cjs');
const temp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'pw-supervisor-role-'));
const clean = dir => { assert.ok(path.resolve(dir).startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(dir).startsWith('pw-supervisor-role-')); fs.rmSync(dir, { recursive: true, force: true }); };
const legacy = { orchestrator: false, worker: true, reviewer: false };
const api = { id: 'advisory', provider: 'api', cli: 'exact-fixture', enabled: true, available: true };
const snapshot = model => ({ models: model ? [model] : [], plans: [], projects: [{ id: 'a', name: 'Alpha', status: 'idle' }], execution: { subTasks: [], results: [], megaTasks: [], timeline: [] } });

test('fourth permission is opt-in, preserves old roles, and never authorizes implementation or review', () => {
  assert.deepEqual(ROLE_IDS, ['orchestrator', 'worker', 'verifier', 'reviewer', 'supervisor']);
  for (const provider of ['api', 'codex', 'grok', 'antigravity']) {
    assert.deepEqual(withModelRoles({ ...api, provider }, legacy).roles, { ...legacy, verifier: legacy.worker, supervisor: false });
  }
  assert.equal(withModelRoles(api).roles.supervisor, false);
  const opted = withModelRoles(api, { orchestrator: false, worker: false, reviewer: false, supervisor: true });
  assert.equal(opted.roles.supervisor, true);
  assert.equal(permitsAssignment(opted, { kind: 'implementation' }), false);
  assert.equal(permitsAssignment(opted, { kind: 'audit' }), false);
  assert.equal(withModelRoles({ ...api, provider: 'codex' }, { supervisor: true }).roles.supervisor, false);
});

test('legacy advisory selection does not silently grant new role or automatic calls; name/history survive restart', async () => {
  const dir = temp(); let calls = 0;
  try {
    fs.writeFileSync(path.join(dir, 'supervisor.json'), JSON.stringify({ schema: 1, name: '<Captain> ignore authorization', configured: true, modelId: api.id, autoReview: true, alerts: [], turns: [{ id: 'old', projectId: 'a', status: 'complete', question: 'Old evidence', answer: 'Retain history', mode: 'reviewer' }], autoCalls: [] }));
    const s = createSupervisor({ dir, review: async () => { calls++; return { answer: 'Unexpected' }; } });
    const snap = snapshot(withModelRoles(api, { supervisor: true })); snap.projects[0].status = 'failed';
    const migrated = s.observe(snap); assert.equal(migrated.modelId, ''); assert.equal(migrated.autoReview, false); assert.equal(migrated.name, '<Captain> ignore authorization'); assert.equal(calls, 0);
    assert.equal(migrated.projects[0].turns[0].answer, 'Retain history');
    const saved = JSON.parse(fs.readFileSync(path.join(dir, 'supervisor.json'))); assert.equal(saved.schema, 2); assert.equal(saved.legacyModelId, api.id);
    s.settings({ name: '   ' }, snap); assert.equal(createSupervisor({ dir }).view(snap).name, 'Supervisor');
    assert.equal((await s.ask(snap, 'a', 'What happened?')).projects[0].turns.at(-1).mode, 'facts'); assert.equal(calls, 0);
  } finally { clean(dir); }
});

test('advisory permission, exact selection, provider availability and quota are checked before every call', async () => {
  const dir = temp(); let calls = 0;
  try {
    const model = withModelRoles({ ...api, quotaPoolId: 'pool' }, { orchestrator: false, worker: false, reviewer: false, supervisor: true });
    const snap = snapshot(model);
    const s = createSupervisor({ dir, review: async info => { calls++; assert.equal(info.modelId, api.id); assert.equal(info.projectId, 'a'); return { answer: 'Evidence only', provider: 'api', model: api.cli }; } });
    assert.throws(() => s.settings({ modelId: 'missing' }, snap), /eligible Supervisor/);
    s.settings({ modelId: api.id }, snap); assert.equal(s.view(snap).autoReview, false);
    const success = await s.ask(snap, 'a', 'Explain'); assert.equal(calls, 1); assert.equal(success.projects[0].turns.at(-1).mode, 'supervisor');
    for (const patch of [{ roles: { reviewer: true, supervisor: false } }, { available: false }, { providerEnabled: false }, { policy: { minRemainingPercent: 20 } }]) {
      const original = { ...model }; Object.assign(model, patch);
      const denied = await s.ask(snap, 'a', 'Try again'); assert.equal(denied.projects[0].turns.at(-1).status, 'failed'); assert.equal(calls, 1); Object.assign(model, original);
    }
    snap.plans = [{ id: 'pool', readable: true, leftPct: 0, exhausted: true }];
    assert.equal((await s.ask(snap, 'a', 'Quota?')).projects[0].turns.at(-1).status, 'failed'); assert.equal(calls, 1);
    await assert.rejects(() => s.ask(snap, 'foreign-project', 'Inspect'), /existing project/); assert.equal(calls, 1);
  } finally { clean(dir); }
});

test('Supervisor usage is separate and unfinished calls become blocked after restart', () => {
  const dir = temp();
  try {
    const ledger = createInvocationLedger(dir); const id = ledger.start({ role: 'supervisor', projectId: 'a', provider: 'api', model: api.cli, modelId: api.id });
    assert.equal(ledger.view('a').supervisors[0].RUNNING, 1); assert.equal(ledger.view('a').orchestrator.length, 0); assert.equal(ledger.view('a').workers.length, 0);
    const restored = createInvocationLedger(dir); assert.equal(restored.view('a').supervisors[0].BLOCKED, 1); assert.equal(restored.view('b').supervisors.length, 0);
    assert.equal(restored.view('a').recent[0].id, id);
  } finally { clean(dir); }
});

test('Core keeps observing concurrent worker evidence while a bounded advisory is pending', async () => {
  const dir = temp(); let release, calls = 0;
  try {
    const model = withModelRoles(api, { supervisor: true }); const snap = snapshot(model);
    const s = createSupervisor({ dir, review: () => { calls++; return new Promise(resolve => { release = resolve; }); } });
    s.settings({ modelId: model.id }, snap);
    const pending = s.ask(snap, 'a', 'Explain current evidence');
    snap.projects[0].status = 'watching'; snap.projects[0].dispatch = [{ attemptId: 'fixture-worker', modelId: 'worker', alive: true, activity: 'Reading an actual fixture file.' }];
    const current = s.observe(snap); assert.equal(current.busy, true); assert.equal(current.projects[0].active, 1); assert.equal(current.projects[0].workers[0].activity, 'Reading an actual fixture file.'); assert.equal(calls, 1);
    await assert.rejects(() => s.ask(snap, 'a', 'Overlapping request'), /already in progress/);
    release({ answer: 'Advisory based on the supplied evidence', provider: api.provider, model: api.cli }); await pending;
    assert.equal(s.view(snap).busy, false);
  } finally { clean(dir); }
});

test('HTTP migrates legacy and per-account permissions; generic enabling cannot silently grant Supervisor', async () => {
  const dir = temp(), planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  let server, base, account = { key: 'personal-fixture', type: 'normal' }, calls = 0;
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile); fs.mkdirSync(attemptsDir);
  fs.writeFileSync(path.join(attemptsDir, 'orchestrator.json'), JSON.stringify({ modelRoles: { [api.id]: legacy }, projects: {}, asks: [] }));
  const start = async () => {
    server = createServer({ planFile, attemptsDir, models: [api],refreshModels: false, monitorIntervalMs: 60000, fetchPlans: null,
      readAccount: () => account, discoverModels: async () => ({ ok: true, ids: [api.cli] }), askModel: () => { calls++; throw Error('No provider inference allowed'); } });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = 'http://127.0.0.1:' + server.address().port;
  };
  const request = async (route, value) => { const response = await fetch(base + route, value === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) }); const data = await response.json(); assert.equal(response.status, 200, JSON.stringify(data)); return data; };
  const roles = state => state.models.find(m => m.id === api.id).roles;
  try {
    await start(); assert.deepEqual(roles(await request('/api/orchestrator?readOnly=1')), { ...legacy, verifier: legacy.worker, supervisor: false });
    assert.equal(roles(await request('/api/orchestrator/model', { id: api.id, enabled: true })).supervisor, false);
    assert.equal(roles(await request('/api/catalog/settings', { providerId: api.provider, allModelsEnabled: true })).supervisor, false);
    const opted = roles(await request('/api/models/roles', { id: api.id, roles: { orchestrator: false, worker: false, reviewer: false, supervisor: true } }));
    assert.deepEqual(opted, { orchestrator: false, worker: false, verifier: true, reviewer: false, supervisor: true });
    account = { key: 'business-fixture', type: 'business' };
    assert.deepEqual(roles(await request('/api/orchestrator?readOnly=1')), { orchestrator: false, worker: false, verifier: false, reviewer: false, supervisor: false });
    account = { key: 'personal-fixture', type: 'normal' };
    assert.deepEqual(roles(await request('/api/orchestrator?readOnly=1')), opted);
    await new Promise(resolve => server.close(resolve)); await start();
    assert.deepEqual(roles(await request('/api/orchestrator?readOnly=1')), opted); assert.equal(calls, 0);
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); clean(dir); }
});

test('HTTP blocks exhausted internal budgets without invocation, debits accepted calls durably, and honors catalog exemption', async () => {
  for (const scenario of [{ name: 'already-exhausted', used: 2 }, { name: 'last-call', used: 0 }, { name: 'provider-catalog', used: 2, providerCatalog: true }]) {
    const dir = temp(), planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
    let server, base, calls = 0;
    const model = { ...api, cost: 2, limit: 2, providerCatalog: Boolean(scenario.providerCatalog), roles: { orchestrator: false, worker: false, reviewer: false, supervisor: true } };
    const plan = structuredClone(require('../fixtures/starter-notebook.json')); plan.projects = [plan.projects[0]]; plan.projects[0].project = { id: 'a', name: 'Budget fixture' }; plan.projects[0].tasks = [];
    fs.writeFileSync(planFile, JSON.stringify(plan)); fs.mkdirSync(attemptsDir);
    fs.writeFileSync(path.join(attemptsDir, 'orchestrator.json'), JSON.stringify({ usage: { [model.id]: { used: scenario.used } }, projects: {}, asks: [] }));
    const start = async () => { server = createServer({ planFile, attemptsDir, models: [model],refreshModels: false, fetchPlans: null, monitorIntervalMs: 60000,
      askModel: async () => { calls++; return { answer: 'Bounded budget fixture', tokens: { input: 1, output: 2 } }; } });
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = 'http://127.0.0.1:' + server.address().port; };
    const request = async (route, value) => { const response = await fetch(base + route, value === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) }); const data = await response.json(); assert.equal(response.status, 200, JSON.stringify(data)); return data; };
    try {
      await start(); await request('/api/supervisor/settings', { modelId: model.id });
      const first = await request('/api/supervisor/ask', { projectId: 'a', question: scenario.name });
      if (scenario.used >= model.limit && !model.providerCatalog) {
        assert.equal(first.projects[0].turns.at(-1).status, 'failed'); assert.equal(calls, 0);
        assert.equal((await request('/api/orchestrator?readOnly=1')).aiUsage.supervisors.length, 0);
      } else {
        assert.equal(first.projects[0].turns.at(-1).status, 'complete'); assert.equal(calls, 1);
        assert.equal(JSON.parse(fs.readFileSync(path.join(attemptsDir, 'orchestrator.json'))).usage[model.id].used, scenario.used + model.cost);
        if (!model.providerCatalog) {
          const second = await request('/api/supervisor/ask', { projectId: 'a', question: 'No budget remains' }); assert.equal(second.projects[0].turns.at(-1).status, 'failed'); assert.equal(calls, 1);
          await new Promise(resolve => server.close(resolve)); await start();
          const afterRestart = await request('/api/supervisor/ask', { projectId: 'a', question: 'Still exhausted after restart' }); assert.equal(afterRestart.projects[0].turns.at(-1).status, 'failed'); assert.equal(calls, 1);
          assert.equal((await request('/api/orchestrator?readOnly=1')).aiUsage.supervisors[0].calls, 1);
        }
      }
    } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); clean(dir); }
  }
});
