'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { LIMITS, createSettings, pushGuardEnvironment, nextStep, parseDecision, nudgePrompt } = require('./autonomy.cjs');

const on = (extra = {}) => ({ ...createSettings('s1', '2026-11-03T10:00:00Z'), started: true, ...extra });
const step = (settings, row, extra = {}) => nextStep({ mode: 'auto', sessionId: 's1', settings, row, ...extra });

test('authority needs the setting, Automatic mode and the session that enabled it', () => {
  const row = { status: 'needs_approval', megaTaskId: 'm1' };
  assert.equal(step(undefined, row).type, 'none');
  assert.equal(step({ ...on(), enabled: false }, row).type, 'none');
  assert.equal(nextStep({ mode: 'manual', sessionId: 's1', settings: on(), row }).type, 'none');
  assert.equal(nextStep({ mode: 'auto', sessionId: 'restarted', settings: on(), row }).type, 'none');
  assert.deepEqual(step(on(), row), { type: 'approve', megaTaskId: 'm1' });
});

test('a user pause or stop is never overridden, even with an active alert', () => {
  const alerts = [{ key: 'a', title: 't', message: 'm' }];
  for (const row of [{ status: 'checkpointed', megaTaskId: 'm1' }, { status: 'watching', megaTaskId: 'm1', megaTask: { pauseRequested: true } }]) assert.equal(step(on(), row, { alerts }).type, 'none');
});

test('the MegaTask cap stops approval and reports the limit', () => {
  const approved = Array.from({ length: LIMITS.maxMegaTasks }, (_, i) => 'old' + i);
  assert.deepEqual(step(on({ approved }), { status: 'needs_approval', megaTaskId: 'm9' }), { type: 'limit', reason: 'MEGATASK_LIMIT' });
});

test('a completed MegaTask is evaluated once', () => {
  const row = { status: 'complete', megaTaskId: 'm1' };
  assert.equal(step(on(), row).type, 'evaluate');
  assert.equal(step(on({ evaluated: ['m1'] }), row).type, 'none');
});

test('each alert nudges once, ignored alerts never, and the hourly cap holds', () => {
  const row = { status: 'watching', megaTaskId: 'm1' }, now = Date.parse('2026-11-03T12:00:00Z');
  const alert = { key: 'quiet', title: 't', message: 'm' };
  assert.equal(step(on(), row, { alerts: [alert], now }).type, 'nudge');
  assert.equal(step(on({ nudgedKeys: ['quiet'] }), row, { alerts: [alert], now }).type, 'none');
  assert.equal(step(on(), row, { alerts: [{ ...alert, ignored: true }], now }).type, 'none');
  const nudgeTimes = Array(LIMITS.maxNudgesPerHour).fill('2026-11-03T11:30:00Z');
  assert.equal(step(on({ nudgeTimes }), row, { alerts: [alert], now }).reason, 'nudge-limit');
  assert.equal(step(on({ nudgeTimes: Array(LIMITS.maxNudgesPerHour).fill('2026-11-03T09:00:00Z') }), row, { alerts: [alert], now }).type, 'nudge');
});

test('a nudge is written from detected facts', () => {
  const text = nudgePrompt({ name: 'Demo', goal: 'Ship', alert: { title: 'No recent progress', message: 'Inspect it.' } });
  assert.match(text, /No recent progress/); assert.match(text, /Goal: Ship/); assert.match(text, /never push/);
});

test('lead replies only yield done, an objective or assumptions; anything else is rejected', () => {
  assert.deepEqual(parseDecision('{"done":true,"next":"ignored","assumptions":["a"]}'), { done: true, next: null, assumptions: ['a'] });
  assert.deepEqual(parseDecision('Sure:\n{"done":false,"next":"Add tests"}'), { done: false, next: 'Add tests', assumptions: [] });
  assert.deepEqual(parseDecision('{"done":false,"next":null}'), { done: false, next: null, assumptions: [] });
  for (const bad of ['', 'all good', '{"done":"yes"}', '{broken', '{"done":false,"next":7}', null]) assert.equal(parseDecision(bad), null);
  assert.equal(Object.keys(parseDecision('{"done":false,"next":"x","approve":true,"push":true}')).includes('approve'), false);
});

test('the guard stops real pushes for URL and local remotes and keeps prior Git config', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-push-guard-'));
  try {
    const repo = path.join(dir, 'repo'), bare = path.join(dir, 'bare.git');
    const git = (args, env = {}) => spawnSync('git', args, { cwd: repo, env: { ...process.env, ...env }, encoding: 'utf8' });
    fs.mkdirSync(repo); git(['init', '-q']); git(['init', '-q', '--bare', bare]);
    git(['-c', 'user.email=t@e', '-c', 'user.name=t', 'commit', '-q', '--allow-empty', '-m', 'x']);
    git(['remote', 'add', 'local', bare]); git(['remote', 'add', 'web', 'https://example.invalid/x.git']);
    const guard = pushGuardEnvironment({});
    for (const remote of ['local', 'web']) {
      const pushed = git(['push', remote, 'HEAD:refs/heads/main'], guard);
      assert.notEqual(pushed.status, 0); assert.match(pushed.stderr, /planwharf-push-blocked/);
    }
    assert.equal(spawnSync('git', ['--git-dir', bare, 'rev-parse', '--verify', '-q', 'refs/heads/main']).status, 1, 'nothing reached the remote');
    assert.equal(git(['log', '--oneline'], guard).status, 0, 'local Git work still runs');
    assert.equal(git(['push', 'local', 'HEAD:refs/heads/main']).status, 0, 'unguarded push still works');
    assert.deepEqual(Object.keys(pushGuardEnvironment({ GIT_CONFIG_COUNT: '2' })).sort(), ['GIT_CONFIG_COUNT', 'GIT_CONFIG_KEY_2', 'GIT_CONFIG_VALUE_2']);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

function fixture(lead) {
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-autonomy-')), folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const plan = { schema: 2, projects: [{ project: { id: 'demo', name: 'Demo', folder }, revisions: [{ id: 1, direction: 'Ship it', open: [] }], tasks: [{ id: 't1', title: 'Build', detail: 'Build it', status: 'running', executionVersion: 2, revision_id: 1, source: 'chat' }] }] };
  const launches = [], live = new Set(), asks = [], prepared = [];
  const orchestra = createOrchestrator({ dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }), models: [{ id: 'lead', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true }], fetchPlans: null,
    inspectFolder: () => ({ git: false, dirty: [] }), spawnWorker: info => { const pid = 3000 + launches.length; launches.push(info); live.add(pid); return { pid }; }, isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid),
    askModel: async info => { asks.push(info); return { answer: lead(info) }; }, prepareTask: async (projectId, question) => { prepared.push({ projectId, question }); return { ok: true }; } });
  return { orchestra, dir, plan, launches, live, asks, prepared };
}

test('off by default: nothing is approved, and enabling requires Automatic mode', async () => {
  const f = fixture(() => '{"done":true}');
  try {
    await f.orchestra.tick(); await f.orchestra.autonomyTick();
    assert.equal(f.launches.length, 0);
    await f.orchestra.setAutonomy('demo', true); await f.orchestra.autonomyTick();
    assert.equal(f.launches.length, 0, 'Manual mode keeps the approval gate');
  } finally { await f.orchestra.shutdown(); fs.rmSync(f.dir, { recursive: true, force: true }); }
});

test('enabled: the policy approves, records itself, guards the worker, and disabling stops it', async () => {
  const f = fixture(() => '{"done":true}');
  try {
    await f.orchestra.setMode('auto'); await f.orchestra.tick();
    assert.equal((await f.orchestra.setAutonomy('missing', true)).ok, false);
    assert.equal((await f.orchestra.setAutonomy('demo', true)).ok, true);
    await f.orchestra.autonomyTick();
    assert.equal(f.launches.length, 1);
    assert.ok(Object.values(f.launches[0].env).includes('url.planwharf-push-blocked://.pushInsteadOf'));
    const view = f.orchestra.executionView('demo');
    assert.ok(view.timeline.some(event => event.type === 'MEGATASK_APPROVED' && /autonomy setting/.test(event.summary)));
    assert.equal((await f.orchestra.snapshot('demo')).autonomy.demo.enabled, true);
    await f.orchestra.setAutonomy('demo', false);
    assert.equal((await f.orchestra.snapshot('demo')).autonomy.demo.enabled, false);
  } finally { await f.orchestra.shutdown(); fs.rmSync(f.dir, { recursive: true, force: true }); }
});

test('a manual approval carries no autonomy claim and no push guard', async () => {
  const f = fixture(() => '{"done":true}');
  try {
    const first = await f.orchestra.tick();
    await f.orchestra.approve('demo', first.projects[0].megaTaskId);
    assert.equal(f.launches[0].env, undefined);
    assert.ok(!f.orchestra.executionView('demo').timeline.some(event => /autonomy setting/.test(event.summary)));
  } finally { await f.orchestra.shutdown(); fs.rmSync(f.dir, { recursive: true, force: true }); }
});

test('full loop on a real server: starts, approves, works, reassesses, continues and stops without a person', async () => {
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-autonomy-loop-')), folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = { id: 'lead', label: 'Lead', provider: 'codex', adapter: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'default', plan: 'Fixture', providerCatalog: true, roles: { orchestrator: true, worker: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } };
  const launches = [], questions = [], replies = ['{"done":false,"next":"Add the missing docs","assumptions":["English docs"]}', '{"done":true,"next":null}'];
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models: [model],refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });
      fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Fixture work finished.', filesChanged: [], tests: [{ command: 'fixture', status: 'PASSED', evidence: 'fixture' }], blockers: [], nextAction: 'None.', createdAt: new Date().toISOString() }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: 'codex', model: 'gpt-6-luna', createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async info => { questions.push(info.question); return { answer: replies.shift() ?? '{"done":true}' }; } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); const body = await response.json(); if (!response.ok) throw Error(route + ': ' + (body.error || body.reason)); return body; };
  try {
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: 'Loop', direction: 'Ship the fixture.', folder }, createFolder: false });
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'lead' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    let view, deadline = Date.now() + 30000;
    while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (view.autonomy?.[projectId]?.stopReason) break; await new Promise(resolve => setTimeout(resolve, 100)); }
    const settings = view.autonomy[projectId];
    assert.equal(settings.stopReason, 'GOAL_REACHED', JSON.stringify(view.projects.find(row => row.id === projectId)?.log));
    assert.equal(launches.length, 2);
    assert.equal(settings.approved.length, 2);
    assert.deepEqual(settings.assumptions, ['English docs']);
    assert.match(launches[1].job.detail, /Add the missing docs/);
    for (const launch of launches) assert.ok(Object.values(launch.env).includes('url.planwharf-push-blocked://.pushInsteadOf'));
    assert.equal(questions.length, 2);
    assert.match(questions[0], /Fixture work finished/);
    const policyApprovals = view.execution.timeline.filter(event => event.projectId === projectId && /autonomy setting/.test(event.summary));
    assert.equal(policyApprovals.length, 2);
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a restart turns autonomy off instead of resuming it', async () => {
  const { createOrchestrator } = require('./orchestrator.js');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-autonomy-restart-')), folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const plan = { schema: 2, projects: [{ project: { id: 'demo', name: 'Demo', folder }, revisions: [{ id: 1, direction: 'Ship it', open: [] }], tasks: [] }] };
  const { createAttemptEngine } = require('./attempts.js');
  const open = () => createOrchestrator({ dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }), models: [{ id: 'lead', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true }], fetchPlans: null, inspectFolder: () => ({ git: false, dirty: [] }), spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false, killWorker: () => {} });
  const first = open();
  try {
    await first.setMode('auto'); await first.setAutonomy('demo', true);
    assert.equal((await first.tick()).autonomy.demo.enabled, true);
    await first.shutdown();
    const second = open();
    assert.equal((await second.tick()).autonomy.demo.enabled, false);
    await second.shutdown();
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});