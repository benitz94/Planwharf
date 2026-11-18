'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const supported = new Set(['claude', 'codex']);
const allowedKeys = ['adapter', 'folder', 'id', 'providerId'];
const idPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function commandFor(adapter, folder) {
  if (typeof folder !== 'string' || /[\r\n\0]/.test(folder)) throw new Error('Invalid account folder.');
  const value = process.platform === 'win32' ? folder.replace(/'/g, "''") : folder.replace(/'/g, "'\\''");
  return process.platform === 'win32'
    ? `$env:${adapter === 'claude' ? 'CLAUDE_CONFIG_DIR' : 'CODEX_HOME'}='${value}'; ${adapter === 'claude' ? 'claude' : 'codex'} login`
    : `${adapter === 'claude' ? 'CLAUDE_CONFIG_DIR' : 'CODEX_HOME'}='${value}' ${adapter === 'claude' ? 'claude' : 'codex'} login`;
}
function insideRoot(root, folder) {
  const rel = path.relative(path.resolve(root), path.resolve(folder));
  if (rel === '' || rel.startsWith('..') || path.isAbsolute(rel)) return false;
  try {
    const realRel = path.relative(path.resolve(root), fs.realpathSync(path.resolve(folder)));
    if (realRel === '' || realRel.startsWith('..') || path.isAbsolute(realRel)) return false;
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  return true;
}
function readAccounts(file, root) {
  let saved;
  try { saved = JSON.parse(fs.readFileSync(file, 'utf8')); }
  catch (error) { if (error.code === 'ENOENT') return []; throw error; }
  if (!Array.isArray(saved)) throw new Error('Invalid account folders file.');
  const seen = new Set();
  for (const row of saved) {
    const keys = row && typeof row === 'object' && !Array.isArray(row) ? Object.keys(row) : null;
    if (!keys || keys.some(key => !allowedKeys.includes(key)) || allowedKeys.some(key => !Object.hasOwn(row, key))) throw new Error('Invalid account folders file.');
    if (!supported.has(row.adapter) || !idPattern.test(row.id) || seen.has(row.id) || row.providerId !== row.adapter) throw new Error('Invalid account folders file.');
    if (typeof row.folder !== 'string' || path.resolve(row.folder) !== path.join(root, row.adapter, row.id) || !insideRoot(root, row.folder)) throw new Error('Invalid account folders file.');
    seen.add(row.id);
  }
  return saved.map(row => ({ id: row.id, adapter: row.adapter, providerId: row.providerId, folder: row.folder }));
}
function pidAlive(pid) {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try { process.kill(pid, 0); return true; } catch (error) { return error.code === 'EPERM'; }
}
function reclaimLock(lock) {
  let text = '';
  try { text = fs.readFileSync(lock, 'utf8'); }
  catch (error) { if (error.code === 'ENOENT') return; throw error; }
  const pid = Number(String(text).split(':')[0]);
  const identified = Number.isInteger(pid) && pid > 0 && String(text).includes(':');
  // A live process still owns the lock, even if the file is old. A dead owner, or an
  // empty lock left behind for more than 10 seconds, can be cleared.
  if (identified) { if (!pidAlive(pid)) fs.rmSync(lock, { force: true }); return; }
  if (Date.now() - fs.statSync(lock).mtimeMs > 10000) fs.rmSync(lock, { force: true });
}
function withLock(file, fn) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const lock = file + '.lock';
  const owner = process.pid + ':' + randomUUID();
  const started = Date.now();
  for (;;) {
    try {
      const fd = fs.openSync(lock, 'wx');
      try { fs.writeSync(fd, owner); return fn(); }
      finally {
        fs.closeSync(fd);
        try { if (fs.readFileSync(lock, 'utf8') === owner) fs.rmSync(lock, { force: true }); } catch { /* the next add recovers a stale lock */ }
      }
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      try { reclaimLock(lock); } catch (statError) { if (statError.code !== 'ENOENT') throw statError; }
      if (Date.now() - started > 5000) throw new Error('Account folders file is busy.');
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 15);
    }
  }
}
function addAccount(file, root, adapter, providerId, existing) {
  if (!supported.has(adapter) || providerId !== adapter) throw new Error('This AI tool does not support another account.');
  if (existing !== undefined && !Array.isArray(existing)) throw new Error('Invalid account folders file.');
  if (typeof root !== 'string' || /[\r\n\0]/.test(root)) throw new Error('Invalid account folder.');
  return withLock(file, () => {
    const saved = readAccounts(file, root);
    const id = randomUUID(), folder = path.join(root, adapter, id);
    fs.mkdirSync(folder, { recursive: true, mode: 0o700 });
    const row = { id, adapter, providerId, folder };
    const temporary = file + '.' + id + '.tmp';
    try {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(temporary, JSON.stringify([...saved, row]) + '\n', { encoding: 'utf8', flag: 'wx', mode: 0o600 });
      fs.renameSync(temporary, file);
      return { ...row, signInCommand: commandFor(adapter, folder) };
    } catch (error) {
      try { fs.rmSync(temporary, { force: true }); } catch { /* nothing was published */ }
      try { fs.rmSync(folder, { recursive: true, force: true }); } catch { /* the account was not recorded */ }
      throw error;
    }
  });
}
module.exports = { supported, commandFor, readAccounts, addAccount };
