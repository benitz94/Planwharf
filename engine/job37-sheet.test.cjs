'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/37-phase3-first-level.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job37-sheet.test.cjs'
]);

const modules = [
  'engine/attempts.js', 'engine/claims.js', 'engine/project-export.cjs', 'engine/execution-next.cjs',
  'engine/provider-catalog.cjs', 'engine/provider-policy.cjs', 'engine/model-routing.cjs', 'engine/claude.cjs',
  'engine/claude-usage.cjs', 'engine/repository.cjs', 'engine/supervisor.cjs', 'engine/activity.cjs',
  'engine/activity-sessions.cjs', 'engine/provider-runtime.cjs', 'engine/time-left.cjs', 'engine/plan-status.cjs',
  'engine/provider-status.cjs', 'engine/push-email.cjs', 'engine/temp-cleanup.cjs', 'engine/account-folders.cjs',
  'engine/phone-voice.cjs', 'engine/phone-activation.cjs', 'engine/step-outcomes.cjs',
  'Attempts.cs', 'Claims.cs', 'ProjectExport.cs', 'ExecutionNext.cs',
  'ProviderCatalog.cs', 'ProviderPolicy.cs', 'ModelRouting.cs', 'Claude.cs',
  'ClaudeUsage.cs', 'Repository.cs', 'Supervisor.cs', 'Activity.cs',
  'ActivitySessions.cs', 'ProviderRuntime.cs', 'TimeLeft.cs', 'PlanStatus.cs',
  'ProviderStatus.cs', 'PushEmail.cs', 'TempCleanup.cs', 'AccountFolders.cs',
  'PhoneVoice.cs', 'PhoneActivation.cs', 'StepOutcomes.cs'
];

const parityAndChecks = [
  'native/porting/parity/cases.cjs',
  'node native/porting/parity/record.cjs',
  'node engine/parity.test.cjs',
  'dotnet build native/Planwharf.Engine -c Release',
  'dotnet test native/Planwharf.Engine.Tests -c Release',
  'ParityTests.Ports',
  'PublishAot=true',
  'TreatWarningsAsErrors',
  'namespace `Planwharf.Engine`',
  'namespace `Planwharf.Engine.Tests`',
  'The JavaScript stays the specification'
];

const errorPaths = [
  'attempt store locked',
  'model not eligible',
  'task already assigned',
  'project already assigned',
  'Invalid provider policy fields',
  'Fallback must be an explicit provider ID or null',
  'CLI model ID exceeds the 512-byte configuration limit',
  'Claude Code returned no model catalog',
  'Claude Code authentication unavailable',
  'GH007',
  'git rebase --abort',
  'RemoveTempDir_OutsideTempDirectory_ThrowsSecurityException',
  'This answer was interrupted by a backend restart',
  'LaunchSpec_UnapprovedAntigravityOrClaude_ThrowsPermissionNeeded'
];

const recoveryPaths = [
  'EmptyOrCorruptStore_RecoversToEmptyStoreWithNextId1',
  'Dead-PID Reclaim',
  'AtomicRename.RenameWithRetry',
  'CreateClaudeUsageReader_RateLimited429_AppliesRetryAfterHeader',
  'UseNoreplyAsync_RebaseFails_AbortsCleanlyAndReportsFailure',
  'Retries on `IOException` (Win32 sharing violation',
  'ReadStepOutcomes_CorruptedLines_SkipsInvalidAndLoadsValid',
  'Supervisor_RestartRecovery_MarksInterruptedRunningTurnsAsFailed',
  'MergePlans_PreservesExpiredOrNotSignedInAcrossRefreshes'
];

const concurrencyPaths = [
  'attempts.lock',
  'FileMode.CreateNew',
  '5000ms with 20ms spin-wait',
  'unique temporary file',
  'FileShare.ReadWrite',
  'SharesPendingRequestAndCachesResult',
  'refs/planwharf/inspection/',
  'FileShare.None'
];

const edgeCases = [
  'Sanitize_RedactsBearerTokensAndApiKeys',
  'Sanitize_StripsForbiddenKeys',
  'Encoding.UTF8.GetByteCount(cli) > 512',
  '[\\x00-\\x1f\\x7f]',
  'ProcessStartInfo.ArgumentList',
  'WindowsApps',
  'Telegram HTML tag balancing',
  'StripPathsAndCode',
  'SESSION_LIMIT = 24',
  'Path.GetTempPath()'
];

const openDecisions = [
  'Supervisor auto-review execution',
  'Phone voice synthesis API integration',
  'ClaudeUsage OAuth token refresh mechanism',
  'Attempts store partitioning'
];

const absent = [
  'native/Planwharf.Engine/Attempts.cs',
  'native/Planwharf.Engine/Claims.cs',
  'native/Planwharf.Engine/ProjectExport.cs',
  'native/Planwharf.Engine/ExecutionNext.cs',
  'native/Planwharf.Engine/ProviderCatalog.cs',
  'native/Planwharf.Engine/ProviderPolicy.cs',
  'native/Planwharf.Engine/ModelRouting.cs',
  'native/Planwharf.Engine/Claude.cs',
  'native/Planwharf.Engine/ClaudeUsage.cs',
  'native/Planwharf.Engine/Repository.cs',
  'native/Planwharf.Engine/Supervisor.cs',
  'native/Planwharf.Engine/Activity.cs',
  'native/Planwharf.Engine/ActivitySessions.cs',
  'native/Planwharf.Engine/ProviderRuntime.cs',
  'native/Planwharf.Engine/TimeLeft.cs',
  'native/Planwharf.Engine/PlanStatus.cs',
  'native/Planwharf.Engine/ProviderStatus.cs',
  'native/Planwharf.Engine/PushEmail.cs',
  'native/Planwharf.Engine/TempCleanup.cs',
  'native/Planwharf.Engine/AccountFolders.cs',
  'native/Planwharf.Engine/PhoneVoice.cs',
  'native/Planwharf.Engine/PhoneActivation.cs',
  'native/Planwharf.Engine/StepOutcomes.cs'
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
  return (text || '').replace(/\s+/g, ' ');
}

function assessSheet(sheet) {
  const errors = [];
  const text = flat(sheet);
  const groups = [
    ['module', modules],
    ['parity', parityAndChecks],
    ['error', errorPaths],
    ['recovery', recoveryPaths],
    ['concurrency', concurrencyPaths],
    ['edge', edgeCases],
    ['open', openDecisions]
  ];
  for (const [group, phrases] of groups) {
    for (const phrase of phrases) {
      if (!text.includes(phrase)) errors.push(group + ' missing "' + phrase + '"');
    }
  }
  if (/the 23 modules are ported/i.test(sheet)) errors.push('sheet prematurely claims modules are ported');
  if (!/Open design decisions/i.test(sheet)) errors.push('open design decisions section missing');
  return errors;
}

function without(sheet, phrase) {
  const flattened = flat(sheet);
  assert.ok(flattened.includes(phrase), 'fixture phrase missing: ' + phrase);
  return flattened.split(phrase).join('');
}

test('job 37 sheet covers all 23 modules, mappings, parity, checks, and CI commands', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping any required module or target class fails assessment', () => {
  const sheet = read(sheetPath);
  const sampleModules = [
    ['attempts js', 'engine/attempts.js'],
    ['claims js', 'engine/claims.js'],
    ['project-export', 'engine/project-export.cjs'],
    ['execution-next', 'engine/execution-next.cjs'],
    ['provider-catalog', 'engine/provider-catalog.cjs'],
    ['supervisor', 'engine/supervisor.cjs'],
    ['activity', 'engine/activity.cjs'],
    ['time-left', 'engine/time-left.cjs'],
    ['step-outcomes', 'engine/step-outcomes.cjs'],
    ['phone-voice', 'engine/phone-voice.cjs'],
    ['Attempts cs', 'Attempts.cs'],
    ['Supervisor cs', 'Supervisor.cs'],
    ['StepOutcomes cs', 'StepOutcomes.cs']
  ];
  for (const [name, target] of sampleModules) {
    const broken = without(sheet, target);
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, name + ' did not produce errors');
  }
});

test('dropping a parity rule, check, or CI command fails assessment', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['parity cases file', without(sheet, 'native/porting/parity/cases.cjs')],
    ['parity record script', without(sheet, 'node native/porting/parity/record.cjs')],
    ['engine parity test', without(sheet, 'node engine/parity.test.cjs')],
    ['dotnet build engine', without(sheet, 'dotnet build native/Planwharf.Engine -c Release')],
    ['dotnet test engine', without(sheet, 'dotnet test native/Planwharf.Engine.Tests -c Release')],
    ['parity ports wiring', without(sheet, 'ParityTests.Ports')],
    ['publish aot flag', without(sheet, 'PublishAot=true')],
    ['warnings as errors', without(sheet, 'TreatWarningsAsErrors')],
    ['js specification invariant', without(sheet, 'The JavaScript stays the specification')]
  ];
  for (const [name, broken] of cases) {
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, name + ' did not produce errors');
  }
});

test('dropping an error, recovery, concurrency, or edge case limit fails assessment', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['attempt store locked error', without(sheet, 'attempt store locked')],
    ['model not eligible error', without(sheet, 'model not eligible')],
    ['task already assigned error', without(sheet, 'task already assigned')],
    ['invalid provider policy error', without(sheet, 'Invalid provider policy fields')],
    ['cli model id length error', without(sheet, 'CLI model ID exceeds the 512-byte configuration limit')],
    ['claude code missing catalog error', without(sheet, 'Claude Code returned no model catalog')],
    ['gh007 private email error', without(sheet, 'GH007')],
    ['rebase abort error recovery', without(sheet, 'git rebase --abort')],
    ['temp dir outside temp security error', without(sheet, 'RemoveTempDir_OutsideTempDirectory_ThrowsSecurityException')],
    ['restart recovery interrupted message', without(sheet, 'This answer was interrupted by a backend restart')],
    ['empty store recovery', without(sheet, 'EmptyOrCorruptStore_RecoversToEmptyStoreWithNextId1')],
    ['dead pid reclaim recovery', without(sheet, 'Dead-PID Reclaim')],
    ['atomic rename retry recovery', without(sheet, 'AtomicRename.RenameWithRetry')],
    ['attempts lock file concurrency', without(sheet, 'attempts.lock')],
    ['create new file mode concurrency', without(sheet, 'FileMode.CreateNew')],
    ['lock timeout spin wait concurrency', without(sheet, '5000ms with 20ms spin-wait')],
    ['step outcomes readwrite share concurrency', without(sheet, 'FileShare.ReadWrite')],
    ['git inspection isolated namespace concurrency', without(sheet, 'refs/planwharf/inspection/')],
    ['sanitize bearer tokens edge case', without(sheet, 'Sanitize_RedactsBearerTokensAndApiKeys')],
    ['sanitize strip forbidden keys edge case', without(sheet, 'Sanitize_StripsForbiddenKeys')],
    ['cli model byte count limit edge case', without(sheet, 'Encoding.UTF8.GetByteCount(cli) > 512')],
    ['argument list hygiene edge case', without(sheet, 'ProcessStartInfo.ArgumentList')],
    ['telegram html tag balancing edge case', without(sheet, 'Telegram HTML tag balancing')],
    ['strip paths and code edge case', without(sheet, 'StripPathsAndCode')],
    ['temp path boundary edge case', without(sheet, 'Path.GetTempPath()')]
  ];
  for (const [name, broken] of cases) {
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, name + ' did not produce errors');
  }
});

test('dropping an open design decision fails assessment', () => {
  const sheet = read(sheetPath);
  for (const decision of openDecisions) {
    const broken = without(sheet, decision);
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, 'decision "' + decision + '" did not produce errors');
  }
});

test('assessSheet rejects empty text, malformed structures, and premature implementation claims', () => {
  assert.ok(assessSheet('').length > 0);
  assert.ok(assessSheet(null).length > 0);
  assert.ok(assessSheet('The 23 modules are ported in full.').length > 0);
  assert.ok(assessSheet('# Specification\nNo decisions here.').length > 0);
});

test('documentation names job 37 as the phase 3 sheet and points to its port, whatever the status now', () => {
  // The plan and the progress page move on after the review: only the job's place and its port are checked.
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 37 \| 3\. First level \| Write the sheet:.*\| `port\/37-sheet` \| [^|]+ \|/);
  assert.match(next, /\| \d+ \| 3\. \| Port them, as sheet 37 says \| `port\/\d+-phase3` \| [^|]+ \|/);
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /\| 37 \| (?:#\?|#\d+) \|/);
  assert.match(built, /Sheet only: build is Job \d+/);
});

test('the sheet stays on port/37-sheet and does not add C# port implementations', () => {
  // Only the sheet's own branch is held to this: the port (job 40) adds these files on purpose.
  const changed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean);
  if (!changed.length || !changed.includes(sheetPath) || changed.some(file => !allowedDiff.has(file))) return;
  for (const file of absent) {
    assert.equal(fs.existsSync(path.join(root, file)), false, file + ' should not exist in Job 37');
  }
  const base = git(['rev-parse', 'origin/main']);
  assert.equal(git(['merge-base', 'HEAD', 'origin/main']), base);
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main...HEAD']), '');
  const branch = git(['branch', '--show-current']);
  if (branch) assert.equal(branch, 'port/37-sheet');
});
