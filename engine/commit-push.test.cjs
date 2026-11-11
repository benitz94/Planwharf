'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { createServer } = require('../server.cjs');

const git = (cwd, ...args) => spawnSync('git', args, { cwd, encoding: 'utf8' });

// The user commits and pushes the finished work from the app, never Planwharf by itself.
async function fixture(t, { withGit = true } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-commit-push-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'project'), remote = path.join(dir, 'remote.git');
  fs.mkdirSync(folder);
  if (withGit) {
    git(dir, 'init', '--bare', '-q', remote);
    git(folder, 'init', '-q', '-b', 'main');
    git(folder, 'config', 'user.name', 'Fixture'); git(folder, 'config', 'user.email', 'fixture@example.invalid');
    fs.writeFileSync(path.join(folder, 'README.md'), 'start\n');
    git(folder, 'add', '-A'); git(folder, 'commit', '-q', '-m', 'start');
    git(folder, 'remote', 'add', 'origin', remote);
  }
  fs.writeFileSync(path.join(folder, 'check.py'), 'print("ok")\n');
  const item = structuredClone(require('../fixtures/starter-notebook.json').projects[0]);
  item.project = { ...item.project, id: 'p', name: 'Project', folder };
  item.tasks = [];
  const planFile = path.join(dir, 'plan.json');
  fs.writeFileSync(planFile, JSON.stringify({ schema: 2, projects: [item] }));
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), refreshModels: false, fetchPlans: null, monitorIntervalMs: 60000, models: [] });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const post = body => fetch(`http://127.0.0.1:${server.address().port}/api/repository/commit`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  return { folder, remote, post, base: `http://127.0.0.1:${server.address().port}` };
}

test('commit and push the finished work to the remote', async t => {
  const { folder, remote, post } = await fixture(t);
  const response = await post({ projectId: 'p', message: 'Add the folder check', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.pushed, true);
  assert.equal(body.branch, 'main');
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Add the folder check');
  assert.equal(git(remote, 'log', '-1', '--format=%s', 'main').stdout.trim(), 'Add the folder check');
  assert.equal(git(folder, 'status', '--porcelain').stdout.trim(), '');
});

test('commit only, without push, and nothing to commit is refused', async t => {
  const { folder, remote, post } = await fixture(t);
  const first = await post({ projectId: 'p', message: 'Local only', push: false });
  assert.equal(first.status, 200);
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Local only');
  assert.notEqual(git(remote, 'log', '-1', '--format=%s', 'main').stdout.trim(), 'Local only');
  const again = await post({ projectId: 'p', message: 'Nothing', push: false });
  assert.equal(again.status, 400);
  assert.match((await again.json()).error, /Nothing to commit/);
});

test('a folder without git or an empty message is refused', async t => {
  const { post } = await fixture(t, { withGit: false });
  assert.equal((await post({ projectId: 'p', message: 'x', push: false })).status, 400);
  assert.equal((await post({ projectId: 'p', message: '  ', push: false })).status, 400);
});

test('a commit already made can be pushed on its own', async t => {
  const { folder, remote, post } = await fixture(t);
  git(folder, 'add', '-A'); git(folder, 'commit', '-q', '-m', 'Made earlier');
  const response = await post({ projectId: 'p', message: 'unused', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.commit, null);
  assert.equal(body.pushed, true);
  assert.equal(git(remote, 'log', '-1', '--format=%s', 'main').stdout.trim(), 'Made earlier');
});

test('the app keeps answering while a slow push runs', async t => {
  const { folder, post, base } = await fixture(t);
  // A pre-push hook that takes 2 seconds stands for a push waiting on the network or a sign-in.
  const hook = path.join(folder, '.git', 'hooks', 'pre-push');
  fs.writeFileSync(hook, '#!/bin/sh\nsleep 2\n'); fs.chmodSync(hook, 0o755);
  let pushDone = false;
  const pending = post({ projectId: 'p', message: 'In the background', push: true }).then(response => { pushDone = true; return response; });
  await new Promise(resolve => setTimeout(resolve, 300));
  const answered = await fetch(base + '/api/app');
  assert.equal(answered.status, 200);
  assert.equal(pushDone, false, 'the app answered while the push was still running');
  assert.equal((await pending).status, 200);
});
