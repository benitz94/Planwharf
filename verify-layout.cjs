// Real Chromium browser acceptance check, using DevTools without dependencies.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Plan = require('./plan.js');
const { createServer } = require('./server.cjs');
const { spawn } = require('node:child_process');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const configuredBrowser = process.env.PLANWHARF_TEST_BROWSER;
assert.ok(configuredBrowser && configuredBrowser.trim() === configuredBrowser && !/[\x00-\x1f]/.test(configuredBrowser) && path.isAbsolute(configuredBrowser), 'Set PLANWHARF_TEST_BROWSER to an absolute Chromium executable path for this DevTools harness.');
assert.ok(fs.existsSync(configuredBrowser) && fs.statSync(configuredBrowser).isFile(), 'PLANWHARF_TEST_BROWSER must point to an existing executable file.');
const browserExecutable = configuredBrowser;
const output = process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'layout-browser-' + Date.now());
fs.mkdirSync(output, { recursive: true });
const profile = path.join(output, `chromium-profile-${Date.now()}`);
const planFile = path.join(output, `browser-plan-${Date.now()}.json`);
const isolatedPlan = Plan.example(); isolatedPlan.tasks = []; isolatedPlan.project = { id: 'layout-probe', name: 'Layout probe' };
fs.writeFileSync(planFile, JSON.stringify(isolatedPlan));
const server = createServer({ planFile, models: [], fetchPlans: async () => [] });
let browser;
let ws;
let command;
let browserError;
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const appUrl = `http://127.0.0.1:${server.address().port}`;
  browser = spawn(browserExecutable, ['--headless=new', '--disable-gpu', '--disable-extensions', '--disable-background-networking', ...(process.env.PLANWHARF_TEST_NO_SANDBOX === '1' ? ['--no-sandbox'] : []), '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${profile}`, appUrl], { windowsHide: true, stdio: 'ignore' });
  browser.on('error', error => { browserError = error; });
  let port;
  for (let i = 0; i < 100; i++) {
    if (browserError) throw browserError;
    try { port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]); if (port) break; } catch (_) {}
    await pause(100);
  }
  assert.ok(port, `Chromium DevTools did not start: ${browserExecutable}`);
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  const page = pages.find(target => target.type === 'page');
  assert.ok(page, `Chromium page missing: ${browserExecutable}`);
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
  const viewport = async (width, height = 1000) => {
    await command('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 600 });
    await pause(100);
  };
  await viewport(1440);
  await command('Page.navigate', { url: appUrl + '/advanced#orchestrator' });
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.querySelectorAll('.layout-splitter').length === 2 && !document.getElementById('orchestrator-view').hidden")) break;
    await pause(50);
  }
  const measure = () => evaluate(`(() => {
    const rail = document.querySelector('.rail').getBoundingClientRect();
    const chat = document.querySelector('#orchestrator-view .orch-chat').getBoundingClientRect();
    const board = document.querySelector('#orchestrator-view .orch-board').getBoundingClientRect();
    const shell = document.getElementById('orchestrator-view');
    return { rail: rail.width, chat: chat.width, board: board.width, stacked: shell.dataset.layoutStacked,
      boardTop: board.top, chatBottom: chat.bottom, overflow: document.documentElement.scrollWidth > innerWidth,
      handles: [...document.querySelectorAll('.layout-splitter')].map(node => ({ display: getComputedStyle(node).display, tab: node.tabIndex })) };
  })()`);
  const initial = await measure();
  assert.equal(initial.stacked, 'false'); assert.ok(!initial.overflow);
  const drag = async (selector, delta, cancel = false) => {
    const point = await evaluate(`(() => { const box = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect(); return { x: box.x + box.width/2, y: Math.max(box.top + 20, Math.min(innerHeight - 30, box.top + box.height/2)) }; })()`);
    await command('Input.dispatchMouseEvent', { type: 'mousePressed', x: point.x, y: point.y, button: 'left', clickCount: 1 });
    await command('Input.dispatchMouseEvent', { type: 'mouseMoved', x: point.x + delta, y: point.y, button: 'left', buttons: 1 });
    if (cancel) {
      await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
      await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    }
    await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x: point.x + delta, y: point.y, button: 'left', clickCount: 1 });
    await pause(100);
  };
  await drag('.app > .layout-splitter', 80);
  const widerRail = await measure(); assert.ok(widerRail.rail > initial.rail + 60);
  await drag('#orchestrator-view > .layout-splitter', 90);
  const widerChat = await measure(); assert.ok(widerChat.chat > widerRail.chat + 70);
  assert.ok(widerChat.board >= 320); assert.ok(!widerChat.overflow);
  const desktopScreenshot = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'desktop-layout.png'), Buffer.from(desktopScreenshot.data, 'base64'));
  const saved = await evaluate("localStorage.getItem('planwharf_layout_v1')");
  await command('Page.reload'); await pause(500);
  const reloaded = await measure(); assert.ok(Math.abs(reloaded.chat - widerChat.chat) < 2); assert.ok(Math.abs(reloaded.rail - widerChat.rail) < 2);
  await drag('#orchestrator-view > .layout-splitter', -60, true);
  assert.equal(await evaluate("localStorage.getItem('planwharf_layout_v1')"), saved, 'Escape cancels the drag');
  assert.ok(Math.abs((await measure()).chat - reloaded.chat) < 2);
  await evaluate("document.querySelector('#orchestrator-view > .layout-splitter').focus()");
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowLeft', code: 'ArrowLeft', windowsVirtualKeyCode: 37 });
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowLeft', code: 'ArrowLeft', windowsVirtualKeyCode: 37 });
  assert.ok((await measure()).chat < reloaded.chat - 15);
  const dragHeight = async (selector, delta, cancel = false) => {
    const point = await evaluate(`(() => { const handle = document.querySelector(${JSON.stringify(selector)}); handle.scrollIntoView({ block: 'center' }); const box = handle.getBoundingClientRect(); return { x: box.x + box.width/2, y: box.y + box.height/2 }; })()`);
    await command('Input.dispatchMouseEvent', { type: 'mousePressed', x: point.x, y: point.y, button: 'left', clickCount: 1 });
    await command('Input.dispatchMouseEvent', { type: 'mouseMoved', x: point.x, y: point.y + delta, button: 'left', buttons: 1 });
    if (cancel) await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27 });
    await command('Input.dispatchMouseEvent', { type: 'mouseReleased', x: point.x, y: point.y + delta, button: 'left', clickCount: 1 });
    await pause(100);
  };
  const historyHeight = () => evaluate("document.getElementById('dashboard-orch-ask-thread').getBoundingClientRect().height");
  const historyBefore = await historyHeight();
  await dragHeight('.orch-chat > .layout-height-splitter', -70);
  const historyAfter = await historyHeight(); assert.ok(historyAfter < historyBefore - 50);
  const goldHandle = '[aria-label="Resize Gold model section height"]';
  const goldHeight = () => evaluate("document.querySelector('[aria-label=\"Gold model\"] > .layout-section-content').getBoundingClientRect().height");
  const goldBefore = await goldHeight();
  await dragHeight(goldHandle, 80);
  const goldAfter = await goldHeight(); assert.ok(goldAfter > goldBefore + 60);
  const heightPreferences = await evaluate("localStorage.getItem('planwharf_layout_heights_v1')");
  await pause(4300); // The dashboard polls every four seconds and replaces its sections.
  assert.ok(Math.abs(await goldHeight() - goldAfter) < 2, 'Section heights survive dashboard refresh');
  await command('Page.reload'); await pause(500);
  assert.ok(Math.abs(await historyHeight() - historyAfter) < 2, 'Conversation height survives reload');
  assert.ok(Math.abs(await goldHeight() - goldAfter) < 2, 'Dashboard height survives reload');
  await dragHeight(goldHandle, 60, true);
  assert.equal(await evaluate("localStorage.getItem('planwharf_layout_heights_v1')"), heightPreferences, 'Escape cancels height edits');
  await evaluate("document.querySelector('[aria-label=\"Resize Gold model section height\"]').focus()");
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'ArrowUp', code: 'ArrowUp', windowsVirtualKeyCode: 38 });
  await command('Input.dispatchKeyEvent', { type: 'keyUp', key: 'ArrowUp', code: 'ArrowUp', windowsVirtualKeyCode: 38 });
  assert.ok(await goldHeight() < goldAfter - 15, 'Keyboard adjusts height');
  await viewport(1000);
  const narrowDesktop = await measure(); assert.equal(narrowDesktop.stacked, 'true'); assert.ok(!narrowDesktop.overflow);
  await viewport(390, 844);
  assert.ok(await evaluate("[...document.querySelectorAll('.layout-height-splitter')].every(handle => getComputedStyle(handle).display === 'none')"));
  const mobile = await measure(); assert.ok(mobile.handles.every(handle => handle.display === 'none' && handle.tab === -1));
  assert.ok(mobile.boardTop >= mobile.chatBottom - 1); assert.ok(!mobile.overflow);
  const mobileScreenshot = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'mobile-layout.png'), Buffer.from(mobileScreenshot.data, 'base64'));
  await viewport(1440);
  assert.equal((await measure()).stacked, 'false');
  await evaluate("document.querySelector('[data-layout-reset]').click()"); await pause(100);
  const reset = await measure(); assert.ok(Math.abs(reset.rail - initial.rail) < 2); assert.ok(Math.abs(reset.chat - initial.chat) < 2, JSON.stringify({ reset, initial }));
  assert.equal(await evaluate("localStorage.getItem('planwharf_layout_v1')"), null);
  assert.equal(await evaluate("localStorage.getItem('planwharf_layout_heights_v1')"), null);
  await evaluate("localStorage.setItem('planwharf_layout_v1', '{invalid')");
  await command('Page.reload'); await pause(500);
  assert.ok(!(await measure()).overflow, 'Invalid saved preferences use defaults');
  await command('Page.navigate', { url: appUrl + '/orchestrator.html' }); await pause(500);
  assert.equal(await evaluate("document.querySelectorAll('.layout-splitter').length"), 2);
  assert.equal(await evaluate("document.querySelector('.orch-shell').dataset.layoutStacked"), 'false');
  await drag('#orchestrator-view > .layout-splitter', 100);
  assert.ok(await evaluate("JSON.parse(localStorage.getItem('planwharf_layout_v1')).chatRatio > 0.45"));
  await viewport(390, 844);
  assert.ok(await evaluate("document.documentElement.scrollWidth <= innerWidth"));
  // Opening Project home normally updates its visit marker; layout controls must preserve project content.
  const { home, ...content } = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  assert.deepEqual(content, isolatedPlan, 'Layout edits preserve project content, revisions and tasks');
  assert.deepEqual(home?.notes || [], [], 'Layout edits preserve user notes');
  const report = `PASS: real Chromium (${browserExecutable}) width and height drag, keyboard resizing, Escape cancellation, persistence across reload and dashboard refresh, reset, nested responsiveness, mobile and legacy route compatibility.\n`;
  fs.writeFileSync(path.join(output, 'report.txt'), report); console.log(report.trim());
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (command) { try { await command('Browser.close'); } catch (_) {} }
  if (ws) ws.close();
  if (browser) browser.kill();
  if (server.listening) await new Promise(resolve => server.close(resolve));
  fs.rmSync(planFile, { force: true });
});
