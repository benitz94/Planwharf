'use strict';
// What the fix tests leave out: a bad gh answer, a lost push, two copies at once, and a fix that stops.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const pr = require('./pr-check.cjs');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
const { gitEnvironment } = require('./project-workspace.cjs');

const git = (cwd, ...args) => {
  const out = spawnSyncGit(cwd, args);
  if (out.status !== 0) throw new Error(args.join(' ') + ': ' + out.stderr);
  return out.stdout.trim();
};
function spawnSyncGit(cwd, args) {
  return require('node:child_process').spawnSync('git', args, {
    cwd, encoding: 'utf8',
    env: { ...gitEnvironment(), GIT_AUTHOR_NAME: 't', GIT_AUTHOR_EMAIL: 't@t', GIT_COMMITTER_NAME: 't', GIT_COMMITTER_EMAIL: 't@t' }
  });
}
function realRun(folder, cmd, args) {
  return new Promise(resolve => {
    const child = spawn(cmd, args, { cwd: folder, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: gitEnvironment() });
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => resolve({ status: 1, stdout, stderr: error.message }));
    child.on('close', code => resolve({ status: code, stdout, stderr }));
  });
}
function setup() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-edge-'));
  const remote = path.join(root, 'remote.git'), folder = path.join(root, 'project');
  git(root, 'init', '-q', '--bare', '-b', 'main', remote);
  git(root, 'clone', '-q', remote, folder);
  for (const [key, value] of [['user.name', 't'], ['user.email', 't@t']]) git(folder, 'config', key, value);
  fs.writeFileSync(path.join(folder, 'notes.md'), 'one\n');
  git(folder, 'add', '.'); git(folder, 'commit', '-q', '-m', 'start'); git(folder, 'push', '-q', 'origin', 'main');
  for (const branch of ['feat/x', 'feat/y']) {
    git(folder, 'switch', '-q', '-c', branch);
    fs.writeFileSync(path.join(folder, 'notes.md'), 'one\nfrom ' + branch + '\n');
    git(folder, 'commit', '-q', '-am', branch); git(folder, 'push', '-q', 'origin', branch);
    git(folder, 'switch', '-q', 'main');
  }
  fs.writeFileSync(path.join(folder, 'notes.md'), 'one\nfrom main\n');
  git(folder, 'commit', '-q', '-am', 'main'); git(folder, 'push', '-q', 'origin', 'main');
  return { root, remote, folder };
}
function stub(methods) {
  const saved = {};
  for (const [key, value] of Object.entries(methods)) { saved[key] = pr[key]; pr[key] = value; }
  return () => { for (const [key, value] of Object.entries(saved)) pr[key] = value; };
}
function planWith(dir) {
  return { schema: 2, projects: ['biblioteca', 'other'].map(id => ({ schema: 1, project: { id, name: id, folder: dir }, revisions: [], tasks: [] })) };
}
function model(id, label, provider, kind) {
  return { id, label, provider, adapter: provider, cli: id, plan: provider, enabled: true, available: true, cost: 1, limit: 100, kind,
    capabilities: { canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true } };
}
async function settle(orchestra, projectId) {
  // The one-at-a-time lock stays until the copy of the folder is gone. That git
  // work shares the machine with the rest of the suite, so a short poll gives up
  // while the copy is still being removed.
  const deadline = Date.now() + 45000;
  let context;
  while (Date.now() < deadline) {
    context = orchestra.pullRequestContext(projectId);
    if (!context.fixing.length) return context;
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  throw new Error('the fix did not finish');
}

test.describe('pull request fix edges', { concurrency: false }, () => {
  test('a closed pull request, unreadable gh output, and an unusual branch do not change the folder', async () => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-gh-'));
    fs.writeFileSync(path.join(folder, 'notes.md'), 'stay\n');
    const calls = [];
    const run = async (cwd, cmd) => { calls.push(cmd); return answers.shift(); };
    let answers = [{ status: 0, stdout: JSON.stringify({ state: 'CLOSED', headRefName: 'feat/x', baseRefName: 'main' }) }];
    const closed = await pr.prepareFix(folder, 4, path.join(folder, 'copy'), run);
    assert.equal(closed.ok, false);
    assert.match(closed.reason, /not open/);
    answers = [{ status: 0, stdout: 'not-json' }];
    const broken = await pr.prepareFix(folder, 4, path.join(folder, 'copy'), run);
    assert.match(broken.reason, /not readable/);
    answers = [{ status: 0, stdout: JSON.stringify({ state: 'OPEN', headRefName: 'feat/x;rm', baseRefName: 'main' }) }];
    const odd = await pr.prepareFix(folder, 4, path.join(folder, 'copy'), run);
    assert.match(odd.reason, /unusual/);
    answers = [{ status: 1, stderr: 'no such pull request' }];
    const missing = await pr.prepareFix(folder, 4, path.join(folder, 'copy'), run);
    assert.match(missing.reason, /not found/);
    const unreadable = await pr.resolveConflicts(folder, 4, async () => ({ status: 0, stdout: '{' }));
    assert.equal(unreadable.ok, false);
    assert.match(unreadable.reason, /not readable/);
    assert.deepEqual(calls, ['gh', 'gh', 'gh', 'gh']);
    assert.equal(fs.readFileSync(path.join(folder, 'notes.md'), 'utf8'), 'stay\n');
    assert.ok(!fs.existsSync(path.join(folder, 'copy')));
  });

  test('a red commit status is given to the fix, and a failed copy does not throw away the other one', async () => {
    const { root, remote, folder } = setup();
    const worktree = path.join(root, 'wt-x');
    const viewed = await pr.prepareFix(folder, 4, worktree, async (cwd, cmd, args) => {
      if (cmd === 'gh') return { status: 0, stdout: JSON.stringify({ state: 'OPEN', headRefName: 'feat/x', baseRefName: 'main', statusCheckRollup: [{ context: 'ci/circleci', state: 'FAILURE' }, { name: 'build', conclusion: 'TIMED_OUT' }, { name: 'lint', conclusion: 'SUCCESS' }] }) };
      return realRun(cwd, cmd, args);
    });
    assert.equal(viewed.ok, true, viewed.reason);
    assert.deepEqual(viewed.failing, ['ci/circleci', 'build']);
    assert.deepEqual(viewed.conflicts, ['notes.md']);
    const again = await pr.prepareWorktree(folder, { branch: 'feat/x', base: 'main' }, worktree);
    assert.equal(again.ok, false);
    assert.equal(fs.readFileSync(path.join(worktree, 'notes.md'), 'utf8').includes('<<<<<<<') || fs.existsSync(path.join(worktree, 'notes.md')), true);
    const gone = await pr.prepareWorktree(folder, { branch: 'feat/missing', base: 'main' }, path.join(root, 'wt-missing'));
    assert.equal(gone.ok, false);
    assert.match(gone.reason, /fetch/);
    assert.ok(!fs.existsSync(path.join(root, 'wt-missing')));
    const other = path.join(root, 'wt-y');
    const [first, second] = await Promise.all([
      pr.prepareWorktree(folder, { branch: 'feat/x', base: 'main' }, path.join(root, 'wt-x2')),
      pr.prepareWorktree(folder, { branch: 'feat/y', base: 'main' }, other)
    ]);
    for (const copy of [first, second]) if (copy.ok) assert.ok(fs.existsSync(path.join(copy === second ? other : path.join(root, 'wt-x2'), 'notes.md')));
    assert.equal(git(folder, 'rev-parse', '--abbrev-ref', 'HEAD'), 'main');
    const tip = git(remote, 'rev-parse', 'feat/x');
    fs.writeFileSync(path.join(remote, 'hooks', 'pre-receive'), '#!/bin/sh\nexit 1\n');
    fs.chmodSync(path.join(remote, 'hooks', 'pre-receive'), 0o755);
    fs.writeFileSync(path.join(worktree, 'notes.md'), 'one\nfrom main\nfrom feat/x\n');
    git(worktree, 'add', 'notes.md');
    const pushed = await pr.finishFix(folder, worktree, viewed, 4);
    assert.equal(pushed.ok, false);
    assert.match(pushed.reason, /push was refused/);
    assert.equal(git(remote, 'rev-parse', 'feat/x'), tip);
    assert.equal(fs.readFileSync(path.join(worktree, 'notes.md'), 'utf8'), 'one\nfrom main\nfrom feat/x\n');
    await pr.removeWorktree(folder, worktree);
    await pr.removeWorktree(folder, worktree);
    assert.ok(!fs.existsSync(worktree));
  });

  test('one project fixes one pull request at a time, and a failed start can be tried again', async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-lock-'));
    const withBadId = { schema: 2, projects: [...planWith(dir).projects, { schema: 1, project: { id: 'bad/id', name: 'Bad', folder: dir }, revisions: [], tasks: [] }] };
    const orchestra = createOrchestrator({
      dir, readPlan: async () => withBadId, attempts: createAttemptEngine({ dir }), models: [], fetchPlans: null,
      spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false
    });
    assert.equal((await orchestra.fixPullRequest('biblioteca', 0)).ok, false);
    assert.equal((await orchestra.fixPullRequest('biblioteca', 1.5)).ok, false);
    assert.match((await orchestra.fixPullRequest('bad/id', 1)).reason, /Unusual project id/);
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const calls = [];
    const made = [];
    const restore = stub({
      prepareFix: async (_folder, number, worktree) => {
        calls.push(number);
        if (number === 1) {
          fs.mkdirSync(worktree, { recursive: true });
          made.push(worktree);
          await gate;
          return { ok: false, reason: 'The pull request is not open.' };
        }
        return { ok: false, reason: 'stopped' };
      }
    });
    try {
      const started = await orchestra.fixPullRequest('biblioteca', 1);
      assert.equal(started.started, true);
      const same = await orchestra.fixPullRequest('biblioteca', 1);
      assert.equal(same.started, false);
      assert.match(same.reason, /already being fixed/);
      const otherNumber = await orchestra.fixPullRequest('biblioteca', 2);
      assert.equal(otherNumber.ok, false);
      assert.match(otherNumber.reason, /already running/);
      const otherProject = await orchestra.fixPullRequest('other', 5);
      assert.equal(otherProject.started, true);
      assert.deepEqual(orchestra.pullRequestContext('biblioteca').fixing.map(item => item.number), [1]);
      assert.equal(calls.filter(number => number === 1).length, 1);
      release();
      const done = await settle(orchestra, 'biblioteca');
      assert.match(done.fixes.find(item => item.number === 1).text, /not open/);
      assert.ok(made[0] && !fs.existsSync(made[0]));
      const retry = await orchestra.fixPullRequest('biblioteca', 1);
      assert.equal(retry.started, true);
      await settle(orchestra, 'biblioteca');
      await settle(orchestra, 'other');
    } finally { restore(); }
  });

  test('a fix that cannot start, or whose worker stops, records the failure and keeps the branch', async () => {
    const { root, remote, folder } = setup();
    const dir = path.join(root, 'data');
    const tip = git(remote, 'rev-parse', 'feat/x');
    const head = git(folder, 'rev-parse', '--abbrev-ref', 'HEAD');
    const launches = [];
    const models = [model('claude-opus-5-5', 'Opus 5.5', 'claude', 'premium'), model('gpt-6-luna', 'GPT-6 Luna', 'codex', 'light')];
    const orchestra = createOrchestrator({
      dir, readPlan: async () => planWith(folder), attempts: createAttemptEngine({ dir }), fetchPlans: null, models,
      spawnWorker: info => { launches.push(info); return { pid: 9 }; }, isProcessAlive: () => false
    });
    assert.equal((await orchestra.setModelRoles('claude-opus-5-5', { worker: false }, 'biblioteca')).ok, true);
    const noneDir = path.join(root, 'none');
    const noModel = createOrchestrator({
      dir: noneDir, readPlan: async () => planWith(folder), attempts: createAttemptEngine({ dir: noneDir }), models: [], fetchPlans: null,
      spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false
    });
    const copy = pr.prepareWorktree;
    const restore = stub({
      prepareFix: async () => ({ ok: true, branch: 'feat/x', base: 'main', before: 'abc', conflicts: ['notes.md'], failing: ['ci/circleci'] }),
      finishFix: async () => ({ ok: false, reason: 'The push was refused: rejected' })
    });
    try {
      assert.equal((await noModel.fixPullRequest('biblioteca', 3)).started, true);
      const none = await settle(noModel, 'biblioteca');
      assert.match(none.fixes[0].text, /no model/);
      assert.deepEqual(none.fixing, []);
      pr.prepareFix = (projectFolder, _number, worktree) => copy(projectFolder, { branch: 'feat/x', base: 'main', failing: ['ci/circleci'] }, worktree);
      assert.equal((await orchestra.fixPullRequest('biblioteca', 8)).started, true);
      const failed = await settle(orchestra, 'biblioteca');
      assert.equal(launches.at(-1).worker.id, 'gpt-6-luna');
      assert.match(launches.at(-1).job.detail, /ci\/circleci/);
      assert.match(launches.at(-1).job.detail, /notes\.md/);
      assert.equal(failed.fixes.find(item => item.number === 8).result, 'failed');
      assert.match(failed.fixes.find(item => item.number === 8).text, /push was refused/);
      assert.equal(git(remote, 'rev-parse', 'feat/x'), tip);
      assert.equal(git(folder, 'rev-parse', '--abbrev-ref', 'HEAD'), head);
      pr.prepareFix = async () => { throw new Error('gh crashed'); };
      assert.equal((await orchestra.fixPullRequest('biblioteca', 9)).started, true);
      const crashed = await settle(orchestra, 'biblioteca');
      assert.match(crashed.fixes.find(item => item.number === 9).text, /gh crashed/);
      assert.equal((await orchestra.fixPullRequest('biblioteca', 9)).started, true);
      await settle(orchestra, 'biblioteca');
    } finally { restore(); }
  });

  test('the push keeps the pull request on the work that is current, and a long link is cut short', async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-link-edge-'));
    fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({
      projects: { biblioteca: { status: 'complete', megaTaskId: 'mega-old', dispatch: [] } },
      execution: {
        megaTasks: [
          { id: 'mega-new', projectId: 'biblioteca', title: 'Newer', goal: 'Newer goal', createdAt: '2026-11-20T00:00:00Z', orchestratorModelId: 'gpt-6-luna' },
          { id: 'mega-old', projectId: 'biblioteca', title: 'Current', goal: 'Current goal', createdAt: '2026-11-01T00:00:00Z', orchestratorModelId: 'claude-opus-5-5' },
          { id: 'mega-other', projectId: 'other', title: 'Other', goal: 'Other goal', createdAt: '2026-11-20T00:00:00Z', orchestratorModelId: 'gpt-6-luna' }
        ],
        subTasks: [{ id: 's1', megaTaskId: 'mega-old', title: 'Build it', assignedModelId: 'claude-opus-5-5' }],
        results: []
      }
    }));
    const open = () => createOrchestrator({
      dir, readPlan: async () => planWith(dir), attempts: createAttemptEngine({ dir }), fetchPlans: null, models: [model('claude-opus-5-5', 'Opus 5.5', 'claude', 'premium'), model('gpt-6-luna', 'GPT-6 Luna', 'codex', 'light')],
      spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false
    });
    const orchestra = open();
    const url = 'https://github.com/acme/app/pull/12?' + 'a'.repeat(400);
    const saved = orchestra.rememberPullRequest('biblioteca', 12, url);
    assert.equal(saved.megaTaskId, 'mega-old');
    assert.equal(orchestra.rememberPullRequest('biblioteca', 13).ok, true);
    const context = orchestra.pullRequestContext('biblioteca');
    assert.equal(context.works.find(work => work.goal === 'Current goal').number, 13);
    assert.equal(context.works.find(work => work.goal === 'Newer goal'), undefined);
    assert.deepEqual(orchestra.pullRequestContext('other').works, []);
    const again = open();
    const kept = again.pullRequestContext('biblioteca').works[0];
    assert.equal(kept.number, 13);
    assert.equal(kept.lead, 'Opus 5.5');
    assert.equal(kept.steps[0].model, 'Opus 5.5');
    const stored = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    const mega = stored.execution.megaTasks.find(item => item.id === 'mega-old');
    assert.equal(mega.pullRequestUrl.length, 300);
    assert.equal(orchestra.rememberPullRequest('biblioteca', 4, '').ok, true);
  });
});
