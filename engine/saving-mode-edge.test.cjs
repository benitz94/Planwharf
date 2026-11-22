'use strict';
// Edges the Job 29 suites leave out: Team weight against the strong-verify rule,
// and chat orders that are not typed as a single clean word.
const test = require('node:test');
const assert = require('node:assert/strict');
const { pickWorker, pickWorkers } = require('./orchestrator.js');
const saving = require('./saving-mode.cjs');
const { VERIFY_SLICE } = require('./model-roles.cjs');

const model = (id, label, extra = {}) => ({
  id, label, provider: 'grok', adapter: 'grok', cli: 'grok-' + id, providerCatalog: true,
  plan: id, enabled: true, available: true, cost: 1, limit: 100, kind: extra.kind || 'default',
  capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true },
  roles: { worker: true, verifier: true, orchestrator: true }, ...extra
});

test('Safe verify stays on a default model when Team marked a flagship as light', () => {
  const weighed = model('fable', 'Claude Fable', { kind: 'premium', weight: 'light', weightSource: 'team', leadTier: 0 });
  const mid = model('sonnet', 'Claude Sonnet', { leadTier: 1, weight: 'default', weightSource: 'lineup' });
  const light = model('haiku', 'Claude Haiku', { leadTier: 2, weight: 'light', weightSource: 'lineup' });
  const verify = { kind: 'verify', title: VERIFY_SLICE };
  assert.equal(pickWorker([weighed, mid, light], { job: verify, savingMode: 'safe' }).id, 'sonnet');
  const crew = pickWorkers([weighed, mid, light], { job: { title: 'Implement the list', kind: 'build' }, savingMode: 'safe' });
  assert.equal(crew[1].id, 'sonnet', 'the check of the work is a default model in Safe');
  const plain = model('fable', 'Claude Fable', { kind: 'premium' });
  assert.equal(pickWorker([plain, mid, light], { job: verify, savingMode: 'safe' }).id, 'sonnet');
  assert.equal(pickWorker([plain, mid, light], { job: verify, savingMode: 'ultra-safe' }).id, 'haiku');
  // A lineup flagship that Team did not reweight stays out of Ultra safe.
  assert.equal(pickWorker([weighed, light], { job: verify, savingMode: 'ultra-safe' }).id, 'fable');
});

test('chat orders survive punctuation, odd spaces, and a missing accent', () => {
  assert.deepEqual(saving.order('  Mode Safe. '), { action: 'saving-mode', savingMode: 'safe' });
  assert.deepEqual(saving.order('mode ultra  safe!'), { action: 'saving-mode', savingMode: 'ultra-safe' });
  assert.deepEqual(saving.order('modalita sicura'), { action: 'saving-mode', savingMode: 'safe' });
  assert.deepEqual(saving.order('modalità ultra sicura.'), { action: 'saving-mode', savingMode: 'ultra-safe' });
  assert.equal(saving.order('mode safe please'), null);
  assert.equal(saving.normalize('ultra__safe'), 'ultra-safe');
  assert.equal(saving.normalize('  ULTRA   SAFE  '), 'ultra-safe');
  assert.equal(saving.persisted(''), 'normal');
});
