'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createOrchestrator, parseOrder } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-recipient-'));
const plan = { schema: 2, projects: ['alpha', 'beta'].map(id => ({
  schema: 1, project: { id, name: `Fixture ${id}`, folder: path.join(dir, id) },
  revisions: [{ id: 1, direction: 'Implement a parser', open: [] }],
  tasks: [{ id: `${id}-job`, projectId: id, revision_id: 1, title: 'Implement a parser', detail: 'Isolated slices', status: 'running' }]
})) };
for (const item of plan.projects) fs.mkdirSync(item.project.folder);
const models = [1, 2, 3].map(n => ({ id: `fixture-${n}`, label: `Fixture model ${n}`, provider: 'codex', cli: 'gpt-6-luna', enabled: true, kind: n === 1 ? 'premium' : 'default', cost: 1, limit: 200 }));
const alive = new Set(), killed = [], launches = [], calls = [];
let failKill = false;
const attempts = createAttemptEngine({ dir });
const options = {
  dir, readPlan: async () => plan, attempts, models,
  sentinel: { tick: () => ({ complete: false, closed: false }) },
  fetchPlans: async () => [],
  inspectFolder: folder => ({ folder, fileCount: 1, names: [], git: false }),
  spawnWorker: info => { const pid = 1000 + launches.length; launches.push(info); alive.add(pid); return { pid }; },
  isProcessAlive: pid => alive.has(pid),
  killWorker: pid => { if (failKill) return false; killed.push(pid); return alive.delete(pid); },
  askModel: async info => { calls.push(info); return { answer: 'Fixture answer.', actions: [] }; }
};

(async () => {
  const orchestra = createOrchestrator(options);
  await orchestra.setOrchestrator('fixture-1');
  await orchestra.approve('alpha');
  assert.equal(launches.length, 2);
  await orchestra.stop('alpha');
  const before = launches.length;
  const requested = await orchestra.ask('Implement the parser and resume support for Sayaku.', 'beta');
  assert.equal(requested.order, null, 'A work request cannot implicitly resume a checkpoint');
  assert.equal(calls.at(-1).state.focus.id, 'beta');
  assert.equal(calls.at(-1).folder, plan.projects[1].project.folder);
  assert.equal(requested.snapshot.projects.find(row => row.id === 'alpha').status, 'checkpointed');
  assert.equal(launches.length, before);
  for (const [question, projectId] of [['Resume', undefined], ['Resume Fixture alpha', 'beta'], ['Resume', 'deleted']]) {
    const result = await orchestra.ask(question, projectId);
    assert.equal(result.ok, false);
    assert.equal(launches.length, before, 'Invalid recipient must be rejected before dispatch');
  }
  const snap = await orchestra.snapshot();
  assert.equal(parseOrder('Do not resume Fixture alpha', snap, 'alpha'), null);
  assert.equal(parseOrder('The start button should resume work.', snap, 'beta'), null);
  assert.equal(parseOrder('Stop should only affect one worker', snap, 'beta'), null);
  options.askModel = async info => ({ answer: 'Research fixture.', actions: [{ type: 'dispatch_research', projectId: 'alpha', modelId: 'fixture-2', title: 'Wrong target', detail: 'Isolated' }] });
  const research = await orchestra.ask('Research parser implementations', 'beta');
  assert.equal(research.actionResults[0].ok, false);
  assert.equal(launches.length, before);
  options.askModel = async () => ({ answer: 'Fixture answer.', actions: [] });

  const approved = await orchestra.approve('beta');
  const parts = approved.snapshot.projects.find(row => row.id === 'beta').dispatch;
  assert.equal(parts.length, 2);
  const launchCount = launches.length, killCount = killed.length;
  assert.equal((await orchestra.stopWorker('alpha', parts[0].pid)).ok, false, 'PID must belong to the requested project');
  failKill = true;
  assert.equal((await orchestra.stopWorker('beta', parts[0].pid)).ok, false);
  failKill = false;
  const stopped = await orchestra.stopWorker('beta', parts[0].pid);
  assert.equal(stopped.ok, true);
  assert.deepEqual(killed.slice(killCount), [parts[0].pid]);
  assert.equal(alive.has(parts[1].pid), true);
  // Build jobs now run two slices (implement, verify); there is no third worker.
  assert.equal((await orchestra.stopWorker('beta', parts[0].pid)).ok, false);
  for (let i = 0; i < 3; i++) await orchestra.tick();
  assert.equal(launches.length, launchCount, 'Monitor must not replace a worker stopped by the user');
  const restored = createOrchestrator(options);
  const restoredSnap = await restored.tick();
  assert.equal(restoredSnap.projects.find(row => row.id === 'beta').dispatch[0].stoppedByUser, true);
  assert.equal(launches.length, launchCount, 'User stop survives restoration');
  for (const part of parts.slice(1)) fs.writeFileSync(part.resultPath, 'STATUS: COMPLETE\n');
  const finished = await restored.tick();
  assert.equal(finished.projects.find(row => row.id === 'beta').status, 'checkpointed', 'Incomplete stopped slice must not report completion');
  assert.equal(killed.filter(pid => pid === parts[0].pid).length, 1, 'Historical stopped PID must not be killed again');
  console.log('OK: isolated recipient routing, checkpoint protection, research target guard, individual worker stop and restoration.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => {
  const resolved = path.resolve(dir);
  assert.ok(resolved.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('pw-recipient-'));
  fs.rmSync(resolved, { recursive: true, force: true });
});
