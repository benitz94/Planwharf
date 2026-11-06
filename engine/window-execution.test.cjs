'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { createServer } = require('../server.cjs');
const { createAppWindows } = require('./app-windows.cjs');
test('closing UI views neither kills workers nor disables backend monitoring', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-window-execution-'));
  const notebook = JSON.parse(fs.readFileSync(path.join(__dirname, '../fixtures/checkpoint-notebook.json')));
  notebook.project.folder = dir;
  const planFile = path.join(dir, 'notebook.json'); fs.writeFileSync(planFile, JSON.stringify(notebook));
  let launched, reads = 0; const killed = [];
  const windows = createAppWindows({ open: () => null });
  windows.heartbeat({ id: 'p', view: 'planwharf' }); windows.heartbeat({ id: 'o', view: 'orchestrator' });
  const server = createServer({planFile, appWindows: windows, monitorIntervalMs: 40, fetchPlans: null,
    models: [{ id: 'isolated', label: 'Isolated mock worker', provider: 'antigravity', cli: 'gemini-3.1-pro-high', enabled: true, kind: 'default' }],
    inspectFolder: () => { reads++; return { names: [], fileCount: 0 }; },
    spawnWorker: info => { launched = info; return { pid: 2001 }; }, isProcessAlive: () => { reads++; return true; }, killWorker: pid => killed.push(pid) });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  const url = `http://127.0.0.1:${server.address().port}`;
  const pulse = data => fetch(url + '/api/app/heartbeat', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
  try {
    await fetch(url + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: notebook.project.id }) });
    assert.ok(launched, 'only the mocked worker launches');
    await pulse({ id: 'p', view: 'planwharf', closed: true }); assert.deepEqual(killed, []);
    await pulse({ id: 'p2', view: 'planwharf' }); await pulse({ id: 'o', view: 'orchestrator', closed: true }); assert.deepEqual(killed, []);
    const before = reads;
    await new Promise(r => setTimeout(r, 160));
    assert.ok(reads > before, 'backend continues execution monitoring without Orchestrator polling');
    assert.deepEqual(killed, []);
    assert.equal((await (await fetch(url + '/api/orchestrator?readOnly=1')).json()).projects[0].status, 'watching');
    await pulse({ id: 'p2', view: 'planwharf', closed: true });
    assert.deepEqual(killed, []); assert.equal(server.listening, true);
  } finally { await new Promise(r => server.close(r)); assert.ok(killed.includes(2001), 'true shutdown executes existing checkpoint policy'); fs.rmSync(dir, { recursive: true, force: true }); }
});
