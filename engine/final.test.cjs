'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');
const { createInvocationLedger } = require('./invocations.cjs');
const { compactState, recentContext } = require('./briefing.cjs');
const { summarizeTimeline, checkpointDiff } = require('./project-history.cjs');
const { exportProject } = require('./project-export.cjs');
const { loadCatalog, providerViews } = require('./provider-catalog.cjs');
const { registerAdapter, adapterFor } = require('./provider-adapters.cjs');
const { launchSpec } = require('./providers.cjs');
const { pickWorker, canOrchestrate } = require('./orchestrator.js');
const { createServer } = require('../server.cjs');
const { createAppWindows } = require('./app-windows.cjs');
test('targeted approve, stop and resume never inspect or dispatch the other project', async () => {
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-scoped-actions-'));
  const plan = { schema: 2, projects: ['ai-companion-desktop', 'raceengineer'].map(id => ({ project: { id, name: id, folder: path.join(dir, id) }, revisions: [{ id: 1, direction: 'safe fixture', open: [] }], tasks: [{ id: id + '-task', title: 'Implement fixture', detail: 'Isolated test', status: 'running', executionVersion: 2, revision_id: 1 }] })) };
  const inspected = [], launches = [], live = new Set();
  const orchestra = createOrchestrator({ dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }), models: [{ id: 'fixture', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true }], fetchPlans: null,
    inspectFolder: folder => { inspected.push(folder); return { git: false, dirty: [] }; }, spawnWorker: info => { const pid = 2000 + launches.length; launches.push(info); live.add(pid); return { pid }; }, isProcessAlive: pid => live.has(pid), killWorker: pid => live.delete(pid) });
  try {
    const initial = await orchestra.tick();
    inspected.length = 0; await orchestra.tick();
    assert.equal(inspected.length, 0, 'Idle proposals never repeatedly inspect project source');
    for (const item of plan.projects) {
      const id = item.project.id, megaTaskId = initial.projects.find(row => row.id === id).megaTaskId;
      inspected.length = 0;
      const approved = await orchestra.approve(id, megaTaskId); assert.equal(approved.ok, true);
      assert.ok(inspected.every(folder => folder === item.project.folder)); assert.equal(launches.at(-1).projectId, id);
      inspected.length = 0;
      assert.equal((await orchestra.stop(id, megaTaskId)).ok, true);
      const cp = orchestra.executionView(id).checkpoints.at(-1);
      assert.equal((await orchestra.resume(id, { megaTaskId, checkpointId: cp.id, confirmChanges: true })).ok, true);
      assert.ok(inspected.every(folder => folder === item.project.folder));
      assert.equal(launches.at(-1).projectId, id);
      await orchestra.stop(id, megaTaskId);
    }
  } finally { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); }
});
test('ledger retains exact identity, durations, terminal states and reliable tokens across restart', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-ledger-'));
  try {
    let at = 0;
    const ledger = createInvocationLedger(dir, { now: () => new Date(at).toISOString() });
    const id = ledger.start({ role: 'orchestrator', projectId: 'p', provider: 'fixture', model: 'exact', reason: 'reasoning', env: { TOKEN: 'must-not-persist' } });
    at = 2000; ledger.finish(id, 'COMPLETE', 0, { input: 12, output: 4 });
    const worker = ledger.start({ role: 'worker', projectId: 'p', provider: 'fixture', model: 'exact', reason: 'approved work', attemptId: 'a' });
    ledger.update(worker, { pid: 123 }); at = 3000;
    ledger.reconcile({ execution: { results: [{ attemptId: 'a', status: 'BLOCKED', exitCode: null }] } }, () => false);
    const reloaded = createInvocationLedger(dir).view('p');
    assert.equal(reloaded.orchestrator[0].durationMs, 2000);
    assert.equal(reloaded.workers[0].BLOCKED, 1);
    assert.deepEqual(reloaded.recent.find(row => row.role === 'orchestrator').tokens, { input: 12, output: 4 });
    assert.equal(reloaded.recent.find(row => row.role === 'worker').tokens, undefined);
    assert.ok(!fs.readFileSync(ledger.file, 'utf8').includes('must-not-persist'));
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('compact briefing excludes user-only notes and completed history, preserves checkpoint identity', () => {
  const state = { focus: { id: 'p', lastCommit: 'abc', dirty: [] }, executionHandoff: { megaTasks: [{ id: 'm', goal: 'goal' }], subTasks: [{ megaTaskId: 'm', id: 'done', status: 'COMPLETE' }, { megaTaskId: 'm', id: 'pending', status: 'PENDING' }], results: [], checkpoints: [{ megaTaskId: 'm', id: 'cp', completedSubTaskIds: ['done'], pendingSubTaskIds: ['pending'] }], notes: [{ text: 'private' }, { text: 'relevant', aiRelevant: true }] } };
  const brief = compactState(state);
  assert.equal(brief.executionHandoff.notes.length, 1);
  assert.equal(brief.executionHandoff.subTasks.length, 1);
  assert.deepEqual(brief.executionHandoff.checkpoint.completedSubTaskIds, ['done']);
  assert.equal(recentContext(Array.from({ length: 100 }, () => ({ question: 'q'.repeat(4000), answer: 'a'.repeat(8000) }))).length, 6);
  assert.equal(state.executionHandoff.notes.length, 2);
});
test('registered provider executes without brand-specific core edits; capabilities and pools are explicit', () => {
  const adapter = { nativeModel: /^fixture-/, executable: () => 'fixture.exe', availability: () => ({ available: true }), execute: model => ({ command: 'fixture.exe', model: model.cli }), environment: env => ({ ...env }), terminate: () => true, quota: async () => ({ readable: false }), discover: async () => ({ ok: true, ids: ['fixture-code'] }), resultIdentity: model => ({ provider: 'test-extension', model: model.cli }) };
  registerAdapter('test-extension', adapter);
  assert.throws(() => registerAdapter('test-extension', adapter), /unique/);
  assert.equal(adapterFor('test-extension').availability().available, true);
  assert.equal(launchSpec({ provider: 'test-extension', cli: 'fixture-code' }, '.', 'prompt').model, 'fixture-code');
  const { providers, models } = loadCatalog({ schema: 3, providers: [{ id: 'test-extension', adapter: 'test-extension', quotaPoolId: 'shared', models: Array.from({ length: 55 }, (_, n) => ({ id: 'm' + n, cli: 'fixture-code', enabled: true, capabilities: { orchestratorEligible: false, canCode: n !== 0 } })) }] });
  assert.equal(models.length, 55); assert.equal(models[0].orchestratorEligible, false);
  assert.equal(pickWorker(models, { job: { title: 'Implement code' } }).id, 'm1');
  assert.equal(canOrchestrate({ enabled: true, orchestratorEligible: true, capabilities: { canUseTools: false } }), false);
  const views = providerViews(providers, models, [{ id: 'shared', readable: true, leftPct: 82 }]);
  assert.equal(views[0].remainingPercent, 82);
  assert.ok(models.every(model => model.quotaPoolId === 'shared'));
});
test('timeline groups technical evidence; checkpoint diff reports changes without replay', () => {
  const events = ['SUBTASK_STARTED', 'SUBTASK_COMPLETED', 'MEGATASK_COMPLETED'].map((type, n) => ({ id: String(n), type, createdAt: new Date(n).toISOString(), reference: { megaTaskId: 'm' }, summary: type }));
  const summary = summarizeTimeline(events, { megaTasks: [{ id: 'm', title: 'Add arithmetic', status: 'COMPLETE' }], results: [{ megaTaskId: 'm', status: 'COMPLETE', summary: 'Added module and two tests.' }] });
  assert.equal(summary.length, 1); assert.equal(summary[0].evidence.length, 3); assert.match(summary[0].summary, /two tests/);
  const diff = checkpointDiff({ repoState: { repoHead: 'a', fingerprint: 'old', comparable: true }, planRevisionId: 1, completedSubTaskIds: [] }, { repoHead: 'b', fingerprint: 'new', comparable: true, dirtyFiles: [' M f'] }, 1, ['done']);
  assert.equal(diff.requiresConfirmation, true); assert.match(diff.changes.join(' '), /will not be replayed/);
});
test('export selects one project metadata and excludes credential fields and raw logs', () => {
  const project = { project: { id: 'p', name: 'Safe' }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [{ text: 'note', aiRelevant: true }] } };
  const bundle = exportProject(project, { megaTasks: [{ id: 'm', projectId: 'p', status: 'COMPLETE', title: 'work', credentials: 'secret' }, { id: 'other', projectId: 'q' }], subTasks: [], results: [{ megaTaskId: 'm', summary: 'result', workerTail: 'raw', token: 'secret' }], checkpoints: [], timeline: [] });
  assert.equal(bundle.execution.megaTasks.length, 1); assert.equal(bundle.schema, 1); assert.equal(bundle.restoreSupported, true);
  assert.ok(!JSON.stringify(bundle).includes('secret')); assert.ok(!JSON.stringify(bundle).includes('raw'));
});
test('separate windows open together, focus independently, and closing one preserves the other', async () => {
  const opened = [], focused = [];
  const windows = createAppWindows({ separateWindows: true, open: (url, view) => { const child = new EventEmitter(); child.kill = () => {}; opened.push({ url, view, child }); return child; }, focus: view => { focused.push(view); return true; } });
  windows.openApp('http://127.0.0.1:4173'); assert.equal(opened.length, 2);
  windows.heartbeat({ id: 'p', view: 'planwharf' }); windows.heartbeat({ id: 'o', view: 'orchestrator' });
  windows.openView('orchestrator', 'http://127.0.0.1:4173'); await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(focused, ['orchestrator']); assert.equal(opened.length, 2);
  opened[0].child.emit('exit', 0); assert.deepEqual(windows.status(), { planwharf: false, orchestrator: true });
  windows.closeAll();
});
test('notification selection only includes important local evidence', () => {
  assert.deepEqual(require('../notifications.js').important([{ type: 'SUBTASK_STARTED' }, { type: 'CHECKPOINT_SAVED' }, { type: 'MEGATASK_COMPLETED' }]).map(event => event.type), ['CHECKPOINT_SAVED', 'MEGATASK_COMPLETED']);
});
test('HTTP status, quota, stop acknowledgements and mode use no model; export and notes stay scoped', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-final-http-'));
  const plan = structuredClone(require('../fixtures/starter-notebook.json'));
  plan.projects = ['ai-companion-desktop', 'raceengineer'].map(id => ({ ...structuredClone(plan.projects[0]), project: { id, name: id }, tasks: [] }));
  const file = path.join(dir, 'notebook.json'); fs.writeFileSync(file, JSON.stringify(plan));
  let calls = 0, inspections = 0;
  const server = createServer({ planFile: file, skillsEnabled: false, refreshModels: false, monitorIntervalMs: 60000, models: [{ id: 'fixture', provider: 'codex', cli: 'gpt-6-luna', enabled: true }], askModel: async () => { calls++; return 'Reasoned reply'; }, inspectFolder: () => { inspections++; return { git: false, dirty: [] }; }, fetchPlans: async () => [{ id: 'ChatGPT', label: 'ChatGPT', readable: true, leftPct: 82 }] });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const request = async (route, data) => { const res = await fetch(`http://127.0.0.1:${server.address().port}` + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return { status: res.status, data: await res.json() }; };
  try {
    await request('/api/focus', { projectId: 'ai-companion-desktop' });
    for (const question of ['quota remaining', 'provider status', 'model status', 'status', 'current checkpoint identity', 'selected project', 'stop', 'switch to manual']) {
      const result = await request('/api/orchestrator/ask', { projectId: 'ai-companion-desktop', question }); assert.equal(result.status, 200); assert.equal(result.data.ok, true);
      if (question === 'quota remaining') assert.match(result.data.answer, /82% REMAINING/);
    }
    assert.equal(calls, 0); assert.equal(inspections, 0);
    const note = await request('/api/notes', { projectId: 'ai-companion-desktop', action: 'add', text: 'AI guidance', aiRelevant: true }); assert.equal(note.data.notes[0].aiRelevant, true);
    const bundle = await request('/api/project-export?projectId=ai-companion-desktop'); assert.equal(bundle.data.project.id, 'ai-companion-desktop'); assert.equal(bundle.data.notes.length, 1);
    assert.equal((await request('/api/project-export?projectId=unknown')).status, 404);
    assert.equal((await request('/api/project-export?projectId=raceengineer')).data.notes.length, 0);
    await request('/api/orchestrator/lead', { id: 'fixture' });
    await request('/api/orchestrator/ask', { projectId: 'ai-companion-desktop', question: 'Explain this project goal.' }); assert.equal(calls, 1);
    const snapshot = await request('/api/orchestrator?readOnly=1'); assert.equal(snapshot.data.aiUsage.orchestrator[0].calls, 1);
    assert.equal(snapshot.data.aiUsage.recent[0].projectId, 'ai-companion-desktop');
  } finally { await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
