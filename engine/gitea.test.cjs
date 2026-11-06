'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const Plan = require('../plan.js');
const { repositoryAddress, giteaToken, createRepositoryInspector } = require('./repository.cjs');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
const { createServer } = require('../server.cjs');
test('Gitea URLs and tokens are explicit, host scoped, and backward compatible', () => {
  assert.equal(repositoryAddress('https://github.com/owner/repo').provider, 'github');
  const address = repositoryAddress('https://forge.example/git/owner/repo.git');
  assert.equal(address.baseUrl, 'https://forge.example/git');
  assert.equal(address.repository, 'owner/repo');
  for (const url of ['https://user:pass@forge.example/a/b', 'http://forge.example/a/b', 'https://forge.example/a/b?token=secret']) assert.equal(repositoryAddress(url), null);
  assert.equal(giteaToken(address, { PLANWHARF_GITEA_TOKEN: 'secret' }), null);
  assert.equal(giteaToken(address, { PLANWHARF_GITEA_TOKEN_FORGE_EXAMPLE: 'host-token' }), 'host-token');
  assert.equal(Plan.isGithubUrl(address.url), true);
  assert.equal(Plan.isGithubUrl('https://user:secret@forge.example/owner/repo'), false);
});
test('Gitea inspection uses real isolated git history without changing source, and contains API drift', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-gitea-'));
  const local = path.join(dir, 'local'), remote = path.join(dir, 'remote.git');
  function git(args) {
    const row = spawnSync('git', ['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid', ...args], { encoding: 'utf8', windowsHide: true });
    assert.equal(row.status, 0, row.stderr); return row.stdout.trim();
  }
  let drift = false, denied = false;
  const apiCalls = [];
  try {
    git(['init', '--initial-branch=main', local]); fs.writeFileSync(path.join(local, 'source.txt'), 'preserve'); git(['-C', local, 'add', '.']); git(['-C', local, 'commit', '-m', 'Fixture']);
    git(['clone', '--bare', local, remote]);
    fs.writeFileSync(path.join(local, 'personal.txt'), 'Do not touch');
    const before = git(['-C', local, 'rev-parse', 'HEAD']);
    const inspector = createRepositoryInspector({ env: { PLANWHARF_GITEA_TOKEN_FORGE_EXAMPLE: 'secret-fixture' },
      fetchRemote: async (url, options) => {
        assert.equal(options.redirect, 'error'); assert.equal(options.headers.Authorization, 'token secret-fixture'); apiCalls.push(url);
        return { ok: !denied, status: denied ? 401 : 200, json: async () => drift ? { changed_schema: true } : url.includes('/branches/') ? { commit: { id: before } } : { default_branch: 'main', private: true } };
      },
      execute: async (exe, args, options) => {
        assert.equal(exe, 'git'); assert.ok(!args.includes('merge')); assert.ok(!args.includes('pull')); assert.ok(!args.includes('reset'));
        if (args.includes('fetch')) { assert.ok(args.includes('--no-write-fetch-head')); assert.ok(args.includes('http.followRedirects=false')); assert.equal(options.env.GIT_CONFIG_VALUE_0, 'Authorization: token secret-fixture'); assert.ok(!args.join(' ').includes('secret-fixture')); }
        const row = spawnSync(exe, args.map(arg => arg === 'https://forge.example/owner/repo.git' ? remote : arg), { encoding: 'utf8', windowsHide: true });
        return { ok: row.status === 0, stdout: row.stdout.trim(), error: row.stderr.trim() };
      } });
    const project = { folder: local, github: 'https://forge.example/owner/repo' };
    const info = await inspector(project);
    assert.equal(info.remote.provider, 'gitea'); assert.equal(info.remote.private, true); assert.equal(info.comparison.status, 'in-sync'); assert.equal(info.local.uncommitted, 1);
    assert.equal(git(['-C', local, 'rev-parse', 'HEAD']), before); assert.equal(fs.readFileSync(path.join(local, 'personal.txt'), 'utf8'), 'Do not touch');
    const calls = apiCalls.length; await inspector(project); assert.equal(apiCalls.length, calls);
    drift = true; const bad = await inspector(project, { force: true }); assert.equal(bad.comparison.ahead, null); assert.match(bad.remote.error, /unreadable repository metadata/);
    denied = true; const failure = await inspector(project, { force: true }); assert.equal(failure.comparison.status, 'access-error'); assert.ok(!JSON.stringify(failure).includes('secret-fixture'));
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('periodic repository metadata checks do not block scheduling or dispatch projects', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-repo-poll-'));
  const plan = Plan.example(); plan.project.github = 'https://forge.example/owner/repo'; plan.tasks = [];
  let finish, checks = 0;
  const orchestra = createOrchestrator({ readPlan: async () => plan, models: [], attempts: createAttemptEngine({ dir }),
    readRepository: project => { checks++; return new Promise(resolve => { finish = () => resolve({ checkedAt: new Date().toISOString(), github: project.github, folder: null, comparison: { status: 'behind', behind: 2 } }); }); },
    spawnWorker: () => { throw Error('Inspection cannot launch workers'); } });
  try {
    await orchestra.tick(); await orchestra.tick(); assert.equal(checks, 1, 'A pending refresh is not duplicated');
    finish(); await new Promise(resolve => setImmediate(resolve));
    const snapshot = await orchestra.tick(); assert.equal(snapshot.projects[0].repository.comparison.behind, 2); assert.equal(checks, 1, 'Recent inspection is retained');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('an idle backend checks linked repositories with no open UI or running jobs', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-idle-repository-'));
  const file = path.join(dir, 'notebook.json');
  const plan = Plan.example(); plan.project.github = 'https://forge.example/owner/repo'; plan.tasks = [];
  const original = JSON.stringify(plan); fs.writeFileSync(file, original);
  let checked, timer, calls = 0;
  const observed = new Promise(resolve => checked = resolve);
  const server = createServer({ planFile: file, attemptsDir: path.join(dir, 'attempts'), models: [],refreshModels: false, monitorIntervalMs: 20,
    readRepository: async project => { calls++; const result = { checkedAt: new Date().toISOString(), folder: null, github: project.github, comparison: { status: 'behind', behind: 1 } }; checked(); return result; },
    spawnWorker: () => { throw Error('Repository monitoring cannot dispatch'); }, fetchPlans: async () => [] });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    await Promise.race([observed, new Promise((_, reject) => { timer = setTimeout(() => reject(Error('Idle repository check did not run')), 3000); })]);
    await new Promise(resolve => setImmediate(resolve));
    const view = await fetch('http://127.0.0.1:' + server.address().port + '/api/orchestrator?readOnly=1').then(r => r.json());
    assert.equal(view.projects[0].repository.comparison.behind, 1); assert.equal(calls, 1);
    assert.equal(fs.readFileSync(file, 'utf8'), original);
  } finally { clearTimeout(timer); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
