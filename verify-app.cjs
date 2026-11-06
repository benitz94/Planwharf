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
  for (const old of ['/advanced', '/index.html', '/orchestrator.html', '/?watch=1', '/?window=orchestrator']) {
    const moved = await fetch(opened.url + old, { redirect: 'manual' });
    assert.equal(moved.status, 302, old);
    assert.equal(moved.headers.get('location'), '/', old + ' opens the chat view');
  }
  const api = await (await fetch(opened.url + '/api/plan')).json();
  assert.equal(api.plan.projects.length, plan.projects.length);
  assert.equal(findBrowser(), findEdge());

  await new Promise(resolve => opened.server.close(resolve));
  const exe = path.join(__dirname, 'Planwharf.exe');
  assert.equal(fs.existsSync(exe), true);
  assert.equal(fs.readFileSync(exe).subarray(0, 2).toString('ascii'), 'MZ');
  // The chat page itself is checked in a real browser by verify-chat.cjs.

  const afterHash = crypto.createHash('sha256').update(fs.readFileSync(production)).digest('hex');
  assert.equal(afterHash, beforeHash);
  console.log('OK: app CLI, loopback bind, chat page and redirects served, production plan unchanged.');
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
}).finally(async () => {
  if (runningServer && runningServer.listening) await new Promise(resolve => runningServer.close(resolve));
  try {
    await fsp.rm(dir, { recursive: true, force: true });
  } catch (_) {}
});
