'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { jobFromFacts } = require('./orchestrator.js');

const longGoal = 'Close the read-only check between this folder and GitHub: start from tools/folder_repo_check.py and tests/test_folder_repo_check.py, '
  + 'run the tests and fix only the failures, then deliver the report of local main against GitHub (3 ahead, 0 behind, untracked sources in tools/ and the test, __pycache__ ignored). '
  + 'No commit, push or history rewrite. Build on Gemini 3.1 Pro; independent verification on Opus 5.5. '.repeat(2);
const gitFacts = { git: true, lastSubject: "Merge commit 'dfadf057'", lastCommit: 'dfadf057 Merge', dirty: ['tools/folder_repo_check.py'], recent: [] };

test('the work is titled with its goal, also in a git project', () => {
  const job = jobFromFacts({ direction: 'Close the read-only folder and GitHub check' }, gitFacts);
  assert.equal(job.title, 'Close the read-only folder and GitHub check');
});

test('the crew receives the whole goal, not a cut version', () => {
  assert.ok(longGoal.length > 400);
  const job = jobFromFacts({ direction: longGoal }, gitFacts);
  assert.ok(job.detail.includes(longGoal.trim()), 'the full goal is in the instructions');
  assert.ok(job.title.length <= 120);
});

test('with no goal, a git project continues from its latest changes', () => {
  assert.match(jobFromFacts({ direction: '' }, gitFacts).title, /^Continue: Merge commit/);
});
