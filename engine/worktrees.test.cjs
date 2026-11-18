'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
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
