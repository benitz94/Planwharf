'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const pr = require('./pr-check.cjs');

const git = (cwd, ...args) => {
  const out = spawnSync('git', args, { cwd, encoding: 'utf8', env: { ...process.env, GIT_AUTHOR_NAME: 't', GIT_AUTHOR_EMAIL: 't@t', GIT_COMMITTER_NAME: 't', GIT_COMMITTER_EMAIL: 't@t' } });
  if (out.status !== 0) throw new Error(args.join(' ') + ': ' + out.stderr);
  return out.stdout.trim();
};

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
