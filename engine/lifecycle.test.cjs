'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createOrchestrator, parseOrder } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
const { createSentinel } = require('./sentinel.js');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-lifecycle-'));
let readable = true;
let year = '2026-10-10T00:00:00Z';
let model = { id: 'worker', label: 'Verified worker', provider: 'antigravity', cli: 'gemini-3.1-pro-high', enabled: true };
const plan = { schema: 1, project: { id: 'demo', name: 'Demo', folder: dir }, revisions: [{ id: 1, open: [] }], tasks: [{ id: 'job-1', title: 'Build', detail: 'Original job', status: 'running' }] };
const killed = []; const launches = [];
function make() {
  const attempts = createAttemptEngine({ dir });
  return createOrchestrator({ dir, attempts, readPlan: async () => { if (!readable) throw new Error('Unreadable notebook'); return plan; }, models: [model], now: () => year,
    inspectFolder: () => ({ names: [], fileCount: 0, git: false }),
    sentinel: createSentinel(attempts, { isAlive: () => true }),
    spawnWorker: info => { launches.push(info); return { pid: 1000 + launches.length }; },
    isProcessAlive: () => true, killWorker: pid => { killed.push(pid); } });
}
(async () => {
  let orchestra = make();
  await orchestra.approve('demo');
  assert.equal(launches[0].worker.provider, 'antigravity');
  await orchestra.shutdown();
  const file = path.join(dir, 'checkpoints/demo.json');
  const saved = fs.readFileSync(file, 'utf8');
  assert.equal(JSON.parse(saved).reason, 'shutdown');
  assert.ok(killed.includes(1001));
  assert.equal((await orchestra.tick()).projects[0].status, 'checkpointed');
  year = '2056-10-10T00:00:00Z';
  model = { ...model, id: 'replacement', cli: 'gemini-3.8-flash-high' };
  orchestra = make();
  for (let n = 0; n < 4; n++) {
    const row = (await orchestra.tick()).projects[0];
    assert.equal(row.label, 'Paused'); assert.equal(row.canResume, true);
    assert.match(row.doing, /Checkpoint saved/);
    assert.equal(fs.readFileSync(file, 'utf8'), saved);
  }
  plan.tasks[0].id = 'wrong-job';
  assert.equal((await orchestra.resume('demo')).ok, false);
  assert.equal(launches.length, 1);
  plan.tasks[0].id = 'job-1';
  plan.tasks[0].detail = 'A different job with the same reused ID';
  assert.equal((await orchestra.resume('demo')).ok, false);
  plan.tasks[0].detail = 'Original job';
  const resumed = await orchestra.resume('demo');
  assert.equal(resumed.ok, true);
  assert.equal(launches[1].worker.id, 'replacement');
  assert.equal(launches[1].worker.cli, 'gemini-3.8-flash-high');
  assert.match(launches[1].job.detail, /Original job/);
  assert.match(launches[1].job.detail, /Resume from checkpoint/);
  assert.equal(parseOrder('Describe the job. Do not implement or edit files.', { projects: [resumed.snapshot.projects[0]] }), null);
  readable = false;
  await orchestra.shutdown();
  assert.equal(JSON.parse(fs.readFileSync(file)).detail, 'Original job');
  assert.equal(JSON.parse(fs.readFileSync(file)).reason, 'shutdown');
  const { createServer } = require('../server.cjs');
  const notebook = JSON.parse(fs.readFileSync(path.join(__dirname, '../fixtures/checkpoint-notebook.json')));
  notebook.project.folder = dir;
  const notebookFile = path.join(dir, 'http-plan.json');
  fs.writeFileSync(notebookFile, JSON.stringify(notebook));
  const httpDir = path.join(dir, 'http-attempts');
  // Its fixture holds a task saved before Phase 2, so this check runs the old execution path explicitly.
  const serverOptions = { phase2: false, planFile: notebookFile, attemptsDir: httpDir, fetchPlans: null,
    models: [{ ...model, kind: 'default' }], inspectFolder: () => ({ names: [], fileCount: 0 }),
    spawnWorker: () => ({ pid: 2001 }), isProcessAlive: () => true, killWorker: pid => killed.push(pid) };
  let server = createServer(serverOptions);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const response = await fetch(`http://127.0.0.1:${server.address().port}/api/orchestrator/approve`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: notebook.project.id }) });
  assert.equal(response.status, 200);
  await new Promise(resolve => server.close(resolve));
  const httpCheckpoint = path.join(httpDir, 'checkpoints', notebook.project.id + '.json');
  const httpSaved = fs.readFileSync(httpCheckpoint, 'utf8');
  assert.equal(JSON.parse(httpSaved).reason, 'shutdown');
  assert.ok(killed.includes(2001));
  server = createServer(serverOptions);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const restored = await (await fetch(`http://127.0.0.1:${server.address().port}/api/orchestrator`)).json();
  assert.equal(restored.projects[0].label, 'Paused');
  assert.equal(restored.projects[0].canResume, true);
  assert.equal(fs.readFileSync(httpCheckpoint, 'utf8'), httpSaved);
  await new Promise(resolve => server.close(resolve));
  console.log('OK: shutdown, years of persistence, repeated ticks, wrong-job rejection, fresh provider assignment on Resume.');
})().catch(error => { console.error(error); process.exitCode = 1; });
