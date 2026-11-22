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
