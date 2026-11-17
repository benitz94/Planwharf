'use strict';
// Independent reproduction of origin/main's push-email fixture without core.autocrlf=false.
const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
const { spawnSync } = require('node:child_process');
const { useNoreply } = require('../../../engine/push-email.cjs');

const NOREPLY = '123+owner@users.noreply.github.com';
const env = { ...Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('GIT_'))), GIT_AUTHOR_NAME: 'Owner', GIT_COMMITTER_NAME: 'Owner' };
const git = (cwd, args) => spawnSync('git', args, { cwd, encoding: 'utf8', env, windowsHide: true });
const run = cwd => async args => { const r = git(cwd, args); return { status: r.status, stdout: r.stdout || '', stderr: r.stderr || '' }; };

(async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-push-email-repro-'));
  try {
    const remote = path.join(dir, 'remote.git'), work = path.join(dir, 'work');
    git(dir, ['init', '--bare', '-q', '-b', 'main', remote]);
    git(dir, ['clone', '-q', remote, work]);
    git(work, ['config', 'user.email', NOREPLY]);
    fs.writeFileSync(path.join(work, 'a.txt'), 'a\n');
    git(work, ['add', '-A']); git(work, ['commit', '-q', '-m', 'first']);
    assert.equal(git(work, ['push', '-q', 'origin', 'HEAD:main']).status, 0);
    git(work, ['switch', '-q', '-c', 'feature']);
    git(work, ['config', 'user.email', 'owner@private.example']);
    for (const name of ['b', 'c']) {
      fs.writeFileSync(path.join(work, name + '.txt'), name + '\n');
      git(work, ['add', '-A']); git(work, ['commit', '-q', '-m', name]);
    }
    const autocrlf = git(work, ['config', '--get', 'core.autocrlf']).stdout.trim();
    const before = fs.readFileSync(path.join(work, 'c.txt'));
    const fixed = await useNoreply(run(work), NOREPLY);
    const after = fs.readFileSync(path.join(work, 'c.txt'));
    const report = {
      ok: fixed.ok,
      rewritten: fixed.rewritten,
      workAutocrlf: autocrlf || '(unset)',
      beforeHex: before.toString('hex'),
      afterHex: after.toString('hex'),
      bytesEqual: before.equals(after),
      afterUtf8: JSON.stringify(after.toString('utf8'))
    };
    fs.writeFileSync(path.join(__dirname, 'repro-origin-main-push-email.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify(report, null, 2));
    process.exitCode = report.bytesEqual ? 0 : 1;
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
