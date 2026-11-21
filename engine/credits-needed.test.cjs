'use strict';
// "Requires usage credits": the model is outside the plan, not short of quota for a while. It leaves the
// lead and the crew for a day; looking for models again does not bring it back.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

test('a model that needs usage credits leaves the lead and the crew for a day', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-credits-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const model = (id, label) => ({ id, label, provider: 'claude', adapter: 'claude', cli: id, plan: 'claude', enabled: true, available: true, cost: 1, limit: 100, kind: 'premium',
    capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true } });
  const answers = { 'claude-fable-5-2': { failure: 'provider', answer: 'Fable 5.2 requires usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage, to continue.' } };
  const asked = [];
  let clock = Date.parse('2026-11-21T12:00:00Z');
  const make = () => createOrchestrator({ dir, readPlan: async () => plan, now: () => new Date(clock).toISOString(), models: [model('claude-fable-5-2', 'Fable 5.2'), model('claude-fable-5-1', 'Fable 5.1')], fetchPlans: null,
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
  // An hour later it is still out.
  clock += 60 * 60 * 1000;
  models = await view();
  assert.equal(models['claude-fable-5-2'].creditsNeeded, true);
  // After a day it is tried again.
  clock += 24 * 60 * 60 * 1000;
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

test('the hand-over goes down the list: out of quota, then a lead that needs credits, then one that answers', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-credits-chain-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const model = (id, label, provider, kind) => ({ id, label, provider, adapter: provider, cli: id, plan: provider, enabled: true, available: true, cost: 1, limit: 100, kind,
    providerCatalog: true, capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true } });
  const answers = {
    'grok-4-7': { failure: 'provider', diagnostic: { code: 'PROVIDER_USAGE_LIMIT' }, answer: 'Rate limit reached: quota exceeded.' },
    'claude-fable-5-2': { failure: 'provider', diagnostic: { code: 'PROVIDER_USAGE_LIMIT' }, answer: 'claude CLI failed (provider): Fable 5.2 requires usage credits. Switch to another model.' }
  };
  const asked = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: null, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [model('grok-4-7', 'Grok 4.7', 'grok', 'premium'), model('claude-fable-5-2', 'Fable 5.2', 'claude', 'premium'), model('gpt-6-astra', 'GPT-6 Astra', 'codex', 'default')],
    askModel: async info => { const id = info.model?.id; asked.push(id); return answers[id] || { answer: 'Aye.', ms: 1 }; } });
  assert.equal((await orchestra.setOrchestrator('grok-4-7')).ok, true);
  const reply = await orchestra.ask('Read docs/NEXT-WORK.md and continue.', undefined);
  assert.deepEqual(asked.slice(0, 3), ['grok-4-7', 'claude-fable-5-2', 'gpt-6-astra']);
  assert.match(JSON.stringify(reply), /Aye\./, 'the last lead answers the message');
  const fable = (await orchestra.tick()).models.find(item => item.id === 'claude-fable-5-2');
  assert.equal(fable.creditsNeeded, true);
});

test('a lead that fails twice hands over, and the hand-over goes on past a lead that needs credits', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-credits-died-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const model = (id, label, provider, kind) => ({ id, label, provider, adapter: provider, cli: id, plan: provider, enabled: true, available: true, cost: 1, limit: 100, kind,
    providerCatalog: true, capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true } });
  const answers = {
    'grok-4-7': { failure: 'process', answer: '' },
    'claude-fable-5-2': { failure: 'provider', diagnostic: { code: 'PROVIDER_USAGE_LIMIT' }, answer: 'PROVIDER_USAGE_LIMIT: claude / claude-fable-5-2.\nclaude CLI failed (provider): Fable 5.2 requires usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage, to continue.' }
  };
  const asked = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: null, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [model('grok-4-7', 'Grok 4.7', 'grok', 'premium'), model('claude-fable-5-2', 'Fable 5.2', 'claude', 'premium'), model('gpt-6-astra', 'GPT-6 Astra', 'codex', 'default')],
    askModel: async info => { const id = info.model?.id; asked.push(id); return answers[id] || { answer: 'Aye.', ms: 1 }; } });
  assert.equal((await orchestra.setOrchestrator('grok-4-7')).ok, true);
  const reply = await orchestra.ask('Read docs/NEXT-WORK.md and continue.', undefined);
  assert.deepEqual(asked.slice(0, 4), ['grok-4-7', 'grok-4-7', 'claude-fable-5-2', 'gpt-6-astra']);
  assert.match(JSON.stringify(reply), /Aye\./, 'the last lead answers the message');
  const fable = (await orchestra.tick()).models.find(item => item.id === 'claude-fable-5-2');
  assert.equal(fable.creditsNeeded, true);
});
