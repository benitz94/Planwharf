'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

const supported = new Set(['claude', 'codex']);
function commandFor(adapter, folder) {
  const value = process.platform === 'win32' ? folder.replace(/'/g, "''") : folder.replace(/'/g, "'\\''");
  return process.platform === 'win32'
    ? `$env:${adapter === 'claude' ? 'CLAUDE_CONFIG_DIR' : 'CODEX_HOME'}='${value}'; ${adapter === 'claude' ? 'claude' : 'codex'} login`
    : `${adapter === 'claude' ? 'CLAUDE_CONFIG_DIR' : 'CODEX_HOME'}='${value}' ${adapter === 'claude' ? 'claude' : 'codex'} login`;
}
function readAccounts(file, root) {
  let saved;
  try { saved = JSON.parse(fs.readFileSync(file, 'utf8')); } catch (error) { if (error.code === 'ENOENT') return []; throw error; }
  if (!Array.isArray(saved) || saved.some(row => !row || !supported.has(row.adapter) || !/^[a-f0-9-]{36}$/.test(row.id)
    || row.providerId !== row.adapter || typeof row.folder !== 'string' || path.resolve(row.folder) !== path.join(root, row.adapter, row.id))) throw new Error('Invalid account folders file.');
  return saved;
}
function addAccount(file, root, adapter, providerId, existing) {
  if (!supported.has(adapter) || providerId !== adapter) throw new Error('This AI tool does not support another account.');
  const id = randomUUID(), folder = path.join(root, adapter, id);
  fs.mkdirSync(folder, { recursive: true, mode: 0o700 });
  const row = { id, adapter, providerId, folder };
  const temporary = file + '.' + id + '.tmp';
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(temporary, JSON.stringify([...existing, row]) + '\n', { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    fs.renameSync(temporary, file);
  } catch (error) { try { fs.rmSync(temporary, { force: true }); } catch {} throw error; }
  return { ...row, signInCommand: commandFor(adapter, folder) };
}
module.exports = { supported, commandFor, readAccounts, addAccount };
