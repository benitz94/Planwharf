// Real browser acceptance check, using Edge's DevTools protocol without dependencies.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Plan = require('./plan.js');
const { createServer } = require('./server.cjs');
const { spawn } = require('node:child_process');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'grill-browser-' + Date.now());
fs.mkdirSync(output, { recursive: true });
const profile = path.join(output, `edge-profile-${Date.now()}`);
const planFile = path.join(output, `browser-plan-${Date.now()}.json`);
const basePlan = Plan.example(); basePlan.tasks = []; basePlan.project = { id: 'existing', name: 'Existing project' };
const isolatedPlan = Plan.createProject(basePlan, { name: 'New imported project', direction: 'Build a small scheduling tool.' });
const targetId = isolatedPlan.projects.at(-1).project.id;
const calls = [], launches = [];
fs.writeFileSync(planFile, JSON.stringify(isolatedPlan));
const server = createServer({ planFile, refreshModels: false, fetchPlans: null,
  models: [{ id: 'fixture-lead', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'default' }],
  inspectFolder: () => ({ git: false, names: [], dirty: [] }), readRepository: () => ({ local: {}, remote: {}, comparison: {} }),
  askModel: info => { calls.push(info); return { answer: 'Q1: Who will use the scheduling tool? Recommended: a small volunteer team.' }; },
  spawnWorker: info => { launches.push(info); throw new Error('Grill browser test must never launch a worker'); } });
let browser;
let ws;
let command;
let browserError;
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const appUrl = `http://127.0.0.1:${server.address().port}`;
  browser = spawn('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', ['--headless=new', '--disable-gpu', '--disable-extensions', '--disable-background-networking', ...(process.env.PLANWHARF_TEST_NO_SANDBOX === '1' ? ['--no-sandbox'] : []), '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${profile}`, appUrl], { windowsHide: true, stdio: 'ignore' });
  browser.on('error', error => { browserError = error; });
  let port;
  for (let i = 0; i < 100; i++) {
    if (browserError) throw browserError;
    try { port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]); if (port) break; } catch (_) {}
    await pause(100);
  }
  assert.ok(port, 'Edge DevTools did not start');
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = pages.find(target => target.type === 'page');
  assert.ok(page, 'Edge page missing');
  ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
  let sequence = 0;
  const waiting = new Map();
  ws.addEventListener('message', event => {
    const response = JSON.parse(event.data);
    const pending = waiting.get(response.id);
    if (pending) { clearTimeout(pending.timer); waiting.delete(response.id); response.error ? pending.reject(new Error(response.error.message)) : pending.resolve(response.result); }
  });
  command = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`Timeout: ${method}`)); }, 30000);
    waiting.set(id, { resolve, reject, timer });
    ws.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  await command('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1100, deviceScaleFactor: 1, mobile: false });
  await fetch(appUrl + '/api/orchestrator/lead', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: 'fixture-lead' }) });
  await command('Page.navigate', { url: appUrl + '/advanced#orchestrator' });
  for (let i = 0; i < 100 && !await evaluate("Boolean(document.getElementById('dashboard-orch-project')?.options.length > 1)"); i++) await pause(25);
  await evaluate(`(() => { const select = document.getElementById('dashboard-orch-project'); select.value = ${JSON.stringify(targetId)}; select.dispatchEvent(new Event('change')); })()`);
  for (let i = 0; i < 100 && !await evaluate(`Boolean(document.querySelector('#dashboard-orchestrator [data-grill-start="${targetId}"][data-grill-kind="me"]'))`); i++) await pause(25);
  await evaluate(`document.querySelector('#dashboard-orchestrator [data-grill-start="${targetId}"][data-grill-kind="me"]').click()`);
  for (let i = 0; i < 100 && !await evaluate("document.getElementById('dashboard-orch-ask-thread').textContent.includes('Q1: Who will use')"); i++) await pause(25);
  assert.ok(await evaluate("document.getElementById('dashboard-orch-ask-thread').textContent.includes('Q1: Who will use')"));
  assert.equal(calls.length, 1); assert.equal(calls[0].projectId, targetId); assert.equal(calls[0].grillKind, 'me'); assert.equal(calls[0].allowActions, false);
  assert.match(calls[0].extraPrompt, /ACTIVE GRILL ME SESSION/);
  assert.equal(await evaluate("document.getElementById('grill-dialog').open"), false);
  assert.equal(await evaluate("document.getElementById('auto-start-dialog').open"), false);
  assert.equal(await evaluate("document.getElementById('orchestrator-view').hidden"), false);
  await evaluate("document.getElementById('dashboard-orch-ask-input').value = 'Implement only the scheduling view for a volunteer team.'; document.getElementById('dashboard-orch-ask-submit').click()");
  for (let i = 0; i < 100 && calls.length < 2; i++) await pause(25);
  assert.equal(calls.length, 2); assert.equal(calls[1].grillKind, 'me'); assert.equal(launches.length, 0);
  const saved = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  for (let i = 0; i < saved.projects.length; i++) {
    assert.deepEqual(saved.projects[i].tasks, isolatedPlan.projects[i].tasks);
    assert.deepEqual(saved.projects[i].revisions, isolatedPlan.projects[i].revisions);
  }
  await pause(100);
  const screenshot = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'grill-in-chat.png'), Buffer.from(screenshot.data, 'base64'));
  const report = 'PASS: new project without a folder, Show Orchestrator Grill click, real lead fixture turn with Grill me instructions, follow-up in chat, no modal/Auto start or worker dispatch.\n';
  fs.writeFileSync(path.join(output, 'report.txt'), report); console.log(report.trim());
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (command) { try { await command('Browser.close'); } catch (_) {} }
  if (ws) ws.close();
  if (browser) browser.kill();
  if (server.listening) await new Promise(resolve => server.close(resolve));
  fs.rmSync(planFile, { force: true });
});
