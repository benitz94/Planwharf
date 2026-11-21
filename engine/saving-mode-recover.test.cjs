'use strict';
// Error, recovery, and stop paths Job 29's suites do not run.
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

function project(id, folder, title, assignedModelId, extra = {}) {
  return {
    schema: 1, project: { id, name: id, folder },
    revisions: [{ id: 1, direction: 'Ship ' + id, open: [] }],
    tasks: [{
      id: 'job-' + id, projectId: id, revision_id: 1, title: id, detail: 'Build', status: 'running',
      executionVersion: 2, source: 'chat', ...extra,
      proposedSubTasks: [{ title, assignedModelId, type: extra.type || 'IMPLEMENTATION', detail: title }]
    }]
  };
}

async function removeTree(target) {
  for (let attempt = 0; attempt < 20; attempt += 1) {
    try { fs.rmSync(target, { recursive: true, force: true }); return; }
    catch (error) {
      if (!['EBUSY', 'EPERM', 'ENOTEMPTY'].includes(error.code)) throw error;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
}

function open(dir, plan, models, plans, extra = {}) {
  let pid = 500;
  const alive = new Set();
  const launches = [];
  let failRead = false;
  const orchestra = createOrchestrator({
    dir, readPlan: async () => { if (failRead) throw new Error('plan unreadable'); return plan; },
    models, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })), plansTtl: 0,
    askModel: async () => ({ answer: 'Noted.' }),
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: info => { pid += 1; alive.add(pid); launches.push({ ...info, pid }); return { pid }; },
    isProcessAlive: id => alive.has(id),
    killWorker: id => { alive.delete(id); return true; },
    ...extra
  });
  return { orchestra, alive, launches, fail: () => { failRead = true; }, heal: () => { failRead = false; } };
}

test('a bad mode, a missing project, and an unreadable plan change nothing', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-err-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'light', readable: true, leftPct: 80 }];
  const roster = [model('fable', 'Claude Fable', 'flag', { kind: 'premium' }), model('haiku', 'Claude Haiku', 'light')];
  const plan = { schema: 2, projects: [project('keep', folder, 'Build keep', 'fable')] };
  const box = open(dir, plan, roster, plans);
  try {
    assert.equal((await box.orchestra.setSavingMode('keep', 'safe')).ok, true);
    const unknown = await box.orchestra.setSavingMode('keep', 'turbo');
    assert.equal(unknown.ok, false);
    assert.match(unknown.reason, /Unknown saving mode/);
    assert.equal((await box.orchestra.setSavingMode('missing', 'ultra-safe')).ok, false);
    assert.equal((await box.orchestra.setSavingMode('__proto__', 'safe')).ok, false);
    assert.equal((await box.orchestra.setModelWeight('nobody', 'light')).reason, 'Model not found.');
    assert.equal((await box.orchestra.setModelWeight('fable', 'huge')).reason, 'Unknown model weight.');
    box.fail();
    await assert.rejects(() => box.orchestra.setSavingMode('keep', 'ultra-safe'), /plan unreadable/);
    box.heal();
    const view = await box.orchestra.snapshot();
    assert.equal(view.projects.find(row => row.id === 'keep').savingMode, 'safe');
    assert.equal(view.projects.some(row => row.id === '__proto__'), false);
    assert.equal(view.models.find(item => item.id === 'fable').weightSource, 'lineup');
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('a damaged saved mode and a damaged weight come back as Normal and the lineup', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-corrupt-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({
    savingModes: { keep: 'ultra_safe', gone: 'nope', broken: 'safe,extra' },
    modelWeights: { fable: 'flagship', sonnet: 'huge', haiku: 'light' }
  }));
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: [project('keep', folder, 'Build keep', 'haiku'), project('gone', folder, 'Build gone', 'haiku')] };
  const box = open(dir, plan, roster, plans);
  try {
    const view = await box.orchestra.snapshot();
    assert.equal(view.projects.find(row => row.id === 'keep').savingMode, 'ultra-safe');
    assert.equal(view.projects.find(row => row.id === 'gone').savingMode, 'normal');
    const weight = id => view.models.find(item => item.id === id);
    assert.equal(weight('fable').weight, 'premium');
    assert.equal(weight('fable').modelClass, 'flagship');
    assert.equal(weight('sonnet').weightSource, 'lineup');
    assert.equal(weight('haiku').weight, 'light');
    assert.equal(weight('haiku').weightSource, 'team');
    const saved = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    assert.equal(saved.modelWeights.sonnet, undefined);
    assert.equal(saved.savingModes.broken, 'normal');
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('Ultra safe does not start a Gold flagship, and it does not hand the step to someone else', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-gold-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'light', readable: true, leftPct: 80 }];
  const roster = [model('fable', 'Claude Fable', 'flag', { kind: 'premium' }), model('haiku', 'Claude Haiku', 'light')];
  const plan = { schema: 2, projects: [project('gold', folder, 'Build gold', 'fable', { goldModel: { id: 'fable', provider: 'grok', cli: 'grok-fable' } })] };
  const box = open(dir, plan, roster, plans);
  try {
    await box.orchestra.setSavingMode('gold', 'ultra-safe');
    await box.orchestra.tick();
    const row = (await box.orchestra.snapshot()).projects.find(item => item.id === 'gold');
    const refused = await box.orchestra.approve('gold', row.megaTaskId);
    assert.equal(refused.ok, false);
    assert.match(refused.reason, /Ultra safe leaves out the Gold model Claude Fable/);
    assert.match(refused.reason, /never switches model/);
    const view = await box.orchestra.snapshot();
    const step = view.projects.find(item => item.id === 'gold').subTasks.find(sub => sub.title === 'Build gold');
    assert.notEqual(step.status, 'RUNNING');
    assert.equal(box.launches.length, 0);
    assert.match(view.projects.find(item => item.id === 'gold').log.map(line => line.text).join('\n'), /Gold model Claude Fable/);
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('when the only allowed model is under its limit, the step waits and names who was left out', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-wait-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 85 }, { id: 'light', readable: true, leftPct: 10 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const plan = { schema: 2, projects: [project('wait', folder, 'Build wait', 'fable')] };
  const box = open(dir, plan, roster, plans);
  try {
    await box.orchestra.setSavingMode('wait', 'ultra-safe');
    await box.orchestra.tick();
    const row = (await box.orchestra.snapshot()).projects.find(item => item.id === 'wait');
    const approved = await box.orchestra.approve('wait', row.megaTaskId);
    assert.equal(approved.ok, true, approved.reason);
    const view = await box.orchestra.snapshot();
    const step = view.projects.find(item => item.id === 'wait').subTasks.find(sub => sub.title === 'Build wait');
    assert.equal(step.status, 'PENDING');
    assert.match(step.waitingFor, /Waiting for quota/);
    assert.match(step.waitingFor, /Claude Haiku/);
    assert.match(step.waitingFor, /Ultra safe leaves out Claude Fable/);
    assert.match(step.waitingFor, /Claude Sonnet/);
    assert.equal(box.launches.length, 0);
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});

test('the lead fallback keeps the best model of the lowest class, and a Team weight counts', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-fallback-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'flash', readable: true, leftPct: 88 }, { id: 'light', readable: true, leftPct: 70 }, { id: 'mid', readable: true, leftPct: 10 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('flash', 'Gemini 3.8 Flash', 'flash'),
    model('haiku', 'Claude Haiku', 'light'),
    model('sonnet', 'Claude Sonnet', 'mid')
  ];
  const plan = { schema: 2, projects: [project('keep', folder, 'Build keep', 'haiku'), project('low', folder, 'Build low', 'sonnet')] };
  const box = open(dir, plan, roster, plans);
  const logOf = async id => ((await box.orchestra.snapshot()).projects.find(row => row.id === id).log || []).map(line => line.text).join('\n');
  try {
    assert.equal((await box.orchestra.setModelWeight('flash', 'premium')).weight, 'premium');
    // On this project only a flagship and a light model marked premium can lead. The lowest class is flagship.
    assert.equal((await box.orchestra.setModelRoles('haiku', { orchestrator: false }, 'keep')).ok, true);
    assert.equal((await box.orchestra.setModelRoles('sonnet', { orchestrator: false }, 'keep')).ok, true);
    assert.equal((await box.orchestra.setOrchestrator('fable', 'keep')).ok, true);
    await box.orchestra.setSavingMode('keep', 'ultra-safe');
    const first = await box.orchestra.ask('hello there', 'keep');
    assert.match(first.answer, /keeps Claude Fable/);
    assert.doesNotMatch(first.answer, /Flash/);
    assert.doesNotMatch(await logOf('keep'), /Gemini 3\.8 Flash/);
    const second = await box.orchestra.ask('hello again', 'keep');
    assert.equal(second.answer.includes('lowest class'), false);

    assert.equal((await box.orchestra.setOrchestrator('sonnet', 'low')).ok, true);
    await box.orchestra.setSavingMode('low', 'safe');
    await box.orchestra.ask('hello there', 'low');
    const lowLog = await logOf('low');
    // Quota may hand the lead to the flagship while the mode is still being applied. Safe must not leave it there.
    assert.match(lowLog.split('\n')[0], /Claude Fable → Claude Haiku/);
    assert.match(lowLog, /outside Safe/);
  } finally {
    await box.orchestra.shutdown();
    await removeTree(dir);
  }
});
