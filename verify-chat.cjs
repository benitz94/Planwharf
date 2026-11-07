// Real browser check of the chat view at "/": first-run guidance, chat, local commands,
// side panels, team changes, folder settings, overlap and narrow screens. No real model runs.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Plan = require('./plan.js');
const { createServer } = require('./server.cjs');
const { spawn } = require('node:child_process');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const configuredBrowser = process.env.PLANWHARF_TEST_BROWSER;
assert.ok(configuredBrowser && path.isAbsolute(configuredBrowser) && fs.existsSync(configuredBrowser), 'Set PLANWHARF_TEST_BROWSER to an absolute Chromium or Edge executable.');
const output = process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'chat-browser-' + Date.now());
fs.mkdirSync(output, { recursive: true });
const profile = path.join(output, `chromium-profile-${Date.now()}`);
const planFile = path.join(output, `chat-plan-${Date.now()}.json`);
const folder = path.join(output, 'site');
fs.mkdirSync(folder, { recursive: true });
const basePlan = Plan.example(); basePlan.tasks = []; basePlan.project = { id: 'first', name: 'First project' };
const plan = Plan.createProject(basePlan, { name: 'Recipe site', direction: 'Build a small recipe website.' });
const projectId = plan.projects.at(-1).project.id;
fs.writeFileSync(planFile, JSON.stringify(plan));
const calls = [], launches = [], live = new Set();
let pid = 4000;
const server = createServer({ planFile, refreshModels: false, fetchPlans: null,
  models: [
    { id: 'lead-a', label: 'Lead A', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'default' },
    { id: 'check-b', label: 'Checker B', provider: 'claude', cli: 'claude-opus-5-5', enabled: true, available: true, kind: 'default' }
  ],
  inspectFolder: () => ({ git: true, names: ['index.html'], dirty: [] }),
  readRepository: () => ({ local: { exists: true, readable: true, writable: true, git: true, branch: 'main', changes: [' M index.html', '?? notes.txt'], uncommitted: 2 }, remote: {}, comparison: {} }),
  askModel: async info => { calls.push(info); await pause(1500); return { answer: 'Fine: a recipe site with a list page and a detail page.' }; },
  monitorIntervalMs: 300,
  // Fake crew: a worker is a pid that stays alive until the check writes its result.
  spawnWorker: info => { const processId = ++pid; launches.push({ ...info, pid: processId }); live.add(processId); if (info.logPath) { try { fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'Writing index.html\nAdding the recipe list\n'); } catch {} } return { pid: processId }; },
  isProcessAlive: processId => live.has(processId), killWorker: processId => live.delete(processId) });
function finish(info) {
  const result = { resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Finished ' + info.job.title,
    filesChanged: ['index.html'], tests: [{ command: 'node --test', status: 'PASSED', evidence: 'fixture' }], blockers: [], nextAction: 'None', createdAt: new Date().toISOString() };
  fs.writeFileSync(info.structuredResultPath, JSON.stringify(result));
  fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
  fs.writeFileSync(info.resultPath, 'STATUS: COMPLETE\nEXIT: 0\n'); live.delete(info.pid);
}
let browser, ws, command, browserError;
const errors = [];
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const appUrl = `http://127.0.0.1:${server.address().port}`;
  browser = spawn(configuredBrowser, ['--headless=new', '--disable-gpu', '--disable-extensions', '--disable-background-networking', ...(process.env.PLANWHARF_TEST_NO_SANDBOX === '1' ? ['--no-sandbox'] : []), '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' });
  browser.on('error', error => { browserError = error; });
  let port;
  for (let i = 0; i < 100; i++) {
    if (browserError) throw browserError;
    try { port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]); if (port) break; } catch (_) {}
    await pause(100);
  }
  assert.ok(port, 'Browser DevTools did not start');
  const page = (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(target => target.type === 'page');
  ws = new WebSocket(page.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
  let sequence = 0; const waiting = new Map();
  ws.addEventListener('message', event => {
    const message = JSON.parse(event.data);
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails?.exception?.description || message.params.exceptionDetails?.text);
    const pending = waiting.get(message.id);
    if (pending) { clearTimeout(pending.timer); waiting.delete(message.id); message.error ? pending.reject(new Error(message.error.message)) : pending.resolve(message.result); }
  });
  command = (method, params = {}) => new Promise((resolve, reject) => {
    const id = ++sequence;
    const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`Timeout: ${method}`)); }, 30000);
    waiting.set(id, { resolve, reject, timer }); ws.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async expression => {
    const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  const until = async (expression, label, ms = 8000) => {
    const end = Date.now() + ms;
    while (Date.now() < end) { if (await evaluate(expression)) return; await pause(100); }
    const seen = await evaluate(`(document.getElementById('thread').innerText.slice(-700) + '\\n--- bar: ' + document.getElementById('action-bar').innerText + '\\n--- notice: ' + document.getElementById('toast').innerText)`).catch(() => '');
    throw new Error('Timed out waiting for: ' + label + '\n' + seen);
  };
  const viewport = (width, height = 900) => command('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: width < 600 });
  const click = selector => evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) throw new Error('missing ' + ${JSON.stringify(selector)}); el.click(); return true; })()`);
  const say = async text => { await evaluate(`(() => { const box = document.getElementById('composer-input'); box.value = ${JSON.stringify(text)}; document.getElementById('composer').requestSubmit(); return true; })()`); };
  // Nothing on screen may overlap: top bar, thread, composer and the open side panel.
  const layout = () => evaluate(`(() => {
    const rect = id => { const el = document.getElementById(id); if (!el || el.hidden) return null; const r = el.getBoundingClientRect(); return r.width && r.height ? r : null; };
    const hit = (a, b) => Boolean(a && b) && a.left < b.right - 1 && b.left < a.right - 1 && a.top < b.bottom - 1 && b.top < a.bottom - 1;
    const top = document.querySelector('.topbar').getBoundingClientRect(), thread = rect('thread'), composer = document.querySelector('.composer-wrap').getBoundingClientRect();
    return { overflow: document.documentElement.scrollWidth > innerWidth + 1, topThread: hit(top, thread), threadComposer: hit(thread, composer), composerVisible: composer.bottom <= innerHeight + 1 && composer.top >= 0, sideOverlaysChat: window.innerWidth > 1100 && hit(rect('side-panel'), thread) };
  })()`);
  const clean = (value, label) => { assert.equal(value.overflow, false, label + ': no horizontal scroll'); assert.equal(value.topThread, false, label + ': header and chat do not overlap'); assert.equal(value.threadComposer, false, label + ': chat and message box do not overlap'); assert.equal(value.composerVisible, true, label + ': message box visible'); assert.equal(value.sideOverlaysChat, false, label + ': panel sits beside the chat'); };

  await command('Runtime.enable'); await command('Page.enable');
  await viewport(1280);
  await command('Page.navigate', { url: appUrl + '/' });
  await until(`document.querySelectorAll('.project-item').length === 2`, 'project list');
  clean(await layout(), 'desktop');

  // First run: the page says what to do and the crew cannot start yet.
  await click(`[data-project="${projectId}"]`);
  await until(`document.getElementById('project-title').textContent === 'Recipe site'`, 'project selected');
  const first = await evaluate(`({ text: document.getElementById('thread').innerText, start: document.querySelector('#action-bar [data-action="autonomy-on"]')?.disabled })`);
  assert.match(first.text, /Welcome aboard, captain/);
  assert.match(first.text, /Choose the lead/);
  assert.match(first.text, /Set the folder/);
  assert.equal(await evaluate(`document.querySelectorAll('[data-action="autonomy-on"]').length`), 1, 'One start button, in the bar');
  assert.equal(first.start, true, 'The crew cannot start before a lead and a folder exist');
  await command("Page.captureScreenshot", { format: "png" }).then(shot => fs.writeFileSync(path.join(output, "chat-first-run.png"), Buffer.from(shot.data, "base64")));

  // Team: choose the lead, then give the second model the Verify role only.
  await click('.tab[data-panel="team"]');
  await until(`!document.getElementById('side-panel').hidden && document.getElementById('side-title').textContent === 'Team'`, 'team panel');
  clean(await layout(), 'desktop with team');
  await evaluate(`(() => { const select = document.querySelector('#side-body [data-lead-select]'); select.value = 'lead-a'; select.dispatchEvent(new Event('change', { bubbles: true })); return true; })()`);
  await until(`document.querySelector('#side-body .badge.lead-badge')?.closest('.member')?.innerText.includes('Lead A')`, 'lead badge');
  await command('Page.captureScreenshot', { format: 'png' }).then(shot => fs.writeFileSync(path.join(output, 'chat-team.png'), Buffer.from(shot.data, 'base64')));
  const wrapped = await evaluate(`[...document.querySelectorAll('#side-body .member .label, #side-body .member .meta')].filter(el => el.getClientRects().length > 1 || el.offsetHeight > 2 * parseFloat(getComputedStyle(el).lineHeight || '20') - 2).map(el => el.innerText)`);
  assert.deepEqual(wrapped, [], 'Team names and details fit on one line');
  const roles = async (id, team = projectId) => (await (await fetch(appUrl + '/api/orchestrator?readOnly=1' + (team ? '&team=' + encodeURIComponent(team) : ''))).json()).models.find(model => model.id === id).roles;
  const sharedWorker = (await roles('check-b', null)).worker;
  assert.match(await evaluate(`document.querySelector('#side-body .team-scope')?.innerText || ''`), /Team for Recipe site/);
  if ((await roles('check-b')).worker) await click(`[data-role-toggle="check-b"][data-role="worker"]`);
  await until(`document.querySelector('[data-role-toggle="check-b"][data-role="worker"]')?.getAttribute('aria-pressed') === 'false'`, 'checker no longer builds');
  assert.equal((await roles('check-b')).worker, false);
  assert.equal((await roles('check-b')).verifier, true);
  assert.equal((await roles('check-b', null)).worker, sharedWorker, 'A change for one project leaves the shared team as it was');

  // Settings: set the folder without leaving the page.
  await click('.tab[data-panel="settings"]');
  await until(`document.getElementById('folder-input')`, 'settings panel');
  await evaluate(`(() => { document.getElementById('folder-input').value = ${JSON.stringify(folder)}; document.getElementById('folder-form').requestSubmit(); return true; })()`);
  await until(`!document.querySelector('#action-bar [data-action="autonomy-on"]')?.disabled`, 'crew ready to start');

  // Chat: the pending turn shows at once, the answer replaces it.
  await say('A recipe site with a list and a detail page.');
  await until(`document.querySelector('#thread .dots') !== null`, 'pending turn');
  await until(`document.getElementById('thread').innerText.includes('Fine: a recipe site')`, 'lead answer', 15000);
  assert.equal(calls.length, 1);
  // A chat message starts nothing; the page says so and offers to make it the goal.
  await until(`document.querySelector('[data-action="use-goal"]') && document.getElementById('thread').innerText.includes('nothing has started yet')`, 'goal hint');
  await click('[data-action="use-goal"]');
  await until(`!document.querySelector('[data-action="use-goal"]')`, 'goal saved');
  assert.equal((await (await fetch(appUrl + '/api/orchestrator?readOnly=1')).json()).projects.find(row => row.id === projectId).goal, 'A recipe site with a list and a detail page.');
  assert.match(await evaluate(`document.getElementById('now').textContent`), /\d{1,2}[:.]\d{2}/, 'Day and time are shown');

  // Crew: proposal, approval, live work with progress, then the result.
  const proposed = await fetch(appUrl + '/api/orchestrator/propose', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId, title: 'Recipe site, first version', goal: 'List and detail pages',
    subTasks: [{ title: 'Build the list and detail pages with tests', detail: 'Fixture build', assignedModelId: 'lead-a' }, { title: 'Verify the work independently against the job: run the tests', detail: 'Fixture verify', assignedModelId: 'check-b', dependsOn: [0] }] }) });
  assert.equal(proposed.status, 201, await proposed.clone().text());
  await until(`document.getElementById('thread').innerText.includes('The lead proposes this work')`, 'proposal card', 10000);
  assert.match(await evaluate(`document.querySelector('.card.crew').innerText`), /Build[\s\S]*Lead A[\s\S]*Verify[\s\S]*Checker B/);
  await click('[data-action="approve"]');
  await until(`document.querySelector('.card.crew.live')?.innerText.includes('0 of 2 steps done')`, 'live crew card', 15000);
  await until(`document.querySelector('.card.crew.live .preview pre')?.innerText.includes('recipe list')`, 'builder preview', 15000);
  // A timed stop shows a countdown and can be cancelled, without a model call.
  await say('fermati tra 5 minuti');
  await until(`document.querySelector('#action-bar .timed-stop')?.innerText.includes('in 5 min')`, 'timed stop countdown');
  await click('[data-action="cancel-timed-stop"]');
  await until(`!document.querySelector('#action-bar .timed-stop')`, 'timed stop cancelled');
  assert.equal(calls.length, 1, 'Stop commands never call a model');
  clean(await layout(), 'crew at work');
  await command('Page.captureScreenshot', { format: 'png' }).then(shot => fs.writeFileSync(path.join(output, 'chat-crew.png'), Buffer.from(shot.data, 'base64')));
  finish(launches[0]);
  await until(`document.querySelector('.card.crew.live')?.innerText.includes('1 of 2 steps done')`, 'first step done', 15000);
  assert.equal(launches.length, 2, 'The verify step starts after the build step');
  finish(launches[1]);
  await until(`document.querySelector('.card.crew.done')?.innerText.includes('2/2')`, 'result card', 15000);
  await until(`/2 files not yet committed/.test(document.querySelector('.card.crew.done').innerText)`, 'changed files counted');
  await command('Page.captureScreenshot', { format: 'png' }).then(shot => fs.writeFileSync(path.join(output, 'chat-done.png'), Buffer.from(shot.data, 'base64')));
  assert.equal(calls.length, 1, 'Showing the crew never calls a model');

  // Settings: dangerous buttons need a second press and can be cancelled.
  await say('settings');
  await until(`document.querySelector('[data-arm="quit"]')`, 'quit button');
  await click('[data-arm="quit"]');
  await until(`document.querySelector('[data-action="quit"]')?.textContent === 'Yes, quit now'`, 'quit confirmation');
  await click('[data-action="disarm"]');
  await until(`document.querySelector('[data-arm="quit"]')`, 'quit cancelled');
  assert.match(await evaluate(`document.getElementById('side-body').innerText`), /updates/i);

  // Move a project: save it to a file, then open that file as a new project.
  const href = await evaluate(`document.querySelector('a[href^="/api/project-export"]').getAttribute('href')`);
  const exported = await fetch(appUrl + href);
  assert.equal(exported.status, 200);
  const bundleFile = path.join(output, 'recipe-export.json');
  fs.writeFileSync(bundleFile, await exported.text());
  const input = await command('Runtime.evaluate', { expression: `document.getElementById('import-file')` });
  await command('DOM.enable');
  await command('DOM.setFileInputFiles', { files: [bundleFile], objectId: input.result.objectId });
  await until(`document.querySelectorAll('.project-item').length === 3`, 'imported project', 15000);
  await click(`[data-project="${projectId}"]`);
  await until(`document.getElementById('project-title').textContent === 'Recipe site'`, 'back to the first copy');

  // AI tools can be turned off and on from Team.
  await say('team');
  await until(`document.querySelector('[data-provider-toggle="claude"]')`, 'provider switch');
  await click('[data-provider-toggle="claude"]');
  await until(`document.querySelector('[data-provider-toggle="claude"]')?.getAttribute('aria-pressed') === 'false'`, 'provider off');
  await click('[data-provider-toggle="claude"]');
  await until(`document.querySelector('[data-provider-toggle="claude"]')?.getAttribute('aria-pressed') === 'true'`, 'provider on');

  // API connection: typing survives the automatic refresh, then the new tool appears.
  await evaluate(`(() => { document.querySelector('#api-form').closest('details').open = true; const box = document.getElementById('api-label'); box.focus(); box.value = 'Fixture API'; box.dispatchEvent(new Event('input', { bubbles: true })); return true; })()`);
  await pause(5000);
  assert.equal(await evaluate(`document.getElementById('api-label')?.value`), 'Fixture API', 'The 4-second refresh keeps what the user typed');
  await evaluate(`(() => { const form = document.getElementById('api-form'); form.baseUrl.value = 'https://api.example.test/v1'; form.authEnv.value = 'FIXTURE_API_KEY'; form.modelIds.value = 'fixture-chat'; form.toolsEnabled.checked = true; form.requestSubmit(); return true; })()`);
  await until(`document.querySelector('[data-provider-toggle="fixture-api"]')`, 'API connection listed', 10000);
  await until(`[...document.querySelectorAll('#side-body .member .label')].some(el => el.textContent.includes('fixture-chat'))`, 'API model on the bench', 10000);

  // Local commands open panels and need confirmation for team changes, without a model call.
  await say('show usage');
  await until(`document.getElementById('side-title').textContent === 'Usage'`, 'usage panel by command');
  await say('apri file cambiati');
  await until(`document.getElementById('side-body').innerText.includes('notes.txt')`, 'changed files by command');
  assert.match(await evaluate(`document.getElementById('side-body').innerText`), /changed\s+index\.html/);
  await say('bench checker');
  await until(`document.getElementById('thread').innerText.includes('Put Checker B on the bench?')`, 'bench confirmation');
  await click('[data-action="confirm-yes"]');
  await until(`fetch('/api/orchestrator?readOnly=1&team=${projectId}').then(r => r.json()).then(s => s.models.find(m => m.id === 'check-b').roles.verifier === false)`, 'checker benched');
  assert.equal(calls.length, 1, 'Commands never call a model');

  // Narrow screens: no overlap, the side panel covers the chat on purpose and closes with Escape.
  for (const width of [900, 390]) {
    await viewport(width, 800); await pause(200);
    clean(await layout(), 'width ' + width);
    await command("Page.captureScreenshot", { format: "png" }).then(shot => fs.writeFileSync(path.join(output, 'chat-' + width + '.png'), Buffer.from(shot.data, "base64")));
  }
  await command('Input.dispatchKeyEvent', { type: 'keyDown', key: 'Escape', code: 'Escape' });
  await until(`document.getElementById('side-panel').hidden`, 'Escape closes the panel');

  // Dark is the default; the text colour can be changed; light stays readable.
  await viewport(1280); await click('.tab[data-panel="settings"]'); await until(`document.querySelector('[data-ink="amber"]')`, 'text colour choices');
  assert.equal(await evaluate(`document.documentElement.dataset.theme`), 'dark');
  await click('[data-ink="amber"]');
  await until(`getComputedStyle(document.body).color === 'rgb(255, 211, 138)'`, 'amber text');
  await until(`document.querySelector('[data-ink="amber"]')?.getAttribute('aria-pressed') === 'true'`, 'amber marked');
  await pause(4500); await command('Page.captureScreenshot', { format: 'png' }).then(shot => fs.writeFileSync(path.join(output, 'chat-dark.png'), Buffer.from(shot.data, 'base64')));
  assert.ok(await evaluate(`Boolean(document.querySelector('[data-ink-custom]'))`), 'Any colour can be picked');
  await click('[data-ink="white"]');
  await click('[data-action="theme"]');
  await until(`document.documentElement.dataset.theme === 'light'`, 'light theme');
  const contrast = await evaluate(`(() => { const s = getComputedStyle(document.body); return [s.backgroundColor, s.color]; })()`);
  assert.notEqual(contrast[0], contrast[1]);
  assert.ok(!(await evaluate(`Boolean(document.querySelector('[data-ink]'))`)), 'text colours only in the dark theme');
  assert.deepEqual(errors, [], 'No script errors on the page');
  console.log('verify-chat: ok');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  try { ws?.close(); } catch {}
  try { browser?.kill(); } catch {}
  await new Promise(resolve => server.close(resolve));
});
