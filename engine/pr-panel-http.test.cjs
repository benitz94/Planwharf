'use strict';
// A pull request list that gh cannot read is still a panel message. The desktop app
// only shows a body it was able to read, so this must not be an HTTP error.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');

test('a pull request list gh cannot read is a panel message, not an HTTP error', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-http-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const planFile = path.join(dir, 'notebook.json');
  fs.writeFileSync(planFile, JSON.stringify({
    schema: 2,
    projects: [{ schema: 1, project: { id: 'biblioteca', name: 'Library', folder }, revisions: [{ id: 1, mode: 'manual', title: '', createdAt: '2026-11-21T00:00:00Z', direction: 'Ship it', state: '', closed: [], open: [] }], tasks: [] }]
  }));
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models: [], refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 60000 });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;
    const response = await fetch(base + '/api/prs?projectId=biblioteca&state=open&author=all&offset=0');
    const body = await response.json();
    assert.equal(response.status, 200);
    assert.equal(body.ok, false);
    assert.equal(typeof body.reason, 'string');
    assert.ok(body.reason.length > 0);
    const missing = await fetch(base + '/api/prs?projectId=missing');
    assert.equal(missing.status, 404);
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
