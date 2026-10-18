'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');
const { browserArguments, validateBrowser, readBrowserPreference } = require('./browser-settings.cjs');
const { openWindow, parseArgs } = require('../planwharf.cjs');
const { createServer } = require('../server.cjs');
const Plan = require('../plan.js');

test('browser choice persists through HTTP and rejects missing executables', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-browser-choice-'));
  const planFile = path.join(dir, 'notebook.json'); fs.writeFileSync(planFile, JSON.stringify(Plan.example()));
  const server = createServer({ planFile, models: [], skillsEnabled: false, refreshModels: false, fetchPlans: null });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;
    const save = value => fetch(base + '/api/settings/browser', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    assert.equal((await save({ id: 'default' })).status, 200);
    assert.deepEqual(readBrowserPreference(planFile), { id: 'default' });
    const custom = { id: 'custom', path: process.execPath, family: 'firefox' };
    assert.equal((await save(custom)).status, 200);
    assert.deepEqual((await (await fetch(base + '/api/settings/browser')).json()).selected, custom);
    assert.equal((await save({ id: 'custom', path: path.join(dir, 'missing.exe'), family: 'firefox' })).status, 400);
    assert.deepEqual(readBrowserPreference(planFile), custom, 'Invalid choices preserve the saved browser');
    assert.throws(() => validateBrowser({ id: 'custom', path: 'relative.exe', family: 'firefox' }));
    assert.throws(() => validateBrowser({ id: 'unknown' }));
    assert.equal(parseArgs(['node','planwharf.cjs','--browser','firefox']).browser, 'firefox');
  } finally { await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('Firefox launch uses an isolated Firefox profile; Chromium browsers retain app mode', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-browser-arguments-'));
  try {
    const url = 'http://127.0.0.1:4173/';
    let call;
    const child = new EventEmitter();
    openWindow(url, dir, { browser: { path: 'fixture-firefox', family: 'firefox' }, start: (...args) => { call = args; return child; } });
    assert.equal(call[0], 'fixture-firefox');
    assert.deepEqual(call[1], ['--no-remote','--profile',dir,'--new-window',url]);
    assert.ok(!call[1].some(arg => arg.startsWith('--app=')));
    for (const id of ['edge','chrome','brave','opera','chromium']) {
      const args = browserArguments({ id, family: 'chromium' }, url, dir);
      assert.ok(args.includes('--app=' + url)); assert.ok(args.includes('--user-data-dir=' + dir));
    }
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
