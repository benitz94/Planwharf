'use strict';
// GitHub refuses a push whose commits carry a private email address (GH007), when the owner keeps the
// address private. The Push button must not stop there: the commits not yet on GitHub get the owner's
// GitHub "noreply" address as author and committer, the repository keeps that address for later
// commits, and the push is tried again. Commits already on the remote are never rewritten.

const privateEmailRefused = text => /\bGH007\b/.test(String(text || ''));

// The noreply address GitHub gives every account: <id>+<login>@users.noreply.github.com, read with the
// GitHub CLI the owner is signed in to. Null when it cannot be read.
async function githubNoreply(gh) {
  const read = await gh(['api', 'user', '--jq', '"\\(.id)+\\(.login)@users.noreply.github.com"']);
  const email = String(read?.stdout || '').trim();
  return read?.status === 0 && /^\d+\+[A-Za-z0-9-]+@users\.noreply\.github\.com$/.test(email) ? email : null;
}

// run(args) runs git in the project folder and resolves { status, stdout, stderr }.
async function useNoreply(run, email) {
  const set = await run(['config', 'user.email', email]);
  if (set.status !== 0) return { ok: false, reason: 'Could not set the GitHub noreply email for this project.' };
  const listed = await run(['rev-list', '--reverse', 'HEAD', '--not', '--remotes']);
  const commits = String(listed.stdout || '').split(/\s+/).filter(Boolean);
  if (listed.status !== 0) return { ok: false, reason: 'Could not list the commits not yet on GitHub.' };
  if (!commits.length) return { ok: true, rewritten: 0 };
  const parent = await run(['rev-parse', '--verify', '-q', commits[0] + '^']);
  const base = parent.status === 0 ? [String(parent.stdout).trim()] : ['--root'];
  const rebased = await run(['-c', 'core.editor=true', 'rebase', '-r', '--exec', 'git commit --amend --no-edit --reset-author --allow-empty', ...base]);
  if (rebased.status !== 0) {
    await run(['rebase', '--abort']);
    return { ok: false, reason: 'Could not change the email of the commits not yet on GitHub; they are as they were.' };
  }
  return { ok: true, rewritten: commits.length };
}

const HOW_TO = 'Set your GitHub noreply email (GitHub → Settings → Emails) with: git config --global user.email "<id>+<login>@users.noreply.github.com", then press Push again.';

module.exports = { privateEmailRefused, githubNoreply, useNoreply, HOW_TO };
