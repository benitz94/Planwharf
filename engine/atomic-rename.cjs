'use strict';
const fs = require('node:fs');

// Windows refuses a rename while another process (an antivirus scan, an indexer, a reader
// in this app) briefly holds the target open. Retry for about a second before failing.
const RETRY_CODES = new Set(['EPERM', 'EBUSY', 'EACCES']);
const pause = new Int32Array(new SharedArrayBuffer(4));

function renameWithRetry(from, to, options = {}) {
  const attempts = options.attempts ?? 12;
  const rename = options.rename || fs.renameSync;
  for (let attempt = 1; ; attempt += 1) {
    try { rename(from, to); return attempt; }
    catch (error) {
      if (!RETRY_CODES.has(error.code) || attempt >= attempts) {
        try { fs.unlinkSync(from); } catch {}
        throw error;
      }
      Atomics.wait(pause, 0, 0, Math.min(200, 10 * 2 ** (attempt - 1)));
    }
  }
}

module.exports = { renameWithRetry, RETRY_CODES };
