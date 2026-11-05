'use strict';
// Planwharf runs the project's own tests before the lead judges a finished MegaTask, so the
// verdict rests on a fresh run instead of a worker's claim, and the lead spends no time or
// tokens running them. Only a recognised test entry point is run; nothing is guessed.
const fs = require('node:fs'), path = require('node:path'), cp = require('node:child_process');

const TIMEOUT_MS = 10 * 60 * 1000;
const TEST_FILE = /(^|[\\/])(test|tests|__tests__)[\\/].+\.(c?js|mjs)$|\.(test|spec)\.(c?js|mjs)$/i;

function testFiles(folder) {
  const found = [];
  const walk = (dir, depth) => {
    if (depth > 4 || found.length > 200) return;
    let entries = []; try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
    for (const entry of entries) {
      if (entry.name.startsWith('.') || entry.name === 'node_modules') continue;
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(full, depth + 1);
      else if (TEST_FILE.test(path.relative(folder, full))) found.push(full);
    }
  };
  walk(folder, 0);
  return found;
}

// Returns { command, args, label } or null when the project has no recognised tests.
function testCommand(folder) {
  if (!folder) return null;
  try {
    const pkg = JSON.parse(fs.readFileSync(path.join(folder, 'package.json'), 'utf8'));
    const script = pkg?.scripts?.test;
    if (typeof script === 'string' && script.trim() && !/no test specified/i.test(script)) {
      return process.platform === 'win32'
        ? { command: process.env.ComSpec || 'cmd.exe', args: ['/d', '/s', '/c', 'npm test'], label: 'npm test' }
        : { command: 'npm', args: ['test'], label: 'npm test' };
    }
  } catch {}
  if (testFiles(folder).length) return { command: process.execPath, args: ['--test'], label: 'node --test' };
  return null;
}

function tail(text, chars = 3000) { const value = String(text || ''); return value.length > chars ? '...' + value.slice(-chars) : value; }

// Resolves to null (no tests found) or { label, exitCode, timedOut, seconds, output }.
function runChecks(folder, options = {}) {
  const spec = testCommand(folder);
  if (!spec) return Promise.resolve(null);
  const started = Date.now();
  return new Promise(resolve => {
    let output = '', timedOut = false, done = false;
    // A parent test runner's context would turn the child run into its subtest, which exits 0.
    const env = { ...process.env, ...(options.env || {}), CI: '1' }; delete env.NODE_TEST_CONTEXT;
    const child = cp.spawn(spec.command, spec.args, { cwd: folder, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    const add = chunk => { output = tail(output + chunk, 20000); };
    child.stdout.on('data', add); child.stderr.on('data', add);
    const timer = setTimeout(() => {
      timedOut = true;
      if (process.platform === 'win32') cp.spawnSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore', windowsHide: true });
      else child.kill('SIGKILL');
    }, options.timeoutMs ?? TIMEOUT_MS);
    const finish = exitCode => { if (done) return; done = true; clearTimeout(timer); resolve({ label: spec.label, exitCode, timedOut, seconds: Math.round((Date.now() - started) / 100) / 10, output: tail(output) }); };
    child.on('error', error => { add('\n' + error.message); finish(null); });
    child.on('close', code => finish(code));
  });
}

module.exports = { testCommand, runChecks, testFiles };
