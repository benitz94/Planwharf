'use strict';
// A part limited to some files that changes others is stopped, and told why when it runs again.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { createAttemptEngine } = require('./attempts.js');
const { createExecutionBridge } = require('./execution.cjs');

const git = (folder, ...args) => execFileSync('git', args, { cwd: folder, stdio: 'pipe' });

// work(info) runs as the worker: it may write files and returns the files it reports as changed.
function fixture(t, { work = () => [], dirtyBefore = [], clock } = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-scope-check-')), folder = path.join(dir, 'project');
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  fs.mkdirSync(path.join(folder, 'src'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'src', 'quota.cs'), 'quota\n'); fs.writeFileSync(path.join(folder, 'src', 'skills.cs'), 'skills\n'); fs.writeFileSync(path.join(folder, 'notes.md'), 'notes\n');
  git(folder, 'init', '-q'); git(folder, 'add', '.'); git(folder, '-c', 'user.name=t', '-c', 'user.email=t@t', 'commit', '-qm', 'start');
  for (const file of dirtyBefore) fs.appendFileSync(path.join(folder, file), 'changed before the work\n');
  const item = structuredClone(require('../fixtures/starter-notebook.json').projects[0]);
  item.project = { id: 'scope-fixture', name: 'Scope fixture', folder };
  const job = { id: 'scope-job', projectId: item.project.id, title: 'Scope fixture', detail: 'Port two modules', revision_id: item.revisions.at(-1).id, status: 'running', executionVersion: 2 };
  item.tasks = [job];
  const models = [{ id: 'a', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, cost: 1 }, { id: 'b', provider: 'grok', cli: 'grok-4.7', enabled: true, available: true, cost: 1 }];
  const launches = [], row = { dispatch: [] }, store = { usage: {} };
  let tick = 0;
  const bridge = createExecutionBridge({ store, dir, rowFor: () => row, models: () => models, leadId: () => 'a', sessionId: 's1', attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    now: clock || (() => new Date(Date.UTC(2026, 10, 12, 1, 0, tick++)).toISOString()), persist: () => {},
    inspect: () => ({ git: true, dirty: dirtyBefore.map(file => ' M ' + file), names: [], fileCount: 3 }),
    spawnWorker(info) {
      launches.push(info);
      const filesChanged = work(info, folder) || [];
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Slice by ' + info.worker.id, filesChanged, tests: [], blockers: [], nextAction: 'None' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => true });
  const parts = [{ modelId: 'a', slice: 'Port quota', dependsOn: [], scope: ['src/quota.cs'] }, { modelId: 'b', slice: 'Port skills', dependsOn: [], scope: ['src/skills.cs'] }];
  const mega = bridge.propose(item, job, parts);
  bridge.approve(item.project.id, mega.id);
  const run = () => { bridge.advance(item, job, parts); bridge.advance(item, job, parts); };
  const sub = title => store.execution.subTasks.find(entry => entry.megaTaskId === mega.id && entry.title === title);
  const results = title => store.execution.results.filter(result => result.subTaskId === sub(title).id);
  return { bridge, item, job, mega, parts, run, sub, results, launches, store, folder };
}

test('a part that reports a file outside its scope is stopped and named', t => {
  const { run, sub, results, store } = fixture(t, { work: info => info.worker.id === 'a' ? ['src/quota.cs', 'src/skills.cs'] : ['src/skills.cs'] });
  run();
  assert.equal(sub('Port quota').status, 'BLOCKED');
  assert.deepEqual(sub('Port quota').scopeViolation, ['src/skills.cs']);
  assert.match(results('Port quota').at(-1).summary, /^Stopped: changed files outside its scope \(src\/skills\.cs\)\. Slice by a$/);
  assert.ok(results('Port quota').at(-1).blockers.includes('Changed files outside its scope: src/skills.cs.'));
  assert.equal(sub('Port skills').status, 'COMPLETE', 'the part that stayed in its files is not blamed');
  assert.ok(store.execution.timeline.some(event => event.type === 'SUBTASK_FAILED' && /Port quota: Stopped: changed files outside its scope/.test(event.summary)));
});

test('when the part runs again its worker is told what it touched last time', t => {
  const { run, sub, launches, bridge, item, job, parts } = fixture(t, { work: info => info.worker.id === 'a' ? ['src/quota.cs', 'notes.md'] : ['src/skills.cs'] });
  run();
  assert.equal(sub('Port quota').status, 'BLOCKED');
  assert.doesNotMatch(launches[0].job.detail, /Last time this part was stopped/);
  bridge.resume(item, job, { confirmChanges: true });
  bridge.advance(item, job, parts);
  const again = launches.filter(launch => launch.subTaskId === sub('Port quota').id).at(-1);
  assert.notEqual(again, launches[0], 'the part ran again');
  assert.match(again.job.detail, /Last time this part was stopped for changing files outside its scope: notes\.md\. Do not touch them/);
});

test('git is not used to blame a part while another part was working at the same time', t => {
  const { run, sub, results } = fixture(t, {
    work: (info, folder) => {
      if (info.worker.id === 'a') { fs.appendFileSync(path.join(folder, 'src', 'quota.cs'), 'ported\n'); fs.appendFileSync(path.join(folder, 'notes.md'), 'quietly changed\n'); return ['src/quota.cs']; }
      fs.appendFileSync(path.join(folder, 'src', 'skills.cs'), 'ported\n'); return ['src/skills.cs'];
    } });
  run();
  // Both parts ran together: a changed file cannot be put down to one of them, so only their reports count.
  assert.equal(sub('Port quota').status, 'COMPLETE', 'no blame from git while another part was working: ' + results('Port quota').at(-1).summary);
  assert.equal(sub('Port skills').status, 'COMPLETE');
});

test('a part alone in its time is checked against git; files dirty before the work do not count', t => {
  const { bridge, item, job, mega, store, sub, results } = fixture(t, {
    dirtyBefore: ['notes.md'],
    work: (info, folder) => {
      if (info.worker.id === 'a') { fs.appendFileSync(path.join(folder, 'src', 'quota.cs'), 'ported\n'); fs.writeFileSync(path.join(folder, 'src', 'extra.cs'), 'new\n'); return ['src/quota.cs']; }
      return ['src/skills.cs'];
    } });
  // Only the quota part runs: the skills part waits for it.
  const parts = [{ modelId: 'a', slice: 'Port quota', dependsOn: [], scope: ['src/quota.cs'] }, { modelId: 'b', slice: 'Port skills', dependsOn: [0], scope: ['src/skills.cs'] }];
  store.execution.subTasks.find(entry => entry.megaTaskId === mega.id && entry.title === 'Port skills').dependencies = [sub('Port quota').id];
  bridge.advance(item, job, parts); bridge.advance(item, job, parts);
  assert.equal(sub('Port quota').status, 'BLOCKED', results('Port quota').at(-1)?.summary);
  assert.deepEqual(sub('Port quota').scopeViolation, ['src/extra.cs'], 'notes.md was changed before the work and is not blamed');
});

test('a part with no scope (the check of the whole) may change any file', t => {
  const { bridge, item, job, store, mega, sub } = fixture(t, { work: () => ['src/quota.cs', 'src/skills.cs', 'notes.md'] });
  for (const entry of store.execution.subTasks.filter(entry => entry.megaTaskId === mega.id)) delete entry.scope;
  bridge.advance(item, job, []); bridge.advance(item, job, []);
  assert.equal(sub('Port quota').status, 'COMPLETE');
  assert.equal(sub('Port skills').status, 'COMPLETE');
});
