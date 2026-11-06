'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const fsp = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
const { formatProjects, formatStatus, loadAttempts, listen, parseArgs, findEdge, findBrowser } = require('./planwharf.cjs');
const { readPlan } = require('./server.cjs');

const production = path.join(__dirname, 'plan.json');
const beforeHash = crypto.createHash('sha256').update(fs.readFileSync(production)).digest('hex');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-app-'));
const planFile = path.join(dir, 'plan.json');
const attemptsDir = path.join(dir, 'attempts');
fs.copyFileSync(path.join(__dirname, 'fixtures/notebook.json'), planFile);
let runningServer;

function runCli(args) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [path.join(__dirname, 'planwharf.cjs'), ...args], { windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', code => resolve({ code, stdout, stderr }));
  });
}

(async () => {
  const plan = await readPlan(planFile);
  const projectsText = formatProjects(plan);
  assert.match(projectsText, new RegExp(`${plan.projects[0].project.id}\\t${plan.projects[0].project.name}`));
  assert.equal(projectsText.trim().split('\n').length, plan.projects.length);

  const statusText = formatStatus(plan, loadAttempts(attemptsDir));
  assert.match(statusText, /^Projects: \d+/);
  assert.match(statusText, new RegExp(`${plan.projects[0].project.id}\\t${plan.projects[0].project.name}`));

  const listed = await runCli(['projects', '--plan', planFile, '--attempts-dir', attemptsDir]);
  assert.equal(listed.code, 0, listed.stderr);
  assert.equal(listed.stdout, projectsText);

  const status = await runCli(['status', '--plan', planFile, '--attempts-dir', attemptsDir]);
  assert.equal(status.code, 0, status.stderr);
  assert.equal(status.stdout, statusText);

  const opts = parseArgs([process.execPath, 'planwharf.cjs', '--no-window', '--plan', planFile, '--attempts-dir', attemptsDir, '--port', '0']);
  opts.models = [];
  opts.fetchPlans = async () => ['ChatGPT', 'Grok'].map(id => ({ id, label: id, readable: true, pct: 18, leftPct: 82, windows: [] }));
  const opened = await listen(opts);
  runningServer = opened.server;
  const chat = await fetch(opened.url + '/');
  assert.equal(chat.status, 200);
  assert.match(await chat.text(), /chat-ui\.js/, 'The root opens the chat view');
  const page = await fetch(opened.url + '/advanced');
  assert.equal(page.status, 200);
  const html = await page.text();
  assert.match(html, /For you/);
  assert.match(html, /Running/);
  assert.match(html, /The plan/);
  assert.match(html, /What changed/);
  assert.match(html, /Folder/);
  assert.match(html, /GitHub/);
  assert.match(html, /Set place/);
  assert.match(html, /Grill me/);
  assert.match(html, /does not start a process/);
  assert.match(html, /All projects/);
  assert.match(html, /orchestrator/);
  assert.match(html, /Remove project/);
  assert.match(html, /Workers and plans/);
  assert.match(html, /data-mode="auto"/);
  assert.match(html, /Automatic/);
  assert.match(html, /ChatGPT and Grok/);
  assert.match(html, /Auto start/);
  assert.match(html, /Grill with the docs/);
  assert.match(html, /id="theme-toggle"/);
  assert.doesNotMatch(html, /skills-version|matt-skills/);
  assert.match(html, /id="orch-ask-form"/);
  assert.match(html, /id="orch-lead"/);
  assert.match(html, /Ask the orchestrator/);
  assert.doesNotMatch(html, /mattpocock\/skills/);
  const orchPage = await fetch(opened.url + '/orchestrator.html');
  assert.equal(orchPage.status, 200);
  const orchHtml = await orchPage.text();
  assert.match(orchHtml, /id="orch-lead"/);
  assert.match(orchHtml, /id="orch-ask-form"/);
  assert.match(orchHtml, /id="dashboard-orch-ask-stop"/);
  assert.match(orchHtml, /Message the orchestrator/);
  assert.match(orchHtml, />Send</);
  assert.match(orchHtml, />Stop</);
  assert.match(orchHtml, /quota is left/);
  assert.match(orchHtml, /Command the orchestrator/);
  const watch = await fetch(opened.url + '/?watch=1', { redirect: 'manual' });
  assert.equal(watch.status, 302);
  assert.match(watch.headers.get('location') || '', /#orchestrator/);
  assert.match(html, /Worker discretion/);
  const api = await (await fetch(opened.url + '/api/plan')).json();
  assert.equal(api.plan.projects.length, plan.projects.length);

  const edge = findEdge();
  assert.equal(findBrowser(), edge);
  if (edge) {
    const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
    const profile = path.join(dir, `edge-chat-${Date.now()}`);
    const browser = spawn(edge, ['--headless=new', '--disable-gpu', ...(process.env.PLANWHARF_TEST_NO_SANDBOX === '1' ? ['--no-sandbox'] : []), '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { windowsHide: true, stdio: 'ignore' });
    try {
      let port;
      for (let i = 0; i < 100; i++) {
        try {
          port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]);
          if (port) break;
        } catch (_) {}
        await pause(100);
      }
      assert.ok(port, 'Edge DevTools did not start');
      const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const page = pages.find(target => target.type === 'page');
      assert.ok(page, 'Edge page missing');
      const ws = new WebSocket(page.webSocketDebuggerUrl);
      await new Promise((resolve, reject) => {
        ws.addEventListener('open', resolve, { once: true });
        ws.addEventListener('error', reject, { once: true });
      });
      let sequence = 0;
      const waiting = new Map();
      ws.addEventListener('message', event => {
        const response = JSON.parse(event.data);
        const pending = waiting.get(response.id);
        if (pending) {
          clearTimeout(pending.timer);
          waiting.delete(response.id);
          response.error ? pending.reject(new Error(response.error.message)) : pending.resolve(response.result);
        }
      });
      const command = (method, params = {}) => new Promise((resolve, reject) => {
        const id = ++sequence;
        const timer = setTimeout(() => {
          waiting.delete(id);
          reject(new Error(`Timeout: ${method}`));
        }, 20000);
        waiting.set(id, { resolve, reject, timer });
        ws.send(JSON.stringify({ id, method, params }));
      });
      const evaluate = async expression => {
        const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
        if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
        return result.result.value;
      };
      await command('Emulation.setDeviceMetricsOverride', { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
      await command('Page.navigate', { url: opened.url + '/orchestrator.html' });
      let ready = false;
      for (let i = 0; i < 80; i++) {
        if (await evaluate("Boolean(document.body && document.body.getAttribute('data-orch-ready') === '1' && document.getElementById('dashboard-orch-ask-input') && document.getElementById('dashboard-orch-ask-submit') && document.getElementById('dashboard-orch-project')?.options.length > 1)")) {
          ready = true;
          break;
        }
        await pause(100);
      }
      assert.equal(ready, true);
      const submitLabel = await evaluate("document.getElementById('dashboard-orch-ask-submit').textContent");
      assert.equal(submitLabel, 'Send');
      assert.equal(await evaluate("Boolean(document.getElementById('dashboard-orch-ask-stop') && document.getElementById('dashboard-orch-ask-stop').hidden)"), true);
      await evaluate("const recipient = document.getElementById('dashboard-orch-project'); recipient.value = recipient.options[1].value; recipient.dispatchEvent(new Event('change'))");
      await evaluate("document.getElementById('dashboard-orch-ask-input').value = 'Switch to automatic'");
      await evaluate("document.getElementById('dashboard-orch-ask-submit').click()");
      let replied = false;
      let thread = '';
      let threadHtml = '';
      for (let i = 0; i < 80; i++) {
        const view = await evaluate("(() => { const el = document.getElementById('dashboard-orch-ask-thread'); return { html: el ? el.innerHTML : '', text: el ? el.innerText : '', pending: Boolean(el && el.querySelector('.is-pending')) }; })()");
        threadHtml = view && view.html || '';
        thread = view && view.text || '';
        if (!view.pending && /chat-turn/.test(threadHtml) && /mode is now Automatic/i.test(thread) && /chat-elapsed|Thought for|Answered in/.test(threadHtml)) {
          replied = true;
          break;
        }
        await pause(100);
      }
      assert.equal(replied, true, thread);
      assert.match(threadHtml, /chat-elapsed|Thought for|Answered in/);
      assert.match(thread, /\d+\s*ms|Answered in|Thought for/i);
      const remaining = await evaluate("document.getElementById('dashboard-orchestrator') ? document.getElementById('dashboard-orchestrator').innerText : ''");
      assert.match(remaining, /Provider quota remaining/i);
      assert.match(remaining, /ChatGPT/i);
      assert.match(remaining, /Grok/i);
      assert.match(remaining, /REMAINING|left/i);
      ws.close();
    } finally {
      try { browser.kill(); } catch (_) {}
      await pause(400);
    }
  }

  await new Promise(resolve => opened.server.close(resolve));
  const exe = path.join(__dirname, 'Planwharf.exe');
  assert.equal(fs.existsSync(exe), true);
  assert.equal(fs.readFileSync(exe).subarray(0, 2).toString('ascii'), 'MZ');
  console.log(edge ? 'BROWSER: chat command verified' : 'BROWSER: missing (chat not opened)');

  const afterHash = crypto.createHash('sha256').update(fs.readFileSync(production)).digest('hex');
  assert.equal(afterHash, beforeHash);
  console.log('OK: app CLI, loopback bind, four areas served, production plan unchanged.');
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
}).finally(async () => {
  if (runningServer && runningServer.listening) await new Promise(resolve => runningServer.close(resolve));
  try {
    await fsp.rm(dir, { recursive: true, force: true });
  } catch (_) {}
});
