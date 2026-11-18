'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

function linkedWorkspace(folder) {
  if (typeof folder !== 'string' || !folder.trim() || !path.isAbsolute(folder)) return null;
  try {
    const canonical = fs.realpathSync(folder);
    return fs.statSync(canonical).isDirectory() ? canonical : null;
  } catch { return null; }
}
function within(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith('..' + path.sep));
}
function requireWorkerWorkspace(info) {
  const research = (info.job?.kind || info.kind) === 'research';
  const root = linkedWorkspace(research ? info.folder : info.linkedFolder);
  if (!root) throw new Error('A real linked project folder is required before worker launch.');
  const folder = linkedWorkspace(info.folder);
  if (!folder || folder !== root && (research || !registeredWorktree(root, folder, info))) throw new Error('Worker launch folder must stay inside the linked project folder.');
  for (const candidate of [info.cwd, info.job?.folder]) {
    if (candidate !== undefined && linkedWorkspace(candidate) !== folder) throw new Error('Worker cwd and job folder must match the linked project folder.');
  }
  return { folder, linkedFolder: research ? '' : root };
}
function registeredWorktree(root, folder, info) {
  try {
    if (!safeGitWorkspace(root)) return false;
    const result = info.resultPath || info.result;
    if (!result || !path.isAbsolute(result) || ![info.projectId, info.megaTaskId, info.subTaskId].every(id => typeof id === 'string' && /^[A-Za-z0-9_-]{1,100}$/.test(id))) return false;
    const dataRoot = path.dirname(path.dirname(result));
    const expected = path.join(dataRoot, 'worktrees', info.projectId, info.megaTaskId, info.subTaskId);
    if (folder !== linkedWorkspace(expected)) return false;
    const listed = spawnSync('git', ['worktree', 'list', '--porcelain', '-z'], { cwd: root, encoding: 'utf8', env: gitEnvironment(), windowsHide: true, timeout: 10000, maxBuffer: 1024 * 1024 });
    return listed.status === 0 && listed.stdout.split('\0').some(record => record.startsWith('worktree ') && linkedWorkspace(record.slice(9).split('\n')[0]) === folder);
  } catch { return false; }
}
// Inspect only an ordinary repository whose metadata cannot redirect to another project.
// External Git worktrees and shared object stores require a separate explicit boundary.
function safeGitWorkspace(folder) {
  const root = linkedWorkspace(folder);
  if (!root) return null;
  const metadata = path.join(root, '.git');
  try {
    if (!fs.lstatSync(metadata).isDirectory()) return null;
    const pending = [metadata];
    let entries = 0;
    while (pending.length) {
      const current = pending.pop();
      for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
        // Excessive metadata is unavailable rather than an unbounded inspection scan.
        if (++entries > 100000) return null;
        const file = path.join(current, entry.name);
        if (entry.isSymbolicLink() || !within(metadata, fs.realpathSync(file))) return null;
        if (entry.isDirectory()) pending.push(file);
      }
    }
    for (const name of ['commondir', 'objects/info/alternates', 'objects/info/http-alternates']) {
      if (fs.existsSync(path.join(metadata, name))) return null;
    }
    const config = fs.readFileSync(path.join(metadata, 'config'), 'utf8');
    if (/^\s*\[\s*include(?:if)?(?:\s|\])/im.test(config) || /^\s*(?:worktree|fsmonitor)\s*=/im.test(config)) return null;
    return root;
  } catch { return null; }
}
function gitEnvironment(inherited = process.env) {
  const env = Object.fromEntries(Object.entries(inherited).filter(([key]) => !key.toUpperCase().startsWith('GIT_')));
  return { ...env, GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: process.platform === 'win32' ? 'NUL' : '/dev/null', GIT_TERMINAL_PROMPT: '0' };
}
module.exports = { linkedWorkspace, within, requireWorkerWorkspace, safeGitWorkspace, gitEnvironment };
