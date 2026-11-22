'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/35-sandbox.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job35-sheet.test.cjs'
]);

const filesystemAndNetwork = [
  'Assigned worktree',
  'Read-Write',
  'User Home directory',
  'Deny all',
  'Credential and secret stores',
  'Other project directories and sibling worktrees',
  'Default policy',
  'Denied'
];

const platformsAndTools = [
  'Bubblewrap',
  'bwrap',
  'namespaces',
  'sandbox-exec',
  'seatbelt',
  'Job Objects',
  'AppContainer',
  'restricted token',
  'Codex',
  'Claude Code',
  'Grok',
  'Antigravity'
];

const escalationAndApprovals = [
  'explicit owner approval',
  'System package installation',
  'Unapproved network destinations',
  'desktop chat',
  'Phone integration',
  'fail-closed',
  'timeout'
];

const workflowAndCompatibility = [
  'dotnet build',
  'dotnet test',
  'git status',
  'sandbox.enabled',
  'Startup latency'
];

const escapeTests = [
  'Path traversal',
  'Symlink breakout',
  'Credential store protection',
  'Host process isolation',
  'Network egress containment',
  'Localhost port probe containment',
  'Process tree cleanup',
  'Escalation lifecycle'
];

const openDecisions = [
  'Linux fallback mechanism when `bwrap` is missing',
  'Windows AppContainer versus restricted token with low integrity level',
  'Outbound network filtering mechanism'
];

function read(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}

function git(args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  assert.equal(result.status, 0, (result.stderr || result.stdout || '').slice(0, 500));
  return (result.stdout || '').replace(/\r\n/g, '\n').trim();
}

function flat(text) {
  return text.replace(/\s+/g, ' ');
}

function assessSheet(sheet) {
  const errors = [];
  const text = flat(sheet);
  for (const phrase of filesystemAndNetwork) {
    if (!text.includes(phrase)) errors.push('filesystem/network missing "' + phrase + '"');
  }
  for (const phrase of platformsAndTools) {
    if (!text.includes(phrase)) errors.push('platform/tool missing "' + phrase + '"');
  }
  for (const phrase of escalationAndApprovals) {
    if (!text.includes(phrase)) errors.push('escalation missing "' + phrase + '"');
  }
  for (const phrase of workflowAndCompatibility) {
    if (!text.includes(phrase)) errors.push('workflow missing "' + phrase + '"');
  }
  for (const phrase of escapeTests) {
    if (!text.includes(phrase)) errors.push('escape test missing "' + phrase + '"');
  }
  for (const phrase of openDecisions) {
    if (!text.includes(phrase)) errors.push('open decision missing "' + phrase + '"');
  }
  if (/the sandbox is implemented/i.test(sheet)) errors.push('sheet marks sandbox implemented');
  if (!/Open design decisions/.test(sheet)) errors.push('open design decisions section removed');
  return errors;
}

function without(sheet, phrase) {
  const flattened = flat(sheet);
  assert.ok(flattened.includes(phrase), 'fixture phrase missing: ' + phrase);
  return flattened.split(phrase).join('');
}

test('job 35 sheet covers worker isolation, OS mechanisms, escalation, and escape tests', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping an isolation rule, platform, escalation, or escape test fails assessment', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['home deny', without(sheet, 'User Home directory')],
    ['credential block', without(sheet, 'Credential and secret stores')],
    ['linux bwrap', without(sheet, 'bwrap')],
    ['macos sandbox-exec', without(sheet, 'sandbox-exec')],
    ['windows job objects', without(sheet, 'Job Objects')],
    ['windows appcontainer', without(sheet, 'AppContainer')],
    ['claude code integration', without(sheet, 'Claude Code')],
    ['codex integration', without(sheet, 'Codex')],
    ['package install escalation', without(sheet, 'System package installation')],
    ['fail closed', without(sheet, 'fail-closed')],
    ['path traversal test', without(sheet, 'Path traversal')],
    ['symlink breakout test', without(sheet, 'Symlink breakout')],
    ['process cleanup test', without(sheet, 'Process tree cleanup')],
    ['open decision', without(sheet, 'Outbound network filtering mechanism')]
  ];
  for (const [name, broken] of cases) {
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, name + ' did not produce errors');
  }
});

test('documentation reflects job 35 status and built-with entries', () => {
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 35 \| Safety \| \*\*Sandbox sheet\*\*.*\| `docs\/sandbox-sheet` \| Done, waiting for review \|/);
  assert.match(next, /\| 36 \| Engine \| \*\*Step outcomes\*\*.*\| `feat\/step-outcomes` \| To do \|/);
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 35 \| #\? \|/);
  assert.match(built, /Sheet only: build is a later job/);
  assert.doesNotMatch(built, /\| 35 \| #\d+/);
});

test('the verdict stays on the sheet branch and conforms to plan', () => {
  const changed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean);
  if (!changed.length || !changed.includes(sheetPath) || changed.some(file => !allowedDiff.has(file))) return;
  const base = git(['rev-parse', 'origin/main']);
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']), base);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main...HEAD']), '');
  assert.equal(fs.existsSync(path.join(root, 'native/Planwharf.Engine/Sandbox.cs')), false);
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 35 \| Safety \| \*\*Sandbox sheet\*\*.*\| `docs\/sandbox-sheet` \| Done, waiting for review \|/);
  assert.match(next, /\| 36 \| Engine \| \*\*Step outcomes\*\*.*\| `feat\/step-outcomes` \| To do \|/);
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 35 \| #\? \|/);
  assert.match(built, /Sheet only: build is a later job/);
  assert.doesNotMatch(built, /\| 35 \| #\d+/);
  const branch = git(['branch', '--show-current']);
  if (branch) assert.equal(branch, 'docs/sandbox-sheet');
});
