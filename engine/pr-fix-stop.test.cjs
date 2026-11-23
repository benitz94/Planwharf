'use strict';
// The owner can stop a fix in the background: the model is ended, nothing is checked or pushed.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

test('Stop the fix ends the model and pushes nothing', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-fix-stop-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'project'); fs.mkdirSync(folder);
  const git = (...args) => { const out = spawnSync('git', args, { cwd: folder, encoding: 'utf8' }); if (out.status !== 0) throw new Error(out.stderr); return out.stdout; };
  git('init', '-q', '-b', 'main'); git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '--allow-empty', '-m', 'start');
  const pr = require('./pr-check.cjs');
  const real = { prepareFix: pr.prepareFix, finishFix: pr.finishFix, removeWorktree: pr.removeWorktree };
  t.after(() => Object.assign(pr, real));
  let finished = 0;
  pr.prepareFix = async (folderArg, number, worktree) => {
    fs.mkdirSync(path.dirname(worktree), { recursive: true });
    spawnSync('git', ['worktree', 'add', '--detach', worktree, 'HEAD'], { cwd: folderArg });
    return { ok: true, branch: 'feat/x', base: 'main', before: 'x', conflicts: ['notes.md'], failing: [] };
  };
  pr.finishFix = async () => { finished += 1; return { ok: true, changed: true, reason: 'Pushed.' }; };
  pr.removeWorktree = async () => {};
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p', name: 'p', folder }, revisions: [{ id: 1, direction: 'Ship', open: [] }], tasks: [] }] };
  const alive = new Set(); const killed = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: async () => [], plansTtl: 0,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [{ id: 'astra', label: 'GPT-6 Astra', provider: 'codex', cli: 'gpt-6-astra', plan: 'codex', enabled: true, available: true, cost: 1, limit: 100, providerCatalog: true, kind: 'premium', policy: { alwaysApprove: true }, capabilities: { canCode: true } }],
    // The model works forever: no exit file.
    spawnWorker: info => { fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'working\n'); alive.add(4242); return { pid: 4242 }; },
    isProcessAlive: pid => alive.has(pid), killWorker: pid => { killed.push(pid); alive.delete(pid); return true; } });
  assert.equal((await orchestra.setModelRoles('astra', { worker: true, verifier: true })).ok, true);
  assert.equal((await orchestra.fixPullRequest('p', 7)).ok, true);
  for (let i = 0; i < 60 && !(await orchestra.pullRequestContext('p')).fixing.some(item => item.model); i++) await new Promise(resolve => setTimeout(resolve, 50));
  assert.equal(orchestra.stopPullRequestFix('p', 99).ok, false, 'no fix of another pull request');
  assert.equal(orchestra.stopPullRequestFix('p', 7).ok, true);
  assert.deepEqual(killed, [4242], 'the model is ended');
  let events = [];
  for (let i = 0; i < 100 && !events.some(event => /PR_FIX_(COMPLETED|FAILED)/.test(event.type)); i++) {
    await new Promise(resolve => setTimeout(resolve, 100));
    events = (await orchestra.snapshot()).execution.timeline.filter(event => event.projectId === 'p');
  }
  const end = events.find(event => /PR_FIX_(COMPLETED|FAILED)/.test(event.type));
  assert.equal(end.type, 'PR_FIX_FAILED');
  assert.equal(end.summary, 'Pull request #7: you stopped the fix. Nothing was pushed.');
  assert.equal(finished, 0, 'nothing checked or pushed');
  assert.deepEqual((await orchestra.pullRequestContext('p')).fixing, [], 'the fix is gone from the panel');
});
