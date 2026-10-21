'use strict';

// Analysis never treats a missing receipt as zero, or a failed run as a fast success.
const ARMS = ['direct', 'planwharf'];
const OUTCOMES = ['accepted', 'defective', 'failed', 'blocked', 'timed_out'];
const USAGE_FIELDS = ['inputTokens', 'outputTokens', 'cachedInputTokens', 'reasoningTokens', 'billedCost'];
const METRICS = ['wallMs', 'totalTokens', ...USAGE_FIELDS, 'invocations', 'interventions', 'initialApprovals', 'restarts', 'failedAttempts', 'modelMs', 'waitingMs', 'retryMs', 'cooldownMs', 'launchMs', 'followUpMs', 'investigationMs', 'recoveryMs', 'quotaDecrement'];
const measured = value => typeof value === 'number' && Number.isFinite(value) && value >= 0;
const median = values => {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b), middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
};
const availability = (value, reason) => ({ value: measured(value) ? value : null, availability: measured(value) ? 'measured' : reason });

function sanitizeEvidence(value) {
  if (Array.isArray(value)) return value.map(sanitizeEvidence);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([key, item]) => [key,
    /^(?:authorization|cookie|set-cookie|password|secret|token|api[_-]?key|access[_-]?token|refresh[_-]?token|id[_-]?token|env|environmentVariables)$|(?:[_-](?:API[_-]?KEY|TOKEN|SECRET|PASSWORD)$)|(?:clientSecret$)/i.test(key) ? '[REDACTED]' : sanitizeEvidence(item)]));
  if (typeof value !== 'string') return value;
  return value
    // Native plugin diagnostics may contain signed download URLs.
    .replace(/(\bhttps?:\/\/[^\\\s"'<>?]+)\?[^\\\s"'<>]*/gi, '$1?[REDACTED QUERY]')
    .replace(/\b(?:Bearer|Basic)\s+[A-Za-z0-9+/_=.-]+/gi, '[REDACTED AUTH]')
    .replace(/\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|sk-[A-Za-z0-9_-]{12,})\b/g, '[REDACTED SECRET]')
    .replace(/\b((?:[A-Z_]*(?:TOKEN|SECRET|PASSWORD|API_KEY)|authorization)\s*[=:]\s*)[^\s,;]+/gi, '$1[REDACTED]')
    .replace(/[A-Za-z]:[\\/]+Users[\\/]+[^\s"'<>]+/gi, '[REDACTED USER PATH]')
    .replace(/\/+(?:home|Users)\/+[^\s"'<>]+/g, '[REDACTED USER PATH]');
}

function usageFor(run) {
  const unknown = reason => Object.fromEntries([...USAGE_FIELDS, 'totalTokens'].map(field => [field, availability(null, reason)]));
  if (run.arm === 'planwharf' && run.usageScope !== 'all_calls') return unknown('UNKNOWN: complete planning, coordination, worker, review, retry and recovery call accounting not established');
  let usage = run.usage;
  if (Array.isArray(run.calls)) {
    if (!measured(run.invocations) || run.calls.length !== run.invocations) return unknown('UNKNOWN: call receipts do not cover the measured invocation count');
    usage = Object.fromEntries(USAGE_FIELDS.map(field => [field,
      run.calls.every(call => measured(call.usage?.[field])) ? run.calls.reduce((sum, call) => sum + call.usage[field], 0) : null]));
  }
  const result = Object.fromEntries(USAGE_FIELDS.map(field => [field, availability(usage?.[field], 'UNKNOWN: provider did not expose a reliable complete measurement')]));
  if (result.cachedInputTokens.value !== null && result.inputTokens.value !== null && result.cachedInputTokens.value > result.inputTokens.value) {
    result.inputTokens = availability(null, 'UNKNOWN: cached input exceeds input receipt; inconsistent usage');
    result.cachedInputTokens = availability(null, 'UNKNOWN: cached input exceeds input receipt; inconsistent usage');
  }
  // Cached input is a subset of input; reasoning is a reported subset of output.
  const input = result.inputTokens.value, output = result.outputTokens.value;
  result.totalTokens = availability(input !== null && output !== null ? input + output : null, 'UNKNOWN: input and output totals are required');
  return result;
}

function normalizeRun(run) {
  if (!ARMS.includes(run.arm) || !OUTCOMES.includes(run.outcome) || !run.id || !run.taskId || !run.pairId) throw Error('Each run requires id, taskId, pairId, a valid arm and a valid outcome');
  const metrics = Object.fromEntries(METRICS.map(field => [field, availability(run[field], 'UNKNOWN: not measured')]));
  Object.assign(metrics, usageFor(run));
  if (run.arm === 'planwharf' && run.usageScope !== 'all_calls') {
    for (const field of ['invocations', 'quotaDecrement']) metrics[field] = availability(null, 'UNKNOWN: all-role call accounting not established');
  }
  const recovery = run.recovery || {};
  return { ...sanitizeEvidence(run), category: run.category || run.taskId, metrics,
    recoveryAttempted: typeof run.recoveryAttempted === 'boolean' ? run.recoveryAttempted : typeof recovery.attempted === 'boolean' ? recovery.attempted : null,
    recovered: typeof run.recovered === 'boolean' ? run.recovered : typeof recovery.success === 'boolean' ? recovery.success : null,
    providerInternalInferences: { value: null, availability: 'UNKNOWN: agent invocation counts do not expose provider internal inference calls' } };
}

function metricSummary(runs, field) {
  const values = runs.map(run => run.metrics[field].value).filter(value => value !== null);
  return { median: median(values), sum: values.length === runs.length && runs.length ? values.reduce((sum, value) => sum + value, 0) : null,
    measuredCount: values.length, unknownCount: runs.length - values.length,
    availability: !runs.length ? 'UNKNOWN: no runs' : values.length !== runs.length ? 'UNKNOWN for missing runs; median describes measured subset only' : 'measured',
    min: values.length ? Math.min(...values) : null, max: values.length ? Math.max(...values) : null };
}

function armSummary(runs) {
  const outcomes = Object.fromEntries(OUTCOMES.map(outcome => [outcome, runs.filter(run => run.outcome === outcome).length]));
  const accepted = runs.filter(run => run.outcome === 'accepted');
  const attempted = runs.filter(run => run.recoveryAttempted === true), knownRecoveries = attempted.filter(run => run.recovered !== null);
  return { runCount: runs.length, outcomes, nonAcceptedCount: runs.length - accepted.length,
    acceptedRate: runs.length ? accepted.length / runs.length : null,
    failureRate: runs.length ? (outcomes.failed + outcomes.timed_out) / runs.length : null,
    recovery: { attempted: attempted.length, succeeded: knownRecoveries.filter(run => run.recovered).length, failed: knownRecoveries.filter(run => !run.recovered).length,
      attemptStatusUnknown: runs.filter(run => run.recoveryAttempted === null).length, outcomeUnknown: attempted.length - knownRecoveries.length,
      rate: attempted.length && knownRecoveries.length === attempted.length ? knownRecoveries.filter(run => run.recovered).length / attempted.length : null },
    allRunsMetrics: Object.fromEntries(METRICS.map(field => [field, metricSummary(runs, field)])),
    acceptedMetrics: Object.fromEntries(METRICS.map(field => [field, metricSummary(accepted, field)])) };
}

function seededRandom(seed) {
  let state = seed >>> 0;
  return () => { state += 0x6D2B79F5; let n = state; n = Math.imul(n ^ n >>> 15, n | 1); n ^= n + Math.imul(n ^ n >>> 7, n | 61); return ((n ^ n >>> 14) >>> 0) / 4294967296; };
}
function interval(values) {
  if (values.length < 5) return { lower: null, upper: null, status: 'inconclusive: fewer than five accepted pairs with comparable measurements' };
  const random = seededRandom(21052026), samples = [];
  for (let i = 0; i < 2000; i++) samples.push(median(Array.from({ length: values.length }, () => values[Math.floor(random() * values.length)])));
  samples.sort((a, b) => a - b);
  return { lower: samples[49], upper: samples[1949], status: 'exploratory paired percentile bootstrap; 95% interval', seed: 21052026, resamples: 2000 };
}
function pairedSummary(runs) {
  const groups = new Map();
  for (const run of runs) { const key = JSON.stringify([run.taskId, run.pairId]); if (!groups.has(key)) groups.set(key, []); groups.get(key).push(run); }
  const pairs = [], excluded = [];
  for (const rows of groups.values()) {
    const direct = rows.filter(run => run.arm === 'direct'), planwharf = rows.filter(run => run.arm === 'planwharf');
    const reason = direct.length > 1 || planwharf.length > 1 ? 'duplicate arm records' : !direct.length || !planwharf.length ? 'missing arm' : direct[0].outcome !== 'accepted' || planwharf[0].outcome !== 'accepted' ? 'unequal acceptance threshold: both arms must be independently accepted' : null;
    if (reason) { excluded.push({ taskId: rows[0].taskId, pairId: rows[0].pairId, reason, runIds: rows.map(run => run.id) }); continue; }
    pairs.push({ taskId: rows[0].taskId, pairId: rows[0].pairId, direct: direct[0], planwharf: planwharf[0] });
  }
  const metrics = Object.fromEntries(METRICS.map(field => {
    const comparable = pairs.filter(pair => pair.direct.metrics[field].value !== null && pair.planwharf.metrics[field].value !== null);
    const differences = comparable.map(pair => pair.planwharf.metrics[field].value - pair.direct.metrics[field].value);
    const percent = comparable.filter(pair => pair.direct.metrics[field].value > 0).map(pair => 100 * (pair.planwharf.metrics[field].value - pair.direct.metrics[field].value) / pair.direct.metrics[field].value);
    return [field, { comparablePairs: comparable.length, unknownPairs: pairs.length - comparable.length,
      directMedian: median(comparable.map(pair => pair.direct.metrics[field].value)), planwharfMedian: median(comparable.map(pair => pair.planwharf.metrics[field].value)),
      medianPairedDifference: median(differences), medianPairedPercentDifference: median(percent), percentDenominatorPairs: percent.length,
      zeroDenominatorPairs: comparable.length - percent.length, differenceInterval: interval(differences), percentInterval: interval(percent) }];
  }));
  return { acceptedPairCount: pairs.length, acceptedPairs: pairs.map(({ taskId, pairId, direct, planwharf }) => ({ taskId, pairId, directRunId: direct.id, planwharfRunId: planwharf.id })), excludedPairs: excluded, metrics };
}

function analyze(input) {
  if (!Array.isArray(input)) throw Error('Runs must be an array');
  const runs = input.map(normalizeRun);
  if (new Set(runs.map(run => run.id)).size !== runs.length) throw Error('Run IDs must be unique');
  const summarize = rows => ({ arms: Object.fromEntries(ARMS.map(arm => [arm, armSummary(rows.filter(run => run.arm === arm))])), equalQualityPairs: pairedSummary(rows) });
  return { schema: 1, study: 'Single-model pilot; observed associations only, no universal performance or causal claim',
    conventions: { percentDifference: '100 * (Planwharf - direct) / direct, per accepted pair; negative favors Planwharf for resource metrics',
      tokenTotal: 'inputTokens + outputTokens; cached input and reasoning are separately reported subsets, never added again',
      invocation: 'Agent CLI invocations across all roles; provider internal inference count UNKNOWN',
      usage: 'Planwharf requires usageScope=all_calls; missing receipts stay UNKNOWN. Billed cost is actual supplied cost only.',
      pairedTime: 'Accepted-pair elapsed wall time only; all incomplete elapsed time and consumption remain in allRunsMetrics' },
    overall: summarize(runs), categories: Object.fromEntries([...new Set(runs.map(run => run.category))].map(category => [category, summarize(runs.filter(run => run.category === category))])),
    runs, limitations: [
      'A pilot cannot establish universal savings; provider load, caching, order, quota and configuration differences can confound comparisons.',
      'Acceptance is an independent quality threshold, not proof that all accepted artifacts have identical maintainability, coverage or security.',
      'Bootstrap intervals are exploratory, conditional on accepted measured pairs, and do not correct selection bias or multiple comparisons.',
      'Unknown measurements are not zero. Measured-subset medians are labeled; incomplete totals are UNKNOWN.',
      'Quota and subscription price cannot be converted into per-task cost. No monetary savings claim follows from tokens alone.',
      'Sanitization is best effort; review receipts manually before publishing. Redaction cannot guarantee detection of arbitrary secrets.' ] };
}

const display = value => value === null || value === undefined ? 'UNKNOWN' : String(Math.round(value * 100) / 100);
function renderReport(analysis, options = {}) {
  const pairs = analysis.overall.equalQualityPairs, arms = analysis.overall.arms;
  const comparison = field => {
    const item = pairs.metrics[field];
    return `${item.comparablePairs} accepted measured pairs; direct median ${display(item.directMedian)}, Planwharf median ${display(item.planwharfMedian)}; median paired difference ${display(item.medianPairedDifference)}, percent difference ${display(item.medianPairedPercentDifference)}%. ${item.percentInterval.status}.`;
  };
  const recommendations = [];
  for (const [category, summary] of Object.entries(analysis.categories)) {
    const m = summary.equalQualityPairs.metrics;
    if (m.wallMs.medianPairedDifference > 0) recommendations.push(`${category}: observed Planwharf elapsed-time overhead warrants profiling planning, approval, launch and review stages in a separate follow-up.`);
    if (m.invocations.medianPairedDifference > 0) recommendations.push(`${category}: more measured agent invocations warrant examining redundant planning/review calls after freezing this baseline.`);
    if (summary.arms.planwharf.nonAcceptedCount) recommendations.push(`${category}: inspect the retained Planwharf nonaccepted runs before proposing performance changes.`);
  }
  if (!recommendations.length) recommendations.push('No measured disadvantage supports a specific optimization yet; collect complete paired receipts and independent acceptance results before choosing changes.');
  const lines = [ 'CONTROLLED SINGLE-MODEL BENCHMARK', analysis.study, '', 'Executive summary',
    `1. Faster? ${comparison('wallMs')}`,
    `2. Fewer tokens? ${comparison('totalTokens')}`,
    `3. Fewer human interventions? ${comparison('interventions')} Initial approvals are reported separately.`,
    `4. Better completion reliability? Direct ${arms.direct.outcomes.accepted}/${arms.direct.runCount}; Planwharf ${arms.planwharf.outcomes.accepted}/${arms.planwharf.runCount} accepted. All outcomes below remain included; small samples do not establish an advantage.`,
    '5. Better quality? Independent acceptance outcomes are reported; quality dimensions without independent evidence remain UNKNOWN.',
    `6. When worse? ${recommendations.join(' ')}`,
    `7. Actual financial savings? ${comparison('billedCost')} Only supplied actual billed receipts count; a fixed subscription is not a per-task expense.`,
    '', 'Environment and protocol', JSON.stringify(sanitizeEvidence(options), null, 2), '', 'All trial records (including failures, blocks and timeouts)',
    'id | task | pair | arm | outcome | wallMs | totalTokens | invocations | interventions | initialApprovals | restarts | quotaDecrement' ];
  for (const run of analysis.runs) lines.push([run.id, run.taskId, run.pairId, run.arm, run.outcome, ...['wallMs', 'totalTokens', 'invocations', 'interventions', 'initialApprovals', 'restarts', 'quotaDecrement'].map(field => display(run.metrics[field].value))].join(' | '));
  lines.push('', 'Category aggregates and paired comparisons');
  for (const [category, summary] of Object.entries(analysis.categories)) {
    lines.push(category);
    for (const arm of ARMS) {
      const stats = summary.arms[arm], recovery = stats.recovery;
      lines.push(`${arm}: ${stats.runCount} runs; ${Object.entries(stats.outcomes).map(([outcome, count]) => `${outcome}=${count}`).join(', ')}; acceptance rate ${display(stats.acceptedRate)}; recovery ${recovery.succeeded}/${recovery.attempted} attempts, outcome unknown=${recovery.outcomeUnknown}, attempt status unknown=${recovery.attemptStatusUnknown}.`);
    }
    lines.push(`Accepted pairs=${summary.equalQualityPairs.acceptedPairCount}; excluded pairs=${summary.equalQualityPairs.excludedPairs.length}.`,
      'metric | measured pairs | direct median | Planwharf median | paired difference | paired percent difference | percent interval');
    for (const field of ['wallMs', 'totalTokens', 'invocations', 'interventions', 'initialApprovals', 'restarts', 'billedCost', 'quotaDecrement']) {
      const item = summary.equalQualityPairs.metrics[field], ci = item.percentInterval;
      lines.push([field, item.comparablePairs, display(item.directMedian), display(item.planwharfMedian), display(item.medianPairedDifference), display(item.medianPairedPercentDifference), ci.lower === null ? 'inconclusive' : `[${display(ci.lower)}, ${display(ci.upper)}]`].join(' | '));
    }
  }
  const unknownReasons = new Map();
  for (const run of analysis.runs) for (const [field, metric] of Object.entries(run.metrics)) {
    if (metric.value !== null) continue;
    if (!unknownReasons.has(metric.availability)) unknownReasons.set(metric.availability, { count: 0, fields: new Set() });
    const reason = unknownReasons.get(metric.availability); reason.count++; reason.fields.add(field);
  }
  lines.push('', 'UNKNOWN measurement reasons (complete per-run availability remains in analysis JSON)',
    ...[...unknownReasons].map(([reason, detail]) => `- ${reason}: ${detail.count} run-metric cells; fields ${[...detail.fields].join(', ')}.`),
    '', 'Limitations', ...analysis.limitations.map(item => '- ' + item), '', 'Recommendations (separate future phase)', ...recommendations.map(item => '- ' + item));
  return sanitizeEvidence(lines.join('\n')) + '\n';
}

module.exports = { analyze, renderReport, sanitizeEvidence, median };
