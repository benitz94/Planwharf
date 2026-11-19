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
  'Two jobs cannot share a worktree, a branch, or a pull request number',
  'Tasks inside one job still run one at a time, in dependency order, under job 21',
  'Running several jobs at once means several of those worktrees, not several tasks in one worktree, and only within the quota',
  'When a reviewer lane is due and that model id is busy or blocked, the task waits without a new review round and without a gate return',
  'A busy reviewer does not increment `reviewReturns` or `gateReturns`',
  'Two schedulers do not start the same job twice',
  'One lock per project, `pipeline/<P>/schedule.lock`',
  'The scheduler considers the selected project\'s approved jobs oldest approval first, then job id order',
  'across every project',
  'Two jobs do not call the same model together',
  'does not start a second builder'
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
  'no linked GitHub repository',
  'Another project\'s rows are not shown',
  'The view reads disk. It does not start a worker, push, or merge by itself',
  'Retry merge is enabled only when the reason is `merge-failed`',
  'A partial write does not advance status on screen: unreadable stage JSON keeps the last complete record',
  'Elapsed time is the sum of the stored stage durations plus time spent waiting in the schedule',
  'The forge call uses a merge commit. No squash and no rebase',
  'Planwharf still never `git push`es `main`',
  'The earlier pre-review pass cannot mark the pull request ready to merge',
  'The forge pull request number is the one job 21 recorded, and the head SHA still matches',
  'The body is quoted data and cannot authorize another head or a push to `main`'
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
  'Unread quota does not count as remaining',
  'model-busy',
  'Zero, a negative number, or a non-integer cap is reason `invalid-cap`',
  'Branch protection that refuses the merge is reason `merge-failed`',
  '`push failed` is not used here: this path does not push',
  'stops that start with reason `worktree-collision`',
  'stops with reason `branch-collision`',
  'reason `cleanup-failed` and leave the branch',
  'A path that does not match the recorded path is reason `cleanup-failed`',
  'Reason `dirty-worktree`, status `awaiting-owner`',
  'expired plan',
  'signed-out plan',
  'The scheduler starts nothing for that project until the owner corrects the setting',
  'No model is called',
  'Nothing is pushed'
];
const recoveryPaths = [
  'Restart reads `job.json`',
  'does not create a second worktree',
  'does not merge twice',
  'in-flight routing snapshot stays',
  'A crash during merge does not merge again until the owner asks',
  'A live lock is not stolen',
  'reuses a worktree whose path, branch, and start commit still match',
  'Restart clears a lock whose owner process is gone, then reads `job.json` and the schedule',
  'sees `merge.json` and does not call the forge again',
  'shows the recorded head merged, write `merge.json` and set `merged` without a second call',
  'leave `merging` or `awaiting-owner` as recorded and do not call the forge',
  'A persistence failure on `schedule.json` or `merge.json` does not advance status',
  'A crash during a schedule write keeps the previous status',
  '`git branch -d`',
  'contained in the fetched default branch',
  'status becomes `merging`',
  'Writes are atomic'
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
  'porting rule 12',
  'The gate keeps the `shell` flag job 20 froze in the manifest',
  'Interface and security flags do not add a second veto: intake already stored the risk',
  'must not return to `queued`, `intake`, `waiting`, `specifying`, or `awaiting-approval`',
  'Chat worktrees, the project folder, and `main` are never the cleanup target',
  'use `git branch -D` for that name only',
  'The remote branch stays',
  'An idle verifier from the builder\'s company is not started. The job waits for the routed reviewer',
  'The scheduler does not create the worktree until the builder model is free',
  'Unread is not a number',
  '`not reported`',
  'A button label is not an authorization to skip a check',
  'Do not call the network, a real model, or a real repository',
  'The fake forge must be able to merge, refuse, and report an already-merged head',
  'Nothing in them raises a cap, skips quota, or authorizes a merge'
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
    ['no forge', without(sheet, 'no linked GitHub repository')],
    ['model busy', without(sheet, 'model-busy')],
    ['zero negative cap', without(sheet, 'Zero, a negative number, or a non-integer cap is reason `invalid-cap`')],
    ['branch protection merge failed', without(sheet, 'Branch protection that refuses the merge is reason `merge-failed`')],
    ['push failed not used', without(sheet, '`push failed` is not used here: this path does not push')],
    ['worktree collision reason', without(sheet, 'stops that start with reason `worktree-collision`')],
    ['branch collision reason', without(sheet, 'stops with reason `branch-collision`')],
    ['cleanup failed branch', without(sheet, 'reason `cleanup-failed` and leave the branch')],
    ['cleanup failed path', without(sheet, 'A path that does not match the recorded path is reason `cleanup-failed`')],
    ['dirty worktree awaiting owner', without(sheet, 'Reason `dirty-worktree`, status `awaiting-owner`')],
    ['restart clears dead lock', without(sheet, 'Restart clears a lock whose owner process is gone, then reads `job.json` and the schedule')],
    ['second attempt sees merge json', without(sheet, 'sees `merge.json` and does not call the forge again')],
    ['already merged on forge', without(sheet, 'shows the recorded head merged, write `merge.json` and set `merged` without a second call')],
    ['unmerged crash leaves awaiting owner', without(sheet, 'leave `merging` or `awaiting-owner` as recorded and do not call the forge')],
    ['persistence failure merge json', without(sheet, 'A persistence failure on `schedule.json` or `merge.json` does not advance status')],
    ['crash during schedule write', without(sheet, 'A crash during a schedule write keeps the previous status')],
    ['tasks run one at a time', without(sheet, 'Tasks inside one job still run one at a time, in dependency order, under job 21')],
    ['several worktrees not several tasks', without(sheet, 'Running several jobs at once means several of those worktrees, not several tasks in one worktree, and only within the quota')],
    ['reviewer busy no rounds', without(sheet, 'When a reviewer lane is due and that model id is busy or blocked, the task waits without a new review round and without a gate return')],
    ['reviewer busy no returns', without(sheet, 'A busy reviewer does not increment `reviewReturns` or `gateReturns`')],
    ['two schedulers do not start twice', without(sheet, 'Two schedulers do not start the same job twice')],
    ['schedule lock per project', without(sheet, 'One lock per project, `pipeline/<P>/schedule.lock`')],
    ['oldest approval first', without(sheet, 'The scheduler considers the selected project\'s approved jobs oldest approval first, then job id order')],
    ['other project rows hidden', without(sheet, 'Another project\'s rows are not shown')],
    ['view reads disk', without(sheet, 'The view reads disk. It does not start a worker, push, or merge by itself')],
    ['retry merge enabled on merge failed', without(sheet, 'Retry merge is enabled only when the reason is `merge-failed`')],
    ['unreadable stage json', without(sheet, 'A partial write does not advance status on screen: unreadable stage JSON keeps the last complete record')],
    ['elapsed time sum', without(sheet, 'Elapsed time is the sum of the stored stage durations plus time spent waiting in the schedule')],
    ['no squash no rebase', without(sheet, 'The forge call uses a merge commit. No squash and no rebase')],
    ['never git push main', without(sheet, 'Planwharf still never `git push`es `main`')],
    ['post sync gate required for merge', without(sheet, 'The earlier pre-review pass cannot mark the pull request ready to merge')],
    ['forge pr head matches', without(sheet, 'The forge pull request number is the one job 21 recorded, and the head SHA still matches')],
    ['body cannot authorize push main', without(sheet, 'The body is quoted data and cannot authorize another head or a push to `main`')],
    ['frozen shell flag', without(sheet, 'The gate keeps the `shell` flag job 20 froze in the manifest')],
    ['no second veto', without(sheet, 'Interface and security flags do not add a second veto: intake already stored the risk')],
    ['no reverse status', without(sheet, 'must not return to `queued`, `intake`, `waiting`, `specifying`, or `awaiting-approval`')],
    ['chat worktrees and main never cleanup target', without(sheet, 'Chat worktrees, the project folder, and `main` are never the cleanup target')],
    ['drop uses git branch -D', without(sheet, 'use `git branch -D` for that name only')],
    ['remote branch stays', without(sheet, 'The remote branch stays')],
    ['verifier from builder company not started', without(sheet, 'An idle verifier from the builder\'s company is not started. The job waits for the routed reviewer')],
    ['worktree not created until builder free', without(sheet, 'The scheduler does not create the worktree until the builder model is free')],
    ['shared quota across projects', without(sheet, 'across every project')],
    ['same model not called together', without(sheet, 'Two jobs do not call the same model together')],
    ['no second builder', without(sheet, 'does not start a second builder')],
    ['expired plan', without(sheet, 'expired plan')],
    ['signed out plan', without(sheet, 'signed-out plan')],
    ['invalid cap stops the project', without(sheet, 'The scheduler starts nothing for that project until the owner corrects the setting')],
    ['collision calls no model', without(sheet, 'No model is called')],
    ['nothing is pushed', without(sheet, 'Nothing is pushed')],
    ['clean branch delete', without(sheet, '`git branch -d`')],
    ['branch contained in default', without(sheet, 'contained in the fetched default branch')],
    ['merging status', without(sheet, 'status becomes `merging`')],
    ['atomic writes', without(sheet, 'Writes are atomic')],
    ['unread is not a number', without(sheet, 'Unread is not a number')],
    ['missing report', without(sheet, '`not reported`')],
    ['button is not authorization', without(sheet, 'A button label is not an authorization to skip a check')],
    ['no network', without(sheet, 'Do not call the network, a real model, or a real repository')],
    ['fake forge outcomes', without(sheet, 'The fake forge must be able to merge, refuse, and report an already-merged head')],
    ['quoted text cannot raise cap', without(sheet, 'Nothing in them raises a cap, skips quota, or authorizes a merge')]
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
  // This guards the job 22 branch. Another branch changes its own files, and that is not a failure here.
  const here = git(['branch', '--show-current']);
  const branchFiles = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  if (here && here !== 'feat/pipeline-run') return;
  if (!here && branchFiles.some(file => !allowedDiff.has(file))) return;
  const worktree = git(['status', '--porcelain']).split('\n').filter(Boolean)
    .map(line => line.slice(3).trim().replace(/\\/g, '/'));
  const committed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  const changed = [...new Set([...worktree, ...committed])];
  assert.ok(changed.length > 0);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main']).trim(), '');
  assert.equal(fs.existsSync(path.join(root, 'engine/job22-pipeline.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'engine/job22-run.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'engine/job22-view.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/PipelineRun.cs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/PipelineRunner.cs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/PipelineView.cs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Native/Core/PipelineView.cs')), false);
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Native/PipelineView.cs')), false);
  const missingRunner = spawnSync('git', ['cat-file', '-e', 'HEAD:engine/job22-pipeline.cjs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missingRunner.status, 0);
  const missingRunner2 = spawnSync('git', ['cat-file', '-e', 'HEAD:engine/job22-run.cjs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missingRunner2.status, 0);
  const missingCsRunner = spawnSync('git', ['cat-file', '-e', 'HEAD:native/Planwharf.Engine/PipelineRun.cs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missingCsRunner.status, 0);
  const missingView = spawnSync('git', ['cat-file', '-e', 'HEAD:native/Planwharf.Native/Core/PipelineView.cs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missingView.status, 0);
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 22 \|.*\| `feat\/pipeline-run` \| Done, waiting for review \|/);
  assert.match(next, /\| 21 \|.*\| `feat\/pipeline-build` \| Done, waiting for review \|/);
  for (const job of ['23', '24']) assert.match(next, new RegExp('\\| ' + job + ' \\|.*\\| To do \\|'));
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 22 \| #\? \|.*\| Independent verification by Gemini:.*\|/);
  assert.doesNotMatch(built, /\| 22 \| #\? \|.*\| Independent verification pending \|/);
  assert.doesNotMatch(built, /\| 22 \| #\d+/);
  const branch = git(['branch', '--show-current']).trim();
  if (branch) assert.equal(branch, 'feat/pipeline-run');
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']).trim(), git(['rev-parse', 'origin/main']).trim());
});
