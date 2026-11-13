'use strict';
// Stopped work can be dropped, so the crew starts the new goal instead of the old work.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { createServer } = require('../server.cjs');

test('stopped work is dropped and the crew then starts the new goal, not the old one', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-drop-work-')), folder = path.join(dir, 'project');
  fs.mkdirSync(folder, { recursive: true });
  fs.writeFileSync(path.join(folder, 'notes.md'), 'notes\n');
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan: provider, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('a', 'codex', 'gpt-6-luna'), model('c', 'claude', 'claude-opus-5-5')];
  const launches = [];
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      // The old work stops: its first step is blocked.
      const status = /Old goal/.test(info.job.title + info.job.detail) ? 'BLOCKED' : 'COMPLETE';
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status, summary: 'Slice by ' + info.worker.id, filesChanged: [], tests: [], blockers: status === 'BLOCKED' ? ['Wrong branch'] : [], nextAction: 'None' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 950000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async () => ({ answer: '{"done":true,"next":null,"assumptions":[]}' }) });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return { status: response.status, ...(await response.json()) }; };
  const row = view => view.projects.find(item => item.id === projectId);
  const until = async (check, what) => { const deadline = Date.now() + 30000; let view; while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (check(view)) return view; await new Promise(resolve => setTimeout(resolve, 100)); } assert.fail(what + ': ' + JSON.stringify(row(view)).slice(0, 600)); };
  const goal = async direction => { const { version } = await api('/api/plan'); await api('/api/revisions', { version, projectId, draft: { title: 'Goal from the chat', direction, state: 'Not started.', closed: [], open: [] } }); };
  let projectId;
  try {
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: 'Drop', direction: 'A project.', folder }, createFolder: false });
    projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'c' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await goal('Old goal: port the pilot modules.');
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    await until(view => row(view)?.canResume === true, 'the old work stops and can be resumed');

    const dropped = await api('/api/orchestrator/reset', { projectId });
    assert.equal(dropped.status, 200, JSON.stringify(dropped).slice(0, 300));
    let view = await api('/api/orchestrator');
    assert.equal(row(view).canResume, false, 'the dropped work cannot be resumed');

    // The old goal was worked on: it is no plan ready to start. A new goal is.
    assert.equal(row(view).goalStarted, true);
    await goal('New goal: port the five modules of job 02.');
    assert.equal(row(await api('/api/orchestrator')).goalStarted, false, 'a new goal not started yet');
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    view = await until(view => /New goal/.test(row(view)?.megaTask?.title || ''), 'the crew starts the new goal');
    assert.doesNotMatch(row(view).megaTask.title, /Old goal/);
    assert.ok(launches.some(launch => /New goal/.test(launch.job.title + launch.job.detail)), 'a worker got the new goal');
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
