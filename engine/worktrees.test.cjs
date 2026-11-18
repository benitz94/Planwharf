'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, spawnSync } = require('node:child_process');
const worktrees = require('./worktrees.cjs');
const { requireWorkerWorkspace } = require('./project-workspace.cjs');
const { createAttemptEngine } = require('./attempts.js');
const { createExecutionBridge } = require('./execution.cjs');

function git(folder, ...args) {
  const result = spawnSync('git', args, { cwd: folder, encoding: 'utf8', windowsHide: true });
  assert.equal(result.status, 0, `${args.join(' ')}: ${result.stderr}`);
  return result.stdout.trim();
}
function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worktrees-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const folder = path.join(root, 'project with spaces é');
  fs.mkdirSync(folder);
  git(folder, 'init', '-b', 'main');
  git(folder, 'config', 'user.name', 'Fixture');
  git(folder, 'config', 'user.email', 'fixture@example.test');
  git(folder, 'config', 'core.autocrlf', 'false');
  fs.writeFileSync(path.join(folder, 'base.txt'), 'base\n');
  git(folder, 'add', '.'); git(folder, 'commit', '-m', 'base');
  git(folder, 'switch', '-c', 'feat/work');
  return { root, folder, dir: path.join(root, 'data'), projectId: 'project-1', megaId: 'MT-1' };
}
function part(f, subId, index) {
  return worktrees.create({ ...f, subId, index, workBranch: 'feat/work' });
}

test('parallel parts have isolated registered worktrees and merge in order', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1), second = part(f, 'ST-2', 2);
  assert.notEqual(first.folder, second.folder);
  assert.equal(git(first.folder, 'branch', '--show-current'), 'feat/work--part-1');
  assert.equal(git(second.folder, 'branch', '--show-current'), 'feat/work--part-2');
  assert.equal(requireWorkerWorkspace({ linkedFolder: f.folder, folder: first.folder, projectId: f.projectId, megaTaskId: f.megaId,
    subTaskId: 'ST-1', resultPath: path.join(f.dir, 'results', 'attempt.json') }).folder, fs.realpathSync(first.folder));
  assert.throws(() => requireWorkerWorkspace({ linkedFolder: f.folder, folder: second.folder, projectId: f.projectId, megaTaskId: f.megaId,
    subTaskId: 'ST-1', resultPath: path.join(f.dir, 'results', 'attempt.json') }), /linked project folder/);
  fs.writeFileSync(path.join(first.folder, 'one.txt'), 'one\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'one');
  assert.equal(fs.existsSync(path.join(second.folder, 'one.txt')), false);
  fs.writeFileSync(path.join(second.folder, 'two.txt'), 'two\n'); git(second.folder, 'add', '.'); git(second.folder, 'commit', '-m', 'two');
  assert.deepEqual(worktrees.integrate({ folder: f.folder, parts: [first, second] }), { ok: true });
  assert.equal(fs.readFileSync(path.join(f.folder, 'one.txt'), 'utf8'), 'one\n');
  assert.equal(fs.readFileSync(path.join(f.folder, 'two.txt'), 'utf8'), 'two\n');
  worktrees.cleanup({ ...f, parts: [first, second] });
  assert.equal(fs.existsSync(first.folder), false);
  assert.equal(git(f.folder, 'branch', '--list', 'feat/work--part-*'), '');
});

test('conflict aborts the merge and retains both branches for recovery', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1), second = part(f, 'ST-2', 2);
  fs.writeFileSync(path.join(first.folder, 'base.txt'), 'first\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'first');
  fs.writeFileSync(path.join(second.folder, 'base.txt'), 'second\n'); git(second.folder, 'add', '.'); git(second.folder, 'commit', '-m', 'second');
  const result = worktrees.integrate({ folder: f.folder, parts: [first, second] });
  assert.equal(result.ok, false);
  assert.equal(result.branch, second.branch);
  assert.deepEqual(result.conflicts, ['base.txt']);
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
  assert.equal(spawnSync('git', ['rev-parse', '-q', '--verify', 'MERGE_HEAD'], { cwd: f.folder }).status, 1);
  assert.equal(fs.existsSync(second.folder), true);
});

test('interrupted worktree is reused and dirty work is preserved during cleanup', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  assert.deepEqual(part(f, 'ST-1', 1), first);
  fs.writeFileSync(path.join(first.folder, 'unfinished.txt'), 'recover me\n');
  assert.throws(() => worktrees.cleanup({ ...f, parts: [first] }), /recoverable changes/);
  assert.equal(fs.readFileSync(path.join(first.folder, 'unfinished.txt'), 'utf8'), 'recover me\n');
});

test('explicit drop removes clean unmerged part branches', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  fs.writeFileSync(path.join(first.folder, 'work.txt'), 'completed part\n');
  git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'part work');
  worktrees.cleanup({ ...f, parts: [first], discardBranches: true });
  assert.equal(fs.existsSync(first.folder), false);
  assert.equal(git(f.folder, 'branch', '--list', first.branch), '');
});

test('non-git folders do not acquire worktrees', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worktrees-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.equal(worktrees.create({ folder: root, dir: root, projectId: 'p', megaId: 'm', subId: 's', index: 1, workBranch: 'work' }), null);
});

test('execution bridge isolates successive parallel groups and verifies after both integrations', t => {
  const f = fixture(t), attemptsDir = path.join(f.root, 'attempts');
  const item = { project: { id: f.projectId, name: 'Fixture', folder: f.folder }, revisions: [{ id: 1, direction: 'Build', open: [] }] };
  const job = { id: 'job-1', title: 'Build', detail: 'Build two files', revision_id: 1, kind: 'implementation' };
  const models = [1, 2, 3, 4, 5].map(n => ({ id: `model-${n}`, provider: 'codex', cli: `gpt-${n}`, enabled: true, available: true, cost: 1 }));
  const store = { usage: {} }, row = { dispatch: [] }, launches = [];
  const bridge = createExecutionBridge({ store, dir: attemptsDir, attempts: createAttemptEngine({ dir: attemptsDir }), rowFor: () => row,
    models: () => models, leadId: () => 'lead', sessionId: 'session', now: () => new Date().toISOString(), persist: () => {},
    inspect: () => ({ git: true, commitId: git(f.folder, 'rev-parse', 'HEAD'), dirty: [], names: [] }), isProcessAlive: () => false,
    spawnWorker(info) {
      launches.push(info);
      const number = launches.length;
      if (number < 5) {
        if (number > 2) assert.equal(fs.readFileSync(path.join(info.folder, '1.txt'), 'utf8'), '1\n');
        fs.writeFileSync(path.join(info.folder, `${number}.txt`), `${number}\n`);
        git(info.folder, 'add', '.'); git(info.folder, 'commit', '-m', `part ${number}`);
      } else {
        assert.equal(fs.readFileSync(path.join(info.folder, '1.txt'), 'utf8'), '1\n');
        assert.equal(fs.readFileSync(path.join(info.folder, '2.txt'), 'utf8'), '2\n');
        assert.equal(fs.readFileSync(path.join(info.folder, '3.txt'), 'utf8'), '3\n');
        assert.equal(fs.readFileSync(path.join(info.folder, '4.txt'), 'utf8'), '4\n');
      }
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId,
        megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Done', filesChanged: number < 5 ? [`${number}.txt`] : [], tests: [], blockers: [], nextAction: 'Review' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
      return { pid: 8000 + number };
    } });
  const mega = bridge.propose(item, job, [
    { modelId: models[0].id, slice: 'First', scope: ['1.txt'], dependsOn: [] },
    { modelId: models[1].id, slice: 'Second', scope: ['2.txt'], dependsOn: [] },
    { modelId: models[2].id, slice: 'Third', scope: ['3.txt'], dependsOn: [0, 1] },
    { modelId: models[3].id, slice: 'Fourth', scope: ['4.txt'], dependsOn: [0, 1] },
    { modelId: models[4].id, slice: 'Verify the work independently', dependsOn: [2, 3] }
  ]);
  bridge.approve(f.projectId, mega.id);
  for (let n = 0; n < 8 && mega.status !== 'COMPLETE'; n++) bridge.advance(item, job, []);
  assert.equal(mega.status, 'COMPLETE');
  assert.notEqual(launches[0].folder, launches[1].folder);
  assert.notEqual(launches[2].folder, launches[3].folder);
  assert.equal(launches[4].folder, f.folder);
  assert.equal(fs.existsSync(launches[0].folder), false);
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
});

test('execution bridge gives merge conflicts to an integration worker', t => {
  const f = fixture(t), attemptsDir = path.join(f.root, 'attempts');
  const item = { project: { id: f.projectId, name: 'Fixture', folder: f.folder }, revisions: [{ id: 1, direction: 'Build', open: [] }] };
  const job = { id: 'job-conflict', title: 'Conflict', detail: 'Edit base file', revision_id: 1, kind: 'implementation' };
  const models = [1, 2].map(n => ({ id: `model-${n}`, provider: 'codex', cli: `gpt-${n}`, enabled: true, available: true, cost: 1 }));
  const store = { usage: {} }, row = { dispatch: [] }, launches = [];
  const bridge = createExecutionBridge({ store, dir: attemptsDir, attempts: createAttemptEngine({ dir: attemptsDir }), rowFor: () => row,
    models: () => models, leadId: () => 'lead', sessionId: 'session', now: () => new Date().toISOString(), persist: () => {},
    inspect: () => ({ git: true, commitId: git(f.folder, 'rev-parse', 'HEAD'), dirty: [], names: [] }), isProcessAlive: () => false,
    spawnWorker(info) {
      launches.push(info);
      const n = launches.length;
      if (n <= 2) {
        fs.writeFileSync(path.join(info.folder, 'base.txt'), `part ${n}\n`);
        git(info.folder, 'add', '.'); git(info.folder, 'commit', '-m', `part ${n}`);
      } else {
        assert.equal(info.folder, f.folder);
        assert.match(info.job.detail, /base\.txt/);
        assert.match(info.job.detail, /feat\/work--part-2/);
        const merge = spawnSync('git', ['merge', '--no-ff', '--no-edit', 'feat/work--part-2'], { cwd: f.folder, encoding: 'utf8' });
        assert.equal(merge.status, 1);
        git(f.folder, 'checkout', '--theirs', 'base.txt'); git(f.folder, 'add', 'base.txt'); git(f.folder, 'commit', '-m', 'resolve conflict');
      }
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId,
        megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Done', filesChanged: ['base.txt'], tests: [], blockers: [], nextAction: 'Review' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
      return { pid: 9000 + n };
    } });
  const mega = bridge.propose(item, job, [{ modelId: models[0].id, slice: 'First', dependsOn: [] }, { modelId: models[1].id, slice: 'Second', dependsOn: [] }]);
  bridge.approve(f.projectId, mega.id);
  for (let n = 0; n < 5 && mega.status !== 'COMPLETE'; n++) bridge.advance(item, job, []);
  assert.equal(mega.status, 'COMPLETE', JSON.stringify({ subs: store.execution.subTasks, results: store.execution.results, nextAction: mega.nextAction }));
  assert.equal(launches.length, 3);
  assert.equal(fs.readFileSync(path.join(f.folder, 'base.txt'), 'utf8').replace(/\r\n/g, '\n'), 'part 2\n');
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
});

test('failed parallel work keeps both worktrees for recovery', t => {
  const f = fixture(t), attemptsDir = path.join(f.root, 'attempts');
  const item = { project: { id: f.projectId, name: 'Fixture', folder: f.folder }, revisions: [{ id: 1, direction: 'Build', open: [] }] };
  const job = { id: 'job-failure', title: 'Failure', detail: 'Build two files', revision_id: 1, kind: 'implementation' };
  const models = [1, 2].map(n => ({ id: `model-${n}`, provider: 'codex', cli: `gpt-${n}`, enabled: true, available: true, cost: 1 }));
  const store = { usage: {} }, row = { dispatch: [] }, launches = [];
  const bridge = createExecutionBridge({ store, dir: attemptsDir, attempts: createAttemptEngine({ dir: attemptsDir }), rowFor: () => row,
    models: () => models, leadId: () => 'lead', sessionId: 'session', now: () => new Date().toISOString(), persist: () => {},
    inspect: () => ({ git: true, commitId: git(f.folder, 'rev-parse', 'HEAD'), dirty: [], names: [] }), isProcessAlive: () => false,
    spawnWorker(info) {
      launches.push(info);
      const failed = launches.length === 2;
      if (failed) fs.writeFileSync(path.join(info.folder, 'recover.txt'), 'unfinished\n');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId,
        megaTaskId: info.megaTaskId, status: failed ? 'FAILED' : 'COMPLETE', summary: failed ? 'Needs recovery' : 'Done', filesChanged: [], tests: [], blockers: failed ? ['Fixture failure'] : [], nextAction: 'Review' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
      return { pid: 9500 + launches.length };
    } });
  const mega = bridge.propose(item, job, [{ modelId: models[0].id, slice: 'First', dependsOn: [] }, { modelId: models[1].id, slice: 'Second', dependsOn: [] }]);
  bridge.approve(f.projectId, mega.id); bridge.advance(item, job, []); bridge.advance(item, job, []);
  assert.equal(mega.status, 'FAILED');
  assert.equal(fs.existsSync(launches[0].folder), true);
  assert.equal(fs.readFileSync(path.join(launches[1].folder, 'recover.txt'), 'utf8'), 'unfinished\n');
});

function gitAsync(folder, args) {
  return new Promise((resolve, reject) => {
    const child = spawn('git', args, { cwd: folder, windowsHide: true });
    let stderr = '';
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', code => code === 0 ? resolve() : reject(new Error(`${args.join(' ')}: ${stderr}`)));
  });
}

test('merges are no-ff and a later conflict keeps only the earlier merge', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1), second = part(f, 'ST-2', 2);
  fs.writeFileSync(path.join(first.folder, 'base.txt'), 'first\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'first');
  fs.writeFileSync(path.join(second.folder, 'base.txt'), 'second\n'); git(second.folder, 'add', '.'); git(second.folder, 'commit', '-m', 'second');
  const result = worktrees.integrate({ folder: f.folder, parts: [first, second] });
  assert.equal(result.ok, false);
  assert.equal(fs.readFileSync(path.join(f.folder, 'base.txt'), 'utf8').replace(/\r\n/g, '\n'), 'first\n');
  const subjects = git(f.folder, 'log', '--first-parent', '--format=%s');
  assert.match(subjects, /Merge branch 'feat\/work--part-1'/);
  assert.doesNotMatch(subjects, /part-2/);
  assert.equal(git(f.folder, 'rev-list', '--parents', '-n', '1', 'HEAD').split(/\s+/).length, 3);
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
  assert.match(git(f.folder, 'branch', '--list', second.branch), /feat\/work--part-2/);
});

test('dirty work or part pauses integration before any merge', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1), second = part(f, 'ST-2', 2);
  fs.writeFileSync(path.join(first.folder, 'one.txt'), 'one\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'one');
  fs.writeFileSync(path.join(second.folder, 'two.txt'), 'two\n'); git(second.folder, 'add', '.'); git(second.folder, 'commit', '-m', 'two');
  const head = git(f.folder, 'rev-parse', 'HEAD');
  fs.writeFileSync(path.join(f.folder, 'dirty.txt'), 'paused\n');
  const paused = worktrees.integrate({ folder: f.folder, parts: [first, second] });
  assert.equal(paused.ok, false);
  assert.match(paused.reason, /uncommitted changes/);
  assert.deepEqual(paused.conflicts, []);
  assert.equal(git(f.folder, 'rev-parse', 'HEAD'), head);
  fs.rmSync(path.join(f.folder, 'dirty.txt'));
  fs.writeFileSync(path.join(second.folder, 'unfinished.txt'), 'still writing\n');
  const partDirty = worktrees.integrate({ folder: f.folder, parts: [first, second] });
  assert.equal(partDirty.ok, false);
  assert.equal(partDirty.branch, second.branch);
  assert.match(partDirty.reason, /uncommitted changes/);
  assert.equal(git(f.folder, 'rev-parse', 'HEAD'), head);
  assert.equal(fs.existsSync(path.join(second.folder, 'unfinished.txt')), true);
});

test('a missing part folder pauses integration instead of throwing', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  fs.writeFileSync(path.join(first.folder, 'one.txt'), 'one\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'one');
  fs.rmSync(first.folder, { recursive: true, force: true });
  const head = git(f.folder, 'rev-parse', 'HEAD');
  let result;
  assert.doesNotThrow(() => { result = worktrees.integrate({ folder: f.folder, parts: [first] }); });
  assert.equal(result.ok, false);
  assert.equal(result.branch, first.branch);
  assert.match(result.reason, /Part worktree is unavailable/);
  assert.equal(git(f.folder, 'rev-parse', 'HEAD'), head);
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
});

test('explicit drop removes a dirty worktree and its branch', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  fs.writeFileSync(path.join(first.folder, 'one.txt'), 'committed\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'one');
  fs.writeFileSync(path.join(first.folder, 'recover.txt'), 'drop me\n');
  worktrees.cleanup({ ...f, parts: [first], discardBranches: true });
  assert.equal(fs.existsSync(first.folder), false);
  assert.equal(git(f.folder, 'branch', '--list', first.branch), '');
  assert.equal(git(f.folder, 'worktree', 'list', '--porcelain').includes('ST-1'), false);
});

test('deleting the worktree directory keeps the branch and create restores it', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  fs.writeFileSync(path.join(first.folder, 'one.txt'), 'kept\n'); git(first.folder, 'add', '.'); git(first.folder, 'commit', '-m', 'one');
  fs.rmSync(first.folder, { recursive: true, force: true });
  const restored = part(f, 'ST-1', 1);
  assert.equal(restored.branch, first.branch);
  assert.equal(fs.readFileSync(path.join(restored.folder, 'one.txt'), 'utf8').replace(/\r\n/g, '\n'), 'kept\n');
  assert.equal(git(restored.folder, 'branch', '--show-current'), first.branch);
});

test('an occupied destination on another branch is refused', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  git(first.folder, 'switch', '-c', 'feat/work--other');
  assert.throws(() => part(f, 'ST-1', 1), /occupied/);
  assert.equal(git(first.folder, 'branch', '--show-current'), 'feat/work--other');
  assert.equal(fs.existsSync(path.join(first.folder, 'base.txt')), true);
});

test('invalid identities and branches are rejected before git writes', t => {
  const f = fixture(t);
  assert.throws(() => worktrees.branchFor('feat/work', 0), /Invalid worktree branch/);
  assert.throws(() => worktrees.branchFor('feat/work', 1.5), /Invalid worktree branch/);
  assert.throws(() => worktrees.branchFor('-sideways', 1), /Invalid worktree branch/);
  assert.throws(() => worktrees.worktreePath(f.dir, '../escape', 'MT-1', 'ST-1'), /Invalid worktree identity/);
  assert.throws(() => worktrees.create({ ...f, subId: 'ST 1', index: 1, workBranch: 'feat/work' }), /Invalid worktree identity/);
  assert.equal(fs.existsSync(path.join(f.dir, 'worktrees')), false);
});

test('cleanup refuses a path outside the work and leaves it in place', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  const outside = path.join(f.root, 'outside folder é');
  fs.mkdirSync(outside);
  fs.writeFileSync(path.join(outside, 'keep.txt'), 'keep\n');
  assert.throws(() => worktrees.cleanup({ ...f, parts: [{ ...first, folder: outside }] }), /escaped/);
  assert.equal(fs.readFileSync(path.join(outside, 'keep.txt'), 'utf8'), 'keep\n');
  assert.equal(fs.existsSync(first.folder), true);
  assert.match(git(f.folder, 'branch', '--list', first.branch), /feat\/work--part-1/);
});

test('worktree paths with spaces and non-ASCII names round-trip', t => {
  const f = fixture(t);
  f.dir = path.join(f.root, 'plan data é');
  const created = part(f, 'ST-1', 1);
  assert.equal(fs.realpathSync(created.folder).startsWith(fs.realpathSync(f.dir)), true);
  fs.writeFileSync(path.join(created.folder, 'note.txt'), 'é\n'); git(created.folder, 'add', '.'); git(created.folder, 'commit', '-m', 'note');
  assert.deepEqual(worktrees.integrate({ folder: f.folder, parts: [created] }), { ok: true });
  assert.equal(fs.readFileSync(path.join(f.folder, 'note.txt'), 'utf8').replace(/\r\n/g, '\n'), 'é\n');
  worktrees.cleanup({ ...f, parts: [created] });
  assert.equal(fs.existsSync(created.folder), false);
});

test('concurrent commits in two worktrees both merge', async t => {
  const f = fixture(t), first = part(f, 'ST-1', 1), second = part(f, 'ST-2', 2);
  await Promise.all([
    (async () => {
      fs.writeFileSync(path.join(first.folder, 'one.txt'), 'one\n');
      await gitAsync(first.folder, ['add', 'one.txt']);
      await gitAsync(first.folder, ['commit', '-m', 'one']);
    })(),
    (async () => {
      fs.writeFileSync(path.join(second.folder, 'two.txt'), 'two\n');
      await gitAsync(second.folder, ['add', 'two.txt']);
      await gitAsync(second.folder, ['commit', '-m', 'two']);
    })()
  ]);
  assert.deepEqual(worktrees.integrate({ folder: f.folder, parts: [first, second] }), { ok: true });
  assert.equal(fs.readFileSync(path.join(f.folder, 'one.txt'), 'utf8').replace(/\r\n/g, '\n'), 'one\n');
  assert.equal(fs.readFileSync(path.join(f.folder, 'two.txt'), 'utf8').replace(/\r\n/g, '\n'), 'two\n');
  const subjects = git(f.folder, 'log', '--first-parent', '--format=%s');
  assert.match(subjects, /Merge branch 'feat\/work--part-2'[\s\S]*Merge branch 'feat\/work--part-1'/);
});

test('explicit drop removes a locked worktree', t => {
  const f = fixture(t), first = part(f, 'ST-1', 1);
  fs.writeFileSync(path.join(first.folder, 'locked.txt'), 'locked\n');
  git(f.folder, 'worktree', 'lock', first.folder);
  worktrees.cleanup({ ...f, parts: [first], discardBranches: true });
  assert.equal(fs.existsSync(first.folder), false);
  assert.equal(git(f.folder, 'branch', '--list', first.branch), '');
});

function finish(info, status = 'COMPLETE', extra = {}) {
  fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId,
    megaTaskId: info.megaTaskId, status, summary: status === 'COMPLETE' ? 'Done' : 'Stopped', filesChanged: extra.filesChanged || [], tests: [],
    blockers: extra.blockers || [], nextAction: 'Review' }));
  fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
}
function harness(f, { count = 2, spawnWorker, alive = () => false, inspect, job } = {}) {
  const attemptsDir = path.join(f.root, 'attempts');
  const item = { project: { id: f.projectId, name: 'Fixture', folder: f.folder }, revisions: [{ id: 1, direction: 'Build', open: [] }] };
  const models = Array.from({ length: count }, (_, n) => ({ id: `model-${n + 1}`, provider: 'codex', cli: `gpt-${n + 1}`, enabled: true, available: true, cost: 1 }));
  const store = { usage: {} }, row = { dispatch: [] }, launches = [];
  const bridge = createExecutionBridge({ store, dir: attemptsDir, attempts: createAttemptEngine({ dir: attemptsDir }), rowFor: () => row,
    models: () => models, leadId: () => 'lead', sessionId: 'session', now: () => new Date().toISOString(), persist: () => {},
    inspect: inspect || (() => ({ git: true, commitId: git(f.folder, 'rev-parse', 'HEAD'), dirty: [], names: [] })), isProcessAlive: alive,
    spawnWorker(info) { launches.push(info); spawnWorker(info, launches); return { pid: 7000 + launches.length }; } });
  return { item, attemptsDir, models, store, row, launches, bridge, job: job || { id: 'job-edge', title: 'Edge', detail: 'Edge work', revision_id: 1, kind: 'implementation' } };
}
function slices(models, titles) {
  return titles.map((slice, index) => ({ modelId: models[index].id, slice, scope: [`${index + 1}.txt`], dependsOn: [] }));
}

test('one build part and every verify step stay in the project folder', t => {
  const f = fixture(t);
  const h = harness(f, { count: 2, spawnWorker(info) { finish(info); } });
  const mega = h.bridge.propose(h.item, h.job, [
    { modelId: h.models[0].id, slice: 'Only build', scope: ['only.txt'], dependsOn: [] },
    { modelId: h.models[1].id, slice: 'Verify the work independently', dependsOn: [0] }
  ]);
  h.bridge.approve(f.projectId, mega.id);
  for (let n = 0; n < 4 && mega.status !== 'COMPLETE'; n++) h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'COMPLETE');
  assert.equal(h.launches.length, 2);
  assert.equal(h.launches[0].folder, f.folder);
  assert.equal(h.launches[1].folder, f.folder);
  assert.equal(fs.existsSync(path.join(h.attemptsDir, 'worktrees')), false);
});

test('a folder that is not a git repository runs every part in that folder', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worktrees-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const f = { root, folder: path.join(root, 'plain project é'), projectId: 'project-1' };
  fs.mkdirSync(f.folder);
  const h = harness(f, { inspect: () => ({ git: false, dirty: [], names: [] }), spawnWorker(info) {
    assert.equal(info.folder, f.folder);
    fs.writeFileSync(path.join(f.folder, 'shared.txt'), `${info.subTaskId}\n`);
    finish(info);
  } });
  const mega = h.bridge.propose(h.item, h.job, slices(h.models, ['First', 'Second']));
  h.bridge.approve(f.projectId, mega.id);
  for (let n = 0; n < 4 && mega.status !== 'COMPLETE'; n++) h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'COMPLETE');
  assert.equal(h.launches.length, 2);
  assert.equal(h.launches[0].folder, h.launches[1].folder);
  assert.equal(fs.existsSync(path.join(h.attemptsDir, 'worktrees')), false);
});

test('a commit outside the worktree scope blocks the part and does not merge', t => {
  const f = fixture(t);
  const h = harness(f, { spawnWorker(info, launches) {
    const n = launches.length;
    fs.writeFileSync(path.join(info.folder, `${n}.txt`), `${n}\n`);
    if (n === 1) fs.writeFileSync(path.join(info.folder, 'secret.txt'), 'outside\n');
    git(info.folder, 'add', '.'); git(info.folder, 'commit', '-m', `part ${n}`);
    finish(info, 'COMPLETE', { filesChanged: [`${n}.txt`] });
  } });
  const mega = h.bridge.propose(h.item, h.job, slices(h.models, ['First', 'Second']));
  h.bridge.approve(f.projectId, mega.id);
  h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'PAUSED');
  assert.match(h.launches[0].job.detail, /this worker folder/);
  assert.equal(h.store.execution.subTasks[0].status, 'BLOCKED');
  assert.match(h.store.execution.subTasks[0].scopeViolation.join(','), /secret\.txt/);
  assert.equal(fs.existsSync(path.join(f.folder, 'secret.txt')), false);
  assert.equal(fs.existsSync(path.join(f.folder, '1.txt')), false);
  assert.equal(fs.existsSync(h.launches[0].folder), true);
  assert.equal(fs.existsSync(h.launches[1].folder), true);
});

test('detached HEAD fails worktree creation and leaves the project untouched', t => {
  const f = fixture(t);
  const head = git(f.folder, 'rev-parse', 'HEAD');
  git(f.folder, 'checkout', '--detach');
  const h = harness(f, { spawnWorker() { throw new Error('worker must not start'); } });
  const mega = h.bridge.propose(h.item, h.job, slices(h.models, ['First', 'Second']));
  h.bridge.approve(f.projectId, mega.id);
  h.bridge.advance(h.item, h.job, []);
  h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'FAILED');
  assert.equal(h.launches.length, 0);
  assert.match(h.store.execution.results[0].summary, /Worktree creation failed/);
  assert.equal(git(f.folder, 'rev-parse', 'HEAD'), head);
  assert.equal(fs.existsSync(path.join(h.attemptsDir, 'worktrees')), false);
});

test('an integration result that does not merge stays blocked and keeps the worktrees', t => {
  const f = fixture(t);
  const h = harness(f, { count: 2, spawnWorker(info, launches) {
    if (launches.length <= 2) {
      fs.writeFileSync(path.join(info.folder, 'base.txt'), `part ${launches.length}\n`);
      git(info.folder, 'add', '.'); git(info.folder, 'commit', '-m', `part ${launches.length}`);
      finish(info, 'COMPLETE', { filesChanged: ['base.txt'] });
    } else finish(info, 'COMPLETE', { filesChanged: ['base.txt'] });
  } });
  const mega = h.bridge.propose(h.item, h.job, [{ modelId: h.models[0].id, slice: 'First', dependsOn: [] }, { modelId: h.models[1].id, slice: 'Second', dependsOn: [] }]);
  h.bridge.approve(f.projectId, mega.id);
  for (let n = 0; n < 5 && mega.status === 'RUNNING'; n++) h.bridge.advance(h.item, h.job, []);
  assert.notEqual(mega.status, 'COMPLETE');
  assert.equal(h.launches.length, 3);
  assert.match(h.launches[2].job.detail, /feat\/work--part-1/);
  assert.match(h.launches[2].job.detail, /feat\/work--part-2/);
  assert.match(h.launches[2].job.detail, /base\.txt/);
  assert.equal(h.store.execution.subTasks.find(sub => sub.type === 'INTEGRATION').status, 'BLOCKED');
  assert.equal(fs.readFileSync(path.join(f.folder, 'base.txt'), 'utf8').replace(/\r\n/g, '\n'), 'part 1\n');
  assert.equal(fs.existsSync(h.launches[0].folder), true);
  assert.equal(fs.existsSync(h.launches[1].folder), true);
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
});

test('a failed part is not merged and its worktree is reused on resume', t => {
  const f = fixture(t);
  const h = harness(f, { spawnWorker(info, launches) {
    if (launches.length === 1) {
      fs.writeFileSync(path.join(info.folder, '1.txt'), '1\n'); git(info.folder, 'add', '.'); git(info.folder, 'commit', '-m', 'one');
      finish(info);
    } else if (launches.length === 2) {
      fs.writeFileSync(path.join(info.folder, 'recover.txt'), 'keep\n');
      finish(info, 'FAILED', { blockers: ['Fixture failure'] });
    } else {
      const kept = fs.readFileSync(path.join(info.folder, 'recover.txt'), 'utf8');
      fs.rmSync(path.join(info.folder, 'recover.txt'));
      fs.writeFileSync(path.join(info.folder, '2.txt'), kept);
      git(info.folder, 'add', '2.txt'); git(info.folder, 'commit', '-m', 'recover');
      finish(info, 'COMPLETE', { filesChanged: ['2.txt'] });
    }
  } });
  const mega = h.bridge.propose(h.item, h.job, slices(h.models, ['First', 'Second']));
  h.bridge.approve(f.projectId, mega.id);
  h.bridge.advance(h.item, h.job, []);
  h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'FAILED');
  assert.equal(fs.existsSync(path.join(f.folder, '1.txt')), false);
  let resumed = h.bridge.resume(h.item, h.job, { megaTaskId: mega.id, checkpointId: mega.currentCheckpointId });
  if (!resumed.ok) resumed = h.bridge.resume(h.item, h.job, { megaTaskId: mega.id, checkpointId: mega.currentCheckpointId, confirmChanges: true });
  assert.equal(resumed.ok, true, JSON.stringify(resumed));
  for (let n = 0; n < 4 && mega.status !== 'COMPLETE'; n++) h.bridge.advance(h.item, h.job, []);
  assert.equal(h.launches.length, 3);
  assert.equal(h.launches[2].folder, h.launches[1].folder);
  assert.equal(mega.status, 'COMPLETE');
  assert.equal(fs.readFileSync(path.join(f.folder, '1.txt'), 'utf8').replace(/\r\n/g, '\n'), '1\n');
  assert.equal(fs.readFileSync(path.join(f.folder, '2.txt'), 'utf8').replace(/\r\n/g, '\n'), 'keep\n');
  assert.equal(fs.existsSync(h.launches[1].folder), false);
});

test('drop refuses while a worker is running and removes dirty work after failure', t => {
  const f = fixture(t);
  let alive = true;
  const h = harness(f, { alive: () => alive, spawnWorker(info, launches) {
    fs.writeFileSync(path.join(info.folder, 'recover.txt'), `part ${launches.length}\n`);
    if (!alive) finish(info, 'FAILED', { blockers: ['stopped'] });
  } });
  const mega = h.bridge.propose(h.item, h.job, slices(h.models, ['First', 'Second']));
  h.bridge.approve(f.projectId, mega.id);
  h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'RUNNING');
  assert.throws(() => h.bridge.dropWork(h.item, mega.id), /Stop every worker/);
  assert.equal(fs.existsSync(h.launches[0].folder), true);
  alive = false;
  h.bridge.advance(h.item, h.job, []);
  h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'FAILED');
  h.bridge.dropWork(h.item, mega.id);
  assert.equal(fs.existsSync(h.launches[0].folder), false);
  assert.equal(fs.existsSync(h.launches[1].folder), false);
  assert.equal(git(f.folder, 'branch', '--list', 'feat/work--part-*'), '');
});

test('uncommitted part files pause integration and keep both worktrees', t => {
  const f = fixture(t);
  const h = harness(f, { spawnWorker(info, launches) {
    if (launches.length <= 2) {
      const n = launches.length;
      fs.writeFileSync(path.join(info.folder, `${n}.txt`), `${n}\n`);
      git(info.folder, 'add', `${n}.txt`); git(info.folder, 'commit', '-m', `part ${n}`);
      fs.writeFileSync(path.join(info.folder, 'scratch.txt'), 'keep\n');
      finish(info, 'COMPLETE', { filesChanged: [`${n}.txt`] });
    } else {
      assert.equal(info.folder, f.folder);
      assert.match(info.job.detail, /uncommitted changes/);
      assert.match(info.job.detail, /feat\/work--part-1/);
      assert.match(info.job.detail, /feat\/work--part-2/);
      finish(info);
    }
  } });
  const mega = h.bridge.propose(h.item, h.job, [{ modelId: h.models[0].id, slice: 'First', dependsOn: [] }, { modelId: h.models[1].id, slice: 'Second', dependsOn: [] }]);
  h.bridge.approve(f.projectId, mega.id);
  for (let n = 0; n < 6 && mega.status === 'RUNNING'; n++) h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'PAUSED');
  assert.equal(h.store.execution.subTasks.find(sub => sub.type === 'INTEGRATION').status, 'BLOCKED');
  assert.equal(fs.existsSync(path.join(f.folder, '1.txt')), false);
  assert.equal(fs.readFileSync(path.join(h.launches[0].folder, 'scratch.txt'), 'utf8'), 'keep\n');
  assert.equal(fs.readFileSync(path.join(h.launches[1].folder, 'scratch.txt'), 'utf8'), 'keep\n');
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
});

test('a worktree removed before integration is handed over and can still be merged', t => {
  const f = fixture(t);
  const h = harness(f, { spawnWorker(info, launches) {
    if (launches.length <= 2) {
      fs.writeFileSync(path.join(info.folder, `${launches.length}.txt`), `${launches.length}\n`);
      git(info.folder, 'add', '.'); git(info.folder, 'commit', '-m', `part ${launches.length}`);
      finish(info, 'COMPLETE', { filesChanged: [`${launches.length}.txt`] });
    } else {
      assert.match(info.job.detail, /Part worktree is unavailable/);
      assert.match(info.job.detail, /feat\/work--part-1/);
      assert.match(info.job.detail, /feat\/work--part-2/);
      git(f.folder, 'merge', '--no-ff', '--no-edit', launches[0].branch);
      git(f.folder, 'merge', '--no-ff', '--no-edit', launches[1].branch);
      finish(info, 'COMPLETE', { filesChanged: ['1.txt', '2.txt'] });
    }
  } });
  const mega = h.bridge.propose(h.item, h.job, [{ modelId: h.models[0].id, slice: 'First', dependsOn: [] }, { modelId: h.models[1].id, slice: 'Second', dependsOn: [] }]);
  h.bridge.approve(f.projectId, mega.id);
  h.bridge.advance(h.item, h.job, []);
  fs.rmSync(h.launches[0].folder, { recursive: true, force: true });
  fs.rmSync(h.launches[1].folder, { recursive: true, force: true });
  for (let n = 0; n < 4 && mega.status !== 'COMPLETE'; n++) h.bridge.advance(h.item, h.job, []);
  assert.equal(mega.status, 'COMPLETE', mega.nextAction);
  assert.equal(fs.readFileSync(path.join(f.folder, '1.txt'), 'utf8').replace(/\r\n/g, '\n'), '1\n');
  assert.equal(fs.readFileSync(path.join(f.folder, '2.txt'), 'utf8').replace(/\r\n/g, '\n'), '2\n');
  assert.equal(git(f.folder, 'branch', '--list', 'feat/work--part-*'), '');
  assert.equal(git(f.folder, 'status', '--porcelain'), '');
});
