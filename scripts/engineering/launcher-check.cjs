'use strict';
// Windows only: starts the built Planwharf.exe with a copy of the starter notebook and no window,
// checks that the app answers, asks it to quit and checks that the launcher ends cleanly.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const root = path.join(__dirname, '..', '..');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function waitFor(check, label, timeout = 30000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { try { const value = await check(); if (value) return value; } catch { /* not ready yet */ } await pause(200); }
  throw new Error('Timed out: ' + label);
}
(async () => {
  assert.equal(process.platform, 'win32', 'Planwharf.exe runs on Windows only.');
  const exe = path.join(root, 'Planwharf.exe');
  assert.equal(fs.readFileSync(exe).subarray(0, 2).toString('ascii'), 'MZ', 'Build it first: node launch/build-exe.cjs');
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'pw launcher '));
  const plan = path.join(work, 'notebook with spaces.json'), attempts = path.join(work, 'attempts with spaces'), logs = path.join(work, 'logs');
  fs.copyFileSync(path.join(root, 'fixtures', 'starter-notebook.json'), plan);
  fs.mkdirSync(attempts);
  // A trailing backslash once escaped the closing quote of the next Windows argument: keep it in the test.
  const launcher = spawn(exe, ['--no-window', '--plan', plan, '--attempts-dir', attempts + path.sep, '--port', '0'],
    { cwd: work, windowsHide: true, stdio: 'ignore', env: { ...process.env, PLANWHARF_LAUNCH_LOG_DIR: logs } });
  let failed = null; launcher.on('error', error => { failed = error; });
  try {
    const lock = await waitFor(() => { if (failed) throw failed; const value = JSON.parse(fs.readFileSync(path.join(attempts, 'app-instance.json'), 'utf8')); return value.url && value; }, 'the launcher starts the app');
    assert.equal((await fetch(lock.url + '/')).status, 200);
    const notebook = await (await fetch(lock.url + '/api/plan')).json();
    assert.ok(notebook.plan.projects.length > 0, 'the app reads the notebook');
    assert.ok((await fetch(lock.url + '/api/app/quit', { method: 'POST', headers: { Origin: lock.url } })).ok);
    await waitFor(() => launcher.exitCode !== null, 'the launcher ends after quit');
    assert.equal(launcher.exitCode, 0, 'the launcher ends cleanly');
    assert.equal(fs.existsSync(path.join(attempts, 'app-instance.json')), false, 'the app releases its lock');
    assert.ok(fs.readdirSync(logs).length >= 1, 'the launcher keeps a log');
    console.log('OK: Planwharf.exe starts the app, the app answers, quits and the launcher ends cleanly.');
  } finally {
    if (launcher.exitCode === null) launcher.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
