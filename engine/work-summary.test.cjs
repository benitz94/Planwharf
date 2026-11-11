'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { workSummary } = require('../work-summary.js');

const label = id => ({ opus: 'Opus 5.5', astra: 'GPT-6 Astra', grok: 'Grok 4.7 Business' })[id] || id;
const VERIFY = 'Verify the work independently against the job: read the requirements and run the tests.';
const finished = {
  megaTask: { status: 'COMPLETE' },
  subTasks: [
    { id: 's1', title: 'Implement the whole job in the project folder.', status: 'COMPLETE', assignedModelId: 'opus' },
    { id: 's2', title: VERIFY, status: 'COMPLETE', assignedModelId: 'astra', takeover: { fromModelId: 'grok' } }
  ],
  results: [
    { subTaskId: 's1', status: 'COMPLETE', summary: 'Built it.', testsReported: 2 },
    { subTaskId: 's2', status: 'BLOCKED', summary: 'Model changed to GPT-6 Astra; the step restarts with it.', modelChange: true },
    { subTaskId: 's2', status: 'COMPLETE', summary: 'Checked it.', testsReported: 1 }
  ]
};

test('a finished work explains what happened, in English', () => {
  const lines = workSummary(finished, { stopReason: 'LEAD_UNCONFIRMED', changedFiles: 5, modelLabel: label });
  const text = lines.join('\n');
  assert.match(lines[0], /All 2 steps finished, but the lead did not confirm the goal/);
  assert.match(text, /Build: Opus 5\.5/);
  assert.match(text, /Verify: GPT-6 Astra, taking over from Grok 4\.7 Business/);
  assert.match(text, /No problems were reported/);
  assert.match(text, /3 test runs/);
  assert.match(text, /5 files changed and not yet committed/);
});

test('problems are named, and a model change is not a problem', () => {
  const failed = { ...finished, megaTask: { status: 'FAILED' }, subTasks: [finished.subTasks[0], { ...finished.subTasks[1], status: 'FAILED' }],
    results: [...finished.results.slice(0, 2), { subTaskId: 's2', status: 'FAILED', summary: 'Tests failed on Windows paths.' }] };
  const text = workSummary(failed, { changedFiles: 0, modelLabel: label }).join('\n');
  assert.match(text, /1 of 2 steps finished/);
  assert.match(text, /Problem in Verify: Tests failed on Windows paths\./);
  assert.doesNotMatch(text, /Problem.*Model changed/);
  assert.match(text, /No files are left uncommitted/);
});

test('a confirmed goal says so', () => {
  assert.match(workSummary(finished, { stopReason: 'GOAL_REACHED', changedFiles: null, modelLabel: label })[0], /All 2 steps finished and the lead confirmed the goal/);
});
