'use strict';
// A mode change must not reach into a step that already started, and two projects
// can save a mode at the same time without losing either choice.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');

function model(id, label, plan, extra = {}) {
  return {
    id, label, provider: 'grok', adapter: 'grok', cli: 'grok-' + id, providerCatalog: true,
    plan, enabled: true, available: true, cost: 1, limit: 100, kind: extra.kind || 'default',
    capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true, canResearch: true },
    roles: { orchestrator: true, worker: true, verifier: true }, ...extra
  };
}

function project(id, folder, slices, extra = {}) {
  return {
    schema: 1, project: { id, name: id, folder },
    revisions: [{ id: 1, direction: 'Ship ' + id, open: [] }],
    tasks: [{
      id: 'job-' + id, projectId: id, revision_id: 1, title: id, detail: 'Build', status: 'running',
      executionVersion: 2, source: 'chat', ...extra,
      proposedSubTasks: slices
    }]
  };
}

const slice = (title, assignedModelId, dependsOn) => ({
  title, assignedModelId, type: 'IMPLEMENTATION', detail: title, ...(dependsOn ? { dependsOn } : {})
});

async function removeTree(target) {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    try { fs.rmSync(target, { recursive: true, force: true }); return; }
    catch (error) {
      if (!['EBUSY', 'EPERM', 'ENOTEMPTY'].includes(error.code)) throw error;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
}

function open(dir, plan, models, plans) {
  let pid = 700;
  const alive = new Set();
  const launches = [];
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, models, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })), plansTtl: 0,
    askModel: async () => ({ answer: 'Noted.' }),
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: info => { pid += 1; alive.add(pid); launches.push({ ...info, pid }); return { pid }; },
    isProcessAlive: id => alive.has(id),
    killWorker: id => { alive.delete(id); return true; }
  });
  return { orchestra, alive, launches };
}

async function approve(box, id) {
  await box.orchestra.tick();
  const row = (await box.orchestra.snapshot()).projects.find(item => item.id === id);
  const result = await box.orchestra.approve(id, row.megaTaskId);
  assert.equal(result.ok, true, result.reason || id);
}

function step(view, id, title) {
  return view.projects.find(row => row.id === id).subTasks.find(sub => sub.title === title);
}

test('two projects can save a mode at the same time', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-both-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }];
  const plan = { schema: 2, projects: [project('a', folder, [slice('Build a', 'fable')]), project('b', folder, [slice('Build b', 'fable')])] };
  const box = open(dir, plan, [model('fable', 'Claude Fable', 'flag', { kind: 'premium' })], plans);
  try {
    const [first, second] = await Promise.all([
      box.orchestra.setSavingMode('a', 'safe'),
      box.orchestra.setSavingMode('b', 'ultra-safe')
    ]);
    assert.equal(first.ok, true);
    assert.equal(second.ok, true);
    const view = await box.orchestra.snapshot();
    assert.equal(view.projects.find(row => row.id === 'a').savingMode, 'safe');
    assert.equal(view.projects.find(row => row.id === 'b').savingMode, 'ultra-safe');
    const saved = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    assert.equal(saved.savingModes.a, 'safe');
    assert.equal(saved.savingModes.b, 'ultra-safe');
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('a quota switch keeps the mode the step started with', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-stamp-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [
    { id: 'flag', readable: true, leftPct: 90 },
    { id: 'opus', readable: true, leftPct: 95 },
    { id: 'mid', readable: true, leftPct: 70 },
    { id: 'light', readable: true, leftPct: 60 }
  ];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('opus', 'Claude Opus', 'opus', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: [project('keep', folder, [slice('Build keep', 'fable')])] };
  const box = open(dir, plan, roster, plans);
  try {
    await box.orchestra.setSavingMode('keep', 'normal');
    await approve(box, 'keep');
    let view = await box.orchestra.snapshot();
    assert.equal(step(view, 'keep', 'Build keep').assignedModelId, 'fable');
    await box.orchestra.setSavingMode('keep', 'ultra-safe');
    plans.find(item => item.id === 'flag').leftPct = 10;
    await box.orchestra.quotaWatchTick();
    view = await box.orchestra.tick();
    const moved = step(view, 'keep', 'Build keep');
    assert.equal(moved.assignedModelId, 'opus');
    assert.equal(moved.status, 'RUNNING');
    const state = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    assert.equal(state.projects.keep.stepModes[moved.id], 'normal');
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('a worker that ends with no result is replaced inside the mode the step started with', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-miss-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: [project('keep', folder, [slice('Build keep', 'fable')])] };
  const box = open(dir, plan, roster, plans);
  const current = async () => step(await box.orchestra.snapshot(), 'keep', 'Build keep');
  async function die() {
    const running = await current();
    const launch = box.launches.find(item => item.subTaskId === running.id && item.pid && box.alive.has(item.pid));
    box.alive.delete(launch.pid);
    fs.mkdirSync(path.dirname(launch.logPath), { recursive: true });
    fs.writeFileSync(launch.logPath, 'stopped\n');
    await box.orchestra.tick();
    return current();
  }
  try {
    await box.orchestra.setSavingMode('keep', 'normal');
    await approve(box, 'keep');
    assert.equal((await current()).assignedModelId, 'fable');
    await box.orchestra.setSavingMode('keep', 'ultra-safe');
    const retried = await die();
    assert.equal(retried.assignedModelId, 'fable');
    assert.equal(retried.status, 'RUNNING');
    const replaced = await die();
    assert.equal(replaced.assignedModelId, 'sonnet');
    assert.notEqual(replaced.assignedModelId, 'haiku');
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('Safe verifies with a default model, and Ultra safe says once that a light model verifies', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-verify-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const { VERIFY_SLICE } = require('./model-roles.cjs');
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: [
    project('safe', folder, [slice('Build safe', 'haiku'), slice(VERIFY_SLICE, 'fable', [0])]),
    project('ultra', folder, [slice('Build ultra', 'haiku'), slice(VERIFY_SLICE, 'fable', [0])])
  ] };
  const box = open(dir, plan, roster, plans);
  async function finish(id, title) {
    const running = step(await box.orchestra.snapshot(), id, title);
    const launch = box.launches.find(item => item.subTaskId === running.id && box.alive.has(item.pid));
    box.alive.delete(launch.pid);
    fs.mkdirSync(path.dirname(launch.structuredResultPath), { recursive: true });
    fs.writeFileSync(launch.structuredResultPath, JSON.stringify({
      resultId: launch.resultId, attemptId: launch.attemptId, subTaskId: launch.subTaskId, megaTaskId: launch.megaTaskId,
      status: 'COMPLETE', summary: 'Done.', filesChanged: [], tests: [{ command: 'node --test', status: 'PASSED', evidence: 'ok' }], blockers: [], nextAction: 'Next.', createdAt: new Date().toISOString()
    }));
    fs.writeFileSync(launch.exitPath, JSON.stringify({ attemptId: launch.attemptId, exitCode: 0, provider: launch.worker.provider, model: launch.worker.cli, createdAt: new Date().toISOString() }));
    return box.orchestra.tick();
  }
  try {
    await box.orchestra.setSavingMode('safe', 'safe');
    await box.orchestra.setSavingMode('ultra', 'ultra-safe');
    await approve(box, 'safe');
    await approve(box, 'ultra');
    let view = await finish('safe', 'Build safe');
    const safeCheck = step(view, 'safe', VERIFY_SLICE);
    assert.equal(safeCheck.assignedModelId, 'sonnet');
    assert.equal(safeCheck.status, 'RUNNING');
    const safeLog = view.projects.find(row => row.id === 'safe').log.map(line => line.text).join('\n');
    assert.doesNotMatch(safeLog, /light model to verify/);
    view = await finish('ultra', 'Build ultra');
    assert.equal(step(view, 'ultra', VERIFY_SLICE).assignedModelId, 'haiku');
    await box.orchestra.tick();
    const ultraLog = (await box.orchestra.snapshot()).projects.find(row => row.id === 'ultra').log.map(line => line.text).join('\n');
    assert.equal(ultraLog.split('light model to verify').length - 1, 1);
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('Ultra safe does not start research on a flagship', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-research-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'light', readable: true, leftPct: 80 }];
  const roster = [model('fable', 'Claude Fable', 'flag', { kind: 'premium' }), model('haiku', 'Claude Haiku', 'light')];
  const plan = { schema: 2, projects: [project('keep', folder, [{ title: 'Look it up', assignedModelId: 'fable', type: 'RESEARCH', detail: 'Look' }])] };
  const box = open(dir, plan, roster, plans);
  try {
    await box.orchestra.setSavingMode('keep', 'ultra-safe');
    await box.orchestra.tick();
    const row = (await box.orchestra.snapshot()).projects.find(item => item.id === 'keep');
    const approved = await box.orchestra.approve('keep', row.megaTaskId);
    const view = await box.orchestra.snapshot();
    const research = step(view, 'keep', 'Look it up');
    assert.notEqual(research.status, 'RUNNING');
    assert.equal(box.launches.some(item => item.worker && item.worker.id === 'fable'), false);
    const why = (approved.reason || '') + '\n' + (research.waitingFor || '') + '\n' + view.projects.find(item => item.id === 'keep').log.map(line => line.text).join('\n');
    assert.match(why, /Ultra safe|Claude Fable/);
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('a pull request fix keeps the mode from the click, even if it changes before a model is picked', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-pr-race-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const prView = JSON.stringify({
    headRefName: 'feat/x', baseRefName: 'main', mergeable: 'MERGEABLE', state: 'OPEN',
    statusCheckRollup: [{ name: 'ci', conclusion: 'FAILURE' }]
  });
  let release = () => {};
  let seen = () => {};
  const gate = new Promise(resolve => { release = resolve; });
  const ghSeen = new Promise(resolve => { seen = resolve; });
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'light', readable: true, leftPct: 80 }];
  const models = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: [project('keep', folder, [slice('Build keep', 'haiku')])] };
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, models, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })), plansTtl: 0,
    runCommand: (_folder, cmd) => {
      if (cmd === 'gh') { seen(); return gate.then(() => ({ status: 0, stdout: prView, stderr: '' })); }
      return Promise.resolve({ status: 0, stdout: cmd === 'git' ? 'abc\n' : '', stderr: '' });
    },
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: () => ({ pid: 900 }),
    isProcessAlive: () => false
  });
  const waitFor = async (read, ms = 8000) => {
    const end = Date.now() + ms;
    let value = await read();
    while (!value && Date.now() < end) { await new Promise(resolve => setTimeout(resolve, 20)); value = await read(); }
    return value;
  };
  try {
    await orchestra.setSavingMode('keep', 'normal');
    const pending = orchestra.fixPullRequest('keep', 4);
    await ghSeen;
    await orchestra.setSavingMode('keep', 'ultra-safe');
    release();
    assert.equal((await pending).started, true);
    const timeline = await waitFor(async () => {
      const text = ((await orchestra.snapshot()).execution.timeline || []).map(item => item.summary).join('\n');
      return /fixing pull request #4|was not fixed/.test(text) ? text : '';
    });
    assert.match(timeline, /Claude Fable is fixing pull request #4/);
    assert.doesNotMatch(timeline, /Claude Haiku is fixing/);
  } finally {
    await orchestra.shutdown();
    await removeTree(dir);
  }
});
