'use strict';
// A model that refuses the background fix (usage credits) did nothing: it is marked, and the next model
// takes the same copy of the folder.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

test('the background fix moves past a model that needs usage credits', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-fix-refusal-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'project'); fs.mkdirSync(folder);
  const git = (...args) => { const out = spawnSync('git', args, { cwd: folder, encoding: 'utf8' }); if (out.status !== 0) throw new Error(out.stderr); return out.stdout; };
  git('init', '-q', '-b', 'main'); git('-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-q', '--allow-empty', '-m', 'start');
  // The git side is checked by pr-fix.test.cjs: here only the choice of model is under test.
  const pr = require('./pr-check.cjs');
  const real = { prepareFix: pr.prepareFix, finishFix: pr.finishFix, removeWorktree: pr.removeWorktree };
  t.after(() => Object.assign(pr, real));
  pr.prepareFix = async (folderArg, number, worktree) => {
    fs.mkdirSync(path.dirname(worktree), { recursive: true });
    spawnSync('git', ['worktree', 'add', '--detach', worktree, 'HEAD'], { cwd: folderArg });
    return { ok: true, branch: 'feat/x', base: 'main', before: 'x', conflicts: ['notes.md'], failing: [] };
  };
  pr.finishFix = async () => ({ ok: true, changed: true, reason: 'Pushed to feat/x.' });
  pr.removeWorktree = async () => {};
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const model = (id, label, provider, cli) => ({ id, label, provider, cli, plan: provider, enabled: true, available: true, cost: 1, limit: 100, providerCatalog: true, kind: 'premium', policy: { alwaysApprove: true }, capabilities: { canCode: true } });
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p', name: 'p', folder }, revisions: [{ id: 1, direction: 'Ship', open: [] }], tasks: [] }] };
  const launched = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: async () => [], plansTtl: 0,
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [model('fable', 'Fable 5.2', 'claude', 'claude-fable-5-2'), model('astra', 'GPT-6 Astra', 'codex', 'gpt-6-astra')],
    spawnWorker: info => {
      launched.push(info.worker.id);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
      fs.writeFileSync(info.logPath, info.worker.id === 'fable' ? 'claude CLI failed (provider): Fable 5.2 requires usage credits. Switch to another model, or manage usage credits at https://claude.ai/settings/usage, to continue.\n' : 'resolved\n');
      fs.mkdirSync(path.dirname(info.exitPath), { recursive: true });
      fs.writeFileSync(info.exitPath, JSON.stringify({ exitCode: info.worker.id === 'fable' ? 1 : 0 }));
      return { pid: 4000 + launched.length };
    }, isProcessAlive: () => false });
  for (const id of ['fable', 'astra']) assert.equal((await orchestra.setModelRoles(id, { worker: true, verifier: true })).ok, true);
  assert.equal((await orchestra.fixPullRequest('p', 196)).ok, true);
  let events = [];
  for (let i = 0; i < 100 && !events.some(event => /PR_FIX_(COMPLETED|FAILED)/.test(event.type)); i++) {
    await new Promise(resolve => setTimeout(resolve, 50));
    events = (await orchestra.snapshot()).execution.timeline.filter(event => event.projectId === 'p');
  }
  assert.deepEqual(launched, ['fable', 'astra'], 'Fable is tried first as the strongest, then Astra');
  const end = events.find(event => /PR_FIX_(COMPLETED|FAILED)/.test(event.type));
  assert.equal(end.type, 'PR_FIX_COMPLETED', end.summary);
  assert.match(end.summary, /GPT-6 Astra fixed pull request #196/);
  assert.ok(events.some(event => /Fable 5\.2 refused the work/.test(event.summary)));
  const fable = (await orchestra.tick()).models.find(item => item.id === 'fable');
  assert.equal(fable.creditsNeeded, true, 'and it is left out for a day');
});
