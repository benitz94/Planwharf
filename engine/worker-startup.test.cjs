'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnWorker, parseArgs, promptFor, isProcessAlive, killWorker } = require('./runner.cjs');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worker-startup-'));
const override = process.env.PLANWHARF_CODEX;
let pid;
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
(async () => {
  process.env.PLANWHARF_CODEX = process.execPath;
  fs.writeFileSync(path.join(dir, 'exec'), `const fs=require('node:fs'); let text=''; process.stdin.setEncoding('utf8'); process.stdin.on('data',chunk=>text+=chunk); process.stdin.on('end',()=>{fs.writeFileSync('received.json',JSON.stringify({text,args:process.argv.slice(2)}));fs.writeFileSync('report.md','Fixture research report');console.log('Isolated fake provider completed');});`);
  const model = { id: 'fixture-luna', label: 'Fixture Luna', provider: 'codex', cli: 'gpt-6-luna' };
  const detail = 'Telemetry research Unicode 🏎️ è 日本語\n'.repeat(6000);
  assert.ok(detail.length > 32767);
  const info = { folder: dir, resultPath: path.join(dir, 'result.txt'), logPath: path.join(dir, 'worker.log'), worker: model, attemptId: 'isolated-attempt', slice: 'Read repositories', job: { title: 'Isolated research', detail, kind: 'research' } };
  const launched = spawnWorker(info);
  pid = launched.pid;
  assert.ok(Number.isInteger(pid), 'Detached runner spawned successfully with long detail');
  assert.ok(fs.existsSync(launched.inputPath));
  const parsed = parseArgs(['node', 'runner', '--job-file', launched.inputPath]);
  assert.equal(parsed.detail, detail);
  assert.equal(parsed.kind, 'research');
  for (let n = 0; n < 150; n++) { if (fs.existsSync(info.resultPath) && /STATUS: COMPLETE/.test(fs.readFileSync(info.resultPath, 'utf8'))) break; await pause(100); }
  assert.match(fs.readFileSync(info.resultPath, 'utf8'), /STATUS: COMPLETE/);
  const received = JSON.parse(fs.readFileSync(path.join(dir, 'received.json'), 'utf8'));
  assert.equal(received.text, promptFor(parsed));
  assert.ok(received.args.includes('--sandbox'), 'Research retains source sandbox');
  assert.ok(received.args.every(value => value.length < 32767));
  assert.equal(received.args.at(-1), '-');
  assert.equal(fs.readFileSync(path.join(dir, 'report.md'), 'utf8'), 'Fixture research report');
  for (const invalid of [{ ...parsed, detail: 12 }, { ...parsed, model: 'wrong' }, { ...parsed, folder: '' }]) {
    const file = path.join(dir, 'invalid.json'); fs.writeFileSync(file, JSON.stringify(invalid));
    assert.throws(() => parseArgs(['node', 'runner', '--job-file', file]));
  }
  assert.equal(parseArgs(['node', 'runner', '--folder', dir, '--kind', 'research']).kind, 'research');
  console.log('Detached worker startup receives full long Unicode research job via preserved input file.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => {
  if (override === undefined) delete process.env.PLANWHARF_CODEX; else process.env.PLANWHARF_CODEX = override;
  if (isProcessAlive(pid)) killWorker(pid);
  const resolved = path.resolve(dir);
  assert.ok(resolved.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('pw-worker-startup-'));
  // Windows can keep the folder locked for a moment after the detached worker exits.
  fs.rmSync(resolved, { recursive: true, force: true, maxRetries: 20, retryDelay: 50 });
});
