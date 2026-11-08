'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { whenFinished } = require('./runner.cjs');

test('a finished CLI is recorded even when a command it started still holds its output open', async () => {
  // The CLI leaves a 30-second command running in the background that inherits its output
  // (like the hung test run Grok left on 2026-11-08), then exits at once.
  const child = process.platform === 'win32'
    ? spawn('cmd.exe', ['/d', '/c', 'start /b ping -n 30 127.0.0.1'], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true, windowsVerbatimArguments: true })
    : spawn('sh', ['-c', 'sleep 30 &'], { stdio: ['ignore', 'pipe', 'pipe'] });
  child.stdout.resume();
  const started = Date.now();
  const code = await new Promise((resolve, reject) => {
    const guard = setTimeout(() => reject(new Error('still waiting for the leftover command')), 20000);
    whenFinished(child, value => { clearTimeout(guard); resolve(value); });
  });
  assert.equal(code, 0);
  assert.ok(Date.now() - started < 15000, 'finished after the grace period, not when the leftover command ends');
});
