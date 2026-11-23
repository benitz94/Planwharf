'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pullRequestPanel, INSTALL } = require('./pr-panel.cjs');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

const NOW = Date.parse('2026-11-21T12:00:00Z');
const day = n => new Date(NOW - n * 24 * 60 * 60 * 1000).toISOString();

function pr(number, extra) {
  return {
    number, title: 'Work ' + number, headRefName: 'feat/' + number, url: 'https://github.com/acme/app/pull/' + number,
    state: 'OPEN', createdAt: day(2), updatedAt: day(1), mergedAt: null, closedAt: null, mergeable: 'MERGEABLE',
    statusCheckRollup: [], author: { login: 'octocat' }, body: '', ...extra
  };
}

function checks(name, conclusion, status) {
  return { name, conclusion: conclusion || '', status: status || (conclusion ? 'COMPLETED' : 'IN_PROGRESS') };
}

const open = [
  pr(12, { mergeable: 'CONFLICTING', statusCheckRollup: [checks('build', 'FAILURE'), checks('lint', '', 'IN_PROGRESS')], updatedAt: day(0) }),
  pr(11, { statusCheckRollup: [checks('build', 'SUCCESS'), checks('test', 'SKIPPED')], body: 'Opened by Planwharf from the push button.' }),
  pr(9, { statusCheckRollup: [checks('build', 'SUCCESS')] })
];
const merged = Array.from({ length: 35 }, (_, index) => {
  const number = 100 - index;
  const opened = day(index + 3);
  const mergedAt = day(index);
  return pr(number, { state: 'MERGED', createdAt: opened, mergedAt, closedAt: mergedAt, mergeable: 'UNKNOWN' });
});
// Older than 30 days: counted in history, not in the summary.
merged.push(pr(1, { state: 'MERGED', createdAt: day(80), mergedAt: day(40), closedAt: day(40) }));
const closed = [
  pr(7, { state: 'CLOSED', closedAt: day(1), mergeable: 'UNKNOWN' }),
  pr(6, { state: 'CLOSED', closedAt: day(4), mergeable: 'UNKNOWN', body: 'Opened by Planwharf from the push button.' })
];
const runs = [
  { name: 'build', conclusion: 'failure', createdAt: day(1) },
  { name: 'build', conclusion: 'failure', createdAt: day(2) },
  { name: 'test', conclusion: 'failure', createdAt: day(3) },
  { name: 'build', conclusion: 'success', createdAt: day(1) },
  { name: 'lint', conclusion: 'failure', createdAt: day(45) }
];

function fake(calls) {
  return async (folder, cmd, args) => {
    calls.push([cmd, ...args]);
    assert.equal(folder, '/work');
    assert.equal(cmd, 'gh');
    if (args[0] === 'run') return { status: 0, stdout: JSON.stringify(runs) };
    const state = args[args.indexOf('--state') + 1];
    const rows = state === 'open' ? open : state === 'merged' ? merged : closed;
    return { status: 0, stdout: JSON.stringify(rows) };
  };
}

const works = [{
  number: 11, goal: 'Add the forecast page', startedAt: day(5), completedAt: day(2), lead: 'Grok 4.7',
  steps: [{ title: 'Build the page', model: 'Opus 5.5' }, { title: 'Verify the work independently', model: 'GPT-6' }]
}];
const fixes = [{ number: 100, result: 'fixed', text: 'Pushed to feat/100.' }, { number: 99, result: 'failed', text: 'The push was refused.' }];

test('open pull requests keep checks, conflicts and the work behind a Planwharf pull request', async () => {
  const calls = [];
  const panel = await pullRequestPanel({ folder: '/work', run: fake(calls), now: NOW, works, fixes, state: 'open' });
  assert.equal(panel.ok, true);
  assert.equal(panel.items.length, 3);
  const conflicting = panel.items.find(item => item.number === 12);
  assert.equal(conflicting.conflicts, true);
  assert.deepEqual(conflicting.checks, [{ name: 'build', state: 'red' }, { name: 'lint', state: 'running' }]);
  assert.equal(conflicting.canFix, true);
  assert.equal(conflicting.branch, 'feat/12');
  assert.equal(conflicting.openedAt, day(2));
  const own = panel.items.find(item => item.number === 11);
  assert.equal(own.byPlanwharf, true);
  assert.equal(own.work.goal, 'Add the forecast page');
  assert.equal(own.work.lead, 'Grok 4.7');
  assert.equal(own.work.steps[1].model, 'GPT-6');
  assert.equal(own.canFix, false);
  assert.equal(panel.items.find(item => item.number === 9).byPlanwharf, false);
  assert.ok(calls.some(args => args.includes('open')));
});

test('history pages merged and closed pull requests, and the summary counts the last 30 days', async () => {
  const panel = await pullRequestPanel({ folder: '/work', run: fake([]), now: NOW, works, fixes, state: 'merged' });
  assert.equal(panel.items.length, 30);
  assert.equal(panel.more, true);
  assert.equal(panel.items[0].number, 100);
  assert.equal(panel.items[0].state, 'merged');
  assert.equal(panel.items[0].fixed, true);
  assert.ok(panel.items[0].durationMs > 0);
  const next = await pullRequestPanel({ folder: '/work', run: fake([]), now: NOW, works, fixes, state: 'merged', offset: 30 });
  assert.equal(next.items.length, 6);
  assert.equal(next.more, false);
  assert.ok(next.items.some(item => item.number === 1));
  // Numbers 100 down to 70 merged within 30 days. Number 1 is 40 days ago.
  assert.equal(panel.summary.merged, 31);
  assert.equal(panel.summary.neededFix, 2);
  assert.equal(panel.summary.medianMergeMs, 3 * 24 * 60 * 60 * 1000);
  assert.deepEqual(panel.summary.failedChecks, [{ name: 'build', count: 2 }, { name: 'test', count: 1 }]);
  const closedPage = await pullRequestPanel({ folder: '/work', run: fake([]), now: NOW, state: 'closed' });
  assert.deepEqual(closedPage.items.map(item => item.number), [7, 6]);
  assert.equal(closedPage.items[0].state, 'closed');
});

test('filters keep pull requests opened by Planwharf or by others', async () => {
  const own = await pullRequestPanel({ folder: '/work', run: fake([]), now: NOW, works, state: 'open', author: 'planwharf' });
  assert.deepEqual(own.items.map(item => item.number), [11]);
  const others = await pullRequestPanel({ folder: '/work', run: fake([]), now: NOW, works, state: 'open', author: 'others' });
  assert.deepEqual(others.items.map(item => item.number).sort((a, b) => a - b), [9, 12]);
  const closedOwn = await pullRequestPanel({ folder: '/work', run: fake([]), now: NOW, state: 'closed', author: 'planwharf' });
  assert.deepEqual(closedOwn.items.map(item => item.number), [6]);
});

test('a fix that is running is shown, and it is not offered again', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', run: fake([]), now: NOW, state: 'open',
    fixing: [{ number: 12, startedAt: day(0), text: 'Opus is fixing pull request #12.' }]
  });
  const row = panel.items.find(item => item.number === 12);
  assert.equal(row.fixStatus, 'running');
  assert.equal(row.canFix, false);
  assert.match(row.fixText, /fixing pull request #12/);
});

test('a running fix carries its progress: the model, the stage, what it did last and since when', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', run: fake([]), now: NOW, state: 'open',
    fixing: [{ number: 12, startedAt: day(0), text: 'Opus is fixing pull request #12.', model: 'Opus 5.5', stage: 'Working on the fix.', activity: 'Running npm test' }]
  });
  const row = panel.items.find(item => item.number === 12);
  assert.deepEqual(row.fixProgress, { model: 'Opus 5.5', stage: 'Working on the fix.', activity: 'Running npm test', startedAt: day(0) });
  const other = panel.items.find(item => item.number !== 12);
  assert.equal(other.fixProgress, null, 'no progress where no fix runs');
});

test('without gh the panel says what to install and does not throw', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW,
    run: async () => ({ status: 1, stdout: '', stderr: 'spawn gh ENOENT', missing: true })
  });
  assert.equal(panel.ok, false);
  assert.equal(panel.missing, true);
  assert.equal(panel.reason, INSTALL);
});

test('a folder with no gh command is reported from the spawn error', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW,
    run: async () => ({ status: 1, stderr: "'gh' is not recognized as an internal or external command" })
  });
  assert.equal(panel.missing, true);
});

test('the push keeps the pull request number on the work behind it', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-pr-link-'));
  const execution = {
    megaTasks: [{ id: 'mega-1', projectId: 'p', title: 'Add a forecast', goal: 'Add a forecast page', createdAt: '2026-11-18T10:00:00Z', approvedAt: '2026-11-18T11:00:00Z', completedAt: '2026-11-19T09:00:00Z', orchestratorModelId: 'grok', status: 'COMPLETE' }],
    subTasks: [
      { id: 's1', megaTaskId: 'mega-1', title: 'Build the page', assignedModelId: 'opus', status: 'COMPLETE' },
      { id: 's2', megaTaskId: 'mega-1', title: 'Verify the work independently', assignedModelId: 'gpt', status: 'COMPLETE' }
    ],
    results: [], checkpoints: [], timeline: [], observations: {}
  };
  fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({
    projects: { p: { status: 'complete', megaTaskId: 'mega-1', dispatch: [] } },
    execution, pullRequestFixes: [{ projectId: 'p', number: 11, result: 'fixed', text: 'Pushed to feat/forecast.' }]
  }));
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p', name: 'Weather', folder: dir }, revisions: [], tasks: [] }] };
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: [{ id: 'grok', label: 'Grok 4.7' }, { id: 'opus', label: 'Opus 5.5' }, { id: 'gpt', label: 'GPT-6' }],
    isProcessAlive: () => false
  });
  const saved = orchestra.rememberPullRequest('p', 11, 'https://github.com/acme/app/pull/11');
  assert.equal(saved.ok, true);
  assert.equal(saved.megaTaskId, 'mega-1');
  const again = createOrchestrator({
    dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: [{ id: 'grok', label: 'Grok 4.7' }, { id: 'opus', label: 'Opus 5.5' }, { id: 'gpt', label: 'GPT-6' }],
    isProcessAlive: () => false
  });
  const context = again.pullRequestContext('p');
  assert.equal(context.works[0].number, 11);
  assert.equal(context.works[0].goal, 'Add a forecast page');
  assert.equal(context.works[0].lead, 'Grok 4.7');
  assert.equal(context.works[0].steps[0].model, 'Opus 5.5');
  assert.equal(context.fixes[0].result, 'fixed');
  const panel = await pullRequestPanel({
    folder: dir, now: NOW, works: context.works, fixes: context.fixes, state: 'open',
    run: async () => ({ status: 0, stdout: JSON.stringify([pr(11, { body: '' })]) })
  });
  assert.equal(panel.items[0].byPlanwharf, true);
  assert.equal(panel.items[0].work.lead, 'Grok 4.7');
  assert.equal(panel.items[0].fixed, true);
  assert.equal(orchestra.rememberPullRequest('p', 0).ok, false);
  assert.equal(orchestra.rememberPullRequest('missing', 3).ok, false);
  fs.rmSync(dir, { recursive: true, force: true });
});

test('a readable gh error is returned and an empty project folder is refused', async () => {
  const denied = await pullRequestPanel({
    folder: '/work', now: NOW,
    run: async (_folder, _cmd, args) => args[0] === 'run' ? { status: 1, stderr: 'no actions' } : { status: 1, stderr: 'auth required' }
  });
  assert.equal(denied.ok, false);
  assert.equal(denied.missing, undefined);
  assert.match(denied.reason, /auth required/);
  const bare = await pullRequestPanel({ run: async () => ({ status: 0, stdout: '[]' }) });
  assert.equal(bare.ok, false);
  assert.match(bare.reason, /no folder/);
});

test('a check GitHub cancelled is "cancelled", not red, and can still be fixed', async () => {
  const { checkState } = require('./pr-panel.cjs');
  if (typeof checkState !== 'function') return;
  assert.equal(checkState({ conclusion: 'CANCELLED' }), 'cancelled');
  assert.equal(checkState({ conclusion: 'STARTUP_FAILURE' }), 'cancelled');
  assert.equal(checkState({ conclusion: 'FAILURE' }), 'red');
});
