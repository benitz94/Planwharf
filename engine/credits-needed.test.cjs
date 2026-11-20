'use strict';
// "Requires usage credits": the model is outside the plan, not short of quota for a while. It leaves the
// lead and the crew until the owner looks for models again.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

test('a model that needs usage credits leaves the lead and the crew until models are looked for again', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-credits-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const model = (id, label) => ({ id, label, provider: 'claude', adapter: 'claude', cli: id, plan: 'claude', enabled: true, available: true, cost: 1, limit: 100, kind: 'premium',
    capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true } });
  const answers = { 'claude-fable-5-2': { failure: 'provider', answer: 'Fable 5.2 requires usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage, to continue.' } };
  const asked = [];
  const make = () => createOrchestrator({ dir, readPlan: async () => plan, models: [model('claude-fable-5-2', 'Fable 5.2'), model('claude-fable-5-1', 'Fable 5.1')], fetchPlans: null,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    askModel: async info => { const id = info.model?.id; asked.push(id); return answers[id] || { answer: 'ok', ms: 1 }; } });
  let orchestra = make();
  assert.equal((await orchestra.setOrchestrator('claude-fable-5-2')).ok, true);
  await orchestra.ask('Hello', undefined);
  assert.ok(asked.includes('claude-fable-5-1'), 'the next lead answered the same message');
  const view = async () => Object.fromEntries((await orchestra.tick()).models.map(item => [item.id, item]));
  let models = await view();
  assert.equal(models['claude-fable-5-2'].available, false);
  assert.equal(models['claude-fable-5-2'].creditsNeeded, true);
  assert.equal(models['claude-fable-5-2'].neverUsable, true, 'hidden from Team with the others that cannot run');
  assert.match(models['claude-fable-5-2'].unavailableReason, /Needs usage credits/);
  assert.equal(models['claude-fable-5-1'].available, true, 'the other models of the company are not touched');
  // Still left out after a restart, and an hour later.
  orchestra.shutdown?.(); orchestra = make();
  models = await view();
  assert.equal(models['claude-fable-5-2'].creditsNeeded, true);
  // Looking for models again checks it again.
  orchestra.clearCreditsNeeded();
  models = await view();
  assert.notEqual(models['claude-fable-5-2'].creditsNeeded, true);
});

test('an ordinary quota refusal is not mistaken for missing credits', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-credits-quota-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const lead = { id: 'claude-opus-5-5', label: 'Opus 5.5', provider: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', enabled: true, available: true, cost: 1, limit: 100, kind: 'premium',
    capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true } };
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, models: [lead], fetchPlans: null, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    askModel: async () => ({ failure: 'provider', answer: "You've hit your usage limit. Your limit resets at 5pm." }) });
  assert.equal((await orchestra.setOrchestrator('claude-opus-5-5')).ok, true);
  await orchestra.ask('Hello', undefined);
  const opus = (await orchestra.tick()).models.find(item => item.id === 'claude-opus-5-5');
  assert.notEqual(opus.creditsNeeded, true);
});
