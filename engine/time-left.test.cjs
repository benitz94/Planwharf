'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { estimate, stepRecord, parseHistory, remember, historyText, HISTORY_LIMIT } = require('./time-left.cjs');
const { appendStepTime, readStepTimes, createExecutionBridge } = require('./execution.cjs');
const { createAttemptEngine } = require('./attempts.js');

const now = '2026-11-12T17:15:00.000Z';
const step = (over = {}) => ({ id: 'a', role: 'Build', model: 'opus', status: 'PENDING', dependencies: [], ...over });

test('no history uses 20 minutes for Build and 10 for Verify', () => {
  const build = estimate({ history: [], steps: [step()], now });
  assert.equal(build.minutes, 20);
  assert.equal(build.endsAt, '2026-11-12T17:35:00.000Z');
  assert.equal(build.sure, 'rough');
  assert.equal(build.longerThanUsual, false);
  assert.equal(estimate({ history: [], steps: [step({ role: 'Verify' })], now }).minutes, 10);
  assert.equal(estimate({ history: [], steps: [step({ status: 'COMPLETE' })], now }), null);
});

test('history of the same model is used before the role', () => {
  const history = [
    { role: 'Build', model: 'opus', minutes: 10 },
    { role: 'Build', model: 'opus', minutes: 12 },
    { role: 'Build', model: 'opus', minutes: 14 },
    { role: 'Build', model: 'sonnet', minutes: 40 },
    { role: 'Build', model: 'opus', minutes: 16 },
    { role: 'Build', model: 'opus', minutes: 18 }
  ];
  const same = estimate({ history, steps: [step()], now });
  assert.equal(same.minutes, 14);
  assert.equal(same.sure, 'based on 6 past steps');
  assert.equal(estimate({ history, steps: [step({ model: 'haiku' })], now }).minutes, 15);
});

test('a working step past its median is longer than usual and stays at 2 minutes', () => {
  const history = [8, 10, 12].map(minutes => ({ role: 'Build', model: 'opus', minutes }));
  const late = estimate({ history, steps: [step({ status: 'RUNNING', startedAt: '2026-11-12T16:55:00.000Z' })], now });
  assert.equal(late.longerThanUsual, true);
  assert.equal(late.minutes, 2);
  assert.equal(late.endsAt, '2026-11-12T17:17:00.000Z');
  assert.equal(late.sure, 'rough');
  const early = estimate({ history, steps: [step({ status: 'RUNNING', startedAt: '2026-11-12T17:12:00.000Z' })], now });
  assert.equal(early.longerThanUsual, false);
  assert.equal(early.minutes, 7);
});

test('parallel steps count once, as the longest', () => {
  const history = [
    { role: 'Build', model: 'opus', minutes: 30 },
    { role: 'Verify', model: 'astra', minutes: 10 }
  ];
  const steps = [
    step({ id: 'build', minutes: 30 }),
    step({ id: 'check', role: 'Verify', model: 'astra' })
  ];
  assert.equal(estimate({ history, steps, now }).minutes, 30);
});

test('steps in sequence add up', () => {
  const history = [
    { role: 'Build', model: 'opus', minutes: 30 },
    { role: 'Verify', model: 'astra', minutes: 10 }
  ];
  const steps = [
    step({ id: 'build' }),
    step({ id: 'check', role: 'Verify', model: 'astra', dependencies: ['build'] })
  ];
  assert.equal(estimate({ history, steps, now }).minutes, 40);
});

test('the history file keeps one line per step and the last 500', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-step-times-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const record = stepRecord({ role: 'Build', model: 'opus', size: 'light', files: 2, startedAt: '2026-11-12T17:00:00.000Z', endedAt: '2026-11-12T17:12:00.000Z', status: 'COMPLETE' });
  assert.deepEqual(record, { role: 'Build', model: 'opus', size: 'light', files: 2, minutes: 12, outcome: 'done' });
  assert.equal(stepRecord({ role: 'Build', model: 'opus', startedAt: '2026-11-12T17:00:00.000Z', endedAt: '2026-11-12T17:01:00.000Z', status: 'FAILED', modelChange: true }), null);
  for (let index = 0; index < HISTORY_LIMIT + 1; index += 1) {
    appendStepTime(dir, { role: 'Verify', model: 'astra', files: index, minutes: 1, outcome: index % 2 ? 'failed' : 'done', projectId: 'secret', prompt: 'do the job', path: 'D:/secret/project' });
  }
  const text = fs.readFileSync(path.join(dir, 'step-times.jsonl'), 'utf8');
  const lines = text.trim().split(/\n/);
  assert.equal(lines.length, HISTORY_LIMIT);
  assert.equal(JSON.parse(lines[0]).files, 1);
  assert.equal(JSON.parse(lines.at(-1)).files, HISTORY_LIMIT);
  assert.doesNotMatch(text, /secret|prompt|project/);
  assert.equal(readStepTimes(dir).length, HISTORY_LIMIT);
  assert.equal(remember(parseHistory(historyText(readStepTimes(dir))), null).length, HISTORY_LIMIT);
});

test('a finished step is recorded when it ends, without the project or the prompt', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-step-collect-'));
  const folder = path.join(dir, 'project');
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  fs.mkdirSync(folder);
  let tick = Date.parse('2026-11-12T17:00:00.000Z');
  const clock = () => new Date(tick += 60000).toISOString();
  const item = { project: { id: 'p1', name: 'Secret Project', folder }, revisions: [{ id: 1, direction: 'secret goal' }], tasks: [], home: { notes: [] } };
  const job = { id: 'job', title: 'Secret title', detail: 'Secret prompt do not store', revision_id: 1, kind: 'build' };
  const row = { dispatch: [] }, store = { usage: {} };
  const bridge = createExecutionBridge({ store, dir, rowFor: () => row, models: () => [{ id: 'opus', provider: 'codex', cli: 'gpt', label: 'Opus', enabled: true, available: true, cost: 1 }],
    leadId: () => 'opus', sessionId: 's1', attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), now: clock, persist: () => {},
    inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
    spawnWorker(info) {
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
      fs.writeFileSync(info.logPath, 'worked');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'done', filesChanged: ['src/a.js', 'src/b.js'], tests: [], blockers: [], nextAction: 'None' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli }));
      return { pid: 4242 };
    },
    isProcessAlive: () => false, killWorker: () => true });
  const parts = [{ modelId: 'opus', slice: 'Implement the page', size: 'light', dependsOn: [] }];
  const mega = bridge.propose(item, job, parts);
  bridge.approve(item.project.id, mega.id);
  bridge.advance(item, job, parts);
  const line = readStepTimes(dir)[0];
  assert.equal(line.role, 'Build');
  assert.equal(line.model, 'opus');
  assert.equal(line.size, 'light');
  assert.equal(line.files, 2);
  assert.equal(line.outcome, 'done');
  assert.ok(line.minutes >= 1);
  assert.doesNotMatch(fs.readFileSync(path.join(dir, 'step-times.jsonl'), 'utf8'), /Secret|prompt|Implement|p1/);
});

test('the snapshot carries timeLeft for work that still has a step to do', async t => {
  const { createOrchestrator } = require('./orchestrator.js');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-time-snap-'));
  const folder = path.join(dir, 'project');
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  fs.mkdirSync(folder);
  fs.writeFileSync(path.join(dir, 'step-times.jsonl'), historyText([{ role: 'Build', model: 'opus', files: 1, minutes: 30, outcome: 'done' }]));
  fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({ projects: { demo: { subTasks: [
    { id: 'b', title: 'Implement the page', status: 'PENDING', assignedModelId: 'opus', dependencies: [] },
    { id: 'v', title: 'Verify the work independently against the job', status: 'PENDING', assignedModelId: 'astra', dependencies: ['b'] }
  ] } } }));
  const plan = { schema: 2, projects: [
    { schema: 1, project: { id: 'demo', name: 'Demo', folder }, revisions: [{ id: 1, direction: 'Ship', state: 'Ready.', closed: [], open: [] }], tasks: [] },
    { schema: 1, project: { id: 'idle', name: 'Idle', folder }, revisions: [{ id: 1, direction: '', state: '', closed: [], open: [] }], tasks: [] }
  ] };
  const orchestra = createOrchestrator({ readPlan: async () => plan, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    models: [{ id: 'opus', label: 'Opus', enabled: true }, { id: 'astra', label: 'Astra', enabled: true }], dir, now: () => now,
    inspectFolder: () => ({ folder, fileCount: 0, names: [], git: false }) });
  const snap = await orchestra.snapshot();
  const row = snap.projects.find(item => item.id === 'demo');
  assert.equal(row.timeLeft.minutes, 40);
  assert.equal(row.timeLeft.endsAt, '2026-11-12T17:55:00.000Z');
  assert.equal(row.timeLeft.sure, 'rough');
  assert.equal(row.timeLeft.longerThanUsual, false);
  assert.equal(snap.projects.find(item => item.id === 'idle').timeLeft, null);
});
