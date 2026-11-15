'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const planFile = require('./plan-file.cjs');

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-plan-file-'));
const remote = path.join(root, 'remote.git');
const folder = path.join(root, 'work');

function git(cwd, args) {
  const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('GIT_')));
  const result = spawnSync('git', args, { cwd, encoding: 'utf8', windowsHide: true, env });
  assert.equal(result.status, 0, `${args.join(' ')} failed: ${result.stderr || result.stdout}`);
  return (result.stdout || '').trim();
}

function setup() {
  fs.mkdirSync(remote, { recursive: true });
  git(remote, ['init', '--bare', '-q', '-b', 'main']);
  fs.mkdirSync(folder, { recursive: true });
  git(folder, ['init', '-q', '-b', 'main']);
  git(folder, ['config', 'user.name', 'Fixture']);
  git(folder, ['config', 'user.email', 'fixture@example.invalid']);
  fs.writeFileSync(path.join(folder, 'readme.txt'), 'start\n');
  git(folder, ['add', 'readme.txt']);
  git(folder, ['commit', '-q', '-m', 'start']);
  git(folder, ['remote', 'add', 'origin', remote]);
  git(folder, ['push', '-q', '-u', 'origin', 'main']);
  git(folder, ['remote', 'set-head', 'origin', 'main']);
  git(folder, ['checkout', '-q', '-b', 'job-04']);
  fs.writeFileSync(path.join(folder, 'readme.txt'), 'other job\n');
  git(folder, ['commit', '-q', '-am', 'other job']);
}

setup();
const base = git(folder, ['rev-parse', 'origin/main']);
const goal = 'Do job 05 on a new branch `fix/plan-file` from origin/main.';
const steps = [
  { role: 'Build', model: 'Grok 4.7 Business', title: 'Implement the whole job' },
  { role: 'Verify', model: 'Opus 5.5', title: 'Verify the work independently' }
];

const prepared = planFile.prepare(folder, { goal, steps });
assert.equal(prepared.ok, true, prepared.reason);
assert.equal(prepared.branch, 'fix/plan-file');
assert.equal(git(folder, ['branch', '--show-current']), 'fix/plan-file');
assert.equal(git(folder, ['rev-parse', 'HEAD']), base, 'the branch starts at origin/main, not the branch the folder was on');
assert.equal(spawnSync('git', ['rev-parse', '--abbrev-ref', '@{upstream}'], { cwd: folder }).status === 0, false, 'the new branch does not track main, so a plain push never goes to main');

const text = planFile.read(folder);
assert.match(text, new RegExp(goal.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
assert.match(text, new RegExp(`origin/main ${base.slice(0, 7)}`));
assert.match(text, /## Branch\s+fix\/plan-file/);
assert.match(text, /- \[ \] Build \(Grok 4\.7 Business\)/);
assert.match(text, /- \[ \] Verify \(Opus 5\.5\)/);
assert.match(text, /No push or merge\. Files outside the goal are not touched\./);
assert.match(text, /## Done\s*$/);
const exclude = fs.readFileSync(path.join(folder, '.git', 'info', 'exclude'), 'utf8');
assert.equal(exclude.split(/\r?\n/).filter(line => line.trim() === '/.planwharf/').length, 1);
planFile.prepare(folder, { goal, steps });
const excludeAgain = fs.readFileSync(path.join(folder, '.git', 'info', 'exclude'), 'utf8');
assert.equal(excludeAgain.split(/\r?\n/).filter(line => line.trim() === '/.planwharf/').length, 1);

fs.writeFileSync(path.join(folder, 'notes.txt'), 'dirty\n');
const blocked = planFile.prepare(folder, { goal, steps });
assert.equal(blocked.ok, false);
assert.equal(blocked.reason, 'Commit or stash the changes in the folder first: notes.txt, then press Let the crew do it again.');
fs.unlinkSync(path.join(folder, 'notes.txt'));

fs.writeFileSync(path.join(folder, 'work.txt'), 'work\n');
git(folder, ['add', 'work.txt']);
git(folder, ['commit', '-q', '-m', 'work']);
const workHead = git(folder, ['rev-parse', 'HEAD']);
git(folder, ['checkout', '-q', 'main']);
const reused = planFile.prepare(folder, { goal, steps });
assert.equal(reused.ok, true, reused.reason);
assert.equal(git(folder, ['branch', '--show-current']), 'fix/plan-file');
assert.equal(git(folder, ['rev-parse', 'HEAD']), workHead, 'an existing branch is reused');

assert.equal(planFile.tickStep(folder, { role: 'Build', model: 'Grok 4.7 Business', commit: 'not committed' }), true);
assert.match(planFile.read(folder), /- \[x\] Build \(Grok 4\.7 Business\) — not committed/);
assert.match(planFile.read(folder), /- \[ \] Verify \(Opus 5\.5\)/);

assert.equal(planFile.finish(folder, { summary: 'The plan file is in place.', branch: 'fix/plan-file', pushed: false }), true);
const finished = planFile.read(folder);
assert.match(finished, /## Done[\s\S]*Goal: Do job 05/);
assert.match(finished, /Summary: The plan file is in place\./);
assert.match(finished, /Branch: fix\/plan-file/);
assert.match(finished, /Pushed: no/);
const doneDir = path.join(folder, '.planwharf', 'done');
const kept = fs.readdirSync(doneDir).filter(name => name.endsWith('.md'));
assert.equal(kept.length, 1);
assert.match(fs.readFileSync(path.join(doneDir, kept[0]), 'utf8'), /Summary: The plan file is in place\./);

const { askPrompt } = require('./runner.cjs');
assert.match(askPrompt({ question: 'Next?', model: { label: 'Grok' }, state: { workPlan: 'GOAL-SENTINEL' } }), /Never suggest again a goal that is in its Done section/);
assert.match(askPrompt({ question: 'Next?', model: { label: 'Grok' }, state: { workPlan: 'GOAL-SENTINEL' } }), /first step that is not ticked/);
assert.match(askPrompt({ question: 'Next?', model: { label: 'Grok' }, state: { workPlan: 'GOAL-SENTINEL' } }), /GOAL-SENTINEL/);

const plain = path.join(root, 'plain');
fs.mkdirSync(plain);
const plainResult = planFile.prepare(plain, { goal: 'Notes only.', steps: [{ role: 'Build', model: 'Grok' }] });
assert.equal(plainResult.git, false);
assert.equal(plainResult.ok, true);
assert.equal(fs.existsSync(path.join(plain, '.git')), false);
assert.match(planFile.read(plain), /Not a git repository/);
assert.match(planFile.read(plain), /- \[ \] Build \(Grok\)/);

git(folder, ['remote', 'set-url', 'origin', path.join(root, 'missing.git')]);
const here = git(folder, ['rev-parse', 'HEAD']);
const offline = planFile.prepare(folder, { goal: 'Work while the remote is offline.' });
assert.equal(offline.ok, true, offline.reason);
assert.match(offline.chat, /The remote could not be read, so the crew starts on the current commit/);
assert.match(planFile.read(folder), /Offline: the remote could not be read/);
assert.equal(git(folder, ['rev-parse', 'HEAD']), here, 'offline work stays on the current commit');
git(folder, ['remote', 'set-url', 'origin', remote]);

console.log('plan-file tests passed');

// --- Independent verification (ST-02): edge, error and recovery paths the first pass left uncovered. ---

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

// The branch name is read from the lead's text: backticks, quotes or a bare token, never a stop-word.
assert.equal(planFile.namedBranch('Do it on branch `fix/plan-file` now'), 'fix/plan-file');
assert.equal(planFile.namedBranch("use branch 'feat/x' please"), 'feat/x');
assert.equal(planFile.namedBranch('on branch port/09-phase2 next'), 'port/09-phase2');
assert.equal(planFile.namedBranch('just do the work, nothing named here'), null);
assert.equal(planFile.namedBranch('create the branch from origin/main'), null);
assert.equal(planFile.branchName('Make the thing nicer', null), 'planwharf/make-the-thing-nicer');
assert.equal(planFile.branchName('whatever the goal says', 'given/branch'), 'given/branch');
assert.equal(planFile.shortGoal(''), 'work');
assert.equal(planFile.shortGoal('!!!'), 'work');

// The dirty message lists up to eight names, counts the rest, and follows a rename to its new name.
const manyDirty = ['A  a.txt', 'A  b.txt', 'A  c.txt', 'A  d.txt', 'A  e.txt', 'A  f.txt', 'A  g.txt', 'A  h.txt', 'A  i.txt', 'A  j.txt'];
assert.match(planFile.dirtyMessage(manyDirty), /first: a\.txt, b\.txt, c\.txt, d\.txt, e\.txt, f\.txt, g\.txt, h\.txt, and 2 more, then press/);
assert.match(planFile.dirtyMessage(['R  old.txt -> renamed.txt']), /first: renamed\.txt, then press/);

// A re-run of the same goal keeps the ticked boxes (a resume after a stop); a new goal archives the old plan.
{
  const { work } = freshRepo('resume');
  const first = planFile.prepare(work, { goal: 'First goal to finish', steps: [{ role: 'Build', model: 'Grok' }] });
  assert.equal(first.ok, true, first.reason);
  assert.equal(planFile.tickStep(work, { role: 'Build', model: 'Grok', commit: 'abc1234' }), true);
  git(work, ['switch', '-q', 'main']);
  const again = planFile.prepare(work, { goal: 'First goal to finish', steps: [{ role: 'Build', model: 'Grok' }] });
  assert.equal(again.ok, true, again.reason);
  assert.match(planFile.read(work), /- \[x\] Build \(Grok\) — abc1234/);
  assert.equal(fs.existsSync(path.join(work, '.planwharf', 'done')), false, 'the same goal is not archived');
  git(work, ['switch', '-q', 'main']);
  const next = planFile.prepare(work, { goal: 'A second, different goal', steps: [{ role: 'Build', model: 'Grok' }] });
  assert.equal(next.ok, true, next.reason);
  assert.match(planFile.read(work), /A second, different goal/);
  assert.match(planFile.read(work), /- \[ \] Build \(Grok\)/);
  const archived = fs.readdirSync(path.join(work, '.planwharf', 'done')).filter(name => name.endsWith('.md'));
  assert.equal(archived.length, 1, 'the old plan is kept under done/');
  assert.match(fs.readFileSync(path.join(work, '.planwharf', 'done', archived[0]), 'utf8'), /First goal to finish/);
}

// An existing branch that does not contain the base commit is refused, not silently reused on the old base.
{
  const { work } = freshRepo('stale');
  git(work, ['switch', '-q', '-c', 'port/stale']);
  fs.writeFileSync(path.join(work, 'stale.txt'), 's\n');
  git(work, ['add', 'stale.txt']);
  git(work, ['commit', '-q', '-m', 'stale work off the old base']);
  git(work, ['switch', '-q', 'main']);
  fs.writeFileSync(path.join(work, 'readme.txt'), 'advanced\n');
  git(work, ['commit', '-q', '-am', 'advance main']);
  git(work, ['push', '-q', 'origin', 'main']);
  git(work, ['switch', '-q', 'port/stale']);
  git(work, ['status', '--porcelain']);
  const refused = planFile.prepare(work, { goal: 'resume on branch `port/stale`', steps: [{ role: 'Build', model: 'Grok' }] });
  assert.equal(refused.ok, false);
  assert.match(refused.reason, /Branch port\/stale does not contain origin\/main [0-9a-f]{7}\./);
}

// Two goals that repeat on the same day do not overwrite each other's archive.
{
  const { work } = freshRepo('collide');
  for (const goal of ['alpha goal', 'beta goal', 'alpha goal', 'beta goal']) {
    const r = planFile.prepare(work, { goal, steps: [{ role: 'Build', model: 'G' }] });
    assert.equal(r.ok, true, r.reason);
    git(work, ['switch', '-q', 'main']);
  }
  const day = new Date().toISOString().slice(0, 10);
  const names = fs.readdirSync(path.join(work, '.planwharf', 'done')).filter(name => name.endsWith('.md')).sort();
  assert.deepEqual(names, [`${day}-alpha-goal-2.md`, `${day}-alpha-goal.md`, `${day}-beta-goal.md`], 'a repeated goal keeps a numbered copy');
}

// commitNote: the base is uncommitted, a real commit shows its short sha, a dirty tree is uncommitted again.
{
  const { work } = freshRepo('note');
  const prep = planFile.prepare(work, { goal: 'note goal', steps: [{ role: 'Build', model: 'G' }] });
  assert.equal(prep.ok, true, prep.reason);
  assert.equal(planFile.commitNote(work), 'not committed', 'at the base commit nothing new is committed');
  fs.writeFileSync(path.join(work, 'done.txt'), 'x\n');
  git(work, ['add', 'done.txt']);
  git(work, ['commit', '-q', '-m', 'the work']);
  assert.match(planFile.commitNote(work), /^[0-9a-f]{7}$/, 'a new commit is reported by its short sha');
  fs.writeFileSync(path.join(work, 'leftover.txt'), 'dirty\n');
  assert.equal(planFile.commitNote(work), 'not committed', 'a dirty tree is not reported as committed');
}

// tickStep and finish do nothing and report false when there is no plan file yet.
{
  const empty = fs.mkdtempSync(path.join(root, 'empty-'));
  assert.equal(planFile.tickStep(empty, { role: 'Build', model: 'G' }), false);
  assert.equal(planFile.finish(empty, { summary: 'nothing to finish' }), false);
}

console.log('plan-file verification tests passed');

// --- Second verification pass: wording that is not a branch, the leading "On <branch>," form, finished-plan archive. ---

// Plain words after "branch" are not branch names; explicit wording and branch-shaped tokens still are.
assert.equal(planFile.namedBranch('fix the failures (plan file, branch creation, dirty-folder gate)'), null);
assert.equal(planFile.namedBranch('commit on that branch, and do not push'), null);
assert.equal(planFile.namedBranch('the branch main.'), null);
assert.equal(planFile.namedBranch('work on branch develop today'), 'develop');
assert.equal(planFile.namedBranch('a new branch named feature please'), 'feature');
assert.equal(planFile.namedBranch('cut branch v2 for it'), 'v2');
assert.equal(planFile.namedBranch('cut branch feat_x, ok'), 'feat_x');

// The goal of this very job: "On fix/plan-file, ... branch creation ..." stays on the existing branch.
{
  const { work } = freshRepo('leading');
  git(work, ['switch', '-q', '-c', 'fix/plan-file']);
  fs.writeFileSync(path.join(work, 'fix.txt'), 'fix\n');
  git(work, ['add', 'fix.txt']);
  git(work, ['commit', '-q', '-m', 'fix work']);
  const fixHead = git(work, ['rev-parse', 'HEAD']);
  git(work, ['switch', '-q', 'main']);
  const jobGoal = 'On fix/plan-file, re-run node --test, fix the failures (job 05 plan file, branch creation, dirty-folder gate), commit on that branch, and do not push or merge.';
  const r = planFile.prepare(work, { goal: jobGoal, steps: [{ role: 'Build', model: 'G' }] });
  assert.equal(r.ok, true, r.reason);
  assert.equal(r.branch, 'fix/plan-file');
  assert.equal(git(work, ['rev-parse', 'HEAD']), fixHead, 'the named branch is reused, not a new "creation" branch');
  assert.equal(spawnSync('git', ['show-ref', '--verify', '--quiet', 'refs/heads/creation'], { cwd: work }).status, 1);

  // "On <path>," where no such branch exists is not taken as a branch name.
  git(work, ['switch', '-q', 'main']);
  const pathGoal = 'On src/app.js, tidy the imports.';
  const p = planFile.prepare(work, { goal: pathGoal, steps: [] });
  assert.equal(p.ok, true, p.reason);
  assert.equal(p.branch, 'planwharf/' + planFile.shortGoal(pathGoal));
}

// A finished plan is archived once by finish(); starting the next goal does not archive it again.
{
  const { work } = freshRepo('finish-once');
  assert.equal(planFile.prepare(work, { goal: 'Finished goal', steps: [{ role: 'Build', model: 'G' }] }).ok, true);
  assert.equal(planFile.finish(work, { summary: 'all done', pushed: false }), true);
  git(work, ['switch', '-q', 'main']);
  assert.equal(planFile.prepare(work, { goal: 'Next goal', steps: [{ role: 'Build', model: 'G' }] }).ok, true);
  const archived = fs.readdirSync(path.join(work, '.planwharf', 'done')).filter(name => name.endsWith('.md'));
  assert.equal(archived.length, 1, 'one archived copy of the finished plan');
  assert.match(fs.readFileSync(path.join(work, '.planwharf', 'done', archived[0]), 'utf8'), /Summary: all done/);
  assert.equal(planFile.parse(planFile.read(work)).done, '', 'the new plan starts with an empty Done section');
}

// A goal written with CRLF and a summary that quotes markdown headings round-trip without breaking the sections.
{
  const { work } = freshRepo('headings');
  const crlfGoal = 'Line one\r\n## Steps\r\n- [ ] not a real step';
  assert.equal(planFile.prepare(work, { goal: crlfGoal, steps: [{ role: 'Build', model: 'G' }] }).ok, true);
  assert.equal(planFile.tickStep(work, { role: 'Build', model: 'G', commit: 'abc1234' }), true);
  let parsed = planFile.parse(planFile.read(work));
  assert.equal(parsed.steps.length, 1, 'the quoted heading in the goal does not add steps');
  assert.equal(parsed.steps[0].done, true);
  git(work, ['switch', '-q', 'main']);
  assert.equal(planFile.prepare(work, { goal: crlfGoal, steps: [{ role: 'Build', model: 'G' }] }).ok, true);
  assert.equal(fs.existsSync(path.join(work, '.planwharf', 'done')), false, 'the same CRLF goal is recognised, not archived');
  assert.match(planFile.read(work), /- \[x\] Build \(G\) — abc1234/, 'the ticked step survives the re-run');
  assert.equal(planFile.finish(work, { summary: 'ok\n## Steps\n- [ ] injected', pushed: false }), true);
  parsed = planFile.parse(planFile.read(work));
  assert.equal(parsed.steps.length, 1, 'a summary that quotes a heading does not add steps');
  assert.match(parsed.done, /Summary: ok/);
}

// Twin steps (same role and model) are told apart by title and ticked one by one.
{
  const { work } = freshRepo('twins');
  const twinSteps = [{ role: 'Build', model: 'G', title: 'Part one' }, { role: 'Build', model: 'G', title: 'Part two' }];
  assert.equal(planFile.prepare(work, { goal: 'twin goal', steps: twinSteps }).ok, true);
  assert.equal(planFile.tickStep(work, { title: 'Part two', role: 'Build', model: 'G', commit: 'bbbbbbb' }), true);
  const lines = planFile.parse(planFile.read(work)).steps;
  assert.deepEqual(lines.map(step => step.done), [false, true]);
  assert.equal(lines[1].commit, 'bbbbbbb');
}

// The exclude file without a final newline gets the line on its own line, once.
{
  const { work } = freshRepo('exclude');
  const excludeFile = path.join(work, '.git', 'info', 'exclude');
  fs.writeFileSync(excludeFile, '*.log');
  assert.equal(planFile.prepare(work, { goal: 'exclude goal', steps: [] }).ok, true);
  git(work, ['switch', '-q', 'main']);
  assert.equal(planFile.prepare(work, { goal: 'exclude goal', steps: [] }).ok, true);
  assert.deepEqual(fs.readFileSync(excludeFile, 'utf8').split('\n'), ['*.log', '/.planwharf/', '']);
  assert.equal(git(work, ['status', '--porcelain']), '', 'the plan folder stays out of git status');
}

console.log('plan-file second verification tests passed');

(async () => {
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const dir = fs.mkdtempSync(path.join(root, 'orch-'));
  const bare = path.join(dir, 'remote.git');
  const project = path.join(dir, 'project');
  fs.mkdirSync(bare);
  git(bare, ['init', '--bare', '-q', '-b', 'main']);
  fs.mkdirSync(project);
  git(project, ['init', '-q', '-b', 'main']);
  git(project, ['config', 'user.name', 'Fixture']);
  git(project, ['config', 'user.email', 'fixture@example.invalid']);
  fs.writeFileSync(path.join(project, 'readme.txt'), 'start\n');
  git(project, ['add', 'readme.txt']);
  git(project, ['commit', '-q', '-m', 'start']);
  git(project, ['remote', 'add', 'origin', bare]);
  git(project, ['push', '-q', 'origin', 'main']);
  git(project, ['remote', 'set-head', 'origin', 'main']);
  git(project, ['checkout', '-q', '-b', 'job-04']);
  fs.writeFileSync(path.join(project, 'other.txt'), 'other\n');
  git(project, ['add', 'other.txt']);
  git(project, ['commit', '-q', '-m', 'other']);
  fs.writeFileSync(path.join(project, 'dirty.txt'), 'x\n');
  const plan = { schema: 2, projects: [{ project: { id: 'demo', name: 'Demo', folder: project }, revisions: [{ id: 1, direction: 'Ship the next slice on branch `port/04-briefing-refusals-autonomy`.', open: [] }], tasks: [] }] };
  let asked = null;
  const orchestra = createOrchestrator({
    dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: [{ id: 'grok', label: 'Grok 4.7 Business', provider: 'codex', cli: 'gpt-6-luna', kind: 'premium', enabled: true, available: true, cost: 1, limit: 200 }],
    inspectFolder: () => ({ git: true, dirty: ['dirty.txt'], names: ['readme.txt'], fileCount: 1, head: 'x', lastCommit: 'x' }),
    askModel: async info => { asked = info; return { answer: 'Noted.' }; }
  });
  const blockedStart = await orchestra.startAutoTask('demo');
  assert.equal(blockedStart.ok, false);
  assert.equal(blockedStart.reason, 'Commit or stash the changes in the folder first: dirty.txt, then press Let the crew do it again.');
  assert.equal(git(project, ['branch', '--show-current']), 'job-04', 'a dirty folder does not change branch');
  fs.unlinkSync(path.join(project, 'dirty.txt'));
  const started = await orchestra.startAutoTask('demo');
  assert.equal(started.ok, true, started.reason);
  assert.equal(git(project, ['branch', '--show-current']), 'port/04-briefing-refusals-autonomy');
  assert.equal(git(project, ['rev-parse', 'HEAD']), git(project, ['rev-parse', 'origin/main']));
  const lead = await orchestra.setOrchestrator('grok', 'demo');
  assert.equal(lead.ok, true, lead.reason);
  const reply = await orchestra.ask('How does the folder look?', 'demo');
  assert.ok(asked, reply && reply.answer);
  assert.match(asked.state.workPlan, /port\/04-briefing-refusals-autonomy/);
  assert.equal(asked.state.workPlanPath, '.planwharf/plan.md');
  console.log('plan-file orchestrator tests passed');
})().catch(error => { console.error(error); process.exit(1); });
