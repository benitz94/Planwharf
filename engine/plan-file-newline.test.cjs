'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');

test('the work plan ends with exactly one newline, so a pull request that carries it passes the patch check', t => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const planFile = require('./plan-file.cjs');
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-plan-newline-'));
  t.after(() => fs.rmSync(folder, { recursive: true, force: true }));
  planFile.writePlan(folder, { goal: 'Port the modules', steps: [{ label: 'Build (A): port', done: false }], done: '' });
  assert.match(fs.readFileSync(planFile.planPath(folder), 'utf8'), /[^\n]\n$/);
  planFile.tickStep(folder, { title: 'port', commit: 'abc1234' });
  assert.match(fs.readFileSync(planFile.planPath(folder), 'utf8'), /[^\n]\n$/);
  planFile.finish(folder, { summary: 'Done.', pushed: false });
  assert.match(fs.readFileSync(planFile.planPath(folder), 'utf8'), /[^\n]\n$/);
});
