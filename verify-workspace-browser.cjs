'use strict';
// Installed Chromium browser + actual isolated backend and persisted protocol fixtures. No live provider calls.
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const { spawn } = require('node:child_process');
const sourceRoot = path.resolve(process.env.PLANWHARF_TEST_SOURCE_ROOT || __dirname);
const Plan = require(path.join(sourceRoot, 'plan.js'));
const { createServer } = require(path.join(sourceRoot, 'server.cjs'));
const chrome = process.env.PLANWHARF_TEST_BROWSER;
assert.ok(chrome && path.isAbsolute(chrome) && /(?:^|[\\/])(?:chrome|msedge)\.exe$/i.test(chrome) && fs.statSync(chrome).isFile(), 'Set PLANWHARF_TEST_BROWSER to the explicit installed Chrome or Edge executable.');
assert.notEqual(process.env.PLANWHARF_TEST_NO_SANDBOX, '1', 'Browser sandbox must stay enabled.');
const output = path.resolve(process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'workspace-' + Date.now()));
fs.mkdirSync(output, { recursive: true });
const profile = path.join(output, 'browser-profile-' + Date.now()), attemptsDir = path.join(output, 'attempts'); fs.mkdirSync(attemptsDir, { recursive: true });
const plan = { schema: 2, projects: ['workspace-alpha', 'workspace-beta'].map(id => { const item = Plan.example(); item.tasks = []; item.project = { id, name: id }; return item; }) };
const planFile = path.join(output, 'notebook.json'); fs.writeFileSync(planFile, JSON.stringify(plan));
const execution = { megaTasks: [], subTasks: [], results: [], checkpoints: [], timeline: [], observations: {} };
const state = { projects: {}, asks: [], workerHistory: [], execution, orchestratorId: 'worker-fixture', selectedProjectId: 'workspace-alpha' };
for (const id of ['workspace-alpha', 'workspace-beta']) {
  const mega = 'mega-' + id; execution.megaTasks.push({ id: mega, projectId: id, taskId: 'job-' + id, status: 'COMPLETE' });
  const dispatch = [];
  for (const role of ['worker', 'reviewer']) {
    const attempt = id + '-' + role, sub = 'sub-' + attempt, logPath = path.join(attemptsDir, attempt + '.log');
    execution.subTasks.push({ id: sub, megaTaskId: mega, type: role === 'reviewer' ? 'AUDIT' : 'IMPLEMENTATION', title: 'Isolated ' + role + ' fixture', status: role === 'worker' ? 'COMPLETE' : 'PENDING', resultId: role === 'worker' ? 'R-' + attempt : null, completedAt: role === 'worker' ? '2026-10-16T10:00:00Z' : null });
    const log = [JSON.stringify({ type: 'item.started', item: { type: 'command_execution', command: 'npm test' } }), JSON.stringify({ type: 'item.completed', item: { type: 'command_execution', command: 'npm test', exit_code: 0, aggregated_output: 'PRIVATE_SOURCE_NOT_FOR_UI' } }), JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: 'Fixture worker reported 5 passed api_key=PRIVATE_TOKEN_NOT_FOR_UI' } })].join('\n');
    fs.writeFileSync(logPath, log);
    dispatch.push({ modelId: 'worker-fixture', provider: 'codex', cli: 'gpt-6-luna', attemptId: attempt, subTaskId: sub, megaTaskId: mega, label: 'Recorded ' + role + ' fixture', slice: 'Isolated ' + role, logPath, pid: role === 'worker' ? 88001 : 88002, alive: false, startedAt: '2026-10-16T09:00:00Z', sessionId: 'recorded-fixture-session' });
    if (role === 'worker') execution.results.push({ resultId: 'R-' + attempt, attemptId: attempt, megaTaskId: mega, subTaskId: sub, assignedModelId: 'worker-fixture', provider: 'codex', cli: 'gpt-6-luna', status: 'COMPLETE', exitCode: 0, summary: 'Persisted structured fixture result', filesChanged: [], tests: [], blockers: [], nextAction: '', createdAt: '2026-10-16T10:00:00Z' });
  }
  state.projects[id] = { status: 'idle', megaTaskId: mega, jobId: 'job-' + id, dispatch };
  state.workerHistory.push({ id, status: 'checkpointed', dispatch: structuredClone(dispatch) });
  state.asks.push({ projectId: id, question: id + ' private discussion', answer: id + ' recorded answer', at: '2026-10-16T10:00:00Z', sessionId: 'recorded-fixture-session' });
}
if (process.env.PLANWHARF_EXPECT_ACTIVITY_PAGING === '1') {
  for (let index = 0; index < 30; index++) {
    const attemptId = 'archive-' + index, subTaskId = 'sub-' + attemptId, megaTaskId = 'mega-workspace-alpha';
    const logPath = path.join(attemptsDir, attemptId + '.log'); fs.writeFileSync(logPath, 'Recorded archive fixture ' + index);
    execution.subTasks.push({ id: subTaskId, megaTaskId, type: 'IMPLEMENTATION', status: 'PENDING', title: 'Archived fixture ' + index });
    state.workerHistory.unshift({ id: 'workspace-alpha', status: 'checkpointed', dispatch: [{ attemptId, subTaskId, megaTaskId, provider: 'codex', cli: 'gpt-6-luna', modelId: 'worker-fixture', startedAt: new Date(Date.UTC(2026, 9, 15, 9, index)).toISOString(), logPath, alive: false }] });
  }
}
fs.writeFileSync(path.join(attemptsDir, 'orchestrator.json'), JSON.stringify(state));
const models = [
  { id: 'worker-fixture', label: 'Fixture worker / reviewer', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'premium', limit: 200, roles: { orchestrator: true, worker: true, reviewer: true, supervisor: false } },
  { id: 'supervisor-fixture', label: 'Fixture Supervisor only', provider: 'api', cli: 'exact-fixture-supervisor', enabled: true, available: true, limit: 200, roles: { orchestrator: false, worker: false, reviewer: false, supervisor: true } }
];
let aiCalls = 0, launches = 0;
const server = createServer({ planFile, attemptsDir, models, refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 60000,
  inspectFolder: () => ({ git: false, names: [], fileCount: 0 }), isProcessAlive: () => false,
  spawnWorker: () => { launches++; throw Error('Browser fixture cannot dispatch'); }, killWorker: () => { throw Error('Browser fixture cannot terminate workers'); },
  askModel: async info => { assert.equal(info.purpose, 'supervisor'); assert.equal(info.model.id, 'supervisor-fixture'); assert.equal(info.allowActions, false); aiCalls++; return { answer: 'Fixture Supervisor advice from isolated injected provider.', provider: 'api', model: 'exact-fixture-supervisor' }; }
});
let browser, socket, command, evaluate, base;
let browserStage = 'not-started';
const browserDiagnostics = [];
const browserStderr = fs.openSync(path.join(output, 'browser-stderr.log'), 'a');
const errors = [], networkErrors = [], checks = [];
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(check, label, timeout = 15000) { const end = Date.now() + timeout; while (Date.now() < end) { if (await check()) return; await delay(100); } throw Error('Timed out: ' + label); }
async function click(selector) {
  const box = await evaluate(`(() => {const e=document.querySelector(${JSON.stringify(selector)});if(!e)throw Error('Missing selector');e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()`);
  await command('Input.dispatchMouseEvent', { type: 'mousePressed', ...box, button: 'left', clickCount: 1 }); await command('Input.dispatchMouseEvent', { type: 'mouseReleased', ...box, button: 'left', clickCount: 1 });
}
async function select(selector, value) { await evaluate(`(() => {const e=document.querySelector(${JSON.stringify(selector)});e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('change',{bubbles:true}));})()`); }
async function type(selector, value) { await click(selector); await evaluate(`document.querySelector(${JSON.stringify(selector)}).select()`); await command('Input.insertText', { text: value }); }
async function shot(name, selector) {
  if (selector) await evaluate(`document.querySelector(${JSON.stringify(selector)}).scrollIntoView({block:'start'})`);
  else await evaluate('window.scrollTo(0,0)');
  assert.equal(await evaluate('document.documentElement.scrollWidth <= innerWidth + 2'), true, 'No horizontal page overflow: ' + name);
  const data = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false }); fs.writeFileSync(path.join(output, name + '.png'), Buffer.from(data.data, 'base64'));
}
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = 'http://127.0.0.1:' + server.address().port;
  const snapshot = await (await fetch(base + '/api/orchestrator?readOnly=1')).json(); assert.ok(snapshot.projects[0].activitySessions.length >= 2);
  if (process.env.PLANWHARF_EXPECT_TECHNICAL_EVIDENCE === '1') assert.ok(snapshot.projects[0].activitySessions.some(session => session.technicalEvidence?.entries?.some(entry => entry.command === 'npm test')), 'Backend must expose actual source command evidence');
  if (process.env.PLANWHARF_EXPECT_ACTIVITY_PAGING === '1') assert.ok(snapshot.projects[0].activitySessionPagination?.available === true && snapshot.projects[0].activitySessionPagination.total > 24, 'Backend must advertise real retained paging');
  browserStage = 'launch';
  browser = spawn(chrome, ['--headless=new', '--disable-gpu', '--disable-extensions', '--disable-background-networking', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', browserStderr] });
  let browserError; browser.on('error', error => { browserError = error; }); let port;
  await until(() => { if (browserError) throw browserError; try { port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]); return port > 0; } catch { return false; } }, 'Chrome DevTools');
  const pages = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json(); const page = pages.find(page => page.type === 'page');
  socket = new WebSocket(page.webSocketDebuggerUrl); await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  let sequence = 0; const pending = new Map();
  socket.addEventListener('message', message => {
    const data = JSON.parse(message.data);
    if (data.method === 'Inspector.targetCrashed') browserDiagnostics.push({ stage: browserStage, event: data.method });
    if (data.method === 'Runtime.exceptionThrown') errors.push(data.params.exceptionDetails);
    if (data.method === 'Network.loadingFailed' && !data.params.canceled) networkErrors.push(data.params);
    if (data.method === 'Network.responseReceived' && data.params.response.status >= 400 && !data.params.response.url.endsWith('/favicon.ico')) networkErrors.push({ url: data.params.response.url, status: data.params.response.status });
    const item = pending.get(data.id); if (item) { clearTimeout(item.timer); pending.delete(data.id); data.error ? item.reject(Error(data.error.message)) : item.resolve(data.result); }
  });
  command = (method, params = {}) => new Promise((resolve, reject) => { const id = ++sequence; const timer = setTimeout(() => { pending.delete(id); reject(Error('DevTools timeout: ' + method)); }, 20000); pending.set(id, { resolve, reject, timer }); socket.send(JSON.stringify({ id, method, params })); });
  evaluate = async expression => { const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true }); if (result.exceptionDetails) throw Error(JSON.stringify(result.exceptionDetails)); return result.result.value; };
  browserStage = 'blank-page-smoke';
  await command('Inspector.enable');
  assert.equal(await evaluate('1 + 1'), 2, 'Blank page renderer must respond before application navigation');
  await command('Runtime.enable'); await command('Network.enable'); await command('Page.enable');
  await command('Emulation.setDeviceMetricsOverride', { width: 1480, height: 1050, deviceScaleFactor: 1, mobile: false });
  browserStage = 'application-navigation';
  await command('Page.navigate', { url: base + '/advanced#orchestrator' });
  await until(() => evaluate("document.body.dataset.orchReady==='1' && document.querySelector('[data-workspace-session]')?.options.length>=2"), 'workspace real sessions');
  await click('[data-workspace-panel=sessions]');
  assert.equal(await evaluate("document.querySelector('[data-activity-mode=friendly]').getAttribute('aria-pressed')"), 'true');
  assert.match(await evaluate("document.querySelector('[data-workspace-activity]').textContent"), /Observed event|Worker reported/);
  assert.doesNotMatch(await evaluate("document.querySelector('[data-workspace-activity]').textContent"), /PRIVATE_SOURCE_NOT|PRIVATE_TOKEN_NOT/);
  await shot('workspace-friendly-dark');
  const reviewer = await evaluate("[...document.querySelector('[data-workspace-session]').options].find(option=>option.textContent.startsWith('reviewer')).value");
  await select('[data-workspace-session]', reviewer); await click('[data-activity-mode=technical]');
  assert.match(await evaluate("document.querySelector('[data-workspace-activity]').textContent"), /Result not validated|item-events/);
  assert.doesNotMatch(await evaluate("document.querySelector('[data-workspace-activity]').textContent"), /Completed · backend-validated/);
  if (process.env.PLANWHARF_EXPECT_TECHNICAL_EVIDENCE === '1') {
    assert.match(await evaluate("document.querySelector('[data-workspace-original]').textContent"), /Original command: npm test/);
    assert.doesNotMatch(await evaluate("document.querySelector('[data-workspace-original]').textContent"), /PRIVATE_TOKEN_NOT_FOR_UI/);
    checks.push('source-specific original sanitized command evidence');
  }
  if (process.env.PLANWHARF_EXPECT_ACTIVITY_PAGING === '1') {
    await click('[data-history-next]');
    await until(() => evaluate("document.querySelector('[data-history-label]').textContent.startsWith('Assignments 25') && document.querySelector('[data-workspace-session]').value!=='' && document.querySelector('[data-workspace-activity]').textContent.includes('archive-')"), 'retained older page');
    assert.match(await evaluate("document.querySelector('[data-workspace-activity]').textContent"), /archive-/);
    await select('#project-select', 'workspace-beta');
    await until(() => evaluate("document.getElementById('dashboard-orch-project').value==='workspace-beta'"), 'paged beta isolation');
    assert.doesNotMatch(await evaluate("document.querySelector('[data-workspace-activity]').textContent"), /archive-/);
    await select('#project-select', 'workspace-alpha');
    await until(() => evaluate("document.querySelector('[data-history-label]').textContent.startsWith('Assignments 25')"), 'per-project history page restoration');
    await click('[data-history-previous]');
    await until(() => evaluate("document.querySelector('[data-history-label]').textContent.startsWith('Assignments 1')"), 'current page restoration');
    await select('[data-workspace-session]', reviewer);
    checks.push('actual retained history beyond 24 and per-project page restoration');
  }
  await type('#dashboard-orch-ask-input', 'Alpha draft must survive panel and project changes.');
  await click('[data-workspace-panel=supervisor]');
  assert.equal(await evaluate("document.getElementById('orchestrator-view').hidden"), false);
  assert.equal(await evaluate("document.getElementById('dashboard-orch-ask-input').value"), 'Alpha draft must survive panel and project changes.');
  await type('#supervisor-question', 'Alpha Supervisor draft');
  await select('#project-select', 'workspace-beta');
  await until(() => evaluate("document.getElementById('dashboard-orch-project').value==='workspace-beta'"), 'beta recipient');
  assert.equal(await evaluate("document.getElementById('dashboard-orch-ask-input').value"), '');
  assert.doesNotMatch(await evaluate("document.getElementById('dashboard-orch-ask-thread').textContent"), /workspace-alpha/);
  await type('#dashboard-orch-ask-input', 'Beta independent draft');
  await select('#project-select', 'workspace-alpha');
  await until(() => evaluate("document.getElementById('dashboard-orch-ask-input').value.startsWith('Alpha draft')"), 'alpha draft restoration');
  assert.equal(await evaluate("document.getElementById('supervisor-question').value"), 'Alpha Supervisor draft');
  await click('[data-workspace-panel=sessions]'); assert.equal(await evaluate("document.querySelector('[data-workspace-session]').value"), reviewer);
  assert.equal(await evaluate("document.querySelector('[data-activity-mode=technical]').getAttribute('aria-pressed')"), 'true');
  await shot('workspace-technical-dark');
  await click('[data-workspace-focus]'); assert.equal(await evaluate("document.querySelector('.orch-chat').checkVisibility()"), false);
  await click('[data-workspace-focus]'); assert.equal(await evaluate("document.querySelector('.orch-chat').checkVisibility()"), true);
  const splitter = '.orch-shell > .layout-splitter'; await click(splitter); const before = await evaluate("document.querySelector('.orch-shell > .layout-splitter').getAttribute('aria-valuenow')");
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 }); await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39 });
  assert.notEqual(await evaluate("document.querySelector('.orch-shell > .layout-splitter').getAttribute('aria-valuenow')"), before);
  await click('#theme-toggle'); await shot('workspace-technical-light'); assert.equal(await evaluate("document.documentElement.dataset.theme"), 'light');
  await click('[data-workspace-panel=supervisor]');
  await type('#supervisor-question', 'What is happening?'); await click('#supervisor-ask-form button');
  await until(() => evaluate("document.getElementById('supervisor-thread').textContent.includes('Live evidence')"), 'model-free Core'); assert.equal(aiCalls, 0);
  await evaluate("document.querySelector('.supervisor-preferences').open=true");
  await type('#supervisor-settings-form [name=name]', 'Captain <Nemo> & Co');
  await select('#supervisor-settings-form [name=modelId]', 'supervisor-fixture'); await click('#supervisor-settings-form button[type=submit]');
  await until(() => evaluate("document.getElementById('supervisor-title').textContent==='Captain <Nemo> & Co'"), 'inert Supervisor name');
  assert.equal(await evaluate("document.getElementById('supervisor-title').children.length"), 0);
  await type('#supervisor-question', 'What do you suggest?'); await click('#supervisor-ask-form button');
  await until(() => evaluate("document.getElementById('supervisor-thread').textContent.includes('Fixture Supervisor advice')"), 'Supervisor-only advisory fixture'); assert.equal(aiCalls, 1);
  await shot('workspace-supervisor-light');
  await evaluate("document.querySelector('.orch-board').scrollTop=0"); await shot('workspace-supervisor-overview-light');
  await type('#supervisor-settings-form [name=name]', '   '); await select('#supervisor-settings-form [name=modelId]', ''); await click('#supervisor-settings-form button[type=submit]');
  await until(() => evaluate("document.getElementById('supervisor-title').textContent==='Supervisor'"), 'exact blank fallback');
  await command('Page.reload'); await until(() => evaluate("document.getElementById('dashboard-orch-ask-input')?.value.startsWith('Alpha draft')"), 'reload draft persistence');
  assert.equal(await evaluate("document.querySelector('[data-workspace-panel=supervisor]').getAttribute('aria-pressed')"), 'true');
  await evaluate("location.hash='#supervisor'"); await until(() => evaluate("!document.getElementById('orchestrator-view').hidden && !document.getElementById('supervisor-view').hidden"), 'legacy Supervisor hash');
  await click('[data-workspace-panel=sessions]');
  await command('Emulation.setDeviceMetricsOverride', { width: 430, height: 900, deviceScaleFactor: 1, mobile: true }); await delay(200);
  assert.equal(await evaluate('document.documentElement.scrollWidth <= innerWidth + 2'), true, 'Mobile has no horizontal page overflow'); await shot('workspace-mobile-light', '.workspace-nav');
  assert.equal(launches, 0); assert.deepEqual(errors, []); assert.deepEqual(networkErrors, []);
  checks.push('actual backend log-to-session-to-DOM', 'Friendly/Technical provenance', 'two-project drafts and sessions', 'shared conversation retained', 'focus restore', 'keyboard resize', 'light/dark/mobile', 'Core zero calls', 'Supervisor-only injected advisory', 'blank/inert names', 'reload/hash compatibility', 'no JS/HTTP errors');
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ status: 'PASS', sourceRoot, browser: chrome, browserDiagnostics, sandboxEnabled: true, fixturesOnly: true, liveProviderCalls: 0, injectedSupervisorCalls: aiCalls, workerLaunches: launches, checks, errors, networkErrors }, null, 2));
  console.log('PASS: ' + path.basename(chrome) + ' workspace browser checks; actual isolated backend, recorded protocol fixtures and injected Supervisor only. Evidence: ' + output);
})().catch(error => { fs.writeFileSync(path.join(output, 'failure.json'), JSON.stringify({ error: error.stack, browser: chrome, stage: browserStage, browserDiagnostics, errors, networkErrors }, null, 2)); console.error(error); process.exitCode = 1; }).finally(async () => {
  if (command) { try { await command('Browser.close'); } catch {} }
  socket?.close();
  if (browser && browser.exitCode === null) { await delay(300); if (browser.exitCode === null) browser.kill(); }
  if (server.listening) await new Promise(resolve => server.close(resolve));
  fs.closeSync(browserStderr);
});
