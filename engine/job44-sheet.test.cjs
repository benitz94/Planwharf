'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const sheet = fs.readFileSync(path.join(__dirname, '../native/porting/jobs/44-phase5-execution.md'), 'utf8');
const requirements = {
  mapping: [
    /`engine\/execution\.cjs`\s*\|\s*`Execution\.cs`[^|]*\|\s*`ExecutionTests\.cs`/,
    /`engine\/project-home\.cjs`\s*\|\s*`ProjectHome\.cs`[^|]*\|\s*`ProjectHomeTests\.cs`/,
    /`scope-check\.test\.cjs`/, /`home-containment\.test\.cjs`/,
  ],
  lifecycle: [
    /addition kill failure still resets Verify/i,
    /addition to paused work immediately resumes/i,
    /first answer remains allowed after work starts/i,
    /delegation has no started-work guard/i,
    /result write failure and persist failure/i, /dead\/live\/reused PID after restart/i,
    /failed kill leaves assignment unchanged/i, /retains recoverable branches/i,
    /Completed steps and preexisting dirty files survive/i,
    /no launch before durable approval/i, /exact provider\/model IDs/i,
    /missing\/invalid result cannot become `COMPLETE`/i, /concurrent shared-folder edits/i,
    /never double-collect/i, /failure to kill/i, /two simultaneous additions/i,
    /merge conflict/i, /explicit confirmation/i, /deadline versus completion race/i,
    /decision after work started/i,
  ],
  home: [
    /5-second timeout and 8 MiB buffer/i, /2,000 paths and 8 MiB/i,
    /observation-only operations/i, /inspection must never dispatch work/i,
    /rejects inherited\/redirected Git metadata/i, /each parent realpath/i,
    /symlink\/junction/i, /plan exclusion/i, /nonancestor/i,
    /stale row versus durable MegaTask/i, /rather than a false change count/i,
  ],
  parity: [
    /async values must never be passed to the synchronous pure recorder/i,
    /preserve existing floors/i, /two new floors independently enforced/i,
    /asserted final state, not just an asserted throw/i,
    /\*\*Pure parity\.\*\*/, /\*\*Scenario parity\.\*\*/,
    /`native\/porting\/parity\/cases\.cjs`/, /`ParityTests\.Ports`/,
    /`native\/porting\/parity\/scenarios\.cjs`/, /`ScenarioParityTests\.cs`/,
    /scenario-aware omission guard/i, /consume every reply once/i,
    /removed fixture, omitted request, changed effect and changed final state/i,
    /`record\.cjs --check` and `scenarios\.cjs --check` must be read-only/i,
  ],
  ownership: [
    /Job 45 may edit exactly:/, /`native\/Planwharf\.Engine\/Execution\.cs`/,
    /`native\/Planwharf\.Engine\/ProjectHome\.cs`/, /`engine\/parity\.test\.cjs`/,
    /No JavaScript engine implementation, orchestrator source\/test/i,
    /no dependency addition/i, /Files you may change for Job 44/,
    /scope revision before adding a helper/i,
  ],
  orchestrator: [
    /`OrchestratorTeamModels`/, /`OrchestratorJobBuilding`/,
    /`OrchestratorLeadQuestions`/, /`OrchestratorState`/, /`OrchestratorOrderParsing`/,
    /Dependency direction:/, /Migration order for Job 47 onward:/,
    /owner reviews the orchestrator split after Job 45/i,
    /Job 46 writes detailed sheets/i, /No orchestrator implementation is authorized before the owner reviews/i,
    /real-program Windows test remains in phase 7/i,
  ],
};

function assess(text) {
  return Object.entries(requirements).flatMap(([group, checks]) =>
    checks.filter(check => !check.test(text || '')).map(check => `${group}: ${check}`));
}

test('Job 44 specifies mappings, risks, parity, ownership and review boundaries', () => {
  assert.deepEqual(assess(sheet), []);
});

test('every durable requirement detects its own omission, including error and recovery paths', () => {
  for (const [group, checks] of Object.entries(requirements)) {
    for (const check of checks) {
      const broken = sheet.replace(new RegExp(check.source, check.flags + 'g'), '');
      assert.notEqual(broken, sheet, `mutation must change ${group}: ${check}`);
      assert.ok(assess(broken).some(error => error === `${group}: ${check}`),
        `omission escaped ${group}: ${check}`);
    }
  }
});

test('empty and truncated sheets fail and interleaved assessments stay independent', async () => {
  for (const text of ['', null, undefined, sheet.slice(0, sheet.indexOf('## Comparison'))]) {
    assert.ok(assess(text).length, 'incomplete specification must fail');
  }
  const results = await Promise.all(Array.from({ length: 20 }, (_, index) =>
    Promise.resolve().then(() => assess(index % 2 ? '' : sheet))));
  results.forEach((errors, index) => assert.equal(errors.length === 0, index % 2 === 0));
});

test('representative omissions fail their own requirement group', () => {
  for (const [group, check] of Object.entries(requirements).map(([name, checks]) => [name, checks[0]])) {
    const match = sheet.match(check);
    assert.ok(match, group);
    const broken = sheet.replace(check, '');
    assert.ok(assess(broken).some(error => error.startsWith(`${group}:`)), group);
  }
});

test('removing each proposed orchestrator concern is detected', () => {
  for (const name of ['OrchestratorTeamModels', 'OrchestratorJobBuilding',
    'OrchestratorLeadQuestions', 'OrchestratorState', 'OrchestratorOrderParsing']) {
    const broken = sheet.replaceAll(name, 'RemovedConcern');
    assert.ok(assess(broken).some(error => error.startsWith('orchestrator:')), name);
  }
});

test('parity, containment, recovery and ownership mutations are detected', () => {
  const mutations = [
    ['pure recorder wiring', '`ParityTests.Ports`'],
    ['scenario replay', 'consume every reply once'],
    ['scenario mutation controls', 'removed fixture, omitted request, changed effect and changed final state'],
    ['read-only recorders', '`record.cjs --check` and `scenarios.cjs --check` must be read-only'],
    ['result identity', 'missing/invalid result cannot become `COMPLETE`'],
    ['restart', 'never double-collect'],
    ['concurrent additions', 'two simultaneous additions'],
    ['git metadata', 'rejects inherited/redirected Git metadata'],
    ['future source boundary', 'No JavaScript engine implementation, orchestrator source/test'],
    ['owner approval', 'No orchestrator implementation is authorized before the owner reviews'],
  ];
  for (const [name, phrase] of mutations) {
    assert.ok(sheet.includes(phrase), name);
    assert.ok(assess(sheet.replaceAll(phrase, '')).length, name);
  }
});

test('ownership lists are separately scoped to the sheet job and the future port', () => {
  const future = sheet.split('## Job 45 acceptance and exact ownership')[1].split('## Before you start')[0];
  const current = sheet.split('## Files you may change for Job 44')[1].split('## Open decisions')[0];
  for (const file of ['Execution.cs', 'ProjectHome.cs', 'ExecutionTests.cs', 'ProjectHomeTests.cs',
    'cases.cjs', 'required.json', 'scenarios.cjs', 'ScenarioParityTests.cs']) {
    assert.ok(future.includes(file), `Job 45 missing ${file}`);
  }
  for (const file of ['44-phase5-execution.md', 'job44-sheet.test.cjs', 'docs/NEXT-WORK.md',
    'docs/BUILT-WITH-PLANWHARF.md', 'native/porting/PLAN.md', '.planwharf/plan.md']) {
    assert.ok(current.includes(file), `Job 44 missing ${file}`);
  }
});
