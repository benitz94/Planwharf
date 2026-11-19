'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { linkedWorkspace, within, safeGitWorkspace, gitEnvironment } = require('./project-workspace.cjs');

function git(folder, args) {
  const result = spawnSync('git', args, { cwd: folder, encoding: 'utf8', env: gitEnvironment(), windowsHide: true, timeout: 30000, maxBuffer: 8 * 1024 * 1024 });
  if (result.status !== 0) throw new Error((result.stderr || result.error?.message || 'Git command failed').trim());
  return result.stdout.trim();
}

function repository(folder) {
  const root = linkedWorkspace(folder);
  if (!root || !safeGitWorkspace(root)) return false;
  try { return linkedWorkspace(git(root, ['rev-parse', '--show-toplevel'])) === root; }
  catch { return false; }
}

function branchFor(workBranch, index) {
  if (!Number.isSafeInteger(index) || index < 1 || !/^[-A-Za-z0-9_./]+$/.test(workBranch)) throw new Error('Invalid worktree branch');
  const branch = `${workBranch}--part-${index}`;
  gitBranchName(branch);
  return branch;
}

function gitBranchName(branch) {
  if (spawnSync('git', ['check-ref-format', '--branch', branch], { encoding: 'utf8', env: gitEnvironment(), windowsHide: true }).status !== 0) throw new Error('Invalid worktree branch');
}

function worktreePath(dir, projectId, megaId, subId) {
  for (const id of [projectId, megaId, subId]) if (typeof id !== 'string' || !/^[A-Za-z0-9_-]{1,100}$/.test(id)) throw new Error('Invalid worktree identity');
  return path.join(dir, 'worktrees', projectId, megaId, subId);
}

function create({ folder, dir, projectId, megaId, subId, index, workBranch }) {
  if (!repository(folder)) return null;
  const branch = branchFor(workBranch, index);
  const destination = worktreePath(dir, projectId, megaId, subId);
  git(folder, ['worktree', 'prune']);
  if (fs.existsSync(destination)) {
    const listed = git(folder, ['worktree', 'list', '--porcelain', '-z']).split('\0');
    if (!listed.some(record => record.startsWith('worktree ') && linkedWorkspace(record.slice(9).split('\n')[0]) === linkedWorkspace(destination))
      || git(destination, ['branch', '--show-current']) !== branch) throw new Error('Worktree destination is occupied');
    return { folder: destination, branch, subId };
  }
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  const exists = spawnSync('git', ['show-ref', '--verify', '--quiet', `refs/heads/${branch}`], { cwd: folder, env: gitEnvironment(), windowsHide: true }).status === 0;
  git(folder, exists ? ['worktree', 'add', destination, branch] : ['worktree', 'add', '-b', branch, destination, workBranch]);
  return { folder: destination, branch, subId };
}

function integrate({ folder, parts }) {
  try {
    if (git(folder, ['status', '--porcelain'])) return { ok: false, reason: 'Work branch has uncommitted changes; integration is paused.', conflicts: [] };
  } catch (error) {
    return { ok: false, reason: error.message, conflicts: [] };
  }
  for (const part of parts) {
    let dirty = '';
    try { dirty = git(part.folder, ['status', '--porcelain']); }
    catch (error) {
      return { ok: false, branch: part.branch, conflicts: [], reason: fs.existsSync(part.folder) ? error.message : `Part worktree is unavailable: ${part.folder}` };
    }
    if (dirty) return { ok: false, branch: part.branch, reason: 'Part has uncommitted changes; integration is paused.', conflicts: [] };
  }
  const conflicts = [];
  for (const part of parts) {
    try { git(folder, ['merge', '--no-ff', '--no-edit', part.branch]); }
    catch (error) {
      try { conflicts.push(...git(folder, ['diff', '--name-only', '--diff-filter=U']).split(/\r?\n/).filter(Boolean)); }
      finally {
        const merging = spawnSync('git', ['rev-parse', '-q', '--verify', 'MERGE_HEAD'], { cwd: folder, env: gitEnvironment(), windowsHide: true });
        if (merging.status === 0) git(folder, ['merge', '--abort']);
      }
      return { ok: false, branch: part.branch, conflicts, reason: error.message };
    }
  }
  return { ok: true };
}

function cleanup({ folder, dir, projectId, megaId, parts, discardBranches = false }) {
  const root = path.resolve(dir, 'worktrees', projectId, megaId);
  for (const part of parts) {
    const destination = path.resolve(part.folder);
    gitBranchName(part.branch);
    if (!within(root, destination) || destination !== worktreePath(dir, projectId, megaId, part.subId)) throw new Error('Worktree cleanup path escaped its work');
    if (fs.existsSync(destination)) {
      const dirty = git(destination, ['status', '--porcelain']);
      // Completion keeps uncommitted work for the owner. Drop this work removes it.
      if (dirty && !discardBranches) throw new Error(`Worktree has recoverable changes: ${destination}`);
      git(folder, discardBranches ? ['worktree', 'remove', '--force', '--force', destination] : ['worktree', 'remove', destination]);
    } else git(folder, ['worktree', 'prune']);
    git(folder, ['branch', discardBranches ? '-D' : '-d', part.branch]);
  }
}

// A worktree whose work is gone stays on disk after a plain prune. Remove those directories.
function sweepOrphans({ dir, liveMegaIds }) {
  const root = path.join(dir, 'worktrees');
  if (!fs.existsSync(root)) return [];
  const live = new Set(liveMegaIds || []);
  const removed = [];
  for (const project of fs.readdirSync(root)) {
    const projectDir = path.join(root, project);
    if (!fs.statSync(projectDir).isDirectory()) continue;
    for (const mega of fs.readdirSync(projectDir)) {
      if (live.has(mega)) continue;
      fs.rmSync(path.join(projectDir, mega), { recursive: true, force: true });
      removed.push(path.join(project, mega));
    }
    if (!fs.readdirSync(projectDir).length) fs.rmdirSync(projectDir);
  }
  return removed;
}
module.exports = { repository, branchFor, worktreePath, create, integrate, cleanup, git, sweepOrphans };
