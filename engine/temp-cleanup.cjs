'use strict';
// Test support: remove a temp folder that a just-finished detached process may still hold on Windows.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const BUSY = new Set(['EBUSY', 'EPERM', 'ENOTEMPTY', 'EACCES', 'EMFILE', 'ENFILE']);
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

// fs.rmSync does not retry when the first rmdir of the folder itself answers EBUSY (a process still has it as cwd),
// so its maxRetries option alone cannot outlast a worker that is still closing. Retry the whole removal instead.
async function removeTempDir(target, options = {}) {
  const resolved = path.resolve(String(target || ''));
  const prefix = path.resolve(os.tmpdir()) + path.sep;
  // Windows folder locks show up with either drive-letter case. The separator still rejects a sibling such as Temp-not.
  const left = process.platform === 'win32' ? resolved.toLowerCase() : resolved;
  const right = process.platform === 'win32' ? prefix.toLowerCase() : prefix;
  if (!left.startsWith(right)) throw new Error('removeTempDir only removes folders inside the temp directory');
  const waitMs = options.waitMs ?? 15000, stepMs = options.stepMs ?? 100;
  const until = Date.now() + waitMs;
  for (;;) {
    try { fs.rmSync(resolved, { recursive: true, force: true }); return true; }
    catch (error) {
      // A path under a file does not exist: Linux and macOS answer ENOTDIR where Windows answers ENOENT (which force ignores).
      if (error.code === 'ENOTDIR') return true;
      if (!BUSY.has(error.code) || Date.now() >= until) throw error;
      await pause(stepMs);
    }
  }
}

// Waits until the process has exited, or the time is up. Answers whether it is gone.
async function waitForExit(isAlive, pid, waitMs = 5000, stepMs = 25) {
  const until = Date.now() + waitMs;
  while (isAlive(pid) && Date.now() < until) await pause(stepMs);
  return !isAlive(pid);
}

module.exports = { removeTempDir, waitForExit };
