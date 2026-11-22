'use strict';
// The owner chooses which models may lead a project: the menu and every hand-over stay among them.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

test('a model switched off with the Lead button never leads, the menu leaves it out, and it is saved per project', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-lead-allowed-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const projectId = plan.projects ? plan.projects[0].project.id : plan.project.id;
  const model = (id, label, provider, cli, kind) => ({ id, label, provider, adapter: provider, cli, plan: provider, enabled: true, available: true, cost: 1, limit: 100, kind, providerCatalog: true,
    capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true } });
  const asked = [];
  const make = () => createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: null, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [model('grok', 'Grok 4.7', 'grok', 'grok-4.7', 'premium'), model('fable', 'Fable 5.2', 'claude', 'claude-fable-5-2', 'premium'), model('sol', 'GPT-6.1 Sol', 'codex', 'gpt-6.1-sol', 'default')],
    askModel: async info => { asked.push(info.model.id); return info.model.id === 'grok' ? { failure: 'process', answer: '' } : { answer: 'Aye.' }; } });
  let orchestra = make();
  assert.equal((await orchestra.setOrchestrator('grok', projectId)).ok, true);
  assert.equal((await orchestra.setLeadAllowed('grok', false, projectId)).ok, false, 'the lead itself cannot be switched off');
  const off = await orchestra.setLeadAllowed('fable', false, projectId);
  assert.equal(off.ok, true);
  const view = await orchestra.snapshot(projectId);
  await orchestra.shutdown?.();
  orchestra = make();
  // Read the project's own team after a restart.
  const again = await orchestra.withTeam(projectId, () => orchestra.snapshot());
  assert.equal(again.models.find(item => item.id === 'fable').leadAllowed, false, 'saved per project');
  assert.ok(!again.leadOrder.includes('fable'), 'not in the lead menu');
  assert.ok(!again.leadBackups.includes('fable'), 'not a backup');
  await orchestra.ask('Read docs/NEXT-WORK.md and continue.', projectId);
  assert.deepEqual(asked.slice(0, 3), ['grok', 'grok', 'sol'], 'Fable is stronger, but the owner switched it off as lead');
  assert.ok(view);
  // The shared team is not touched.
  const shared = await orchestra.snapshot();
  assert.notEqual(shared.models.find(item => item.id === 'fable').leadAllowed, false);
});
