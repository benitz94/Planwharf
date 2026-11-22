'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const {
  LINE_MAX,
  SUMMARY_MAX,
  SPLIT_LINE_ASK,
  SUMMARY_ASK,
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

test('flat removes markdown marks and normalises whitespace', () => {
  assert.equal(flat('  ## Step 1: **Build** the `weather` page\n* item 1\n* item 2  '), 'Step 1: Build the weather page item 1 item 2');
  assert.equal(flat(null), '');
});

test('clip truncates cleanly at word boundaries with an ellipsis', () => {
  assert.equal(clip('A quick brown fox jumps over the lazy dog', 25), 'A quick brown fox jumps…');
  assert.equal(clip('Short text', 20), 'Short text');
});

test('firstSentence extracts up to the first terminal punctuation or colon', () => {
  assert.equal(firstSentence('Opus is building the weather page. It also adds tests.'), 'Opus is building the weather page.');
  assert.equal(firstSentence('Build the forecast: create API and UI components'), 'Build the forecast');
  assert.equal(firstSentence('No punctuation sentence'), 'No punctuation sentence');
});

test('stepLine returns the lead line when present', () => {
  const sub = { id: 's1', title: 'Implement feature A', leadLine: 'Opus is building the weather page.' };
  assert.deepEqual(stepLine(sub), { line: 'Opus is building the weather page.', from: 'lead' });

  const subAlt = { id: 's1', title: 'Implement feature A', line: 'Opus builds feature A.' };
  assert.deepEqual(stepLine(subAlt), { line: 'Opus builds feature A.', from: 'lead' });
});

test('stepLine falls back to a safe short sentence when lead gives none', () => {
  const sub = { id: 's1', title: 'Add 7-day forecast page. It connects to the weather API.' };
  assert.deepEqual(stepLine(sub), { line: 'Add 7-day forecast page.', from: 'fallback' });

  const subVerify = { id: 's2', title: 'Verify the work independently against the job: check everything.' };
  assert.deepEqual(stepLine(subVerify), { line: 'Checks the whole work and runs the tests.', from: 'fallback' });

  const subEmpty = { id: 's3', title: '' };
  assert.deepEqual(stepLine(subEmpty), { line: '', from: 'none' });
  assert.deepEqual(stepLine(null), { line: '', from: 'none' });
});

test('stepLine caps text to LINE_MAX characters', () => {
  const veryLong = 'A'.repeat(50) + ' ' + 'B'.repeat(50);
  const sub = { id: 's1', leadLine: veryLong };
  const res = stepLine(sub);
  assert.ok(res.line.length <= LINE_MAX);
  assert.ok(res.line.endsWith('…'));
});

test('workSummary returns the lead summary when present', () => {
  const mega = { id: 'm1', leadSummary: 'Built the forecast page and verified weather data. Tests pass.' };
  assert.deepEqual(workSummary(mega, [], []), {
    summary: 'Built the forecast page and verified weather data. Tests pass.',
    from: 'lead'
  });
});

test('workSummary falls back to last finished build step result or work title', () => {
  const mega = { id: 'm1', title: 'Add weather forecasting' };
  const subTasks = [
    { id: 's1', title: 'Build weather page' },
    { id: 's2', title: 'Verify the work independently against the job' }
  ];
  const results = [
    { subTaskId: 's1', status: 'COMPLETE', summary: 'Added 7-day forecast component and unit tests. All pass.' },
    { subTaskId: 's2', status: 'COMPLETE', summary: 'Verified everything independently.' }
  ];
  assert.deepEqual(workSummary(mega, results, subTasks), {
    summary: 'Added 7-day forecast component and unit tests.',
    from: 'fallback'
  });

  // Fallback to title when no build step result summary
  assert.deepEqual(workSummary(mega, [], subTasks), {
    summary: 'Add weather forecasting',
    from: 'fallback'
  });

  // None when nothing present
  assert.deepEqual(workSummary({}, [], []), {
    summary: '',
    from: 'none'
  });
});

test('workSummary caps text to SUMMARY_MAX characters and 2 sentences', () => {
  const threeSentences = 'First sentence. Second sentence. Third sentence is dropped.';
  const mega = { id: 'm1', leadSummary: threeSentences };
  const res = workSummary(mega, [], []);
  assert.equal(res.summary, 'First sentence. Second sentence.');
});

test('readSplitLines and readSummary parse JSON answers from the lead', () => {
  const splitText = 'Here is the plan:\n```json\n{"parts":[{"title":"A","line":"Opus builds part A"},{"title":"B"}],"verify":"v"}\n```';
  assert.deepEqual(readSplitLines(splitText), ['Opus builds part A', '']);

  const reviewText = 'Review complete: {"done":true,"summary":"Built weather UI and passed tests. Ready to ship."}';
  assert.equal(readSummary(reviewText), 'Built weather UI and passed tests. Ready to ship.');
  assert.equal(readSummary('no json here'), '');
});

test('forWork formats work summary and step lines for native client consumption', () => {
  const mega = { id: 'm1', leadSummary: 'All features built cleanly.' };
  const subTasks = [
    { id: 's1', title: 'Build weather page', leadLine: 'Opus builds the forecast' },
    { id: 's2', title: 'Verify the work independently against the job' }
  ];
  const view = forWork(mega, subTasks, []);
  assert.equal(view.summary, 'All features built cleanly.');
  assert.equal(view.summaryFrom, 'lead');
  assert.equal(view.steps.length, 2);
  assert.deepEqual(view.steps[0], { id: 's1', line: 'Opus builds the forecast', from: 'lead' });
  assert.deepEqual(view.steps[1], { id: 's2', line: 'Checks the whole work and runs the tests.', from: 'fallback' });
});

test('asks prompts are defined and clear', () => {
  assert.ok(SPLIT_LINE_ASK.includes('"line"'));
  assert.ok(SUMMARY_ASK.includes('"summary"'));
});
