'use strict';
// Work that stops part way goes back to the lead, which names the rest; the rest is split again and the
// crew carries on. The lead sizes the work by the documents the goal names, and may read the project.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');

function harness({ worker, lead }) {
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-continue-')), folder = path.join(dir, 'project');
  fs.mkdirSync(path.join(folder, 'test'), { recursive: true });
  fs.mkdirSync(path.join(folder, 'jobs'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'test', 'ok.test.cjs'), "require('node:test')('ok', () => {});\n");
  fs.writeFileSync(path.join(folder, 'jobs', '42-port.md'), '# Sheet 42\n\nModules to port: alpha, beta, gamma.\n');
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan: provider, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('a', 'codex', 'gpt-6-luna'), model('b', 'grok', 'grok-4.7'), model('c', 'claude', 'claude-opus-5-5')];
  const launches = [], asks = [];
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      const result = worker(info, launches.length);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, filesChanged: [], tests: [], blockers: [], nextAction: 'None', ...result }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async info => { asks.push(info); return { answer: lead(info, asks.length) }; } });
  const start = async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = 'http://127.0.0.1:' + server.address().port;
    const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return response.json(); };
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: 'Port', direction: 'Port, as sheet 42 says, the modules.', folder }, createFolder: false });
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'a' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    const until = async done => {
      let view; const deadline = Date.now() + 60000;
      while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (done(view)) break; await new Promise(resolve => setTimeout(resolve, 100)); }
      return view;
    };
    return { api, projectId, until };
  };
  const close = async () => { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); };
  return { start, close, launches, asks, folder };
}

const splitQuestion = info => /Your crew can work in parallel/.test(info.question);
const oneBuild = JSON.stringify({ why: 'One job.', verify: 'b', parts: [{ title: 'Port the modules', detail: 'Port alpha, beta and gamma.', files: ['src'], dependsOn: [], size: 'heavy', model: 'c' }] });

test('a step that stops part way goes back to the lead; the rest is split again and the crew finishes', async () => {
  let splits = 0;
  const run = harness({
    // The first build ports one module of three and stops; everything after it completes.
    worker: (info, count) => count === 1 ? { status: 'BLOCKED', summary: 'Ported alpha only.', filesChanged: ['src/alpha.cs'], blockers: ['The full three-module port is not complete.'] } : { status: 'COMPLETE', summary: 'Done by ' + info.worker.id },
    lead: (info) => {
      if (splitQuestion(info)) {
        splits += 1;
        return splits === 1 ? oneBuild : JSON.stringify({ why: 'Two modules at once.', verify: 'b', parts: [
          { title: 'Port beta', detail: 'Port beta.', files: ['src/beta.cs'], dependsOn: [], size: 'normal', model: 'a' },
          { title: 'Port gamma', detail: 'Port gamma.', files: ['src/gamma.cs'], dependsOn: [], size: 'normal', model: 'c' }] });
      }
      if (/stopped before the goal was met/.test(info.question)) return '{"done":false,"next":"Port beta and gamma; alpha is already ported.","assumptions":[]}';
      return '{"done":true,"next":null,"assumptions":[]}';
    } });
  try {
    const { projectId, until } = await run.start();
    const view = await until(view => view.autonomy?.[projectId]?.stopReason);
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId).map(event => event.type + ' ' + event.summary);
    assert.equal(view.autonomy[projectId].stopReason, 'GOAL_REACHED', JSON.stringify(timeline));
    const first = run.asks.find(splitQuestion);
    assert.match(first.question, /Document the goal names, jobs\/42-port\.md:\n<<<\n# Sheet 42\n\nModules to port: alpha, beta, gamma\./, 'the lead sees the sheet the goal names');
    assert.equal(first.folder, run.folder, 'the project folder is open to the lead when it splits the work');
    const told = run.asks.find(info => /stopped before the goal was met/.test(info.question));
    assert.ok(told, 'the lead heard about the stop without waiting for an alert');
    assert.match(told.question, /BLOCKED: Ported alpha only\. Files changed: src\/alpha\.cs\. Blockers: The full three-module port is not complete\./);
    assert.equal(splits, 2, 'the rest of the work was split again');
    assert.ok(run.launches.some(launch => /Port beta and gamma; alpha is already ported/.test(launch.job.detail + ' ' + launch.job.title) || launch.job.title === 'Port beta'), 'the crew continued with the rest');
  } finally { await run.close(); }
});

test('two rounds with no file changed stop the crew and ask the owner in Decisions; the answer starts it again', async () => {
  const run = harness({
    worker: () => ({ status: 'BLOCKED', summary: 'Could not start the port.', filesChanged: [], blockers: ['Missing tool.'] }),
    lead: info => splitQuestion(info) ? oneBuild : /stopped before the goal was met/.test(info.question) ? '{"done":false,"next":"Port the modules again.","assumptions":[]}' : '{"done":true,"next":null,"assumptions":[]}' });
  try {
    const { api, projectId, until } = await run.start();
    const view = await until(view => view.autonomy?.[projectId]?.stopReason);
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId).map(event => event.type + ' ' + event.summary);
    assert.equal(view.autonomy[projectId].stopReason, 'NO_PROGRESS', JSON.stringify(timeline));
    assert.equal(run.asks.filter(info => /stopped before the goal was met/.test(info.question)).length, 1, 'the lead was asked once; the second stall went to the owner');
    const inbox = await api('/api/decisions');
    const decision = inbox.open.find(item => item.projectId === projectId);
    assert.ok(decision, JSON.stringify(inbox));
    assert.match(decision.text, /made no progress twice in a row \(no file changed, or the same step stopped for the same reason\)\. Last blocker: .*How should it continue\?/);
    assert.ok(view.execution.timeline.some(event => event.projectId === projectId && event.type === 'NOTE' && /no progress twice in a row/.test(event.summary)), 'the stop is explained in Progress');
    const launched = run.launches.length;
    const answered = await api('/api/decisions/answer', { projectId, decisionId: decision.id, answer: 'Install the tool first, then port.' });
    assert.equal(answered.ok, true, JSON.stringify(answered));
    const after = await until(view => run.launches.length > launched);
    assert.ok(run.launches.length > launched, 'the crew started again after the answer: ' + JSON.stringify(after.execution.timeline.filter(event => event.projectId === projectId).slice(-6).map(event => event.type + ' ' + event.summary)) + JSON.stringify(after.autonomy[projectId]));
    assert.equal(after.autonomy[projectId].stopReason ?? null, null);
    assert.ok(run.launches.slice(launched).some(launch => /Install the tool first, then port\./.test(JSON.stringify(launch.job))), 'the owner\'s answer reaches the new work');
  } finally { await run.close(); }
});

test('an unusable split is asked again once, and what Planwharf decided is shown in Progress', async () => {
  let splits = 0;
  const run = harness({
    worker: info => ({ status: 'COMPLETE', summary: 'Done by ' + info.worker.id }),
    lead: info => {
      if (!splitQuestion(info)) return '{"done":true,"next":null,"assumptions":[]}';
      splits += 1;
      // First a plan in prose, then the JSON object: sixteen parts with a model that may not build.
      return splits === 1 ? 'I would port the sixteen modules in parallel.' : JSON.stringify({ why: 'One part per module.', verify: 'nobody', parts: Array.from({ length: 16 }, (_, i) => (
        { title: 'Port module ' + i, detail: 'Port module ' + i + '.', files: ['src/m' + i + '.cs'], dependsOn: [], size: 'normal', model: i === 0 ? 'nobody' : 'a' })) });
    } });
  try {
    const { projectId, until } = await run.start();
    const view = await until(view => view.autonomy?.[projectId]?.stopReason);
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId);
    assert.equal(view.autonomy[projectId].stopReason, 'GOAL_REACHED', JSON.stringify(timeline.map(event => event.type + ' ' + event.summary)));
    assert.equal(splits, 2);
    assert.match(run.asks.filter(splitQuestion)[1].question, /Your previous reply could not be used \(no JSON object with parts\)/);
    assert.equal(run.launches.filter(launch => /^Port module/.test(launch.job.title)).length, 16, 'sixteen parts ran, ten at a time: ' + JSON.stringify(run.launches.map(launch => launch.job.title)) + JSON.stringify(timeline.map(event => event.type + ' ' + event.summary).slice(-12)));
    assert.ok(timeline.some(event => event.type === 'NOTE' && /Planwharf gave "Port module 0" to .*: the lead named a model that may not build it\./.test(event.summary)), 'the note is in Progress');
  } finally { await run.close(); }
});
