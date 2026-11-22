'use strict';
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
    capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true },
    roles: { orchestrator: true, worker: true, verifier: true }, ...extra
  };
}

function project(id, folder, title, assignedModelId) {
  return {
    schema: 1, project: { id, name: id, folder },
    revisions: [{ id: 1, direction: 'Ship ' + id, open: [] }],
    tasks: [{
      id: 'job-' + id, projectId: id, revision_id: 1, title: id, detail: 'Build', status: 'running',
      executionVersion: 2, source: 'chat',
      proposedSubTasks: [{ title, assignedModelId, type: 'IMPLEMENTATION', detail: title }]
    }]
  };
}

function open(dir, plan, models, plans, askModel) {
  let pid = 300;
  const alive = new Set();
  const launches = [];
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, models, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })), plansTtl: 0, askModel,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: info => { pid += 1; alive.add(pid); launches.push({ ...info, pid }); return { pid }; },
    isProcessAlive: id => alive.has(id),
    killWorker: id => { alive.delete(id); return true; }
  });
  return { orchestra, alive, launches };
}

test('Safe and Ultra safe hand the lead to an allowed class; Normal keeps the flagship', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-lead-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: ['plain', 'safe', 'ultra'].map(id => project(id, folder, 'Build ' + id, 'fable')) };
  const box = open(dir, plan, roster, plans, async () => ({ answer: 'Noted.' }));
  const logOf = async id => ((await box.orchestra.snapshot()).projects.find(row => row.id === id).log || []).map(line => line.text).join('\n');
  try {
    for (const id of ['plain', 'safe', 'ultra']) assert.equal((await box.orchestra.setOrchestrator('fable', id)).ok, true);
    await box.orchestra.setSavingMode('plain', 'normal');
    await box.orchestra.setSavingMode('safe', 'safe');
    await box.orchestra.setSavingMode('ultra', 'ultra-safe');
    await box.orchestra.ask('hello there', 'plain');
    assert.doesNotMatch(await logOf('plain'), /switched the lead/i);
    await box.orchestra.ask('hello there', 'safe');
    assert.match(await logOf('safe'), /Claude Fable → Claude Sonnet/);
    assert.match(await logOf('safe'), /outside Safe/);
    await box.orchestra.ask('hello there', 'ultra');
    assert.match(await logOf('ultra'), /Claude Fable → Claude Haiku/);
    assert.match(await logOf('ultra'), /outside Ultra safe/);
    assert.doesNotMatch(await logOf('ultra'), /Claude Sonnet/);
  } finally {
    await box.orchestra.shutdown();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a refusal replacement stays inside Safe and Ultra safe', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-replace-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 95 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }, { id: 'luna', readable: true, leftPct: 60 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light'),
    model('luna', 'Gemini Luna', 'luna')
  ];
  const plan = { schema: 2, projects: [
    project('safe', folder, 'Build safe', 'sonnet'),
    project('ultra', folder, 'Build ultra', 'haiku')
  ] };
  const box = open(dir, plan, roster, plans);
  const step = async (id, title) => (await box.orchestra.snapshot()).projects.find(row => row.id === id).subTasks.find(sub => sub.title === title);
  async function refuse(id, title) {
    const running = await step(id, title);
    const launch = box.launches.find(item => item.subTaskId === running.id);
    box.alive.delete(launch.pid);
    fs.mkdirSync(path.dirname(launch.logPath), { recursive: true });
    fs.writeFileSync(launch.logPath, "You've hit your 5-hour limit\n");
    await box.orchestra.tick();
    return step(id, title);
  }
  try {
    await box.orchestra.setSavingMode('safe', 'safe');
    await box.orchestra.setSavingMode('ultra', 'ultra-safe');
    await box.orchestra.tick();
    for (const id of ['safe', 'ultra']) {
      const row = (await box.orchestra.snapshot()).projects.find(item => item.id === id);
      assert.equal((await box.orchestra.approve(id, row.megaTaskId)).ok, true, id);
    }
    assert.equal((await step('safe', 'Build safe')).assignedModelId, 'sonnet');
    assert.equal((await step('ultra', 'Build ultra')).assignedModelId, 'haiku');
    const safeNext = await refuse('safe', 'Build safe');
    assert.equal(safeNext.assignedModelId, 'haiku');
    assert.notEqual(safeNext.assignedModelId, 'fable');
    const ultraNext = await refuse('ultra', 'Build ultra');
    assert.equal(ultraNext.assignedModelId, 'luna');
    assert.notEqual(ultraNext.assignedModelId, 'sonnet');
    assert.notEqual(ultraNext.assignedModelId, 'fable');
  } finally {
    await box.orchestra.shutdown();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('the lead split is offered only models the mode allows', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-split-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }, { id: 'luna', readable: true, leftPct: 60 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light'),
    model('luna', 'Gemini Luna', 'luna')
  ];
  const plan = { schema: 2, projects: ['safe', 'ultra'].map(id => project(id, folder, 'Build ' + id, 'fable')) };
  const questions = [];
  const box = open(dir, plan, roster, plans, async ({ question }) => {
    questions.push(String(question || ''));
    return { answer: '{"parts":[{"title":"Build one","detail":"Build the list.","files":["readme.txt"],"dependsOn":[],"size":"heavy","model":"fable"}],"verify":"fable","why":"flagship"}' };
  });
  const offered = name => questions.find(text => text.includes('Autonomous planning for project ' + name) && text.includes('Models allowed to build')) || '';
  try {
    await box.orchestra.tick();
    await box.orchestra.setMode('auto');
    await box.orchestra.setSavingMode('safe', 'safe');
    await box.orchestra.setSavingMode('ultra', 'ultra-safe');
    for (const id of ['safe', 'ultra']) assert.equal((await box.orchestra.setOrchestrator('fable', id)).ok, true);
    await box.orchestra.setAutonomy('safe', true);
    await box.orchestra.setAutonomy('ultra', true);
    await box.orchestra.autonomyTick();
    const safeOffer = offered('safe');
    const ultraOffer = offered('ultra');
    assert.match(safeOffer, /Models allowed to build:.*sonnet/);
    assert.match(safeOffer, /haiku/);
    assert.doesNotMatch(safeOffer, /Models allowed to build:[^\n]*fable/);
    assert.match(ultraOffer, /Models allowed to build:.*haiku/);
    assert.match(ultraOffer, /luna/);
    assert.doesNotMatch(ultraOffer, /Models allowed to build:[^\n]*(fable|sonnet)/);
    const view = await box.orchestra.snapshot();
    for (const id of ['safe', 'ultra']) {
      const running = view.projects.find(row => row.id === id).subTasks.find(sub => sub.status === 'RUNNING');
      assert.ok(running, id);
      assert.notEqual(running.assignedModelId, 'fable');
    }
  } finally {
    await box.orchestra.shutdown();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
