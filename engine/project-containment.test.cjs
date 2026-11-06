'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');

async function fixture(t, folderState = 'linked') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  const alpha = path.join(dir, 'app'), beta = path.join(dir, 'application');
  fs.mkdirSync(alpha); fs.mkdirSync(beta);
  const starter = require('../fixtures/starter-notebook.json').projects[0];
  const plan = { schema: 2, projects: ['alpha', 'beta'].map((id, index) => ({ ...structuredClone(starter), project: { id, name: id, folder: index ? beta : folderState === 'unlinked' ? null : folderState === 'missing' ? path.join(dir, 'missing') : alpha }, tasks: [] })) };
  if (folderState === 'unlinked') delete plan.projects[0].project.folder;
  const file = path.join(dir, 'notebook.json'); fs.writeFileSync(file, JSON.stringify(plan));
  const launches = [], inspections = [], asks = [], live = new Set();
  const server = createServer({ planFile: file, attemptsDir: path.join(dir, 'attempts'),refreshModels: false, monitorIntervalMs: 60000,
    models: [{ id: 'fixture', label: 'Fixture', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, limit: 100 }], fetchPlans: async () => [],
    inspectFolder: folder => { inspections.push(folder); return { folder, git: false, dirty: [], names: [], fileCount: 0 }; },
    spawnWorker: info => { launches.push(info); const pid = 1000 + launches.length; live.add(pid); return { pid }; },
    isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid),
    readRepository: async project => { inspections.push(project.folder); return { folder: project.folder, local: {}, remote: {}, comparison: {} }; },
    askModel: async info => { asks.push(info); return { answer: 'Fixture answer.', actions: [] }; }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(async () => { await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); });
  async function request(route, data) {
    const response = await fetch(`http://127.0.0.1:${server.address().port}${route}`, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
    return { status: response.status, body: await response.json() };
  }
  async function propose(projectId = 'alpha') {
    const result = await request('/api/orchestrator/propose', { projectId, title: 'Contained change', goal: 'Use only the linked workspace', subTasks: [{ title: 'Implement fixture', detail: 'No live provider', assignedModelId: 'fixture' }] });
    assert.equal(result.status, 201, JSON.stringify(result.body));
    return result.body.snapshot.projects.find(row => row.id === projectId).megaTaskId;
  }
  return { dir, alpha, beta, file, request, propose, launches, inspections, asks };
}

test('HTTP implementation approval without a folder fails closed and Home asks to relink', async t => {
  const f = await fixture(t, 'unlinked');
  await f.propose();
  const approved = await f.request('/api/orchestrator/approve', { projectId: 'alpha' });
  assert.equal(approved.status, 400);
  assert.match(JSON.stringify(approved.body), /folder/i);
  assert.equal(f.launches.length, 0);
  const home = await f.request('/api/project-home', { projectId: 'alpha' });
  assert.equal(home.status, 200);
  assert.equal(home.body.next, 'Relink the project folder on this PC.');
});

test('HTTP implementation approval with a missing folder rejects before the launcher', async t => {
  const f = await fixture(t, 'missing');
  const megaTaskId = await f.propose();
  const result = await f.request('/api/orchestrator/approve', { projectId: 'alpha', megaTaskId });
  assert.equal(result.status, 400, JSON.stringify(result.body));
  assert.match(JSON.stringify(result.body), /folder|directory/i);
  assert.equal(f.launches.length, 0);
  assert.equal((await f.request('/api/project-home', { projectId: 'alpha' })).body.next, 'Relink the project folder on this PC.');
});

test('selected project approve, inspect, ask, stop and resume leave its sibling untouched', async t => {
  const f = await fixture(t);
  const megaTaskId = await f.propose();
  const betaBefore = JSON.parse(fs.readFileSync(f.file)).projects[1];
  f.inspections.length = 0;
  assert.equal((await f.request('/api/orchestrator/approve', { projectId: 'alpha', megaTaskId })).status, 200);
  assert.equal((await f.request('/api/repository?projectId=alpha')).status, 200);
  await f.request('/api/orchestrator/lead', { id: 'fixture' });
  const answer = await f.request('/api/orchestrator/ask', { projectId: 'alpha', question: 'Explain the current assignment.' });
  assert.equal(answer.status, 200, JSON.stringify(answer.body));
  assert.ok(f.asks.length > 0);
  assert.ok(f.asks.every(info => info.projectId === 'alpha' && info.folder === f.alpha));
  assert.equal((await f.request('/api/orchestrator/stop', { projectId: 'alpha', megaTaskId })).status, 200);
  const cp = (await f.request('/api/execution?projectId=alpha')).body.checkpoints.at(-1);
  assert.equal((await f.request('/api/orchestrator/resume', { projectId: 'alpha', megaTaskId, checkpointId: cp.id, confirmChanges: true })).status, 200);
  assert.ok(f.inspections.length > 0);
  assert.ok(f.inspections.every(folder => folder === f.alpha), JSON.stringify(f.inspections));
  assert.equal(f.launches.length, 2);
  assert.ok(f.launches.every(info => info.projectId === 'alpha' && info.folder === f.alpha));
  assert.deepEqual(JSON.parse(fs.readFileSync(f.file)).projects[1], betaBefore);
  assert.equal((await f.request('/api/execution?projectId=beta')).body.megaTasks.length, 0);
});

for (const linked of [false, true]) test(`HTTP import ${linked ? 'with confirmed folder offers Resume' : 'without folder asks to relink before Resume'}`, async t => {
  const f = await fixture(t);
  const megaTaskId = await f.propose();
  assert.equal((await f.request('/api/orchestrator/approve', { projectId: 'alpha', megaTaskId })).status, 200);
  assert.equal((await f.request('/api/orchestrator/stop', { projectId: 'alpha', megaTaskId })).status, 200);
  const bundle = (await f.request('/api/project-export?projectId=alpha')).body;
  const version = (await f.request('/api/plan')).body.version;
  const count = f.launches.length;
  const result = await f.request('/api/project-import', { version, bundle, ...(linked ? { folder: f.alpha } : {}) });
  assert.equal(result.status, 201, JSON.stringify(result.body));
  assert.equal(result.body.continue.action, 'resume', 'Checkpoint metadata stays import-compatible');
  const id = result.body.projectId;
  const home = await f.request('/api/project-home', { projectId: id });
  assert.equal(home.status, 200, JSON.stringify(home.body));
  assert.equal(home.body.next, linked ? 'Resume from checkpoint.' : 'Relink the project folder on this PC.');
  const snapshot = (await f.request('/api/orchestrator?readOnly=1')).body.projects.find(row => row.id === id);
  assert.equal(snapshot.next, home.body.next, 'Home and Orchestrator share one deterministic Next');
  assert.equal(f.launches.length, count, 'Import and read-only Home never launch');
  if (!linked) {
    const attempt = await f.request('/api/orchestrator/resume', { projectId: id, megaTaskId: result.body.continue.megaTaskId, checkpointId: result.body.continue.checkpointId, confirmChanges: true });
    assert.equal(attempt.status, 400);
    assert.match(JSON.stringify(attempt.body), /folder|directory/i);
    assert.equal(f.launches.length, count);
  }
});

test('folder removed or replaced by a file after proposal blocks approval without launch', async t => {
  const f = await fixture(t);
  const megaTaskId = await f.propose();
  fs.rmdirSync(f.alpha);
  fs.writeFileSync(f.alpha, 'not a directory');
  const approved = await f.request('/api/orchestrator/approve', { projectId: 'alpha', megaTaskId });
  assert.equal(approved.status, 400);
  assert.match(JSON.stringify(approved.body), /folder|directory/i);
  assert.equal(f.launches.length, 0);
  assert.equal((await f.request('/api/project-home', { projectId: 'alpha' })).body.next, 'Relink the project folder on this PC.');
});

test('an explicit command naming beta cannot retarget the selected alpha recipient', async t => {
  const f = await fixture(t);
  const megaTaskId = await f.propose('beta');
  assert.equal((await f.request('/api/orchestrator/approve', { projectId: 'beta', megaTaskId })).status, 200);
  const betaBefore = (await f.request('/api/execution?projectId=beta')).body;
  f.inspections.length = 0;
  for (const question of ['stop beta', 'resume beta', 'approve beta', 'start beta']) {
    const answer = await f.request('/api/orchestrator/ask', { projectId: 'alpha', question });
    assert.equal(answer.status, 400, question);
    assert.match(JSON.stringify(answer.body), /different project|recipient/i);
  }
  assert.equal(f.launches.length, 1);
  assert.equal(f.asks.length, 0);
  assert.deepEqual(f.inspections, []);
  assert.deepEqual((await f.request('/api/execution?projectId=beta')).body, betaBefore);
});
