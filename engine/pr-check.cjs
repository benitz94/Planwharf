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

module.exports = { listPrs, resolveConflicts };
