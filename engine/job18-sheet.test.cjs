'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { gitEnvironment } = require('./project-workspace.cjs');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/18-mcp-protocol.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job18-sheet.test.cjs'
]);
const loadBearing = {
  ask: ['without reading the project again', 'same live worker session'],
  progress: ["program's output", 'without declaring success'],
  finish: ['filesChanged', 'blockers', 'nextAction', 'missing field', 'at once'],
  decision: ['highlighted', 'never cut', 'owner'],
  handoff: ['successor', 'switch']
};
const guards = [
  'malformed', 'oversized', 'Reject stale', 'cross-attempt', 'restart',
  'falls back', 'must not be completed twice', 'Workers never call each other',
  'Parallel attempts', 'claim, not proof', 'job 19', '#?',
  'dotnet build native/Planwharf.Engine',
  'dotnet test native/Planwharf.Engine.Tests',
  'node scripts/engineering/ci.cjs',
  'resultId', 'attemptId', 'subTaskId', 'megaTaskId',
  'reportPath', 'sources', 'recommendedNextAction',
  "owner's MCP checkbox", 'Do not turn on the owner'
];

function read(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}
function git(args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, (result.stderr || result.stdout || '').slice(0, 500));
  return (result.stdout || '').replace(/\r\n/g, '\n').trim();
}
const provenance = 'Independent verification named the design limits the sheet left implicit: ask stays in the same session without rereading the project, progress is not guessed from program output, finish fields are checked at once, and a decision is shown highlighted and never cut';
function sheetBranchErrors(snapshot) {
  const errors = [];
  const changed = snapshot.changed;
  if (!changed.length) errors.push('diff is empty');
  for (const file of changed) {
    if (!allowedDiff.has(file)) errors.push('file outside the sheet branch: ' + file);
  }
  if (!changed.includes(sheetPath)) errors.push('sheet missing from diff');
  if (!changed.includes('docs/NEXT-WORK.md')) errors.push('NEXT-WORK missing from diff');
  if (!changed.includes('docs/BUILT-WITH-PLANWHARF.md')) errors.push('BUILT-WITH missing from diff');
  if (snapshot.mergeBase !== snapshot.originMain) errors.push('branch is not based on refreshed origin/main');
  if (snapshot.branch && snapshot.branch !== 'feat/mcp-protocol') errors.push('branch name is ' + snapshot.branch);
  if (snapshot.diffCheckStatus !== 0 || snapshot.diffCheck) errors.push('diff check is not clean');
  if (!/\| 18 \|.*\| `feat\/mcp-protocol` \| Done, waiting for review \|/.test(snapshot.next)) errors.push('job 18 status drifted');
  for (const job of ['19', '20', '21', '22']) {
    if (!new RegExp('\\| ' + job + ' \\|.*\\| To do \\|').test(snapshot.next)) errors.push('job ' + job + ' is not still to do');
  }
  if (!/\| 18 \| #\? \|/.test(snapshot.built)) errors.push('job 18 pull request is not #?');
  if (/\| 18 \| #?\d+/.test(snapshot.built)) errors.push('job 18 is marked merged');
  if (snapshot.built.includes('**15 of 55**') !== snapshot.publishedBuilt.includes('**15 of 55**')) errors.push('progress count drifted');
  const mentions = text => (text.match(/about 1,040 of 10,300/g) || []).length;
  if (mentions(snapshot.built) !== mentions(snapshot.publishedBuilt)) errors.push('line count drifted');
  if (!snapshot.built.includes(provenance)) errors.push('independent-review provenance is missing or inaccurate');
  if (/\| 18 \|[^|\n]*\|[^|\n]*\|[^|\n]*Independent verification pending/.test(snapshot.built)) errors.push('independent-review provenance is still pending');
  return errors;
}
function designTools(design) {
  const tools = [];
  for (const line of design.split('\n')) {
    const match = line.match(/^\| `([a-z]+)` \| (.+) \|$/);
    if (match) tools.push({ name: match[1], text: match[2] });
  }
  return tools;
}
function assessSheet(sheet, design) {
  const errors = [];
  const tools = designTools(design);
  if (tools.map(tool => tool.name).join() !== 'ask,progress,finish,decision,handoff') errors.push('design tool table drifted');
  for (const tool of tools) {
    if (!sheet.includes('`' + tool.name + '`')) errors.push('missing tool ' + tool.name);
    for (const phrase of loadBearing[tool.name] || []) {
      if (!sheet.toLowerCase().includes(phrase.toLowerCase())) errors.push(tool.name + ' missing "' + phrase + '"');
    }
  }
  for (const phrase of guards) {
    if (!sheet.includes(phrase)) errors.push('sheet missing "' + phrase + '"');
  }
  if (/mcp server is (built|implemented|complete)/i.test(sheet)) errors.push('sheet marks the server built');
  if (/^\s*\| 18 \|.*\| Done \(#/m.test(sheet)) errors.push('sheet marks job 18 merged');
  return errors;
}
function without(sheet, phrase) {
  assert.ok(sheet.includes(phrase), 'fixture phrase missing: ' + phrase);
  return sheet.split(phrase).join('');
}

test('job 18 sheet keeps the design tools and the error, recovery, and concurrency limits', () => {
  const errors = assessSheet(read(sheetPath), read('docs/DESIGN-PIPELINE.md'));
  assert.deepEqual(errors, []);
});

test('a dropped limit, a built server, or a drifted tool table fails on its own', () => {
  const design = read('docs/DESIGN-PIPELINE.md');
  const sheet = read(sheetPath);
  const cases = [
    ['malformed input', without(sheet, 'malformed')],
    ['restart recovery', without(sheet, 'restart')],
    ['parallel isolation', without(sheet, 'Parallel attempts')],
    ['cross-attempt calls', without(sheet, 'cross-attempt')],
    ['fallback recovery', without(sheet, 'falls back')],
    ['double completion', without(sheet, 'must not be completed twice')],
    ['peer calls', without(sheet, 'Workers never call each other')],
    ['finish identity', without(sheet, 'resultId')],
    ['built server', sheet + '\nThe MCP server is built.\n']
  ];
  for (const [name, broken] of cases) {
    const errors = assessSheet(broken, design);
    assert.ok(errors.length > 0, name);
  }
  const drifted = design.replace('| `handoff` |', '| `whisper` |');
  assert.ok(assessSheet(sheet, drifted).some(error => error.includes('design tool table')));
});

test('parallel assessments do not mix a sound sheet with a broken one', async () => {
  const design = read('docs/DESIGN-PIPELINE.md');
  const sheet = read(sheetPath);
  const [sound, broken] = await Promise.all([
    Promise.resolve().then(() => assessSheet(sheet, design)),
    Promise.resolve().then(() => assessSheet(without(sheet, 'oversized'), design))
  ]);
  assert.deepEqual(sound, []);
  assert.ok(broken.some(error => error.includes('oversized')));
  assert.equal(broken.some(error => error.includes('malformed')), false);
});

function liveSheetSnapshot() {
  const diffCheck = spawnSync('git', ['diff', '--check', 'origin/main...HEAD'], { cwd: root, encoding: 'utf8' });
  return {
    changed: git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean),
    branch: git(['branch', '--show-current']),
    originMain: git(['rev-parse', 'origin/main']),
    mergeBase: git(['merge-base', 'HEAD', 'origin/main']),
    diffCheckStatus: diffCheck.status,
    diffCheck: (diffCheck.stdout || '').replace(/\r\n/g, '\n').trim(),
    next: read('docs/NEXT-WORK.md'),
    built: read('docs/BUILT-WITH-PLANWHARF.md'),
    publishedBuilt: git(['show', 'origin/main:docs/BUILT-WITH-PLANWHARF.md'])
  };
}
function isJob18SheetCheckout(snapshot) {
  // The sheet itself has to be in the diff. A branch that only touches the test is not this branch.
  const hasSheet = snapshot.changed.includes(sheetPath);
  const onlySheetFiles = hasSheet && snapshot.changed.every(file => allowedDiff.has(file));
  return snapshot.branch === 'feat/mcp-protocol' || onlySheetFiles;
}
test('the branch is only the sheet and its required docs on refreshed origin/main', () => {
  const snapshot = liveSheetSnapshot();
  // Another branch, including one that is merely behind origin/main, is not this failure.
  // The same contract still runs on an isolated repository below.
  if (!isJob18SheetCheckout(snapshot)) return;
  assert.deepEqual(sheetBranchErrors(snapshot), []);
});
test('job 18 records the independent review that actually happened', () => {
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  // "#?" until merged, then its pull request number.
  assert.match(built, /\| 18 \| #(\?|\d+) \|/);
  // The page is rewritten over time; the review's own words are in the job 18 commit, not checked here.
  assert.doesNotMatch(built, /\| 18 \|[^|\n]*\|[^|\n]*\|[^|\n]*Independent verification pending/);
});

function gitRaw(repo, args) {
  return spawnSync('git', ['-C', repo, ...args], { encoding: 'utf8', env: gitEnvironment(), windowsHide: true });
}
function gitOk(repo, args) {
  let result = gitRaw(repo, args);
  if (result.status !== 0 && args[0] === 'init') result = gitRaw(repo, args);
  assert.equal(result.status, 0, (result.stderr || result.stdout || '').slice(0, 500));
  return (result.stdout || '').replace(/\r\n/g, '\n').trim();
}
function writeRepoFile(repo, rel, text) {
  const full = path.join(repo, ...rel.split('/'));
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, text.endsWith('\n') ? text : text + '\n', 'utf8');
}
function row(job, branch, status) {
  return '| ' + job + ' | phase | job | `' + branch + '` | ' + status + ' |';
}
function nextDoc(status18, later) {
  return [
    row(18, 'feat/mcp-protocol', status18),
    row(19, 'feat/gate', later),
    row(20, 'feat/pipeline-intake', later),
    row(21, 'feat/pipeline-build', later),
    row(22, 'feat/pipeline-run', later)
  ].join('\n') + '\n';
}
function builtDoc(review) {
  return '**15 of 55**\nabout 1,040 of 10,300\n| 18 | #? | Wrote the worker MCP protocol sheet | ' + review + ' |\n';
}
function commitAll(repo, message) {
  gitOk(repo, ['add', '-A']);
  gitOk(repo, ['commit', '-qm', message]);
}
function snapshotOf(repo) {
  const diffCheck = gitRaw(repo, ['diff', '--check', 'origin/main...HEAD']);
  return {
    changed: gitOk(repo, ['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean),
    branch: gitOk(repo, ['branch', '--show-current']),
    originMain: gitOk(repo, ['rev-parse', 'origin/main']),
    mergeBase: gitOk(repo, ['merge-base', 'HEAD', 'origin/main']),
    diffCheckStatus: diffCheck.status,
    diffCheck: (diffCheck.stdout || '').replace(/\r\n/g, '\n').trim(),
    next: gitOk(repo, ['show', 'HEAD:docs/NEXT-WORK.md']),
    built: gitOk(repo, ['show', 'HEAD:docs/BUILT-WITH-PLANWHARF.md']),
    publishedBuilt: gitOk(repo, ['show', 'origin/main:docs/BUILT-WITH-PLANWHARF.md'])
  };
}
function withSheetRepo(setup) {
  const repo = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-job18-sheet-'));
  try {
    gitOk(repo, ['init', '-q', '-b', 'main']);
    gitOk(repo, ['config', 'user.name', 'Isolated fixture']);
    gitOk(repo, ['config', 'user.email', 'fixture@example.invalid']);
    gitOk(repo, ['config', 'core.autocrlf', 'false']);
    writeRepoFile(repo, 'docs/NEXT-WORK.md', nextDoc('To do', 'To do'));
    writeRepoFile(repo, 'docs/BUILT-WITH-PLANWHARF.md', builtDoc('—'));
    writeRepoFile(repo, 'README.md', 'fixture baseline\n');
    commitAll(repo, 'baseline');
    gitOk(repo, ['update-ref', 'refs/remotes/origin/main', gitOk(repo, ['rev-parse', 'HEAD'])]);
    setup(repo);
    return snapshotOf(repo);
  } finally {
    fs.rmSync(repo, { recursive: true, force: true });
  }
}
function addSheetCommit(repo, review, extra) {
  gitOk(repo, ['switch', '-c', 'feat/mcp-protocol']);
  writeRepoFile(repo, 'docs/NEXT-WORK.md', nextDoc('Done, waiting for review', 'To do'));
  writeRepoFile(repo, 'docs/BUILT-WITH-PLANWHARF.md', builtDoc(review));
  writeRepoFile(repo, sheetPath, 'sheet fixture\n');
  writeRepoFile(repo, 'engine/job18-sheet.test.cjs', 'test fixture\n');
  if (extra) extra(repo);
  commitAll(repo, 'write the sheet');
}
test('an isolated sheet branch on refreshed origin/main passes the same checks', () => {
  const snapshot = withSheetRepo(repo => addSheetCommit(repo, provenance));
  assert.deepEqual(sheetBranchErrors(snapshot), []);
  assert.equal(snapshot.branch, 'feat/mcp-protocol');
  assert.equal(snapshot.mergeBase, snapshot.originMain);
  assert.deepEqual(snapshot.changed.slice().sort(), [
    'docs/BUILT-WITH-PLANWHARF.md',
    'docs/NEXT-WORK.md',
    'engine/job18-sheet.test.cjs',
    sheetPath
  ].sort());
});
test('a detached sheet checkout still has to be based on refreshed origin/main', () => {
  const snapshot = withSheetRepo(repo => {
    addSheetCommit(repo, provenance);
    gitOk(repo, ['switch', '--detach', 'HEAD']);
  });
  assert.equal(snapshot.branch, '');
  assert.deepEqual(sheetBranchErrors(snapshot), []);
});
test('each isolated branch fault fails on its own', () => {
  const behind = withSheetRepo(repo => {
    addSheetCommit(repo, provenance);
    gitOk(repo, ['switch', 'main']);
    writeRepoFile(repo, 'README.md', 'main moved\n');
    commitAll(repo, 'move main');
    gitOk(repo, ['update-ref', 'refs/remotes/origin/main', gitOk(repo, ['rev-parse', 'HEAD'])]);
    gitOk(repo, ['switch', 'feat/mcp-protocol']);
  });
  assert.deepEqual(sheetBranchErrors(behind), ['branch is not based on refreshed origin/main']);
  const wrongName = withSheetRepo(repo => {
    gitOk(repo, ['switch', '-c', 'planwharf/from-refreshed-origin-main-create-feat-p']);
    writeRepoFile(repo, 'docs/NEXT-WORK.md', nextDoc('Done, waiting for review', 'To do'));
    writeRepoFile(repo, 'docs/BUILT-WITH-PLANWHARF.md', builtDoc(provenance));
    writeRepoFile(repo, sheetPath, 'sheet fixture\n');
    writeRepoFile(repo, 'engine/job18-sheet.test.cjs', 'test fixture\n');
    commitAll(repo, 'write the sheet on the wrong branch');
  });
  assert.ok(sheetBranchErrors(wrongName).includes('branch name is planwharf/from-refreshed-origin-main-create-feat-p'));
  const extra = withSheetRepo(repo => addSheetCommit(repo, provenance, checkout => {
    writeRepoFile(checkout, 'engine/job18-gate.cjs', 'gate\n');
  }));
  assert.deepEqual(sheetBranchErrors(extra), ['file outside the sheet branch: engine/job18-gate.cjs']);
  const noSheet = withSheetRepo(repo => {
    gitOk(repo, ['switch', '-c', 'feat/mcp-protocol']);
    writeRepoFile(repo, 'docs/NEXT-WORK.md', nextDoc('Done, waiting for review', 'To do'));
    writeRepoFile(repo, 'docs/BUILT-WITH-PLANWHARF.md', builtDoc(provenance));
    writeRepoFile(repo, 'engine/job18-sheet.test.cjs', 'test fixture\n');
    commitAll(repo, 'docs only');
  });
  assert.ok(sheetBranchErrors(noSheet).includes('sheet missing from diff'));
  const pending = withSheetRepo(repo => addSheetCommit(repo, 'Independent verification pending'));
  const pendingErrors = sheetBranchErrors(pending);
  assert.ok(pendingErrors.includes('independent-review provenance is missing or inaccurate'));
  assert.ok(pendingErrors.includes('independent-review provenance is still pending'));
  const whitespace = withSheetRepo(repo => addSheetCommit(repo, provenance, checkout => {
    writeRepoFile(checkout, 'docs/NEXT-WORK.md', nextDoc('Done, waiting for review', 'To do') + 'trailing \n');
  }));
  assert.ok(sheetBranchErrors(whitespace).includes('diff check is not clean'));
  const drifted = withSheetRepo(repo => {
    gitOk(repo, ['switch', '-c', 'feat/mcp-protocol']);
    writeRepoFile(repo, 'docs/NEXT-WORK.md', nextDoc('Done, waiting for review', 'Done, waiting for review'));
    writeRepoFile(repo, 'docs/BUILT-WITH-PLANWHARF.md', '**16 of 55**\n| 18 | #144 | Wrote the worker MCP protocol sheet | ' + provenance + ' |\n');
    writeRepoFile(repo, sheetPath, 'sheet fixture\n');
    writeRepoFile(repo, 'engine/job18-sheet.test.cjs', 'test fixture\n');
    commitAll(repo, 'drift the records');
  });
  const driftedErrors = sheetBranchErrors(drifted);
  assert.ok(driftedErrors.includes('job 19 is not still to do'));
  assert.ok(driftedErrors.includes('job 18 pull request is not #?'));
  assert.ok(driftedErrors.includes('job 18 is marked merged'));
  assert.ok(driftedErrors.includes('progress count drifted'));
  assert.ok(driftedErrors.includes('line count drifted'));
});
test('an empty diff behind origin/main is not a sheet-branch pass', () => {
  const snapshot = withSheetRepo(repo => {
    gitOk(repo, ['switch', '-c', 'planwharf/from-refreshed-origin-main-create-feat-p']);
    gitOk(repo, ['switch', 'main']);
    writeRepoFile(repo, 'README.md', 'origin moved on\n');
    commitAll(repo, 'advance origin/main');
    gitOk(repo, ['update-ref', 'refs/remotes/origin/main', gitOk(repo, ['rev-parse', 'HEAD'])]);
    gitOk(repo, ['switch', 'planwharf/from-refreshed-origin-main-create-feat-p']);
  });
  assert.deepEqual(snapshot.changed, []);
  assert.notEqual(snapshot.mergeBase, snapshot.originMain);
  assert.deepEqual(sheetBranchErrors(snapshot), [
    'diff is empty',
    'sheet missing from diff',
    'NEXT-WORK missing from diff',
    'BUILT-WITH missing from diff',
    'branch is not based on refreshed origin/main',
    'branch name is planwharf/from-refreshed-origin-main-create-feat-p',
    'job 18 status drifted',
    'independent-review provenance is missing or inaccurate'
  ]);
});

test('the set-aside gate files stay recoverable and out of this branch', () => {
  assert.equal(fs.existsSync(path.join(root, 'engine/job18-gate.cjs')), false);
  assert.equal(fs.existsSync(path.join(root, 'engine/job18-gate.test.cjs')), false);
  // The stash is local: it proves the gate files are recoverable on the machine that set them
  // aside. A check on GitHub has no stash, and only has to prove they are not in this branch.
  const stashes = git(['stash', 'list', '--format=%gd %s']).split('\n');
  const entry = stashes.find(line => line.includes('once-those-changes-are-safely-set-aside'));
  if (entry) {
    const ref = entry.split(' ')[0];
    assert.equal(git(['cat-file', '-e', ref + '^3:engine/job18-gate.cjs']), '');
    assert.equal(git(['cat-file', '-e', ref + '^3:engine/job18-gate.test.cjs']), '');
    const parked = git(['diff', '--name-only', ref + '^1', ref]);
    assert.match(parked, /docs\/NEXT-WORK\.md/);
  }
  const missing = spawnSync('git', ['cat-file', '-e', 'HEAD:engine/job18-gate.cjs'], { cwd: root, encoding: 'utf8' });
  assert.notEqual(missing.status, 0);
});
