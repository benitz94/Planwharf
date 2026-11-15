'use strict';
// quotaWatchTick must use each project's team, and must move a running step whose model lost that step's role.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-switch-'));
const folder = path.join(dir, 'src');
fs.mkdirSync(folder);
const windows = [
  { id: 'chatgpt', readable: true, leftPct: 80 },
  { id: 'claude', readable: true, leftPct: 70 },
  { id: 'gemini', readable: true, leftPct: 90 }
];
const model = (id, label, provider, cli, plan) => ({ id, label, provider, cli, plan, enabled: true, available: true, cost: 1, limit: 100, capabilities: { canCode: true, canResearch: true } });
const roster = [
  model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'),
  model('sonnet', 'Sonnet', 'claude', 'claude-sonnet-5', 'claude'),
  model('opus', 'Opus', 'antigravity', 'gemini-3.1-pro', 'gemini')
];
const slice = (title, assignedModelId, type = 'IMPLEMENTATION') => ({ title, assignedModelId, type, detail: title, dependsOn: [] });
const project = (id, tasks) => ({ schema: 1, project: { id, name: id, folder }, revisions: [{ id: 1, direction: 'Ship ' + id, open: [] }], tasks });
const plan = { schema: 2, projects: [
  project('alpha', [{ id: 'job-alpha', projectId: 'alpha', revision_id: 1, title: 'Alpha work', detail: 'Build alpha', status: 'running', executionVersion: 2, source: 'chat', proposedSubTasks: [slice('Alpha build', 'terra')] }]),
  project('beta', [{ id: 'job-beta', projectId: 'beta', revision_id: 1, title: 'Beta work', detail: 'Build beta', status: 'running', executionVersion: 2, source: 'chat', proposedSubTasks: [slice('Beta build', 'opus'), slice('Beta research', 'terra', 'RESEARCH')] }]),
  project('gamma', [{ id: 'job-gamma', projectId: 'gamma', revision_id: 1, title: 'Gamma work', detail: 'Build gamma', status: 'running', executionVersion: 2, source: 'chat', goldModel: { id: 'opus', provider: 'antigravity', cli: 'gemini-3.1-pro' }, proposedSubTasks: [slice('Gamma gold', 'opus')] }])
] };
let pid = 100;
const alive = new Set();
const orchestra = createOrchestrator({
  dir, readPlan: async () => plan, models: roster, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
  fetchPlans: async () => windows.map(item => ({ ...item })), plansTtl: 0,
  inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
  spawnWorker: () => { pid += 1; alive.add(pid); return { pid }; },
  isProcessAlive: id => alive.has(id),
  killWorker: id => { alive.delete(id); return true; }
});

function step(view, projectId, title) {
  return view.projects.find(row => row.id === projectId).subTasks.find(sub => sub.title === title);
}

test('quotaWatchTick uses the project team and moves a step whose model lost its role', async () => {
  for (const id of ['terra', 'sonnet', 'opus']) assert.equal((await orchestra.setModelRoles(id, { worker: true, verifier: true })).ok, true);
  await orchestra.tick();
  for (const id of ['alpha', 'beta', 'gamma']) {
    const row = (await orchestra.snapshot()).projects.find(item => item.id === id);
    const approved = await orchestra.approve(id, row.megaTaskId);
    assert.equal(approved.ok, true, approved.reason || id);
  }
  let view = await orchestra.snapshot();
  assert.equal(step(view, 'alpha', 'Alpha build').assignedModelId, 'terra');
  assert.equal(step(view, 'alpha', 'Alpha build').status, 'RUNNING');
  assert.equal(step(view, 'beta', 'Beta build').assignedModelId, 'opus');
  assert.equal(step(view, 'beta', 'Beta build').status, 'RUNNING');
  assert.equal(step(view, 'beta', 'Beta research').type, 'RESEARCH');
  assert.equal(step(view, 'beta', 'Beta research').status, 'RUNNING');
  const gold = step(view, 'gamma', 'Gamma gold');
  assert.equal(gold.status, 'RUNNING');
  assert.ok(gold.goldModel, 'a gold step carries its gold model');
  assert.equal(gold.assignedModelId, 'opus');

  // Own teams diverge from the shared team after the steps are already running.
  assert.equal((await orchestra.setModelRoles('opus', { worker: false }, 'alpha')).ok, true);
  assert.equal((await orchestra.setModelRoles('opus', { worker: false }, 'beta')).ok, true);
  assert.equal((await orchestra.setModelRoles('terra', { worker: false }, 'beta')).ok, true);
  assert.equal((await orchestra.setModelRoles('opus', { worker: false }, 'gamma')).ok, true);
  windows.find(item => item.id === 'chatgpt').leftPct = 12;

  await orchestra.quotaWatchTick();
  view = await orchestra.snapshot();
  const alpha = step(view, 'alpha', 'Alpha build');
  const beta = step(view, 'beta', 'Beta build');
  const research = step(view, 'beta', 'Beta research');
  const stayed = step(view, 'gamma', 'Gamma gold');
  // Shared team would pick Opus (90% left, still a worker there). Alpha's team does not.
  assert.equal(alpha.assignedModelId, 'sonnet');
  assert.match(view.projects.find(row => row.id === 'alpha').log.map(line => line.text).join('\n'), /Switched Terra to Sonnet: Quota of Terra is at 12%/);
  // Opus still has quota, but Beta's team took its worker role away.
  assert.equal(beta.assignedModelId, 'sonnet');
  assert.match(view.projects.find(row => row.id === 'beta').log.map(line => line.text).join('\n'), /no longer has the worker role in this project's team/);
  assert.equal(research.assignedModelId, 'terra', 'research is not switched');
  assert.equal(research.status, 'RUNNING');
  assert.equal(stayed.assignedModelId, 'opus', 'a gold step is not switched');
  assert.equal(stayed.status, 'RUNNING');
  const shared = await orchestra.snapshot();
  assert.equal(shared.models.find(item => item.id === 'opus').roles.worker, true, 'the shared team is unchanged');
  const alphaTeam = await orchestra.withTeam('alpha', () => orchestra.snapshot());
  assert.equal(alphaTeam.models.find(item => item.id === 'opus').roles.worker, false);
  assert.equal(alphaTeam.ownTeam, true);
});

test.after(async () => {
  try { await orchestra.shutdown?.(); } catch {}
  fs.rmSync(dir, { recursive: true, force: true });
});
