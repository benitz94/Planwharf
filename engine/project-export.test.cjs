'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Plan = require('../plan.js');
const { exportProject, importProject } = require('./project-export.cjs');
const { createServer } = require('../server.cjs');

function sampleProject() {
  return {
    schema: 1,
    project: { id: 'harbor-library', name: 'Harbor library', folder: 'D:\\benit\\Desktop\\Projects\\Harbor' },
    revisions: [{ id: 1, mode: 'manual', title: 'Start', createdAt: '2026-10-01T00:00:00.000Z', direction: 'Open a small library.', state: 'Location chosen.', closed: ['Free borrowing.'], open: ['Saturday hours?'] }],
    tasks: [{ id: 'schedule', projectId: 'harbor-library', revision_id: 1, title: 'Prepare schedule', detail: 'Four shifts.', status: 'running' }],
    home: { notes: [{ id: 'n1', text: 'Ask volunteers.', createdAt: '2026-10-02T00:00:00.000Z', pinned: true, aiRelevant: false }] }
  };
}

function sampleExecution() {
  return {
    megaTasks: [{ id: 'MT-old', projectId: 'harbor-library', taskId: 'schedule', status: 'RUNNING', title: 'Prepare schedule', goal: 'Four shifts.', credentials: 'secret', jobFingerprint: 'abc' }],
    subTasks: [{ id: 'ST-1', megaTaskId: 'MT-old', title: 'Draft hours', status: 'COMPLETE' }],
    results: [{ megaTaskId: 'MT-old', summary: 'Drafted hours', workerTail: 'raw log', token: 'secret' }],
    checkpoints: [{ id: 'CP-1', projectId: 'harbor-library', megaTaskId: 'MT-old', summary: '1/1 slices complete.', createdAt: '2026-10-03T00:00:00.000Z' }],
    timeline: [{ id: 'EV-1', projectId: 'harbor-library', type: 'CHECKPOINT_SAVED', summary: 'Paused.', createdAt: '2026-10-03T00:00:00.000Z' }]
  };
}

test('export marks restore supported and still strips secrets', () => {
  const bundle = exportProject(sampleProject(), sampleExecution());
  assert.equal(bundle.format, 'planwharf-project-export');
  assert.equal(bundle.schema, 1);
  assert.equal(bundle.restoreSupported, true);
  assert.equal(bundle.execution.megaTasks.length, 1);
  assert.ok(!JSON.stringify(bundle).includes('secret'));
  assert.ok(!JSON.stringify(bundle).includes('raw log'));
});

test('import creates a new notebook project and remaps paused execution', () => {
  const notebook = structuredClone(require('../fixtures/starter-notebook.json'));
  const original = JSON.stringify(notebook);
  const bundle = exportProject(sampleProject(), sampleExecution());
  const result = importProject(notebook, bundle, {});
  assert.equal(JSON.stringify(notebook), original, 'Source notebook is not mutated');
  assert.equal(result.project.project.id, 'harbor-library');
  assert.notEqual(result.project.project.folder, sampleProject().project.folder, 'Foreign folder is not copied until relinked');
  assert.equal(result.project.revisions[0].direction, 'Open a small library.');
  assert.equal(result.project.tasks[0].status, 'paused');
  assert.equal(result.project.tasks[0].projectId, result.project.project.id);
  assert.equal(result.project.home.notes[0].text, 'Ask volunteers.');
  assert.equal(result.notebook.projects.length, 2);
  assert.equal(result.notebook.projects[0].project.id, 'first-project');
  assert.equal(result.execution.megaTasks[0].projectId, result.project.project.id);
  assert.notEqual(result.execution.megaTasks[0].id, 'MT-old');
  assert.equal(result.execution.megaTasks[0].status, 'PAUSED');
  assert.equal(result.execution.subTasks[0].megaTaskId, result.execution.megaTasks[0].id);
  assert.notEqual(result.execution.subTasks[0].id, 'ST-1');
  assert.equal(result.execution.checkpoints[0].megaTaskId, result.execution.megaTasks[0].id);
  assert.ok(!JSON.stringify(result.execution).includes('secret'));
});

test('import twice keeps both restored executions', () => {
  let notebook = structuredClone(require('../fixtures/starter-notebook.json'));
  const bundle = exportProject(sampleProject(), sampleExecution());
  const first = importProject(notebook, bundle, {});
  const second = importProject(first.notebook, bundle, {});
  assert.equal(first.project.project.id, 'harbor-library');
  assert.equal(second.project.project.id, 'harbor-library-2');
  assert.notEqual(first.execution.megaTasks[0].id, second.execution.megaTasks[0].id);
  assert.notEqual(first.execution.subTasks[0].id, second.execution.subTasks[0].id);
  assert.equal(first.execution.subTasks[0].megaTaskId, first.execution.megaTasks[0].id);
  assert.equal(second.execution.subTasks[0].megaTaskId, second.execution.megaTasks[0].id);
});

test('import relinks a confirmed folder and rejects a colliding overwrite', () => {
  const notebook = Plan.createProject(structuredClone(require('../fixtures/starter-notebook.json')), { name: 'Harbor library', direction: 'Keep existing.' });
  const bundle = exportProject(sampleProject(), sampleExecution());
  const folder = path.join(os.tmpdir(), 'pw-import-folder-' + Date.now());
  fs.mkdirSync(folder, { recursive: true });
  try {
    const result = importProject(notebook, bundle, { folder });
    assert.equal(result.project.project.id, 'harbor-library-2');
    assert.equal(result.project.project.folder, folder);
    assert.equal(notebook.projects.some(item => item.project.id === 'harbor-library'), true);
  } finally { fs.rmSync(folder, { recursive: true, force: true }); }
});

test('import rejects unknown payloads', () => {
  const notebook = structuredClone(require('../fixtures/starter-notebook.json'));
  assert.throws(() => importProject(notebook, { format: 'other', schema: 1, project: { name: 'X' } }), /schema 1/);
  assert.throws(() => importProject(notebook, null), /schema 1/);
});

test('HTTP import restores one project without touching the other or launching workers', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-import-http-'));
  const plan = structuredClone(require('../fixtures/starter-notebook.json'));
  plan.projects = ['keep-alpha', 'keep-beta'].map(id => ({ ...structuredClone(plan.projects[0]), project: { id, name: id }, tasks: [] }));
  const file = path.join(dir, 'notebook.json'); fs.writeFileSync(file, JSON.stringify(plan));
  let launches = 0;
  const server = createServer({
    planFile: file, attemptsDir: path.join(dir, 'attempts'), skillsEnabled: false, refreshModels: false, monitorIntervalMs: 60000,
    models: [{ id: 'fixture', provider: 'codex', cli: 'gpt-6-luna', enabled: true }],
    spawnWorker: () => { launches++; throw new Error('Import must not launch workers'); },
    inspectFolder: () => ({ git: false, dirty: [] }), fetchPlans: async () => []
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    const version = (await (await fetch(base + '/api/plan')).json()).version;
    const bundle = exportProject(sampleProject(), sampleExecution());
    const response = await fetch(base + '/api/project-import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version, bundle }) });
    const body = await response.json();
    assert.equal(response.status, 201, JSON.stringify(body));
    assert.equal(body.projectId, 'harbor-library');
    assert.equal(body.continue.action, 'resume');
    assert.ok(body.continue.checkpointId);
    assert.equal(launches, 0);
    const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
    assert.deepEqual(saved.projects.map(item => item.project.id).sort(), ['harbor-library', 'keep-alpha', 'keep-beta']);
    const exported = await (await fetch(base + '/api/project-export?projectId=keep-alpha')).json();
    assert.equal(exported.execution.megaTasks.length, 0);
    const restored = await (await fetch(base + '/api/project-export?projectId=harbor-library')).json();
    assert.equal(restored.execution.megaTasks[0].status, 'PAUSED');
    assert.equal((await fetch(base + '/api/project-import', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: 'stale', bundle }) })).status, 409);
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
