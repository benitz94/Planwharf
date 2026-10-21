'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const vm = require('node:vm');
const { createRequire } = require('node:module');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const { spawnSync } = require('node:child_process');
const B = require('./run.cjs');
const R = require('./runtime.cjs');
const tasks = require('./tasks.cjs');

const valid = { pairs: 1, timeoutMs: 1000, maxCalls: 1 };
test('live limits accept endpoints and reject missing, fractional and excessive budgets', () => {
  B.bounded(valid); B.bounded({ pairs: 5, timeoutMs: 900000, maxCalls: 12 });
  for (const [key, bad] of Object.entries({ pairs: [undefined, 0, 6, 1.5, NaN], timeoutMs: [undefined, 999, 900001, Infinity], maxCalls: [undefined, 0, 13, 1.5] })) {
    for (const value of bad) assert.throws(() => B.bounded({ ...valid, [key]: value }));
  }
});

test('acceptance requires completed transport; timeouts, blocks and defects remain distinct', () => {
  const grade = { passed: 6, total: 6 };
  assert.equal(B.completion({ completed: true }, grade), 'accepted');
  assert.equal(B.completion({ completed: true }, { passed: 5, total: 6 }), 'defective');
  assert.equal(B.completion({ completed: false }, grade), 'failed');
  assert.equal(B.completion({ completed: true, blocked: true }, grade), 'blocked');
  assert.equal(B.completion({ completed: true, blocked: true, timedOut: true }, grade), 'timed_out');
});

test('schedule pairs both arms once, counterbalances order and reserves faults for larger tasks', () => {
  const pilot = B.schedule(1);
  assert.equal(pilot.length, 3);
  assert.deepEqual(pilot.map(p => p.arms), [['direct', 'planwharf'], ['planwharf', 'direct'], ['direct', 'planwharf']]);
  assert.deepEqual(pilot.map(p => p.scenario), ['baseline', 'implementation-interruption', 'implementation-interruption']);
  const full = B.schedule(5);
  assert.equal(full.length, 15); assert.equal(new Set(full.map(p => p.pairId)).size, 15);
  for (const task of tasks.listTasks()) {
    const pairs = full.filter(p => p.taskId === task.id);
    for (let i = 0; i < pairs.length; i++) {
      assert.deepEqual([...pairs[i].arms].sort(), ['direct', 'planwharf']);
      if (i) assert.notDeepEqual(pairs[i].arms, pairs[i - 1].arms);
    }
  }
});

test('usage sums complete turns and preserves missing, invalid and failed receipts as unknown', () => {
  const turn = usage => ({ type: 'turn.completed', usage });
  assert.equal(R.usageFromEvents([]), null);
  const usage = R.usageFromEvents([turn({ input_tokens: 100, output_tokens: 20, cached_input_tokens: 50 }), turn({ input_tokens: 30, output_tokens: 5, cached_input_tokens: 0 })]);
  assert.deepEqual(usage, { inputTokens: 130, outputTokens: 25, cachedInputTokens: 50, reasoningTokens: null });
  assert.equal(R.usageFromEvents([turn({ input_tokens: 0, output_tokens: 0 })]).inputTokens, 0);
  assert.equal(R.usageFromEvents([turn({ input_tokens: -1, output_tokens: 2 })]).inputTokens, null);
  assert.equal(R.usageFromEvents([turn({ input_tokens: 1, output_tokens: 2 }), { type: 'turn.failed' }]), null);
});

test('independent starter repositories have identical content and Git history', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-benchmark-run-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  for (const task of tasks.listTasks()) {
    const one = path.join(root, task.id, 'direct'), two = path.join(root, task.id, 'planwharf');
    assert.equal(tasks.setupTask(task.id, one).starterHash, tasks.setupTask(task.id, two).starterHash);
    assert.equal(R.sourceDigest(one), R.sourceDigest(two));
    assert.equal(R.initializeGit(one), R.initializeGit(two));
    fs.appendFileSync(path.join(one, task.module), '\n// Trial-local edit\n');
    assert.notEqual(R.sourceDigest(one), R.sourceDigest(two));
  }
});

test('default command never invokes subprocesses or spends provider access', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-benchmark-default-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const guard = path.join(root, 'deny-subprocess.cjs');
  fs.writeFileSync(guard, "const p=require('node:child_process');for(const k of ['spawn','spawnSync','exec','execSync','execFile','execFileSync','fork'])p[k]=()=>{throw Error('No provider or subprocess permitted')};\n");
  const result = spawnSync(process.execPath, ['--require', guard, path.join(__dirname, 'run.cjs')], { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 10000 });
  assert.equal(result.status, 0, result.stderr);
  assert.match(result.stdout, /Offline:/); assert.match(result.stdout, /Opt-in live:/);
  assert.deepEqual(fs.readdirSync(root), ['deny-subprocess.cjs']);
});

test('worker session receipt uses the latest cumulative usage without double-counting snapshots', t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-benchmark-receipt-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const id = '12345678-1234-1234-1234-123456789abc', sessions = path.join(root, 'sessions', 'day');
  fs.mkdirSync(sessions, { recursive: true });
  const snapshot = total => JSON.stringify({ type: 'event_msg', payload: { type: 'token_count', info: { total_token_usage: total } } });
  fs.writeFileSync(path.join(sessions, 'rollout-' + id + '.jsonl'), [
    snapshot({ input_tokens: 10, output_tokens: 2 }), 'not JSON',
    snapshot({ input_tokens: 40, output_tokens: 8, cached_input_tokens: 20, reasoning_output_tokens: 3 })
  ].join('\n'));
  assert.deepEqual(B.receiptSession(id, root), { inputTokens: 40, outputTokens: 8, cachedInputTokens: 20, reasoningTokens: 3 });
  assert.equal(B.receiptSession('../other-profile', root), null);
  assert.equal(B.receiptSession('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', root), null);
});

// Local CLI event boundary fixture; no provider, authentication, network, or
// actual CLI executable is used.
function runtimeWithEvents(events, exitCode, observed) {
  const filename = path.join(__dirname, 'runtime.cjs'), actualRequire = createRequire(filename);
  const injectedRequire = name => {
    if (name === '../../engine/provider-runtime.cjs') return { executable: () => 'offline-cli-fixture', providerEnvironment: (_provider, env) => env };
    if (name === '../../engine/runner.cjs') return { killWorker: () => false };
    if (name === 'node:child_process') return { spawnSync, spawn: (command, args, options) => {
      Object.assign(observed, { command, args, options });
      const child = new EventEmitter(); child.stdin = new PassThrough(); child.stdout = new PassThrough(); child.stderr = new PassThrough();
      queueMicrotask(() => { child.emit('spawn'); child.stdout.write(events.map(e => JSON.stringify(e)).join('\n') + '\n'); child.emit('close', exitCode, null); });
      return child;
    } };
    return actualRequire(name);
  };
  const module = { exports: {} };
  vm.runInThisContext('(function(require,module,exports){' + fs.readFileSync(filename, 'utf8') + '\n})', { filename })(injectedRequire, module, module.exports);
  return module.exports;
}

test('CLI event transport requires exit zero, retains structured errors and enforces quiet permissions', async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-benchmark-events-test-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const complete = { type: 'turn.completed', usage: { input_tokens: 10, output_tokens: 2 } };
  const cases = [
    { events: [complete], exit: 0, expected: true },
    { events: [complete], exit: 1, expected: false },
    { events: [complete, { type: 'turn.failed', error: { message: '429 quota exceeded' } }], exit: 0, expected: false },
    { events: [{ type: 'error', message: 'authentication unavailable' }], exit: 0, expected: false }
  ];
  for (let i = 0; i < cases.length; i++) {
    const item = cases[i], observed = {}, runtime = runtimeWithEvents(item.events, item.exit, observed);
    const result = await runtime.runCli({ workspace: root, prompt: 'Offline fixture', model: 'gpt-offline-fixture', artifactDir: path.join(root, String(i)), timeoutMs: 1000 });
    assert.equal(result.completed, item.expected);
    assert.equal(observed.options.windowsHide, true);
    assert.ok(observed.args.includes('sandbox_workspace_write.network_access=false'));
    assert.ok(observed.args.includes('sandbox_workspace_write.writable_roots=[]'));
    assert.ok(observed.args.includes('approval_policy="never"'));
    if (i === 2) assert.deepEqual(result.failure, ['429 quota exceeded']);
    if (i === 3) assert.deepEqual(result.failure, ['authentication unavailable']);
  }
  await new Promise(setImmediate);
});
