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

test('quotaWatchTick moves verifier and reviewer steps when models lose respective roles', async t => {
  const testDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-roles-'));
  const testFolder = path.join(testDir, 'src');
  fs.mkdirSync(testFolder);
  const testWindows = [
    { id: 'chatgpt', readable: true, leftPct: 80 },
    { id: 'claude', readable: true, leftPct: 70 },
    { id: 'gemini', readable: true, leftPct: 90 }
  ];
  const testRoster = [
    model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'),
    model('sonnet', 'Sonnet', 'claude', 'claude-sonnet-5', 'claude'),
    model('opus', 'Opus', 'antigravity', 'gemini-3.1-pro', 'gemini')
  ];
  const testPlan = { schema: 2, projects: [
    project('roles-proj', [{
      id: 'job-roles', projectId: 'roles-proj', revision_id: 1, title: 'Roles work', detail: 'Roles test',
      status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [
        slice('Verify the work independently against the job', 'opus'),
        slice('Review security changes', 'terra', 'AUDIT')
      ]
    }])
  ] };
  let testPid = 200;
  const testAlive = new Set();
  const testOrchestra = createOrchestrator({
    dir: testDir, readPlan: async () => testPlan, models: testRoster, attempts: createAttemptEngine({ dir: path.join(testDir, 'attempts') }),
    fetchPlans: async () => testWindows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { testPid += 1; testAlive.add(testPid); return { pid: testPid }; },
    isProcessAlive: id => testAlive.has(id),
    killWorker: id => { testAlive.delete(id); return true; }
  });
  t.after(async () => {
    try { await testOrchestra.shutdown?.(); } catch {}
    fs.rmSync(testDir, { recursive: true, force: true });
  });

  for (const id of ['terra', 'sonnet', 'opus']) {
    assert.equal((await testOrchestra.setModelRoles(id, { worker: true, verifier: true, reviewer: true })).ok, true);
  }
  await testOrchestra.tick();
  const row = (await testOrchestra.snapshot()).projects.find(item => item.id === 'roles-proj');
  const approved = await testOrchestra.approve('roles-proj', row.megaTaskId);
  assert.equal(approved.ok, true, approved.reason);

  let view = await testOrchestra.snapshot();
  const verifyStepBefore = step(view, 'roles-proj', 'Verify the work independently against the job');
  const reviewStepBefore = step(view, 'roles-proj', 'Review security changes');
  assert.equal(verifyStepBefore.assignedModelId, 'opus');
  assert.equal(verifyStepBefore.status, 'RUNNING');
  assert.equal(reviewStepBefore.assignedModelId, 'terra');
  assert.equal(reviewStepBefore.status, 'RUNNING');

  // In project's own team, remove verifier from opus and reviewer from terra
  assert.equal((await testOrchestra.setModelRoles('opus', { verifier: false }, 'roles-proj')).ok, true);
  assert.equal((await testOrchestra.setModelRoles('terra', { reviewer: false }, 'roles-proj')).ok, true);

  await testOrchestra.quotaWatchTick();
  view = await testOrchestra.snapshot();

  const verifyStepAfter = step(view, 'roles-proj', 'Verify the work independently against the job');
  const reviewStepAfter = step(view, 'roles-proj', 'Review security changes');

  assert.equal(verifyStepAfter.assignedModelId, 'sonnet');
  assert.equal(reviewStepAfter.assignedModelId, 'sonnet');

  const logText = view.projects.find(p => p.id === 'roles-proj').log.map(line => line.text).join('\n');
  assert.match(logText, /Opus no longer has the verifier role in this project's team/);
  assert.match(logText, /Terra no longer has the reviewer role in this project's team/);
});

test('quotaWatchTick leaves step in place when no eligible model is above quota', async t => {
  const testDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-no-elig-'));
  const testFolder = path.join(testDir, 'src');
  fs.mkdirSync(testFolder);
  const testWindows = [
    { id: 'chatgpt', readable: true, leftPct: 10 },
    { id: 'claude', readable: true, leftPct: 15 },
    { id: 'gemini', readable: true, leftPct: 90 }
  ];
  const testRoster = [
    model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'),
    model('sonnet', 'Sonnet', 'claude', 'claude-sonnet-5', 'claude'),
    model('opus', 'Opus', 'antigravity', 'gemini-3.1-pro', 'gemini')
  ];
  const testPlan = { schema: 2, projects: [
    project('no-elig-proj', [{
      id: 'job-no-elig', projectId: 'no-elig-proj', revision_id: 1, title: 'No elig work', detail: 'No elig test',
      status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Build piece', 'opus')]
    }])
  ] };
  let testPid = 300;
  const testAlive = new Set();
  const testOrchestra = createOrchestrator({
    dir: testDir, readPlan: async () => testPlan, models: testRoster, attempts: createAttemptEngine({ dir: path.join(testDir, 'attempts') }),
    fetchPlans: async () => testWindows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { testPid += 1; testAlive.add(testPid); return { pid: testPid }; },
    isProcessAlive: id => testAlive.has(id),
    killWorker: id => { testAlive.delete(id); return true; }
  });
  t.after(async () => {
    try { await testOrchestra.shutdown?.(); } catch {}
    fs.rmSync(testDir, { recursive: true, force: true });
  });

  for (const id of ['terra', 'sonnet', 'opus']) {
    assert.equal((await testOrchestra.setModelRoles(id, { worker: true })).ok, true);
  }
  await testOrchestra.tick();
  const row = (await testOrchestra.snapshot()).projects.find(item => item.id === 'no-elig-proj');
  const approved = await testOrchestra.approve('no-elig-proj', row.megaTaskId);
  assert.equal(approved.ok, true);

  // Opus loses worker role in no-elig-proj, but both terra (10%) and sonnet (15%) are below threshold (35%)
  assert.equal((await testOrchestra.setModelRoles('opus', { worker: false }, 'no-elig-proj')).ok, true);

  await testOrchestra.quotaWatchTick();
  const view = await testOrchestra.snapshot();
  const sub = step(view, 'no-elig-proj', 'Build piece');
  // Since no other worker is above quota, step remains on opus
  assert.equal(sub.assignedModelId, 'opus');
  assert.equal(sub.status, 'RUNNING');
});

test('quotaWatchTick catches reassignRemaining error without crashing', async t => {
  const testDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-err-'));
  const testFolder = path.join(testDir, 'src');
  fs.mkdirSync(testFolder);
  const testWindows = [
    { id: 'chatgpt', readable: true, leftPct: 80 },
    { id: 'claude', readable: true, leftPct: 70 },
    { id: 'gemini', readable: true, leftPct: 90 }
  ];
  const testRoster = [
    model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'),
    model('sonnet', 'Sonnet', 'claude', 'claude-sonnet-5', 'claude'),
    model('opus', 'Opus', 'antigravity', 'gemini-3.1-pro', 'gemini')
  ];
  const testPlan = { schema: 2, projects: [
    project('err-proj', [{
      id: 'job-err', projectId: 'err-proj', revision_id: 1, title: 'Err work', detail: 'Err test',
      status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Unkillable build', 'opus')]
    }])
  ] };
  let testPid = 400;
  const testAlive = new Set();
  let failKill = false;
  const testOrchestra = createOrchestrator({
    dir: testDir, readPlan: async () => testPlan, models: testRoster, attempts: createAttemptEngine({ dir: path.join(testDir, 'attempts') }),
    fetchPlans: async () => testWindows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { testPid += 1; testAlive.add(testPid); return { pid: testPid }; },
    isProcessAlive: id => testAlive.has(id),
    killWorker: id => {
      if (failKill) return false;
      testAlive.delete(id);
      return true;
    }
  });
  t.after(async () => {
    failKill = false;
    try { await testOrchestra.shutdown?.(); } catch {}
    fs.rmSync(testDir, { recursive: true, force: true });
  });

  for (const id of ['terra', 'sonnet', 'opus']) {
    assert.equal((await testOrchestra.setModelRoles(id, { worker: true })).ok, true);
  }
  await testOrchestra.tick();
  const row = (await testOrchestra.snapshot()).projects.find(item => item.id === 'err-proj');
  const approved = await testOrchestra.approve('err-proj', row.megaTaskId);
  assert.equal(approved.ok, true);

  // Remove worker role from opus, and fail killWorker
  assert.equal((await testOrchestra.setModelRoles('opus', { worker: false }, 'err-proj')).ok, true);
  failKill = true;

  // quotaWatchTick should catch the error and record a note instead of throwing
  await testOrchestra.quotaWatchTick();
  const view = await testOrchestra.snapshot();
  const logText = view.projects.find(p => p.id === 'err-proj').log.map(line => line.text).join('\n');
  assert.match(logText, /Automatic model switch failed: Could not stop the worker/);
});

test('quotaWatchTick moves step when role is removed in shared team for project without own team', async t => {
  const testDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-shared-'));
  const testFolder = path.join(testDir, 'src');
  fs.mkdirSync(testFolder);
  const testWindows = [
    { id: 'chatgpt', readable: true, leftPct: 80 },
    { id: 'claude', readable: true, leftPct: 70 },
    { id: 'gemini', readable: true, leftPct: 90 }
  ];
  const testRoster = [
    model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'),
    model('sonnet', 'Sonnet', 'claude', 'claude-sonnet-5', 'claude'),
    model('opus', 'Opus', 'antigravity', 'gemini-3.1-pro', 'gemini')
  ];
  const testPlan = { schema: 2, projects: [
    project('shared-proj', [{
      id: 'job-shared', projectId: 'shared-proj', revision_id: 1, title: 'Shared work', detail: 'Shared test',
      status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Shared build', 'opus')]
    }])
  ] };
  let testPid = 500;
  const testAlive = new Set();
  const testOrchestra = createOrchestrator({
    dir: testDir, readPlan: async () => testPlan, models: testRoster, attempts: createAttemptEngine({ dir: path.join(testDir, 'attempts') }),
    fetchPlans: async () => testWindows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { testPid += 1; testAlive.add(testPid); return { pid: testPid }; },
    isProcessAlive: id => testAlive.has(id),
    killWorker: id => { testAlive.delete(id); return true; }
  });
  t.after(async () => {
    try { await testOrchestra.shutdown?.(); } catch {}
    fs.rmSync(testDir, { recursive: true, force: true });
  });

  for (const id of ['terra', 'sonnet', 'opus']) {
    assert.equal((await testOrchestra.setModelRoles(id, { worker: true })).ok, true);
  }
  await testOrchestra.tick();
  const row = (await testOrchestra.snapshot()).projects.find(item => item.id === 'shared-proj');
  const approved = await testOrchestra.approve('shared-proj', row.megaTaskId);
  assert.equal(approved.ok, true);

  // Remove worker role from opus in the SHARED team (no projectId passed)
  assert.equal((await testOrchestra.setModelRoles('opus', { worker: false })).ok, true);

  await testOrchestra.quotaWatchTick();
  const view = await testOrchestra.snapshot();
  const sub = step(view, 'shared-proj', 'Shared build');
  assert.equal(sub.assignedModelId, 'terra');
  assert.equal(sub.status, 'PENDING');
  const logText = view.projects.find(p => p.id === 'shared-proj').log.map(line => line.text).join('\n');
  assert.match(logText, /Opus no longer has the worker role in this project's team/);
});

test('quotaWatchTick prefers an independent provider when moving a step that lost its role', async t => {
  const testDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-avoid-'));
  const testFolder = path.join(testDir, 'src');
  fs.mkdirSync(testFolder);
  const testWindows = [
    { id: 'chatgpt', readable: true, leftPct: 85 },
    { id: 'claude', readable: true, leftPct: 70 },
    { id: 'gemini', readable: true, leftPct: 90 }
  ];
  const testRoster = [
    model('terra', 'Terra', 'codex', 'gpt-6.1-terra', 'chatgpt'),
    model('terra-alt', 'Terra Alt', 'codex', 'gpt-6.1-alt', 'chatgpt'),
    model('sonnet', 'Sonnet', 'claude', 'claude-sonnet-5', 'claude'),
    model('opus', 'Opus', 'antigravity', 'gemini-3.1-pro', 'gemini')
  ];
  const testPlan = { schema: 2, projects: [
    project('avoid-proj', [{
      id: 'job-avoid', projectId: 'avoid-proj', revision_id: 1, title: 'Avoid work', detail: 'Avoid test',
      status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [
        slice('Step 1', 'opus'),
        slice('Step 2', 'terra')
      ]
    }])
  ] };
  let testPid = 600;
  const testAlive = new Set();
  const testOrchestra = createOrchestrator({
    dir: testDir, readPlan: async () => testPlan, models: testRoster, attempts: createAttemptEngine({ dir: path.join(testDir, 'attempts') }),
    fetchPlans: async () => testWindows.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => { testPid += 1; testAlive.add(testPid); return { pid: testPid }; },
    isProcessAlive: id => testAlive.has(id),
    killWorker: id => { testAlive.delete(id); return true; }
  });
  t.after(async () => {
    try { await testOrchestra.shutdown?.(); } catch {}
    fs.rmSync(testDir, { recursive: true, force: true });
  });

  for (const id of ['terra', 'terra-alt', 'sonnet', 'opus']) {
    assert.equal((await testOrchestra.setModelRoles(id, { worker: true })).ok, true);
  }
  await testOrchestra.tick();
  const row = (await testOrchestra.snapshot()).projects.find(item => item.id === 'avoid-proj');
  const approved = await testOrchestra.approve('avoid-proj', row.megaTaskId);
  assert.equal(approved.ok, true);

  // Opus loses worker role in avoid-proj.
  // Step 2 is running on terra (codex).
  // terra-alt has 85% quota (codex), sonnet has 70% quota (claude).
  // Sonnet should be chosen because codex is avoided by Step 1.
  assert.equal((await testOrchestra.setModelRoles('opus', { worker: false }, 'avoid-proj')).ok, true);

  await testOrchestra.quotaWatchTick();
  const view = await testOrchestra.snapshot();
  const step1 = step(view, 'avoid-proj', 'Step 1');
  assert.equal(step1.assignedModelId, 'sonnet');
});

test.after(async () => {
  try { await orchestra.shutdown?.(); } catch {}
  fs.rmSync(dir, { recursive: true, force: true });
});
