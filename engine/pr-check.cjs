'use strict';
const { spawn } = require('child_process');

function run(folder, cmd, args) {
  return new Promise(resolve => {
    const child = spawn(cmd, args, { cwd: folder, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => resolve({ status: 1, stdout, stderr: error.message }));
    child.on('close', code => resolve({ status: code, stdout, stderr }));
  });
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
    failing: (pr.statusCheckRollup || []).filter(check => check.conclusion === 'FAILURE').map(check => check.name)
  })) };
}

// Merge the default branch into a conflicting pull request and push when it merges cleanly.
async function resolveConflicts(folder, number) {
  const view = await run(folder, 'gh', ['pr', 'view', String(number), '--json', 'headRefName,baseRefName,mergeable']);
  if (view.status !== 0) return { ok: false, reason: 'Pull request not found.' };
  const pr = JSON.parse(view.stdout);
  if (pr.mergeable !== 'CONFLICTING') return { ok: true, changed: false, reason: 'No conflicts.' };
  const checkout = await run(folder, 'gh', ['pr', 'checkout', String(number)]);
  if (checkout.status !== 0) return { ok: false, reason: 'Could not check out the pull request.' };
  await run(folder, 'git', ['fetch', 'origin', pr.baseRefName]);
  const merged = await run(folder, 'git', ['merge', 'origin/' + pr.baseRefName, '--no-edit']);
  if (merged.status !== 0) {
    await run(folder, 'git', ['merge', '--abort']);
    const files = (await run(folder, 'git', ['diff', '--name-only', '--diff-filter=U'])).stdout.split(/\r?\n/).filter(Boolean);
    return { ok: false, reason: 'Conflicts need a decision.', files };
  }
  const pushed = await run(folder, 'git', ['push']);
  return pushed.status === 0
    ? { ok: true, changed: true, reason: 'Merged the base branch and pushed.' }
    : { ok: false, reason: 'Merged, but the push failed. The commit is saved locally.' };
}

// Fixing a pull request in the background, the way a reviewer does: in a copy of the folder of its own (a
// git worktree on the pull request's branch), never in the owner's folder, and pushed to the same branch,
// so the same pull request updates. The engine merges the base branch; a model fixes what is left.
async function prepareFix(folder, number, worktree) {
  const view = await run(folder, 'gh', ['pr', 'view', String(number), '--json', 'headRefName,baseRefName,mergeable,statusCheckRollup,state']);
  if (view.status !== 0) return { ok: false, reason: 'Pull request not found.' };
  let pr;
  try { pr = JSON.parse(view.stdout); } catch { return { ok: false, reason: 'Pull request was not readable.' }; }
  if (pr.state && pr.state !== 'OPEN') return { ok: false, reason: 'The pull request is not open.' };
  const branch = pr.headRefName, base = pr.baseRefName || 'main';
  if (!/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(branch || '') || !/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(base)) return { ok: false, reason: 'The pull request branch has an unusual name.' };
  const failing = (pr.statusCheckRollup || []).filter(check => check.conclusion === 'FAILURE').map(check => check.name);
  return prepareWorktree(folder, { branch, base, failing }, worktree);
}

async function prepareWorktree(folder, { branch, base, failing = [] }, worktree) {
  const fetched = await run(folder, 'git', ['fetch', 'origin', `+refs/heads/${branch}:refs/remotes/origin/${branch}`, `+refs/heads/${base}:refs/remotes/origin/${base}`]);
  if (fetched.status !== 0) return { ok: false, reason: 'Could not fetch the pull request branch.' };
  require('node:fs').mkdirSync(require('node:path').dirname(worktree), { recursive: true });
  const added = await run(folder, 'git', ['worktree', 'add', '--detach', worktree, 'origin/' + branch]);
  if (added.status !== 0) return { ok: false, reason: 'Could not make a copy of the folder for the fix.' };
  const before = (await run(worktree, 'git', ['rev-parse', 'HEAD'])).stdout.trim();
  const merged = await run(worktree, 'git', ['merge', 'origin/' + base, '--no-edit']);
  const conflicts = merged.status === 0 ? [] : (await run(worktree, 'git', ['diff', '--name-only', '--diff-filter=U'])).stdout.split(/\r?\n/).filter(Boolean);
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
  const pushed = await run(worktree, 'git', ['push', 'origin', 'HEAD:refs/heads/' + fix.branch]);
  if (pushed.status !== 0) return { ok: false, reason: 'The push was refused: ' + (pushed.stderr || '').trim().split(/\r?\n/).slice(-1)[0] };
  return { ok: true, changed: true, reason: 'Pushed to ' + fix.branch + '.' };
}

async function removeWorktree(folder, worktree) {
  await run(folder, 'git', ['worktree', 'remove', '--force', worktree]);
  await run(folder, 'git', ['worktree', 'prune']);
}

module.exports = { listPrs, resolveConflicts, prepareFix, prepareWorktree, finishFix, removeWorktree };
