'use strict';

// tickStep does not need git: it only rewrites .planwharf/plan.md.
// These cases used to go through prepare(), which can miss the plan file when
// many git fixtures run at once, so the fallback looked like a product failure.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const planFile = require('./plan-file.cjs');

function folder() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tick-'));
  planFile.writePlan(dir, { goal: 'fallback goal', steps: [{ role: 'Build', model: 'Grok' }] });
  return dir;
}

test('tickStep falls back to the first unchecked step when role and title do not match', () => {
  const dir = folder();
  try {
    const ticked = planFile.tickStep(dir, { role: 'Research', model: 'Totally Different', commit: 'abc1234' });
    assert.equal(ticked, true);
    assert.match(planFile.read(dir), /- \[x\] Build \(Grok\) — abc1234/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep returns false when every step is already ticked', () => {
  const dir = folder();
  try {
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok', commit: 'aaa' }), true);
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok', commit: 'bbb' }), false);
    assert.match(planFile.read(dir), /aaa/);
    assert.ok(!planFile.read(dir).includes('bbb'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep returns false when the plan file is missing', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tick-empty-'));
  try {
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok' }), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep matches by title when distinct titles are present', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tick-title-'));
  try {
    planFile.writePlan(dir, {
      goal: 'title match goal',
      steps: [
        { role: 'Build', model: 'Grok', title: 'Part One' },
        { role: 'Build', model: 'Grok', title: 'Part Two' }
      ]
    });
    // Tick Part Two first
    const tickedTwo = planFile.tickStep(dir, { title: 'Part Two', commit: 'sha-two' });
    assert.equal(tickedTwo, true);
    let content = planFile.read(dir);
    assert.match(content, /- \[ \] Build \(Grok\): Part One/);
    assert.match(content, /- \[x\] Build \(Grok\): Part Two — sha-two/);

    // Now tick Part One with whitespace normalization in title
    const tickedOne = planFile.tickStep(dir, { title: '  Part   One  ', commit: 'sha-one' });
    assert.equal(tickedOne, true);
    content = planFile.read(dir);
    assert.match(content, /- \[x\] Build \(Grok\): Part One — sha-one/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep handles twin steps without titles in order', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tick-twins-'));
  try {
    // Two twin steps with role only
    fs.mkdirSync(path.join(dir, '.planwharf'), { recursive: true });
    fs.writeFileSync(path.join(dir, '.planwharf', 'plan.md'), [
      '# Work plan',
      '## Goal',
      'twin goal',
      '## Steps',
      '- [ ] Build (Grok)',
      '- [ ] Build (Grok)'
    ].join('\n'));

    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok', commit: 'first' }), true);
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok', commit: 'second' }), true);
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok', commit: 'third' }), false);
    const content = planFile.read(dir);
    assert.match(content, /- \[x\] Build \(Grok\) — first/);
    assert.match(content, /- \[x\] Build \(Grok\) — second/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep defaults commit note to "not committed" when omitted or empty', () => {
  const dir = folder();
  try {
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok' }), true);
    assert.match(planFile.read(dir), /- \[x\] Build \(Grok\) — not committed/);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep recovers from empty or step-free plan file', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tick-nosteps-'));
  try {
    fs.mkdirSync(path.join(dir, '.planwharf'), { recursive: true });
    fs.writeFileSync(path.join(dir, '.planwharf', 'plan.md'), '## Goal\nSome goal\n');
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok' }), false);

    fs.writeFileSync(path.join(dir, '.planwharf', 'plan.md'), '');
    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok' }), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('tickStep preserves other sections and handles CRLF line endings', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tick-crlf-'));
  try {
    fs.mkdirSync(path.join(dir, '.planwharf'), { recursive: true });
    const crlfText = [
      '# Work plan',
      '',
      '## Goal',
      '',
      'A specific goal',
      '',
      '## Base',
      '',
      'origin/main 1234567',
      '',
      '## Branch',
      '',
      'feat/test',
      '',
      '## Steps',
      '',
      '- [ ] Build (Grok)',
      '',
      '## Rules',
      '',
      'No push or merge. Files outside the goal are not touched.',
      '',
      '## Done',
      '',
      'Summary of work',
      ''
    ].join('\r\n');
    fs.writeFileSync(path.join(dir, '.planwharf', 'plan.md'), crlfText);

    assert.equal(planFile.tickStep(dir, { role: 'Build', model: 'Grok', commit: 'abc1234' }), true);
    const content = planFile.read(dir);
    assert.match(content, /- \[x\] Build \(Grok\) — abc1234/);
    const parsed = planFile.parse(content);
    assert.equal(parsed.goal, 'A specific goal');
    assert.equal(parsed.base, 'origin/main 1234567');
    assert.equal(parsed.branch, 'feat/test');
    assert.equal(parsed.rules, 'No push or merge. Files outside the goal are not touched.');
    assert.equal(parsed.done, 'Summary of work');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('pullRequestTitle handles edges: long goals, backticks, following clauses, and invalid fuller titles', () => {
  // Goal with following clause
  assert.equal(planFile.pullRequestTitle('Implement job 32, following the architecture notes'), 'Implement job 32');

  // Goal with backticks and trailing punctuation
  assert.equal(planFile.pullRequestTitle('Fix `broken` parser... '), 'Fix broken parser');

  // Goal over 120 chars is truncated
  const longGoal = 'Add support for very long description that goes on and on and ' + 'x'.repeat(100);
  assert.equal(planFile.pullRequestTitle(longGoal).length, 120);

  // Fuller step title with backticks or newlines is not selected
  assert.equal(planFile.pullRequestTitle('Build engine', [
    'Build engine with `backtick`',
    'Build engine\nwith newline',
    'Other step'
  ]), 'Build engine');

  // Fuller step title shorter than short goal is not selected
  assert.equal(planFile.pullRequestTitle('Build the engine component', ['Build the engine']), 'Build the engine component');

  // Empty / null / undefined goal returns empty string
  assert.equal(planFile.pullRequestTitle(''), '');
  assert.equal(planFile.pullRequestTitle(null), '');
  assert.equal(planFile.pullRequestTitle(undefined), '');
});
