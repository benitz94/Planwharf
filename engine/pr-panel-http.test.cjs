'use strict';
// A pull request list that gh cannot read is still a panel message. The desktop app
// only shows a body it was able to read, so this must not be an HTTP error.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');
const pr = require('./pr-check.cjs');

function notebook(dir, folder, other) {
  return {
    schema: 2,
    projects: [folder, other].filter(Boolean).map(row => ({
      schema: 1, project: { id: row.id, name: row.name, folder: row.folder },
      revisions: [{ id: 1, mode: 'manual', title: '', createdAt: '2026-11-21T00:00:00Z', direction: 'Ship it', state: '', closed: [], open: [] }],
      tasks: []
    }))
  };
}

async function postFix(base, projectId, number) {
  const response = await fetch(base + '/api/prs/fix', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ projectId, number })
  });
  return { status: response.status, body: await response.json() };
}

test('a pull request list gh cannot read is a panel message, not an HTTP error', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-http-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const planFile = path.join(dir, 'notebook.json');
  fs.writeFileSync(planFile, JSON.stringify({
    schema: 2,
    projects: [
      { schema: 1, project: { id: 'biblioteca', name: 'Library', folder }, revisions: [{ id: 1, mode: 'manual', title: '', createdAt: '2026-11-21T00:00:00Z', direction: 'Ship it', state: '', closed: [], open: [] }], tasks: [] },
      { schema: 1, project: { id: 'empty', name: 'Empty' }, revisions: [{ id: 1, mode: 'manual', title: '', createdAt: '2026-11-21T00:00:00Z', direction: 'Ship it', state: '', closed: [], open: [] }], tasks: [] }
    ]
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
    const empty = await fetch(base + '/api/prs?projectId=empty');
    const emptyBody = await empty.json();
    assert.equal(empty.status, 200);
    assert.equal(emptyBody.ok, false);
    assert.match(emptyBody.reason, /folder/);
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('one project refuses a second fix while another project starts, then the first can be tried again', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-http-fix-'));
  const left = path.join(dir, 'left');
  const right = path.join(dir, 'right');
  fs.mkdirSync(left);
  fs.mkdirSync(right);
  const planFile = path.join(dir, 'notebook.json');
  fs.writeFileSync(planFile, JSON.stringify(notebook(dir, { id: 'biblioteca', name: 'Library', folder: left }, { id: 'other', name: 'Other', folder: right })));
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  const original = pr.prepareFix;
  pr.prepareFix = async () => { await gate; return { ok: false, reason: 'stopped' }; };
  const server = createServer({
    planFile, attemptsDir: path.join(dir, 'attempts'), models: [], refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 60000,
    spawnWorker: () => ({ pid: 1 }), isProcessAlive: () => false
  });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;
    const pending = postFix(base, 'biblioteca', 8);
    const started = await pending;
    assert.equal(started.status, 200);
    assert.equal(started.body.started, true);
    const same = await postFix(base, 'biblioteca', 8);
    assert.equal(same.status, 200);
    assert.equal(same.body.started, false);
    assert.match(same.body.reason, /already being fixed/);
    const otherNumber = await postFix(base, 'biblioteca', 9);
    assert.equal(otherNumber.status, 409);
    assert.match(otherNumber.body.reason, /already running/);
    const alongside = await postFix(base, 'other', 5);
    assert.equal(alongside.status, 200);
    assert.equal(alongside.body.started, true);
    const bad = await postFix(base, 'biblioteca', 1.5);
    assert.equal(bad.status, 409);
    release();
    let retry;
    const deadline = Date.now() + 5000;
    do {
      retry = await postFix(base, 'biblioteca', 8);
      if (retry.body.started) break;
      await new Promise(resolve => setTimeout(resolve, 25));
    } while (Date.now() < deadline);
    assert.equal(retry.status, 200);
    assert.equal(retry.body.started, true);
  } finally {
    release();
    pr.prepareFix = original;
    await new Promise(resolve => setTimeout(resolve, 300));
    await new Promise(resolve => server.close(resolve));
    for (let attempt = 0; attempt < 5; attempt += 1) {
      try { fs.rmSync(dir, { recursive: true, force: true }); break; }
      catch (error) {
        if (!['EBUSY', 'EPERM'].includes(error.code) || attempt === 4) break;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }
  }
});
