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
