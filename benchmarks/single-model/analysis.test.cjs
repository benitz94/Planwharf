'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { analyze, renderReport, sanitizeEvidence } = require('./analysis.cjs');

function run(arm, pairId, extra = {}) {
  return { id: `${pairId}-${arm}`, taskId: 'small', category: 'small', pairId, arm, outcome: 'accepted',
    wallMs: arm === 'direct' ? 1000 : 1200, usageScope: 'all_calls',
    usage: { inputTokens: 100, outputTokens: 50, cachedInputTokens: 70, reasoningTokens: 20, billedCost: null },
    invocations: arm === 'direct' ? 1 : 3, interventions: 0, initialApprovals: arm === 'direct' ? 0 : 1,
    ...extra };
}

test('only accepted, unique, complete pairs enter resource comparisons; all outcomes remain visible', () => {
  const a = analyze([
    run('direct', 'complete'), run('planwharf', 'complete'),
    run('direct', 'missing'),
    run('direct', 'defective'), run('planwharf', 'defective', { outcome: 'defective', wallMs: 1 }),
    run('direct', 'failed', { outcome: 'failed', wallMs: 2 }), run('planwharf', 'failed'),
    run('direct', 'duplicate'), run('direct', 'duplicate', { id: 'duplicate-direct-2' }), run('planwharf', 'duplicate')
  ]);
  assert.equal(a.overall.equalQualityPairs.acceptedPairCount, 1);
  assert.equal(a.overall.equalQualityPairs.metrics.wallMs.medianPairedDifference, 200);
  assert.deepEqual(a.overall.equalQualityPairs.excludedPairs.map(row => row.reason), [
    'missing arm', 'unequal acceptance threshold: both arms must be independently accepted',
    'unequal acceptance threshold: both arms must be independently accepted', 'duplicate arm records'
  ]);
  assert.equal(a.overall.arms.direct.runCount, 6);
  assert.equal(a.overall.arms.direct.outcomes.failed, 1);
  assert.equal(a.overall.arms.planwharf.outcomes.defective, 1);
  assert.equal(a.overall.arms.planwharf.allRunsMetrics.wallMs.min, 1);
  assert.equal(a.overall.arms.planwharf.acceptedMetrics.wallMs.min, 1200);
  assert.match(a.overall.equalQualityPairs.metrics.wallMs.differenceInterval.status, /inconclusive/);
});

test('measured zeros survive; zero and unknown denominators never produce percentages or infinity', () => {
  const a = analyze([run('direct', 'zero', { wallMs: 0, usage: null }), run('planwharf', 'zero', { wallMs: 5, usage: null }),
    run('direct', 'unknown', { wallMs: null }), run('planwharf', 'unknown')]);
  const m = a.overall.equalQualityPairs.metrics.wallMs;
  assert.equal(m.directMedian, 0); assert.equal(m.medianPairedDifference, 5);
  assert.equal(m.medianPairedPercentDifference, null); assert.equal(m.zeroDenominatorPairs, 1); assert.equal(m.unknownPairs, 1);
  assert.equal(a.overall.arms.direct.allRunsMetrics.interventions.median, 0);
  assert.equal(a.overall.arms.direct.allRunsMetrics.restarts.median, null);
  assert.equal(a.overall.arms.direct.allRunsMetrics.wallMs.sum, null);
  assert.equal(a.runs[0].metrics.totalTokens.value, null);
  assert.doesNotMatch(JSON.stringify(a), /Infinity|NaN/);
});

test('cached input and reasoning subsets are not added twice; billed cost and quota stay unknown', () => {
  const a = analyze([run('direct', 'one'), run('planwharf', 'one')]);
  for (const row of a.runs) {
    assert.equal(row.metrics.totalTokens.value, 150);
    assert.equal(row.metrics.cachedInputTokens.value, 70);
    assert.equal(row.metrics.reasoningTokens.value, 20);
    assert.equal(row.metrics.billedCost.value, null);
    assert.equal(row.metrics.quotaDecrement.value, null);
    assert.match(row.metrics.quotaDecrement.availability, /UNKNOWN/);
    assert.equal(row.providerInternalInferences.value, null);
  }
  assert.equal(a.overall.equalQualityPairs.metrics.billedCost.comparablePairs, 0);
});

test('Planwharf usage must attest all roles; incomplete receipts cannot masquerade as full totals', () => {
  for (const usageScope of [undefined, 'worker_only']) {
    const a = analyze([run('planwharf', 'partial', { usageScope })]);
    assert.equal(a.runs[0].metrics.totalTokens.value, null);
    assert.equal(a.runs[0].metrics.invocations.value, null);
    assert.match(a.runs[0].metrics.inputTokens.availability, /planning, coordination, worker, review, retry and recovery/);
  }
  const calls = [{ role: 'lead', usage: { inputTokens: 10, outputTokens: 5 } }, { role: 'worker', usage: { inputTokens: 20, outputTokens: 7 } }];
  const complete = analyze([run('planwharf', 'calls', { invocations: 2, calls })]).runs[0];
  assert.equal(complete.metrics.inputTokens.value, 30); assert.equal(complete.metrics.outputTokens.value, 12);
  assert.equal(complete.metrics.totalTokens.value, 42); assert.equal(complete.metrics.cachedInputTokens.value, null);
  const partial = analyze([run('planwharf', 'calls', { invocations: 3, calls })]).runs[0];
  assert.equal(partial.metrics.totalTokens.value, null);
  assert.match(partial.metrics.totalTokens.availability, /do not cover/);
});

test('inconsistent cache receipt invalidates input instead of reporting impossible savings', () => {
  const a = analyze([run('direct', 'bad', { usage: { inputTokens: 4, outputTokens: 3, cachedInputTokens: 5 } })]);
  assert.equal(a.runs[0].metrics.inputTokens.value, null);
  assert.equal(a.runs[0].metrics.totalTokens.value, null);
  assert.match(a.runs[0].metrics.cachedInputTokens.availability, /inconsistent/);
});

test('bootstrap is paired and deterministic, and requires at least five comparable accepted pairs', () => {
  const runs = Array.from({ length: 5 }, (_, i) => [run('direct', String(i), { wallMs: 100 * (i + 1) }), run('planwharf', String(i), { wallMs: 90 * (i + 1) })]).flat();
  const a = analyze(runs), b = analyze(runs);
  const m = a.overall.equalQualityPairs.metrics.wallMs;
  assert.deepEqual(m, b.overall.equalQualityPairs.metrics.wallMs);
  assert.equal(m.medianPairedPercentDifference, -10);
  assert.equal(m.percentInterval.lower, -10); assert.equal(m.percentInterval.upper, -10);
  assert.equal(m.percentInterval.seed, 21052026);
  assert.match(analyze(runs.slice(0, -2)).overall.equalQualityPairs.metrics.wallMs.percentInterval.status, /inconclusive/);
  runs[0].wallMs = null;
  assert.match(analyze(runs).overall.equalQualityPairs.metrics.wallMs.differenceInterval.status, /inconclusive/);
});

test('blocked and timed-out pilot includes elapsed resources but no fabricated valid completion', () => {
  const a = analyze([run('direct', 'blocked', { outcome: 'blocked', wallMs: 700, usage: null }),
    run('planwharf', 'blocked', { outcome: 'blocked', wallMs: 900, usage: null }),
    run('direct', 'timeout', { outcome: 'timed_out', wallMs: 6000, usage: null }),
    run('planwharf', 'timeout', { outcome: 'timed_out', wallMs: 6000, usage: null })]);
  assert.equal(a.overall.equalQualityPairs.acceptedPairCount, 0);
  for (const arm of Object.values(a.overall.arms)) {
    assert.equal(arm.acceptedRate, 0);
    assert.equal(arm.failureRate, .5);
    assert.equal(arm.acceptedMetrics.wallMs.median, null);
    assert.equal(arm.allRunsMetrics.wallMs.measuredCount, 2);
    assert.equal(arm.allRunsMetrics.totalTokens.sum, null);
  }
  const report = renderReport(a, { environment: { model: 'gpt-exact', quota: 'UNKNOWN' } });
  assert.match(report, /Single-model pilot/); assert.match(report, /inconclusive/);
  assert.match(report, /blocked-direct/); assert.match(report, /timed_out/);
  assert.match(report, /Actual financial savings/); assert.match(report, /UNKNOWN/);
  assert.match(report, /gpt-exact/);
});

test('recovery rates preserve unknown outcomes and include failed runs, separate from approvals', () => {
  const a = analyze([
    run('direct', 'a', { outcome: 'failed', recoveryAttempted: true, recovered: false, interventions: 2, initialApprovals: 0 }),
    run('direct', 'b', { recovery: { attempted: true, success: true }, interventions: 1 }),
    run('direct', 'c', { outcome: 'blocked', recoveryAttempted: true }),
    run('direct', 'd')
  ]);
  assert.deepEqual(a.overall.arms.direct.recovery, { attempted: 3, succeeded: 1, failed: 1, attemptStatusUnknown: 1, outcomeUnknown: 1, rate: null });
  assert.equal(a.overall.arms.direct.allRunsMetrics.interventions.sum, 3);
  assert.equal(a.overall.arms.direct.allRunsMetrics.initialApprovals.sum, 0);
});

test('sanitize evidence removes common credentials and personal paths while preserving numeric usage', () => {
  const raw = { accessToken: 'private', OPENAI_API_KEY: 'private', GH_TOKEN: 'private', clientSecret: 'private', env: { KEY: 'secret' }, inputTokens: 1234, outputTokens: 99, cachedInputTokens: 800,
    usage: { billedCost: .02 }, log: 'Authorization: Bearer abcd.123 TOKEN=private C:\\Users\\Rocco\\private\\receipt.json /home/rocco/key ghp_abcdef123456 sk-abcdefghijklmnop' };
  const safe = sanitizeEvidence(raw);
  assert.equal(safe.inputTokens, 1234); assert.equal(safe.outputTokens, 99); assert.equal(safe.cachedInputTokens, 800); assert.equal(safe.usage.billedCost, .02);
  assert.doesNotMatch(JSON.stringify(safe), /private|Rocco|rocco|abcd\.123|abcdef123456|abcdefghijklmnop/);
  assert.match(safe.log, /REDACTED/);
  assert.equal(raw.accessToken, 'private', 'Original local evidence is not mutated');
});

test('empty data remains inconclusive; ambiguous identities and invalid outcomes are rejected', () => {
  const a = analyze([]);
  assert.equal(a.overall.arms.direct.acceptedRate, null);
  assert.equal(a.overall.arms.planwharf.allRunsMetrics.invocations.sum, null);
  assert.match(renderReport(a), /inconclusive/);
  assert.throws(() => analyze([run('direct', 'a'), run('direct', 'a')]), /unique/);
  assert.throws(() => analyze([run('direct', '', { pairId: null })]), /requires/);
  assert.throws(() => analyze([run('direct', 'a', { outcome: 'complete' })]), /outcome/);
});

test('Rust-style escaped and mixed Windows separators do not expose diagnostic user paths', () => {
  const evidence = { diagnostic: String.raw`error creating C:\\Users\\HP\\.codex\\tmp\\receipt.json; C:\Users\Rocco\.codex\tmp`,
    authStatus: String.raw`failed to open C://Users//HP//.codex//auth.json and D:\\/Users/\\Rocco\\secret`,
    usage: { inputTokens: 3421, cachedInputTokens: 1700, reasoningTokens: 53 }, log: '//home//rocco//private/config' };
  const safe = sanitizeEvidence(evidence);
  assert.doesNotMatch(JSON.stringify(safe), /HP|Rocco|rocco|\.codex|receipt\.json|auth\.json/);
  assert.deepEqual(safe.usage, evidence.usage);
  assert.match(safe.diagnostic, /REDACTED USER PATH/);
});

test('plain report remains compact for an all-blocked three-category pilot', () => {
  const runs = ['small', 'medium', 'complex'].flatMap(category => ['direct', 'planwharf'].map(arm => run(arm, category, { category, outcome: 'blocked', usage: null })));
  const a = analyze(runs), report = renderReport(a);
  assert.ok(report.split('\n').length < 130, 'Human report should not dump full category JSON or repeated availability objects');
  assert.match(report, /UNKNOWN measurement reasons/);
  assert.match(report, /blocked=1/);
  for (const row of a.runs) assert.ok(report.includes(row.id));
  assert.ok(a.categories.complex.arms.planwharf.allRunsMetrics.wallMs, 'Machine analysis retains full detail');
});


test('publication removes signed URL queries in parsed and raw native diagnostics', () => {
  const link = 'https://example.com/file?se=2030-01-01&sig=sensitive%2Bsignature';
  for (const raw of [link, JSON.stringify({ diagnostic: link })]) {
    const safe = sanitizeEvidence(raw);
    assert.ok(!safe.includes('sensitive'));
    assert.ok(!safe.includes('sig='));
    assert.ok(safe.includes('https://example.com/file?[REDACTED QUERY]'));
  }
  const nested = JSON.stringify({ diagnostic: JSON.stringify({ url: link }) });
  assert.doesNotThrow(() => JSON.parse(sanitizeEvidence(nested)));
  assert.equal(JSON.parse(JSON.parse(sanitizeEvidence(nested)).diagnostic).url, 'https://example.com/file?[REDACTED QUERY]');
  assert.equal(sanitizeEvidence('https://example.com/docs'), 'https://example.com/docs');
  assert.equal(sanitizeEvidence({ diagnostic: link, inputTokens: 123 }).inputTokens, 123);
});
