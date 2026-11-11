'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createUpdateManager } = require('./app-updates.cjs');

// The version shown next to the name is known at start, before any update check.
test('the portable app knows its version from its install record', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-version-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  fs.writeFileSync(path.join(root, '.planwharf-install.json'), JSON.stringify({ schema: 1, version: 'v0.2.22', files: { 'server.cjs': 'a'.repeat(64) } }));
  assert.equal(createUpdateManager({ root, fetchImpl: async () => { throw new Error('no network'); } }).version(), 'v0.2.22');
});

test('the desktop app knows its version from the shell', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-version-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const saved = { desktop: process.env.PLANWHARF_DESKTOP, version: process.env.PLANWHARF_DESKTOP_VERSION };
  t.after(() => { for (const [key, value] of [['PLANWHARF_DESKTOP', saved.desktop], ['PLANWHARF_DESKTOP_VERSION', saved.version]]) if (value === undefined) delete process.env[key]; else process.env[key] = value; });
  process.env.PLANWHARF_DESKTOP = '1'; process.env.PLANWHARF_DESKTOP_VERSION = 'v0.2.22';
  assert.equal(createUpdateManager({ root }).version(), 'v0.2.22');
});

test('a source checkout has no version', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-version-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  assert.equal(createUpdateManager({ root }).version(), null);
});
