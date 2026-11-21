'use strict';
// Edges the main panel tests leave out: check shapes, a partial gh failure, paging bounds, and overlapping reads.
const test = require('node:test');
const assert = require('node:assert/strict');
const { pullRequestPanel } = require('./pr-panel.cjs');

const NOW = Date.parse('2026-11-21T12:00:00Z');
const DAY = 24 * 60 * 60 * 1000;
const at = ms => new Date(ms).toISOString();

function row(number, extra) {
  return {
    number, title: 'Work ' + number, headRefName: 'feat/' + number, url: 'https://github.com/acme/app/pull/' + number,
    state: 'OPEN', createdAt: at(NOW - 2 * DAY), updatedAt: at(NOW - DAY), mergedAt: null, closedAt: null,
    mergeable: 'MERGEABLE', statusCheckRollup: [], author: { login: 'octocat' }, body: '', ...extra
  };
}

function scripted(handler) {
  return async (folder, cmd, args) => {
    assert.equal(cmd, 'gh');
    const kind = args[0];
    const state = kind === 'pr' ? args[args.indexOf('--state') + 1] : 'runs';
    return handler(state, folder);
  };
}

const ok = rows => ({ status: 0, stdout: JSON.stringify(rows) });

test('a commit status is red or green by its state, and a red one can be fixed', async () => {
  const open = [row(4, {
    mergeable: 'MERGEABLE',
    statusCheckRollup: [
      { context: 'ci/circleci', state: 'FAILURE' },
      { context: 'ci/lint', state: 'SUCCESS' },
      { name: 'build', conclusion: 'TIMED_OUT', status: 'COMPLETED' },
      { context: 'ci/pending', state: 'PENDING' },
      { name: 'docs', conclusion: 'NEUTRAL', status: 'COMPLETED' },
      { name: '', conclusion: 'FAILURE' },
      null
    ]
  })];
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'open',
    run: scripted(state => state === 'open' ? ok(open) : ok([]))
  });
  assert.equal(panel.ok, true);
  const item = panel.items[0];
  assert.deepEqual(item.checks, [
    { name: 'ci/circleci', state: 'red' },
    { name: 'ci/lint', state: 'green' },
    { name: 'build', state: 'red' },
    { name: 'ci/pending', state: 'running' },
    { name: 'docs', state: 'green' }
  ]);
  assert.equal(item.canFix, true);
  assert.equal(item.conflicts, false);
});

test('a merged list that cannot be read does not pretend nothing was merged', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'open',
    run: scripted(state => {
      if (state === 'merged') return { status: 1, stderr: 'API rate limit exceeded' };
      if (state === 'open') return ok([row(3, { statusCheckRollup: [{ name: 'build', conclusion: 'SUCCESS', status: 'COMPLETED' }] })]);
      return ok([]);
    })
  });
  assert.equal(panel.ok, false);
  assert.match(panel.reason, /rate limit/);
  assert.equal(panel.summary, undefined);
});

test('a closed list or a check-run list can fail and the open panel still answers', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'open',
    run: scripted(state => {
      if (state === 'closed') return { status: 1, stderr: 'closed history unavailable' };
      if (state === 'runs') return { status: 0, stdout: '{not json' };
      if (state === 'merged') return ok([row(8, { state: 'MERGED', createdAt: at(NOW - 5 * DAY), mergedAt: at(NOW - 2 * DAY), closedAt: at(NOW - 2 * DAY) })]);
      return ok([row(3)]);
    })
  });
  assert.equal(panel.ok, true);
  assert.deepEqual(panel.items.map(item => item.number), [3]);
  assert.equal(panel.summary.merged, 1);
  assert.deepEqual(panel.summary.failedChecks, []);
  const closed = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'closed',
    run: scripted(state => state === 'closed' ? { status: 1, stderr: 'closed history unavailable' } : ok([]))
  });
  assert.equal(closed.ok, false);
  assert.match(closed.reason, /closed history/);
});

test('the summary window, the median and the busiest checks stay inside 30 days', async () => {
  const merged = [
    row(1, { state: 'MERGED', createdAt: at(NOW - 30 * DAY), mergedAt: at(NOW - 30 * DAY), closedAt: at(NOW - 30 * DAY) }),
    row(2, { state: 'MERGED', createdAt: at(NOW - 4 * DAY), mergedAt: at(NOW - 2 * DAY), closedAt: at(NOW - 2 * DAY) }),
    row(3, { state: 'MERGED', createdAt: at(NOW - 40 * DAY), mergedAt: at(NOW - 30 * DAY - 1), closedAt: at(NOW - 30 * DAY - 1) }),
    row(4, { state: 'MERGED', createdAt: at(NOW + DAY), mergedAt: at(NOW + DAY), closedAt: at(NOW + DAY) }),
    row(5, { state: 'MERGED', createdAt: at(NOW - DAY), mergedAt: at(NOW - 2 * DAY), closedAt: at(NOW - 2 * DAY) })
  ];
  const runs = [
    { name: 'zeta', conclusion: 'FAILURE', createdAt: at(NOW - DAY) },
    { name: 'alpha', conclusion: 'failure', createdAt: at(NOW - DAY) },
    { name: 'alpha', conclusion: 'FAILURE', createdAt: at(NOW - 2 * DAY) },
    { name: 'beta', conclusion: 'failure', createdAt: at(NOW - 3 * DAY) },
    { name: 'gamma', conclusion: 'failure', createdAt: at(NOW - 4 * DAY) },
    { name: 'delta', conclusion: 'failure', createdAt: at(NOW - 5 * DAY) },
    { name: 'epsilon', conclusion: 'failure', createdAt: at(NOW - 6 * DAY) },
    { name: 'old', conclusion: 'failure', createdAt: at(NOW - 45 * DAY) },
    { name: '  ', conclusion: 'failure', createdAt: at(NOW - DAY) },
    { name: 'build', conclusion: 'success', createdAt: at(NOW - DAY) },
    { name: 'soon', conclusion: 'failure', createdAt: at(NOW + DAY) }
  ];
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'merged', fixes: [{ number: 2, result: 'failed', text: 'The push was refused.' }],
    run: scripted(state => state === 'runs' ? ok(runs) : state === 'merged' ? ok(merged) : ok([]))
  });
  assert.equal(panel.summary.merged, 3);
  // Odd count: the middle of 0, 0 and 2 days. A merge dated before it opened counts as no wait.
  assert.equal(panel.summary.medianMergeMs, 0);
  assert.equal(panel.summary.neededFix, 1);
  assert.deepEqual(panel.summary.failedChecks, [
    { name: 'alpha', count: 2 },
    { name: 'beta', count: 1 },
    { name: 'delta', count: 1 },
    { name: 'epsilon', count: 1 },
    { name: 'gamma', count: 1 }
  ]);
  const clamped = panel.items.find(item => item.number === 5);
  assert.equal(clamped.durationMs, 0);
  // Outside the 30 days still listed in history, and left out of the summary above.
  assert.ok(panel.items.some(item => item.number === 4));
  assert.ok(panel.items.some(item => item.number === 3));
  const even = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'merged',
    run: scripted(state => state === 'merged' ? ok([
      row(10, { state: 'MERGED', createdAt: at(NOW - 2 * DAY), mergedAt: at(NOW - DAY), closedAt: at(NOW - DAY) }),
      row(11, { state: 'MERGED', createdAt: at(NOW - 4 * DAY), mergedAt: at(NOW - 2 * DAY), closedAt: at(NOW - 2 * DAY) })
    ]) : ok([]))
  });
  assert.equal(even.summary.merged, 2);
  assert.equal(even.summary.medianMergeMs, Math.round(1.5 * DAY));
});

test('bad pages, duplicate numbers and a failed fix still leave a way to try again', async () => {
  const open = [
    row(9, { mergeable: 'CONFLICTING', updatedAt: at(NOW - 1000) }),
    row(9, { title: 'duplicate' }),
    { number: '8', title: 'not a number' },
    null,
    row(7, { updatedAt: at(NOW - 1000), body: 'opened by planwharf from the push button.' })
  ];
  const run = scripted(state => state === 'open' ? ok(open) : state === 'runs' ? { status: 1, stderr: 'no actions', missing: false } : ok([]));
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'nope', author: 'nope', offset: -3, works: [], fixes: [{ number: 9, result: 'failed', text: 'The push was refused.' }],
    fixing: [7, { number: 1.5 }, null],
    run
  });
  assert.equal(panel.ok, true);
  assert.equal(panel.state, 'open');
  assert.equal(panel.author, 'all');
  assert.equal(panel.offset, 0);
  assert.deepEqual(panel.items.map(item => item.number), [9, 7]);
  const failed = panel.items.find(item => item.number === 9);
  assert.equal(failed.fixStatus, 'failed');
  assert.equal(failed.canFix, true);
  assert.equal(failed.fixed, false);
  const running = panel.items.find(item => item.number === 7);
  assert.equal(running.fixStatus, 'running');
  assert.equal(running.canFix, false);
  assert.equal(running.byPlanwharf, true);
  const past = await pullRequestPanel({ folder: '/work', now: NOW, state: 'open', offset: 30, run });
  assert.deepEqual(past.items, []);
  assert.equal(past.more, false);
  const junk = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'open',
    run: scripted(state => state === 'open' ? { status: 0, stdout: '{"number":1}' } : ok([]))
  });
  assert.equal(junk.ok, true);
  assert.deepEqual(junk.items, []);
  const broken = await pullRequestPanel({
    folder: '/work', now: NOW,
    run: scripted(state => state === 'open' ? { status: 0, stdout: 'not-json' } : ok([]))
  });
  assert.equal(broken.ok, false);
  assert.match(broken.reason, /not readable/);
});

test('the four gh reads overlap, and two panels do not mix their answers', async () => {
  const waiting = [];
  let overlapped = false;
  const run = () => new Promise(resolve => {
    waiting.push(resolve);
    if (waiting.length === 4) overlapped = true;
  });
  const pending = pullRequestPanel({ folder: '/work', now: NOW, state: 'open', run });
  try {
    await new Promise(resolve => setTimeout(resolve, 50));
    assert.equal(overlapped, true);
    assert.equal(waiting.length, 4);
  } finally {
    for (const release of waiting) release(ok([]));
  }
  const panel = await pending;
  assert.equal(panel.ok, true);

  let current = 0;
  const gate = [];
  const slow = folder => new Promise(resolve => gate.push(() => resolve(ok([row(folder === '/left' ? 1 : 2)]))));
  const left = pullRequestPanel({ folder: '/left', now: NOW, state: 'open', run: (folder, cmd) => { assert.equal(cmd, 'gh'); return slow(folder); } });
  const right = pullRequestPanel({ folder: '/right', now: NOW, state: 'open', run: (folder, cmd) => { assert.equal(cmd, 'gh'); return slow(folder); } });
  await new Promise(resolve => setTimeout(resolve, 20));
  assert.equal(gate.length, 8);
  for (const release of gate) release();
  const [a, b] = await Promise.all([left, right]);
  assert.deepEqual(a.items.map(item => item.number), [1]);
  assert.deepEqual(b.items.map(item => item.number), [2]);
  current = a.items.length + b.items.length;
  assert.equal(current, 2);
});

test('the 30 day summary counts merges past the first page gh would return', async () => {
  const merged = Array.from({ length: 120 }, (_, index) => row(index + 1, {
    state: 'MERGED', createdAt: at(NOW - 2 * DAY), mergedAt: at(NOW - DAY), closedAt: at(NOW - DAY)
  }));
  let limit = 0;
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'merged',
    run: async (_folder, cmd, args) => {
      assert.equal(cmd, 'gh');
      if (args[0] !== 'pr') return ok([]);
      const state = args[args.indexOf('--state') + 1];
      if (state !== 'merged') return ok([]);
      limit = Number(args[args.indexOf('--limit') + 1]);
      return ok(merged.slice(0, limit));
    }
  });
  assert.equal(panel.ok, true);
  assert.ok(limit >= 120);
  assert.equal(panel.summary.merged, 120);
  assert.equal(panel.items.length, 30);
  assert.equal(panel.more, true);
});

test('the merged list still answers when the closed list cannot be read', async () => {
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'merged',
    run: scripted(state => {
      if (state === 'closed') return { status: 1, stderr: 'closed history unavailable' };
      if (state === 'merged') return ok([row(8, { state: 'MERGED', createdAt: at(NOW - 5 * DAY), mergedAt: at(NOW - 2 * DAY), closedAt: at(NOW - 2 * DAY) })]);
      return ok([]);
    })
  });
  assert.equal(panel.ok, true);
  assert.deepEqual(panel.items.map(item => item.number), [8]);
  assert.equal(panel.summary.merged, 1);
});

test('a gh read that crashes is a panel message, and it does not wipe the other panel', async () => {
  const crashed = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'open',
    run: async (_folder, _cmd, args) => {
      const kind = args[0] === 'pr' ? args[args.indexOf('--state') + 1] : 'runs';
      if (kind === 'open') throw new Error('gh crashed');
      if (kind === 'merged') return ok([row(2, { state: 'MERGED', createdAt: at(NOW - 3 * DAY), mergedAt: at(NOW - DAY), closedAt: at(NOW - DAY) })]);
      return ok([]);
    }
  });
  assert.equal(crashed.ok, false);
  assert.match(crashed.reason, /gh crashed/);
  assert.equal(crashed.summary, undefined);

  const hidden = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'open',
    run: async (_folder, _cmd, args) => {
      const kind = args[0] === 'pr' ? args[args.indexOf('--state') + 1] : 'runs';
      if (kind === 'merged') throw new Error('merged history crashed');
      return ok([row(3)]);
    }
  });
  assert.equal(hidden.ok, false);
  assert.match(hidden.reason, /merged history crashed/);
  assert.equal(hidden.summary, undefined);

  const checks = pullRequestPanel({
    folder: '/checks', now: NOW, state: 'open',
    run: async (_folder, _cmd, args) => {
      if (args[0] === 'run') throw new Error('run list crashed');
      return ok(args.includes('open') ? [row(4)] : []);
    }
  });
  const other = pullRequestPanel({
    folder: '/other', now: NOW, state: 'open',
    run: async () => { throw new Error('other folder crashed'); }
  });
  const [listed, bad] = await Promise.all([checks, other]);
  assert.equal(listed.ok, true);
  assert.deepEqual(listed.items.map(item => item.number), [4]);
  assert.deepEqual(listed.summary.failedChecks, []);
  assert.equal(bad.ok, false);
  assert.match(bad.reason, /other folder crashed/);
});

test('a timed out or broken check counts among the checks that failed most often', async () => {
  const runs = [
    { name: 'build', conclusion: 'timed_out', createdAt: at(NOW - DAY) },
    { name: 'build', conclusion: 'TIMED_OUT', createdAt: at(NOW - 2 * DAY) },
    { name: 'lint', conclusion: 'failure', createdAt: at(NOW - DAY) },
    { name: 'deploy', conclusion: 'cancelled', createdAt: at(NOW - DAY) },
    { name: 'docs', conclusion: 'startup_failure', createdAt: at(NOW - DAY) },
    { name: 'nightly', conclusion: 'skipped', createdAt: at(NOW - DAY) },
    { name: 'release', conclusion: 'action_required', createdAt: at(NOW - DAY) },
    { name: 'package', conclusion: 'error', createdAt: at(NOW - DAY) },
    { name: 'quiet', conclusion: 'success', createdAt: at(NOW - DAY) }
  ];
  const panel = await pullRequestPanel({
    folder: '/work', now: NOW, state: 'merged',
    run: scripted(state => state === 'runs' ? ok(runs) : state === 'merged' ? ok([row(1, { state: 'MERGED', createdAt: at(NOW - 2 * DAY), mergedAt: at(NOW - DAY), closedAt: at(NOW - DAY) })]) : ok([]))
  });
  assert.equal(panel.ok, true);
  assert.equal(panel.summary.merged, 1);
  assert.deepEqual(panel.summary.failedChecks, [
    { name: 'build', count: 2 },
    { name: 'docs', count: 1 },
    { name: 'lint', count: 1 },
    { name: 'package', count: 1 },
    { name: 'release', count: 1 }
  ]);
});
