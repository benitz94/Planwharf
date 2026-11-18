'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const plan = require('./plan-file.cjs');
const { gitEnvironment } = require('./project-workspace.cjs');

test('managed exclusions can be toggled without changing personal rules', t => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-plan-option-'));
  t.after(() => fs.rmSync(folder, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd: folder, encoding: 'utf8', env: gitEnvironment(), stdio: ['ignore', 'pipe', 'pipe'] });
  git('init', '-q');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.com', 'commit', '--allow-empty', '-qm', 'initial');
  const exclude = path.join(folder, '.git', 'info', 'exclude');
  fs.writeFileSync(exclude, '*.log\n');
  const prepare = keepWorkPlan => plan.prepare(folder, { goal: 'test', steps: [], keepWorkPlan });
  assert.equal(prepare(false).ok, true);
  const managed = fs.readFileSync(exclude, 'utf8');
  assert.match(managed, /# Planwharf managed work plan\n\/\.planwharf\/\n/);
  assert.equal(prepare(false).ok, true);
  assert.equal(fs.readFileSync(exclude, 'utf8'), managed);
  assert.equal(prepare(true).ok, true);
  assert.equal(fs.readFileSync(exclude, 'utf8'), '*.log\n');
  assert.match(git('status', '--porcelain'), /\.planwharf/);
  fs.rmSync(path.join(folder, '.planwharf'), { recursive: true });
  fs.writeFileSync(exclude, '*.log\n/.planwharf/\n');
  assert.equal(prepare(true).ok, true);
  assert.equal(fs.readFileSync(exclude, 'utf8'), '*.log\n/.planwharf/\n');
  assert.equal(prepare(false).ok, true);
  assert.equal(fs.readFileSync(exclude, 'utf8'), '*.log\n/.planwharf/\n');
});

test('enabled plans are included by an ordinary git commit', t => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-plan-commit-'));
  t.after(() => fs.rmSync(folder, { recursive: true, force: true }));
  const git = (...args) => execFileSync('git', args, { cwd: folder, encoding: 'utf8', env: gitEnvironment(), stdio: ['ignore', 'pipe', 'pipe'] });
  git('init', '-q');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.com', 'commit', '--allow-empty', '-qm', 'initial');
  assert.equal(plan.prepare(folder, { goal: 'keep this', keepWorkPlan: true }).ok, true);
  assert.equal(plan.prepare(folder, { goal: 'keep this', keepWorkPlan: true }).ok, true, 'the generated plan permits resuming');
  fs.writeFileSync(path.join(folder, '.planwharf', 'personal.txt'), 'personal');
  // On the work's own branch, unfinished changes do not block the same work (#119); a new work does.
  assert.equal(plan.prepare(folder, { goal: 'keep this', keepWorkPlan: true }).ok, true, 'the same work goes on');
  assert.equal(plan.prepare(folder, { goal: 'another goal on `fix/other`', keepWorkPlan: true }).ok, true, 'a new work sets untracked files aside');
  assert.equal(plan.prepare(folder, { goal: 'keep this', keepWorkPlan: true }).ok, true);
  git('add', '-A');
  git('-c', 'user.name=Test', '-c', 'user.email=test@example.com', 'commit', '-qm', 'work');
  assert.match(git('show', 'HEAD:.planwharf/plan.md'), /keep this/);
  fs.appendFileSync(path.join(folder, '.planwharf', 'plan.md'), 'personal edit\n');
  assert.equal(plan.prepare(folder, { goal: 'a third goal on `fix/third`', keepWorkPlan: true }).ok, true, 'a new work sets tracked edits aside');
});
