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

test('a COMPLETE result naming a missing file fails; deletions and other statuses do not', () => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const { spawnSync } = require('node:child_process');
  const { missingClaims, rejectMissingClaims } = require('./worker-result.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-claims-'));
  try {
    fs.writeFileSync(path.join(dir, 'real.txt'), 'x');
    const claim = (status, filesChanged) => ({ status, filesChanged, blockers: [] });
    assert.deepEqual(missingClaims(claim('COMPLETE', ['real.txt', path.join(dir, 'real.txt')]), dir), []);
    assert.deepEqual(missingClaims(claim('COMPLETE', ['ghost.txt']), dir), ['ghost.txt']);
    assert.deepEqual(missingClaims(claim('BLOCKED', ['ghost.txt']), dir), []);
    const rejected = rejectMissingClaims(claim('COMPLETE', ['ghost.txt']), dir);
    assert.equal(rejected.status, 'FAILED'); assert.match(rejected.blockers[0], /ghost\.txt/);
    spawnSync('git', ['init', '-q'], { cwd: dir }); fs.writeFileSync(path.join(dir, 'gone.txt'), 'x'); spawnSync('git', ['add', 'gone.txt'], { cwd: dir }); fs.rmSync(path.join(dir, 'gone.txt'));
    assert.deepEqual(missingClaims(claim('COMPLETE', ['gone.txt']), dir), []);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
