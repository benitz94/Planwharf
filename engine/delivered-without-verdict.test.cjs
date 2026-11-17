'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { missingClaims } = require('./worker-result.cjs');
const { deliveredWithoutVerdict } = require('./execution.cjs');

const git = (cwd, args) => spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true });
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-delivered-'));
git(dir, ['init', '-q', '-b', 'main']);
git(dir, ['config', 'user.email', 't@example.com']);
git(dir, ['config', 'user.name', 'Test']);
fs.mkdirSync(path.join(dir, 'validation/job14-afresh'), { recursive: true });
fs.mkdirSync(path.join(dir, 'engine'), { recursive: true });
fs.writeFileSync(path.join(dir, 'validation/job14-afresh/log.txt'), 'old\n');
fs.writeFileSync(path.join(dir, 'engine/push-email.test.cjs'), 'keep\n');
git(dir, ['add', '-A']);
git(dir, ['commit', '-q', '-m', 'base']);
const before = git(dir, ['rev-parse', 'HEAD']).stdout.trim();
git(dir, ['rm', '-q', '-r', 'validation/job14-afresh']);
git(dir, ['commit', '-q', '-m', 'delete evidence']);
const after = git(dir, ['rev-parse', 'HEAD']).stdout.trim();

const result = { status: 'COMPLETE', filesChanged: ['validation/job14-afresh/log.txt'], blockers: [] };
assert.deepEqual(missingClaims(result, dir), [], 'a committed deletion is a delivery, not a missing file');
assert.deepEqual(missingClaims({ status: 'COMPLETE', filesChanged: ['no/such.txt'], blockers: [] }, dir), ['no/such.txt']);

const delivered = deliveredWithoutVerdict({
  folder: dir, before, after, exitCode: 0,
  scope: ['validation/job14-afresh'],
  names: ['validation/job14-afresh/log.txt']
});
assert.equal(delivered.filesChanged[0], 'validation/job14-afresh/log.txt');
assert.equal(deliveredWithoutVerdict({ folder: dir, before, after, exitCode: 0, scope: ['engine'], names: ['validation/job14-afresh/log.txt'] }), null);
assert.equal(deliveredWithoutVerdict({ folder: dir, before, after: before, exitCode: 0, names: ['a'] }), null);
assert.equal(deliveredWithoutVerdict({ folder: dir, before, after, exitCode: 1, names: ['validation/job14-afresh/log.txt'] }), null);

fs.rmSync(dir, { recursive: true, force: true });
console.log('PASS delivered without verdict');
