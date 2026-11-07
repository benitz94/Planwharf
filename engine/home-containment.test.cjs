'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { projectHome, localGit } = require('./project-home.cjs');
const { homeNext } = require('./execution-next.cjs');
async function fixture(t) { const root = await fs.mkdtemp(path.join(os.tmpdir(), 'planwharf-home-containment-')); t.after(() => fs.rm(root, { recursive: true, force: true })); return root; }
const item = folder => ({ project: { id: 'alpha', folder }, revisions: [{ id: 1, title: 'Plan', direction: 'Goal', state: '', open: ['First question?', 'Second question?'], closed: [] }] });
test('imported paused project without a linked folder offers relink, never resume', async () => {
  const project = item(null);
  project.project.continuation = { resume: true };
  const home = await projectHome(project, { projects: [{ id: 'alpha', status: 'checkpointed', canResume: true }], researches: [] });
  assert.equal(home.next, 'Relink the project folder on this PC.');
  assert.notEqual(home.action, 'resume');
});
test('Home priority is relink, approval, resume, execution, question, review', async t => {
  const folder = await fixture(t), project = item(folder);
  const durable = { megaTasks: [{ id: 'm', projectId: 'alpha', status: 'RUNNING' }], subTasks: [{ id: 's', megaTaskId: 'm', title: 'Run tests', status: 'RUNNING' }] };
  assert.equal(homeNext(item(null), { status: 'needs_approval', canResume: true }, durable), 'Relink the project folder on this PC.');
  assert.equal(homeNext(project, { status: 'needs_approval', canResume: true }, {}), 'Approve the prepared job.');
  assert.equal(homeNext(project, { canResume: true }, {}), 'Resume from checkpoint.');
  assert.equal(homeNext(project, {}, durable), 'Work in progress: Run tests (s).');
  assert.equal(homeNext(project, {}, {}), 'Tell the lead what to build.');
  project.revisions[0].open = [];
  assert.equal(homeNext(project, { next: 'Untrusted invented next action' }, {}), 'Review the Living Plan.');
  const file = path.join(folder, 'file'); await fs.writeFile(file, 'not a directory');
  assert.equal(homeNext(item(file), {}, durable), 'Relink the project folder on this PC.');
});
test('Git inspection rejects inherited parent repositories instead of inspecting siblings', async t => {
  const root = await fixture(t), alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta');
  await fs.mkdir(alpha); await fs.mkdir(beta);
  execFileSync('git', ['init', '-q', root]);
  execFileSync('git', ['-C', root, 'config', 'user.email', 'fixture@example.invalid']);
  execFileSync('git', ['-C', root, 'config', 'user.name', 'Fixture']);
  await fs.writeFile(path.join(alpha, 'a.txt'), 'alpha'); await fs.writeFile(path.join(beta, 'b.txt'), 'beta');
  execFileSync('git', ['-C', root, 'add', '.']); execFileSync('git', ['-C', root, 'commit', '-qm', 'Fixture']);
  const before = await localGit(alpha);
  await fs.writeFile(path.join(beta, 'b.txt'), 'secret sibling change');
  const after = await localGit(alpha);
  assert.equal(before.readable, false);
  assert.equal(after.readable, false);
  assert.equal(after.fingerprint, undefined);
  await fs.writeFile(path.join(alpha, 'a.txt'), 'alpha change');
  assert.equal((await localGit(alpha)).readable, false);
});
test('linked project inspection does not fingerprint files through a directory symlink to another project', async t => {
  const root = await fixture(t), alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta');
  await fs.mkdir(path.join(alpha, 'linked'), { recursive: true }); await fs.mkdir(beta);
  execFileSync('git', ['init', '-q', alpha]);
  execFileSync('git', ['-C', alpha, 'config', 'user.email', 'fixture@example.invalid']);
  execFileSync('git', ['-C', alpha, 'config', 'user.name', 'Fixture']);
  await fs.writeFile(path.join(alpha, 'linked', 'data'), 'tracked');
  execFileSync('git', ['-C', alpha, 'add', '.']); execFileSync('git', ['-C', alpha, 'commit', '-qm', 'Fixture']);
  await fs.rm(path.join(alpha, 'linked'), { recursive: true });
  await fs.writeFile(path.join(beta, 'data'), 'sibling secret');
  try { await fs.symlink(beta, path.join(alpha, 'linked'), 'dir'); } catch (error) { if (error.code === 'EPERM') { t.skip('Directory symlinks unavailable'); return; } throw error; }
  const before = await localGit(alpha);
  await fs.writeFile(path.join(beta, 'data'), 'changed sibling secret');
  const after = await localGit(alpha);
  assert.equal(after.fingerprint, before.fingerprint);
  assert.equal(after.files.find(file => file.path === 'linked/data')?.fingerprint, 'deleted');
});

test('current durable MegaTask outranks stale row flags and another project or later task', async t => {
  const folder = await fixture(t), project = item(folder);
  const durable = { megaTasks: [{ id: 'active', projectId: 'alpha', status: 'RUNNING' }, { id: 'later', projectId: 'alpha', status: 'READY_FOR_APPROVAL' }, { id: 'foreign', projectId: 'beta', status: 'READY_FOR_APPROVAL' }], subTasks: [{ id: 's', megaTaskId: 'active', title: 'Run tests', status: 'RUNNING' }] };
  assert.equal(homeNext(project, { megaTaskId: 'active', status: 'needs_approval', canResume: true }, durable), 'Work in progress: Run tests (s).');
  assert.equal(homeNext(project, { megaTaskId: 'later' }, durable), 'Approve the prepared MegaTask.');
  assert.equal(homeNext(project, { megaTaskId: 'foreign' }, durable), 'Approve the prepared MegaTask.');
});

test('Git metadata symlink and gitdir redirect to another project fail closed', async t => {
  const root = await fixture(t), alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta');
  await fs.mkdir(alpha); await fs.mkdir(beta);
  execFileSync('git', ['init', '-q', beta]);
  await fs.writeFile(path.join(beta, 'private'), 'sibling-only content');
  try {
    await fs.symlink(path.join(beta, '.git'), path.join(alpha, '.git'), 'dir');
    assert.equal((await localGit(alpha)).readable, false);
    await fs.unlink(path.join(alpha, '.git'));
  } catch (error) { if (error.code !== 'EPERM') throw error; t.diagnostic('Directory symlink unavailable; gitdir redirect still tested.'); }
  await fs.writeFile(path.join(alpha, '.git'), `gitdir: ${path.join(beta, '.git')}\n`);
  assert.equal((await localGit(alpha)).readable, false);
});
test('Git inspection rejects redirected HEAD metadata before reading another project', async t => {
  const root = await fixture(t), alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta');
  await fs.mkdir(alpha); await fs.mkdir(beta);
  execFileSync('git', ['init', '-q', alpha]);
  await fs.writeFile(path.join(beta, 'private-head'), 'ref: refs/heads/private\n');
  await fs.unlink(path.join(alpha, '.git', 'HEAD'));
  try { await fs.symlink(path.join(beta, 'private-head'), path.join(alpha, '.git', 'HEAD')); } catch (error) { if (error.code === 'EPERM') { t.skip('File symlinks unavailable'); return; } throw error; }
  assert.equal((await localGit(alpha)).readable, false);
});

test('Git inspection ignores a submodule redirected to another project but observes local edits', async t => {
  const root = await fixture(t), alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta');
  const git = (folder, ...args) => execFileSync('git', ['-C', folder, ...args], { encoding: 'utf8', stdio: 'pipe' }).trim();
  for (const folder of [alpha, beta]) {
    await fs.mkdir(folder);
    git(folder, 'init', '-q');
    git(folder, 'config', 'user.name', 'Fixture');
    git(folder, 'config', 'user.email', 'fixture@example.invalid');
  }
  await fs.writeFile(path.join(beta, 'private'), 'Foreign initial content');
  git(beta, 'add', '.'); git(beta, 'commit', '-qm', 'Foreign fixture');
  await fs.mkdir(path.join(alpha, 'sub'));
  await fs.writeFile(path.join(alpha, 'sub', '.git'), `gitdir: ${path.join(beta, '.git')}\n`);
  git(alpha, 'update-index', '--add', '--cacheinfo', `160000,${git(beta, 'rev-parse', 'HEAD')},sub`);
  await fs.writeFile(path.join(alpha, 'local.txt'), 'Local initial content');
  git(alpha, 'add', 'local.txt'); git(alpha, 'commit', '-qm', 'Local fixture');
  git(alpha, 'config', 'diff.submodule', 'log');
  git(alpha, 'config', 'submodule.sub.ignore', 'none');
  const { defaultInspect } = require('./orchestrator.js');
  const { createRepositoryInspector } = require('./repository.cjs');
  const inspectRepository = createRepositoryInspector();
  async function capture() {
    const facts = defaultInspect(alpha), home = await localGit(alpha);
    const repository = await inspectRepository({ folder: alpha }, { force: true });
    assert.equal(facts.git, true);
    assert.equal(home.readable, true);
    assert.equal(typeof home.fingerprint, 'string');
    assert.equal(repository.local.git, true);
    return { dirty: facts.dirty, diffStat: facts.diffStat, fingerprint: home.fingerprint, changes: repository.local.changes };
  }
  const before = await capture();
  assert.deepEqual(before.dirty, []);
  assert.deepEqual(before.changes, []);
  assert.equal(before.diffStat, '');
  await fs.writeFile(path.join(beta, 'private'), 'Foreign changed content');
  git(beta, 'commit', '-qam', 'Foreign changed fixture');
  assert.deepEqual(await capture(), before, 'Foreign commits cannot change the selected project inspection');
  await fs.writeFile(path.join(alpha, 'local.txt'), 'Local changed content');
  const local = await capture();
  assert.notEqual(local.fingerprint, before.fingerprint, 'Local edits remain visible');
  assert.match(local.diffStat, /local\.txt/);
  assert.ok(local.dirty.some(line => line.endsWith('local.txt')));
  assert.ok(local.changes.some(line => line.endsWith('local.txt')));
});
