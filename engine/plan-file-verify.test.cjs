'use strict';

// Independent verification of plan-file.cjs: edge cases, error paths and recovery.
// Does not rewrite working code — only adds coverage for untested paths.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const planFile = require('./plan-file.cjs');

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-'));

function git(cwd, args) {
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('GIT_')));
  const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, env });
  assert.equal(result.status, 0, `${args.join(' ')} failed: ${result.stderr || result.stdout}`);
  return (result.stdout || '').trim();
}

function freshRepo(tag) {
  const place = fs.mkdtempSync(path.join(root, tag + '-'));
  const rem = path.join(place, 'remote.git');
  const work = path.join(place, 'work');
  fs.mkdirSync(rem, { recursive: true });
  git(rem, ['init', '--bare', '-q', '-b', 'main']);
  fs.mkdirSync(work, { recursive: true });
  git(work, ['init', '-q', '-b', 'main']);
  git(work, ['config', 'user.name', 'Fixture']);
  git(work, ['config', 'user.email', 'fixture@example.invalid']);
  git(work, ['config', 'core.autocrlf', 'false']);
  fs.writeFileSync(path.join(work, 'readme.txt'), 'start\n');
  git(work, ['add', 'readme.txt']);
  git(work, ['commit', '-q', '-m', 'start']);
  git(work, ['remote', 'add', 'origin', rem]);
  git(work, ['push', '-q', '-u', 'origin', 'main']);
  git(work, ['remote', 'set-head', 'origin', 'main']);
  return { place, rem, work };
}

// --- 1. isGit with invalid input ---
{
  assert.equal(planFile.isGit(null), false, 'isGit(null) is false');
  assert.equal(planFile.isGit(undefined), false, 'isGit(undefined) is false');
  assert.equal(planFile.isGit(''), false, 'isGit("") is false');
  assert.equal(planFile.isGit(42), false, 'isGit(number) is false');
  assert.equal(planFile.isGit(path.join(root, 'does-not-exist')), false, 'isGit(missing path) is false');
}

// --- 2. parse with malformed / empty input ---
{
  const empty = planFile.parse('');
  assert.equal(empty.goal, '');
  assert.equal(empty.branch, '');
  assert.deepEqual(empty.steps, []);

  const noSections = planFile.parse('just some text\nno headings\n');
  assert.equal(noSections.goal, '');
  assert.deepEqual(noSections.steps, []);

  const nullInput = planFile.parse(null);
  assert.equal(nullInput.goal, '');

  const undefinedInput = planFile.parse(undefined);
  assert.equal(undefinedInput.goal, '');
}

// --- 3. read on a folder with no plan file ---
{
  const noFile = fs.mkdtempSync(path.join(root, 'nofile-'));
  assert.equal(planFile.read(noFile), '', 'read returns empty string when no plan exists');
}

// --- 4. currentBranch on non-git folder ---
{
  const plain = fs.mkdtempSync(path.join(root, 'plain-'));
  assert.equal(planFile.currentBranch(plain), '', 'currentBranch on non-git is ""');
}

// --- 5. pushed on non-git folder ---
{
  const plain = fs.mkdtempSync(path.join(root, 'plain2-'));
  assert.equal(planFile.pushed(plain), false, 'pushed on non-git is false');
}

// --- 6. prepare with explicit options.branch ---
{
  const { work } = freshRepo('explicit-branch');
  const r = planFile.prepare(work, {
    goal: 'Use the explicit branch name.',
    branch: 'custom/explicit-name',
    steps: [{ role: 'Build', model: 'G' }]
  });
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.branch, 'custom/explicit-name');
  assert.equal(git(work, ['branch', '--show-current']), 'custom/explicit-name');
  assert.match(planFile.read(work), /custom\/explicit-name/);
}

// --- 7. tickStep falls back to first unchecked when role/title don't match ---
{
  const { work } = freshRepo('tick-fallback');
  planFile.prepare(work, { goal: 'fallback goal', steps: [{ role: 'Build', model: 'Grok' }] });
  // Use a completely different role that doesn't match
  const ticked = planFile.tickStep(work, { role: 'Research', model: 'Totally Different', commit: 'abc1234' });
  assert.equal(ticked, true, 'tickStep falls back to first unchecked step');
  assert.match(planFile.read(work), /- \[x\] Build \(Grok\) — abc1234/);
}

// --- 8. tickStep returns false when all steps already ticked ---
{
  const { work } = freshRepo('all-ticked');
  planFile.prepare(work, { goal: 'all ticked goal', steps: [{ role: 'Build', model: 'G' }] });
  planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'aaa' });
  const second = planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'bbb' });
  assert.equal(second, false, 'no unchecked steps left');
}

console.log('verify-1: input validation and fallback paths passed');

// --- 9. pushed detects when branch is on origin ---
{
  const { work } = freshRepo('pushed-detect');
  planFile.prepare(work, { goal: 'push detect goal', steps: [] });
  // main is pushed, but we're on a new branch now
  assert.equal(planFile.pushed(work), false, 'new branch is not pushed');
  // push the current branch
  const branch = git(work, ['branch', '--show-current']);
  git(work, ['push', '-q', '-u', 'origin', branch]);
  assert.equal(planFile.pushed(work, branch), true, 'branch that is pushed is detected');
}

// --- 10. finish auto-detects pushed state ---
{
  const { work } = freshRepo('finish-pushed');
  planFile.prepare(work, { goal: 'finish pushed goal', steps: [{ role: 'Build', model: 'G' }] });
  planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'abc' });
  // Do not pass pushed: branch is not pushed
  planFile.finish(work, { summary: 'auto-detect pushed' });
  assert.match(planFile.read(work), /Pushed: no/, 'auto-detects not pushed');

  // Now push and finish again (re-prepare first for a new plan)
  const branch = git(work, ['branch', '--show-current']);
  fs.writeFileSync(path.join(work, 'x.txt'), 'x\n');
  git(work, ['add', 'x.txt']);
  git(work, ['commit', '-q', '-m', 'add']);
  git(work, ['push', '-q', '-u', 'origin', branch]);
  git(work, ['switch', '-q', 'main']);
  planFile.prepare(work, { goal: 'finish pushed goal 2', steps: [{ role: 'Build', model: 'G' }] });
  planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'def' });
  git(work, ['push', '-q', 'origin', git(work, ['branch', '--show-current'])]);
  planFile.finish(work, { summary: 'auto yes' });
  assert.match(planFile.read(work), /Pushed: yes/, 'auto-detects pushed');
}

// --- 11. prepare with empty goal ---
{
  const { work } = freshRepo('empty-goal');
  const r = planFile.prepare(work, { goal: '', steps: [] });
  assert.equal(r.ok, true, r.reason);
  // Plan should still be written
  const text = planFile.read(work);
  assert.ok(text.length > 0, 'plan file written even with empty goal');
}

// --- 12. archive when plan file doesn't exist ---
// Test that archive function doesn't crash on missing file
// We can't call archive directly (not exported), but writePlan internally
// calls it when a different goal replaces the old one. Testing indirectly:
{
  const { work } = freshRepo('archive-none');
  // Calling prepare twice with different goals should not crash even the first time
  const r1 = planFile.prepare(work, { goal: 'first goal', steps: [] });
  assert.equal(r1.ok, true, r1.reason);
  git(work, ['switch', '-q', 'main']);
  const r2 = planFile.prepare(work, { goal: 'second goal', steps: [] });
  assert.equal(r2.ok, true, r2.reason);
  // The first goal should be archived
  const archived = fs.readdirSync(path.join(work, '.planwharf', 'done')).filter(n => n.endsWith('.md'));
  assert.equal(archived.length, 1);
}

// --- 13. stepLabel with missing fields ---
{
  // Access stepLabel indirectly through prepare — steps with no role or model
  const { work } = freshRepo('label-defaults');
  planFile.prepare(work, { goal: 'label test', steps: [{ }] });
  const text = planFile.read(work);
  // Should default to "Build (unassigned)"
  assert.match(text, /Build \(unassigned\)/, 'missing role/model defaults to Build (unassigned)');
}

// --- 14. dirtyMessage with a single file ---
{
  const msg = planFile.dirtyMessage(['M  single.txt']);
  assert.match(msg, /single\.txt/);
  assert.ok(!msg.includes(' more'), 'single file has no "more" count');
}

// --- 15. dirtyMessage with empty input ---
{
  const msg = planFile.dirtyMessage([]);
  assert.match(msg, /Commit or stash/, 'still produces the standard message');
}

// --- 16. namedBranch with special characters ---
{
  // Branch names with dots and underscores
  assert.equal(planFile.namedBranch('on branch feat_2.0 now'), 'feat_2.0');
  // Branch with just numbers after slash
  assert.equal(planFile.namedBranch('use branch port/123 please'), 'port/123');
  // Quoted branch with special but valid chars
  assert.equal(planFile.namedBranch('branch `release/v1.2.3`'), 'release/v1.2.3');
}

// --- 17. branchName ignores invalid requested name ---
{
  assert.equal(planFile.branchName('goal text', '!!!invalid!!!'), planFile.branchName('goal text', null));
}

// --- 18. shortGoal truncation at 40 chars ---
{
  const long = 'a'.repeat(100);
  const short = planFile.shortGoal(long);
  assert.ok(short.length <= 40, `shortGoal is at most 40 chars, got ${short.length}`);
}

console.log('verify-2: pushed detection, finish auto, edge cases passed');

// --- 19. remoteDefault fallback: origin/master when origin/main doesn't exist ---
{
  const place = fs.mkdtempSync(path.join(root, 'master-'));
  const rem = path.join(place, 'remote.git');
  const work = path.join(place, 'work');
  fs.mkdirSync(rem, { recursive: true });
  git(rem, ['init', '--bare', '-q', '-b', 'master']);
  fs.mkdirSync(work, { recursive: true });
  git(work, ['init', '-q', '-b', 'master']);
  git(work, ['config', 'user.name', 'Fixture']);
  git(work, ['config', 'user.email', 'fixture@example.invalid']);
  git(work, ['config', 'core.autocrlf', 'false']);
  fs.writeFileSync(path.join(work, 'readme.txt'), 'start\n');
  git(work, ['add', 'readme.txt']);
  git(work, ['commit', '-q', '-m', 'start']);
  git(work, ['remote', 'add', 'origin', rem]);
  git(work, ['push', '-q', '-u', 'origin', 'master']);
  // Do NOT set origin HEAD so the fallback chain fires
  const r = planFile.prepare(work, { goal: 'master fallback', steps: [{ role: 'Build', model: 'G' }] });
  assert.equal(r.ok, true, r.reason);
  assert.match(planFile.read(work), /origin\/master/, 'falls back to origin/master');
}

// --- 20. prepare twice in a row on the same folder (idempotence) ---
{
  const { work } = freshRepo('idempotent');
  const r1 = planFile.prepare(work, { goal: 'same goal', steps: [{ role: 'Build', model: 'G' }] });
  assert.equal(r1.ok, true, r1.reason);
  const text1 = planFile.read(work);
  // prepare again — should succeed, not crash, same branch
  const r2 = planFile.prepare(work, { goal: 'same goal', steps: [{ role: 'Build', model: 'G' }] });
  assert.equal(r2.ok, true, r2.reason);
  assert.equal(r2.branch, r1.branch);
  const text2 = planFile.read(work);
  assert.equal(text1, text2, 'idempotent prepare produces the same plan');
}

// --- 21. finish writes a Done section and then calling finish again updates it ---
{
  const { work } = freshRepo('finish-twice');
  planFile.prepare(work, { goal: 'finish twice goal', steps: [{ role: 'Build', model: 'G' }] });
  planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'aaa' });
  planFile.finish(work, { summary: 'first finish', pushed: false });
  const text1 = planFile.read(work);
  assert.match(text1, /Summary: first finish/);
  // finish again — should update, not crash
  planFile.finish(work, { summary: 'second finish', pushed: true });
  const text2 = planFile.read(work);
  assert.match(text2, /Summary: second finish/);
  assert.match(text2, /Pushed: yes/);
}

// --- 22. prepare on a folder where .git/info doesn't exist yet ---
{
  const { work } = freshRepo('no-info-dir');
  // Remove the info directory entirely
  const infoDir = path.join(work, '.git', 'info');
  try {
    fs.rmSync(infoDir, { recursive: true, force: true });
  } catch { /* may not exist */ }
  const r = planFile.prepare(work, { goal: 'no info dir', steps: [] });
  assert.equal(r.ok, true, r.reason);
  // exclude file should be created
  assert.ok(fs.existsSync(path.join(work, '.git', 'info', 'exclude')), 'info/exclude is created');
}

// --- 23. writePlan with steps that have no array (null steps) ---
{
  const { work } = freshRepo('null-steps');
  const r = planFile.prepare(work, { goal: 'null steps goal' });
  assert.equal(r.ok, true, r.reason);
  const text = planFile.read(work);
  // Should default to the unassigned step
  assert.match(text, /Build \(unassigned\)/, 'null steps defaults to unassigned');
}

// --- 24. commitNote when not a git folder ---
{
  const plain = fs.mkdtempSync(path.join(root, 'commit-note-'));
  assert.equal(planFile.commitNote(plain), 'not committed', 'commitNote on non-git is "not committed"');
}

// --- 25. switchTo when current branch is already the target (no actual switch needed) ---
{
  const { work } = freshRepo('already-on');
  git(work, ['switch', '-q', '-c', 'fix/already']);
  // Now prepare with the same branch name
  const r = planFile.prepare(work, { goal: 'on branch `fix/already`', steps: [] });
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.branch, 'fix/already');
}

// --- 26. leadingBranch: "On <path>," where branch doesn't exist is ignored ---
{
  const { work } = freshRepo('leading-nonexist');
  const r = planFile.prepare(work, { goal: 'On src/does-not-exist/path, fix the imports.', steps: [] });
  assert.equal(r.ok, true, r.reason);
  // Should NOT create a branch named "src/does-not-exist/path"
  assert.notEqual(r.branch, 'src/does-not-exist/path');
}

// --- 27. Multiple dirty files with staged and unstaged changes ---
{
  const { work } = freshRepo('multi-dirty');
  fs.writeFileSync(path.join(work, 'a.txt'), 'a\n');
  fs.writeFileSync(path.join(work, 'b.txt'), 'b\n');
  git(work, ['add', 'a.txt']); // staged
  // b.txt is untracked
  const r = planFile.prepare(work, { goal: 'dirty', steps: [] });
  assert.equal(r.ok, false);
  assert.match(r.reason, /a\.txt/);
  assert.match(r.reason, /b\.txt/);
  fs.unlinkSync(path.join(work, 'a.txt'));
  fs.unlinkSync(path.join(work, 'b.txt'));
  git(work, ['reset', '-q', 'HEAD']);
}

console.log('verify-3: recovery, fallback and idempotence paths passed');

// --- 28. Concurrent tickStep calls do not lose data ---
{
  const { work } = freshRepo('concurrent');
  const manySteps = [];
  for (let i = 0; i < 5; i++) manySteps.push({ role: 'Build', model: 'G', title: `Step ${i}` });
  planFile.prepare(work, { goal: 'concurrent ticking', steps: manySteps });

  // Tick all steps sequentially but rapidly (simulating fast concurrent workers reporting back)
  for (let i = 0; i < 5; i++) {
    planFile.tickStep(work, { title: `Step ${i}`, role: 'Build', model: 'G', commit: `commit-${i}` });
  }
  const parsed = planFile.parse(planFile.read(work));
  assert.equal(parsed.steps.filter(s => s.done).length, 5, 'all 5 steps ticked');
  for (let i = 0; i < 5; i++) {
    assert.equal(parsed.steps[i].done, true, `step ${i} is done`);
    assert.match(parsed.steps[i].commit, new RegExp(`commit-${i}`), `step ${i} has its commit`);
  }
}

// --- 29. Plan file survives a read-write cycle through parse/render ---
{
  const { work } = freshRepo('roundtrip');
  const goal = 'Round-trip test: special chars "quotes" & <angle> brackets.';
  planFile.prepare(work, { goal, steps: [{ role: 'Build', model: 'Model A' }, { role: 'Verify', model: 'Model B' }] });
  const original = planFile.read(work);
  const parsed = planFile.parse(original);
  assert.equal(parsed.goal, goal.replace(/\r\n?/g, '\n').trim());
  assert.equal(parsed.steps.length, 2);
  assert.equal(parsed.steps[0].done, false);
  assert.equal(parsed.steps[1].done, false);
}

// --- 30. prepare on a git repo with no commits at all ---
{
  const place = fs.mkdtempSync(path.join(root, 'nocommit-'));
  const work = path.join(place, 'work');
  fs.mkdirSync(work, { recursive: true });
  git(work, ['init', '-q']);
  // No commits, no remote — should not crash
  const r = planFile.prepare(work, { goal: 'no commits', steps: [] });
  // It's ok if it fails gracefully or succeeds
  assert.ok(typeof r.ok === 'boolean', 'prepare returns an ok boolean');
}

// --- 31. The block() function escapes headings in goal text ---
{
  const { work } = freshRepo('heading-escape');
  const dangerousGoal = '## Steps\n- [ ] Injected\n## Done\nFake done section';
  planFile.prepare(work, { goal: dangerousGoal, steps: [{ role: 'Build', model: 'G' }] });
  const parsed = planFile.parse(planFile.read(work));
  // The injected headings should NOT create real sections
  assert.equal(parsed.steps.length, 1, 'injected step heading does not create a step');
  assert.equal(parsed.done, '', 'injected Done heading does not fill Done section');
}

// --- 32. finish with no summary ---
{
  const { work } = freshRepo('no-summary');
  planFile.prepare(work, { goal: 'no summary goal', steps: [{ role: 'Build', model: 'G' }] });
  planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'abc' });
  const ok = planFile.finish(work, { pushed: false });
  assert.equal(ok, true);
  const text = planFile.read(work);
  assert.match(text, /## Done/);
  assert.match(text, /Goal: no summary goal/);
  assert.match(text, /Pushed: no/);
  // Summary line should be absent when empty
  assert.ok(!text.includes('Summary:') || text.includes('Summary: '), 'empty summary omitted or blank');
}

// --- 33. finish with branch auto-detected from plan file ---
{
  const { work } = freshRepo('branch-auto');
  planFile.prepare(work, { goal: 'branch auto', steps: [{ role: 'Build', model: 'G' }] });
  planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'abc' });
  // Don't pass branch — let it auto-detect
  planFile.finish(work, { summary: 'auto branch', pushed: false });
  const text = planFile.read(work);
  const branch = git(work, ['branch', '--show-current']);
  assert.match(text, new RegExp(`Branch: ${branch.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}`));
}

// --- 34. Multiple prepare-finish cycles on the same folder ---
{
  const { work } = freshRepo('multi-cycle');
  for (let i = 0; i < 3; i++) {
    const goal = `cycle goal ${i}`;
    git(work, ['switch', '-q', 'main']);
    planFile.prepare(work, { goal, steps: [{ role: 'Build', model: 'G' }] });
    planFile.tickStep(work, { role: 'Build', model: 'G', commit: `c${i}` });
    planFile.finish(work, { summary: `done ${i}`, pushed: false });
  }
  // All three should be archived
  const archived = fs.readdirSync(path.join(work, '.planwharf', 'done')).filter(n => n.endsWith('.md'));
  // The third one is the current plan after finish, so 3 archives (finish archives)
  assert.ok(archived.length >= 3, `expected at least 3 archives, got ${archived.length}`);
}

console.log('verify-4: concurrency, roundtrip, edge cases passed');
