'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/21-pipeline-build.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job21-sheet.test.cjs'
]);
const routing = [
  'The engine, not a model, picks the models',
  'routing table the owner can edit',
  'Risk is `low`, `medium`, or `high`',
  'touchesInterface',
  'touchesSecurity',
  'reviewer-code',
  'reviewer-interface',
  'reviewer-security',
  'another company',
  'does not restore the Review role',
  'does not invent a second manifest'
];
const pairsAndRounds = [
  'Each task has a builder and, as soon as it finishes, its reviewer',
  'Code always; interface and security when those parts are touched',
  'maxReviewRounds',
  'defaults to 2',
  'then the owner is asked',
  'maxGateReturns',
  'One pull request per job, not per task'
];
const gateAndPr = [
  'Before any reviewer starts',
  'failing build goes straight back to the builder with the log',
  'Again after syncing with `main`',
  'A branch cannot change the checks it is judged by',
  'No model is invoked by the gate',
  'cannot trigger, substitute for, or satisfy it',
  'never pushes `main`',
  'never merges',
  'Workers never push',
  'Closes #<number>',
  'ready-local'
];
const errorPaths = [
  'invalid risk',
  'no eligible model',
  'Gate evidence missing',
  'push failed',
  'merge conflict'
];
const recoveryPaths = [
  'Restart reads `job.json`',
  'does not open a second pull request',
  'in-flight routing snapshot stays',
  'A partial write does not advance status',
  'does not overwrite the failure'
];
const concurrencyPaths = [
  'One worktree per job',
  'Two tasks do not write the same worktree at once',
  'Two jobs do not share a directory'
];
const edgeCases = [
  'spaces and Unicode',
  'no linked GitHub repository',
  '`shell` is false',
  'bad-tree',
  'gate-not-ported'
];
const openDecisions = [
  'Which isolation mechanism job 19 uses',
  'How a complete pre-review snapshot includes working files without private notebook data',
  'Whether Planwharf later merges a low-risk pull request',
  'How long a `ready-local` branch waits'
];

function read(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}
function git(args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, (result.stderr || result.stdout || '').slice(0, 500));
  return (result.stdout || '').replace(/\r\n/g, '\n');
}
function flat(text) {
  return text.replace(/\s+/g, ' ');
}
function assessSheet(sheet) {
  const errors = [];
  const text = flat(sheet);
  const groups = [
    ['routing', routing],
    ['pairs', pairsAndRounds],
    ['gate', gateAndPr],
    ['error', errorPaths],
    ['recovery', recoveryPaths],
    ['concurrency', concurrencyPaths],
    ['edge', edgeCases],
    ['open', openDecisions]
  ];
  for (const [group, phrases] of groups) {
    for (const phrase of phrases) {
      if (!text.includes(phrase)) errors.push(group + ' missing "' + phrase + '"');
    }
  }
  if (/the pipeline is implemented/i.test(sheet)) errors.push('sheet marks the pipeline implemented');
  if (!/Open design decisions/.test(sheet)) errors.push('owner decisions removed');
  return errors;
}
function without(sheet, phrase) {
  const flattened = flat(sheet);
  assert.ok(flattened.includes(phrase), 'fixture phrase missing: ' + phrase);
  return flattened.split(phrase).join('');
}

test('job 21 sheet covers routing, pairs, rounds, the gate, and one pull request', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('job 21 sheet follows the pipeline design and jobs 18 to 20', () => {
  const design = flat(read('docs/DESIGN-PIPELINE.md'));
  assert.match(design, /4\.\s+\*\*Route\.\*\*/);
  assert.match(design, /The engine, not a model, picks the models/);
  assert.match(design, /routing table the owner can edit/);
  assert.match(design, /6\.\s+\*\*Build and review in pairs\.\*\*/);
  assert.match(design, /before any reviewer starts/);
  assert.match(design, /8\.\s+\*\*Pull request\.\*\*/);
  const intake = flat(read('native/porting/jobs/20-pipeline-intake.md'));
  assert.match(intake, /ready for job 21 to read/);
  assert.match(intake, /Do not invent a second manifest/);
  const gate = flat(read('native/porting/jobs/19-gate.md'));
  assert.match(gate, /job 21 owns build\/review routing and PR stage transitions/);
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping routing, a pair rule, the gate, or a second pull request fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['engine route', without(sheet, 'The engine, not a model, picks the models')],
    ['other company', without(sheet, 'another company')],
    ['review role', without(sheet, 'does not restore the Review role')],
    ['pairs', without(sheet, 'Each task has a builder and, as soon as it finishes, its reviewer')],
    ['rounds', without(sheet, 'maxReviewRounds')],
    ['pre-review', without(sheet, 'Before any reviewer starts')],
    ['post-sync', without(sheet, 'Again after syncing with `main`')],
    ['one pr', without(sheet, 'One pull request per job, not per task')],
    ['built pipeline', sheet + '\nThe pipeline is implemented.\n']
  ];
  for (const [name, broken] of cases) {
    assert.ok(assessSheet(broken).length > 0, name);
  }
});

test('dropping an error, recovery, concurrency, edge, or open decision limit fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['invalid risk', without(sheet, 'invalid risk')],
    ['no model', without(sheet, 'no eligible model')],
    ['missing evidence', without(sheet, 'Gate evidence missing')],
    ['push', without(sheet, 'push failed')],
    ['conflict', without(sheet, 'merge conflict')],
    ['restart', without(sheet, 'Restart reads `job.json`')],
    ['second pr', without(sheet, 'does not open a second pull request')],
    ['snapshot', without(sheet, 'in-flight routing snapshot stays')],
    ['partial write', without(sheet, 'A partial write does not advance status')],
    ['gate retry', without(sheet, 'does not overwrite the failure')],
    ['one worktree', without(sheet, 'One worktree per job')],
    ['two tasks', without(sheet, 'Two tasks do not write the same worktree at once')],
    ['two jobs', without(sheet, 'Two jobs do not share a directory')],
    ['unicode', without(sheet, 'spaces and Unicode')],
    ['no forge', without(sheet, 'no linked GitHub repository')],
    ['isolation decision', without(sheet, 'Which isolation mechanism job 19 uses')],
    ['merge decision', without(sheet, 'Whether Planwharf later merges a low-risk pull request')]
  ];
  for (const [name, broken] of cases) {
    assert.ok(assessSheet(broken).length > 0, name);
  }
});

test('parallel assessments do not mix a sound sheet with a broken one', async () => {
  const sheet = read(sheetPath);
  const [sound, brokenRoute, brokenGate, brokenPr, brokenRecover] = await Promise.all([
    Promise.resolve().then(() => assessSheet(sheet)),
    Promise.resolve().then(() => assessSheet(without(sheet, 'another company'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Before any reviewer starts'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'does not open a second pull request'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Restart reads `job.json`')))
  ]);
  assert.deepEqual(sound, []);
  assert.ok(brokenRoute.some(error => error.includes('another company')));
  assert.ok(brokenGate.some(error => error.includes('Before any reviewer starts')));
  assert.ok(brokenPr.some(error => error.includes('does not open a second pull request')));
  assert.ok(brokenRecover.some(error => error.includes('Restart reads `job.json`')));
});

test('the sheet stays on feat/pipeline-build and does not add a pipeline', () => {
  const worktree = git(['status', '--porcelain']).split('\n').filter(Boolean)
    .map(line => line.slice(3).trim().replace(/\\/g, '/'));
  const committed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  const changed = [...new Set([...worktree, ...committed])];
  assert.ok(changed.length > 0);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main']).trim(), '');
  assert.equal(fs.existsSync(path.join(root, 'engine/job21-pipeline.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/PipelineBuild.cs')), false);
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 21 \|.*\| `feat\/pipeline-build` \| Done, waiting for review \|/);
  assert.match(next, /\| 20 \|.*\| `feat\/pipeline-intake` \| Done, waiting for review \|/);
  for (const job of ['22', '23']) assert.match(next, new RegExp('\\| ' + job + ' \\|.*\\| To do \\|'));
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 21 \| #\? \|/);
  assert.match(built, /Independent verification pending/);
  assert.doesNotMatch(built, /\| 21 \| #\d+/);
  const branch = git(['branch', '--show-current']).trim();
  if (branch) assert.equal(branch, 'feat/pipeline-build');
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']).trim(), git(['rev-parse', 'origin/main']).trim());
});
