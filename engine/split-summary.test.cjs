'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { splitSummary } = require('./execution.cjs');

const label = id => id.toUpperCase();

test('one build step and a check never reads as "0 parts"', () => {
  const subs = [{ id: 's1', title: 'Implement job 26', assignedModelId: 'grok', dependencies: [] },
    { id: 's2', title: 'Check', assignedModelId: 'fable', dependencies: ['s1'] }];
  assert.strictEqual(splitSummary(subs, label), 'Lead kept the work as one build step and a check of the whole: Implement job 26 (GROK) · Check (FABLE).');
});

test('several parts and a check', () => {
  const subs = [{ id: 'a', title: 'A', assignedModelId: 'x', dependencies: [] }, { id: 'b', title: 'B', assignedModelId: 'y', dependencies: [] },
    { id: 'c', title: 'C', assignedModelId: 'z', dependencies: ['a', 'b'] }];
  assert.strictEqual(splitSummary(subs, label, 'Two at once.'), 'Lead split the work into 2 parts and a check of the whole: A (X) · B (Y) · C (Z). Why: Two at once.');
});
