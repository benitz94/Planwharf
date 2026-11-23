'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const root = path.join(__dirname, '..');
const sheetPath = 'native/porting/jobs/42-phase4-processes-providers.md';
const allowedDiff = new Set([
  sheetPath,
  'docs/NEXT-WORK.md',
  'docs/BUILT-WITH-PLANWHARF.md',
  'engine/job42-sheet.test.cjs',
  '.planwharf/plan.md'
]);

const modules = [
  'engine/runner.cjs', 'engine/provider-models.cjs', 'engine/external-adapters.cjs', 'engine/api-agent.cjs',
  'engine/provider-adapters.cjs', 'engine/provider-connections.cjs', 'engine/antigravity.cjs', 'engine/planUsage.js',
  'engine/worktrees.cjs', 'engine/plan-file.cjs', 'engine/pr-check.cjs', 'engine/phone.cjs',
  'engine/phone-ollama.cjs', 'engine/phone-handover.cjs', 'engine/phone-handover-live.cjs', 'engine/phone-activation-check.cjs',
  'Runner.cs', 'ProviderModels.cs', 'ExternalAdapters.cs', 'ApiAgent.cs',
  'ProviderAdapters.cs', 'ProviderConnections.cs', 'Antigravity.cs', 'PlanUsage.cs',
  'Worktrees.cs', 'PlanFile.cs', 'PrCheck.cs', 'Phone.cs',
  'PhoneOllama.cs', 'PhoneHandover.cs', 'PhoneHandoverLive.cs', 'PhoneActivationCheck.cs',
  'RunnerTests.cs', 'ProviderModelsTests.cs', 'ExternalAdaptersTests.cs', 'ApiAgentTests.cs',
  'ProviderAdaptersTests.cs', 'ProviderConnectionsTests.cs', 'AntigravityTests.cs', 'PlanUsageTests.cs',
  'WorktreesTests.cs', 'PlanFileTests.cs', 'PrCheckTests.cs', 'PhoneTests.cs',
  'PhoneOllamaTests.cs', 'PhoneHandoverTests.cs', 'PhoneHandoverLiveTests.cs', 'PhoneActivationCheckTests.cs'
];

const parityAndChecks = [
  'native/porting/parity/cases.cjs',
  'native/porting/parity/required.json',
  'node native/porting/parity/record.cjs',
  'node native/porting/parity/record.cjs --check',
  'node --test engine/parity.test.cjs',
  'node scripts/engineering/ci.cjs',
  'dotnet build native/Planwharf.Engine -c Release',
  'dotnet test native/Planwharf.Engine.Tests -c Release',
  'dotnet build native/Planwharf.FakeCli -c Release',
  'dotnet test native/Planwharf.FakeCli.Tests -c Release',
  'ParityTests.Ports',
  'JavaScript is the behavioral specification'
];

const errorPaths = [
  'PROVIDER_PROTOCOL_CHANGED',
  'reject escaped cwd before spawning',
  'stdin EPIPE and spawn failure',
  'API HTTPS or HTTP loopback only',
  'exact model mismatch rejection',
  'symlink/junction escape',
  'ID outside lowercase pattern',
  'blank/over-100 label',
  'missing executable',
  'Requests abort after 8000 ms',
  'Dirty root or any dirty/missing part blocks integration',
  'Reject remaining conflicts/markers before commit/push',
  'ignore foreign chats',
  'ambiguous project selection must not act',
  'Stop confirmation expires at 60000 ms',
  'HTTP loopback only',
  'Pause failure does not start or resume',
  'Stop failure cannot skip resume',
  '409/conflict at most eight times with 500 ms waits',
  'thrown failures exit 1'
];

const recoveryPaths = [
  'drains until close or 5000 ms after exit',
  'terminal processing must be idempotent across error/exit/close/cancel races',
  'failed discovery retains previous same-scope availability',
  'Persist invalidated scopes across restart',
  'Grok refresh within five minutes of expiry',
  'ChatGPT retries once after 401 when refresh exists',
  'Ordered no-ff merges retain earlier successful merges',
  'abort only the active merge and preserve branches',
  'Cleanup verifies exact work path, retains dirty work normally',
  'reads conflicts after abort',
  'refusal returns kept:true and preserves the local commit',
  'Persist next offset **before** handling each accepted update',
  'restart must not replay commands',
  'Failed handlers must not poison the serialized lane',
  'exactly one retry after failure',
  'corrupt file starts empty',
  'Always release the busy gate',
  'restoration still runs',
  'stops only the test build, restores polling and removes copied token in finally'
];

const concurrencyPaths = [
  'Read both pipes concurrently',
  'A new lead call aborts only its own key',
  'single-flight versus queued catalog refresh',
  'Account/configuration hash and generation prevent stale in-flight results',
  'concurrent parts',
  'two serialized lanes (transitions and commands)',
  'concurrent polls/alerts/pairing',
  'isolated concurrent replies',
  'reject a second handover while the first is active',
  'shared handover gate'
];

const edgeCases = [
  'dropThinkingPrefix',
  'MAX_OUTPUT_CHARS',
  'Stderr retains the last 4000 characters',
  'Defaults: 4096 output tokens, 30 rounds, 90000 ms',
  'positive integer maxima: 65536 tokens, 100 rounds, 300000 ms',
  'At most 32 calls per turn',
  'response bound 4 MiB, stdin bound 16 MiB',
  'read 64 KiB, write/result 1 MiB, list 1000 entries',
  'Parse BOM, ANSI, duplicate IDs and first readable label',
  'Valid finite fractions 0..1',
  'null/string/number percentage distinctions',
  'spaces/Unicode',
  'title-before-role-before-first-unchecked ticking',
  'duplicate-step labels',
  'Managed exclude marker must not remove user rules',
  'exact English/Italian product strings',
  'HTML escaping',
  'step zero',
  'redacted secrets/paths',
  'Memory defaults 8 messages, caps configured limit at 20',
  'Facts cap 12 projects, prompts cap 2400 characters',
  'Pending ack is max integer update ID + 1 only on successful poll'
];

const openDecisions = [
  'Native worker/API host',
  'Dependency gaps',
  'Tree termination',
  'Bounded operations versus observed gaps',
  'Portable live validation',
  'Data and safety gaps',
  'side-effect comparison approval is a prerequisite'
];

const absent = [
  'native/Planwharf.Engine/Runner.cs',
  'native/Planwharf.Engine/ProviderModels.cs',
  'native/Planwharf.Engine/ExternalAdapters.cs',
  'native/Planwharf.Engine/ApiAgent.cs',
  'native/Planwharf.Engine/ProviderAdapters.cs',
  'native/Planwharf.Engine/ProviderConnections.cs',
  'native/Planwharf.Engine/Antigravity.cs',
  'native/Planwharf.Engine/PlanUsage.cs',
  'native/Planwharf.Engine/Worktrees.cs',
  'native/Planwharf.Engine/PlanFile.cs',
  'native/Planwharf.Engine/PrCheck.cs',
  'native/Planwharf.Engine/Phone.cs',
  'native/Planwharf.Engine/PhoneOllama.cs',
  'native/Planwharf.Engine/PhoneHandover.cs',
  'native/Planwharf.Engine/PhoneHandoverLive.cs',
  'native/Planwharf.Engine/PhoneActivationCheck.cs'
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
  if (/the 16 modules are ported/i.test(sheet)) errors.push('sheet prematurely claims modules are ported');
  return errors;
}

function without(sheet, phrase) {
  const flattened = flat(sheet);
  assert.ok(flattened.includes(phrase), 'fixture phrase missing: ' + phrase);
  return flattened.split(phrase).join('');
}

test('job 42 sheet covers all 16 modules, mappings, parity, checks, and CI commands', () => {
  assert.deepEqual(assessSheet(read(sheetPath)), []);
});

test('dropping any required module or target class fails assessment', () => {
  const sheet = read(sheetPath);
  const sampleModules = [
    ['runner cjs', 'engine/runner.cjs'],
    ['provider-models cjs', 'engine/provider-models.cjs'],
    ['planUsage js', 'engine/planUsage.js'],
    ['worktrees cjs', 'engine/worktrees.cjs'],
    ['phone-handover cjs', 'engine/phone-handover.cjs'],
    ['Runner cs', 'Runner.cs'],
    ['PlanUsage cs', 'PlanUsage.cs'],
    ['Worktrees cs', 'Worktrees.cs'],
    ['RunnerTests cs', 'RunnerTests.cs'],
    ['WorktreesTests cs', 'WorktreesTests.cs']
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
    ['parity required file', without(sheet, 'native/porting/parity/required.json')],
    ['parity record script', without(sheet, 'node native/porting/parity/record.cjs')],
    ['engineering ci', without(sheet, 'node scripts/engineering/ci.cjs')],
    ['dotnet build engine', without(sheet, 'dotnet build native/Planwharf.Engine -c Release')],
    ['dotnet test engine', without(sheet, 'dotnet test native/Planwharf.Engine.Tests -c Release')],
    ['dotnet build fake cli', without(sheet, 'dotnet build native/Planwharf.FakeCli -c Release')],
    ['dotnet test fake cli', without(sheet, 'dotnet test native/Planwharf.FakeCli.Tests -c Release')],
    ['parity ports wiring', without(sheet, 'ParityTests.Ports')],
    ['js specification invariant', without(sheet, 'JavaScript is the behavioral specification')]
  ];
  for (const [name, broken] of cases) {
    const errors = assessSheet(broken);
    assert.ok(errors.length > 0, name + ' did not produce errors');
  }
});

test('dropping an error, recovery, concurrency, or edge case limit fails assessment', () => {
  const sheet = read(sheetPath);
  const cases = [
    ['runner protocol changed error', without(sheet, 'PROVIDER_PROTOCOL_CHANGED')],
    ['api loopback error', without(sheet, 'API HTTPS or HTTP loopback only')],
    ['api symlink escape error', without(sheet, 'symlink/junction escape')],
    ['phone telegram pairing error', without(sheet, 'ignore foreign chats')],
    ['phone handover pause error', without(sheet, 'Pause failure does not start or resume')],
    ['runner drain recovery', without(sheet, 'drains until close or 5000 ms after exit')],
    ['plan usage token refresh recovery', without(sheet, 'ChatGPT retries once after 401 when refresh exists')],
    ['worktrees merge abort recovery', without(sheet, 'abort only the active merge and preserve branches')],
    ['phone offset update recovery', without(sheet, 'Persist next offset **before** handling each accepted update')],
    ['phone ollama single retry recovery', without(sheet, 'exactly one retry after failure')],
    ['runner concurrent pipes', without(sheet, 'Read both pipes concurrently')],
    ['provider models stale generation concurrency', without(sheet, 'Account/configuration hash and generation prevent stale in-flight results')],
    ['phone serialized lanes concurrency', without(sheet, 'two serialized lanes (transitions and commands)')],
    ['phone handover busy gate concurrency', without(sheet, 'reject a second handover while the first is active')],
    ['runner drop thinking prefix edge case', without(sheet, 'dropThinkingPrefix')],
    ['api turn limit edge case', without(sheet, 'At most 32 calls per turn')],
    ['plan file title before role tick edge case', without(sheet, 'title-before-role-before-first-unchecked ticking')],
    ['phone bilingual product strings edge case', without(sheet, 'exact English/Italian product strings')]
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
  assert.ok(assessSheet('The 16 modules are ported in full.').length > 0);
  assert.ok(assessSheet('# Specification\nNo decisions here.').length > 0);
});

test('documentation names job 42 as the phase 4 sheet and points to its port, whatever the status now', () => {
  const next = read('docs/NEXT-WORK.md');
  assert.match(next, /\| 42 \| 4\. Processes and providers \| Write the sheet:.*\| `port\/42-sheet` \| [^|]+ \|/);
  assert.match(next, /\| 43 \| 4\. \| Port them, as sheet 42 says\..*\| `port\/43-phase4` \| [^|]+ \|/);
  const built = read('docs/BUILT-WITH-PLANWHARF.md');
  assert.match(built, /Job 42/);
});

test('the sheet stays on port/42-sheet and does not add C# port implementations', () => {
  const changed = git(['diff', '--name-only', 'origin/main...HEAD']).split('\n').filter(Boolean);
  if (!changed.length || !changed.includes(sheetPath) || changed.some(file => !allowedDiff.has(file))) return;
  for (const file of absent) {
    assert.equal(fs.existsSync(path.join(root, file)), false, file + ' should not exist in Job 42');
  }
  // The branch may be behind main (main moves on while it waits for review): that is not a fault of the sheet.
  for (const file of changed) assert.ok(allowedDiff.has(file), file);
  assert.ok(changed.includes(sheetPath));
  assert.equal(git(['diff', '--check', 'origin/main...HEAD']), '');
  const branch = git(['branch', '--show-current']);
  if (branch) assert.equal(branch, 'port/42-sheet');
});
