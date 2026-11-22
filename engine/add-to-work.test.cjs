'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');
const { VERIFY_SLICE } = require('./model-roles.cjs');

function setup(dir, plan, models, plans = [], askModel = null) {
  let pid = 200;
  const alive = new Set();
  const launches = [];
  const killed = [];
  const orchestra = createOrchestrator({
    dir,
    readPlan: async () => plan,
    models,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })),
    plansTtl: 0,
    inspectFolder: () => ({ git: false, dirty: [], names: ['src/app.js'], fileCount: 1 }),
    spawnWorker: info => {
      pid += 1;
      alive.add(pid);
      launches.push({ ...info, pid });
      return { pid };
    },
    isProcessAlive: id => alive.has(id),
    killWorker: id => {
      alive.delete(id);
      killed.push(id);
      return true;
    },
    askModel: async info => {
      if (typeof askModel === 'function') return askModel(info);
      return { answer: 'Noted.' };
    }
  });
  return { orchestra, alive, launches, killed };
}

const model = (id, label, extra = {}) => ({
  id, label, provider: 'codex', adapter: 'codex', cli: 'gpt-' + id, providerCatalog: true,
  plan: 'plus', enabled: true, available: true, cost: 1, limit: 100, kind: 'default',
  capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true, canResearch: true },
  roles: { orchestrator: true, worker: true, verifier: true }, ...extra
});

function project(id, folder, tasks) {
  return { schema: 1, project: { id, name: id, folder }, revisions: [{ id: 1, direction: 'Build ' + id, open: [] }], tasks };
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

test('an addition during running work adds one step before Verify and starts it on an allowed model', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-work-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const slice = (title, assignedModelId, dependsOn, scope) => ({
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {}), ...(scope ? { scope } : {})
  });
  const plan = {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          slice('Build first part', 'opus'),
          slice(VERIFY_SLICE, 'astra', [0])
        ]
      }])
    ]
  };

  const box = setup(dir, plan, roster);
  const snap0 = await box.orchestra.tick('demo');
  const proj0 = snap0.projects.find(p => p.id === 'demo');
  const approved = await box.orchestra.approve('demo', proj0.megaTaskId);
  assert.equal(approved.ok, true);

  const snap1 = approved.snapshot;
  const proj1 = snap1.projects.find(p => p.id === 'demo');
  assert.equal(proj1.subTasks.length, 2);
  assert.equal(proj1.subTasks[0].status, 'RUNNING');
  assert.equal(proj1.subTasks[1].status, 'PENDING');
  assert.equal(box.launches.length, 1);
  assert.equal(box.launches[0].worker.id, 'opus');

  // Add an addition while work is running
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Addition to this work: Add a logging helper module in `src/logger.js`.'
  });

  assert.equal(result.ok, true);
  const snap2 = result.snapshot;
  const proj2 = snap2.projects.find(p => p.id === 'demo');
  assert.equal(proj2.subTasks.length, 3);

  // Subtask order: 0: Build first part, 1: Addition, 2: Verify
  assert.equal(proj2.subTasks[0].title, 'Build first part');
  assert.match(proj2.subTasks[1].title, /logging helper/i);
  assert.equal(proj2.subTasks[1].assignedModelId, 'opus');
  assert.match(proj2.subTasks[2].title, /^Verify/);

  // Verify depends on the new addition step
  assert.ok(proj2.subTasks[2].dependencies.includes(proj2.subTasks[1].id));

  // The chat has the note
  assert.ok(proj2.log.some(line => line.text.includes('Added to this work:')));
});

test('Verify already running runs again after the addition', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-verify-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const slice = (title, assignedModelId, dependsOn) => ({
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {})
  });
  const plan = {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          slice('Build part', 'opus'),
          slice(VERIFY_SLICE, 'astra', [0])
        ]
      }])
    ]
  };

  const box = setup(dir, plan, roster);
  const snap0 = await box.orchestra.tick('demo');
  const proj0 = snap0.projects.find(p => p.id === 'demo');
  const approved = await box.orchestra.approve('demo', proj0.megaTaskId);
  assert.equal(approved.ok, true);
  assert.equal(box.launches.length, 1);

  // Complete the build step so Verify starts
  const buildLaunch = box.launches[0];
  completeLaunch(box, buildLaunch, 'Build complete', ['src/app.js']);

  const snapVerify = await box.orchestra.tick('demo');
  const projVerify = snapVerify.projects.find(p => p.id === 'demo');
  assert.equal(projVerify.subTasks[0].status, 'COMPLETE');
  assert.equal(projVerify.subTasks[1].status, 'RUNNING');
  assert.equal(box.launches.length, 2);
  const verifyLaunch = box.launches[1];
  assert.equal(verifyLaunch.worker.id, 'astra');

  // While Verify is running, add an addition
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Addition to this work: Fix edge cases in input parser.'
  });
  assert.equal(result.ok, true);

  // Verify should have been stopped
  assert.ok(box.killed.includes(verifyLaunch.pid));
  const snapAfter = result.snapshot;
  const projAfter = snapAfter.projects.find(p => p.id === 'demo');
  const verifySubAfter = projAfter.subTasks.find(s => /^Verify/.test(s.title));
  assert.equal(verifySubAfter.status, 'PENDING');

  // The addition step is now running
  const addSub = projAfter.subTasks.find(s => /input parser/i.test(s.title));
  assert.equal(addSub.status, 'RUNNING');

  // Complete the addition step
  const addLaunch = box.launches.find(l => l.subTaskId === addSub.id);
  assert.ok(addLaunch);
  completeLaunch(box, addLaunch, 'Addition complete', ['src/parser.js']);

  // Tick again: Verify runs again on the same branch
  const snapFinal = await box.orchestra.tick('demo');
  const projFinal = snapFinal.projects.find(p => p.id === 'demo');
  const verifySubFinal = projFinal.subTasks.find(s => /^Verify/.test(s.title));
  assert.equal(verifySubFinal.status, 'RUNNING');
  // 4 launches total: Build, Verify (initial), Addition, Verify (rerun)
  assert.equal(box.launches.length, 4);
  assert.equal(box.launches[3].worker.id, 'astra');
});

test('two steps on the same files run one after the other', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-scope-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('terra', 'Terra Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const slice = (title, assignedModelId, dependsOn, scope) => ({
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {}), ...(scope ? { scope } : {})
  });
  const plan = {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          slice('Build app component', 'opus', [], ['src/app.js']),
          slice(VERIFY_SLICE, 'astra', [0])
        ]
      }])
    ]
  };

  const box = setup(dir, plan, roster);
  const snap0 = await box.orchestra.tick('demo');
  const proj0 = snap0.projects.find(p => p.id === 'demo');
  const approved = await box.orchestra.approve('demo', proj0.megaTaskId);
  assert.equal(approved.ok, true);

  const initialBuildLaunch = box.launches[0];
  assert.equal(initialBuildLaunch.worker.id, 'opus');

  // Add addition that touches the same file `src/app.js`
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Addition to this work: Update `src/app.js` with extra validation.'
  });
  assert.equal(result.ok, true);

  const snapAdded = result.snapshot;
  const projAdded = snapAdded.projects.find(p => p.id === 'demo');
  const build1 = projAdded.subTasks[0];
  const addStep = projAdded.subTasks[1];

  // The addition must depend on build1 because they touch the same file
  assert.ok(addStep.dependencies.includes(build1.id));
  // Because build1 is running, the addition step is pending
  assert.equal(addStep.status, 'PENDING');
  assert.equal(build1.status, 'RUNNING');

  // Now complete build1
  completeLaunch(box, initialBuildLaunch, 'Build 1 finished', ['src/app.js']);

  // Tick: now addition step runs!
  const snapAfterBuild1 = await box.orchestra.tick('demo');
  const projAfterBuild1 = snapAfterBuild1.projects.find(p => p.id === 'demo');
  const addStepAfter = projAfterBuild1.subTasks.find(s => s.id === addStep.id);
  assert.equal(addStepAfter.status, 'RUNNING');
});

test('a stopped work resumes with the addition', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-stop-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const slice = (title, assignedModelId, dependsOn) => ({
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {})
  });
  const plan = {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          slice('Build base', 'opus'),
          slice(VERIFY_SLICE, 'astra', [0])
        ]
      }])
    ]
  };

  const box = setup(dir, plan, roster);
  const snap0 = await box.orchestra.tick('demo');
  const proj0 = snap0.projects.find(p => p.id === 'demo');
  await box.orchestra.approve('demo', proj0.megaTaskId);

  // Stop the work
  const stopped = await box.orchestra.stop('demo');
  assert.equal(stopped.ok, true);
  const snapStopped = await box.orchestra.snapshot();
  const projStopped = snapStopped.projects.find(p => p.id === 'demo');
  assert.equal(projStopped.status, 'checkpointed');

  // Add addition while stopped
  const result = await box.orchestra.addToWork('demo', {
    addition: 'Addition to this work: Add resume test file in `src/resume.js`.'
  });
  assert.equal(result.ok, true);

  // Work resumed to watching
  const snapResumed = result.snapshot;
  const projResumed = snapResumed.projects.find(p => p.id === 'demo');
  assert.equal(projResumed.status, 'watching');
  assert.equal(projResumed.subTasks.length, 3);
});

test('a finished work turns it into a new goal', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-finish-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const slice = (title, assignedModelId, dependsOn) => ({
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {})
  });
  const plan = {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          slice('Build piece', 'opus'),
          slice(VERIFY_SLICE, 'astra', [0])
        ]
      }])
    ]
  };

  let askReply = 'Noted.';
  const box = setup(dir, plan, roster, [], async () => ({ answer: askReply }));
  await box.orchestra.setOrchestrator('lead');
  const snap0 = await box.orchestra.tick('demo');
  const proj0 = snap0.projects.find(p => p.id === 'demo');
  await box.orchestra.approve('demo', proj0.megaTaskId);

  // Complete Build
  completeLaunch(box, box.launches[0], 'Build complete');
  await box.orchestra.tick('demo');

  // Complete Verify
  assert.equal(box.launches.length, 2);
  completeLaunch(box, box.launches[1], 'Verify passed');
  const snapDone = await box.orchestra.tick('demo');
  const projDone = snapDone.projects.find(p => p.id === 'demo');
  assert.equal(projDone.status, 'complete');

  // Attempting addToWork on finished work fails
  const refused = await box.orchestra.addToWork('demo', {
    addition: 'Addition to this work: New feature.'
  });
  assert.equal(refused.ok, false);

  // When lead gives an answer with "Addition to this work: ...", it is converted into crewGoal (new goal proposal)
  askReply = 'Here is what we can do next.\nAddition to this work: Build user settings screen.';
  await box.orchestra.ask('What next?', 'demo');
  const snapAfterAsk = await box.orchestra.snapshot();
  const projAfterAsk = snapAfterAsk.projects.find(p => p.id === 'demo');
  assert.equal(projAfterAsk.addition, null);
  assert.equal(projAfterAsk.crewGoal, 'Build user settings screen.');
});

test('Talk never offers it', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-add-talk-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder);
  const roster = [
    model('lead', 'Lead model', { roles: { orchestrator: true, worker: false, verifier: false } }),
    model('opus', 'Opus Builder', { roles: { orchestrator: false, worker: true, verifier: false } }),
    model('astra', 'Astra Verifier', { roles: { orchestrator: false, worker: false, verifier: true } })
  ];
  const slice = (title, assignedModelId, dependsOn) => ({
    title, assignedModelId, type: 'IMPLEMENTATION', detail: title,
    ...(dependsOn ? { dependsOn } : {})
  });
  const plan = {
    schema: 2,
    projects: [
      project('demo', folder, [{
        id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Demo work', detail: 'Initial build',
        status: 'running', executionVersion: 2, source: 'chat',
        proposedSubTasks: [
          slice('Build part', 'opus'),
          slice(VERIFY_SLICE, 'astra', [0])
        ]
      }])
    ]
  };

  let askReply = 'Going well!\nAddition to this work: Add a dark mode toggle.';
  const box = setup(dir, plan, roster, [], async () => ({ answer: askReply }));
  await box.orchestra.setOrchestrator('lead');
  await box.orchestra.setChatMode('demo', 'talk');
  const snap0 = await box.orchestra.tick('demo');
  const proj0 = snap0.projects.find(p => p.id === 'demo');
  await box.orchestra.approve('demo', proj0.megaTaskId);

  // Ask in talk mode
  await box.orchestra.ask('How is it going?', 'demo');

  const snap = await box.orchestra.snapshot();
  const proj = snap.projects.find(p => p.id === 'demo');
  // Neither addition nor crewGoal should be offered in talk mode
  assert.equal(proj.addition, null);
  assert.equal(proj.crewGoal, null);

  // Calling addToWork directly in talk mode is refused
  const refused = await box.orchestra.addToWork('demo', {
    addition: 'Addition to this work: Add a dark mode toggle.'
  });
  assert.equal(refused.ok, false);
  assert.match(refused.reason, /Talk mode/i);
});
