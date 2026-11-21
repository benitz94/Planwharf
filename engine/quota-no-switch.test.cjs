'use strict';
// A model under its limit that nobody can replace: the work stops, and the chat says why each other model cannot take the step.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');

test('a step under its limit with no replacement stops the work, and the chat names why each other model cannot take it', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-no-switch-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'src'); fs.mkdirSync(folder);
  const windows = [{ id: 'claude', readable: true, leftPct: 90 }, { id: 'grok', readable: false }];
  const model = (id, label, provider, cli, plan) => ({ id, label, provider, cli, plan, enabled: true, available: true, cost: 1, limit: 100, policy: { alwaysApprove: true }, capabilities: { canCode: true } });
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'sleep', name: 'sleep', folder }, revisions: [{ id: 1, direction: 'Ship', open: [] }],
    tasks: [{ id: 'job', projectId: 'sleep', revision_id: 1, title: 'Work', detail: 'Build it', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [{ title: 'Build it', assignedModelId: 'opus', type: 'IMPLEMENTATION', detail: 'Build it', dependsOn: [] }] }] }] };
  let pid = 100; const alive = new Set();
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, models: [model('opus', 'Opus 5.5', 'claude', 'claude-opus-5-5', 'claude'), model('grok', 'Grok Build', 'grok', 'grok-4.7', 'grok')],
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => windows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { pid += 1; alive.add(pid); return { pid }; }, isProcessAlive: id => alive.has(id), killWorker: id => { alive.delete(id); return true; } });
  assert.equal((await orchestra.setModelRoles('opus', { worker: true, verifier: true })).ok, true);
  assert.equal((await orchestra.setModelRoles('grok', { worker: false, verifier: true })).ok, true);
  await orchestra.tick();
  const row = (await orchestra.snapshot()).projects.find(item => item.id === 'sleep');
  const approved = await orchestra.approve('sleep', row.megaTaskId);
  assert.equal(approved.ok, true, approved.reason);
  windows[0].leftPct = 20;
  await orchestra.quotaWatchTick();
  const view = await orchestra.snapshot();
  const notes = view.execution.timeline.filter(event => event.type === 'QUOTA_STOPPED');
  assert.equal(notes.length, 1);
  assert.equal(notes[0].summary, 'Stopped: Opus 5.5 is under its quota limit on Build and no other model can take the step (Grok Build: no Build role in this project). Resume when quota is back, or give the role to a model above its limit in Team.');
  const after = view.projects.find(item => item.id === 'sleep');
  assert.equal(after.megaTask.status, 'PAUSED', 'the work is stopped, with a checkpoint to resume from');
  assert.equal(alive.size, 0, 'no worker is left running');
});

test('a new step never starts on a model under its limit: with nobody above it, the step waits for quota', async () => {
  const { pickWorker } = require('./orchestrator.js');
  const models = [{ id: 'opus', label: 'Opus', provider: 'claude', cli: 'claude-opus-5-5', plan: 'claude', enabled: true, available: true, cost: 1, limit: 100, providerCatalog: true, roles: { worker: true }, capabilities: { canCode: true } }];
  assert.equal(pickWorker(models, { job: { title: 'Build it', kind: 'build' }, low: () => true }), null);
  assert.equal(pickWorker(models, { job: { title: 'Build it', kind: 'build' }, low: () => false }).id, 'opus');
});

test('a Verify step waiting its turn moves off a model that fell under its limit, before it starts', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-wait-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'src'); fs.mkdirSync(folder);
  const windows = [{ id: 'chatgpt', readable: true, leftPct: 90 }, { id: 'claude', readable: true, leftPct: 90 }, { id: 'gemini', readable: true, leftPct: 90 }];
  const model = (id, label, provider, cli, plan) => ({ id, label, provider, cli, plan, enabled: true, available: true, cost: 1, limit: 100, kind: 'premium', policy: { alwaysApprove: true }, capabilities: { canCode: true } });
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p', name: 'p', folder }, revisions: [{ id: 1, direction: 'Ship', open: [] }],
    tasks: [{ id: 'job', projectId: 'p', revision_id: 1, title: 'Work', detail: 'Build it', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [{ title: 'Build it', assignedModelId: 'terra', type: 'IMPLEMENTATION', detail: 'Build it', dependsOn: [] },
        { title: 'Verify the work independently against the job', assignedModelId: 'opus', type: 'IMPLEMENTATION', detail: 'Check it', dependsOn: [0] }] }] }] };
  let pid = 100; const alive = new Set();
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan,
    models: [model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'), model('opus', 'Opus 5.5', 'claude', 'claude-opus-5-5', 'claude'), model('gemini', 'Gemini Pro', 'antigravity', 'gemini-3.1-pro', 'gemini')],
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => windows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { pid += 1; alive.add(pid); return { pid }; }, isProcessAlive: id => alive.has(id), killWorker: id => { alive.delete(id); return true; } });
  for (const id of ['terra', 'opus', 'gemini']) assert.equal((await orchestra.setModelRoles(id, { worker: true, verifier: true })).ok, true);
  await orchestra.tick();
  const row = (await orchestra.snapshot()).projects.find(item => item.id === 'p');
  const approved = await orchestra.approve('p', row.megaTaskId);
  assert.equal(approved.ok, true, approved.reason);
  let view = await orchestra.snapshot();
  const verify = () => view.projects[0].subTasks.find(sub => /^Verify/.test(sub.title));
  assert.equal(verify().status, 'PENDING');
  assert.equal(verify().assignedModelId, 'opus');
  windows[1].leftPct = 20; // Claude under its limit while the build runs.
  await orchestra.quotaWatchTick();
  view = await orchestra.snapshot();
  assert.equal(verify().assignedModelId, 'gemini', 'moved before it starts, to a model above its limit');
  assert.equal(view.projects[0].subTasks.find(sub => sub.title === 'Build it').assignedModelId, 'terra', 'the running build is not touched');
});

test('with no verifier above its limit, the work still plans its Verify step (it waits for quota)', () => {
  const { pickWorkers } = require('./orchestrator.js');
  const model = (id, provider, cli) => ({ id, label: id, provider, cli, plan: provider, enabled: true, available: true, cost: 1, limit: 100, providerCatalog: true, kind: 'premium', roles: { worker: true, verifier: true }, capabilities: { canCode: true } });
  const crew = pickWorkers([model('a', 'codex', 'gpt-6.1-terra'), model('b', 'claude', 'claude-opus-5-5')], { job: { title: 'Build it', kind: 'build' }, low: item => item.id === 'b' });
  assert.deepEqual(crew.map(item => item.id), ['a', 'b']);
});
