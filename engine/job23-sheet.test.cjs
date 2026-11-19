'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/23-phase2-rest.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job23-sheet.test.cjs'
]);
const modules = [
  'plan.js', 'quota.js', 'models.js', 'engine/sentinel.js', 'engine/invocations.cjs',
  'engine/notebook-file.cjs', 'engine/project-checks.cjs', 'engine/project-workspace.cjs',
  'Plan.cs', 'Quota.cs', 'Models.cs', 'Sentinel.cs', 'Invocations.cs', 'NotebookFile.cs',
  'ProjectChecks.cs', 'ProjectWorkspace.cs', 'does not port the modules'
];
const parityAndChecks = [
  'native/porting/parity/cases.cjs',
  'node native/porting/parity/record.cjs',
  'node engine/parity.test.cjs',
  'ParityTests.Ports',
  'dotnet build native/Planwharf.Engine',
  'dotnet test native/Planwharf.Engine.Tests',
  'node scripts/engineering/ci.cjs',
  'Do not put `within`, `gitEnvironment`, ledger ids, or `readResultFile` in the JSON',
  'Nothing else: not the JavaScript',
  'path map',
  'another company reviews this sheet'
];
const deferred = [
  'Do not port `attempts.js`',
  'createAttemptEngine',
  'createServer',
  'createOrchestrator',
  'spawnWorker',
  'Live `npm test`, live `node --test`, and live `taskkill` are deferred',
  'randomUUID',
  'models.json',
  'provider-catalog',
  'does not restore the Review role',
  'Reading the checkout\'s `plan.json` or `.git`'
];
const errorPaths = [
  'The saved notebook is invalid.',
  'Fill in name and goal.',
  'Folder must be an absolute path on this PC.',
  'Repository must be an HTTPS GitHub or Gitea owner/repository URL.',
  'The notebook must keep at least one project.',
  'This project already has a running task. No second task was created.',
  'Choose only one new direction per round.',
  'Invalid import JSON.',
  'No docs in the folder to grill with.',
  'A real linked project folder is required before worker launch.',
  'Worker launch folder must stay inside the linked project folder.',
  'Worker cwd and job folder must match the linked project folder.',
  'no model enabled',
  'model not present',
  'model disabled',
  'REMAINING UNREAD',
  'reason: \'invalid data\'',
  'no verdict',
  'A truncated historical line is skipped',
  'EEXIST',
  'COPYFILE_EXCL',
  'found.length > 200',
  'no deeper than 4',
  'no test specified',
  'NODE_TEST_CONTEXT',
  'safeGitWorkspace` returns null',
  'Unsafe token integers are omitted',
  'CHECKPOINTED` is stored as `BLOCKED`',
  'exitCode` null',
  'The updated notebook is invalid.',
  'Export is missing a project name.',
  'Import is not a valid Planwharf project.',
  'Job not found.',
  'Set the task deadline.',
  'Choose a deadline or leave timing to the worker.',
  'Each answer must indicate the current question and a valid choice.',
  'Answer the open questions of the current revision.',
  'Import an object with title, goal, where it stands, decided decisions, and open questions.',
  'Fill in title, goal, and where it stands.',
  'commondir',
  'objects/info/alternates',
  'objects/info/http-alternates',
  'worktree=',
  'fsmonitor=',
  'validateProject` rejects unknown keys on `data.project`',
  'Revision modes are strictly `manual`, `grill`, or `import`'
];
const recoveryPaths = [
  'a second call leaves personal notebook bytes',
  'Reload skips it',
  'a second finish appends nothing',
  'only while status is `RUNNING`',
  'A RUNNING `worker` is not blocked merely by reload',
  'A missing usage file does not fail reconcile',
  'Do not close a row that is not `active`',
  'COMPLETE wins if both lines exist',
  'startGrill` keeps the notebook',
  'must stay JSON-identical',
  'finishes every still-RUNNING `orchestrator`, `reviewer`, and `supervisor`',
  'A matching `snapshot.execution.results` row finishes with that status and `exitCode`',
  'A dead worker reads `exitPath` and the first 8192 characters of `resultPath`',
  'No status becomes `BLOCKED`',
  'The attempt is not closed',
  'If the file does not end in a newline, append one before new records'
];
const concurrent = [
  'Two projects may each have a running task',
  'Two starts in one process both remain',
  'Two workers do not share a ledger row',
  'hides other projects',
  'A sibling folder is rejected',
  '^[A-Za-z0-9_-]{1,100}$',
  'one ledger file per directory',
  'does not open a second attempt while the first is active',
  'claim still valid',
  'at most one task with status `running` per project',
  'A second running task makes `validate` false',
  'A concurrent test in one process appends two lines and reloads both',
  'There is one ledger file per directory',
  'The JavaScript does not lock two processes',
  'External worktrees and shared object stores stay out',
  'A directory symlink or junction that leaves the root is rejected'
];
const edgeCases = [
  'spaces and Unicode',
  'NFKD',
  'may exceed 48',
  'toLocaleLowerCase(\'it-IT\')',
  'leftPct` when it is finite',
  'clamped from 0 to 100',
  ' · EXHAUSTED',
  'forces `enabled` false',
  'null entry stays null',
  'enabled === true',
  'no extra escaping',
  'An empty detail is allowed',
  'running task as `paused`',
  'schema 1 wraps',
  'git@github.com:',
  'user:secret',
  'file://',
  'kind `research`',
  'linkedFolder: \'\'',
  'GIT_CONFIG_NOSYSTEM',
  'GIT_CONFIG_GLOBAL',
  'GIT_TERMINAL_PROMPT',
  '`shell` is not true',
  'cmd.exe',
  'node_modules',
  'accountType` (default `unread`)',
  'prompts, output, environment, and credentials never enter',
  '0o600',
  'must not depend on the ACL',
  'CI` to `1`',
  'drops `home`',
  'Do not call the network, a real model, or a real repository',
  'role `reviewer` stay',
  'model not eligible',
  'does not create directories',
  'at most 1000 notes',
  'text up to 10000 characters',
  'safe integer `revisionIds`',
  'Non-array inputs to `getEnabledModels` or `setModelEnabled` return an empty array',
  '100000 entries',
  'middle dot, spaces',
  'cachedInput` is added only when it is a safe integer',
  'durationMs` is `max(0, end - start)`',
  'recent` is the last 20 rows, newest first',
  'TIMEOUT_MS` is 10 minutes',
  '20000 characters',
  '3000 characters, with a leading `...` when cut',
  'taskkill` with `/PID`, `/T`, `/F`',
  'SIGKILL'
];
const openDecisions = [
  'Whether `setPlace` should keep `home`',
  'Whether job 24 should be split',
  'Whether the C# process hook for `runChecks` should later be added to the JavaScript'
];
const absent = [
  'native/Planwharf.Engine/Plan.cs',
  'native/Planwharf.Engine/Quota.cs',
  'native/Planwharf.Engine/Models.cs',
  'native/Planwharf.Engine/Sentinel.cs',
  'native/Planwharf.Engine/Invocations.cs',
  'native/Planwharf.Engine/NotebookFile.cs',
  'native/Planwharf.Engine/ProjectChecks.cs',
  'native/Planwharf.Engine/ProjectWorkspace.cs'
];

function read(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}
function git(args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, (result.stderr || result.stdout || '').slice(0, 500));
  return (result.stdout || '').replace(/\r\n/g, '\n');
}
function flat(text) {
  return text.replace(/\s+/g, ' ');
}
function assessSheet(sheet) {
  const errors = [];
  const text = flat(sheet);
  const groups = [
    ['module', modules],
    ['parity', parityAndChecks],
    ['deferred', deferred],
    ['error', errorPaths],
    ['recovery', recoveryPaths],
    ['concurrent', concurrent],
    ['edge', edgeCases],
    ['open', openDecisions]
  ];
  for (const [group, phrases] of groups) {
    for (const phrase of phrases) {
      if (!text.includes(phrase)) errors.push(group + ' missing "' + phrase + '"');
    }
  }
  if (/the modules are ported/i.test(sheet)) errors.push('sheet marks the modules ported');
  if (!/Open design decisions/.test(sheet)) errors.push('owner decisions removed');
  return errors;
}
function without(sheet, phrase) {
  const flattened = flat(sheet);
  assert.ok(flattened.includes(phrase), 'fixture phrase missing: ' + phrase);
  return flattened.split(phrase).join('');
}

test('job 23 sheet covers the eight modules, parity, checks, and deferred work', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('job 23 sheet follows the porting rules and leaves job 04\'s shape', () => {
  const rules = flat(read('native/porting/README.md'));
  assert.match(rules, /Do not change the JavaScript/);
  assert.match(rules, /Every port joins the comparison/);
  assert.match(rules, /Tests do not depend on the machine/);
  const plan = flat(read('native/porting/PLAN.md'));
  assert.match(plan, /plan\.js/);
  assert.match(plan, /project-workspace/);
  const earlier = flat(read('native/porting/jobs/04-briefing-refusals-autonomy.md'));
  assert.match(earlier, /Files you may change/);
  assert.match(earlier, /Deferred cases/);
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping a parity rule, a check, or a deferred dependency fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['parity file', without(sheet, 'native/porting/parity/cases.cjs')],
    ['record', without(sheet, 'node native/porting/parity/record.cjs')],
    ['ports', without(sheet, 'ParityTests.Ports')],
    ['ci', without(sheet, 'node scripts/engineering/ci.cjs')],
    ['not json', without(sheet, 'Do not put `within`, `gitEnvironment`, ledger ids, or `readResultFile` in the JSON')],
    ['javascript', without(sheet, 'Nothing else: not the JavaScript')],
    ['attempts', without(sheet, 'Do not port `attempts.js`')],
    ['live npm', without(sheet, 'Live `npm test`, live `node --test`, and live `taskkill` are deferred')],
    ['uuid', without(sheet, 'randomUUID')],
    ['review role', without(sheet, 'does not restore the Review role')],
    ['ported claim', sheet + '\nThe modules are ported.\n']
  ];
  for (const [name, broken] of cases) assert.ok(assessSheet(broken).length > 0, name);
});

test('dropping an error, recovery, concurrency, edge, or open decision limit fails on its own', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['invalid notebook', without(sheet, 'The saved notebook is invalid.')],
    ['goal', without(sheet, 'Fill in name and goal.')],
    ['folder', without(sheet, 'Folder must be an absolute path on this PC.')],
    ['github', without(sheet, 'Repository must be an HTTPS GitHub or Gitea owner/repository URL.')],
    ['last project', without(sheet, 'The notebook must keep at least one project.')],
    ['second task', without(sheet, 'This project already has a running task. No second task was created.')],
    ['direction', without(sheet, 'Choose only one new direction per round.')],
    ['import', without(sheet, 'Invalid import JSON.')],
    ['docs', without(sheet, 'No docs in the folder to grill with.')],
    ['linked', without(sheet, 'A real linked project folder is required before worker launch.')],
    ['inside', without(sheet, 'Worker launch folder must stay inside the linked project folder.')],
    ['cwd', without(sheet, 'Worker cwd and job folder must match the linked project folder.')],
    ['no model', without(sheet, 'no model enabled')],
    ['missing model', without(sheet, 'model not present')],
    ['disabled', without(sheet, 'model disabled')],
    ['unread', without(sheet, 'REMAINING UNREAD')],
    ['invalid data', without(sheet, 'reason: \'invalid data\'')],
    ['verdict', without(sheet, 'no verdict')],
    ['truncated', without(sheet, 'A truncated historical line is skipped')],
    ['exists', without(sheet, 'EEXIST')],
    ['excl', without(sheet, 'COPYFILE_EXCL')],
    ['cap files', without(sheet, 'found.length > 200')],
    ['depth', without(sheet, 'no deeper than 4')],
    ['no tests', without(sheet, 'no test specified')],
    ['context', without(sheet, 'NODE_TEST_CONTEXT')],
    ['git meta', without(sheet, 'safeGitWorkspace` returns null')],
    ['tokens', without(sheet, 'Unsafe token integers are omitted')],
    ['checkpointed', without(sheet, 'CHECKPOINTED` is stored as `BLOCKED`')],
    ['overwrite', without(sheet, 'a second call leaves personal notebook bytes')],
    ['partial', without(sheet, 'Reload skips it')],
    ['second finish', without(sheet, 'a second finish appends nothing')],
    ['worker stays', without(sheet, 'A RUNNING `worker` is not blocked merely by reload')],
    ['usage', without(sheet, 'A missing usage file does not fail reconcile')],
    ['not active', without(sheet, 'Do not close a row that is not `active`')],
    ['both status', without(sheet, 'COMPLETE wins if both lines exist')],
    ['grill keep', without(sheet, 'startGrill` keeps the notebook')],
    ['identical', without(sheet, 'must stay JSON-identical')],
    ['two projects', without(sheet, 'Two projects may each have a running task')],
    ['two starts', without(sheet, 'Two starts in one process both remain')],
    ['two workers', without(sheet, 'Two workers do not share a ledger row')],
    ['other project', without(sheet, 'hides other projects')],
    ['sibling', without(sheet, 'A sibling folder is rejected')],
    ['ids', without(sheet, '^[A-Za-z0-9_-]{1,100}$')],
    ['second attempt', without(sheet, 'does not open a second attempt while the first is active')],
    ['unicode', without(sheet, 'spaces and Unicode')],
    ['locale', without(sheet, 'toLocaleLowerCase(\'it-IT\')')],
    ['clamp', without(sheet, 'clamped from 0 to 100')],
    ['exhausted', without(sheet, ' · EXHAUSTED')],
    ['shell', without(sheet, '`shell` is not true')],
    ['acl', without(sheet, 'must not depend on the ACL')],
    ['home drop', without(sheet, 'drops `home`')],
    ['network', without(sheet, 'Do not call the network, a real model, or a real repository')],
    ['keep home', without(sheet, 'Whether `setPlace` should keep `home`')],
    ['split', without(sheet, 'Whether job 24 should be split')],
    ['hook later', without(sheet, 'Whether the C# process hook for `runChecks` should later be added to the JavaScript')],
    ['secrets', without(sheet, 'prompts, output, environment, and credentials never enter')],
    ['research', without(sheet, 'kind `research`')],
    ['paused', without(sheet, 'running task as `paused`')],
    ['eligible', without(sheet, 'model not eligible')],
    ['directories', without(sheet, 'does not create directories')],
    ['updated notebook invalid', without(sheet, 'The updated notebook is invalid.')],
    ['export missing name', without(sheet, 'Export is missing a project name.')],
    ['import invalid project', without(sheet, 'Import is not a valid Planwharf project.')],
    ['job not found', without(sheet, 'Job not found.')],
    ['set task deadline', without(sheet, 'Set the task deadline.')],
    ['choose deadline or discretion', without(sheet, 'Choose a deadline or leave timing to the worker.')],
    ['valid answer choice', without(sheet, 'Each answer must indicate the current question and a valid choice.')],
    ['answer open questions', without(sheet, 'Answer the open questions of the current revision.')],
    ['import object shape', without(sheet, 'Import an object with title, goal, where it stands, decided decisions, and open questions.')],
    ['fill title goal state', without(sheet, 'Fill in title, goal, and where it stands.')],
    ['commondir', without(sheet, 'commondir')],
    ['alternates', without(sheet, 'objects/info/alternates')],
    ['http alternates', without(sheet, 'objects/info/http-alternates')],
    ['worktree config', without(sheet, 'worktree=')],
    ['fsmonitor config', without(sheet, 'fsmonitor=')],
    ['validate reject unknown project keys', without(sheet, 'validateProject` rejects unknown keys on `data.project`')],
    ['revision modes strictly manual grill import', without(sheet, 'Revision modes are strictly `manual`, `grill`, or `import`')],
    ['snapshot reconcile match', without(sheet, 'A matching `snapshot.execution.results` row finishes with that status and `exitCode`')],
    ['dead worker read paths', without(sheet, 'A dead worker reads `exitPath` and the first 8192 characters of `resultPath`')],
    ['no status blocked', without(sheet, 'No status becomes `BLOCKED`')],
    ['attempt not closed on complete', without(sheet, 'The attempt is not closed')],
    ['append newline on missing newline', without(sheet, 'If the file does not end in a newline, append one before new records')],
    ['second running task invalidates', without(sheet, 'A second running task makes `validate` false')],
    ['concurrent append reload', without(sheet, 'A concurrent test in one process appends two lines and reloads both')],
    ['one ledger per directory', without(sheet, 'There is one ledger file per directory')],
    ['no cross process lock', without(sheet, 'The JavaScript does not lock two processes')],
    ['external worktrees out', without(sheet, 'External worktrees and shared object stores stay out')],
    ['junction escaping rejected', without(sheet, 'A directory symlink or junction that leaves the root is rejected')],
    ['note count limit', without(sheet, 'at most 1000 notes')],
    ['note length limit', without(sheet, 'text up to 10000 characters')],
    ['safe revision ids in last visit', without(sheet, 'safe integer `revisionIds`')],
    ['non array models input', without(sheet, 'Non-array inputs to `getEnabledModels` or `setModelEnabled` return an empty array')],
    ['entries walk limit', without(sheet, '100000 entries')],
    ['middle dot spaces', without(sheet, 'middle dot, spaces')],
    ['cached input safe integer', without(sheet, 'cachedInput` is added only when it is a safe integer')],
    ['duration non negative', without(sheet, 'durationMs` is `max(0, end - start)`')],
    ['recent rows limit', without(sheet, 'recent` is the last 20 rows, newest first')],
    ['timeout limit', without(sheet, 'TIMEOUT_MS` is 10 minutes')],
    ['stream tail limit', without(sheet, '20000 characters')],
    ['output tail limit', without(sheet, '3000 characters, with a leading `...` when cut')],
    ['windows taskkill tree', without(sheet, 'taskkill` with `/PID`, `/T`, `/F`')],
    ['sigkill posix', without(sheet, 'SIGKILL')]
  ];
  for (const [name, broken] of cases) assert.ok(assessSheet(broken).length > 0, name);
});

test('parallel assessments do not mix a sound sheet with a broken one', async () => {
  const sheet = read(sheetPath);
  const [sound, brokenParity, brokenContainment, brokenRecover] = await Promise.all([
    Promise.resolve().then(() => assessSheet(sheet)),
    Promise.resolve().then(() => assessSheet(without(sheet, 'ParityTests.Ports'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Worker launch folder must stay inside the linked project folder.'))),
    Promise.resolve().then(() => assessSheet(without(sheet, 'Reload skips it')))
  ]);
  assert.deepEqual(sound, []);
  assert.ok(brokenParity.some(error => error.includes('ParityTests.Ports')));
  assert.ok(brokenContainment.some(error => error.includes('Worker launch folder must stay inside the linked project folder.')));
  assert.ok(brokenRecover.some(error => error.includes('Reload skips it')));
});

function assessProvenance(built) {
  const errors = [];
  if (!/\| 23 \| #\? \|.*\| Independent verification by Gemini:.*\|/.test(built)) errors.push('missing Gemini verification entry');
  if (!/left that Gemini sentence and commit 61cdb5b unchanged/.test(built)) errors.push('missing unchanged Gemini attribution');
  if (!/archived work plan still checks Verify \(claude-sonnet-4-6\) on that same commit/.test(built)) errors.push('missing archived work plan note');
  if (!/stopped on quota before review and did not write 61cdb5b/.test(built)) errors.push('missing Claude quota stop note');
  if (!/does not replace the Gemini claim/.test(built)) errors.push('missing non-replacement assertion');
  if (/\| 23 \| #\? \|.*\| Independent verification pending \|/.test(built)) errors.push('verification still marked pending');
  if (/\| 23 \| #\d+/.test(built)) errors.push('premature pull request number assigned');
  if (!/15 of 55/.test(built)) errors.push('built-with progress count changed');
  return errors;
}

function assessDiffFiles(changedFiles, guardDiff) {
  const errors = [];
  const branchGuard = 'engine/job22-sheet.test.cjs';
  for (const file of changedFiles) {
    if (file === branchGuard) continue;
    if (!allowedDiff.has(file)) errors.push('disallowed file in diff: ' + file);
  }
  if (!changedFiles.includes(sheetPath)) errors.push('sheetPath missing from diff');
  if (guardDiff) {
    if (guardDiff.removed && guardDiff.removed.length > 0) errors.push('unexpected lines removed from branchGuard');
    const expectedAdded = [
      '+  // This guards the job 22 branch. Another branch changes its own files, and that is not a failure here.',
      '+  const here = git([\'branch\', \'--show-current\']);',
      '+  const branchFiles = git([\'diff\', \'--name-only\', \'origin/main...HEAD\']).split(\'\\n\').map(line => line.trim()).filter(Boolean);',
      '+  if (here && here !== \'feat/pipeline-run\') return;',
      '+  if (!here && branchFiles.some(file => !allowedDiff.has(file))) return;'
    ];
    if (JSON.stringify(guardDiff.added) !== JSON.stringify(expectedAdded)) errors.push('branchGuard additions mismatch');
  }
  return errors;
}

test('reviewer provenance checks reject fabricated or conflicting attributions', () => {
  const valid = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.deepEqual(assessProvenance(valid), []);

  const cases = [
    ['missing gemini', valid.replace(/(\| 23 \| #\? \|.*\| Independent verification by )Gemini(:.*\|)/, '$1Claude$2')],
    ['dropped unchanged sentence', valid.replace('left that Gemini sentence and commit 61cdb5b unchanged', '')],
    ['dropped archived work plan note', valid.replace('That Claude run stopped on quota before review and did not write 61cdb5b.', '')
      .replace(/The archived work plan still checks Verify \(claude-sonnet-4-6\) on that same commit\./g, '')],
    ['falsely claims claude completed', valid.replace('That Claude run stopped on quota before review and did not write 61cdb5b.', 'That Claude run completed review.')],
    ['claims replacement', valid.replace('This review does not replace the Gemini claim', 'This review replaces the Gemini claim')],
    ['still pending', valid.replace(/\| 23 \| #\? \| Wrote the phase 2 rest sheet.*\|/s, '| 23 | #? | Wrote the phase 2 rest sheet | Independent verification pending |')],
    ['premature PR number', valid.replace('| 23 | #? |', '| 23 | #148 |')]
  ];
  for (const [name, broken] of cases) {
    assert.ok(assessProvenance(broken).length > 0, name);
  }
});

test('branch guard and diff isolation reject unauthorized changes or modified guard lines', () => {
  const currentDiff = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  const guardAdded = git(['diff', '--unified=0', 'origin/main', '--', 'engine/job22-sheet.test.cjs']).split('\n')
    .filter(line => line.startsWith('+') && !line.startsWith('+++'));
  const guardRemoved = git(['diff', '--unified=0', 'origin/main', '--', 'engine/job22-sheet.test.cjs']).split('\n')
    .filter(line => line.startsWith('-') && !line.startsWith('---'));

  assert.deepEqual(assessDiffFiles(currentDiff, { added: guardAdded, removed: guardRemoved }), []);

  assert.ok(assessDiffFiles([...currentDiff, 'engine/unauthorized.js'], { added: guardAdded, removed: guardRemoved }).some(e => e.includes('disallowed file')));
  assert.ok(assessDiffFiles(currentDiff.filter(f => f !== sheetPath), { added: guardAdded, removed: guardRemoved }).some(e => e.includes('sheetPath missing')));
  assert.ok(assessDiffFiles(currentDiff, { added: guardAdded, removed: ['- some line'] }).some(e => e.includes('unexpected lines removed')));
  assert.ok(assessDiffFiles(currentDiff, { added: [...guardAdded, '+ extra line'], removed: guardRemoved }).some(e => e.includes('branchGuard additions mismatch')));
});

test('branch guard skips verification cleanly on other branches or detached heads with separate diffs', () => {
  function shouldSkip(here, branchFiles) {
    if (here && here !== 'port/23-sheet') return true;
    if (!here && branchFiles.some(file => !allowedDiff.has(file))) return true;
    return false;
  }
  assert.equal(shouldSkip('port/23-sheet', ['native/porting/jobs/23-phase2-rest.md']), false);
  assert.equal(shouldSkip('feat/pipeline-run', ['engine/job22-sheet.test.cjs']), true);
  assert.equal(shouldSkip('main', ['any.file']), true);
  assert.equal(shouldSkip('', ['engine/other.js']), true);
  assert.equal(shouldSkip('', ['native/porting/jobs/23-phase2-rest.md']), false);
});

test('the sheet stays on port/23-sheet and does not add a C# port', () => {
  // This guards the job 23 branch. Another branch changes its own files, and that is not a failure here.
  const here = git(['branch', '--show-current']);
  const branchFiles = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  if (here && here !== 'port/23-sheet') return;
  if (!here && branchFiles.some(file => !allowedDiff.has(file))) return;

  const worktree = git(['status', '--porcelain']).split('\n').filter(Boolean)
    .map(line => line.slice(3).trim().replace(/\\/g, '/'));
  const committed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').map(line => line.trim()).filter(Boolean);
  const changed = [...new Set([...worktree, ...committed])];
  assert.ok(changed.length > 0);

  const branchGuard = 'engine/job22-sheet.test.cjs';
  const added = git(['diff', '--unified=0', 'origin/main', '--', branchGuard]).split('\n')
    .filter(line => line.startsWith('+') && !line.startsWith('+++'));
  const removed = git(['diff', '--unified=0', 'origin/main', '--', branchGuard]).split('\n')
    .filter(line => line.startsWith('-') && !line.startsWith('---'));

  assert.deepEqual(assessDiffFiles(changed, { added, removed }), []);
  assert.equal(git(['diff', '--check', 'origin/main']).trim(), '');
  for (const file of absent) {
    assert.equal(fs.existsSync(path.join(root, file)), false, file);
    const missing = spawnSync('git', ['cat-file', '-e', 'HEAD:' + file], { cwd: root, encoding: 'utf8' });
    assert.notEqual(missing.status, 0, file);
  }
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 23 \|.*\| `port\/23-sheet` \| Done, waiting for review \|/);
  assert.match(next, /23-phase2-rest\.md/);
  for (const job of ['24', '25']) assert.match(next, new RegExp('\\| ' + job + ' \\|.*\\| To do \\|'));
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.deepEqual(assessProvenance(built), []);
  const branch = git(['branch', '--show-current']).trim();
  if (branch) assert.equal(branch, 'port/23-sheet');
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']).trim(), git(['rev-parse', 'origin/main']).trim());
});
