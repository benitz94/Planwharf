'use strict';
// Independent verification for Job 36: step outcomes
// Covers edge cases, error recovery, sanitization, concurrency, and execution paths.

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
  outcomesFile,
  canonicalRole,
  canonicalClass,
  resolveModelMeta
} = require('./step-outcomes.cjs');

test('stripPathsAndCode thoroughly strips Unix paths, Windows paths (slash & backslash), UNC, and relative paths', () => {
  const sample = 'Failure at /home/user/app/server.js and C:/projects/planwharf/index.ts plus D:\\secret\\data.json or \\\\wsl$\\Ubuntu\\tmp\\log and ../relative/path.cjs or ./local.mjs';
  const rec = stepOutcomeRecord({
    model: 'sonnet-4-6',
    refusal: sample
  });

  assert.ok(rec);
  assert.equal(rec.refusal.includes('/home/'), false);
  assert.equal(rec.refusal.includes('C:/'), false);
  assert.equal(rec.refusal.includes('D:\\'), false);
  assert.equal(rec.refusal.includes('\\\\wsl$'), false);
  assert.equal(rec.refusal.includes('../relative'), false);
  assert.equal(rec.refusal.includes('./local'), false);
  assert.equal(rec.refusal.includes('.js'), false);
  assert.equal(rec.refusal.includes('.ts'), false);
  assert.equal(rec.refusal.includes('.json'), false);
});

test('stripPathsAndCode removes code blocks, braces, backticks, and filenames without stripping model IDs or normal words', () => {
  const sample = 'Crash ```js\nconst x = { a: 1 };\n``` with `eval()` and { return false; } in module auth.js for claude-sonnet-4-6 on grok-4.7';
  const rec = stepOutcomeRecord({
    model: 'claude-sonnet-4-6',
    refusal: sample
  });

  assert.ok(rec);
  assert.equal(rec.refusal.includes('const x'), false);
  assert.equal(rec.refusal.includes('eval()'), false);
  assert.equal(rec.refusal.includes('return false'), false);
  assert.equal(rec.refusal.includes('auth.js'), false);
  assert.equal(rec.refusal.includes('claude-sonnet-4-6'), true);
  assert.equal(rec.refusal.includes('grok-4.7'), true);
});

test('stepOutcomeRecord sanitizes free-text fields (kind, size, refusal) against path and code leakage', () => {
  const rec = stepOutcomeRecord({
    model: 'gpt-5',
    kind: 'fix in /src/controllers/user.js',
    size: '12 files in C:/repo/src',
    refusal: 'Denied by policy at /etc/hosts'
  });

  assert.ok(rec);
  assert.equal(rec.kind.includes('/src'), false);
  assert.equal(rec.kind.includes('user.js'), false);
  assert.equal(String(rec.size).includes('C:/'), false);
  assert.equal(String(rec.size).includes('/repo'), false);
  assert.equal(rec.refusal.includes('/etc'), false);
  assert.equal(rec.refusal.includes('hosts'), false);
});

test('stepOutcomeRecord handles model objects, empty models, and case normalization', () => {
  // Empty or invalid model returns null
  assert.equal(stepOutcomeRecord({ model: null }), null);
  assert.equal(stepOutcomeRecord({ model: '' }), null);
  assert.equal(stepOutcomeRecord({ model: '   ' }), null);

  // Model passed as an object
  const objModel = { id: 'opus-4-6', companyId: 'claude', class: 'flagship' };
  const fromObj = stepOutcomeRecord({ model: objModel });
  assert.ok(fromObj);
  assert.equal(fromObj.model, 'opus-4-6');
  assert.equal(fromObj.company, 'claude');
  assert.equal(fromObj.class, 'flagship');

  // Role casing and alias canonicalization
  assert.equal(canonicalRole('build'), 'Build');
  assert.equal(canonicalRole('BUILDER'), 'Build');
  assert.equal(canonicalRole('verifier'), 'Verify');
  assert.equal(canonicalRole('reviewer'), 'Review');
  assert.equal(canonicalRole('audit'), 'Review');
  assert.equal(canonicalRole('researcher'), 'Research');
  assert.equal(canonicalRole('unknown_role'), 'Build');

  // Class canonicalization
  assert.equal(canonicalClass('premium'), 'flagship');
  assert.equal(canonicalClass('default'), 'mid');
  assert.equal(canonicalClass('light'), 'light');
  assert.equal(canonicalClass('anything_else'), 'mid');
});

test('stepOutcomeRecord normalizes negative/NaN/string values in duration, quota, and test counts', () => {
  const rec = stepOutcomeRecord({
    model: 'sol',
    durationMs: -5000,
    testsReported: -10,
    testsPassed: 5, // reported should be max(reported, passed) = 5
    quota: { inputTokens: -300, outputTokens: 1200, cachedInput: 'bad' }
  });

  assert.ok(rec);
  assert.equal(rec.durationMs, 0);
  assert.equal(rec.testsReported, 5);
  assert.equal(rec.testsPassed, 5);
  assert.deepEqual(rec.tests, { reported: 5, passed: 5 });
  assert.equal(rec.quota.inputTokens, 0);
  assert.equal(rec.quota.outputTokens, 1200);
  assert.equal(rec.quota.cachedInput, undefined);

  // Timestamps where endedAt is earlier than startedAt
  const reversedDates = stepOutcomeRecord({
    model: 'sol',
    startedAt: '2026-11-23T12:00:00Z',
    endedAt: '2026-11-23T11:00:00Z'
  });
  assert.equal(reversedDates.durationMs, 0);

  // Invalid timestamp strings
  const invalidDates = stepOutcomeRecord({
    model: 'sol',
    startedAt: 'invalid-date',
    endedAt: 'also-invalid'
  });
  assert.equal(invalidDates.durationMs, null);
});

test('readStepOutcomes gracefully recovers across corrupt JSON lines, empty lines, and non-existent folders', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-recover-'));
  try {
    // Non-existent directory returns []
    assert.deepEqual(readStepOutcomes(path.join(dir, 'does_not_exist')), []);

    const file = outcomesFile(dir);
    // Write valid line, blank line, corrupt JSON line, another blank line, valid line
    const content = [
      JSON.stringify(stepOutcomeRecord({ model: 'sonnet', role: 'Build', accepted: true })),
      '',
      '   ',
      '{ corrupt json line without closing bracket',
      'NOT JSON AT ALL',
      JSON.stringify(stepOutcomeRecord({ model: 'opus', role: 'Verify', accepted: false, fixed: 2 })),
      ''
    ].join('\n');

    fs.writeFileSync(file, content, 'utf8');

    const lines = readStepOutcomes(dir);
    assert.equal(lines.length, 2);
    assert.equal(lines[0].model, 'sonnet');
    assert.equal(lines[0].accepted, true);
    assert.equal(lines[1].model, 'opus');
    assert.equal(lines[1].fixed, 2);

    const summary = outcomesSummary(lines);
    assert.equal(summary.length, 2);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('high-concurrency writes: multiple concurrent appends produce intact JSONL lines with matching summary counts', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-concurrency-'));
  try {
    const totalWorkers = 60;
    const tasks = [];
    for (let i = 0; i < totalWorkers; i++) {
      const model = (i % 2 === 0) ? 'claude-sonnet-4-6' : 'gpt-5-mini';
      const accepted = (i % 3 !== 0);
      const fixed = accepted ? 0 : 1;
      tasks.push(new Promise((resolve) => {
        setImmediate(() => {
          appendStepOutcome(dir, {
            model,
            role: 'Build',
            accepted,
            fixed,
            testsReported: 5,
            testsPassed: accepted ? 5 : 4,
            durationMs: 1000 + i * 10
          });
          resolve();
        });
      }));
    }
    await Promise.all(tasks);

    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, totalWorkers);

    const summary = outcomesSummary(outcomes);
    assert.equal(summary.length, 2);

    const sonnet = summary.find(s => s.model === 'claude-sonnet-4-6');
    const gpt = summary.find(s => s.model === 'gpt-5-mini');
    assert.equal(sonnet.steps + gpt.steps, totalWorkers);
    assert.equal(sonnet.accepted + gpt.accepted + sonnet.fixed + gpt.fixed, totalWorkers);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge handles multiple build steps verified together', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-multibuild-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-multi', title: 'Multi build', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let pid = 500;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'm1', provider: 'claude', companyId: 'claude', cli: 'm1', label: 'M1', enabled: true, available: true, cost: 1 },
        { id: 'm2', provider: 'codex', companyId: 'codex', cli: 'm2', label: 'M2', enabled: true, available: true, cost: 1 },
        { id: 'verifier', provider: 'claude', companyId: 'claude', cli: 'verifier', label: 'Verifier', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'm1',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker log');
        fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });
        const isVerify = info.slice.includes('Verify');
        const sub = store.execution.subTasks.find(s => s.id === info.subTaskId);
        fs.writeFileSync(info.structuredResultPath, JSON.stringify({
          resultId: 'R-' + info.attemptId,
          attemptId: info.attemptId,
          subTaskId: info.subTaskId,
          megaTaskId: info.megaTaskId,
          status: 'COMPLETE',
          summary: isVerify ? 'Verify passed' : 'Built slice',
          filesChanged: isVerify ? [] : (sub?.scope || ['file.js']),
          tests: [{ command: 'test', status: 'PASSED', evidence: 'ok' }],
          blockers: [],
          nextAction: isVerify ? 'Done' : 'Next'
        }));
        fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
        return { pid: ++pid };
      },
      isProcessAlive: () => false,
      killWorker: () => true
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'm1', slice: 'Implement part 1', scope: ['f1.js'], dependsOn: [] },
      { modelId: 'm2', slice: 'Implement part 2', scope: ['f2.js'], dependsOn: [] },
      { modelId: 'verifier', slice: 'Verify the work independently against the job', dependsOn: [0, 1] }
    ]);
    bridge.approve('p1', mega.id);

    // Run part 1 and part 2, then verifier
    bridge.advance(item, job, []);
    bridge.advance(item, job, []);
    bridge.advance(item, job, []);

    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, 3);

    const m1Outcome = outcomes.find(o => o.model === 'm1');
    assert.ok(m1Outcome);
    assert.equal(m1Outcome.role, 'Build');
    assert.equal(m1Outcome.accepted, true);

    const m2Outcome = outcomes.find(o => o.model === 'm2');
    assert.ok(m2Outcome);
    assert.equal(m2Outcome.role, 'Build');
    assert.equal(m2Outcome.accepted, true);

    const verifierOutcome = outcomes.find(o => o.model === 'verifier');
    assert.ok(verifierOutcome);
    assert.equal(verifierOutcome.role, 'Verify');
    assert.equal(verifierOutcome.accepted, true);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge with retried subtask uses completed attempt result rather than failed initial attempt', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-retry-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-retry', title: 'Retry test', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let pid = 600;
    let attemptNum = 0;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'builder', provider: 'claude', companyId: 'claude', cli: 'builder', label: 'Builder', enabled: true, available: true, cost: 1 },
        { id: 'verifier', provider: 'claude', companyId: 'claude', cli: 'verifier', label: 'Verifier', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'builder',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        attemptNum++;
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker log ' + attemptNum);
        fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });

        if (info.slice.includes('Verify')) {
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'COMPLETE',
            summary: 'Verify clean',
            filesChanged: [],
            tests: [{ command: 'npm test', status: 'PASSED', evidence: 'pass' }],
            blockers: [],
            nextAction: 'Done'
          }));
          fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
        } else if (attemptNum === 1) {
          // First attempt of build fails with exit code 1 and no complete result
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'FAILED',
            summary: 'Crash on attempt 1',
            filesChanged: [],
            tests: [],
            blockers: ['Error'],
            nextAction: 'Fix'
          }));
          fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 1, provider: info.worker.provider, model: info.worker.cli }));
        } else {
          // Second attempt of build succeeds
          fs.writeFileSync(info.structuredResultPath, JSON.stringify({
            resultId: 'R-' + info.attemptId,
            attemptId: info.attemptId,
            subTaskId: info.subTaskId,
            megaTaskId: info.megaTaskId,
            status: 'COMPLETE',
            summary: 'Succeeded on attempt 2',
            filesChanged: ['a.js'],
            tests: [{ command: 'npm test', status: 'PASSED', evidence: 'pass' }, { command: 'test 2', status: 'PASSED', evidence: 'pass' }],
            blockers: [],
            nextAction: 'Ready'
          }));
          fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
        }
        return { pid: ++pid };
      },
      isProcessAlive: () => false,
      killWorker: () => true
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'builder', slice: 'Implement feature', scope: ['a.js'], dependsOn: [] },
      { modelId: 'verifier', slice: 'Verify the work independently against the job', dependsOn: [0] }
    ]);
    bridge.approve('p1', mega.id);

    // Attempt 1 runs and fails
    bridge.advance(item, job, []);
    const sub0 = store.execution.subTasks[0];
    assert.equal(sub0.status, 'FAILED');

    // Reset subtask to retry (e.g. lead restarts it)
    sub0.status = 'PENDING';
    sub0.resultId = null;

    // Attempt 2 runs and succeeds
    bridge.advance(item, job, []);
    assert.equal(sub0.status, 'COMPLETE');

    // Verify runs and succeeds
    bridge.advance(item, job, []);

    const outcomes = readStepOutcomes(dir);
    // There should be:
    // 1) Failed outcome for attempt 1
    // 2) Verify outcome
    // 3) Complete outcome for attempt 2 (recorded when verify completes)
    assert.equal(outcomes.length, 3);

    const builderOutcomes = outcomes.filter(o => o.model === 'builder');
    assert.equal(builderOutcomes.length, 2);

    const completeBuilderOutcome = builderOutcomes.find(o => o.accepted === true);
    assert.ok(completeBuilderOutcome);
    // Should have tests from attempt 2 (2 reported, 2 passed), NOT 0 from attempt 1
    assert.equal(completeBuilderOutcome.testsReported, 2);
    assert.equal(completeBuilderOutcome.testsPassed, 2);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge records no-verdict switch outcome when worker process times out twice', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-noverdict-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-nv', title: 'No verdict test', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let pid = 700;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'm1', provider: 'claude', companyId: 'claude', cli: 'm1', label: 'M1', enabled: true, available: true, cost: 1 },
        { id: 'm2', provider: 'codex', companyId: 'codex', cli: 'm2', label: 'M2', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'm1',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker log without exit');
        return { pid: ++pid };
      },
      isProcessAlive: () => false,
      killWorker: () => true,
      replacement: () => ({ id: 'm2', provider: 'codex', cli: 'm2' })
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'm1', slice: 'Implement part', scope: ['x.js'], dependsOn: [] }
    ]);
    bridge.approve('p1', mega.id);

    // Step starts
    bridge.advance(item, job, []);
    const sub0 = store.execution.subTasks[0];

    // First no-verdict reconcile: retry-same
    bridge.reconcile(mega);
    assert.equal(sub0.noVerdictTries, 1);
    assert.equal(sub0.assignedModelId, 'm1');

    // Second run starts
    bridge.advance(item, job, []);

    // Second no-verdict reconcile: switches to m2
    bridge.reconcile(mega);
    assert.equal(sub0.noVerdictTries, 2);
    assert.equal(sub0.assignedModelId, 'm2');

    // A switched outcome should have been recorded for m1
    const outcomes = readStepOutcomes(dir);
    assert.equal(outcomes.length, 1);
    assert.equal(outcomes[0].model, 'm1');
    assert.equal(outcomes[0].switch, true);
    assert.equal(outcomes[0].accepted, false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('executionBridge megatask completion records unverified steps cleanly without duplicates', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-unverified-'));
  const folder = path.join(dir, 'proj');
  fs.mkdirSync(folder, { recursive: true });
  const { createExecutionBridge } = require('./execution.cjs');
  const { createAttemptEngine } = require('./attempts.js');

  try {
    let clock = 1000;
    const now = () => new Date(clock += 10000).toISOString();
    const item = { project: { id: 'p1', name: 'Proj', folder }, revisions: [{ id: 1, direction: 'goal' }], tasks: [], home: { notes: [] } };
    const job = { id: 'job-single', title: 'Single build', detail: 'detail', revision_id: 1, kind: 'implementation' };
    const row = { dispatch: [] }, store = { usage: {} };

    let pid = 800;
    const bridge = createExecutionBridge({
      store,
      dir,
      rowFor: () => row,
      models: () => [
        { id: 'solo', provider: 'claude', companyId: 'claude', cli: 'solo', label: 'Solo', enabled: true, available: true, cost: 1 }
      ],
      leadId: () => 'solo',
      sessionId: 's1',
      attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
      now,
      persist: () => {},
      inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
      spawnWorker(info) {
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'worker log');
        fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true });
        fs.writeFileSync(info.structuredResultPath, JSON.stringify({
          resultId: 'R-' + info.attemptId,
          attemptId: info.attemptId,
          subTaskId: info.subTaskId,
          megaTaskId: info.megaTaskId,
          status: 'COMPLETE',
          summary: 'Built solo slice',
          filesChanged: ['solo.js'],
          tests: [{ command: 'npm test', status: 'PASSED', evidence: 'ok' }],
          blockers: [],
          nextAction: 'Done'
        }));
        fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, provider: info.worker.provider, model: info.worker.cli }));
        return { pid: ++pid };
      },
      isProcessAlive: () => false,
      killWorker: () => true
    });

    const mega = bridge.propose(item, job, [
      { modelId: 'solo', slice: 'Implement whole feature', scope: ['solo.js'], dependsOn: [] }
    ]);
    bridge.approve('p1', mega.id);

    // Step 0 runs and completes; megatask completes
    bridge.advance(item, job, []);
    bridge.advance(item, job, []);

    const outcomes = readStepOutcomes(dir);
    // Exactly 1 outcome recorded, no duplicates from completion hook
    assert.equal(outcomes.length, 1);
    assert.equal(outcomes[0].model, 'solo');
    assert.equal(outcomes[0].accepted, true);
    assert.equal(outcomes[0].fixed, 0);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
