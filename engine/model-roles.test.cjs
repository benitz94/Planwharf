'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');
const { pickWorker, canOrchestrate } = require('./orchestrator.js');
const { withModelRoles } = require('./model-roles.cjs');
test('user role permissions constrain routing independently of model capability', () => {
  const base = { id: 'capable', enabled: true, kind: 'default', orchestratorEligible: true, workerEligible: true };
  const reviewer = withModelRoles(base, { orchestrator: false, worker: false, reviewer: true });
  assert.equal(canOrchestrate(reviewer), false);
  assert.equal(pickWorker([reviewer], { job: { title: 'Implement code' } }), null);
  assert.equal(pickWorker([reviewer], { job: { kind: 'audit', title: 'Review code and tests' } }).id, base.id);
  const lead = withModelRoles(base, { orchestrator: true, worker: false, reviewer: false });
  assert.equal(canOrchestrate(lead), true);
  assert.equal(pickWorker([lead], { job: { kind: 'audit' } }), null);
  assert.equal(pickWorker([lead], { job: { title: 'Implement code' } }), null);
  const inspector = withModelRoles({ ...base, workerEligible: false, reviewerEligible: true, capabilities: { canCode: false } });
  assert.equal(pickWorker([inspector], { job: { kind: 'audit', title: 'Review code and tests' } }).id, base.id);
  const unsupported = withModelRoles({ ...base, orchestratorEligible: false }, { orchestrator: true });
  assert.equal(unsupported.roles.orchestrator, false);
});
test('role settings persist, incompatible roles are rejected, exact audit/implementation assignments honor flags', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-model-roles-'));
  const file = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  const plan = structuredClone(require('../fixtures/starter-notebook.json'));
  plan.projects = [plan.projects[0]]; plan.projects[0].project = { id: 'isolated', name: 'Isolated roles', folder: dir }; plan.projects[0].tasks = [];
  plan.projects.push({ ...structuredClone(plan.projects[0]), project: { id: 'isolated-audit', name: 'Isolated audit', folder: dir } });
  fs.writeFileSync(file, JSON.stringify(plan));
  const models = [{ id: 'capable', provider: 'codex', cli: 'gpt-6-luna', enabled: true, orchestratorEligible: true, workerEligible: true }, { id: 'inspector', provider: 'codex', cli: 'gpt-6-luna', enabled: true, orchestratorEligible: false, workerEligible: false, reviewerEligible: true, capabilities: { canCode: false } }];
  let server, base, calls = 0;
  const launches = [], live = new Set();
  async function start() {
    server = createServer({ planFile: file, attemptsDir, models,refreshModels: false, monitorIntervalMs: 60000, fetchPlans: null,
      inspectFolder: () => ({ git: false, dirty: [], names: [] }), askModel: () => { calls++; return 'Unexpected'; },
      spawnWorker: info => { launches.push(info); const pid = 1000 + launches.length; live.add(pid); return { pid }; }, isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid) });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = 'http://127.0.0.1:' + server.address().port;
  }
  async function request(route, value, status = 200) {
    const response = await fetch(base + route, value === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    const data = await response.json(); assert.equal(response.status, status, JSON.stringify(data)); return data;
  }
  const proposal = async type => (await request('/api/orchestrator/propose', { projectId: type === 'AUDIT' ? 'isolated-audit' : 'isolated', title: 'Role restriction probe', goal: 'Isolated harmless work', subTasks: [{ title: 'Exact role assignment', detail: 'Fixture only', assignedModelId: 'capable', type, dependsOn: [] }] }, 201)).snapshot.projects.find(project => project.id === (type === 'AUDIT' ? 'isolated-audit' : 'isolated')).megaTaskId;
  try {
    await start();
    await request('/api/models/roles', { id: 'inspector', roles: { orchestrator: true } }, 400);
    await request('/api/models/roles', { id: 'capable', roles: { reviewer: 'yes' } }, 400);
    await request('/api/models/roles', { id: 'capable', roles: { admin: true } }, 400);
    await request('/api/orchestrator/lead', { id: 'capable' });
    const roles = { orchestrator: false, worker: false, reviewer: true, supervisor: false };
    const updated = await request('/api/models/roles', { id: 'capable', roles });
    assert.equal(updated.orchestratorId, null); assert.deepEqual(updated.models.find(model => model.id === 'capable').roles, { ...roles, verifier: true });
    await request('/api/orchestrator/lead', { id: 'capable' }, 400);
    await request('/api/orchestrator/model', { id: 'capable', enabled: false });
    const restored = await request('/api/models/roles', { id: 'capable', roles: { reviewer: true } });
    assert.equal(restored.models.find(model => model.id === 'capable').enabled, true, 'Role flags override the obsolete model switch');
    assert.deepEqual(restored.models.find(model => model.id === 'capable').roles, { ...roles, verifier: false });
    await new Promise(resolve => server.close(resolve)); await start();
    assert.deepEqual((await request('/api/orchestrator?readOnly=1')).models.find(model => model.id === 'capable').roles, { ...roles, verifier: false });
    const implementation = await proposal('IMPLEMENTATION');
    await request('/api/orchestrator/approve', { projectId: 'isolated', megaTaskId: implementation }, 400); assert.equal(launches.length, 0);
    const audit = await proposal('AUDIT');
    await request('/api/orchestrator/approve', { projectId: 'isolated-audit', megaTaskId: audit });
    assert.equal(launches.length, 1); assert.equal(launches[0].worker.id, 'capable'); assert.equal(launches[0].job.kind, 'audit');
    const exported = await request('/api/project-export?projectId=isolated'); assert.deepEqual(exported.settings.modelRoles.capable, { ...roles, verifier: false });
    assert.equal(calls, 0, 'Role settings and routing need no model call');
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
