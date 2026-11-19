'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/19-gate.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job19-sheet.test.cjs'
]);
const engineRun = [
  'no model call',
  'No model is invoked by the gate',
  'releases the reviewer',
  'pull request ready',
  'green worker report',
  'exit zero without assertions'
];
const branchGuard = [
  'Candidate tampering',
  'package.json',
  'cannot grant that authorization',
  'A stays selected',
  'on its own authority',
  'cannot trigger, substitute for, or satisfy it'
];

function read(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}
function git(args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, (result.stderr || result.stdout || '').slice(0, 500));
  return (result.stdout || '').replace(/\r\n/g, '\n').trim();
}
function flat(text) {
  return text.replace(/\s+/g, ' ');
}
function assessSheet(sheet) {
  const errors = [];
  const text = flat(sheet);
  for (const phrase of engineRun) {
    if (!text.includes(phrase)) errors.push('engine-run missing "' + phrase + '"');
  }
  for (const phrase of branchGuard) {
    if (!text.includes(phrase)) errors.push('branch guard missing "' + phrase + '"');
  }
  if (/the gate is implemented/i.test(sheet)) errors.push('sheet marks the gate implemented');
  if (!/Open design decisions/.test(sheet)) errors.push('owner decisions removed');
  return errors;
}
function without(sheet, phrase) {
  assert.ok(sheet.includes(phrase), 'fixture phrase missing: ' + phrase);
  return sheet.split(phrase).join('');
}

test('job 19 sheet has the engine run the checks and stops a branch changing them', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping the engine start, a tamper case, or marking the gate built fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['engine start', without(sheet, 'on its own authority')],
    ['no model', without(sheet, 'No model is invoked by the gate')],
    ['before review', without(sheet, 'releases the reviewer')],
    ['work ready', without(sheet, 'pull request ready')],
    ['worker claim', without(sheet, 'green worker report')],
    ['tamper', without(sheet, 'Candidate tampering')],
    ['package script', without(sheet, 'package.json')],
    ['builder authorization', without(sheet, 'cannot grant that authorization')],
    ['built gate', sheet + '\nThe gate is implemented.\n']
  ];
  for (const [name, broken] of cases) {
    assert.ok(assessSheet(broken).length > 0, name);
  }
});

test('parallel assessments do not mix a sound sheet with a branch that starts its own gate', async () => {
  const sheet = read(sheetPath);
  const [sound, broken] = await Promise.all([
    Promise.resolve().then(() => assessSheet(sheet)),
    Promise.resolve().then(() => assessSheet(without(sheet, 'on its own authority')))
  ]);
  assert.deepEqual(sound, []);
  assert.ok(broken.some(error => error.includes('on its own authority')));
  assert.equal(broken.some(error => error.includes('package.json')), false);
});

test('the verdict stays on the sheet branch and does not add a gate', () => {
  const changed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean);
  if (!changed.length || changed.some(file => !allowedDiff.has(file))) return;
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main...HEAD']), '');
  assert.equal(fs.existsSync(path.join(root, 'engine/job19-gate.cjs')), false);
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 19 \|.*\| `feat\/gate` \| Done, waiting for review \|/);
  for (const job of ['20', '21', '22']) assert.match(next, new RegExp('\\| ' + job + ' \\|.*\\| To do \\|'));
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 19 \| #\? \|/);
  assert.match(built, /claude-sonnet-4-6/);
  assert.match(built, /cannot trigger or satisfy it/);
  assert.doesNotMatch(built, /\| 19 \| #\d+/);
  const branch = git(['branch', '--show-current']);
  if (branch) assert.equal(branch, 'feat/gate');
});
