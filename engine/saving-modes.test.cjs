'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { pickWorker, pickWorkers, leadTier } = require('./orchestrator.js');
const { quotaSwitches } = require('./model-policy.cjs');
const { permitsAssignment } = require('./model-roles.cjs');
const { compactState } = require('./briefing.cjs');
const { savingModeError } = require('./model-routing.cjs');
const { askPrompt } = require('./runner.cjs');
const { formatStatusMessage, formatTelegramAlertMessage } = require('./phone.cjs');

const model = (id, label, extra = {}) => ({
  id, label, provider: 'grok', adapter: 'grok', cli: 'grok-' + id, providerCatalog: true,
  plan: id, enabled: true, available: true, cost: 1, limit: 100, kind: 'default',
  capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true },
  roles: { worker: true, verifier: true, orchestrator: true }, ...extra
});

test('leadTier classes flagship, mid and light, and effort stays light', () => {
  assert.equal(leadTier({ label: 'Claude Fable', kind: 'default' }), 0);
  assert.equal(leadTier({ label: 'Claude Sonnet', kind: 'default' }), 1);
  assert.equal(leadTier({ label: 'Claude Haiku', kind: 'default' }), 2);
  assert.equal(leadTier({ label: 'GPT-6 Astra', kind: 'premium' }), 0);
  assert.equal(leadTier({ label: 'Gemini 3.8 Flash (High)', kind: 'default' }), 2);
});

test('Safe and Ultra safe pick only the allowed classes', () => {
  const flagship = model('fable', 'Claude Fable', { kind: 'premium' });
  const mid = model('sonnet', 'Claude Sonnet');
  const light = model('haiku', 'Claude Haiku');
  const flash = model('flash', 'Gemini 3.8 Flash (High)');
  const roster = [flagship, mid, light, flash];
  const job = { title: 'Implement the list', kind: 'build' };
  assert.equal(pickWorker(roster, { job }).id, 'fable');
  assert.equal(pickWorker(roster, { job, savingMode: 'normal' }).id, 'fable');
  assert.equal(pickWorker(roster, { job, savingMode: 'safe' }).id, 'sonnet');
  assert.equal(pickWorker(roster, { job, savingMode: 'ultra-safe' }).id, 'haiku');
  const crew = pickWorkers(roster, { job, count: 2, savingMode: 'safe' }).map(item => item.id);
  assert.deepEqual(crew, ['sonnet', 'haiku']);
  assert.equal(pickWorker([flagship], { job, savingMode: 'ultra-safe' }), null);
  // A Team weight of light lets a flagship through Ultra safe.
  const weighted = { ...flagship, weight: 'light', weightSource: 'team', leadTier: 0 };
  assert.equal(pickWorker([weighted], { job, savingMode: 'ultra-safe' }).id, 'fable');
});

test('quota switch and role check honor the mode', () => {
  const low = { id: 'haiku', label: 'Haiku', enabled: true, available: true, providerEnabled: true, plan: 'p', kind: 'default', leadTier: 2, weight: 'light' };
  const mid = { id: 'sonnet', label: 'Sonnet', enabled: true, available: true, providerEnabled: true, plan: 'q', kind: 'default', leadTier: 1, weight: 'default' };
  const flagship = { id: 'fable', label: 'Fable', enabled: true, available: true, providerEnabled: true, plan: 'r', kind: 'premium', leadTier: 0, weight: 'premium' };
  const rich = [{ id: 'p', readable: true, leftPct: 10 }, { id: 'q', readable: true, leftPct: 90 }, { id: 'r', readable: true, leftPct: 95 }];
  const running = [{ subTaskId: 's1', modelId: 'haiku' }];
  const open = quotaSwitches({ running, models: [low, mid, flagship], plans: rich, thresholdPercent: 35 });
  assert.equal(open[0].toModelId, 'fable');
  const safe = quotaSwitches({ running, models: [low, mid, flagship], plans: rich, thresholdPercent: 35, allowed: model => model.leadTier !== 0 });
  assert.equal(safe[0].toModelId, 'sonnet');
  const ultra = quotaSwitches({ running, models: [low, mid, flagship], plans: rich, thresholdPercent: 35, allowed: model => model.leadTier === 2 });
  assert.equal(ultra.length, 0);
  assert.equal(permitsAssignment(flagship, { title: 'Implement', savingMode: 'safe' }), false);
  assert.equal(permitsAssignment(mid, { title: 'Implement', savingMode: 'safe' }), true);
  assert.equal(permitsAssignment(flagship, { title: 'Implement' }), true);
  assert.equal(savingModeError(flagship, 'ultra-safe', 0), 'Fable is flagship, and Ultra safe does not allow it.');
  assert.equal(savingModeError(low, 'normal', 2), null);
});

test('briefing names the mode only when the project has one', () => {
  const plain = compactState({ focus: { id: 'p', name: 'P', lastCommit: 'a', dirty: [] } });
  assert.equal(plain.savingMode, undefined);
  const safe = compactState({ focus: { id: 'p', name: 'P', lastCommit: 'a', dirty: [], savingMode: 'safe' } });
  assert.equal(safe.savingMode, 'safe');
  assert.equal(safe.focus.savingMode, 'safe');
});

test('phone status and alerts name the saving mode in English and Italian', () => {
  const projects = [{ id: 'a', name: 'Alpha', status: 'watching', chatMode: 'plan', savingMode: 'safe', step: { step: 1, total: 2 } }];
  assert.match(formatStatusMessage(projects, 'en'), /<b>Saving:<\/b> Safe/);
  assert.match(formatStatusMessage(projects, 'it'), /<b>Risparmio:<\/b> Sicura/);
  assert.match(formatStatusMessage([{ id: 'b', name: 'Beta', status: 'idle' }], 'en'), /<b>Saving:<\/b> Normal/);
  const alert = formatTelegramAlertMessage({ type: 'started', projectName: 'Ship', goalTitle: 'Goal', step: { step: 2, total: 5 }, savingMode: 'ultra-safe' });
  assert.match(alert, /🧭 Started ▶️\n■■□□□ 2\/5/);
  assert.match(alert, /<b>Saving:<\/b> Ultra safe/);
  const italian = formatTelegramAlertMessage({ type: 'started', projectName: 'Nave', goalTitle: 'Meta', savingMode: 'safe', language: 'it' });
  assert.match(italian, /<b>Risparmio:<\/b> Sicura/);
});

test('the lead prompt names Safe and Ultra safe, and Normal adds nothing', () => {
  const base = { question: 'Ship it', model: { label: 'Lead' }, state: { focus: { name: 'Demo' } } };
  assert.doesNotMatch(askPrompt(base), /Saving mode is/);
  assert.match(askPrompt({ ...base, state: { focus: { name: 'Demo', savingMode: 'safe' } } }), /Saving mode is Safe/);
  assert.match(askPrompt({ ...base, state: { focus: { name: 'Demo', savingMode: 'ultra-safe' } } }), /light model verifies/);
});
