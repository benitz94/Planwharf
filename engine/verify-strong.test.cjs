'use strict';
// Verify checks the whole work and fixes it: it goes to a stronger model than a light one (Flash, mini,
// Luna, Haiku) whenever one can take it; a light model verifies only when nobody else can.
const test = require('node:test');
const assert = require('node:assert/strict');
const { pickWorker, ableToVerify } = require('./orchestrator.js');
const { VERIFY_SLICE } = require('./model-roles.cjs');

const model = (id, label, extra = {}) => ({ id, label, provider: 'grok', adapter: 'grok', cli: 'grok-' + id, providerCatalog: true, plan: id, enabled: true, available: true, cost: 1, limit: 100, kind: 'default',
  capabilities: { canCode: true, canUseTools: true }, roles: { worker: true, verifier: true }, ...extra });
const verify = { kind: 'verify', title: VERIFY_SLICE };

test('a light model is not chosen to verify when a stronger one can', () => {
  const flash = model('flash', 'Gemini 3.8 Flash (High)'), grok = model('grok', 'Grok 4.7 Business');
  assert.equal(pickWorker([flash, grok], { job: verify }).id, 'grok');
  assert.deepEqual(ableToVerify([flash, model('luna', 'GPT-5.6 Luna'), grok]).map(item => item.id), ['grok']);
});

test('a light model still verifies when it is the only one', () => {
  assert.equal(pickWorker([model('flash', 'Gemini 3.8 Flash (High)')], { job: verify }).id, 'flash');
});

test('building is not affected: the first model in order still builds', () => {
  const flash = model('flash', 'Gemini 3.8 Flash (High)'), grok = model('grok', 'Grok 4.7 Business');
  assert.equal(pickWorker([flash, grok], { job: { kind: 'build', title: 'Build it' } }).id, 'flash');
});
