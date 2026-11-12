'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createExecutionBridge } = require('./execution.cjs');

function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-reassign-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const item = structuredClone(require('../fixtures/starter-notebook.json').projects[0]);
  item.project = { id: 'crew-fixture', name: 'Crew fixture', folder: dir };
  const job = { id: 'crew-job', projectId: item.project.id, title: 'Crew fixture', detail: 'Reassign fixture', revision_id: item.revisions.at(-1).id, status: 'running', executionVersion: 2 };
  item.tasks = [job];
  const models = [
    { id: 'terra', provider: 'codex', cli: 'gpt-6.1-terra', enabled: true, available: true, cost: 1 },
    { id: 'sonnet', provider: 'claude', cli: 'claude-sonnet-5-5', enabled: true, available: true, cost: 1 },
    { id: 'no-role', provider: 'grok', cli: 'grok-4.7', enabled: true, available: true, cost: 1 }
  ];
  const killed = [], dispatch = [], store = { usage: {} }, live = new Set();
  const bridge = createExecutionBridge({ store, dir, rowFor: () => ({ dispatch }), models: () => models, leadId: () => 'sonnet', sessionId: 's1',
    attempts: createAttemptEngine({ dir }), now: () => '2026-11-11T01:00:00Z', persist: () => {}, inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
    workerEligible: model => model.id !== 'no-role', spawnWorker: () => ({ pid: 1 }), isProcessAlive: pid => live.has(pid), killWorker: pid => { killed.push(pid); live.delete(pid); return true; } });
  const mega = bridge.propose(item, job, [{ modelId: 'terra', slice: 'Build', dependsOn: [] }, { modelId: 'sonnet', slice: 'Verify', dependsOn: [0] }]);
  return { bridge, mega, item, killed, dispatch, dir, store, live };
}

test('the lead can move a remaining step to a model the user allowed', t => {
  const { bridge, mega, item } = fixture(t);
  const build = bridge.view(item.project.id).subTasks.find(sub => sub.megaTaskId === mega.id && sub.title === 'Build');
  const changed = bridge.reassignRemaining(item.project.id, mega.id, { [build.id]: 'sonnet' }, 'Terra is out of quota.');
  const after = bridge.view(item.project.id).subTasks.find(sub => sub.id === build.id);
  assert.equal(after.assignedModelId, 'sonnet');
  assert.equal(after.proposedProvider, 'claude');
  assert.equal(changed.status, 'READY_FOR_APPROVAL');
});

test('a model without the role, or an unknown step, is refused and nothing changes', t => {
  const { bridge, mega, item } = fixture(t);
  const build = bridge.view(item.project.id).subTasks.find(sub => sub.megaTaskId === mega.id && sub.title === 'Build');
  assert.throws(() => bridge.reassignRemaining(item.project.id, mega.id, { [build.id]: 'no-role' }), /cannot take/);
  assert.throws(() => bridge.reassignRemaining(item.project.id, mega.id, { 'missing-step': 'sonnet' }), /not a remaining step/);
  assert.equal(bridge.view(item.project.id).subTasks.find(sub => sub.id === build.id).assignedModelId, 'terra');
});

test('work that still reads RUNNING with a blocked step can change its models', t => {
  const { bridge, mega, item } = fixture(t);
  const build = bridge.view(item.project.id).subTasks.find(sub => sub.megaTaskId === mega.id && sub.title === 'Build');
  const live = bridge.target(item.project.id, mega.id);
  live.status = 'RUNNING';
  bridge.view(item.project.id).subTasks.find(sub => sub.id === build.id).status = 'BLOCKED';
  const changed = bridge.reassignRemaining(item.project.id, mega.id, { [build.id]: 'sonnet' });
  const after = bridge.view(item.project.id).subTasks.find(sub => sub.id === build.id);
  assert.equal(after.assignedModelId, 'sonnet');
  assert.equal(changed.status, 'RUNNING');
});

test('a working step can change model: its worker stops and the step restarts with the new one', t => {
  const { bridge, mega, item, killed, dispatch, dir, store, live: alive } = fixture(t);
  alive.add(4242);
  const live = store.execution.megaTasks.find(entry => entry.id === mega.id);
  const build = store.execution.subTasks.find(sub => sub.megaTaskId === mega.id && sub.title === 'Build');
  live.status = 'RUNNING';
  build.status = 'RUNNING';
  const claim = createAttemptEngine({ dir }).claim({ taskId: build.id, projectId: item.project.id, modelId: 'terra', candidates: [], megaTaskId: mega.id, subTaskId: build.id });
  const attemptId = claim.ok ? claim.attempt.id : 'attempt-1';
  build.attemptIds = [...(build.attemptIds || []), attemptId];
  dispatch.push({ subTaskId: build.id, megaTaskId: mega.id, pid: 4242, alive: true, attemptId,
    structuredResultPath: path.join(dir, 'none.json'), exitPath: path.join(dir, 'none.exit.json'), logPath: path.join(dir, 'worker.log'), startedAt: '2026-11-11T01:00:00Z' });
  fs.writeFileSync(path.join(dir, 'worker.log'), 'start\nwrote tools/check.py\nrunning tests/test_check.py\n');
  bridge.reassignRemaining(item.project.id, mega.id, { [build.id]: 'sonnet' });
  assert.deepEqual(killed, [4242]);
  assert.equal(dispatch[0].alive, false);
  assert.equal(build.status, 'PENDING');
  assert.equal(build.assignedModelId, 'sonnet');
  assert.ok(store.execution.results.some(result => result.subTaskId === build.id && /Model changed to/.test(result.summary)));
  assert.equal(build.takeover.fromModelId, 'terra');
  assert.match(build.takeover.logTail, /running tests\/test_check.py/);
});

test('the chat says who changed the crew: the lead, the user or the automatic switch', t => {
  const { bridge, mega, item, store } = fixture(t);
  const build = () => bridge.view(item.project.id).subTasks.find(sub => sub.megaTaskId === mega.id && sub.title === 'Build');
  const last = () => store.execution.timeline.filter(event => event.type === 'MEGATASK_ASSIGNED').at(-1).summary;
  bridge.reassignRemaining(item.project.id, mega.id, { [build().id]: 'sonnet' }, 'Sonnet builds this best.');
  assert.equal(last(), 'Lead assigned Build: sonnet. Why: Sonnet builds this best.');
  bridge.reassignRemaining(item.project.id, mega.id, { [build().id]: 'terra' }, '', 'user');
  assert.equal(last(), 'You assigned Build: terra.');
  bridge.reassignRemaining(item.project.id, mega.id, { [build().id]: 'sonnet' }, 'Quota of terra is at 15% (switch at 20%).', 'auto');
  assert.equal(last(), 'Planwharf switched Build: sonnet. Why: Quota of terra is at 15% (switch at 20%).');
});
