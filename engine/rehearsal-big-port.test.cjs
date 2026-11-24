'use strict';
// Rehearsal of a big job, end to end, with fake models: the scenario in fixtures/scenarios/big-port is the
// size of job 43 (sixteen modules from a sheet, one part that wires them together, then Verify). The fake
// lead and workers answer the way the real ones did on 2026-11-24, when this job stopped five times:
// - the lead sees only a one-line goal and must read the sheet it names to size the work;
// - it gives every part to its own company;
// - its replies carry a "summary";
// - one worker stops part way, reporting the files it changed;
// - the part that wires the others together changes their files;
// - one company's usage cannot be read.
// The run must reach the goal with every module on disk, the parts spread over the companies, and no error.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { unreadPlan } = require('./planUsage.js');

const scenarioDir = path.join(__dirname, '..', 'fixtures', 'scenarios', 'big-port');
const scenario = JSON.parse(fs.readFileSync(path.join(scenarioDir, 'scenario.json'), 'utf8'));

test('rehearsal: a job the size of job 43 runs from the goal to the end', { timeout: 120000 }, async () => {
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-rehearsal-')), folder = path.join(dir, 'project');
  fs.mkdirSync(path.join(folder, 'jobs'), { recursive: true });
  fs.mkdirSync(path.join(folder, 'test'), { recursive: true });
  fs.mkdirSync(path.join(folder, 'parity'), { recursive: true });
  fs.copyFileSync(path.join(scenarioDir, scenario.sheet), path.join(folder, 'jobs', scenario.sheet));
  fs.writeFileSync(path.join(folder, 'test', 'ok.test.cjs'), "require('node:test')('ok', () => {});\n");
  fs.writeFileSync(path.join(folder, scenario.integration), '[]\n');
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, plan, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('sol61', 'codex', 'ChatGPT', 'gpt-6-luna'), model('sol6', 'codex', 'ChatGPT', 'gpt-6-luna'), model('grok', 'grok', 'Grok', 'grok-4.7'), model('gemini', 'antigravity', 'Gemini', 'gemini-3.1-pro-high')];
  const launches = [], asks = [];
  let splits = 0, runnerTries = 0;
  const write = (file, text) => { fs.mkdirSync(path.dirname(path.join(folder, file)), { recursive: true }); fs.writeFileSync(path.join(folder, file), text); };
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, refreshModels: false, monitorIntervalMs: 50,
    // Grok's usage service refuses the saved token, as on 2026-11-24: Grok must still work.
    fetchPlans: async () => [unreadPlan('Grok', 'Grok login expired.')],
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      const title = info.job.title;
      const module = scenario.modules.find(name => title === 'Port ' + name);
      let result;
      if (module === 'runner' && ++runnerTries === 1) {
        write('src/runner.cs', '// half\n');
        result = { status: 'BLOCKED', summary: 'Ported half of runner; the process tree part is not done.', filesChanged: ['src/runner.cs'], blockers: ['The runner port is not complete.'] };
      } else if (module) {
        write(`src/${module}.cs`, `// ${module}\n`); write(`tests/${module}.tests.cs`, `// tests of ${module}\n`);
        result = { status: 'COMPLETE', summary: 'Ported ' + module + '.', filesChanged: [`src/${module}.cs`, `tests/${module}.tests.cs`] };
      } else if (/^Wire/.test(title)) {
        // Wiring the parts together changes their test files too.
        for (const name of scenario.modules) fs.appendFileSync(path.join(folder, 'tests', name + '.tests.cs'), '// wired\n');
        write(scenario.integration, JSON.stringify(scenario.modules) + '\n');
        result = { status: 'COMPLETE', summary: 'Wired every module together.', filesChanged: [scenario.integration, ...scenario.modules.map(name => `tests/${name}.tests.cs`)] };
      } else result = { status: 'COMPLETE', summary: 'Checked the whole work.' };
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, filesChanged: [], tests: [], blockers: [], nextAction: 'None', ...result }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async info => {
      asks.push(info);
      const q = info.question;
      if (/Your crew can work in parallel/.test(q)) {
        splits += 1;
        // The real lead gave its own company everything; a second split covers only what is left.
        const names = splits === 1 ? scenario.modules : ['runner'];
        const ports = names.map((name, i) => ({ title: 'Port ' + name, detail: 'Port ' + name + ' as the sheet says.', files: [`src/${name}.cs`, `tests/${name}.tests.cs`], dependsOn: [], size: 'heavy', model: i % 2 ? 'sol6' : 'sol61' }));
        return { answer: JSON.stringify({ why: 'One part per module, then wire them.', verify: 'sol61', parts: [...ports,
          { title: 'Wire the modules together', detail: 'Name every module in the comparison list and make every test pass.', files: [scenario.integration], dependsOn: ports.map((_, i) => i), size: 'heavy', model: 'sol61' }] }) };
      }
      if (/stopped before the goal was met/.test(q)) return { answer: '{"done":false,"next":"Finish the runner port, then wire every module together; the other fifteen modules are ported.","assumptions":[],"summary":"Fifteen modules are ported. Runner is half done."}' };
      return { answer: '{"done":true,"next":null,"assumptions":[],"summary":"Every module is ported and wired together. The tests pass."}' };
    } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return response.json(); };
  try {
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: scenario.name, direction: scenario.goal, folder }, createFolder: false });
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'sol61' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    let view; const deadline = Date.now() + 100000;
    while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (view.autonomy?.[projectId]?.stopReason) break; await new Promise(resolve => setTimeout(resolve, 100)); }
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId);
    const story = JSON.stringify(timeline.map(event => event.type + ' ' + event.summary), null, 1);
    assert.equal(view.autonomy[projectId].stopReason, 'GOAL_REACHED', story);
    // The lead sized the work by the sheet the goal names.
    assert.match(asks.find(info => /Your crew can work in parallel/.test(info.question)).question, /Document the goal names, jobs\/42-phase4\.md/);
    // Every module is on disk, wired together.
    for (const name of scenario.modules) assert.ok(fs.existsSync(path.join(folder, 'src', name + '.cs')) && fs.readFileSync(path.join(folder, 'tests', name + '.tests.cs'), 'utf8').includes('wired'), name);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(folder, scenario.integration), 'utf8')), scenario.modules);
    // The step that stopped part way went back to the lead, and only the rest was split again.
    assert.equal(splits, 2, story);
    assert.equal(runnerTries, 2);
    // The ports were spread over the companies, Grok included although its usage could not be read.
    const companies = new Set(launches.filter(launch => /^Port /.test(launch.job.title)).map(launch => launch.worker.provider));
    assert.deepEqual([...companies].sort(), ['antigravity', 'codex', 'grok'], story);
    // Nothing failed on Planwharf's side.
    assert.ok(!timeline.some(event => /AUTONOMY_ERROR|met an error|outside its scope|NO_PROGRESS/.test(event.summary)), story);
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
