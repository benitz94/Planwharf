'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

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

test('the branch is only the sheet and its required docs on refreshed origin/main', () => {
  const base = git(['rev-parse', 'origin/main']);
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']), base);
  // GitHub checks the merge commit out detached, so the branch name comes from the run there.
  const branch = git(['branch', '--show-current']) || process.env.GITHUB_HEAD_REF || process.env.GITHUB_REF_NAME || '';
  assert.equal(branch, 'feat/mcp-protocol');
  const changed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.ok(changed.includes('docs/NEXT-WORK.md'));
  assert.ok(changed.includes('docs/BUILT-WITH-PLANWHARF.md'));
  assert.equal(git(['diff', '--check', 'origin/main...HEAD']), '');
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 18 \|.*\| `feat\/mcp-protocol` \| Done, waiting for review \|/);
  for (const job of ['19', '20', '21', '22']) assert.match(next, new RegExp('\\| ' + job + ' \\|.*\\| To do \\|'));
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  const published = git(['show', 'origin/main:docs/BUILT-WITH-PLANWHARF.md']);
  assert.match(built, /\| 18 \| #\? \|/);
  assert.equal(built.includes('**15 of 55**'), published.includes('**15 of 55**'));
  assert.equal((built.match(/about 1,040 of 10,300/) || []).length, (published.match(/about 1,040 of 10,300/) || []).length);
  assert.doesNotMatch(built, /\| 18 \| #?\d+/);
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
