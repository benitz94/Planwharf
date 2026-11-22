'use strict';
// Edges the Job 30 suite leaves out: refusals, two additions, overlapping files,
// a plan-file write, a thrown kill, and two clicks at once.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');
const { VERIFY_SLICE } = require('./model-roles.cjs');
const planFile = require('./plan-file.cjs');

function setup(dir, plan, models, extra = {}) {
  let pid = 400;
  const alive = new Set();
  const launches = [];
  const killed = [];
  const orchestra = createOrchestrator({
    dir,
    readPlan: extra.readPlan || (async () => plan),
    models,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => [],
    plansTtl: 0,
    inspectFolder: extra.inspectFolder || (() => ({ git: false, dirty: [], names: ['src/app.js'], fileCount: 1 })),
    spawnWorker: info => {
      pid += 1;
      alive.add(pid);
      launches.push({ ...info, pid });
      return { pid };
    },
    isProcessAlive: id => alive.has(id),
    killWorker: extra.killWorker || (id => {
      alive.delete(id);
      killed.push(id);
      return true;
    }),
    askModel: extra.askModel || (async () => ({ answer: 'Noted.' }))
  });
  return { orchestra, alive, launches, killed };
}

const model = (id, label, extra = {}) => ({
  id, label, provider: extra.provider || 'codex', adapter: extra.adapter || 'codex',
  cli: extra.cli || ('gpt-' + id), providerCatalog: true,
  plan: extra.plan || 'plus', enabled: true, available: true, cost: 1, limit: 100,
  kind: extra.kind || 'default',
  capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true, canResearch: true },
  roles: extra.roles || { orchestrator: true, worker: true, verifier: true }, ...extra
});

function project(id, folder, tasks) {
  return { schema: 1, project: { id, name: id, folder }, revisions: [{ id: 1, direction: 'Build ' + id, open: [] }], tasks };
}

function slice(title, assignedModelId, dependsOn, scope) {
  return {
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {}), ...(scope ? { scope } : {})
  };
}

function runningPlan(folder, extra = {}) {
  return {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: extra.jobStatus || 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: extra.slices || [
          slice('Build first part', extra.builder || 'opus', null, extra.scope),
          slice(VERIFY_SLICE, extra.verifier || 'astra', [0])
        ]
      }])
    ]
  };
}

const roster = () => [
  model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
  model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false }, kind: 'premium' }),
  model('haiku', 'Haiku Builder', { roles: { orchestrator: false, worker: true, verifier: false }, kind: 'light', leadTier: 2, plan: 'light' }),
  model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
];

async function startWork(box, projectId = 'demo') {
  const snap0 = await box.orchestra.tick(projectId);
  const proj0 = snap0.projects.find(p => p.id === projectId);
  const approved = await box.orchestra.approve(projectId, proj0.megaTaskId);
  assert.equal(approved.ok, true);
  return approved.snapshot.projects.find(p => p.id === projectId);
}

function completeLaunch(box, launch, summary = 'Slice complete', filesChanged = []) {
  box.alive.delete(launch.pid);
  fs.mkdirSync(path.dirname(launch.structuredResultPath), { recursive: true });
  fs.writeFileSync(launch.structuredResultPath, JSON.stringify({
    resultId: launch.resultId || ('R-' + launch.attemptId),
    attemptId: launch.attemptId,
    subTaskId: launch.subTaskId,
    megaTaskId: launch.megaTaskId,
    status: 'COMPLETE',
    summary,
    filesChanged,
    tests: [{ command: 'test', status: 'PASSED', evidence: 'OK' }],
    blockers: [],
    nextAction: 'None',
    createdAt: new Date().toISOString()
  }));
  fs.mkdirSync(path.dirname(launch.exitPath), { recursive: true });
  fs.writeFileSync(launch.exitPath, JSON.stringify({
    attemptId: launch.attemptId,
    exitCode: 0,
    provider: launch.worker.provider,
    model: launch.worker.cli,
    createdAt: new Date().toISOString()
  }));
}

test('addToWork refuses empty, missing, idle and unknown work', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-err-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const plan = runningPlan(folder);
  const idle = project('idle', folder, []);
  plan.projects.push(idle);
  const box = setup(dir, plan, roster());
  await startWork(box);

  const empty = await box.orchestra.addToWork('demo', { addition: '   ' });
  assert.equal(empty.ok, false);
  assert.match(empty.reason, /addition required/i);

  const missing = await box.orchestra.addToWork('demo', {});
  assert.equal(missing.ok, false);
  assert.match(missing.reason, /addition required/i);

  const noProject = await box.orchestra.addToWork('missing', { addition: 'Add a helper.' });
  assert.equal(noProject.ok, false);
  assert.match(noProject.reason, /project not found/i);

  const blankId = await box.orchestra.addToWork('', { addition: 'Add a helper.' });
  assert.equal(blankId.ok, false);

  const idleAdd = await box.orchestra.addToWork('idle', { addition: 'Add a helper.' });
  assert.equal(idleAdd.ok, false);
  assert.match(idleAdd.reason, /no running job/i);
});

test('two additions keep unique ids, sit before Verify, and Verify waits for both', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-two-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const box = setup(dir, runningPlan(folder), roster());
  await startWork(box);

  const first = await box.orchestra.addToWork('demo', { addition: 'Add logging in `src/log.js`.' });
  assert.equal(first.ok, true);
  const second = await box.orchestra.addToWork('demo', { addition: 'Add metrics in `src/metrics.js`.' });
  assert.equal(second.ok, true);

  const snap = await box.orchestra.snapshot();
  const proj = snap.projects.find(p => p.id === 'demo');
  const builds = proj.subTasks.filter(s => !/^Verify/.test(s.title));
  const verify = proj.subTasks.find(s => /^Verify/.test(s.title));
  assert.equal(proj.subTasks.length, 4);
  assert.equal(new Set(proj.subTasks.map(s => s.id)).size, 4);
  assert.equal(proj.subTasks[proj.subTasks.length - 1].id, verify.id);
  assert.ok(verify.dependencies.includes(first.subTask.id));
  assert.ok(verify.dependencies.includes(second.subTask.id));
  assert.equal(builds[1].id, first.subTask.id);
  assert.equal(builds[2].id, second.subTask.id);
});

test('two addToWork calls at once keep both steps and unique ids', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-race-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  let reads = 0;
  const plan = runningPlan(folder);
  const box = setup(dir, plan, roster(), {
    readPlan: async () => {
      reads += 1;
      if (reads <= 3) await new Promise(resolve => setImmediate(resolve));
      return plan;
    }
  });
  await startWork(box);
  const [a, b] = await Promise.all([
    box.orchestra.addToWork('demo', { addition: 'Add cache in `src/cache.js`.' }),
    box.orchestra.addToWork('demo', { addition: 'Add retry in `src/retry.js`.' })
  ]);
  assert.equal(a.ok, true);
  assert.equal(b.ok, true);
  assert.notEqual(a.subTask.id, b.subTask.id);
  const snap = await box.orchestra.snapshot();
  const proj = snap.projects.find(p => p.id === 'demo');
  const verify = proj.subTasks.find(s => /^Verify/.test(s.title));
  assert.equal(proj.subTasks.length, 4);
  assert.equal(new Set(proj.subTasks.map(s => s.id)).size, 4);
  assert.ok(verify.dependencies.includes(a.subTask.id));
  assert.ok(verify.dependencies.includes(b.subTask.id));
  assert.ok(proj.log.some(line => /Added to this work:/.test(line.text)));
});

test('the work plan lists the new step and unticks Verify', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-plan-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  planFile.prepare(folder, {
    goal: 'Demo work',
    steps: [
      { role: 'Build', model: 'Opus Builder', title: 'Build first part', done: false },
      { role: 'Verify', model: 'Astra Verifier', title: VERIFY_SLICE, done: true, commit: 'checked' }
    ]
  });
  const box = setup(dir, runningPlan(folder), roster());
  await startWork(box);
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Add a helper in `src/help.js`.',
    files: ['src/help.js']
  });
  assert.equal(result.ok, true);
  const parsed = planFile.parse(planFile.read(folder));
  assert.match(parsed.steps.map(s => s.line).join('\n'), /helper|help\.js|Addition/i);
  const verifyLine = parsed.steps.find(s => /Verify/i.test(s.line));
  assert.ok(verifyLine);
  assert.equal(verifyLine.done, false);
});

test('a thrown kill while Verify runs still adds the step and reruns Verify', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-kill-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const box = setup(dir, runningPlan(folder), roster(), {
    killWorker: id => {
      box.alive.delete(id);
      box.killed.push(id);
      throw new Error('kill refused');
    }
  });
  await startWork(box);
  completeLaunch(box, box.launches[0], 'Build complete', ['src/app.js']);
  await box.orchestra.tick('demo');
  const verifyLaunch = box.launches[1];
  assert.equal(verifyLaunch.worker.id, 'astra');
  const result = await box.orchestra.addToWork('demo', { addition: 'Cover the empty input path.' });
  assert.equal(result.ok, true);
  const proj = result.snapshot.projects.find(p => p.id === 'demo');
  const verify = proj.subTasks.find(s => /^Verify/.test(s.title));
  assert.equal(verify.status, 'PENDING');
  const add = proj.subTasks.find(s => /empty input/i.test(s.title));
  completeLaunch(box, box.launches.find(l => l.subTaskId === add.id), 'Addition done');
  const after = await box.orchestra.tick('demo');
  const verifyAgain = after.projects.find(p => p.id === 'demo').subTasks.find(s => /^Verify/.test(s.title));
  assert.equal(verifyAgain.status, 'RUNNING');
});

test('Ultra safe assigns a light model, and a flagged model is not used', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-save-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const box = setup(dir, runningPlan(folder, { builder: 'haiku' }), roster());
  for (const id of ['opus', 'haiku', 'astra']) {
    assert.equal((await box.orchestra.setModelRoles(id, { orchestrator: false, worker: id !== 'astra', verifier: id === 'astra' })).ok, true);
  }
  await startWork(box);
  await box.orchestra.setSavingMode('demo', 'ultra-safe');
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Add a retry helper.',
    modelId: 'opus'
  });
  assert.equal(result.ok, true);
  assert.equal(result.subTask.assignedModelId, 'haiku');
  const snap = await box.orchestra.snapshot();
  const proj = snap.projects.find(p => p.id === 'demo');
  assert.ok(proj.log.some(line => /Added to this work:.*Haiku Builder/.test(line.text)));
});

test('a last chat answer supplies the addition, and a broken plan file does not block it', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-ask-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const box = setup(dir, runningPlan(folder), roster(), {
    askModel: async () => ({ answer: 'Yes.\nAddition to this work: Add a settings panel in `src/settings.js`.' })
  });
  await box.orchestra.setOrchestrator('lead');
  await startWork(box);
  await box.orchestra.ask('Also add settings.', 'demo');
  const planDir = path.join(folder, '.planwharf');
  fs.mkdirSync(planDir, { recursive: true });
  fs.writeFileSync(path.join(planDir, 'plan.md'), '');
  fs.rmSync(path.join(planDir, 'plan.md'));
  fs.mkdirSync(path.join(planDir, 'plan.md'));
  const result = await box.orchestra.addToWork('demo', {});
  assert.equal(result.ok, true);
  assert.match(result.subTask.title, /settings panel/i);
  assert.deepEqual(result.subTask.scope, ['src/settings.js']);
  const proj = result.snapshot.projects.find(p => p.id === 'demo');
  assert.equal(proj.addition, null);
  assert.ok(proj.timeLeft);
});

test('named files wait for a running step on the same path, including a parent folder', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-scope-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const plan = runningPlan(folder, { scope: ['src'] });
  const box = setup(dir, plan, roster());
  await startWork(box);
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Tighten validation.',
    files: ['src/app.js']
  });
  assert.equal(result.ok, true);
  const proj = result.snapshot.projects.find(p => p.id === 'demo');
  const build = proj.subTasks[0];
  const added = proj.subTasks.find(s => s.id === result.subTask.id);
  assert.ok(added.dependencies.includes(build.id));
  assert.equal(added.status, 'PENDING');
  assert.equal(build.status, 'RUNNING');
});

test('an addition waits for a running Build that did not name its files', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-unscoped-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const box = setup(dir, runningPlan(folder), roster());
  await startWork(box);
  const named = await box.orchestra.addToWork('demo', { addition: 'Add logging in `src/log.js`.' });
  assert.equal(named.ok, true);
  const projNamed = named.snapshot.projects.find(p => p.id === 'demo');
  assert.equal(projNamed.subTasks[0].status, 'RUNNING');
  assert.equal(named.subTask.status, 'PENDING');
  assert.ok(named.subTask.dependencies.includes(projNamed.subTasks[0].id));

  const unnamed = await box.orchestra.addToWork('demo', { addition: 'Add a retry helper.' });
  assert.equal(unnamed.ok, true);
  assert.equal(unnamed.subTask.status, 'PENDING');
  assert.ok(unnamed.subTask.dependencies.includes(projNamed.subTasks[0].id));
});
