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
const errorPaths = [
  'missing, skipped, failed, errored, timed-out, or stale',
  'Executable missing, spawn denied, invalid path, process crash',
  'persistence failure',
  'Reject traversal, symlink escape, unexpected absolute paths',
  'reach another project or attempt',
  'do not pass Planwharf tokens',
  'provider credentials, Git push credentials, or the trusted manifest store',
  'Bound wall time, output size and process trees',
  'close descendants on timeout or cancellation',
  'fake executable covers pass, nonzero exit, spawn error, timeout, cancellation, and child cleanup'
];
const recoveryPaths = [
  'Restart reads the pinned manifest',
  'resume only verified completed checks bound to the exact snapshot',
  'retry creates another run record; it does not overwrite the failure',
  'post-sync gate uses the new integrated commit and tree',
  'cannot reuse the pre-review result',
  'change during a run aborts it',
  'later change invalidates its pass',
  'revised manifest requires a fresh gate run',
  'approval does not relabel old evidence as passing'
];
const concurrencyPaths = [
  'concurrent jobs cannot read or reuse one another',
  'partial write, duplicate run, and changed candidate input cannot reuse or overwrite a pass',
  'isolated job worktree'
];
const edgeCases = [
  'without copying personal notebook data or including ignored/private files',
  'spaces and Unicode',
  'Windows child-tree cleanup',
  'Run candidate code as untrusted',
  'exit code alone cannot prove that an acceptance assertion ran',
  'sole authority',
  'dotnet build native/Planwharf.Engine',
  'dotnet test native/Planwharf.Engine.Tests',
  'node scripts/engineering/ci.cjs',
  'porting rule 12'
];
const openDecisions = [
  'How the owner approves and stores a manifest before job 20 supplies its spec workflow',
  'Which isolation mechanism is available on each platform for running candidate code',
  'How a complete pre-review snapshot includes working files without private notebook data'
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
  for (const phrase of errorPaths) {
    if (!text.includes(phrase)) errors.push('error path missing "' + phrase + '"');
  }
  for (const phrase of recoveryPaths) {
    if (!text.includes(phrase)) errors.push('recovery path missing "' + phrase + '"');
  }
  for (const phrase of concurrencyPaths) {
    if (!text.includes(phrase)) errors.push('concurrency path missing "' + phrase + '"');
  }
  for (const phrase of edgeCases) {
    if (!text.includes(phrase)) errors.push('edge case missing "' + phrase + '"');
  }
  for (const phrase of openDecisions) {
    if (!text.includes(phrase)) errors.push('open decision missing "' + phrase + '"');
  }
  if (/the gate is implemented/i.test(sheet)) errors.push('sheet marks the gate implemented');
  if (!/Open design decisions/.test(sheet)) errors.push('owner decisions removed');
  return errors;
}
function without(sheet, phrase) {
  const flattened = flat(sheet);
  assert.ok(flattened.includes(phrase), 'fixture phrase missing: ' + phrase);
  return flattened.split(phrase).join('');
}

test('job 19 sheet has the engine run the checks and stops a branch changing them', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('job 19 sheet aligns with the pipeline design gate contract', () => {
  const design = flat(read('docs/DESIGN-PIPELINE.md'));
  assert.match(design, /7\.\s+\*\*Gate\.\*\*\s+The engine runs the checks and the acceptance tests itself/);
  assert.match(design, /with no model and no tokens/);
  assert.match(design, /before any reviewer starts/);
  assert.match(design, /again after syncing with `main`, before the pull request is ready/);
  assert.match(design, /A branch cannot change the checks it is judged by/);
  const sheet = read(sheetPath);
  assert.deepEqual(assessSheet(sheet), []);
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

test('dropping an error, recovery, concurrency, edge-case, or open decision limit fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['traversal error', without(sheet, 'Reject traversal, symlink escape, unexpected absolute paths')],
    ['credential isolation', without(sheet, 'do not pass Planwharf tokens')],
    ['timeout error', without(sheet, 'close descendants on timeout or cancellation')],
    ['fake executable', without(sheet, 'fake executable covers pass, nonzero exit, spawn error, timeout, cancellation, and child cleanup')],
    ['restart recovery', without(sheet, 'Restart reads the pinned manifest')],
    ['retry isolation', without(sheet, 'retry creates another run record; it does not overwrite the failure')],
    ['post-sync gate', without(sheet, 'cannot reuse the pre-review result')],
    ['worktree change during run', without(sheet, 'change during a run aborts it')],
    ['concurrent jobs isolation', without(sheet, 'concurrent jobs cannot read or reuse one another')],
    ['race on pass record', without(sheet, 'partial write, duplicate run, and changed candidate input cannot reuse or overwrite a pass')],
    ['notebook privacy', without(sheet, 'without copying personal notebook data or including ignored/private files')],
    ['untrusted candidate code', without(sheet, 'Run candidate code as untrusted')],
    ['manifest approval decision', without(sheet, 'How the owner approves and stores a manifest before job 20 supplies its spec workflow')],
    ['platform isolation decision', without(sheet, 'Which isolation mechanism is available on each platform for running candidate code')],
    ['complete snapshot decision', without(sheet, 'How a complete pre-review snapshot includes working files without private notebook data')]
  ];
  for (const [name, broken] of cases) {
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, name);
  }
});

test('parallel assessments do not mix a sound sheet with a broken one', async () => {
  const sheet = read(sheetPath);
  const [sound, brokenEngine, brokenTamper, brokenRecover, brokenConcur] = await Promise.all([
    Promise.resolve().then(() => assessSheet(sheet)),
    Promise.resolve().then(() => assessSheet(without(sheet, 'on its own authority'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Candidate tampering'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Restart reads the pinned manifest'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'concurrent jobs cannot read or reuse one another')))
  ]);
  assert.deepEqual(sound, []);
  assert.ok(brokenEngine.some(error => error.includes('on its own authority')));
  assert.ok(brokenTamper.some(error => error.includes('Candidate tampering')));
  assert.ok(brokenRecover.some(error => error.includes('Restart reads the pinned manifest')));
  assert.ok(brokenConcur.some(error => error.includes('concurrent jobs cannot read or reuse one another')));
});

test('the verdict stays on the sheet branch and does not add a gate', () => {
  const changed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean);
  if (!changed.length || changed.some(file => !allowedDiff.has(file))) return;
  const base = git(['rev-parse', 'origin/main']);
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']), base);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main...HEAD']), '');
  assert.equal(fs.existsSync(path.join(root, 'engine/job19-gate.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'engine/job19-gate.test.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/Gate.cs')), false);
  const missing = spawnSync('git', ['cat-file', '-e', 'HEAD:engine/job19-gate.cjs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missing.status, 0);
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
