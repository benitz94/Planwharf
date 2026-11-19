'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { EventEmitter } = require('node:events');
const { spawn } = require('node:child_process');
const { createAppWindows } = require('./app-windows.cjs');
const { acquireApp, parseArgs, listen, openWindow } = require('../planwharf.cjs');
const { createServer } = require('../server.cjs');

function windowFixture(focus = () => true) {
  const opened = [];
  let clock = 100;
  const windows = createAppWindows({ now: () => clock, focus, open: () => {
    const child = new EventEmitter(); child.kill = () => {};
    opened.push(child); return child;
  } });
  return { windows, opened, advance: () => { clock += 16000; } };
}

test('closing a native window without a close beacon permits immediate relaunch', () => {
  const { windows, opened } = windowFixture();
  windows.openApp('http://127.0.0.1:4173');
  windows.heartbeat({ id: 'closed-window' });
  opened[0].emit('exit', 0);
  assert.equal(windows.status().planwharf, false);
  windows.openApp('http://127.0.0.1:4173');
  assert.equal(opened.length, 2);
});

test('a failed browser spawn does not suppress the next launch', () => {
  const { windows, opened } = windowFixture();
  windows.openApp('http://127.0.0.1:4173');
  opened[0].emit('error', new Error('Browser unavailable'));
  windows.openApp('http://127.0.0.1:4173');
  assert.equal(opened.length, 2);
});

test('failed native focus reopens despite a stale heartbeat and deduplicates requests', async () => {
  const { windows, opened } = windowFixture(async () => false);
  windows.openApp('http://127.0.0.1:4173');
  windows.heartbeat({ id: 'stale-window' });
  windows.openApp('http://127.0.0.1:4173');
  windows.openApp('http://127.0.0.1:4173');
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened.length, 2);
});

test('a late exit from an old browser cannot clear a newer launch', () => {
  const { windows, opened, advance } = windowFixture();
  windows.openApp('http://127.0.0.1:4173');
  advance();
  windows.openApp('http://127.0.0.1:4173');
  opened[0].emit('exit', 0);
  windows.openApp('http://127.0.0.1:4173');
  assert.equal(opened.length, 2);
});

test('browser errors fall back once; normal close and application quit never reopen', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-browser-fallback-'));
  try {
    for (const mode of ['error', 'failed-exit', 'normal-close', 'quit']) {
      let fallbacks = 0;
      const child = new EventEmitter(); child.kill = () => child.emit('exit', 1);
      const open = () => openWindow('http://127.0.0.1:4173/', dir, { browser: 'isolated-browser.exe', start: () => child, fallbackBrowser: () => { fallbacks++; } });
      const windows = createAppWindows({ open });
      windows.openApp('http://127.0.0.1:4173');
      if (mode === 'quit') windows.closeAll();
      else if (mode === 'normal-close') child.emit('exit', 0);
      else if (mode === 'failed-exit') child.emit('exit', 23);
      else { child.emit('error', new Error('Isolated spawn failure')); child.emit('exit', 23); }
      assert.equal(fallbacks, ['error', 'failed-exit'].includes(mode) ? 1 : 0, mode);
    }
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('closing the owned desktop window requests Quit once; failures and fallback helpers do not', () => {
  for (const mode of ['close', 'failure', 'quit', 'external']) {
    let quits = 0;
    const child = new EventEmitter();
    child.kill = () => child.emit('exit', 0);
    child.planwharfExternalBrowser = mode === 'external';
    const windows = createAppWindows({ open: () => child, onWindowClosed: () => { quits++; } });
    windows.openApp('http://127.0.0.1:4173');
    if (mode === 'quit') windows.closeAll();
    child.emit('exit', mode === 'failure' ? 23 : 0);
    child.emit('exit', 0);
    assert.equal(quits, mode === 'close' ? 1 : 0, mode);
  }
});

test('desktop close shuts down a real isolated backend and releases its notebook lock', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-close-quit-'));
  const planFile = path.join(dir, 'notebook.json');
  const attemptsDir = path.join(dir, 'attempts');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  const source = `
    const { EventEmitter } = require('node:events');
    const { runApp } = require(${JSON.stringify(path.resolve(__dirname, '../planwharf.cjs'))});
    const { createAppWindows } = require(${JSON.stringify(path.resolve(__dirname, 'app-windows.cjs'))});
    let app;
    const browser = new EventEmitter(); browser.kill = () => browser.emit('exit', 0);
    const windows = createAppWindows({ open: () => browser, onWindowClosed: () => app.server.quitApp() });
    runApp({ planFile: ${JSON.stringify(planFile)}, attemptsDir: ${JSON.stringify(attemptsDir)}, port: 0, models: [], fetchPlans: async () => [],windows }).then(value => {
      app = value;
      process.on('message', message => { if (message === 'close') browser.emit('exit', 0); });
      process.send({ url: app.url });
    }).catch(error => { console.error(error); process.exit(1); });
  `;
  const child = spawn(process.execPath, ['-e', source], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe', 'ipc'] });
  let stderr = '';
  child.stderr.on('data', data => { stderr += data; });
  const exited = new Promise(resolve => child.once('exit', (code, signal) => resolve({ code, signal })));
  const timeout = setTimeout(() => child.kill(), 15000);
  try {
    const ready = await new Promise((resolve, reject) => {
      child.once('message', resolve);
      child.once('error', reject);
      child.once('exit', () => reject(new Error('Backend closed before readiness: ' + stderr)));
    });
    assert.equal((await (await fetch(ready.url + '/api/app')).json()).pid, child.pid);
    child.send('close');
    assert.deepEqual(await exited, { code: 0, signal: null }, stderr);
    assert.equal(fs.existsSync(path.join(attemptsDir, 'app-instance.json')), false);
  } finally {
    clearTimeout(timeout);
    if (child.exitCode === null && child.signalCode === null) { child.kill(); await exited; }
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('external browser close requests Quit after grace, but reload cancels it', async () => {
  let quits = 0;
  const helper = new EventEmitter();
  helper.planwharfExternalBrowser = true;
  const windows = createAppWindows({ open: () => helper, closeGraceMs: 20, onWindowClosed: () => { quits++; } });
  windows.openApp('http://127.0.0.1:4173');
  helper.emit('exit', 0);
  windows.heartbeat({ id: 'tab' });
  windows.heartbeat({ id: 'tab', closed: true });
  windows.heartbeat({ id: 'reloaded' });
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(quits, 0);
  windows.heartbeat({ id: 'reloaded', closed: true });
  await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(quits, 1);
  windows.closeAll();
});

test('a delayed focus failure cannot reopen after application shutdown', async () => {
  let finish;
  const { windows, opened } = windowFixture(() => new Promise(resolve => { finish = resolve; }));
  windows.openApp('http://127.0.0.1:4173');
  windows.heartbeat({ id: 'active-window' });
  windows.openApp('http://127.0.0.1:4173');
  windows.closeAll();
  finish(false);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(opened.length, 1);
});

test('Quit closes owned browsers even after a focus recovery replaces the latest handle', async () => {
  const { windows, opened } = windowFixture(async () => false);
  windows.openApp('http://127.0.0.1:4173');
  windows.heartbeat({ id: 'visible-window' });
  windows.openApp('http://127.0.0.1:4173');
  await new Promise(resolve => setImmediate(resolve));
  let killed = 0;
  for (const child of opened) child.kill = () => { killed++; child.emit('exit', 1); };
  windows.closeAll();
  assert.equal(killed, 2);
});

test('old corrupt startup locks recover while preserving the original bytes', async () => {
  for (const raw of ['', '{broken', '{}', 'null', '{"pid":0,"planFile":"x"}']) {
    const attemptsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-'));
    try {
      const lockFile = path.join(attemptsDir, 'app-instance.json');
      fs.writeFileSync(lockFile, raw);
      fs.utimesSync(lockFile, new Date(0), new Date(0));
      const claimed = await acquireApp({ attemptsDir, planFile: path.join(attemptsDir, 'notebook.json'), ownerTimeoutMs: 500 });
      assert.ok(claimed.ownerId);
      assert.equal(JSON.parse(fs.readFileSync(lockFile)).ownerId, claimed.ownerId);
      const archive = fs.readdirSync(attemptsDir).find(file => file.includes('.invalid-'));
      assert.equal(fs.readFileSync(path.join(attemptsDir, archive), 'utf8'), raw);
    } finally { fs.rmSync(attemptsDir, { recursive: true, force: true }); }
  }
});

test('dead owner locks recover; live unresponsive owners stay protected', async () => {
  const attemptsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-'));
  const planFile = path.join(attemptsDir, 'notebook.json');
  const lockFile = path.join(attemptsDir, 'app-instance.json');
  const dead = spawn(process.execPath, ['-e', ''], { windowsHide: true });
  await new Promise((resolve, reject) => { dead.on('error', reject); dead.on('close', resolve); });
  try {
    fs.writeFileSync(lockFile, JSON.stringify({ pid: dead.pid, planFile }));
    assert.ok((await acquireApp({ attemptsDir, planFile })).ownerId);
    const raw = JSON.stringify({ pid: process.pid, planFile });
    fs.writeFileSync(lockFile, raw);
    await assert.rejects(acquireApp({ attemptsDir, planFile, ownerTimeoutMs: 200 }), /not responding/);
    assert.equal(fs.readFileSync(lockFile, 'utf8'), raw);
  } finally { fs.rmSync(attemptsDir, { recursive: true, force: true }); }
});

test('a recently interrupted partial lock recovers within the same launch', async () => {
  const attemptsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-partial-'));
  try {
    fs.writeFileSync(path.join(attemptsDir, 'app-instance.json'), '');
    const result = await acquireApp({ attemptsDir, planFile: path.join(attemptsDir, 'notebook.json'), ownerTimeoutMs: 500 });
    assert.ok(result.ownerId);
  } finally { fs.rmSync(attemptsDir, { recursive: true, force: true }); }
});

test('malformed identity responses do not crash or release a live owner', async () => {
  const attemptsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-'));
  const planFile = path.join(attemptsDir, 'notebook.json');
  const server = http.createServer((req, res) => res.end('not JSON'));
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const raw = JSON.stringify({ pid: process.pid, planFile, url: `http://127.0.0.1:${server.address().port}` });
  fs.writeFileSync(path.join(attemptsDir, 'app-instance.json'), raw);
  try {
    await assert.rejects(acquireApp({ attemptsDir, planFile, ownerTimeoutMs: 200 }), /not responding/);
    assert.equal(fs.readFileSync(path.join(attemptsDir, 'app-instance.json'), 'utf8'), raw);
  } finally { await new Promise(resolve => server.close(resolve)); fs.rmSync(attemptsDir, { recursive: true, force: true }); }
});

test('missing notebook and attempts arguments are rejected explicitly', () => {
  for (const args of [['--plan'], ['--attempts-dir'], ['--plan', '--no-window']]) {
    assert.throws(() => parseArgs(['node', 'planwharf.cjs', ...args]), /needs a path/);
  }
});

test('an occupied default port selects the next port without touching the notebook', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-port-'));
  const planFile = path.join(dir, 'notebook.json');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  // The fixture's running task is pre-phase-2. Startup close supersedes that task and rewrites the file.
  // Mark it current first so this check is the port fallback itself, including after that close settles.
  const notebook = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  for (const project of notebook.projects || []) {
    for (const task of project.tasks || []) {
      if (task.status === 'running') task.executionVersion = 2;
    }
  }
  fs.writeFileSync(planFile, JSON.stringify(notebook));
  const before = fs.readFileSync(planFile);
  const blocker = http.createServer();
  await new Promise(resolve => blocker.listen(0, '127.0.0.1', resolve));
  let app;
  const logged = [];
  const originalError = console.error;
  console.error = (...args) => {
    logged.push(args.join(' '));
    originalError.apply(console, args);
  };
  try {
    app = await listen({ planFile, attemptsDir: path.join(dir, 'attempts'), port: blocker.address().port, models: [],fetchPlans: async () => [] });
    assert.ok(app.port > blocker.address().port);
    assert.equal((await fetch(app.url + '/')).status, 200);
    await app.server.settled;
    assert.deepEqual(fs.readFileSync(planFile), before);
  } finally {
    if (app?.server?.settled) await app.server.settled.catch(() => {});
    if (app) await new Promise(resolve => app.server.close(resolve));
    await new Promise(resolve => blocker.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
    console.error = originalError;
    assert.equal(logged.some(msg => msg.includes('Closing old work failed: ENOENT')), false);
  }
});

test('startup close failures other than missing notebook log to console.error', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-err-'));
  const planFile = path.join(dir, 'notebook.json');
  fs.writeFileSync(planFile, '{ invalid json');
  const logged = [];
  const originalError = console.error;
  console.error = (...args) => {
    logged.push(args.join(' '));
  };
  let app;
  try {
    app = await listen({ planFile, attemptsDir: path.join(dir, 'attempts'), port: 0, models: [], fetchPlans: async () => [] });
    await app.server.settled.catch(() => {});
    assert.ok(logged.some(msg => msg.includes('Closing old work failed:')));
    assert.equal(logged.some(msg => msg.includes('Closing old work failed: ENOENT')), false);
  } finally {
    console.error = originalError;
    if (app) await new Promise(resolve => app.server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

function captureConsoleError() {
  const logged = [];
  const originalError = console.error;
  console.error = (...args) => { logged.push(args.join(' ')); };
  return { logged, restore: () => { console.error = originalError; } };
}

test('a missing notebook on startup close stays quiet and the server still answers', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-missing-'));
  const planFile = path.join(dir, 'notebook.json');
  const { logged, restore } = captureConsoleError();
  let app;
  try {
    app = await listen({ planFile, attemptsDir: path.join(dir, 'attempts'), port: 0, models: [], fetchPlans: async () => [] });
    await app.server.settled;
    assert.equal(fs.existsSync(planFile), false);
    assert.equal((await fetch(app.url + '/')).status, 200);
    const planResponse = await fetch(app.url + '/api/plan', { signal: AbortSignal.timeout(2000) });
    assert.equal(planResponse.status, 500);
    assert.equal(logged.some(msg => msg.includes('Closing old work failed')), false);
  } finally {
    restore();
    if (app) await new Promise(resolve => app.server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('parallel startups with a missing notebook do not log ENOENT', async () => {
  const dirs = [0, 1].map(() => fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-parallel-')));
  const { logged, restore } = captureConsoleError();
  let apps = [];
  try {
    apps = await Promise.all(dirs.map(dir => listen({
      planFile: path.join(dir, 'notebook.json'),
      attemptsDir: path.join(dir, 'attempts'),
      port: 0,
      models: [],
      fetchPlans: async () => []
    })));
    await Promise.all(apps.map(app => app.server.settled));
    assert.equal(logged.some(msg => msg.includes('Closing old work failed')), false);
    const pages = await Promise.all(apps.map(app => fetch(app.url + '/')));
    assert.deepEqual(pages.map(response => response.status), [200, 200]);
  } finally {
    restore();
    await Promise.all(apps.map(app => new Promise(resolve => app.server.close(resolve))));
    for (const dir of dirs) fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('startup close silences a missing notebook code and still logs any other failure', async () => {
  const fixture = path.join(__dirname, '../fixtures/notebook.json');
  async function boot(save) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-close-'));
    const planFile = path.join(dir, 'notebook.json');
    fs.copyFileSync(fixture, planFile);
    const before = fs.readFileSync(planFile);
    const { logged, restore } = captureConsoleError();
    const server = createServer({
      planFile,
      attemptsDir: path.join(dir, 'attempts'),
      models: [],
      fetchPlans: async () => [],
      refreshModels: false,
      monitorIntervalMs: 600000,
      save
    });
    try {
      await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
      await server.settled;
      const status = (await fetch(`http://127.0.0.1:${server.address().port}/`)).status;
      return { logged, status, unchanged: fs.readFileSync(planFile).equals(before) };
    } finally {
      restore();
      await new Promise(resolve => server.close(resolve));
      fs.rmSync(dir, { recursive: true, force: true });
    }
  }
  const missing = await boot(async () => {
    const error = new Error('notebook is gone');
    error.code = 'ENOENT';
    throw error;
  });
  assert.equal(missing.logged.some(msg => msg.includes('Closing old work failed')), false);
  assert.equal(missing.status, 200);
  assert.equal(missing.unchanged, true);

  const namedOnly = await boot(async () => { throw new Error('ENOENT'); });
  assert.equal(namedOnly.logged.some(msg => msg.includes('Closing old work failed')), false);

  const locked = await boot(async () => {
    const error = new Error('EACCES: locked after ENOENT on the temp name');
    error.code = 'EACCES';
    throw error;
  });
  assert.ok(locked.logged.some(msg => msg.includes('Closing old work failed:')));
  assert.ok(locked.logged.some(msg => msg.includes('EACCES')));
  assert.equal(locked.status, 200);
});
