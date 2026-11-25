'use strict';
// Work the crew started goes on with the crew after Resume. On 2026-11-25 the owner stopped job 45 (Stop the
// crew), resumed it the next day, and the first step that stopped part way ended the work: with the crew off,
// nobody asked the lead for the rest.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');

test('Resume of work the crew started turns the crew back on, so a partial step goes back to the lead', { timeout: 90000 }, async () => {
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-resume-crew-')), folder = path.join(dir, 'project');
  fs.mkdirSync(path.join(folder, 'test'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'test', 'ok.test.cjs'), "require('node:test')('ok', () => {});\n");
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan: provider, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('a', 'codex', 'gpt-6-luna'), model('b', 'grok', 'grok-4.7')];
  const launches = [], asks = [], alive = new Set();
  let hold = true, builds = 0;
  const finish = (info, result) => {
    fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
    fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
    fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, filesChanged: [], tests: [], blockers: [], nextAction: 'None', ...result }));
    fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
  };
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      const pid = 900000 + launches.length;
      const build = !/Verify|independently/i.test(info.job.title);
      if (build && hold) { alive.add(pid); info.held = true; }
      else if (build && ++builds === 1) finish(info, { status: 'BLOCKED', summary: 'Did half of it.', filesChanged: ['src/half.cs'], blockers: ['Not complete.'] });
      else finish(info, { status: 'COMPLETE', summary: 'Done.', filesChanged: ['src/done.cs'] });
      return { pid };
    },
    isProcessAlive: pid => alive.has(pid), killWorker: pid => { alive.delete(pid); return true; }, abortAsk: () => {},
    askModel: async info => {
      asks.push(info.question);
      if (/stopped before the goal was met/.test(info.question)) return { answer: '{"done":false,"next":"Finish the other half.","assumptions":[]}' };
      return { answer: '{"done":true,"next":null,"assumptions":[]}' };
    } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return { status: response.status, body: await response.json() }; };
  const until = async done => { const deadline = Date.now() + 60000; let view; while (Date.now() < deadline) { view = (await api('/api/orchestrator')).body; if (done(view)) return view; await new Promise(resolve => setTimeout(resolve, 100)); } return view; };
  try {
    const current = (await api('/api/plan')).body;
    const created = (await api('/api/projects', { version: current.version, project: { name: 'Resume', direction: 'Build the thing and write tests.', folder }, createFolder: false })).body;
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'a' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    await until(() => launches.length > 0);
    // The owner stops the crew and the work, as with Stop the crew.
    await api('/api/orchestrator/autonomy', { projectId, enabled: false });
    const stopped = await api('/api/orchestrator/stop', { projectId });
    assert.equal(stopped.status, 200, JSON.stringify(stopped.body).slice(0, 300));
    hold = false;
    const resumed = await api('/api/orchestrator/resume', { projectId, confirmChanges: true });
    assert.equal(resumed.status, 200, JSON.stringify(resumed.body).slice(0, 300));
    const view = await until(view => view.autonomy?.[projectId]?.stopReason);
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId).map(event => event.type + ' ' + event.summary);
    assert.ok(timeline.some(line => /Resumed work the crew started: the crew carries on with it\./.test(line)), JSON.stringify(timeline));
    assert.ok(asks.some(question => /stopped before the goal was met/.test(question)), 'the partial step after Resume went back to the lead: ' + JSON.stringify(timeline));
    assert.equal(view.autonomy[projectId].stopReason, 'GOAL_REACHED', JSON.stringify(timeline));
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
