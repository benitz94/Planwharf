'use strict';
// Temp cleanup must fail closed, then recover once Windows releases the folder.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { removeTempDir, waitForExit } = require('./temp-cleanup.cjs');

const temp = path.resolve(os.tmpdir());
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

function scratch(t) {
  const dir = fs.mkdtempSync(path.join(temp, 'pw-temp-cleanup-'));
  t.after(() => removeTempDir(dir));
  return dir;
}

test('a path outside temp is refused before anything is removed', async () => {
  const outside = path.join(temp, '..', 'pw-temp-cleanup-escape');
  await assert.rejects(removeTempDir(outside), /inside the temp directory/);
  assert.equal(fs.existsSync(outside), false);
  await assert.rejects(removeTempDir(temp + '-not-inside'), /inside the temp directory/);
  await assert.rejects(removeTempDir(temp), /inside the temp directory/);
});

test('a path that only looks like it leaves temp is still removed', async t => {
  const dir = scratch(t);
  fs.writeFileSync(path.join(dir, 'note.txt'), 'inside');
  const viaParent = path.join(dir, '..', path.basename(dir));
  assert.equal(await removeTempDir(viaParent), true);
  assert.equal(fs.existsSync(dir), false);
});

test('a missing path inside temp is already clean, and an invalid path fails at once', async t => {
  const dir = scratch(t);
  const file = path.join(dir, 'file.txt');
  fs.writeFileSync(file, 'x');
  assert.equal(await removeTempDir(path.join(file, 'child'), { waitMs: 5000, stepMs: 200 }), true);
  assert.equal(fs.readFileSync(file, 'utf8'), 'x');
  const started = Date.now();
  await assert.rejects(removeTempDir(path.join(dir, 'a\0b'), { waitMs: 5000, stepMs: 200 }), { code: 'ERR_INVALID_ARG_VALUE' });
  assert.ok(Date.now() - started < 400, 'an invalid path must not use the full wait');
  assert.equal(fs.readFileSync(file, 'utf8'), 'x');
});

test('a different drive-letter case is still inside temp on Windows', async t => {
  if (process.platform !== 'win32') return;
  const dir = scratch(t);
  fs.writeFileSync(path.join(dir, 'note.txt'), 'case');
  assert.equal(await removeTempDir(dir.toLowerCase()), true);
  assert.equal(fs.existsSync(dir), false);
});

test('removing a symlink removes the link and leaves the target', async t => {
  const dir = scratch(t);
  const target = path.join(dir, 'target');
  fs.mkdirSync(target);
  fs.writeFileSync(path.join(target, 'keep.txt'), 'keep');
  const link = path.join(dir, 'link');
  try { fs.symlinkSync(target, link, 'dir'); }
  catch (error) { if (error.code === 'EPERM') return; throw error; }
  assert.equal(await removeTempDir(link), true);
  assert.equal(fs.existsSync(link), false);
  assert.equal(fs.readFileSync(path.join(target, 'keep.txt'), 'utf8'), 'keep');
});

test('two removals of the same folder both finish', async t => {
  const dir = scratch(t);
  fs.writeFileSync(path.join(dir, 'note.txt'), 'race');
  const results = await Promise.all([removeTempDir(dir), removeTempDir(dir)]);
  assert.deepEqual(results, [true, true]);
  assert.equal(fs.existsSync(dir), false);
  assert.equal(await removeTempDir(dir), true);
});

test('cleanup waits out a holder, then removes the folder after it exits', async t => {
  if (process.platform !== 'win32') return;
  const dir = scratch(t);
  fs.writeFileSync(path.join(dir, 'note.txt'), 'held');
  const holder = spawn(process.execPath, ['-e', "console.log('ready'); setInterval(() => {}, 1000)"], {
    cwd: dir, stdio: ['ignore', 'pipe', 'ignore'], windowsHide: true
  });
  t.after(() => { if (holder.exitCode === null) holder.kill(); });
  await new Promise((resolve, reject) => { holder.stdout.once('data', resolve); holder.once('error', reject); });
  const started = Date.now();
  await assert.rejects(removeTempDir(dir, { waitMs: 0, stepMs: 50 }), error => ['EBUSY', 'EPERM'].includes(error.code));
  assert.ok(Date.now() - started < 500);
  assert.equal(fs.readFileSync(path.join(dir, 'note.txt'), 'utf8'), 'held');
  const pending = removeTempDir(dir, { waitMs: 8000, stepMs: 50 });
  await pause(150);
  assert.equal(fs.existsSync(dir), true, 'the folder stays while the holder is alive');
  holder.kill();
  assert.equal(await pending, true);
  assert.equal(fs.existsSync(dir), false);
});

test('waitForExit reports a dead pid and gives up on one that stays', async () => {
  assert.equal(await waitForExit(() => false, 1, 200, 20), true);
  const started = Date.now();
  assert.equal(await waitForExit(() => true, 1, 80, 30), false);
  assert.ok(Date.now() - started >= 70);
  const failed = Date.now();
  await assert.rejects(waitForExit(() => { throw new Error('probe failed'); }, 1, 2000, 50), /probe failed/);
  assert.ok(Date.now() - failed < 400, 'a probe error is not retried for the whole wait');
});
