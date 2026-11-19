'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/22-pipeline-run.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job22-sheet.test.cjs'
]);
const concurrent = [
  'several jobs at once',
  'as many as the quota allows',
  'within the quota',
  'One worktree per job',
  'worktrees/<P>/jobs/<J>',
  'pipeline/<J>',
  'one invocation per model id',
  'maxConcurrentJobs',
  'Chat worktrees stay job 15',
  'A waiting job stays `approved`',
  'does not rewrite `routing.json`',
  'Two jobs do not share a directory',
  'Two jobs cannot share a worktree, a branch, or a pull request number'
];
const viewAndMerge = [
  'pipeline view',
  'stages, time, and quota',
  'Needs your decision',
  'does not show credentials',
  'PipelineView',
  'English and Italian',
  'mergeLowRisk',
  'defaults to false',
  'The owner merges by default',
  'Medium and high never auto-merge',
  'merge commit',
  'Job 21 never merges',
  'Workers never push',
  'Workers never merge',
  'does not merge twice',
  'setting off does not merge',
  'ready-local',
  'no linked GitHub repository'
];
const errorPaths = [
  'invalid-cap',
  'unread-quota',
  'worktree-collision',
  'branch-collision',
  'merge-failed',
  'dirty-worktree',
  'not-mergeable',
  'wrong-head',
  'cleanup-failed',
  'persistence failure',
  'A partial write does not advance status',
  'Unread quota does not count as remaining'
];
const recoveryPaths = [
  'Restart reads `job.json`',
  'does not create a second worktree',
  'does not merge twice',
  'in-flight routing snapshot stays',
  'A crash during merge does not merge again until the owner asks',
  'A live lock is not stolen',
  'reuses a worktree whose path, branch, and start commit still match'
];
const edgeCases = [
  'spaces and Unicode',
  '`shell` is false',
  'quoted data',
  'cap of 1',
  'does not delete unknown folders',
  'does not restore the Review role',
  'does not invent a second manifest',
  'another company',
  'dotnet build native/Planwharf.Engine',
  'dotnet test native/Planwharf.Engine.Tests',
  'dotnet test native/Planwharf.Native.Tests',
  'node scripts/engineering/ci.cjs',
  'porting rule 12'
];
const openDecisions = [
  'Whether a later job should delete the remote',
  'How long a `ready-local` branch waits',
  'Whether the owner wants this panel on Home'
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
    ['concurrent', concurrent],
    ['view', viewAndMerge],
    ['error', errorPaths],
    ['recovery', recoveryPaths],
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

test('job 22 sheet covers concurrent worktrees, the pipeline view, and low-risk merge', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('job 22 sheet follows the pipeline design and jobs 18 to 21', () => {
  const design = flat(read('docs/DESIGN-PIPELINE.md'));
  assert.match(design, /several jobs run at once, as many as the quota allows/);
  assert.match(design, /You merge it/);
  assert.match(design, /low risk and/);
  assert.match(design, /pass every check/);
  const build = flat(read('native/porting/jobs/21-pipeline-build.md'));
  assert.match(build, /Job 22 owns that, the pipeline view, and any merge/);
  assert.match(build, /This job never merges/);
  const gate = flat(read('native/porting/jobs/19-gate.md'));
  assert.match(gate, /A candidate branch cannot select or weaken/);
  const protocol = flat(read('native/porting/jobs/18-mcp-protocol.md'));
  assert.match(protocol, /A program without it keeps today's UTF-8 JSON result file/);
  const intake = flat(read('native/porting/jobs/20-pipeline-intake.md'));
  assert.match(intake, /ready for job 21 to read/);
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping a quota rule, the view, or a merge limit fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['several jobs', without(sheet, 'several jobs at once')],
    ['quota cap', without(sheet, 'as many as the quota allows')],
    ['one model', without(sheet, 'one invocation per model id')],
    ['view', without(sheet, 'pipeline view')],
    ['needs decision', without(sheet, 'Needs your decision')],
    ['option', without(sheet, 'mergeLowRisk')],
    ['default merge', without(sheet, 'The owner merges by default')],
    ['not low', without(sheet, 'Medium and high never auto-merge')],
    ['twice', without(sheet, 'does not merge twice')],
    ['built pipeline', sheet + '\nThe pipeline is implemented.\n']
  ];
  for (const [name, broken] of cases) {
    assert.ok(assessSheet(broken).length > 0, name);
  }
});

test('dropping an error, recovery, concurrency, edge, or open decision limit fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['invalid cap', without(sheet, 'invalid-cap')],
    ['unread', without(sheet, 'unread-quota')],
    ['collision', without(sheet, 'worktree-collision')],
    ['branch', without(sheet, 'branch-collision')],
    ['merge failed', without(sheet, 'merge-failed')],
    ['dirty', without(sheet, 'dirty-worktree')],
    ['not mergeable', without(sheet, 'not-mergeable')],
    ['wrong head', without(sheet, 'wrong-head')],
    ['cleanup', without(sheet, 'cleanup-failed')],
    ['partial', without(sheet, 'A partial write does not advance status')],
    ['restart', without(sheet, 'Restart reads `job.json`')],
    ['second worktree', without(sheet, 'does not create a second worktree')],
    ['snapshot', without(sheet, 'in-flight routing snapshot stays')],
    ['crash', without(sheet, 'A crash during merge does not merge again until the owner asks')],
    ['lock', without(sheet, 'A live lock is not stolen')],
    ['share', without(sheet, 'Two jobs do not share a directory')],
    ['same slot', without(sheet, 'Two jobs cannot share a worktree, a branch, or a pull request number')],
    ['unicode', without(sheet, 'spaces and Unicode')],
    ['shell', without(sheet, '`shell` is false')],
    ['unknown folders', without(sheet, 'does not delete unknown folders')],
    ['remote branch', without(sheet, 'Whether a later job should delete the remote')],
    ['ready local wait', without(sheet, 'How long a `ready-local` branch waits')],
    ['home panel', without(sheet, 'Whether the owner wants this panel on Home')],
    ['review role', without(sheet, 'does not restore the Review role')],
    ['second manifest', without(sheet, 'does not invent a second manifest')],
    ['other company', without(sheet, 'another company')],
    ['workers merge', without(sheet, 'Workers never merge')],
    ['no forge', without(sheet, 'no linked GitHub repository')]
  ];
  for (const [name, broken] of cases) {
    assert.ok(assessSheet(broken).length > 0, name);
  }
});

test('parallel assessments do not mix a sound sheet with a broken one', async () => {
  const sheet = read(sheetPath);
  const [sound, brokenQuota, brokenMerge, brokenRecover] = await Promise.all([
    Promise.resolve().then(() => assessSheet(sheet)),
    Promise.resolve().then(() => assessSheet(without(sheet, 'one invocation per model id'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Medium and high never auto-merge'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'does not create a second worktree')))
  ]);
  assert.deepEqual(sound, []);
  assert.ok(brokenQuota.some(error => error.includes('one invocation per model id')));
  assert.ok(brokenMerge.some(error => error.includes('Medium and high never auto-merge')));
  assert.ok(brokenRecover.some(error => error.includes('does not create a second worktree')));
});

test('the sheet stays on feat/pipeline-run and does not add a runner', () => {
  const worktree = git(['status', '--porcelain']).split('\n').filter(Boolean)
    .map(line => line.slice(3).trim().replace(/\\/g, '/'));
  const committed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  const changed = [...new Set([...worktree, ...committed])];
  assert.ok(changed.length > 0);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main']).trim(), '');
  assert.equal(fs.existsSync(path.join(root, 'engine/job22-pipeline.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/PipelineRun.cs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Native/Core/PipelineView.cs')), false);
  const missingRunner = spawnSync('git', ['cat-file', '-e', 'HEAD:engine/job22-pipeline.cjs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missingRunner.status, 0);
  const missingView = spawnSync('git', ['cat-file', '-e', 'HEAD:native/Planwharf.Native/Core/PipelineView.cs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missingView.status, 0);
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 22 \|.*\| `feat\/pipeline-run` \| Done, waiting for review \|/);
  assert.match(next, /\| 21 \|.*\| `feat\/pipeline-build` \| Done, waiting for review \|/);
  for (const job of ['23', '24']) assert.match(next, new RegExp('\\| ' + job + ' \\|.*\\| To do \\|'));
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 22 \| #\? \|.*Independent verification pending/);
  assert.doesNotMatch(built, /\| 22 \| #\d+/);
  const branch = git(['branch', '--show-current']).trim();
  if (branch) assert.equal(branch, 'feat/pipeline-run');
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']).trim(), git(['rev-parse', 'origin/main']).trim());
});
