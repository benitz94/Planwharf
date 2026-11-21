'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const pr = require('./pr-check.cjs');
const { gitEnvironment } = require('./project-workspace.cjs');

// The worker that runs the suite blocks every push. These fixtures use a clean Git environment.
const git = (cwd, ...args) => {
  const out = spawnSync('git', args, { cwd, encoding: 'utf8', env: { ...gitEnvironment(), GIT_AUTHOR_NAME: 't', GIT_AUTHOR_EMAIL: 't@t', GIT_COMMITTER_NAME: 't', GIT_COMMITTER_EMAIL: 't@t' } });
  if (out.status !== 0) throw new Error(args.join(' ') + ': ' + out.stderr);
  return out.stdout.trim();
};

function withPushGuard(fn) {
  const saved = {};
  for (const key of Object.keys(process.env)) if (key === 'GIT_CONFIG_COUNT' || /^GIT_CONFIG_(KEY|VALUE)_\d+$/.test(key)) saved[key] = process.env[key];
  process.env.GIT_CONFIG_COUNT = '1';
  process.env.GIT_CONFIG_KEY_0 = 'url.planwharf-push-blocked://.pushInsteadOf';
  process.env.GIT_CONFIG_VALUE_0 = '';
  const restore = () => {
    for (const key of Object.keys(process.env)) if (key === 'GIT_CONFIG_COUNT' || /^GIT_CONFIG_(KEY|VALUE)_\d+$/.test(key)) delete process.env[key];
    for (const [key, value] of Object.entries(saved)) process.env[key] = value;
  };
  return Promise.resolve().then(fn).finally(restore);
}

function setup() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-fix-'));
  const remote = path.join(root, 'remote.git'), folder = path.join(root, 'project');
  git(root, 'init', '-q', '--bare', '-b', 'main', remote);
  git(root, 'clone', '-q', remote, folder);
  for (const [k, v] of [['user.name', 't'], ['user.email', 't@t']]) git(folder, 'config', k, v);
  fs.writeFileSync(path.join(folder, 'notes.md'), 'one\n');
  git(folder, 'add', '.'); git(folder, 'commit', '-q', '-m', 'start'); git(folder, 'push', '-q', 'origin', 'main');
  git(folder, 'switch', '-q', '-c', 'feat/x');
  fs.writeFileSync(path.join(folder, 'notes.md'), 'one\nfrom the branch\n');
  git(folder, 'commit', '-q', '-am', 'branch'); git(folder, 'push', '-q', 'origin', 'feat/x');
  git(folder, 'switch', '-q', 'main');
  fs.writeFileSync(path.join(folder, 'notes.md'), 'one\nfrom main\n');
  git(folder, 'commit', '-q', '-am', 'main'); git(folder, 'push', '-q', 'origin', 'main');
  return { root, remote, folder, worktree: path.join(root, 'data', 'worktrees', 'p', 'pr-1', 'fix-a') };
}

test('a conflicting pull request is fixed in its own copy and pushed to the same branch', async () => {
  const { remote, folder, worktree } = setup();
  const before = git(folder, 'rev-parse', '--abbrev-ref', 'HEAD');
  const fix = await pr.prepareWorktree(folder, { branch: 'feat/x', base: 'main' }, worktree);
  assert.ok(fix.ok); assert.deepStrictEqual(fix.conflicts, ['notes.md']);
  // Unresolved: nothing is pushed.
  const early = await pr.finishFix(folder, worktree, fix, 1);
  assert.strictEqual(early.ok, false);
  // The model's work: keep both sides.
  fs.writeFileSync(path.join(worktree, 'notes.md'), 'one\nfrom main\nfrom the branch\n');
  git(worktree, 'add', 'notes.md');
  const done = await pr.finishFix(folder, worktree, fix, 1);
  assert.ok(done.ok, done.reason); assert.ok(done.changed);
  assert.strictEqual(git(remote, 'show', 'feat/x:notes.md'), 'one\nfrom main\nfrom the branch');
  // The owner's folder never moved.
  assert.strictEqual(git(folder, 'rev-parse', '--abbrev-ref', 'HEAD'), before);
  await pr.removeWorktree(folder, worktree);
  assert.ok(!fs.existsSync(worktree));
});

test('conflict markers left in a file stop the push', async () => {
  const { remote, folder, worktree } = setup();
  const tip = git(remote, 'rev-parse', 'feat/x');
  const fix = await pr.prepareWorktree(folder, { branch: 'feat/x', base: 'main' }, worktree);
  git(worktree, 'add', 'notes.md');
  const done = await pr.finishFix(folder, worktree, fix, 1);
  assert.strictEqual(done.ok, false); assert.match(done.reason, /Conflict markers/);
  assert.strictEqual(git(remote, 'rev-parse', 'feat/x'), tip);
  await pr.removeWorktree(folder, worktree);
});

test('a fix is pushed while the worker push guard is set', async () => {
  await withPushGuard(async () => {
    const { remote, folder, worktree } = setup();
    const fix = await pr.prepareWorktree(folder, { branch: 'feat/x', base: 'main' }, worktree);
    assert.ok(fix.ok, fix.reason);
    fs.writeFileSync(path.join(worktree, 'notes.md'), 'one\nfrom main\nfrom the branch\n');
    git(worktree, 'add', 'notes.md');
    const done = await pr.finishFix(folder, worktree, fix, 1);
    assert.ok(done.ok, done.reason);
    assert.strictEqual(git(remote, 'show', 'feat/x:notes.md'), 'one\nfrom main\nfrom the branch');
    await pr.removeWorktree(folder, worktree);
    assert.ok(!fs.existsSync(worktree));
  });
});

test('a concurrent push to the remote branch refuses the fix push without losing work', async () => {
  const { remote, folder, worktree } = setup();
  const fix = await pr.prepareWorktree(folder, { branch: 'feat/x', base: 'main' }, worktree);
  assert.ok(fix.ok, fix.reason);

  // A concurrent push lands on the remote branch before finishFix runs
  const otherFolder = path.join(path.dirname(folder), 'other');
  git(path.dirname(folder), 'clone', '-q', remote, otherFolder);
  for (const [k, v] of [['user.name', 'other'], ['user.email', 'other@example.com']]) git(otherFolder, 'config', k, v);
  git(otherFolder, 'switch', '-q', 'feat/x');
  fs.writeFileSync(path.join(otherFolder, 'notes.md'), 'one\nfrom concurrent author\n');
  git(otherFolder, 'commit', '-q', '-am', 'concurrent change');
  git(otherFolder, 'push', '-q', 'origin', 'feat/x');
  const remoteTip = git(remote, 'rev-parse', 'feat/x');

  // Now resolve the conflict locally in the worktree and attempt to finish the fix
  fs.writeFileSync(path.join(worktree, 'notes.md'), 'one\nfrom main\nfrom the branch\n');
  git(worktree, 'add', 'notes.md');
  const done = await pr.finishFix(folder, worktree, fix, 1);

  // The non-fast-forward push is rejected, protecting remote history
  assert.strictEqual(done.ok, false);
  assert.match(done.reason, /push was refused/i);
  assert.strictEqual(git(remote, 'rev-parse', 'feat/x'), remoteTip);

  // Worktree preserves the local work for inspection/recovery
  assert.strictEqual(fs.readFileSync(path.join(worktree, 'notes.md'), 'utf8'), 'one\nfrom main\nfrom the branch\n');
  await pr.removeWorktree(folder, worktree);
  assert.ok(!fs.existsSync(worktree));
});

test('a clean fix where nothing needed changing does not push', async () => {
  const { remote, folder, worktree } = setup();
  git(folder, 'switch', '-q', '-c', 'feat/clean');
  fs.writeFileSync(path.join(folder, 'extra.md'), 'extra\n');
  git(folder, 'add', '.'); git(folder, 'commit', '-q', '-m', 'clean');
  git(folder, 'push', '-q', 'origin', 'feat/clean');
  const tipBefore = git(remote, 'rev-parse', 'feat/clean');

  const fix = await pr.prepareWorktree(folder, { branch: 'feat/clean', base: 'main' }, worktree);
  assert.ok(fix.ok, fix.reason);
  assert.deepStrictEqual(fix.conflicts, []);

  const done = await pr.finishFix(folder, worktree, fix, 2);
  assert.ok(done.ok, done.reason);
  assert.strictEqual(done.changed, false);
  assert.strictEqual(git(remote, 'rev-parse', 'feat/clean'), tipBefore);

  await pr.removeWorktree(folder, worktree);
  assert.ok(!fs.existsSync(worktree));
});
