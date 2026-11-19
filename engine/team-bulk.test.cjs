'use strict';
// Team's "All" and "None": the same role for the models shown, skipping a model that cannot take it.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

test('several models get or lose a role at once; one that cannot take it is skipped, not refused', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-team-bulk-'));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const model = (id, label, extra = {}) => ({ id, label, provider: 'claude', adapter: 'claude', cli: id, plan: 'claude', enabled: true, available: true, cost: 1, limit: 100, kind: 'default',
    capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: false, worker: false, verifier: false, reviewer: false, supervisor: false }, ...extra });
  const roster = [model('haiku', 'Haiku 5.5'), model('sonnet', 'Sonnet 5.5'), model('chat', 'Chat only', { workerEligible: false })];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, models: roster, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }) });
  try {
    const roles = async () => Object.fromEntries((await orchestra.tick()).models.map(item => [item.id, item.roles.worker]));
    const on = await orchestra.setRolesForMany(['haiku', 'sonnet', 'chat'], { worker: true });
    assert.equal(on.ok, true);
    assert.equal(on.changed, 2, 'the model that cannot code is skipped');
    assert.deepEqual(await roles(), { haiku: true, sonnet: true, chat: false });
    assert.equal((await orchestra.setRolesForMany(['sonnet'], { worker: false })).ok, true);
    assert.deepEqual(await roles(), { haiku: true, sonnet: false, chat: false });
    assert.equal((await orchestra.setRolesForMany([], { worker: true })).ok, false);
    assert.equal((await orchestra.setRolesForMany(['haiku'], { boss: true })).ok, false);
    // Each model has its weight, for the filters.
    assert.deepEqual((await orchestra.tick()).models.map(item => item.weight), ['light', 'default', 'default']);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
