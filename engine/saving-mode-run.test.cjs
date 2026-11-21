'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');
const { VERIFY_SLICE } = require('./model-roles.cjs');

function open(dir, plan, models, plans) {
  let pid = 200;
  const alive = new Set();
  const launches = [];
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, models, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })), plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: info => { pid += 1; alive.add(pid); launches.push({ ...info, pid }); return { pid }; },
    isProcessAlive: id => alive.has(id),
    killWorker: id => { alive.delete(id); return true; },
    askModel: async () => ({ answer: 'Noted.' })
  });
  return { orchestra, alive, launches };
}

const model = (id, label, plan, extra = {}) => ({
  id, label, provider: 'grok', adapter: 'grok', cli: 'grok-' + id, providerCatalog: true,
  plan, enabled: true, available: true, cost: 1, limit: 100, kind: extra.kind || 'default',
  capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true, canResearch: true },
  roles: { orchestrator: true, worker: true, verifier: true }, ...extra
});

function project(id, folder, tasks) {
  return { schema: 1, project: { id, name: id, folder }, revisions: [{ id: 1, direction: 'Ship ' + id, open: [] }], tasks };
}

test('saving mode is saved, shown, and limits the next step without stopping the one already running', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-run-'));
  const folder = path.join(dir, 'src');
  fs.mkdirSync(folder);
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'mid', readable: true, leftPct: 80 }, { id: 'light', readable: true, leftPct: 70 }];
  const roster = [
    model('fable', 'Claude Fable', 'flag', { kind: 'premium' }),
    model('sonnet', 'Claude Sonnet', 'mid'),
    model('haiku', 'Claude Haiku', 'light')
  ];
  const slice = (title, assignedModelId, dependsOn) => ({ title, assignedModelId, type: 'IMPLEMENTATION', detail: title, ...(dependsOn ? { dependsOn } : {}) });
  const plan = { schema: 2, projects: [
    project('keep', folder, [{ id: 'job-keep', projectId: 'keep', revision_id: 1, title: 'Keep', detail: 'Build', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Build the list', 'fable'), slice(VERIFY_SLICE, 'fable', [0])] }]),
    project('safe', folder, [{ id: 'job-safe', projectId: 'safe', revision_id: 1, title: 'Safe', detail: 'Build', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Build safe', 'haiku')] }]),
    project('ultra', folder, [{ id: 'job-ultra', projectId: 'ultra', revision_id: 1, title: 'Ultra', detail: 'Build', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Build ultra', 'fable')] }]),
    project('none', folder, [{ id: 'job-none', projectId: 'none', revision_id: 1, title: 'None', detail: 'Build', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Build none', 'fable')] }]),
    project('weight', folder, [{ id: 'job-weight', projectId: 'weight', revision_id: 1, title: 'Weight', detail: 'Build', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [slice('Build weight', 'fable')] }])
  ] };
  const box = open(dir, plan, roster, plans);
  try {
    for (const id of ['fable', 'sonnet', 'haiku']) assert.equal((await box.orchestra.setModelRoles(id, { orchestrator: true, worker: true, verifier: true })).ok, true);
    const saved = await box.orchestra.setSavingMode('keep', 'normal');
    assert.equal(saved.ok, true);
    assert.equal(saved.snapshot.projects.find(row => row.id === 'keep').savingMode, 'normal');
    const italian = await box.orchestra.ask('modalità sicura', 'safe');
    assert.match(italian.answer, /Sicura/);
    assert.match(italian.answer, /prossimo passo/);
    assert.equal((await box.orchestra.snapshot()).projects.find(row => row.id === 'safe').savingMode, 'safe');
    await box.orchestra.setSavingMode('ultra', 'ultra-safe');
    await box.orchestra.setSavingMode('none', 'ultra-safe');
    await box.orchestra.setModelRoles('sonnet', { worker: false, verifier: false, orchestrator: false }, 'none');
    await box.orchestra.setModelRoles('haiku', { worker: false, verifier: false, orchestrator: false }, 'none');
    await box.orchestra.setSavingMode('weight', 'ultra-safe');

    await box.orchestra.tick();
    const noneRow = (await box.orchestra.snapshot()).projects.find(item => item.id === 'none');
    const refused = await box.orchestra.approve('none', noneRow.megaTaskId);
    assert.equal(refused.ok, false);
    assert.match(refused.reason, /Ultra safe allows no model/);
    assert.match(refused.reason, /leaves out Claude Fable/);
    for (const id of ['keep', 'safe', 'ultra']) {
      const row = (await box.orchestra.snapshot()).projects.find(item => item.id === id);
      assert.equal((await box.orchestra.approve(id, row.megaTaskId)).ok, true, id);
    }
    let view = await box.orchestra.snapshot();
    const step = (projectId, title) => view.projects.find(row => row.id === projectId).subTasks.find(sub => sub.title === title);
    assert.equal(step('keep', 'Build the list').status, 'RUNNING');
    assert.equal(step('keep', 'Build the list').assignedModelId, 'fable');
    assert.equal(step('keep', VERIFY_SLICE).status, 'PENDING', 'verify waits for the build');
    assert.equal(step('safe', 'Build safe').assignedModelId, 'haiku');
    assert.equal(step('ultra', 'Build ultra').assignedModelId, 'haiku');
    const blocked = (await box.orchestra.snapshot()).projects.find(row => row.id === 'none');
    assert.match((blocked.log || []).map(line => line.text).join('\n'), /Ultra safe allows no model/);

    const running = step('keep', 'Build the list');
    await box.orchestra.setSavingMode('keep', 'ultra-safe');
    view = await box.orchestra.snapshot();
    const still = view.projects.find(row => row.id === 'keep').subTasks.find(sub => sub.title === 'Build the list');
    assert.equal(still.assignedModelId, 'fable');
    assert.equal(still.status, 'RUNNING');
    assert.equal(box.alive.has(box.launches.find(item => item.subTaskId === running.id).pid), true);
    const state = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    assert.equal(state.projects.keep.stepModes[running.id], 'normal');
    assert.equal(state.savingModes.keep, 'ultra-safe');

    const launch = box.launches.find(item => item.subTaskId === running.id);
    box.alive.delete(launch.pid);
    fs.writeFileSync(launch.structuredResultPath, JSON.stringify({
      resultId: launch.resultId, attemptId: launch.attemptId, subTaskId: launch.subTaskId, megaTaskId: launch.megaTaskId,
      status: 'COMPLETE', summary: 'Built.', filesChanged: [], tests: [{ command: 'node --test', status: 'PASSED', evidence: 'ok' }], blockers: [], nextAction: 'Verify.', createdAt: new Date().toISOString()
    }));
    fs.writeFileSync(launch.exitPath, JSON.stringify({ attemptId: launch.attemptId, exitCode: 0, provider: launch.worker.provider, model: launch.worker.cli, createdAt: new Date().toISOString() }));
    view = await box.orchestra.tick();
    const verify = view.projects.find(row => row.id === 'keep').subTasks.find(sub => sub.title.startsWith('Verify'));
    assert.equal(verify.assignedModelId, 'haiku');
    assert.equal(verify.status, 'RUNNING');
    assert.match(view.projects.find(row => row.id === 'keep').log.map(line => line.text).join('\n'), /light model to verify/);

    plans.find(item => item.id === 'light').leftPct = 10;
    await box.orchestra.quotaWatchTick();
    view = await box.orchestra.snapshot();
    const moved = view.projects.find(row => row.id === 'safe').subTasks.find(sub => sub.title === 'Build safe');
    assert.equal(moved.assignedModelId, 'sonnet');

    const lead = await box.orchestra.setOrchestrator('fable');
    assert.equal(lead.ok, true);
    await box.orchestra.setSavingMode('keep', 'ultra-safe');
    await box.orchestra.setModelRoles('sonnet', { orchestrator: false, worker: true, verifier: true });
    await box.orchestra.setModelRoles('haiku', { orchestrator: false, worker: true, verifier: true });
    const reply = await box.orchestra.ask('hello there', 'keep');
    assert.match(reply.answer, /keeps Claude Fable/);
    assert.match(reply.answer, /lowest class/);
    assert.equal((await box.orchestra.setModelWeight('fable', 'light')).weight, 'light');
    const weightRow = (await box.orchestra.snapshot()).projects.find(item => item.id === 'weight');
    assert.equal((await box.orchestra.approve('weight', weightRow.megaTaskId)).ok, true);
    const weighted = (await box.orchestra.snapshot()).projects.find(row => row.id === 'weight').subTasks.find(sub => sub.title === 'Build weight');
    assert.equal(weighted.assignedModelId, 'fable');
    assert.equal(weighted.status, 'RUNNING');

    await box.orchestra.shutdown();
    const again = open(dir, plan, roster, plans);
    const restored = await again.orchestra.snapshot();
    assert.equal(restored.projects.find(row => row.id === 'safe').savingMode, 'safe');
    assert.equal(restored.projects.find(row => row.id === 'ultra').savingMode, 'ultra-safe');
    await again.orchestra.shutdown();
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
