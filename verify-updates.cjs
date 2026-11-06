// Real browser acceptance check, using Edge's DevTools protocol without dependencies.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Plan = require('./plan.js');
const { createServer } = require('./server.cjs');
const { spawn } = require('node:child_process');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'update-browser-' + Date.now());
fs.mkdirSync(output, { recursive: true });
const profile = path.join(output, `edge-profile-${Date.now()}`);
const planFile = path.join(output, `browser-plan-${Date.now()}.json`);
const isolatedPlan = { schema: 2, projects: ['one', 'two'].map(id => {
  const project = Plan.example(); project.project = { id, name: 'Isolated ' + id };
  project.tasks = [{ id: 'task-' + id, projectId: id, revision_id: 2, title: 'Isolated active task', detail: 'No providers are launched', status: 'running' }]; return project;
}) };
const calls = [];
const updateState = { state: 'available', currentVersion: 'v1.0.0', latestVersion: 'v1.0.1', available: true, canInstall: true, releaseUrl: 'https://github.com/benitz94/Planwharf/releases/tag/v1.0.1' };
const updateManager = { snapshot: () => updateState, check: async () => updateState, install: async confirm => { calls.push(confirm); return confirm ? { ok: true, restarting: true } : { requiresConfirmation: true }; }, busy: false };
fs.writeFileSync(planFile, JSON.stringify(isolatedPlan));
const server = createServer({ planFile, models: [], updateManager, fetchPlans: async () => [] });
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
  await command('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await command('Page.navigate', { url: appUrl });
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.querySelector('.app-update-controls button:last-child')?.textContent.includes('v1.0.1')")) break;
    await pause(50);
  }
  assert.equal(calls.length, 0, 'Startup status never installs an update');
  await evaluate("document.querySelector('.app-update-controls button:last-child').click()");
  for (let i = 0; i < 100 && !await evaluate("Boolean(document.querySelector('.app-update-dialog[open]'))"); i++) await pause(25);
  const warning = 'One or more tasks are still running. If you proceed, Planwharf will save a checkpoint for each unfinished task before updating and restarting. Are you sure you want to update? You can always do this later.';
  assert.equal(await evaluate("document.querySelector('.app-update-dialog > p').textContent"), warning);
  assert.equal(await evaluate("document.querySelector('.app-update-dialog .primary').textContent"), 'Update anyway');
  assert.equal(await evaluate("document.querySelector('.app-update-dialog .secondary').textContent"), "No, I'll do it later");
  const screenshot = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'running-task-warning.png'), Buffer.from(screenshot.data, 'base64'));
  await evaluate("document.querySelector('.app-update-dialog .secondary').click()"); await pause(50);
  assert.equal(calls.length, 0, 'Later closes the dialog without checkpointing or installing');
  assert.equal(await evaluate("document.querySelector('.app-update-dialog') === null"), true);
  await evaluate("document.querySelector('.app-update-controls button:last-child').click()");
  for (let i = 0; i < 100 && !await evaluate("Boolean(document.querySelector('.app-update-dialog[open]'))"); i++) await pause(25);
  await evaluate("document.querySelector('.app-update-dialog .primary').click()"); await pause(150);
  assert.deepEqual(calls, [true], 'Explicit confirmation is forwarded to the update API');
  assert.ok(await evaluate("document.querySelector('.app-update-dialog > p').textContent.includes('updating and restarting')"));
  const { projects } = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  assert.ok(projects.every(project => project.tasks[0].status === 'running'), 'No real workers or updates run in the browser fixture');
  const report = 'PASS: real Edge release notice, exact running-task warning, Update anyway, Later without side effects, and explicit update consent.\n';
  fs.writeFileSync(path.join(output, 'report.txt'), report); console.log(report.trim());
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (command) { try { await command('Browser.close'); } catch (_) {} }
  if (ws) ws.close();
  if (browser) browser.kill();
  if (server.listening) await new Promise(resolve => server.close(resolve));
  fs.rmSync(planFile, { force: true });
});
