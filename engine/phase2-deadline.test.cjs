'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createExecutionBridge } = require('./execution.cjs');
const { executionNext } = require('./execution-next.cjs');
const { spawn } = require('node:child_process');
const { createServer } = require('../server.cjs');
const { createAppWindows } = require('./app-windows.cjs');
const { isProcessAlive, killWorker } = require('./runner.cjs');

function fixture(t, { timing = 'deadline', count = 3, parallel = false } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phase2-deadline-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const item = structuredClone(require('../fixtures/starter-notebook.json').projects[0]);
  item.project = { id: 'deadline-fixture', name: 'Deadline fixture', folder: dir };
  item.revisions.forEach(revision => { revision.open = []; });
  let clock = Date.parse('2026-10-13T12:00:00Z');
  const deadline = clock + 1000;
  const job = { id: 'deadline-job', projectId: item.project.id, title: 'Bounded fixture', detail: 'Isolated deadline regression', revision_id: item.revisions.at(-1).id, status: 'running', executionVersion: 2, timing, deadlineAt: new Date(deadline).toISOString() };
  item.tasks = [job];
  const store = { usage: {} }, row = { dispatch: [] }, launches = [], live = new Set(), killed = [];
  const models = [1, 2, 3].map(n => ({ id: `fixture-${n}`, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, cost: 1 }));
  let kill = pid => { killed.push(pid); return live.delete(pid); };
  let onLaunch = () => {};
  const options = { store, dir, rowFor: () => row, models: () => models, leadId: () => models[0].id, sessionId: 'deadline-session',
    attempts: createAttemptEngine({ dir }), now: () => new Date(clock).toISOString(), persist: () => {},
    inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
    spawnWorker: info => { const pid = 1000 + launches.length; launches.push({ ...info, pid }); live.add(pid); onLaunch(); return { pid }; },
    isProcessAlive: pid => live.has(pid), killWorker: pid => kill(pid) };
  let bridge = createExecutionBridge(options);
  const parts = Array.from({ length: count }, (_, index) => ({ modelId: models[index % 3].id, slice: `Slice ${index + 1}`, dependsOn: parallel || index === 0 ? [] : [index - 1] }));
  const mega = bridge.propose(item, job, parts);
  function finish(info) {
    const result = { resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE',
      summary: 'Completed isolated slice.', filesChanged: [], tests: [], blockers: [], nextAction: 'Review isolated result.', createdAt: options.now() };
    fs.writeFileSync(info.structuredResultPath, JSON.stringify(result));
    fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, provider: info.worker.provider, model: info.worker.cli, exitCode: 0 }));
    live.delete(info.pid);
  }
  return { item, job, mega, store, row, launches, live, killed, finish, deadline,
    setClock: value => { clock = value; }, setKill: value => { kill = value; }, onLaunch: value => { onLaunch = value; },
    approve: () => bridge.approve(item.project.id, mega.id),
    advance: (schedule = true) => bridge.advance(item, job, [], schedule),
    resume: () => bridge.resume(item, job, { megaTaskId: mega.id, checkpointId: mega.currentCheckpointId }),
    restart: () => { bridge = createExecutionBridge({ ...options, sessionId: 'restarted-session' }); },
    get bridge() { return bridge; } };
}

test('expired approval checkpoints without launching a worker', t => {
  const f = fixture(t);
  f.setClock(f.deadline); f.approve(); f.advance();
  assert.equal(f.launches.length, 0);
  assert.equal(f.mega.status, 'PAUSED');
  assert.equal(f.store.execution.checkpoints[0].reason, 'deadline');
  assert.equal(f.mega.deadlineAt, f.job.deadlineAt);
});

test('deadline preserves completed work, stops active work, and never launches dependencies', t => {
  const f = fixture(t);
  f.approve(); f.advance(); f.finish(f.launches[0]); f.advance();
  assert.equal(f.launches.length, 2);
  f.setClock(f.deadline);
  f.advance(false);
  assert.equal(f.killed.length, 0, 'Read-only reconciliation cannot stop workers');
  f.advance();
  const cp = f.store.execution.checkpoints.at(-1), subs = f.store.execution.subTasks;
  assert.deepEqual(f.killed, [f.launches[1].pid]);
  assert.equal(f.mega.status, 'PAUSED');
  assert.equal(cp.reason, 'deadline');
  assert.deepEqual(cp.completedSubTaskIds, [subs[0].id]);
  assert.ok(cp.activeAttemptIds.includes(f.launches[1].attemptId));
  assert.equal(subs[2].attemptIds.length, 0);
  assert.match(f.store.execution.results.at(-1).summary, /deadline/i);
  assert.match(executionNext(f.mega, f.store.execution), /deadline/i);
  for (let n = 0; n < 3; n++) f.advance();
  assert.equal(f.store.execution.checkpoints.length, 1, 'Deadline handling is idempotent');
  assert.equal(f.store.execution.timeline.filter(event => event.type === 'DEADLINE_REACHED').length, 1);
  f.restart();
  assert.equal(f.resume().ok, true); f.advance();
  assert.equal(f.launches.length, 3);
  assert.notEqual(f.launches[2].attemptId, f.launches[1].attemptId);
  assert.equal(f.launches[2].subTaskId, f.launches[1].subTaskId);
  assert.equal(subs[0].attemptIds.length, 1);
  assert.equal(f.killed.length, 1, 'Explicit Resume does not reapply the spent deadline');
});

test('complete evidence at the deadline completes instead of checkpointing', t => {
  const f = fixture(t, { count: 1 });
  f.approve(); f.advance(); f.finish(f.launches[0]); f.setClock(f.deadline); f.advance();
  assert.equal(f.mega.status, 'COMPLETE');
  assert.equal(f.store.execution.checkpoints.length, 0);
  assert.equal(f.killed.length, 0);
});

test('completion racing with deadline termination preserves valid results and completes', t => {
  const f = fixture(t, { count: 1 }); f.approve(); f.advance();
  f.setKill(() => { f.finish(f.launches[0]); return false; });
  f.setClock(f.deadline); f.advance();
  assert.equal(f.mega.status, 'COMPLETE');
  assert.equal(f.store.execution.results[0].status, 'COMPLETE');
  assert.equal(f.store.execution.checkpoints.length, 0);
  assert.equal(f.mega.deadlineError, null);
});

test('discretion mode does not enforce a stray deadline field', t => {
  const f = fixture(t, { timing: 'discretion' });
  f.approve(); f.advance(); f.setClock(f.deadline + 10000); f.advance();
  assert.equal(f.mega.status, 'RUNNING'); assert.equal(f.killed.length, 0);
});

test('an unapproved overdue proposal remains awaiting approval', t => {
  const f = fixture(t); f.setClock(f.deadline + 1); f.advance();
  assert.equal(f.mega.status, 'READY_FOR_APPROVAL');
  assert.equal(f.store.execution.checkpoints.length, 0); assert.equal(f.launches.length, 0);
});

test('failed termination stays visible, stops other slices, and retries without launching more work', t => {
  const f = fixture(t, { parallel: true }); f.approve(); f.advance();
  const first = f.launches[0].pid, second = f.launches[1].pid;
  f.setKill(pid => { if (pid === first) throw new Error('Isolated denied kill'); if (pid === second) return false; f.killed.push(pid); return f.live.delete(pid); });
  f.setClock(f.deadline); f.advance();
  assert.equal(f.mega.status, 'RUNNING');
  assert.equal(f.mega.pauseRequested, true);
  assert.equal(f.live.size, 2); assert.equal(f.row.workerAlive, true);
  assert.match(f.mega.deadlineError, /terminate/i);
  assert.match(executionNext(f.mega, f.store.execution), /terminate/i);
  assert.equal(f.store.execution.checkpoints.length, 0, 'A live process cannot be reported as checkpointed');
  f.restart();
  f.setKill(pid => { f.killed.push(pid); return f.live.delete(pid); }); f.advance();
  assert.equal(f.mega.status, 'PAUSED'); assert.equal(f.live.size, 0);
  assert.equal(f.store.execution.checkpoints.at(-1).reason, 'deadline');
  assert.equal(f.store.execution.checkpoints.at(-1).activeAttemptIds.length, 3);
  assert.equal(f.launches.length, 3);
  assert.equal(f.mega.deadlineError, null);
});

test('persisted older MegaTasks acquire their job deadline and expire after restart', t => {
  const f = fixture(t); f.approve(); f.advance();
  delete f.mega.timing; delete f.mega.deadlineAt;
  f.restart(); f.setClock(f.deadline + 1); f.advance();
  assert.equal(f.mega.status, 'PAUSED');
  assert.equal(f.store.execution.checkpoints.at(-1).reason, 'deadline');
  assert.equal(f.killed.length, 1);
});

test('successful explicit Stop clears a prior deadline failure before Resume', t => {
  const f = fixture(t, { count: 1 }); f.approve(); f.advance();
  f.setKill(() => false); f.setClock(f.deadline); f.advance();
  assert.ok(f.mega.deadlineError);
  f.setKill(pid => f.live.delete(pid));
  f.bridge.stop(f.item, f.job, f.mega.id);
  assert.equal(f.mega.status, 'PAUSED'); assert.equal(f.mega.deadlineError, null);
  assert.doesNotMatch(f.row.doing, /processes are still active/);
  assert.equal(f.resume().ok, true); f.advance();
  assert.equal(f.mega.status, 'RUNNING'); assert.equal(f.launches.length, 2);
});

test('a deadline crossing during dispatch prevents later parallel launches', t => {
  const f = fixture(t, { parallel: true });
  f.onLaunch(() => f.setClock(f.deadline)); f.approve(); f.advance();
  assert.equal(f.launches.length, 1);
  assert.equal(f.killed.length, 1); assert.equal(f.mega.status, 'PAUSED');
  assert.equal(f.store.execution.checkpoints.length, 1);
});

test('background monitoring expires a deadline with no open window and stops the actual process tree', { timeout: 20000 }, async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-deadline-background-'));
  const attemptsDir = path.join(dir, 'attempts'), planFile = path.join(dir, 'notebook.json');
  const deadlineAt = new Date(Date.now() + 2000).toISOString();
  const starter = require('../fixtures/starter-notebook.json').projects[0];
  const projects = ['deadline-target', 'unrelated-project'].map(id => {
    const item = structuredClone(starter);
    item.project = { id, name: id, folder: path.join(dir, id) }; fs.mkdirSync(item.project.folder);
    item.revisions.forEach(revision => { revision.open = []; });
    item.tasks = [{ id: id + '-job', projectId: id, title: 'Isolated process tree', detail: 'Background deadline regression', revision_id: item.revisions.at(-1).id,
      status: 'running', executionVersion: 2, timing: id === 'deadline-target' ? 'deadline' : 'discretion', ...(id === 'deadline-target' ? { deadlineAt } : {}),
      proposedSubTasks: [0, 1].map(index => ({ title: `Slice ${index + 1}`, detail: 'Isolated native Node worker', assignedModelId: 'process-fixture', dependsOn: index ? [0] : [] })) }];
    return item;
  });
  fs.writeFileSync(planFile, JSON.stringify({ schema: 2, projects }));
  const parentFile = path.join(dir, 'parent.cjs'), childFile = path.join(dir, 'child.cjs');
  fs.writeFileSync(childFile, 'setInterval(() => {}, 1000);');
  fs.writeFileSync(parentFile, "const fs=require('node:fs'),{spawn}=require('node:child_process');const child=spawn(process.execPath,[process.argv[2]],{stdio:'ignore',windowsHide:true});fs.writeFileSync(process.argv[3],String(child.pid));setInterval(()=>{},1000);");
  const launches = [], ownedPids = [];
  const windows = createAppWindows({ open: () => { throw new Error('This test must not open a browser'); } });
  const server = createServer({ planFile, attemptsDir, models: [{ id: 'process-fixture', provider: 'codex', cli: 'gpt-6-luna', kind: 'default', enabled: true, available: true, cost: 1, limit: 100 }],
    fetchPlans: null, refreshModels: false,monitorIntervalMs: 25, appWindows: windows,
    inspectFolder: () => ({ git: false, dirty: [], names: [], fileCount: 0 }), isProcessAlive, killWorker,
    spawnWorker: info => {
      const childPidFile = path.join(dir, info.attemptId + '.child-pid');
      const child = spawn(process.execPath, [parentFile, childFile, childPidFile], { detached: true, windowsHide: true, stdio: 'ignore' }); child.unref();
      ownedPids.push(child.pid); launches.push({ ...info, pid: child.pid, childPidFile }); return { pid: child.pid };
    } });
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  async function until(check, label) {
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) { if (check()) return; await pause(25); }
    throw new Error('Timeout: ' + label);
  }
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const url = `http://127.0.0.1:${server.address().port}`;
    const state = await (await fetch(url + '/api/orchestrator')).json();
    for (const item of state.projects) {
      const response = await fetch(url + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: item.id, megaTaskId: item.megaTaskId }) });
      assert.equal(response.status, 200, await response.text());
    }
    assert.equal(launches.length, 2);
    const target = launches.find(info => info.folder === projects[0].project.folder), other = launches.find(info => info.folder === projects[1].project.folder);
    await until(() => launches.every(info => fs.existsSync(info.childPidFile)), 'descendant processes start');
    for (const info of launches) { info.childPid = Number(fs.readFileSync(info.childPidFile, 'utf8')); ownedPids.push(info.childPid); assert.ok(isProcessAlive(info.pid) && isProcessAlive(info.childPid)); }
    assert.deepEqual(windows.status(), { planwharf: false, orchestrator: false });
    // Observe persisted state only: no client polls or requests drive scheduling.
    await until(() => {
      try { return JSON.parse(fs.readFileSync(path.join(attemptsDir, 'orchestrator.json'))).execution.checkpoints.some(cp => cp.projectId === projects[0].project.id && cp.reason === 'deadline'); }
      catch { return false; }
    }, 'background deadline checkpoint');
    assert.equal(isProcessAlive(target.pid), false);
    assert.equal(isProcessAlive(target.childPid), false);
    assert.ok(isProcessAlive(other.pid) && isProcessAlive(other.childPid), 'Unrelated project must keep running');
    assert.equal(launches.length, 2, 'No dependent worker starts after the deadline');
    const saved = JSON.parse(fs.readFileSync(path.join(attemptsDir, 'orchestrator.json'))).execution;
    const cp = saved.checkpoints.find(cp => cp.projectId === projects[0].project.id);
    assert.equal(cp.reason, 'deadline'); assert.ok(cp.activeAttemptIds.includes(target.attemptId));
    assert.equal(saved.megaTasks.find(mega => mega.projectId === projects[0].project.id).status, 'PAUSED');
    assert.equal(saved.megaTasks.find(mega => mega.projectId === projects[1].project.id).status, 'RUNNING');
    console.log('PASS: background deadline stopped the actual parent and descendant, saved a checkpoint, and preserved the unrelated worker without any open window or client polling.');
  } finally {
    if (server.listening) await new Promise(resolve => server.close(resolve));
    for (const pid of ownedPids) if (isProcessAlive(pid)) { try { killWorker(pid); } catch {} if (isProcessAlive(pid)) { try { process.kill(pid); } catch {} } }
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
