'use strict';
// Benchmark fixes of 2026-11-04: two-slice plan, the lead picks the crew, a provider refusal
// reroutes the slice, Planwharf runs the tests before the lead judges, and a failed lead
// decision is retried once with a lead from another provider.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { refusalReason, activeRefusal, recordRefusal } = require('./provider-refusal.cjs');
const { renameWithRetry } = require('./atomic-rename.cjs');
const { testCommand, runChecks } = require('./project-checks.cjs');
const autonomy = require('./autonomy.cjs');
const { dispatchPlan, sliceCount } = require('./orchestrator.js');

test('refusals are read from the end of an output, and expire', () => {
  assert.match(refusalReason('work...\nFable 5.1 requires usage credits. Switch to another model.'), /requires usage credits/);
  assert.match(refusalReason('{"result":"You\'ve hit your 5-hour limit · resets 3am (Europe/Rome)"}'), /5-hour limit/);
  assert.equal(refusalReason('All 42 tests passed.'), null);
  assert.equal(refusalReason('requires usage credits' + ' x'.repeat(5000)), null, 'old text is not a refusal');
  const store = {};
  recordRefusal(store, 'm', 'credits', '2026-11-04T10:00:00.000Z', 1000);
  assert.ok(activeRefusal(store, 'm', Date.parse('2026-11-04T10:00:00.500Z')));
  assert.equal(activeRefusal(store, 'm', Date.parse('2026-11-04T10:00:02.000Z')), null);
  assert.deepEqual(store.refusals, {});
});

test('a locked rename is retried, other errors are not', () => {
  let calls = 0;
  const busy = () => { calls += 1; if (calls < 3) throw Object.assign(Error('locked'), { code: 'EPERM' }); };
  assert.equal(renameWithRetry('a', 'b', { rename: busy }), 3);
  assert.throws(() => renameWithRetry('a', 'b', { rename: () => { throw Object.assign(Error('gone'), { code: 'ENOENT' }); } }), /gone/);
});

test('a build job is split into implement and an independent verify, without an inspection slice', () => {
  assert.equal(sliceCount({ title: 'Implement it', detail: 'write code' }, {}, 4), 2);
  const plan = dispatchPlan([{ id: 'a' }, { id: 'b' }], { title: 'Implement it' }, {});
  assert.equal(plan.length, 2);
  assert.match(plan[0].slice, /^Implement the whole job/);
  assert.match(plan[1].slice, /^Verify the work independently/);
  assert.ok(!plan.some(part => /Inspect/.test(part.slice)));
});

test('the crew reply must name one allowed worker per slice', () => {
  assert.deepEqual(autonomy.parseAssignment('{"assign":["b","a"],"why":"fast"}', 2, ['a', 'b']), { assign: ['b', 'a'], why: 'fast' });
  assert.equal(autonomy.parseAssignment('{"assign":["b"]}', 2, ['a', 'b']), null);
  assert.equal(autonomy.parseAssignment('{"assign":["z","a"]}', 2, ['a', 'b']), null);
  assert.match(autonomy.assignmentPrompt({ name: 'P', goal: 'G', slices: ['one', 'two'], workers: [{ id: 'a', label: 'A', provider: 'x' }] }), /different provider/);
});

test('project checks run node --test when test files exist and report a failure', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-checks-'));
  try {
    assert.equal(testCommand(dir), null);
    assert.equal(await runChecks(dir), null);
    fs.mkdirSync(path.join(dir, 'test'));
    fs.writeFileSync(path.join(dir, 'test', 'a.test.cjs'), "require('node:test')('x', () => { throw Error('boom') });\n");
    const failed = await runChecks(dir);
    assert.equal(failed.label, 'node --test');
    assert.notEqual(failed.exitCode, 0);
    fs.writeFileSync(path.join(dir, 'test', 'a.test.cjs'), "require('node:test')('x', () => {});\n");
    const passed = await runChecks(dir);
    assert.equal(passed.exitCode, 0);
    assert.equal(passed.timedOut, false);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a clean finish needs two complete slices without blockers and a fresh passing run', () => {
  const ok = { status: 'COMPLETE', blockers: [] }, pass = { exitCode: 0, timedOut: false };
  assert.equal(autonomy.cleanFinish([ok, ok], pass), true);
  assert.equal(autonomy.cleanFinish([ok], pass), false, 'a single slice had no independent check');
  assert.equal(autonomy.cleanFinish([ok, { status: 'COMPLETE', blockers: ['x'] }], pass), false);
  assert.equal(autonomy.cleanFinish([ok, { status: 'PARTIAL', blockers: [] }], pass), false);
  assert.equal(autonomy.cleanFinish([ok, ok], { exitCode: 1, timedOut: false }), false);
  assert.equal(autonomy.cleanFinish([ok, ok], { exitCode: 0, timedOut: true }), false);
  assert.equal(autonomy.cleanFinish([ok, ok], null), false, 'no tests found');
});

test('full loop: the lead picks the crew, a refusal reroutes, checks feed the verdict, a failed lead is replaced', async () => {
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-loop-')), folder = path.join(dir, 'project');
  fs.mkdirSync(path.join(folder, 'test'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'test', 'ok.test.cjs'), "require('node:test')('ok', () => {});\n");
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan: provider, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, reviewer: true, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('a', 'codex', 'gpt-6-luna'), model('b', 'grok', 'grok-4.7'), model('c', 'claude', 'claude-opus-5-5')];
  const launches = [], asks = [];
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, skillsEnabled: false, syncSkills: false, refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
      if (info.worker.id === 'c') { fs.writeFileSync(info.logPath, 'Starting.\nYou\'ve hit your 5-hour limit · resets 3am\n'); return { pid: 800000 + launches.length }; }
      fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });
      fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Slice by ' + info.worker.id, filesChanged: [], tests: [{ command: 'fixture', status: 'PASSED', evidence: 'fixture' }], blockers: info.worker.id === 'b' ? ['open question for the lead'] : [], nextAction: 'None.', createdAt: new Date().toISOString() }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async info => {
      asks.push({ model: info.model.id, question: info.question });
      if (/Available workers:/.test(info.question)) return { answer: '{"assign":["c","b"],"why":"c builds, b checks"}' };
      if (info.model.id === 'a') return { answer: 'I think it is fine.', failure: 'process' };
      return { answer: '{"done":true,"next":null,"assumptions":["checked by b"]}' };
    } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); const body = await response.json(); if (!response.ok) throw Error(route + ': ' + (body.error || body.reason)); return body; };
  try {
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: 'Crew', direction: 'Implement the fixture and write tests.', folder }, createFolder: false });
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'a' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    let view, deadline = Date.now() + 60000;
    while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (view.autonomy?.[projectId]?.stopReason) break; await new Promise(resolve => setTimeout(resolve, 100)); }
    const settings = view.autonomy[projectId];
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId);
    assert.equal(settings.stopReason, 'GOAL_REACHED', JSON.stringify(timeline.map(event => event.type + ' ' + event.summary)));
    assert.ok(timeline.some(event => event.type === 'MEGATASK_ASSIGNED'), 'the lead chose the crew');
    assert.deepEqual(launches.map(launch => launch.worker.id).slice(0, 1), ['c'], 'first slice went to the chosen implementer');
    assert.ok(timeline.some(event => event.type === 'MODEL_REFUSAL_FALLBACK' && /hit your 5-hour limit/.test(event.summary)));
    assert.ok(launches.length >= 3 && launches[1].worker.id !== 'c', 'the refused slice went to another model');
    assert.equal(launches.filter(launch => launch.worker.id === 'c').length, 1, 'the refused model is not used again');
    const evaluations = asks.filter(ask => /Recorded results:/.test(ask.question));
    assert.match(evaluations[0].question, /Planwharf ran `node --test`.*PASSED/);
    assert.deepEqual(evaluations.map(ask => ask.model), ['a', 'b'], 'a failed lead decision is retried once with another provider');
    assert.deepEqual(settings.assumptions, ['checked by b']);
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
