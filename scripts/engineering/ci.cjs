'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const output = path.resolve(process.env.PLANWHARF_CI_LOGS || path.join(root, 'engineering-logs'));
fs.mkdirSync(output, { recursive: true });
const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-ci-'));
const env = { PATH: process.env.PATH, SystemRoot: process.env.SystemRoot, HOME: temporary, USERPROFILE: temporary, TMPDIR: temporary, TMP: temporary, TEMP: temporary, CI: 'true' };
let failures = 0;
function run(label, executable, args) {
  // The isolated test home has no operator trust config; scope trust to this checkout.
  const commandArgs = executable === 'git' ? ['-c', 'safe.directory=' + root, ...args] : args;
  const result = spawnSync(executable, commandArgs, { cwd: root, env, windowsHide: true, encoding: 'utf8', timeout: 60000, maxBuffer: 8 * 1024 * 1024 });
  const log = (result.stdout || '') + (result.stderr || '') + (result.error ? result.error.message : '');
  fs.writeFileSync(path.join(output, label.replace(/[^a-z0-9.-]/gi, '_') + '.log'), log);
  const ok = !result.error && result.status === 0;
  console.log(`${ok ? 'PASS' : 'FAIL'} ${label}`);
  if (!ok) { failures++; console.error(log); }
}
try {
  const tracked = spawnSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8' });
  if (tracked.status !== 0) throw Error('Cannot enumerate tracked source');
  const added = [];
  function walk(dir) { for (const item of fs.readdirSync(dir, { withFileTypes: true })) { const name = path.join(dir, item.name); if (item.isDirectory()) walk(name); else if (/\.(cjs|js|mjs)$/.test(name)) added.push(path.relative(root, name)); } }
  walk(__dirname);
  for (const file of new Set([...tracked.stdout.split('\0').filter(x => /\.(cjs|js|mjs)$/.test(x) && !x.startsWith('.review/')), ...added])) run('syntax-' + file, process.execPath, ['--check', file]);
  // Historical review evidence preserves its original bytes and whitespace.
  const patchPaths = ['--', '.', ':(exclude).review/**'];
  run('working-patch', 'git', ['diff', '--check', ...patchPaths]);
  if (process.env.PLANWHARF_DIFF_BASE) run('pr-patch', 'git', ['diff', '--check', process.env.PLANWHARF_DIFF_BASE, 'HEAD', ...patchPaths]);
  const tests = tracked.stdout.split('\0').filter(file => /\.test\.(cjs|js|mjs)$/.test(file) && !file.startsWith('.review/'));
  for (const file of tests) run(file, process.execPath, [file]);
  run('embedded-worker-ui', process.execPath, ['-e', "process.env.PLANWHARF_TEST_EMBEDDED='1';require('./engine/worker-ui.test.cjs')"]);
} finally { fs.rmSync(temporary, { recursive: true, force: true }); }
process.exitCode = failures ? 1 : 0;
