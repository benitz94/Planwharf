'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  stepOutcomeRecord,
  appendStepOutcome,
  readStepOutcomes,
  outcomesSummary,
  summaryText,
  outcomesFile
} = require('./step-outcomes.cjs');

test('stepOutcomeRecord creates a complete record with all required fields', () => {
  const record = stepOutcomeRecord({
    model: 'claude-sonnet-4-6',
    company: 'claude',
    class: 'mid',
    role: 'Build',
    kind: 'implementation',
    size: 'normal',
    accepted: true,
    fixed: 0,
    testsReported: 12,
    testsPassed: 12,
    refusal: null,
    switch: false,
    durationMs: 45000,
    quota: { inputTokens: 1500, outputTokens: 800, cachedInput: 200 }
  });

  assert.equal(record.model, 'claude-sonnet-4-6');
  assert.equal(record.company, 'claude');
  assert.equal(record.class, 'mid');
  assert.equal(record.role, 'Build');
  assert.equal(record.kind, 'implementation');
  assert.equal(record.size, 'normal');
  assert.equal(record.accepted, true);
  assert.equal(record.fixed, 0);
  assert.equal(record.fixedByVerify, false);
  assert.equal(record.testsReported, 12);
  assert.equal(record.testsPassed, 12);
  assert.deepEqual(record.tests, { reported: 12, passed: 12 });
  assert.equal(record.refusal, null);
  assert.equal(record.switch, false);
  assert.equal(record.durationMs, 45000);
  assert.deepEqual(record.quota, { inputTokens: 1500, outputTokens: 800, cachedInput: 200 });
});

test('a step that was switched is recorded as such', () => {
  const record = stepOutcomeRecord({
    model: 'gpt-5-mini',
    company: 'codex',
    class: 'light',
    role: 'Build',
    switch: true,
    durationMs: 12000
  });

  assert.equal(record.switch, true);
  assert.equal(record.accepted, false);
  assert.equal(record.fixed, 0);
  assert.equal(record.refusal, null);
});

test('a step that was refused is recorded as such', () => {
  const record = stepOutcomeRecord({
    model: 'opus-4-6',
    company: 'claude',
    class: 'flagship',
    role: 'Verify',
    refusal: 'quota limit reached on 5-hour window',
    durationMs: 2000
  });

  assert.equal(record.refusal, 'quota limit reached on 5-hour window');
  assert.equal(record.accepted, false);
  assert.equal(record.switch, false);
});

test('a step fixed by verify records how much was fixed', () => {
  const record = stepOutcomeRecord({
    model: 'grok-4.7',
    company: 'grok',
    class: 'flagship',
    role: 'Build',
    accepted: false,
    fixed: 3,
    testsReported: 5,
    testsPassed: 4
  });

  assert.equal(record.accepted, false);
  assert.equal(record.fixed, 3);
  assert.equal(record.fixedByVerify, true);
});

test('no paths or code survive in the recorded outcome', () => {
  const record = stepOutcomeRecord({
    model: 'astra',
    company: 'codex',
    class: 'mid',
    role: 'Build',
    refusal: 'Failed at C:\\secret\\repo\\file.js with code `const x = 1;`'
  });

  const json = JSON.stringify(record);
  assert.equal(json.includes('C:\\'), false);
  assert.equal(json.includes('`'), false);
  assert.equal(json.includes('file.js'), false);
});

test('appendStepOutcome and readStepOutcomes roundtrip in data folder', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-outcomes-'));
  try {
    assert.equal(readStepOutcomes(dir).length, 0);

    appendStepOutcome(dir, {
      model: 'sonnet-4-6',
      company: 'claude',
      class: 'mid',
      role: 'Build',
      accepted: true,
      fixed: 0,
      durationMs: 30000
    });

    appendStepOutcome(dir, {
      model: 'sonnet-4-6',
      company: 'claude',
      class: 'mid',
      role: 'Build',
      accepted: false,
      fixed: 2,
      durationMs: 40000
    });

    appendStepOutcome(dir, {
      model: 'opus',
      company: 'claude',
      class: 'flagship',
      role: 'Verify',
      accepted: true,
      fixed: 0,
      durationMs: 20000
    });

    const lines = readStepOutcomes(dir);
    assert.equal(lines.length, 3);
    assert.equal(lines[0].model, 'sonnet-4-6');
    assert.equal(lines[0].accepted, true);
    assert.equal(lines[1].fixed, 2);
    assert.equal(lines[2].model, 'opus');

    const summary = outcomesSummary(lines);
    assert.equal(summary.length, 2);

    const sonnetSummary = summary.find(s => s.model === 'sonnet-4-6');
    assert.deepEqual(sonnetSummary, {
      model: 'sonnet-4-6',
      steps: 2,
      accepted: 1,
      fixed: 1
    });

    const opusSummary = summary.find(s => s.model === 'opus');
    assert.deepEqual(opusSummary, {
      model: 'opus',
      steps: 1,
      accepted: 1,
      fixed: 0
    });

    assert.equal(summaryText(sonnetSummary), '2 steps · 1 accepted as they were · 1 fixed by Verify');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge records build and verify outcomes when verify accepts', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-outcomes-bridge-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-1', title: 'Job 1', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let launchCount = 0;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'opus', provider: 'claude', companyId: 'claude', cli: 'opus', label: 'Opus', enabled: true, available: true, cost: 1 },
        { id: 'astra', provider: 'codex', companyId: 'codex', cli: 'astra', label: 'Astra', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'opus',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        launchCount++;
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker started');
        fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });
        if (info.slice.startsWith('Implement')) {
          fs.mkdirSync(path.join(folder, 'src'), { recursive: true });
          fs.writeFileSync(path.join(folder, 'src/a.js'), 'code');
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'COMPLETE',
            summary: 'Build complete',
            filesChanged: ['src/a.js'],
            tests: [{ command: 'npm test', status: 'PASSED', evidence: 'all ok' }],
            blockers: [],
            nextAction: 'Ready for verify'
          }));
          fs.writeFileSync(info.structuredResultPath + '.usage.json', JSON.stringify({ input: 500, output: 250 }));
        } else {
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'COMPLETE',
            summary: 'Verify passed without changes',
            filesChanged: [],
            tests: [{ command: 'npm test', status: 'PASSED', evidence: 'all ok' }],
            blockers: [],
            nextAction: 'Done'
          }));
          fs.writeFileSync(info.structuredResultPath + '.usage.json', JSON.stringify({ input: 300, output: 100 }));
        }
        fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
        return { pid: 100 + launchCount };
      },
      isProcessAlive: () => false,
      killWorker: () => true
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'opus', slice: 'Implement feature', scope: ['src/a.js'], dependsOn: [] },
      { modelId: 'astra', slice: 'Verify the work independently against the job', dependsOn: [0] }
    ]);
    bridge.approve('p1', mega.id);

    // Step 0 runs and completes; Step 1 is launched
    bridge.advance(item, job, []);
    // Step 1 runs and completes; MegaTask finishes
    bridge.advance(item, job, []);

    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, 2);

    const verifyRec = outcomes.find(o => o.role === 'Verify');
    assert.ok(verifyRec);
    assert.equal(verifyRec.model, 'astra');
    assert.equal(verifyRec.accepted, true);
    assert.equal(verifyRec.fixed, 0);

    const buildRec = outcomes.find(o => o.role === 'Build');
    assert.ok(buildRec);
    assert.equal(buildRec.model, 'opus');
    assert.equal(buildRec.accepted, true);
    assert.equal(buildRec.fixed, 0);
    assert.equal(buildRec.fixedByVerify, false);

    // Ensure no code or paths in file
    const text = fs.readFileSync(outcomesFile(dir), 'utf8');
    assert.equal(text.includes('src/a.js'), false);
    assert.equal(text.includes('npm test'), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge records fixed when verify fixes the build', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-outcomes-fix-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-2', title: 'Job 2', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let launchCount = 0;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'sonnet', provider: 'claude', companyId: 'claude', cli: 'sonnet', label: 'Sonnet', enabled: true, available: true, cost: 1 },
        { id: 'opus', provider: 'claude', companyId: 'claude', cli: 'opus', label: 'Opus', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'sonnet',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        launchCount++;
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker started');
        fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });
        if (info.slice.startsWith('Implement')) {
          fs.mkdirSync(path.join(folder, 'src'), { recursive: true });
          fs.writeFileSync(path.join(folder, 'src/b.js'), 'code');
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'COMPLETE',
            summary: 'Build complete',
            filesChanged: ['src/b.js'],
            tests: [{ command: 'npm test', status: 'PASSED', evidence: 'all ok' }],
            blockers: [],
            nextAction: 'Ready for verify'
          }));
        } else {
          fs.writeFileSync(path.join(folder, 'src/fix.js'), 'fix');
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'COMPLETE',
            summary: 'Fixed 2 bugs in implementation',
            filesChanged: ['src/b.js', 'src/fix.js'],
            tests: [{ command: 'npm test', status: 'PASSED', evidence: 'all ok' }],
            blockers: [],
            nextAction: 'Done'
          }));
        }
        fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
        return { pid: 200 + launchCount };
      },
      isProcessAlive: () => false,
      killWorker: () => true
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'sonnet', slice: 'Implement part', scope: ['src/b.js'], dependsOn: [] },
      { modelId: 'opus', slice: 'Verify the work independently against the job', dependsOn: [0] }
    ]);
    bridge.approve('p1', mega.id);

    // Step 0 runs and completes; Step 1 is launched
    bridge.advance(item, job, []);
    // Step 1 runs and completes; MegaTask finishes
    bridge.advance(item, job, []);

    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, 2);

    const buildRec = outcomes.find(o => o.role === 'Build');
    assert.ok(buildRec);
    assert.equal(buildRec.model, 'sonnet');
    assert.equal(buildRec.accepted, false);
    assert.equal(buildRec.fixed, 2);
    assert.equal(buildRec.fixedByVerify, true);

    const verifyRec = outcomes.find(o => o.role === 'Verify');
    assert.ok(verifyRec);
    assert.equal(verifyRec.model, 'opus');
    assert.equal(verifyRec.fixed, 2);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge records switched step when reassignRemaining is called', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-outcomes-switch-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-3', title: 'Job 3', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let pidCounter = 300;
    const livePids = new Set();
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'sonnet', provider: 'claude', companyId: 'claude', cli: 'sonnet', label: 'Sonnet', enabled: true, available: true, cost: 1 },
        { id: 'opus', provider: 'claude', companyId: 'claude', cli: 'opus', label: 'Opus', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'sonnet',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker started');
        const pid = ++pidCounter;
        livePids.add(pid);
        return { pid };
      },
      isProcessAlive: (pid) => livePids.has(pid),
      killWorker: (pid) => { livePids.delete(pid); return true; }
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'sonnet', slice: 'Implement part', scope: ['src/c.js'], dependsOn: [] }
    ]);
    bridge.approve('p1', mega.id);
    bridge.advance(item, job, []);

    const sub0 = store.execution.subTasks[0];
    assert.equal(sub0.status, 'RUNNING');

    // Reassign running subtask to opus
    bridge.reassignRemaining('p1', mega.id, { [sub0.id]: 'opus' });

    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, 1);
    assert.equal(outcomes[0].model, 'sonnet');
    assert.equal(outcomes[0].switch, true);
    assert.equal(outcomes[0].accepted, false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge records refusal when worker log shows quota refusal', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-outcomes-refuse-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-4', title: 'Job 4', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let pidCounter = 400;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'sonnet', provider: 'claude', companyId: 'claude', cli: 'sonnet', label: 'Sonnet', enabled: true, available: true, cost: 1 },
        { id: 'opus', provider: 'claude', companyId: 'claude', cli: 'opus', label: 'Opus', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'sonnet',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'Error: Sonnet requires usage credits.');
        fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 1, provider: info.worker.provider, model: info.worker.cli }));
        return { pid: ++pidCounter };
      },
      isProcessAlive: () => false,
      killWorker: () => true,
      onRefusal: () => {},
      replacement: () => ({ id: 'opus', provider: 'claude', cli: 'opus' })
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'sonnet', slice: 'Implement part', scope: ['src/d.js'], dependsOn: [] }
    ]);
    bridge.approve('p1', mega.id);
    bridge.advance(item, job, []);

    // Reconcile checks the failed worker log and reroutes refusal
    bridge.reconcile(mega);

    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, 1);
    assert.equal(outcomes[0].model, 'sonnet');
    assert.match(outcomes[0].refusal, /requires usage credits/);
    assert.equal(outcomes[0].accepted, false);
    assert.equal(outcomes[0].switch, false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('orchestrator snapshot includes stepOutcomes and aiUsage.stepOutcomes', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-outcomes-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    // Write sample outcomes
    appendStepOutcome(dir, { model: 'sonnet', company: 'claude', role: 'Build', accepted: true, fixed: 0 });
    appendStepOutcome(dir, { model: 'sonnet', company: 'claude', role: 'Build', accepted: false, fixed: 1 });
    appendStepOutcome(dir, { model: 'opus', company: 'claude', role: 'Verify', accepted: true, fixed: 0 });

    const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [] }] };
    const orchestra = createOrchestrator({
      dir,
      readPlan: async () => plan,
      models: [{ id: 'sonnet', provider: 'claude', cli: 'sonnet', label: 'Sonnet', enabled: true, available: true, cost: 1 }],
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') })
    });

    const snap = await orchestra.snapshot();
    assert.ok(Array.isArray(snap.stepOutcomes));
    assert.ok(Array.isArray(snap.aiUsage.stepOutcomes));
    assert.equal(snap.stepOutcomes.length, 2);

    const sonnet = snap.stepOutcomes.find(s => s.model === 'sonnet');
    assert.deepEqual(sonnet, { model: 'sonnet', steps: 2, accepted: 1, fixed: 1 });

    const opus = snap.stepOutcomes.find(s => s.model === 'opus');
    assert.deepEqual(opus, { model: 'opus', steps: 1, accepted: 1, fixed: 0 });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
