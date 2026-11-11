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
  const bridge = createExecutionBridge({ store: { usage: {} }, dir, rowFor: () => ({ dispatch: [] }), models: () => models, leadId: () => 'sonnet', sessionId: 's1',
    attempts: createAttemptEngine({ dir }), now: () => '2026-11-11T01:00:00Z', persist: () => {}, inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
    workerEligible: model => model.id !== 'no-role', spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false, killWorker: () => true });
  const mega = bridge.propose(item, job, [{ modelId: 'terra', slice: 'Build', dependsOn: [] }, { modelId: 'sonnet', slice: 'Verify', dependsOn: [0] }]);
  return { bridge, mega, item };
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
