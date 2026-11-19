'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { spawnWorker, parseArgs, promptFor, isProcessAlive, killWorker } = require('./runner.cjs');
const { removeTempDir, waitForExit } = require('./temp-cleanup.cjs');
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
  // A loaded machine can take many seconds to start the detached runner; a half-written result reads as not ready.
  const readResult = () => { try { return fs.readFileSync(info.resultPath, 'utf8'); } catch (_) { return ''; } };
  for (const until = Date.now() + 60000; Date.now() < until && !/STATUS: COMPLETE/.test(readResult());) await pause(100);
  assert.match(readResult(), /STATUS: COMPLETE/);
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
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (override === undefined) delete process.env.PLANWHARF_CODEX; else process.env.PLANWHARF_CODEX = override;
  // The result is written before the detached runner exits and releases the folder as its cwd.
  if (!await waitForExit(isProcessAlive, pid)) killWorker(pid);
  const resolved = path.resolve(dir);
  assert.ok(resolved.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('pw-worker-startup-'));
  await removeTempDir(resolved);
  assert.equal(fs.existsSync(resolved), false);
}).then(cleanupChecks).catch(error => { console.error(error); process.exitCode = 1; });

// Cleanup must outlast a process that still holds the folder, and must not hide a folder that never frees.
async function cleanupChecks() {
  const held = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worker-startup-held-'));
  fs.writeFileSync(path.join(held, 'note.txt'), 'held');
  const holder = spawn(process.execPath, ['-e', "console.log('ready'); setInterval(() => {}, 1000)"], { cwd: held, stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true });
  try {
    await new Promise((resolve, reject) => { holder.stdout.once('data', resolve); holder.once('error', reject); holder.once('exit', resolve); });
    if (process.platform === 'win32') {
      // While the holder lives, a short wait must fail loudly with the folder and its file untouched.
      await assert.rejects(removeTempDir(held, { waitMs: 200, stepMs: 50 }), error => ['EBUSY', 'EPERM'].includes(error.code));
      assert.equal(fs.readFileSync(path.join(held, 'note.txt'), 'utf8'), 'held');
    }
    holder.kill();
    await new Promise(resolve => { holder.once('exit', resolve); if (holder.exitCode !== null) resolve(); });
    await removeTempDir(held);
    assert.equal(fs.existsSync(held), false);
  } finally {
    if (holder.exitCode === null) holder.kill();
    await removeTempDir(held).catch(() => {});
  }
  await removeTempDir(held); // An already removed folder is not an error.
  await assert.rejects(removeTempDir(path.join(__dirname, 'not-temp')), /inside the temp directory/);
  await assert.rejects(removeTempDir(os.tmpdir()), /inside the temp directory/);
  await assert.rejects(removeTempDir(''), /inside the temp directory/);
  assert.equal(await waitForExit(() => true, 1, 60, 20), false);
  assert.equal(await waitForExit(isProcessAlive, 99999999, 60, 20), true);
  console.log('Temp cleanup outlasts a process holding the folder and refuses paths outside temp.');
}
