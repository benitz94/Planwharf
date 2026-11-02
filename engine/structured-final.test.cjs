'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { supportsFinalResult, resultSchema, readFinalResult } = require('./structured-final.cjs');

const value = { status: 'COMPLETE', summary: 'probe ok' };

test('only adapters with a verified format use final output', () => {
  assert.equal(supportsFinalResult({ adapter: 'claude' }), true);
  for (const provider of ['grok', 'codex', 'antigravity', 'api']) assert.equal(supportsFinalResult({ provider }), false);
});

test('the schema pins the attempt identity', () => {
  const schema = resultSchema({ resultId: 'R', attemptId: 'A', subTaskId: 'S', megaTaskId: 'M' });
  assert.deepEqual(schema.properties.attemptId.enum, ['A']);
  assert.ok(schema.required.includes('megaTaskId') && !schema.required.includes('createdAt'));
});

test('reads the Claude result event and refuses error results', () => {
  const stream = [JSON.stringify({ type: 'system' }), JSON.stringify({ type: 'result', subtype: 'success', is_error: false, structured_output: value })].join('\r\n') + '\r\n';
  assert.deepEqual(readFinalResult('claude', stream), value);
  assert.equal(readFinalResult('claude', JSON.stringify({ type: 'result', is_error: true, structured_output: value })), null);
  assert.equal(readFinalResult('claude', JSON.stringify({ type: 'result', is_error: false })), null);
});

test('free text is never read as a result', () => {
  assert.equal(readFinalResult('claude', JSON.stringify(value)), null);
  assert.equal(readFinalResult('claude', 'plain text'), null);
});

test('other adapters and empty output yield nothing', () => {
  assert.equal(readFinalResult('grok', JSON.stringify({ structuredOutput: value })), null);
  assert.equal(readFinalResult('claude', ''), null);
});
