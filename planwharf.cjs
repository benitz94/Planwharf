'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');
const Plan = require('./plan.js');
const { createAppWindows } = require('./engine/app-windows.cjs');
const { createServer, readPlan } = require('./server.cjs');
const { createAttemptEngine } = require('./engine/attempts.js');
const { spawnWorker, isProcessAlive, killWorker, askOrchestrator, abortAsk, abortSupervisor } = require('./engine/runner.cjs');
const { readBrowserPreference, resolveBrowser, browserArguments } = require('./engine/browser-settings.cjs');

const root = __dirname;
const defaultPlan = path.join(root, 'plan.json');

function attemptsDirFor(planFile) {
  return path.join(path.dirname(planFile), `.attempts-${path.basename(planFile, path.extname(planFile))}`);
}

function parseArgs(argv) {
  const args = argv.slice(2);
  const opts = {
    command: null,
    planFile: process.env.PLANWHARF_PLAN || defaultPlan,
    attemptsDir: process.env.PLANWHARF_ATTEMPTS || null,
    noWindow: process.env.PLANWHARF_NO_WINDOW === '1',
    port: Number(process.env.PORT || 4173),
    portExplicit: Boolean(process.env.PORT)
  };
  for (let i = 0; i < args.length; i++) {
    const token = args[i];
    if (token === 'status' || token === 'projects') opts.command = token;
    else if (token === 'ask') {
      opts.command = 'ask';
      opts.question = args.slice(i + 1).join(' ').trim();
      break;
    }
    else if (token === '--browser') {
      const value = args[++i];
      if (!value || value.startsWith('--')) throw new Error('--browser needs a browser name or absolute executable path.');
      opts.browser = value;
    }
    else if (token === '--no-window') opts.noWindow = true;
    else if (token === '--plan' || token === '--attempts-dir') {
      const value = args[++i];
      if (!value || value.startsWith('--')) throw new Error(`${token} needs a path.`);
      opts[token === '--plan' ? 'planFile' : 'attemptsDir'] = path.resolve(value);
    }
    else if (token === '--port') {
      opts.port = Number(args[++i]);
      opts.portExplicit = true;
    } else {
      throw new Error(`Unknown argument: ${token}`);
    }
  }
  if (!opts.attemptsDir) opts.attemptsDir = attemptsDirFor(opts.planFile);
  if (!Number.isInteger(opts.port) || opts.port < 0 || opts.port > 65535) {
    throw new Error('PORT must be an integer from 0 to 65535.');
  }
  return opts;
}

function notebookProjects(plan) {
  return plan.schema === 2 ? plan.projects : [plan];
}

function loadAttempts(dir) {
  const file = path.join(dir, 'attempts.json');
  if (!fs.existsSync(file)) return [];
  return createAttemptEngine({ dir }).getAttempts();
}

function formatProjects(plan) {
  return notebookProjects(plan).map(item => `${item.project.id}\t${item.project.name}`).join('\n') + '\n';
}

function formatStatus(plan, attempts) {
  const lines = [];
  const projects = notebookProjects(plan);
  lines.push(`Projects: ${projects.length}`);
  for (const item of projects) {
    const current = item.revisions.at(-1);
    const running = item.tasks.filter(task => task.status === 'running');
    const runningText = running.length ? running.map(task => task.id).join(', ') : 'none';
    const active = attempts.find(attempt => attempt.status === 'active' && attempt.projectId === item.project.id);
    const attemptText = active ? `${active.id} (${active.modelId})` : 'none';
    lines.push(`${item.project.id}\t${item.project.name}\tR${current.id} ${current.mode}\trunning: ${runningText}\tattempt: ${attemptText}`);
  }
  return lines.join('\n') + '\n';
}

function which(cmd) {
  const pathEnv = process.env.PATH || '';
  const sep = process.platform === 'win32' ? ';' : ':';
  const exts = process.platform === 'win32' ? ['.exe', '.cmd', ''] : [''];
  for (const dir of pathEnv.split(sep)) {
    if (!dir) continue;
    for (const ext of exts) {
      const full = path.join(dir, cmd + ext);
      try { if (fs.existsSync(full)) return full; } catch (_) {}
    }
  }
  return null;
}

function findBrowser() {
  const homeApps = path.join(os.homedir(), 'Applications');
  const candidates = [
    process.env.PLANWHARF_BROWSER,
    process.env.PLANWHARF_EDGE,
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Chromium.app/Contents/MacOS/Chromium',
    '/Applications/Brave Browser.app/Contents/MacOS/Brave Browser',
    path.join(homeApps, 'Microsoft Edge.app/Contents/MacOS/Microsoft Edge'),
    path.join(homeApps, 'Google Chrome.app/Contents/MacOS/Google Chrome')
  ].filter(Boolean);
  const found = candidates.find(file => {
    try { return fs.existsSync(file); } catch (_) { return false; }
  });
  if (found) return found;
  for (const cmd of ['msedge', 'microsoft-edge', 'microsoft-edge-stable', 'google-chrome', 'google-chrome-stable', 'chromium', 'chromium-browser', 'brave-browser']) {
    const resolved = which(cmd);
    if (resolved) return resolved;
  }
  return null;
}
function findEdge() { return findBrowser(); }

function listen(opts) {
  const windows = opts.windows || createAppWindows({ onWindowClosed: () => server.quitApp && server.quitApp(), open: (url, view) => openWindow(url, path.join(opts.attemptsDir, 'windows', view), { browser: launchBrowser(opts) }) });
  const server = createServer({ planFile: opts.planFile, attemptsDir: opts.attemptsDir, models: opts.models, fetchPlans: opts.fetchPlans, syncSkills: opts.syncSkills !== false, skillsEnabled: opts.skillsEnabled !== false, spawnWorker, isProcessAlive, killWorker, askModel: askOrchestrator, abortAsk, abortSupervisor,
    updateManager: opts.updateManager, restartAfterUpdate: opts.restartAfterUpdate, appWindows: windows, quitApp: () => server.quitApp && server.quitApp(),
    openWatchWindow: url => windows.openView('orchestrator', url), openPlanWindow: url => windows.openView('planwharf', url) });
  server.windows = windows;
  server.on('close', () => windows.closeAll());
  const start = opts.port;
  const last = opts.portExplicit && start !== 0 ? start : (start === 0 ? 0 : start + 20);
  return new Promise((resolve, reject) => {
    const tryPort = port => {
      const onError = error => {
        server.removeListener('listening', onListening);
        if (error.code === 'EADDRINUSE' && !opts.portExplicit && port !== 0 && port < last) {
          tryPort(port + 1);
          return;
        }
        reject(error);
      };
      const onListening = () => {
        server.removeListener('error', onError);
        const address = server.address();
        resolve({ server, port: address.port, url: `http://127.0.0.1:${address.port}` });
      };
      server.once('error', onError);
      server.once('listening', onListening);
      server.listen(port, '127.0.0.1');
    };
    tryPort(start);
  });
}

function openDefaultBrowser(url) {
  const child = process.platform === 'win32'
    ? spawn('cmd.exe', ['/c', 'start', '', url], { detached: true, stdio: 'ignore', windowsHide: true })
    : spawn(process.platform === 'darwin' ? 'open' : 'xdg-open', [url], { detached: true, stdio: 'ignore' });
  child.on('error', error => console.error('Open this view in your browser:', url, error.message));
  child.planwharfExternalBrowser = true;
  child.unref();
  return child;
}
function launchBrowser(opts) {
  const override = opts.browser || process.env.PLANWHARF_BROWSER || process.env.PLANWHARF_EDGE;
  const preference = override ? (path.isAbsolute(override) ? { id: 'custom', path: override, family: /firefox/i.test(path.basename(override)) ? 'firefox' : 'chromium' } : { id: override }) : readBrowserPreference(opts.planFile);
  return resolveBrowser(preference);
}
function openWindow(url, profileDir, { browser = findBrowser(), start = spawn, fallbackBrowser = openDefaultBrowser } = {}) {
  if (!browser) {
    return fallbackBrowser(url);
  }
  fs.mkdirSync(profileDir, { recursive: true });
  const opts = { stdio: 'ignore' };
  if (process.platform === 'win32') opts.windowsHide = false;
  const testArgs = process.env.PLANWHARF_TEST_NO_SANDBOX === '1' ? ['--no-sandbox'] : [];
  const selected = typeof browser === 'string' ? { path: browser, family: /firefox/i.test(path.basename(browser)) ? 'firefox' : 'chromium' } : browser;
  const child = start(selected.path, [...(selected.family === 'firefox' ? [] : testArgs), ...browserArguments(selected, url, profileDir)], opts);
  child.profileDir = profileDir;
  let fallback = false;
  const recover = reason => {
    if (fallback || child.planwharfClosing) return;
    fallback = true;
    console.error(`Application window failed (${reason}). Opening your default browser: ${url}`);
    fallbackBrowser(url);
  };
  child.on('error', error => recover(error.message));
  child.on('exit', code => { if (code !== null && code !== 0) recover(`browser exit ${code}`); });
  return child;
}

async function runCli(opts) {
  if (opts.command === 'ask') {
    if (!opts.question) throw new Error('ask needs a question.');
    const origin = `http://127.0.0.1:${opts.port}`;
    const response = await fetch(origin + '/api/orchestrator/ask', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Origin: origin },
      body: JSON.stringify({ question: opts.question })
    });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || 'Ask failed. Start Planwharf first.');
    process.stdout.write((body.answer || '') + '\n');
    return;
  }
  const plan = await readPlan(opts.planFile);
  const attempts = loadAttempts(opts.attemptsDir);
  if (opts.command === 'projects') process.stdout.write(formatProjects(plan));
  else process.stdout.write(formatStatus(plan, attempts));
}

// One launcher owner per notebook. Reopening either view talks to that owner.
async function acquireApp(opts) {
  fs.mkdirSync(opts.attemptsDir, { recursive: true });
  const lockFile = path.join(opts.attemptsDir, 'app-instance.json');
  const timeout = opts.ownerTimeoutMs || 10000;
  const deadline = Date.now() + timeout;
  const ownerId = randomUUID();
  const removeUnchanged = raw => {
    try { if (fs.readFileSync(lockFile, 'utf8') === raw) fs.unlinkSync(lockFile); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
  };
  while (Date.now() < deadline) {
    try {
      const fd = fs.openSync(lockFile, 'wx');
      try { fs.writeFileSync(fd, JSON.stringify({ pid: process.pid, ownerId, planFile: path.resolve(opts.planFile) })); }
      finally { fs.closeSync(fd); }
      return { lockFile, ownerId };
    } catch (error) { if (error.code !== 'EEXIST') throw error; }
    let existing, raw;
    try {
      raw = fs.readFileSync(lockFile, 'utf8');
      existing = JSON.parse(raw);
      if (!existing || !Number.isInteger(existing.pid) || existing.pid <= 0 || typeof existing.planFile !== 'string') throw new Error('Invalid application lock.');
    } catch (error) {
      if (error.code === 'ENOENT') continue;
      if (raw !== undefined) {
        try {
          if (Date.now() - fs.statSync(lockFile).mtimeMs > Math.min(1000, timeout / 2)) {
            // Keep broken startup evidence before removing only the unchanged lock.
            fs.writeFileSync(`${lockFile}.invalid-${randomUUID()}`, raw, { flag: 'wx' });
            removeUnchanged(raw);
            continue;
          }
        } catch (failure) { if (failure.code !== 'ENOENT') throw failure; }
      } else throw error;
      await new Promise(resolve => setTimeout(resolve, 100)); continue;
    }
    let alive = true;
    try { process.kill(existing.pid, 0); } catch (error) { alive = error.code !== 'ESRCH'; }
    if (!alive) { removeUnchanged(raw); continue; }
    if (existing.planFile !== path.resolve(opts.planFile)) throw new Error('This attempts directory belongs to another running notebook.');
    if (existing.url) {
      const app = await (async () => {
        try {
          const response = await fetch(existing.url + '/api/app', { signal: AbortSignal.timeout(Math.max(1, Math.min(1000, deadline - Date.now()))) });
          return response.ok ? await response.json() : null;
        } catch { return null; }
      })();
      if (app && app.pid === existing.pid && app.planFile === path.resolve(opts.planFile) && app.attemptsDir === path.resolve(opts.attemptsDir)) return { existing };
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`A Planwharf instance owns this notebook but is not responding. Lock: ${lockFile}. Quit that instance and try again.`);
}
async function runApp(opts) {
  await readPlan(opts.planFile);
  const ownership = await acquireApp(opts);
  if (ownership.existing) {
    const url = ownership.existing.url;
    if (!opts.noWindow) {
      const response = await fetch(url + '/api/planwharf/window', { method: 'POST', headers: { 'Content-Type': 'application/json', Origin: url }, body: '{}' });
      if (!response.ok) throw new Error('Could not reopen the application.');
    }
    console.log(`Planwharf already running: ${url}`);
    return { url, reused: true };
  }
  let server, url;
  const release = () => {
    try { if (JSON.parse(fs.readFileSync(ownership.lockFile, 'utf8')).ownerId === ownership.ownerId) fs.unlinkSync(ownership.lockFile); } catch {}
  };
  try {
    opts.restartAfterUpdate = async info => {
      const args = [path.join(root, 'planwharf.cjs'), '--plan', opts.planFile, '--attempts-dir', opts.attemptsDir, '--port', String(server.address().port), ...(opts.noWindow ? ['--no-window'] : [])];
      const jobFile = path.join(info.stage, 'job.json');
      fs.writeFileSync(jobFile, JSON.stringify({ ...info, parentPid: process.pid, node: process.execPath, args }), { flag: 'wx' });
      const log = fs.openSync(path.join(info.stage, 'updater.log'), 'a');
      let child;
      try {
        child = spawn(process.execPath, [info.helper, jobFile], { cwd: info.stage, detached: true, stdio: ['ignore', log, log], windowsHide: true });
        await new Promise((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
        child.unref();
      } finally { fs.closeSync(log); }
      setImmediate(() => server.quitApp());
    };
    ({ server, url } = await listen(opts));
    const temporary = `${ownership.lockFile}.${ownership.ownerId}.tmp`;
    try {
      fs.writeFileSync(temporary, JSON.stringify({ pid: process.pid, ownerId: ownership.ownerId, planFile: path.resolve(opts.planFile), url }), { flag: 'wx' });
      fs.renameSync(temporary, ownership.lockFile);
    } finally { try { fs.unlinkSync(temporary); } catch (error) { if (error.code !== 'ENOENT') throw error; } }
  } catch (error) {
    if (server) await new Promise(resolve => server.close(resolve));
    release(); throw error;
  }
  let closing = false;
  const shutdown = async () => {
    if (closing) return;
    closing = true;
    try { await server.shutdown(); } catch (error) { console.error('Checkpoint shutdown failed:', error.message); }
    server.close(() => { release(); process.exit(0); });
    setTimeout(() => { release(); process.exit(0); }, 1500).unref();
  };
  server.quitApp = shutdown;
  server.on('close', release);
  process.on('exit', release);
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
  server.updates.check().catch(error => console.error('Update check failed:', error.message));
  if (!opts.noWindow) server.windows.openApp(url);
  console.log(`Planwharf\n${url}\nOrchestrator: ${url}/#orchestrator\nPlan: ${opts.planFile}\nDesktop launch opens one window. Use Show Orchestrator / Show Planwharf to switch views. Closing the desktop window saves checkpoints and quits. Quit application or Ctrl+C also shuts down.`);
  return { server, url };
}

async function main(argv = process.argv) {
  const opts = parseArgs(argv);
  if (opts.command) return runCli(opts);
  return runApp(opts);
}

if (require.main === module) {
  main().catch(error => {
    console.error(error.message || error);
    process.exitCode = 1;
  });
}

module.exports = {
  parseArgs,
  attemptsDirFor,
  formatProjects,
  formatStatus,
  loadAttempts,
  findEdge,
  findBrowser,
  openWindow,
  listen,
  main,
  runApp,
  acquireApp
};
