'use strict';
// Notebook publish while another reader holds plan.json, and the failure paths around it.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { writePlan } = require('../server.cjs');

function scratch() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'pw-notebook-save-'));
}

function hold(file, share) {
  const child = spawn('powershell.exe', ['-NoProfile', '-Command',
    '$stream = [System.IO.File]::Open($env:HOLD, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::' + share + '); [Console]::Out.WriteLine("held"); [Console]::Out.Flush(); while ($true) { Start-Sleep -Seconds 60 }'],
  { env: { ...process.env, HOLD: file }, stdio: ['ignore', 'pipe', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  const ready = new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('reader did not open the notebook: ' + stderr)), 8000);
    const onExit = code => { clearTimeout(timer); reject(new Error('reader exited ' + code + ' ' + stderr)); };
    child.once('exit', onExit);
    child.stdout.on('data', chunk => {
      if (!String(chunk).includes('held')) return;
      clearTimeout(timer);
      child.removeListener('exit', onExit);
      resolve();
    });
  });
  return {
    ready,
    release() { if (child.exitCode === null) child.kill(); },
    async close() {
      const done = new Promise(resolve => {
        if (child.exitCode !== null || child.signalCode !== null) resolve();
        else child.once('exit', () => resolve());
      });
      if (child.exitCode === null && child.signalCode === null) child.kill();
      await done;
    }
  };
}

function removeDir(dir) {
  const pause = new Int32Array(new SharedArrayBuffer(4));
  let last;
  for (let attempt = 0; attempt < 30; attempt += 1) {
    try { fs.rmSync(dir, { recursive: true, force: true }); return; }
    catch (error) {
      last = error;
      if (!['EBUSY', 'EPERM', 'ENOTEMPTY'].includes(error.code)) throw error;
      Atomics.wait(pause, 0, 0, 50);
    }
  }
  throw last;
}

function temps(dir) {
  return fs.readdirSync(dir).filter(name => name.includes('.tmp'));
}

test('a reader that still allows writing does not block the notebook save', async () => {
  const dir = scratch();
  const file = path.join(dir, 'plan.json');
  fs.writeFileSync(file, '{"old":true}\n');
  const reader = hold(file, 'ReadWrite');
  try {
    await reader.ready;
    const plan = { schema: 1, note: 'published while open', pad: 'x'.repeat(8000) };
    await writePlan(file, plan);
    assert.equal(fs.readFileSync(file, 'utf8'), JSON.stringify(plan, null, 2) + '\n');
    assert.deepEqual(temps(dir), []);
  } finally { await reader.close(); removeDir(dir); }
});

test('a short lock is retried and the saved notebook is complete', async () => {
  const dir = scratch();
  const file = path.join(dir, 'plan.json');
  fs.writeFileSync(file, '{"old":true}\n');
  const reader = hold(file, 'ReadWrite');
  try {
    await reader.ready;
    const plan = { schema: 1, note: 'after the lock' };
    const pending = writePlan(file, plan);
    setTimeout(() => reader.release(), 120);
    await pending;
    assert.equal(fs.readFileSync(file, 'utf8'), JSON.stringify(plan, null, 2) + '\n');
    assert.deepEqual(temps(dir), []);
  } finally { await reader.close(); removeDir(dir); }
});

test('a reader that denies writing keeps the previous notebook and leaves no temporary file', async () => {
  const dir = scratch();
  const file = path.join(dir, 'plan.json');
  const original = '{"old":true}\n';
  fs.writeFileSync(file, original);
  const reader = hold(file, 'Read');
  try {
    await reader.ready;
    await assert.rejects(writePlan(file, { schema: 1, note: 'must not land' }), error => ['EBUSY', 'EPERM', 'EACCES'].includes(error.code));
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    assert.deepEqual(temps(dir), []);
  } finally { await reader.close(); removeDir(dir); }
});

test('two overlapping saves leave one complete notebook', async () => {
  const dir = scratch();
  const file = path.join(dir, 'plan.json');
  fs.writeFileSync(file, '{}\n');
  const first = { schema: 1, mark: 'A'.repeat(12000) };
  const second = { schema: 1, mark: 'B'.repeat(12000) };
  try {
    await Promise.all([writePlan(file, first), writePlan(file, second)]);
    const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
    assert.ok(saved.mark === first.mark || saved.mark === second.mark);
    assert.deepEqual(temps(dir), []);
  } finally { removeDir(dir); }
});

test('a missing notebook directory fails before a temporary file is left behind', async () => {
  const dir = scratch();
  const file = path.join(dir, 'missing', 'plan.json');
  try {
    await assert.rejects(writePlan(file, { schema: 1 }), { code: 'ENOENT' });
    assert.equal(fs.existsSync(path.join(dir, 'missing')), false);
    assert.deepEqual(temps(dir), []);
  } finally { removeDir(dir); }
});
