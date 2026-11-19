'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createExecutionBridge } = require('./execution.cjs');

// A step with no model that has quota waits, says why, and starts by itself when quota returns.
function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-quota-wait-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const item = structuredClone(require('../fixtures/starter-notebook.json').projects[0]);
  item.project = { id: 'wait-fixture', name: 'Wait fixture', folder: dir };
  const job = { id: 'wait-job', projectId: item.project.id, title: 'Wait fixture', detail: 'Quota wait', revision_id: item.revisions.at(-1).id, status: 'running', executionVersion: 2 };
  item.tasks = [job];
  const models = [{ id: 'terra', provider: 'codex', cli: 'gpt-6.1-terra', enabled: true, available: true, cost: 1 }];
  const state = { quota: false, explain: { waiting: true, message: 'Waiting for quota: GPT-6.1 Terra 0% (resets 15:09 UTC).' } };
  const launches = [], live = new Set(), store = { usage: {} }, row = { dispatch: [] };
  const bridge = createExecutionBridge({ store, dir, rowFor: () => row, models: () => models, leadId: () => 'terra', sessionId: 's1',
    attempts: createAttemptEngine({ dir }), now: () => new Date().toISOString(), persist: () => {}, inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
    resolveWorker: worker => state.quota ? worker : null,
    explainNoWorker: () => state.explain,
    spawnWorker: info => { const pid = 5000 + launches.length; launches.push(info); live.add(pid); return { pid }; },
    isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid) });
  const parts = [{ modelId: 'terra', slice: 'Build', dependsOn: [] }];
  const mega = bridge.propose(item, job, parts);
  bridge.approve(item.project.id, mega.id);
  return { bridge, mega, item, job, parts, store, launches, state };
}

test('with no model that has quota, the step waits instead of blocking, and says why', t => {
  const { bridge, mega, item, job, parts, store, launches } = fixture(t);
  bridge.advance(item, job, parts);
  const sub = store.execution.subTasks.find(entry => entry.megaTaskId === mega.id);
  const live = store.execution.megaTasks.find(entry => entry.id === mega.id);
  assert.equal(sub.status, 'PENDING');
  assert.match(sub.waitingFor, /Waiting for quota/);
  assert.equal(live.status, 'RUNNING');
  assert.match(live.nextAction, /Waiting for quota/);
  assert.equal(store.execution.checkpoints.length, 0);
  assert.equal(launches.length, 0);
});

test('when quota returns, the waiting step starts by itself', t => {
  const { bridge, mega, item, job, parts, store, launches, state } = fixture(t);
  bridge.advance(item, job, parts);
  state.quota = true;
  bridge.advance(item, job, parts);
  const sub = store.execution.subTasks.find(entry => entry.megaTaskId === mega.id);
  assert.equal(sub.status, 'RUNNING');
  assert.equal(sub.waitingFor, null);
  assert.equal(launches.length, 1);
});

test('a problem that is not quota blocks the step with a clear message', t => {
  const { bridge, mega, item, job, parts, store, state } = fixture(t);
  state.explain = { waiting: false, message: 'No model in your crew can take "Build": give a model the Build role in Team.' };
  bridge.advance(item, job, parts);
  const sub = store.execution.subTasks.find(entry => entry.megaTaskId === mega.id);
  const live = store.execution.megaTasks.find(entry => entry.id === mega.id);
  assert.equal(sub.status, 'BLOCKED');
  assert.match(live.nextAction, /give a model the Build role/);
});
