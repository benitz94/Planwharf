'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { parseDecision, strictDecisionPrompt } = require('./autonomy.cjs');
const { projectFacts } = require('./supervisor.cjs');
const { availableAgain } = require('./model-policy.cjs');

test('the lead decision is read even with prose or braces around the JSON', () => {
  const reply = 'I checked {the folder} and the tests.\n```json\n{"done": true, "next": null, "assumptions": ["tests are the evidence"]}\n```\nAll good {really}.';
  assert.deepEqual(parseDecision(reply), { done: true, next: null, assumptions: ['tests are the evidence'] });
  assert.equal(parseDecision('No decision here {at all}.'), null);
});

test('an unreadable decision is asked again with a strict prompt', () => {
  const strict = strictDecisionPrompt('Is the goal reached?');
  assert.match(strict, /Is the goal reached\?/);
  assert.match(strict, /only the JSON object/);
});

test('a model change is not reported as a failed assignment', () => {
  const snap = { projects: [{ id: 'p', megaTaskId: 'm', status: 'watching', dispatch: [] }],
    execution: { subTasks: [], megaTasks: [{ id: 'm', projectId: 'p' }], results: [
      { resultId: 'r1', megaTaskId: 'm', status: 'BLOCKED', summary: 'Model changed to GPT-6 Astra; the step restarts with it.', modelChange: true },
      { resultId: 'r2', megaTaskId: 'm', status: 'FAILED', summary: 'Real failure.' }] } };
  const keys = projectFacts(snap, 'p').alerts.map(alert => alert.kind);
  assert.ok(keys.includes('result-r2'));
  assert.ok(!keys.includes('result-r1'));
});

test('a model marked unavailable after a failure is tried again after a few minutes', () => {
  const at = Date.parse('2026-11-11T16:00:00Z');
  const model = { id: 'gemini', available: false, unavailableReason: 'CLI failed', unavailableAt: new Date(at).toISOString() };
  assert.equal(availableAgain(model, at + 60000), false);
  assert.equal(availableAgain(model, at + 6 * 60000), true);
  assert.equal(availableAgain({ id: 'x', available: false, unavailableReason: 'not installed' }, at + 6 * 60000), false, 'only failures with a time are retried');
});
