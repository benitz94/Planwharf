'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { createServer } = require('../server.cjs');

const { gitEnvironment } = require('./project-workspace.cjs');
// The worker that runs the suite blocks every push. The user's commit button, and these fixtures, use a clean Git environment.
for (const key of Object.keys(process.env)) if (key === 'GIT_CONFIG_COUNT' || /^GIT_CONFIG_(KEY|VALUE)_\d+$/.test(key)) delete process.env[key];
const git = (cwd, ...args) => spawnSync('git', args, { cwd, encoding: 'utf8', env: gitEnvironment() });

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
  git(folder, 'switch', '-c', 'topic');
  const response = await post({ projectId: 'p', message: 'Add the folder check', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.pushed, true);
  assert.equal(body.branch, 'topic');
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Add the folder check');
  assert.equal(git(remote, 'log', '-1', '--format=%s', 'topic').stdout.trim(), 'Add the folder check');
  assert.equal(git(folder, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}').stdout.trim(), 'origin/topic');
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
  git(folder, 'switch', '-c', 'topic');
  git(folder, 'add', '-A'); git(folder, 'commit', '-q', '-m', 'Made earlier');
  const response = await post({ projectId: 'p', message: 'unused', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.commit, null);
  assert.equal(body.pushed, true);
  assert.equal(git(remote, 'log', '-1', '--format=%s', 'topic').stdout.trim(), 'Made earlier');
});

test('the app keeps answering while a slow push runs', async t => {
  const { folder, post, base } = await fixture(t);
  git(folder, 'switch', '-c', 'topic');
  // A pre-push hook that takes 2 seconds stands for a push waiting on the network or a sign-in.
  const hook = path.join(folder, '.git', 'hooks', 'pre-push');
  // Git's hook shell on Windows does not provide sleep. Node waits the same two seconds.
  const node = process.execPath.replace(/\\/g, '/');
  fs.writeFileSync(hook, `#!/bin/sh\n"${node}" -e "setTimeout(() => {}, 2000)"\n`); fs.chmodSync(hook, 0o755);
  let pushDone = false;
  const pending = post({ projectId: 'p', message: 'In the background', push: true }).then(response => { pushDone = true; return response; });
  await new Promise(resolve => setTimeout(resolve, 300));
  const answered = await fetch(base + '/api/app');
  assert.equal(answered.status, 200);
  assert.equal(pushDone, false, 'the app answered while the push was still running');
  assert.equal((await pending).status, 200);
});

test('a branch whose upstream is origin/main is pushed to its own name', async t => {
  const { folder, remote, post } = await fixture(t);
  assert.equal(git(folder, 'push', '-u', 'origin', 'main').status, 0);
  const main = git(folder, 'rev-parse', 'HEAD').stdout.trim();
  git(folder, 'switch', '-c', 'work');
  assert.equal(git(folder, 'branch', '--set-upstream-to', 'origin/main').status, 0);
  const response = await post({ projectId: 'p', message: 'On the work branch', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.pushed, true);
  assert.equal(body.branch, 'work');
  const published = git(folder, 'rev-parse', 'HEAD').stdout.trim();
  assert.equal(git(remote, 'rev-parse', 'work').stdout.trim(), published);
  assert.equal(git(remote, 'rev-parse', 'main').stdout.trim(), main);
  assert.notEqual(published, main);
  assert.equal(git(folder, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}').stdout.trim(), 'origin/work');
});

test('a push from main is refused and remote main is unchanged', async t => {
  const { folder, remote, post } = await fixture(t);
  assert.equal(git(folder, 'push', '-u', 'origin', 'main').status, 0);
  const before = git(remote, 'rev-parse', 'main').stdout.trim();
  const response = await post({ projectId: 'p', message: 'Do not publish main', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.equal(body.error, 'This is main: Planwharf does not push it. Push it yourself if you mean to.');
  assert.equal(git(remote, 'rev-parse', 'main').stdout.trim(), before);
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Do not publish main');
});

test('a push of the remote default branch is refused when it is not named main', async t => {
  const { folder, remote, post } = await fixture(t);
  assert.equal(git(remote, 'symbolic-ref', 'HEAD', 'refs/heads/trunk').status, 0);
  assert.equal(git(folder, 'branch', '-m', 'main', 'trunk').status, 0);
  assert.equal(git(folder, 'push', '-u', 'origin', 'trunk').status, 0);
  const before = git(remote, 'rev-parse', 'trunk').stdout.trim();
  const response = await post({ projectId: 'p', message: 'Do not publish the default', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.equal(body.error, 'This is main: Planwharf does not push it. Push it yourself if you mean to.');
  assert.equal(git(remote, 'rev-parse', 'trunk').stdout.trim(), before);
});

test('a failed push shows the first error line and hides the HEAD:main advice', async t => {
  const { folder, remote, post } = await fixture(t);
  git(folder, 'switch', '-c', 'work');
  const hook = path.join(folder, '.git', 'hooks', 'pre-push');
  fs.writeFileSync(hook, '#!/bin/sh\necho "fatal: rejected by the remote" >&2\necho "hint: git push origin HEAD:main" >&2\nexit 1\n');
  fs.chmodSync(hook, 0o755);
  const response = await post({ projectId: 'p', message: 'Will fail', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.equal(body.error, 'fatal: rejected by the remote\nPush failed. The commit is saved; nothing was sent.');
  assert.equal(body.error.includes('HEAD:main'), false);
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Will fail');
  assert.notEqual(git(remote, 'rev-parse', '--verify', 'work').status, 0);
});

test('a push on main with a clean working tree is refused', async t => {
  const { folder, remote, post } = await fixture(t);
  git(folder, 'add', '-A'); git(folder, 'commit', '-q', '-m', 'Clean');
  assert.equal(git(folder, 'push', '-u', 'origin', 'main').status, 0);
  const before = git(remote, 'rev-parse', 'main').stdout.trim();
  const response = await post({ projectId: 'p', message: 'Push main', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.equal(body.error, 'This is main: Planwharf does not push it. Push it yourself if you mean to.');
  assert.equal(git(remote, 'rev-parse', 'main').stdout.trim(), before);
});

test('a push on detached HEAD is refused and nothing is pushed', async t => {
  const { folder, post } = await fixture(t);
  const sha = git(folder, 'rev-parse', 'HEAD').stdout.trim();
  git(folder, 'checkout', '-q', sha);
  const response = await post({ projectId: 'p', message: 'Detached commit', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.match(body.error, /not pushed: the folder is not on a branch/);
});

test('a failed push with an unreachable remote preserves the local commit', async t => {
  const { folder, post } = await fixture(t);
  git(folder, 'switch', '-c', 'work');
  git(folder, 'remote', 'set-url', 'origin', 'file:///nonexistent-remote-path-invalid.git');
  const response = await post({ projectId: 'p', message: 'Local work saved', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.match(body.error, /Push failed\. The commit is saved; nothing was sent\./);
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Local work saved');
});

test('a push failure where the first line mentions HEAD:main hides it and shows the error', async t => {
  const { folder, remote, post } = await fixture(t);
  git(folder, 'switch', '-c', 'work');
  const hook = path.join(folder, '.git', 'hooks', 'pre-push');
  fs.writeFileSync(hook, '#!/bin/sh\necho "hint: git push origin HEAD:main" >&2\necho "fatal: real error below advice" >&2\nexit 1\n');
  fs.chmodSync(hook, 0o755);
  const response = await post({ projectId: 'p', message: 'Will fail advice first', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.equal(body.error, 'fatal: real error below advice\nPush failed. The commit is saved; nothing was sent.');
  assert.equal(body.error.includes('HEAD:main'), false);
});

test('a feature branch pushes cleanly when remote default branch is trunk', async t => {
  const { folder, remote, post } = await fixture(t);
  assert.equal(git(remote, 'symbolic-ref', 'HEAD', 'refs/heads/trunk').status, 0);
  assert.equal(git(folder, 'branch', '-m', 'main', 'trunk').status, 0);
  assert.equal(git(folder, 'push', '-u', 'origin', 'trunk').status, 0);
  git(folder, 'switch', '-c', 'feat-1');
  const response = await post({ projectId: 'p', message: 'Feature on custom default', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.pushed, true);
  assert.equal(body.branch, 'feat-1');
  assert.equal(git(remote, 'log', '-1', '--format=%s', 'feat-1').stdout.trim(), 'Feature on custom default');
  assert.equal(git(folder, 'rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{u}').stdout.trim(), 'origin/feat-1');
});

test('a non-fast-forward push failure hides advice and keeps the local commit', async t => {
  const { folder, remote, post } = await fixture(t);
  git(folder, 'switch', '-c', 'work');
  const first = await post({ projectId: 'p', message: 'First on work', push: true });
  assert.equal(first.status, 200);
  const other = path.join(path.dirname(folder), 'other');
  git(path.dirname(folder), 'clone', '-q', remote, other);
  git(other, 'config', 'user.name', 'Other'); git(other, 'config', 'user.email', 'other@example.invalid');
  git(other, 'switch', 'work');
  fs.writeFileSync(path.join(other, 'conflict.txt'), 'other');
  git(other, 'add', '-A'); git(other, 'commit', '-q', '-m', 'Other commit');
  assert.equal(git(other, 'push', 'origin', 'work').status, 0);
  fs.writeFileSync(path.join(folder, 'conflict.txt'), 'local');
  const response = await post({ projectId: 'p', message: 'Local commit ahead of fetch', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  assert.match(body.error, /Push failed\. The commit is saved; nothing was sent\./);
  assert.equal(body.error.includes('hint:'), false);
  assert.equal(body.error.includes('HEAD:'), false);
  assert.equal(git(folder, 'log', '-1', '--format=%s').stdout.trim(), 'Local commit ahead of fetch');
});

function installFakeGh(bin, log) {
  fs.mkdirSync(bin, { recursive: true });
  const stub = path.join(bin, 'gh-stub.cjs');
  fs.writeFileSync(stub, `'use strict';
const fs = require('node:fs');
const args = process.argv.slice(2);
if (args[0] === 'pr' && args[1] === 'view') process.exit(1);
fs.appendFileSync(process.env.GH_LOG, args.join('\\n') + '\\n');
process.stdout.write('https://github.com/owner/repo/pull/7\\n');
`);
  process.env.GH_LOG = log;
  process.env.GH_NODE = process.execPath;
  process.env.GH_STUB = stub;
  if (process.platform === 'win32') {
    // spawn('gh') looks up gh.exe, so a .cmd stub never wins over GitHub CLI.
    const windir = process.env.WINDIR || 'C:\\Windows';
    const csc = [
      path.join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
      path.join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe')
    ].find(file => fs.existsSync(file));
    if (!csc) return false;
    const cs = path.join(bin, 'GhShim.cs');
    fs.writeFileSync(cs, [
      'using System; using System.Diagnostics;',
      'class P {',
      '  static int Main(string[] args) {',
      '    var q = ((char)34).ToString();',
      '    var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("GH_NODE"));',
      '    psi.UseShellExecute = false;',
      '    psi.Arguments = q + Environment.GetEnvironmentVariable("GH_STUB") + q;',
      '    foreach (var a in args) psi.Arguments += " " + q + a.Replace(q, "\\\\" + q) + q;',
      '    var p = Process.Start(psi); p.WaitForExit(); return p.ExitCode;',
      '  }',
      '}',
      ''
    ].join('\n'));
    const compiled = spawnSync(csc, ['/nologo', '/out:' + path.join(bin, 'gh.exe'), cs], { encoding: 'utf8', windowsHide: true });
    return compiled.status === 0 && fs.existsSync(path.join(bin, 'gh.exe'));
  }
  fs.writeFileSync(path.join(bin, 'gh'), `#!/bin/sh\nexec "${process.execPath.replace(/\\/g, '/')}" "${stub.replace(/\\/g, '/')}" "$@"\n`);
  fs.chmodSync(path.join(bin, 'gh'), 0o755);
  return true;
}

test('the pull request opened by the push is named after the work, not "push"', async t => {
  const { folder, post } = await fixture(t);
  const bin = path.join(path.dirname(folder), 'bin'), log = path.join(path.dirname(folder), 'gh.log');
  const oldPath = process.env.PATH;
  const oldGh = { GH_LOG: process.env.GH_LOG, GH_NODE: process.env.GH_NODE, GH_STUB: process.env.GH_STUB };
  if (!installFakeGh(bin, log)) { t.skip('no C# compiler to stub gh.exe on Windows'); return; }
  process.env.PATH = bin + path.delimiter + oldPath;
  t.after(() => {
    process.env.PATH = oldPath;
    for (const [key, value] of Object.entries(oldGh)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  });
  git(folder, 'switch', '-c', 'topic');
  const response = await post({ projectId: 'p', message: 'push', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.prUrl, 'https://github.com/owner/repo/pull/7');
  const args = fs.readFileSync(log, 'utf8').split('\n');
  const title = args[args.indexOf('--title') + 1];
  assert.notEqual(title, 'push');
  assert.ok(title.length > 0);
});

test('the pull request title uses the revision direction when message is "push"', async t => {
  const { folder, post } = await fixture(t);
  const bin = path.join(path.dirname(folder), 'bin'), log = path.join(path.dirname(folder), 'gh.log');
  const oldPath = process.env.PATH;
  const oldGh = { GH_LOG: process.env.GH_LOG, GH_NODE: process.env.GH_NODE, GH_STUB: process.env.GH_STUB };
  if (!installFakeGh(bin, log)) { t.skip('no C# compiler to stub gh.exe on Windows'); return; }
  process.env.PATH = bin + path.delimiter + oldPath;
  t.after(() => {
    process.env.PATH = oldPath;
    for (const [key, value] of Object.entries(oldGh)) {
      if (value === undefined) delete process.env[key];
      else process.env[key] = value;
    }
  });
  const planPath = path.join(path.dirname(folder), 'plan.json');
  const notebook = JSON.parse(fs.readFileSync(planPath, 'utf8'));
  notebook.projects[0].revisions[0].direction = 'Implement job 42 on `feat/topic` from `origin/main`';
  fs.writeFileSync(planPath, JSON.stringify(notebook));

  git(folder, 'switch', '-c', 'topic');
  const response = await post({ projectId: 'p', message: 'push', push: true });
  const body = await response.json();
  assert.equal(response.status, 200, JSON.stringify(body));
  assert.equal(body.prUrl, 'https://github.com/owner/repo/pull/7');
  const args = fs.readFileSync(log, 'utf8').split('\n');
  const title = args[args.indexOf('--title') + 1];
  assert.equal(title, 'Implement job 42');
});

test('a refused push says why, not only "To <remote>"', async t => {
  const { folder, remote, post } = await fixture(t);
  git(folder, 'switch', '-c', 'topic');
  assert.equal((await post({ projectId: 'p', message: 'First', push: true })).status, 200);
  // Someone else pushes to the same branch; this folder is now behind it.
  const other = path.join(path.dirname(folder), 'other');
  git(path.dirname(folder), 'clone', '-q', '-b', 'topic', remote, other);
  git(other, 'config', 'user.name', 'Other'); git(other, 'config', 'user.email', 'other@example.invalid');
  fs.writeFileSync(path.join(other, 'other.txt'), 'x\n'); git(other, 'add', '-A'); git(other, 'commit', '-q', '-m', 'other');
  git(other, 'push', '-q', 'origin', 'topic');
  fs.writeFileSync(path.join(folder, 'mine.txt'), 'y\n');
  const response = await post({ projectId: 'p', message: 'Mine', push: true });
  const body = await response.json();
  assert.equal(response.status, 400);
  const said = body.error || body.reason || JSON.stringify(body);
  assert.match(said, /rejected|fetch first|non-fast-forward/i);
  assert.match(said, /behind/);
  assert.doesNotMatch(said.split('\n')[0], /^To /);
});
