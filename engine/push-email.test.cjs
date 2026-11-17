'use strict';
// A push refused for a private email (GH007) is not a dead end: the commits not yet on GitHub take the
// owner's noreply address and the push goes through. A local remote plays GitHub with a pre-receive hook.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { spawnSync } = require('node:child_process');
const { privateEmailRefused, githubNoreply, useNoreply } = require('./push-email.cjs');

const NOREPLY = '123+owner@users.noreply.github.com';
const env = { ...Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('GIT_'))), GIT_AUTHOR_NAME: 'Owner', GIT_COMMITTER_NAME: 'Owner' };
const git = (cwd, args) => spawnSync('git', args, { cwd, encoding: 'utf8', env, windowsHide: true });
const run = cwd => async args => { const r = git(cwd, args); return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' }; };

function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-push-email-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const remote = path.join(dir, 'remote.git'), work = path.join(dir, 'work');
  git(dir, ['init', '--bare', '-q', '-b', 'main', remote]);
  // Like GitHub: refuse any pushed commit whose author or committer is not the noreply address.
  const hook = path.join(remote, 'hooks', 'pre-receive');
  fs.writeFileSync(hook, `#!/bin/sh
while read old new ref; do
  for c in $(git rev-list "$new" --not --all); do
    a=$(git log -1 --format=%ae "$c"); m=$(git log -1 --format=%ce "$c")
    if [ "$a" != "${NOREPLY}" ] || [ "$m" != "${NOREPLY}" ]; then echo "remote: error: GH007: Your push would publish a private email address." >&2; exit 1; fi
  done
done
`);
  fs.chmodSync(hook, 0o755);
  git(dir, ['clone', '-q', remote, work]);
  git(work, ['config', 'user.email', NOREPLY]);
  fs.writeFileSync(path.join(work, 'a.txt'), 'a\n');
  git(work, ['add', '-A']); git(work, ['commit', '-q', '-m', 'first']);
  assert.equal(git(work, ['push', '-q', 'origin', 'HEAD:main']).status, 0, 'the first commit has the noreply address');
  git(work, ['switch', '-q', '-c', 'feature']);
  git(work, ['config', 'user.email', 'owner@private.example']);
  for (const name of ['b', 'c']) { fs.writeFileSync(path.join(work, name + '.txt'), name + '\n'); git(work, ['add', '-A']); git(work, ['commit', '-q', '-m', name]); }
  return { remote, work };
}

test('a push refused for a private email goes through once the new commits use the noreply address', async t => {
  const { work } = fixture(t);
  const refused = git(work, ['push', '-u', 'origin', 'HEAD']);
  assert.notEqual(refused.status, 0);
  assert.equal(privateEmailRefused(refused.stderr), true);
  const before = git(work, ['rev-parse', 'origin/main']).stdout.trim();
  const fixed = await useNoreply(run(work), NOREPLY);
  assert.deepEqual(fixed, { ok: true, rewritten: 2 });
  assert.deepEqual(git(work, ['log', '-3', '--format=%ae %ce %s']).stdout.trim().split('\n'), [`${NOREPLY} ${NOREPLY} c`, `${NOREPLY} ${NOREPLY} b`, `${NOREPLY} ${NOREPLY} first`]);
  assert.equal(git(work, ['rev-parse', 'HEAD~2']).stdout.trim(), before, 'the commit already on the remote is not rewritten');
  assert.equal(fs.readFileSync(path.join(work, 'c.txt'), 'utf8'), 'c\n', 'the files are unchanged');
  assert.equal(git(work, ['config', 'user.email']).stdout.trim(), NOREPLY, 'later commits use it too');
  assert.equal(git(work, ['push', '-u', 'origin', 'HEAD']).status, 0);
});

test('the noreply address is read from the GitHub CLI, and nothing else is accepted', async () => {
  assert.equal(await githubNoreply(async () => ({ status: 0, stdout: NOREPLY + '\n' })), NOREPLY);
  assert.equal(await githubNoreply(async () => ({ status: 1, stdout: '' })), null, 'not signed in');
  assert.equal(await githubNoreply(async () => ({ status: 0, stdout: 'owner@private.example' })), null);
  assert.equal(privateEmailRefused('rejected: non-fast-forward'), false);
});

test('a long summary keeps its "Needs your decision" line whole', () => {
  const { clipSummary } = require('./orchestrator.js');
  const long = 'Verified the work. ' + 'Added tests for edge cases. '.repeat(30) + '\nNeeds your decision: keep the snapshot test pinned to 16/11?';
  const clipped = clipSummary(long);
  assert.ok(clipped.length < long.length);
  assert.match(clipped, /\nNeeds your decision: keep the snapshot test pinned to 16\/11\?$/);
  assert.equal(clipSummary('Short.'), 'Short.');
});
