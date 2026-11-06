'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');
const { jobFingerprint } = require('./orchestrator.js');
const { ensureDefaultNotebook } = require('./notebook-file.cjs');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-task-completion-'));
let server;
let launches = 0;
const model = { id: 'fixture', label: 'Fixture', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'default', plan: 'ChatGPT', cost: 1, limit: 50 };
const file = path.join(dir, 'notebook.json');
const attemptsDir = path.join(dir, 'attempts');
const task = { id: 'task-1', projectId: 'demo', revision_id: 1, title: 'Inspect fixture', detail: 'Isolated inspection.', status: 'running' };
const project = { schema: 1, project: { id: 'demo', name: 'Demo', folder: dir }, revisions: [{ id: 1, mode: 'manual', title: '', createdAt: '2026-10-12T00:00:00Z', direction: 'Fixture', state: '', closed: [], open: [] }], tasks: [task] };
fs.writeFileSync(file, JSON.stringify(project));
fs.mkdirSync(path.join(attemptsDir, 'results'), { recursive: true });
const resultPath = path.join(attemptsDir, 'results', 'attempt-1.txt');
const slicePath = path.join(attemptsDir, 'results', 'attempt-1-s2.txt');
const raw = 'STATUS: COMPLETE\nEXIT: 0\n';
fs.writeFileSync(resultPath, raw);
fs.writeFileSync(slicePath, 'STATUS: WATCHING\n');
fs.writeFileSync(path.join(attemptsDir, 'attempts.json'), JSON.stringify({ nextId: 2, attempts: [{ id: 'attempt-1', taskId: task.id, projectId: 'demo', modelId: 'fixture', status: 'active', resultPath }] }));
const stateFile = path.join(attemptsDir, 'orchestrator.json');
fs.writeFileSync(stateFile, JSON.stringify({ mode: 'auto', projects: { demo: { status: 'watching', jobId: task.id, jobFingerprint: jobFingerprint(task), attemptId: 'attempt-1', resultPath, workerPid: 111, workerAlive: true, dispatch: [{ modelId: 'fixture', pid: 111, alive: true, resultPath }, { modelId: 'fixture', pid: 222, alive: true, resultPath: slicePath }] } } }));
async function start() {
  server = createServer({ phase2: false, planFile: file, attemptsDir, models: [model],refreshModels: false, fetchPlans: null, monitorIntervalMs: 60000,
    inspectFolder: () => ({ folder: dir, fileCount: 0, names: [] }),
    isProcessAlive: () => false, spawnWorker: () => { launches++; throw new Error('No real workers allowed.'); }, killWorker: () => { throw new Error('Historical PIDs must not be killed.'); } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return `http://127.0.0.1:${server.address().port}`;
}
async function get(base, route) { const response = await fetch(base + route, { signal: AbortSignal.timeout(5000) }); assert.equal(response.status, 200); return response.json(); }
(async () => {
  let base = await start();
  await get(base, '/api/orchestrator');
  assert.equal(JSON.parse(fs.readFileSync(file)).tasks[0].status, 'running', 'One complete slice cannot complete the job');
  assert.equal(JSON.parse(fs.readFileSync(path.join(attemptsDir, 'attempts.json'))).attempts[0].status, 'active');
  fs.writeFileSync(slicePath, raw);
  const beforeRevision = JSON.stringify(project.revisions);
  for (let n = 0; n < 3; n++) {
    const snapshot = await get(base, '/api/orchestrator');
    assert.equal(snapshot.projects[0].status, 'complete');
    assert.equal(snapshot.projects[0].workerAlive, false);
    assert.equal(snapshot.projects[0].canResume, false);
  }
  const completed = JSON.parse(fs.readFileSync(file));
  assert.equal(completed.tasks[0].status, 'complete');
  assert.equal(completed.tasks[0].attemptId, 'attempt-1');
  assert.equal(JSON.stringify(completed.revisions), beforeRevision);
  const record = JSON.parse(fs.readFileSync(path.join(attemptsDir, 'attempts.json'))).attempts[0];
  assert.equal(record.status, 'closed');
  assert.equal(record.closedReason, 'complete');
  assert.equal(fs.readFileSync(resultPath, 'utf8'), raw, 'Raw result evidence retained');
  const state = JSON.parse(fs.readFileSync(stateFile));
  assert.equal(state.projects.demo.workerAlive, false);
  assert.equal(state.projects.demo.dispatch[0].pid, 111, 'Historical PID retained');
  assert.ok(state.projects.demo.dispatch.every(part => !part.alive));
  await new Promise(resolve => server.close(resolve)); server = null;
  base = await start();
  assert.equal((await get(base, '/api/orchestrator')).projects[0].status, 'complete', 'Restart retains completion');
  assert.equal(launches, 0);
  await new Promise(resolve => server.close(resolve)); server = null;

  // Completion during a queued task creation must not deadlock or save stale tasks.
  const queuedDir = path.join(dir, 'queued-attempts');
  fs.mkdirSync(queuedDir);
  fs.writeFileSync(path.join(queuedDir, 'orchestrator.json'), JSON.stringify({ mode: 'auto', projects: {} }));
  fs.writeFileSync(file, JSON.stringify({ ...project, tasks: [] }));
  server = createServer({ phase2: false, planFile: file, attemptsDir: queuedDir, models: [model],refreshModels: false, fetchPlans: null, monitorIntervalMs: 60000,
    inspectFolder: () => ({ folder: dir, fileCount: 0, names: [] }), isProcessAlive: () => false,
    spawnWorker: info => { fs.writeFileSync(info.resultPath, raw); return { pid: 333 }; }, killWorker: () => { throw new Error('Completed PIDs must not be killed.'); } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  base = `http://127.0.0.1:${server.address().port}`;
  await get(base, '/api/orchestrator');
  const current = await get(base, '/api/plan');
  const response = await fetch(base + '/api/tasks', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: current.version, projectId: 'demo', task: { title: 'Inspect fixture', detail: 'Queued completion', revision_id: 1 } }), signal: AbortSignal.timeout(5000) });
  const created = await response.json();
  assert.equal(response.status, 201, JSON.stringify(created));
  assert.equal(created.plan.tasks[0].status, 'complete', 'Creation response reflects saved completion');
  assert.equal(JSON.parse(fs.readFileSync(file)).tasks[0].status, 'complete');
  await new Promise(resolve => server.close(resolve)); server = null;

  const root = path.join(dir, 'fresh');
  fs.mkdirSync(path.join(root, 'fixtures'), { recursive: true });
  fs.writeFileSync(path.join(root, 'fixtures', 'starter-notebook.json'), '{}');
  const defaultFile = path.join(root, 'plan.json');
  ensureDefaultNotebook(defaultFile, root);
  assert.equal(fs.readFileSync(defaultFile, 'utf8'), '{}');
  fs.writeFileSync(defaultFile, 'personal notebook bytes');
  ensureDefaultNotebook(defaultFile, root);
  assert.equal(fs.readFileSync(defaultFile, 'utf8'), 'personal notebook bytes');
  const explicit = path.join(root, 'missing.json');
  ensureDefaultNotebook(explicit, root);
  assert.equal(fs.existsSync(explicit), false);
  console.log('PASS: all slices required, claims closed, tasks completed, repeat polls and restart do not relaunch, historical evidence retained, default notebook never overwritten.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (server?.listening) await new Promise(resolve => server.close(resolve));
  const resolved = path.resolve(dir);
  assert.ok(resolved.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('pw-task-completion-'));
  fs.rmSync(resolved, { recursive: true, force: true });
});
