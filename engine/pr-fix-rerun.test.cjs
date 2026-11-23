'use strict';
// A check GitHub cancelled (no free machine) is not a fault in the code: Fix in the background runs it
// again and calls no model; a red check a model left as it was is not reported as fixed.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const pr = require('./pr-check.cjs');

test('cancelled checks are told apart from failed ones, with their run', () => {
  const rollup = [
    { name: 'build (macos-latest, osx-x64)', conclusion: 'CANCELLED', detailsUrl: 'https://github.com/o/r/actions/runs/45276104247/job/138048197786' },
    { name: 'offline', conclusion: 'FAILURE', detailsUrl: 'https://github.com/o/r/actions/runs/1/job/2' },
    { name: 'build (ubuntu)', conclusion: 'SUCCESS' }];
  assert.deepEqual(pr.failingNames(rollup), ['offline']);
  assert.deepEqual(pr.notRunChecks(rollup), [{ name: 'build (macos-latest, osx-x64)', runId: '45276104247' }]);
});

async function harness(t, fake) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-fix-rerun-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'project'); fs.mkdirSync(folder);
  spawnSync('git', ['init', '-q', '-b', 'main'], { cwd: folder });
  spawnSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '--allow-empty', '-m', 'start'], { cwd: folder });
  const real = { prepareFix: pr.prepareFix, finishFix: pr.finishFix, removeWorktree: pr.removeWorktree, rerunNotRun: pr.rerunNotRun };
  t.after(() => Object.assign(pr, real));
  const calls = { rerun: [], spawned: [] };
  pr.prepareFix = async (folderArg, number, worktree) => {
    fs.mkdirSync(path.dirname(worktree), { recursive: true });
    spawnSync('git', ['worktree', 'add', '--detach', worktree, 'HEAD'], { cwd: folderArg });
    return { ok: true, branch: 'feat/x', base: 'main', before: 'x', conflicts: [], failing: fake.failing, notRun: fake.notRun };
  };
  pr.finishFix = async () => ({ ok: true, changed: false, reason: 'Nothing needed changing.' });
  pr.removeWorktree = async () => {};
  pr.rerunNotRun = async (folderArg, notRun) => { calls.rerun.push(notRun.map(check => check.runId)); return { asked: 1, rerun: 1 }; };
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p', name: 'p', folder }, revisions: [{ id: 1, direction: 'Ship', open: [] }], tasks: [] }] };
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: async () => [], plansTtl: 0,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [{ id: 'astra', label: 'GPT-6 Astra', provider: 'codex', cli: 'gpt-6-astra', plan: 'codex', enabled: true, available: true, cost: 1, limit: 100, providerCatalog: true, kind: 'premium', policy: { alwaysApprove: true }, capabilities: { canCode: true } }],
    spawnWorker: info => {
      calls.spawned.push(info.worker.id);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'looked\n');
      fs.mkdirSync(path.dirname(info.exitPath), { recursive: true }); fs.writeFileSync(info.exitPath, JSON.stringify({ exitCode: 0 }));
      return { pid: 77 };
    }, isProcessAlive: () => false });
  assert.equal((await orchestra.setModelRoles('astra', { worker: true, verifier: true })).ok, true);
  assert.equal((await orchestra.fixPullRequest('p', 218)).ok, true);
  let end;
  for (let i = 0; i < 100 && !end; i++) {
    await new Promise(resolve => setTimeout(resolve, 50));
    end = (await orchestra.snapshot()).execution.timeline.find(event => /PR_FIX_(COMPLETED|FAILED)/.test(event.type));
  }
  return { end, calls };
}

test('a check GitHub cancelled runs again, and no model is called', async t => {
  const { end, calls } = await harness(t, { failing: [], notRun: [{ name: 'build (macos-latest, osx-x64)', runId: '45276104247' }] });
  assert.deepEqual(calls.rerun, [['45276104247']]);
  assert.deepEqual(calls.spawned, [], 'no model for a check that only needs to run again');
  assert.equal(end.type, 'PR_FIX_COMPLETED');
  assert.match(end.summary, /Ran again the check GitHub had cancelled .*build \(macos-latest, osx-x64\)\. Nothing in the code needed changing\./);
});

test('a red check the model left as it was is not reported as fixed', async t => {
  const { end, calls } = await harness(t, { failing: ['offline'], notRun: [] });
  assert.deepEqual(calls.spawned, ['astra']);
  assert.equal(end.type, 'PR_FIX_FAILED');
  assert.equal(end.summary, 'Pull request #218 was not fixed: GPT-6 Astra changed nothing, and offline still fails.');
});
