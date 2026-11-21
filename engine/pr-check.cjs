'use strict';
const { spawn } = require('child_process');
const { gitEnvironment } = require('./project-workspace.cjs');

function run(folder, cmd, args) {
  return new Promise(resolve => {
    // A worker blocks every push. Repairing a pull request is an intentional push, so Git and gh
    // start from a clean Git environment, the same way the commit button does.
    const env = cmd === 'git' || cmd === 'gh' ? gitEnvironment() : process.env;
    const child = spawn(cmd, args, { cwd: folder, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env });
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => resolve({ status: 1, stdout, stderr: error.message }));
    child.on('close', code => resolve({ status: code, stdout, stderr }));
  });
}

// The same reds the panel shows: a failed check run, or an older commit status such as ci/circleci.
const RED = ['FAILURE', 'TIMED_OUT', 'CANCELLED', 'ACTION_REQUIRED', 'STARTUP_FAILURE', 'ERROR'];
function failingNames(rollup) {
  return (Array.isArray(rollup) ? rollup : []).filter(check => RED.includes(String(check && (check.conclusion || check.state) || '').toUpperCase()))
    .map(check => String(check && (check.name || check.context) || '').trim()).filter(Boolean);
}

async function listPrs(folder) {
  const listed = await run(folder, 'gh', ['pr', 'list', '--state', 'open', '--json', 'number,title,mergeable,headRefName,statusCheckRollup,url']);
  if (listed.status !== 0) return { ok: false, reason: (listed.stderr || 'Could not read pull requests.').trim().split(/\r?\n/)[0] };
  let prs;
  try { prs = JSON.parse(listed.stdout || '[]'); } catch { return { ok: false, reason: 'Pull request list was not readable.' }; }
  return { ok: true, prs: prs.map(pr => ({
    number: pr.number,
    title: pr.title,
    branch: pr.headRefName,
    url: pr.url,
    conflicts: pr.mergeable === 'CONFLICTING',
    failing: failingNames(pr.statusCheckRollup)
  })) };
}

// Merge the default branch into a conflicting pull request and push when it merges cleanly.
async function resolveConflicts(folder, number, runCommand = run) {
  const view = await runCommand(folder, 'gh', ['pr', 'view', String(number), '--json', 'headRefName,baseRefName,mergeable']);
  if (view.status !== 0) return { ok: false, reason: 'Pull request not found.' };
  let pr;
  try { pr = JSON.parse(view.stdout); } catch { return { ok: false, reason: 'Pull request was not readable.' }; }
  if (!pr || typeof pr !== 'object') return { ok: false, reason: 'Pull request was not readable.' };
  if (pr.mergeable !== 'CONFLICTING') return { ok: true, changed: false, reason: 'No conflicts.' };
  const checkout = await runCommand(folder, 'gh', ['pr', 'checkout', String(number)]);
  if (checkout.status !== 0) return { ok: false, reason: 'Could not check out the pull request.' };
  await runCommand(folder, 'git', ['fetch', 'origin', pr.baseRefName]);
  const merged = await runCommand(folder, 'git', ['merge', 'origin/' + pr.baseRefName, '--no-edit']);
  if (merged.status !== 0) {
    await runCommand(folder, 'git', ['merge', '--abort']);
    const files = (await runCommand(folder, 'git', ['diff', '--name-only', '--diff-filter=U'])).stdout.split(/\r?\n/).filter(Boolean);
    return { ok: false, reason: 'Conflicts need a decision.', files };
  }
  const pushed = await runCommand(folder, 'git', ['push']);
  return pushed.status === 0
    ? { ok: true, changed: true, reason: 'Merged the base branch and pushed.' }
    : { ok: false, reason: 'Merged, but the push failed. The commit is saved locally.' };
}

// Fixing a pull request in the background, the way a reviewer does: in a copy of the folder of its own (a
// git worktree on the pull request's branch), never in the owner's folder, and pushed to the same branch,
// so the same pull request updates. The engine merges the base branch; a model fixes what is left.
async function prepareFix(folder, number, worktree, runCommand = run) {
  const view = await runCommand(folder, 'gh', ['pr', 'view', String(number), '--json', 'headRefName,baseRefName,mergeable,statusCheckRollup,state']);
  if (view.status !== 0) return { ok: false, reason: 'Pull request not found.' };
  let pr;
  try { pr = JSON.parse(view.stdout); } catch { return { ok: false, reason: 'Pull request was not readable.' }; }
  if (!pr || typeof pr !== 'object') return { ok: false, reason: 'Pull request was not readable.' };
  if (pr.state && String(pr.state).toUpperCase() !== 'OPEN') return { ok: false, reason: 'The pull request is not open.' };
  const branch = pr.headRefName, base = pr.baseRefName || 'main';
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(branch || '') || !/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(base)) return { ok: false, reason: 'The pull request branch has an unusual name.' };
  const failing = failingNames(pr.statusCheckRollup);
  return prepareWorktree(folder, { branch, base, failing }, worktree, runCommand);
}

async function prepareWorktree(folder, { branch, base, failing = [] }, worktree, runCommand = run) {
  const fetched = await runCommand(folder, 'git', ['fetch', 'origin', `+refs/heads/${branch}:refs/remotes/origin/${branch}`, `+refs/heads/${base}:refs/remotes/origin/${base}`]);
  if (fetched.status !== 0) return { ok: false, reason: 'Could not fetch the pull request branch.' };
  require('node:fs').mkdirSync(require('node:path').dirname(worktree), { recursive: true });
  const added = await runCommand(folder, 'git', ['worktree', 'add', '--detach', worktree, 'origin/' + branch]);
  if (added.status !== 0) return { ok: false, reason: 'Could not make a copy of the folder for the fix.' };
  const before = (await runCommand(worktree, 'git', ['rev-parse', 'HEAD'])).stdout.trim();
  const merged = await runCommand(worktree, 'git', ['merge', 'origin/' + base, '--no-edit']);
  const conflicts = merged.status === 0 ? [] : (await runCommand(worktree, 'git', ['diff', '--name-only', '--diff-filter=U'])).stdout.split(/\r?\n/).filter(Boolean);
  if (merged.status !== 0 && !conflicts.length) { await removeWorktree(folder, worktree); return { ok: false, reason: 'The base branch could not be merged.' }; }
  return { ok: true, branch, base, before, conflicts, failing };
}

// After the model: nothing left unmerged, no conflict markers, everything committed; then the push.
async function finishFix(folder, worktree, fix, number) {
  const unmerged = (await run(worktree, 'git', ['diff', '--name-only', '--diff-filter=U'])).stdout.split(/\r?\n/).filter(Boolean);
  if (unmerged.length) return { ok: false, reason: 'Conflicts are still open in: ' + unmerged.join(', ') + '.' };
  const markers = await run(worktree, 'git', ['grep', '-l', '-E', '^(<<<<<<< |>>>>>>> )', '--', ...(fix.conflicts.length ? fix.conflicts : ['.'])]);
  if (markers.status === 0 && markers.stdout.trim()) return { ok: false, reason: 'Conflict markers are left in: ' + markers.stdout.trim().split(/\r?\n/).join(', ') + '.' };
  const fs = require('node:fs'), path = require('node:path');
  const gitDir = (await run(worktree, 'git', ['rev-parse', '--git-dir'])).stdout.trim();
  if (gitDir && fs.existsSync(path.resolve(worktree, gitDir, 'MERGE_HEAD'))) {
    await run(worktree, 'git', ['add', '-A']);
    const committed = await run(worktree, 'git', ['commit', '--no-edit']);
    if (committed.status !== 0) return { ok: false, reason: 'The merge could not be committed.' };
  }
  if ((await run(worktree, 'git', ['status', '--porcelain'])).stdout.trim()) {
    await run(worktree, 'git', ['add', '-A']);
    const committed = await run(worktree, 'git', ['commit', '-m', `Fix pull request #${number}`]);
    if (committed.status !== 0) return { ok: false, reason: 'The fix could not be committed.' };
  }
  const head = (await run(worktree, 'git', ['rev-parse', 'HEAD'])).stdout.trim();
  if (head === fix.before) return { ok: true, changed: false, reason: 'Nothing needed changing.' };
  // Never a force push: if someone pushed to the branch meanwhile, the push is refused and nothing is lost.
  // The commit stays in this copy. The caller must not delete it.
  const pushed = await run(worktree, 'git', ['push', 'origin', 'HEAD:refs/heads/' + fix.branch]);
  if (pushed.status !== 0) {
    const why = (pushed.stderr || '').trim().split(/\r?\n/).filter(Boolean).slice(-1)[0] || 'the remote refused it';
    return { ok: false, kept: true, reason: 'The push was refused: ' + why + ' The commit is saved locally.' };
  }
  return { ok: true, changed: true, reason: 'Pushed to ' + fix.branch + '.' };
}

async function removeWorktree(folder, worktree) {
  await run(folder, 'git', ['worktree', 'remove', '--force', worktree]);
  await run(folder, 'git', ['worktree', 'prune']);
}

module.exports = { listPrs, resolveConflicts, prepareFix, prepareWorktree, finishFix, removeWorktree };
