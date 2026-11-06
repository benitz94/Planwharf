'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');
const { pickWorker } = require('./orchestrator.js');
const { permitsAssignment } = require('./model-roles.cjs');

test('Gold pins exact provider/model for workers while reviewers keep their roles', () => {
  const models = ['default', 'gold'].map(id => ({ id, provider: 'codex', modelRouting: 'configured', cli: id, enabled: true, available: true, kind: 'default' }));
  const goldModel = { id: 'gold', provider: 'codex', cli: 'gold' };
  assert.equal(pickWorker(models, { job: { title: 'Implement code', goldModel } }).id, 'gold');
  assert.equal(pickWorker(models, { job: { title: 'Implement code' } }).id, 'default');
  assert.equal(pickWorker(models, { job: { kind: 'audit', goldModel } }).id, 'default');
  assert.equal(pickWorker(models.map(model => ({ ...model, available: model.id !== 'gold' })), { job: { goldModel } }), null);
  assert.equal(permitsAssignment({ ...models[1], cli: 'changed' }, { goldModel }), false);
  assert.equal(permitsAssignment({ ...models[1], provider: 'different' }, { goldModel }), false);
});

test('bulk roles persist independently; Gold survives restart and blocks quota fallback and reassignment', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-gold-'));
  const file = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  const plan = structuredClone(require('../fixtures/starter-notebook.json'));
  plan.projects = [plan.projects[0]];
  plan.projects[0].project = { id: 'isolated', name: 'Isolated Gold', folder: dir };
  plan.projects[0].tasks = []; plan.projects[0].revisions.at(-1).open = [];
  fs.writeFileSync(file, JSON.stringify(plan));
  const models = ['default', 'gold'].map(id => ({ id, provider: id === 'gold' ? 'grok' : 'codex', plan: id, modelRouting: 'configured', cli: 'fixture-' + id, enabled: true, available: true, kind: 'default', limit: 100 }));
  models.push({ id: 'incompatible', provider: 'codex', cli: 'fixture-inspector', modelRouting: 'configured', enabled: true, workerEligible: false, orchestratorEligible: false, reviewerEligible: true });
  let server, base, remaining = 80;
  const launches = [], live = new Set(), leadCalls = [];
  async function start() {
    server = createServer({ planFile: file, attemptsDir, models,refreshModels: false, monitorIntervalMs: 60000,
      fetchPlans: async () => models.slice(0, 2).map(model => ({ id: model.id === 'gold' ? 'Grok' : 'ChatGPT', readable: true, leftPct: model.id === 'gold' ? remaining : 80 })),
      inspectFolder: () => ({ git: false, dirty: [], names: [] }),
      readRepository: () => ({ local: {}, remote: {}, comparison: {} }),
      askModel: info => { leadCalls.push(info); return { answer: 'Isolated plan discussion.' }; },
      spawnWorker: info => { launches.push(info); const pid = 1000 + launches.length; live.add(pid); return { pid }; },
      isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid) });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    base = 'http://127.0.0.1:' + server.address().port;
  }
  async function request(route, input, status = 200) {
    const response = await fetch(base + route, input === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) });
    const value = await response.json(); assert.equal(response.status, status, JSON.stringify(value)); return value;
  }
  try {
    await start();
    await request('/api/models/roles/bulk', { role: 'worker', enabled: 'yes' }, 400);
    await request('/api/models/roles/bulk', { role: 'invalid', enabled: true }, 400);
    await request('/api/models/gold', { id: 'incompatible' }, 400);
    await request('/api/models/gold', { id: 'missing' }, 400);
    for (const role of ['worker', 'reviewer', 'orchestrator']) {
      const cleared = await request('/api/models/roles/bulk', { role, enabled: false });
      assert.ok(cleared.models.every(model => !model.roles[role]));
      const selected = await request('/api/models/roles/bulk', { role, enabled: true });
      assert.ok(selected.models.every(model => model.roles[role] === model.roleEligibility[role]));
    }
    await request('/api/models/gold', { id: 'gold' });
    await request('/api/orchestrator/lead', { id: 'default' });
    await request('/api/orchestrator/ask', { question: 'Implement an isolated fixture change', projectId: 'isolated', requestMode: 'invalid' }, 400);
    const asked = await request('/api/orchestrator/ask', { question: 'Implement an isolated fixture change', projectId: 'isolated', requestMode: 'gold' });
    assert.equal(asked.snapshot.asks[0].goldModel.id, 'gold');
    assert.equal(leadCalls[0].model.id, 'default');
    assert.equal(leadCalls[0].state.currentRequest.goldModel.id, 'gold');
    assert.equal(launches.length, 0, 'Sending prepares a task but does not launch workers');
    const job = JSON.parse(fs.readFileSync(file)).projects[0].tasks.at(-1);
    assert.deepEqual(job.goldModel, { id: 'gold', provider: 'grok', cli: 'fixture-gold' });
    const megaTaskId = asked.snapshot.projects[0].megaTaskId;
    assert.ok(megaTaskId);
    await new Promise(resolve => server.close(resolve)); await start();
    assert.equal((await request('/api/orchestrator?readOnly=1')).goldModelId, 'gold');
    await request('/api/providers/policy', { id: 'grok', policy: { minRemainingPercent: 90, fallbackProviderId: '@automatic' } });
    const blocked = await request('/api/orchestrator/approve', { projectId: 'isolated', megaTaskId }, 400);
    assert.match(blocked.error, /quota|unavailable/);
    assert.equal(launches.length, 0, 'Gold forbids automatic provider fallback');
    await request('/api/providers/policy', { id: 'grok', policy: { minRemainingPercent: null } });
    await request('/api/orchestrator/approve', { projectId: 'isolated', megaTaskId });
    assert.equal(launches.length, 1); assert.equal(launches[0].worker.id, 'gold');
    await request('/api/orchestrator/stop', { projectId: 'isolated', megaTaskId });
    const durable = await request('/api/execution?projectId=isolated');
    assert.equal(durable.subTasks[0].goldModel.id, 'gold');
    const checkpointId = durable.checkpoints.at(-1).id;
    await request('/api/orchestrator/resume', { projectId: 'isolated', megaTaskId, checkpointId, assignments: { [durable.subTasks[0].id]: 'default' } }, 400);
    assert.equal(launches.length, 1, 'Resume cannot replace the pinned worker');
    await request('/api/models/gold', { id: 'default' });
    await request('/api/orchestrator/resume', { projectId: 'isolated', megaTaskId, checkpointId });
    assert.equal(launches.at(-1).worker.id, 'gold', 'Changing preference never changes an existing task');
    await request('/api/orchestrator/stop', { projectId: 'isolated', megaTaskId });
    await request('/api/models/gold', { id: 'gold' });
    await request('/api/orchestrator/grill', { propose: false });
    await request('/api/orchestrator/ask', { question: 'Start', projectId: 'isolated', requestMode: 'gold' });
    assert.equal(JSON.parse(fs.readFileSync(file)).projects[0].tasks.at(-1).goldModel.id, 'gold', 'Gold also binds explicit Start');

  } finally {
    if (server?.listening) await new Promise(resolve => server.close(resolve));
    const absolute = path.resolve(dir);
    assert.ok(absolute.startsWith(path.resolve(os.tmpdir()) + path.sep));
    fs.rmSync(absolute, { recursive: true, force: true });
  }
});
