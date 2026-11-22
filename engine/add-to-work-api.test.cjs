'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');
const { VERIFY_SLICE } = require('./model-roles.cjs');
const { crewOfProject } = require('./phone.cjs');

const model = (id, label, extra = {}) => ({
  id, label, provider: 'codex', adapter: 'codex', cli: 'gpt-' + id, providerCatalog: true,
  plan: 'plus', enabled: true, available: true, cost: 1, limit: 100, kind: 'default',
  capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true },
  roles: extra.roles || { orchestrator: true, worker: true, verifier: true }, ...extra
});

test('a long addition is cut to 120 characters, and work that has not started refuses it', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-api-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const plan = {
    schema: 2,
    projects: [{
      schema: 1, project: { id: 'demo', name: 'demo', folder },
      revisions: [{ id: 1, direction: 'Build demo', open: [] }],
      tasks: [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo', detail: 'Build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          { title: 'Build first', assignedModelId: 'opus', type: 'IMPLEMENTATION', detail: 'Build first' },
          { title: VERIFY_SLICE, assignedModelId: 'astra', type: 'IMPLEMENTATION', detail: VERIFY_SLICE, dependsOn: [0] }
        ]
      }]
    }]
  };
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, models: roster,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => [], plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['a.js'], fileCount: 1 }),
    spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false, killWorker: () => true,
    askModel: async () => ({ answer: 'Noted.' })
  });
  const snap = await orchestra.tick('demo');
  const megaId = snap.projects.find(p => p.id === 'demo').megaTaskId;
  const tooSoon = await orchestra.addToWork('demo', { addition: 'Add a panel.' });
  assert.equal(tooSoon.ok, false);

  const approved = await orchestra.approve('demo', megaId);
  assert.equal(approved.ok, true);
  const long = 'Add ' + 'x'.repeat(200);
  const added = await orchestra.addToWork('demo', { addition: long });
  assert.equal(added.ok, true);
  assert.equal(added.subTask.title.length, 120);
  assert.equal(added.subTask.detail, long);
});

test('phone crew lines include the extra Build step', () => {
  const crew = crewOfProject({
    leadLabel: 'Lead',
    subTasks: [
      { id: 'a', title: 'Build first', assignedModelId: 'opus', status: 'RUNNING' },
      { id: 'b', title: 'Add a logger', assignedModelId: 'opus', status: 'PENDING' },
      { id: 'c', title: VERIFY_SLICE, assignedModelId: 'astra', status: 'PENDING' }
    ]
  }, [{ id: 'opus', label: 'Opus Builder' }, { id: 'astra', label: 'Astra Verifier' }]);
  assert.equal(crew.build.length, 2);
  assert.equal(crew.build[1].name, 'Opus Builder');
  assert.equal(crew.verify.length, 1);
});

test('POST /api/orchestrator/add-to-work validates the body', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-http-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const planPath = path.join(dir, 'plan.json');
  fs.writeFileSync(planPath, JSON.stringify({
    schema: 2,
    projects: [{
      schema: 1, project: { id: 'demo', name: 'Demo', folder },
      revisions: [{ id: 1, mode: 'manual', title: '', createdAt: new Date().toISOString(), direction: 'Ship it', state: '', closed: [], open: [] }],
      tasks: []
    }]
  }));
  const { createServer } = require('../server.cjs');
  const server = createServer({ dir, planFile: planPath, phase2: true });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}/api/orchestrator/add-to-work`;
  try {
    const nonJson = await fetch(url, { method: 'POST', headers: { 'content-type': 'text/plain' }, body: 'hello' });
    assert.equal(nonJson.status, 415);
    const extra = await fetch(url, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ projectId: 'demo', addition: 'Add a helper.', extra: true })
    });
    assert.equal(extra.status, 400);
    const idle = await fetch(url, {
      method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ projectId: 'demo', addition: 'Add a helper.' })
    });
    assert.equal(idle.status, 400);
  } finally {
    server.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
