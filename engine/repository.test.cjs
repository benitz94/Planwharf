'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { createRepositoryInspector, githubRepo, cleanError } = require('./repository.cjs');
const { createOrchestrator, packState, defaultInspect } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
const { askPrompt } = require('./runner.cjs');
const { createServer } = require('../server.cjs');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-repository-'));
const remote = path.join(dir, 'remote.git'), local = path.join(dir, 'local'), writer = path.join(dir, 'writer');
function command(args) {
  const row = spawnSync('git', ['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', ...args], { encoding: 'utf8', windowsHide: true, timeout: 10000 });
  if (row.status !== 0) throw new Error(row.stderr);
  return row.stdout.trim();
}
function commit(folder, text) {
  fs.writeFileSync(path.join(folder, 'file.txt'), text);
  command(['-C', folder, 'add', 'file.txt']); command(['-C', folder, 'commit', '-m', text]);
}
let deny = false, missingBranch = false, fetchFail = false, metaCalls = 0;
async function execute(exe, args) {
  if (exe === 'gh') {
    if (deny) return { ok: false, stdout: '', error: 'HTTP 404: Not Found (private repository access denied)' };
    if (args[1] === 'repos/owner/project') {
      metaCalls++;
      return { ok: true, stdout: JSON.stringify({ private: true, defaultBranch: 'main', permissions: { pull: true, push: true } }) };
    }
    if (missingBranch && args[1].endsWith('/feature')) return { ok: false, stdout: '', error: 'HTTP 404: Not Found' };
    return { ok: true, stdout: command(['--git-dir', remote, 'rev-parse', 'refs/heads/main']) };
  }
  assert.equal(exe, 'git');
  const converted = args.map(arg => arg === 'https://github.com/owner/project.git' ? remote : arg);
  if (args.includes('fetch')) {
    assert.ok(args.includes('--no-write-fetch-head'));
    assert.ok(args.some(arg => arg.includes('refs/planwharf/inspection/')));
    if (fetchFail) return { ok: false, stdout: '', error: 'Network offline' };
  }
  const row = spawnSync('git', converted, { encoding: 'utf8', windowsHide: true, timeout: 10000 });
  return { ok: row.status === 0, stdout: row.stdout.trim(), error: row.status ? row.stderr.trim() : null };
}
let server;
(async () => {
  assert.equal(githubRepo('https://github.com/owner/project.git'), 'owner/project');
  assert.equal(githubRepo('git@github.com:owner/project.git'), 'owner/project');
  assert.equal(githubRepo('https://evil.example/owner/project'), null);
  assert.equal(cleanError('ghp_secret https://name:secret@github.com'), '[redacted] https://[redacted]@github.com');
  command(['init', '--bare', '--initial-branch=main', remote]);
  command(['clone', remote, local]); commit(local, 'base'); command(['-C', local, 'push', 'origin', 'main']);
  command(['clone', remote, writer]);
  const inspect = createRepositoryInspector({ execute });
  const project = { id: 'demo', name: 'Demo', folder: local, github: 'https://github.com/owner/project' };
  let row = await inspect(project, { force: true });
  assert.equal(row.remote.private, true); assert.equal(row.comparison.status, 'in-sync');
  const calls = metaCalls; await inspect(project); assert.equal(metaCalls, calls, 'Cache avoids repeated network requests');
  commit(local, 'local');
  row = await inspect(project, { force: true }); assert.equal(row.comparison.ahead, 1); assert.equal(row.comparison.behind, 0);
  commit(writer, 'remote'); command(['-C', writer, 'push', 'origin', 'main']);
  row = await inspect(project, { force: true }); assert.equal(row.comparison.status, 'diverged'); assert.equal(row.comparison.behind, 1);
  command(['-C', local, 'reset', '--hard', row.comparison.mergeBase]);
  row = await inspect(project, { force: true }); assert.equal(row.comparison.status, 'behind');
  fs.writeFileSync(path.join(local, 'untracked.txt'), 'uncommitted');
  row = await inspect(project, { force: true }); assert.equal(row.local.uncommitted, 1);
  command(['-C', local, 'switch', '-c', 'feature']); missingBranch = true;
  row = await inspect(project, { force: true }); assert.equal(row.remote.localBranchPublished, false); assert.equal(row.comparison.remoteBranch, 'main');
  fetchFail = true; row = await inspect(project, { force: true }); assert.equal(row.comparison.ahead, null); assert.match(row.comparison.error, /offline/); fetchFail = false;
  deny = true; row = await inspect(project, { force: true }); assert.equal(row.remote.accessible, false); assert.equal(row.comparison.status, 'access-error'); deny = false;
  row = await inspect({ ...project, folder: path.join(dir, 'missing') }, { force: true }); assert.equal(row.local.exists, false); assert.equal(row.remote.accessible, true);
  await inspect(project, { force: true });
  let questionInfo;
  const notebook = { schema: 1, project, revisions: [{ id: 1, mode: 'manual', title: 'Spec', createdAt: new Date().toISOString(), direction: 'Inspect repository', state: 'Ready', closed: [], open: [] }], tasks: [] };
  const orchestra = createOrchestrator({ readPlan: async () => notebook, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [{ id: 'lead', label: 'Lead', kind: 'default', enabled: true, alwaysApprove: true }], readRepository: inspect,
    inspectFolder: defaultInspect, askModel: async info => { questionInfo = info; return 'Checked'; } });
  await orchestra.setOrchestrator('lead'); await orchestra.ask('How does local compare with GitHub?', 'demo');
  assert.equal(questionInfo.github, project.github); assert.equal(questionInfo.state.focus.repository.remote.private, true);
  assert.match(askPrompt(questionInfo), /GitHub repository: https:\/\/github.com\/owner\/project/);
  assert.match(askPrompt(questionInfo), /full local tool access/);
  assert.equal(packState({ projects: [{ id: 'other', folder: '/other', github: 'https://github.com/owner/other' }] }, null).others[0].folder, '/other');
  const planFile = path.join(dir, 'notebook.json'); fs.writeFileSync(planFile, JSON.stringify(notebook));
  server = createServer({planFile, models: [], fetchPlans: async () => [] });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  assert.equal((await fetch(base + '/api/repository?projectId=missing')).status, 400);
  assert.equal((await fetch(base + '/api/attempts').then(r => r.json())).attempts.length, 0, 'Inspection never dispatches jobs');
  await new Promise(resolve => server.close(resolve)); server = null;
  fs.rmSync(dir, { recursive: true, force: true });
  console.log('OK: actual git graphs, ahead/behind/diverged, private auth, missing branch, offline failure, cache, full lead briefing and safe API inspection.');
})().catch(async error => { console.error(error); if (server) await new Promise(resolve => server.close(resolve)); process.exitCode = 1; });
