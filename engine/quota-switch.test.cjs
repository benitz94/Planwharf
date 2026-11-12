'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { quotaLeft, lowQuota, quotaSwitches, AUTO_SWITCH_DEFAULT } = require('./model-policy.cjs');

const plans = [
  { id: 'chatgpt', readable: true, leftPct: 12 },
  { id: 'claude', readable: true, leftPct: 60 },
  { id: 'gemini', readable: true, leftPct: 98 },
  { id: 'grok', readable: false }
];
const model = (id, plan, extra = {}) => ({ id, label: id, plan, enabled: true, available: true, ...extra });
const models = [model('terra', 'chatgpt'), model('sonnet', 'claude'), model('gemini-pro', 'gemini'), model('grok', 'grok')];

test('the default threshold is 20% left', () => {
  assert.equal(AUTO_SWITCH_DEFAULT.thresholdPercent, 20);
  assert.equal(AUTO_SWITCH_DEFAULT.enabled, true);
});

test('quota is low at or under the threshold, or when exhausted; unread quota is not low', () => {
  assert.equal(quotaLeft(models[0], plans), 12);
  assert.equal(lowQuota(models[0], plans, 20), true);
  assert.equal(lowQuota(models[1], plans, 20), false);
  assert.equal(lowQuota(model('x', 'chatgpt'), [{ id: 'chatgpt', readable: true, leftPct: 50, exhausted: true }], 20), true);
  assert.equal(lowQuota(models[3], plans, 20), false);
});

test('a working step on a low model moves to the eligible model with the most quota left', () => {
  const running = [{ subTaskId: 's1', modelId: 'terra' }, { subTaskId: 's2', modelId: 'sonnet' }];
  const switches = quotaSwitches({ running, models, plans, thresholdPercent: 20, eligible: () => true });
  assert.deepEqual(switches, [{ subTaskId: 's1', fromModelId: 'terra', toModelId: 'gemini-pro', leftPercent: 12 }]);
});

test('no switch without an eligible model above the threshold, and none when switching is off', () => {
  const running = [{ subTaskId: 's1', modelId: 'terra' }];
  assert.deepEqual(quotaSwitches({ running, models, plans, thresholdPercent: 20, eligible: candidate => candidate.id === 'terra' }), []);
  assert.deepEqual(quotaSwitches({ running, models, plans, thresholdPercent: 20, enabled: false, eligible: () => true }), []);
  assert.deepEqual(quotaSwitches({ running, models: [models[0], models[3]], plans, thresholdPercent: 20, eligible: () => true }), []);
});

test('the switch keeps the check independent: it prefers a company the other steps do not use', () => {
  // Build runs on terra (ChatGPT, low); Verify uses a Gemini model. With room in both, Claude is
  // preferred to Gemini, though Gemini has more quota, so build and check stay with different companies.
  const withProviders = [model('terra', 'chatgpt', { provider: 'codex' }), model('sonnet', 'claude', { provider: 'claude' }), model('gemini-pro', 'gemini', { provider: 'antigravity' })];
  const running = [{ subTaskId: 's1', modelId: 'terra', avoidProviders: ['antigravity'] }];
  assert.equal(quotaSwitches({ running, models: withProviders, plans, thresholdPercent: 20, eligible: () => true })[0].toModelId, 'sonnet');
  // Only the other company's model has room: the step still moves rather than stop.
  assert.equal(quotaSwitches({ running, models: [withProviders[0], withProviders[2]], plans, thresholdPercent: 20, eligible: () => true })[0].toModelId, 'gemini-pro');
});
