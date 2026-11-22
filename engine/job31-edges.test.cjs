'use strict';
// Edges the Job 31 sheet suite leaves out: corrupt lead JSON, missing or wrong-typed
// fields, failed/blocked results, concurrent reads, and recovery when the lead writes none.
const test = require('node:test');
const assert = require('node:assert/strict');
const { Worker } = require('node:worker_threads');
const {
  LINE_MAX,
  SUMMARY_MAX,
  flat,
  clip,
  firstSentence,
  shortLine,
  stepLine,
  workSummary,
  readSplitLines,
  readSummary,
  forWork
} = require('./plain-lines.cjs');

const VERIFY = 'Verify the work independently against the job';

test('clip and flat recover from empty, unclosed and non-string input', () => {
  assert.equal(clip('', 10), '');
  assert.equal(clip('abc', 3), 'abc');
  assert.equal(clip('hello world', 1), '…');
  assert.equal(clip('A'.repeat(100), LINE_MAX).length, LINE_MAX);
  assert.ok(clip('A'.repeat(100), LINE_MAX).endsWith('…'));
  assert.equal(flat(null), '');
  assert.equal(flat(12), '');
  assert.equal(flat({}), '');
  assert.equal(flat('before ```code'), 'before `code');
  assert.equal(shortLine('   '), '');
});

test('firstSentence keeps decimals and splits on ! and ?', () => {
  assert.equal(firstSentence('Stop! Then go.'), 'Stop!');
  assert.equal(firstSentence('Ready? Then go.'), 'Ready?');
  assert.equal(firstSentence('Version 1.2 is ready. Next.'), 'Version 1.2 is ready.');
  assert.equal(firstSentence('Build:'), 'Build:');
  assert.equal(firstSentence('Build: add the page'), 'Build');
});

test('stepLine ignores blank or non-string lead lines and prefers leadLine over line', () => {
  assert.deepEqual(stepLine({ title: 'T', leadLine: '   ' }), { line: 'T', from: 'fallback' });
  assert.deepEqual(stepLine({ title: 'T', leadLine: 12 }), { line: 'T', from: 'fallback' });
  assert.deepEqual(stepLine({ leadLine: 'from lead', line: 'from line', title: 'T' }), { line: 'from lead', from: 'lead' });
  assert.deepEqual(stepLine({ line: 'from line', title: 'T' }), { line: 'from line', from: 'lead' });
  assert.deepEqual(stepLine({}), { line: '', from: 'none' });
  assert.deepEqual(stepLine([]), { line: '', from: 'none' });
});

test('workSummary skips failed, blocked and verify results', () => {
  const mega = { title: 'Weather page' };
  const build = { id: 's1', title: 'Build the page' };
  const verify = { id: 's2', title: VERIFY };
  assert.deepEqual(
    workSummary(mega, [{ subTaskId: 's1', status: 'FAILED', summary: 'Boom. Bad.' }], [build]),
    { summary: 'Weather page', from: 'fallback' }
  );
  assert.deepEqual(
    workSummary(mega, [{ subTaskId: 's1', status: 'BLOCKED', summary: 'Stuck.' }], [build]),
    { summary: 'Weather page', from: 'fallback' }
  );
  assert.deepEqual(
    workSummary(mega, [
      { subTaskId: 's1', status: 'COMPLETE', summary: 'Built page.' },
      { subTaskId: 's2', status: 'COMPLETE', summary: 'Verified independently. Extra.' }
    ], [build, verify]),
    { summary: 'Built page.', from: 'fallback' }
  );
  assert.deepEqual(workSummary(null, [], []), { summary: '', from: 'none' });
  assert.deepEqual(workSummary({ leadSummary: '   ' }, [], []), { summary: '', from: 'none' });
  assert.deepEqual(
    workSummary({ goal: 'Ship the weather page. Extra detail.' }, [], []),
    { summary: 'Ship the weather page.', from: 'fallback' }
  );
});

test('readSplitLines and readSummary recover from corrupt, extra and last-wins JSON', () => {
  assert.deepEqual(readSplitLines('{not json'), []);
  assert.deepEqual(readSplitLines('{"parts":[{"line":"first"}]} and {"parts":[{"line":"second"}]}'), ['second']);
  assert.deepEqual(readSplitLines('{"parts":[{"line":"uses {braces} here"}]}'), ['uses {braces} here']);
  assert.deepEqual(readSplitLines('{"parts":[null,{"line":"ok"}]}'), ['', 'ok']);
  assert.deepEqual(readSplitLines('{"parts":[{"line":12}]}'), ['']);
  assert.deepEqual(readSplitLines('{"parts":[]}'), []);
  assert.deepEqual(readSplitLines('{"parts":[{"line":"says \\"hi\\" now"}]}'), ['says "hi" now']);
  assert.equal(readSummary('{"done":true}'), '');
  assert.equal(readSummary('{"done":true,"summary":12}'), '');
  assert.equal(readSummary('{"done":true,"summary":"old"} {"done":false,"summary":"new one."}'), 'new one.');
  assert.equal(readSummary('{"done":true,"summary":"x"'), '');
  assert.equal(readSummary(null), '');
});

test('forWork skips holes and recovers when lists are missing or the wrong type', () => {
  assert.deepEqual(forWork(null, [{ id: 's1', title: 'A' }], []), {
    summary: '',
    summaryFrom: 'none',
    steps: [{ id: 's1', line: 'A', from: 'fallback' }]
  });
  assert.deepEqual(forWork({}, [null, { id: 's1', title: 'Build it.' }], []), {
    summary: '',
    summaryFrom: 'none',
    steps: [{ id: 's1', line: 'Build it.', from: 'fallback' }]
  });
  assert.deepEqual(forWork({ leadSummary: 'All good.' }, undefined, undefined), {
    summary: 'All good.',
    summaryFrom: 'lead',
    steps: []
  });
  assert.deepEqual(forWork({}, 'nope', 'nope'), {
    summary: '',
    summaryFrom: 'none',
    steps: []
  });
});

test('plain-lines stays the same when many workers read at once', async () => {
  const mega = { leadSummary: 'Built the forecast page.' };
  const subs = [
    { id: 's1', title: 'Build weather page', leadLine: 'Opus builds the forecast' },
    { id: 's2', title: VERIFY }
  ];
  const results = [{ subTaskId: 's1', status: 'COMPLETE', summary: 'Added charts.' }];
  const expected = forWork(mega, subs, results);
  const script = `
    const { parentPort, workerData } = require('node:worker_threads');
    const p = require(${JSON.stringify(require.resolve('./plain-lines.cjs'))});
    parentPort.postMessage(p.forWork(workerData.mega, workerData.subs, workerData.results));
  `;
  const workers = Array.from({ length: 8 }, () => new Worker(script, { eval: true, workerData: { mega, subs, results } }));
  const got = await Promise.all(workers.map(worker => new Promise((resolve, reject) => {
    worker.once('message', resolve);
    worker.once('error', reject);
  })));
  await Promise.all(workers.map(worker => worker.terminate()));
  for (const view of got) assert.deepEqual(view, expected);
  assert.ok(expected.summary.length <= SUMMARY_MAX);
});
