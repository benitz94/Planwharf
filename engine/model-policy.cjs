'use strict';

const DEFAULT_POLICY = Object.freeze({ minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null, alwaysApprove: false });
function migratePolicy(value) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Invalid quota policy.');
  const migrated = { ...value };
  for (const [oldKey, newKey] of [['maxUsedPercent', 'minRemainingPercent'], ['warningUsedPercent', 'warningRemainingPercent']]) {
    if (Object.hasOwn(migrated, oldKey)) {
      const previous = migrated[oldKey];
      if (Object.hasOwn(migrated, newKey)) throw new Error('Do not mix consumed and remaining quota settings.');
      if (previous !== null && (!Number.isFinite(previous) || previous < 0 || previous > 100)) throw new Error('Invalid legacy usage percentage.');
      migrated[newKey] = previous === null ? null : 100 - previous;
      delete migrated[oldKey];
    }
  }
  return migrated;
}
function validatePolicy(value) {
  value = migratePolicy(value);
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(key => !Object.hasOwn(DEFAULT_POLICY, key))) throw new Error('Invalid model policy fields.');
  const policy = { ...DEFAULT_POLICY, ...value };
  for (const key of ['minRemainingPercent', 'warningRemainingPercent']) if (policy[key] !== null && (!Number.isFinite(policy[key]) || policy[key] < 0 || policy[key] > 100)) throw new Error('Remaining quota thresholds must be percentages from 0 to 100, or null.');
  if (policy.warningRemainingPercent !== null && (policy.minRemainingPercent === null || policy.warningRemainingPercent <= policy.minRemainingPercent)) throw new Error('Warning remaining quota must be higher than the stop threshold.');
  if (typeof policy.alwaysApprove !== 'boolean') throw new Error('Always approve must be a boolean.');
  if (policy.fallbackModelId !== null && (typeof policy.fallbackModelId !== 'string' || !policy.fallbackModelId.trim())) throw new Error('Fallback must be an explicit model ID, or null.');
  return policy;
}
function usagePolicy(model, plans = []) {
  const policy = validatePolicy(model.policy || {});
  const plan = plans.find(plan => plan.id === (model.quotaPoolId || model.plan));
  const percent = value => Number.isFinite(value) && value >= 0 && value <= 100;
  const left = plan?.readable === true && percent(plan.leftPct) ? plan.leftPct : plan?.readable === true && percent(plan.pct) ? 100 - plan.pct : null;
  const usedPercent = left === null ? null : Math.max(0, Math.min(100, 100 - left));
  const warningRemainingPercent = policy.warningRemainingPercent ?? (policy.minRemainingPercent !== null && policy.minRemainingPercent < 100 ? Math.min(100, policy.minRemainingPercent + 10) : null);
  const unread = left === null && policy.minRemainingPercent !== null;
  const reached = left !== null && policy.minRemainingPercent !== null && left <= policy.minRemainingPercent;
  const near = left !== null && warningRemainingPercent !== null && left <= warningRemainingPercent;
  return { ...policy, warningRemainingPercent, remainingPercent: left, usedPercent, blocked: unread || reached, state: unread ? 'unread' : reached ? 'limit' : near ? 'warning' : 'ok',
    reason: unread ? 'Configured remaining quota threshold cannot be verified: provider quota is unread.' : reached ? `Remaining quota stop threshold reached (${left.toFixed(1)}% remaining / stop at ${policy.minRemainingPercent}%).` : near ? `Low provider quota (${left.toFixed(1)}% remaining / warning at ${warningRemainingPercent}%).` : null };
}
function resolveUsageFallback(model, models, plans, eligible) {
  const seen = new Set();
  let current = model;
  while (current && !seen.has(current.id)) {
    seen.add(current.id);
    if (!current.enabled || current.available === false || current.providerEnabled === false || !eligible(current)) return null;
    const status = usagePolicy(current, plans);
    const exhausted = plans.some(plan => plan.id === (current.quotaPoolId || current.plan) && plan.readable && (plan.exhausted || plan.leftPct === 0));
    if (status.state === 'unread') return null;
    if (!status.blocked && !exhausted) return eligible(current) ? current : null;
    if (current.fallbackProviderId === '@automatic') {
      const sourceProvider = current.providerId || current.provider;
      const sourcePool = current.quotaPoolId || current.plan;
      current = models.find(other => (other.providerId || other.provider) !== sourceProvider && (!sourcePool || (other.quotaPoolId || other.plan) !== sourcePool) && !seen.has(other.id) && other.enabled && other.available !== false && other.providerEnabled !== false && eligible(other)
        && !usagePolicy(other, plans).blocked && !plans.some(plan => plan.id === (other.quotaPoolId || other.plan) && plan.readable && (plan.exhausted || plan.leftPct === 0)));
    } else if (current.fallbackProviderId) {
      const candidates = models.filter(other => (other.providerId || other.provider) === current.fallbackProviderId && !seen.has(other.id) && other.enabled && other.available !== false && other.providerEnabled !== false && eligible(other));
      current = candidates.find(other => !usagePolicy(other, plans).blocked && !plans.some(plan => plan.id === (other.quotaPoolId || other.plan) && plan.readable && (plan.exhausted || plan.leftPct === 0))) || candidates[0];
    } else current = models.find(other => other.id === status.fallbackModelId);
  }
  return null;
}
// Automatic model switch when a model's quota runs low: on by default, at 35% left on the 5-hour
// window and on the weekly window. thresholdPercent is the higher of the two, for old callers.
const AUTO_SWITCH_DEFAULT = Object.freeze({ enabled: true, thresholdPercent: 35, fiveHourPercent: 35, weeklyPercent: 35 });

function quotaLeft(model, plans = []) {
  const plan = plans.find(item => item && item.id === (model.quotaPoolId || model.plan));
  if (!plan || plan.readable !== true) return null;
  if (Number.isFinite(plan.leftPct)) return plan.leftPct;
  return Number.isFinite(plan.pct) ? 100 - plan.pct : null;
}

// Providers name their windows differently ("5-hour", "five_hour", "weekly", "seven_day"…).
function windowKind(name) {
  const text = String(name || '').toLowerCase();
  if (/week|seven|7.?d/.test(text)) return 'weekly';
  if (/5|five|hour/.test(text)) return 'fiveHour';
  return 'other';
}
const percentOr = (value, fallback) => Number.isFinite(value) && value >= 0 && value <= 100 ? value : fallback;
// A model's quota limit: the two thresholds, or none at all (a model the user wants used whatever its quota).
// Takes a number (both windows), an object, or a function of the model that returns one of these.
function quotaLimit(value, model) {
  if (typeof value === 'function') value = value(model);
  if (value && typeof value === 'object') {
    if (value.none === true) return { none: true, fiveHourPercent: null, weeklyPercent: null };
    const both = percentOr(value.thresholdPercent, null);
    return { none: false, fiveHourPercent: percentOr(value.fiveHourPercent, both ?? AUTO_SWITCH_DEFAULT.fiveHourPercent), weeklyPercent: percentOr(value.weeklyPercent, both ?? AUTO_SWITCH_DEFAULT.weeklyPercent) };
  }
  const both = percentOr(value, null);
  return { none: false, fiveHourPercent: both ?? AUTO_SWITCH_DEFAULT.fiveHourPercent, weeklyPercent: both ?? AUTO_SWITCH_DEFAULT.weeklyPercent };
}
// What is left on each window, read from the provider (null when not reported).
function quotaWindows(model, plans = []) {
  const plan = plans.find(item => item && item.id === (model.quotaPoolId || model.plan));
  const out = { fiveHour: null, weekly: null };
  if (!plan || plan.readable !== true) return out;
  for (const window of Array.isArray(plan.windows) ? plan.windows : []) {
    const kind = windowKind(window?.name);
    if (kind !== 'other' && Number.isFinite(window.leftPct)) out[kind] = out[kind] === null ? window.leftPct : Math.min(out[kind], window.leftPct);
  }
  return out;
}

// Low means exhausted, at the provider's own stop threshold, or at or under the switch threshold of
// either window (5-hour or weekly). Unread quota is not low: nothing is switched on a guess. A model
// with no limit is low only when its quota is really exhausted.
function lowQuota(model, plans = [], limit = AUTO_SWITCH_DEFAULT) {
  const plan = plans.find(item => item && item.id === (model.quotaPoolId || model.plan));
  if (plan?.readable === true && plan.exhausted) return true;
  if (usagePolicy(model, plans).state === 'limit') return true;
  const own = quotaLimit(limit, model);
  if (own.none || plan?.readable !== true) return false;
  const windows = (Array.isArray(plan.windows) ? plan.windows : []).filter(window => window && Number.isFinite(window.leftPct));
  const highest = Math.max(own.fiveHourPercent, own.weeklyPercent);
  if (windows.length) return windows.some(window => window.leftPct <= ({ fiveHour: own.fiveHourPercent, weekly: own.weeklyPercent }[windowKind(window.name)] ?? highest));
  const left = quotaLeft(model, plans);
  return left !== null && left <= highest;
}

// Which working steps should move, and to which model: the eligible one with the most quota left above the threshold.
function quotaSwitches({ running = [], models = [], plans = [], thresholdPercent = AUTO_SWITCH_DEFAULT, enabled = true, eligible = () => true } = {}) {
  if (!enabled) return [];
  const out = [];
  for (const step of running) {
    const current = models.find(model => model.id === step.modelId);
    if (!current || !lowQuota(current, plans, thresholdPercent)) continue;
    const best = models
      .filter(model => model.id !== current.id && model.enabled && model.available !== false && model.providerEnabled !== false && eligible(model, step))
      .map(model => ({ model, left: quotaLeft(model, plans) }))
      // A model whose quota is not reported (Grok Business bills in dollars) can take over too, after
      // the ones with quota read above their threshold.
      .filter(item => !lowQuota(item.model, plans, thresholdPercent))
      // A company the other steps of the work do not use comes first, so build and check stay independent;
      // then quota that is read before quota that is not, then the most quota left.
      .map(item => ({ ...item, shared: (step.avoidProviders || []).includes(item.model.providerId || item.model.provider) }))
      .sort((a, b) => (a.shared - b.shared) || ((a.left === null) - (b.left === null)) || ((b.left ?? 0) - (a.left ?? 0)))[0];
    if (best) out.push({ subTaskId: step.subTaskId, fromModelId: current.id, toModelId: best.model.id, leftPercent: quotaLeft(current, plans) });
  }
  return out;
}

// A model set unavailable by a failed call is tried again after a while, not left out for the whole session.
const RETRY_UNAVAILABLE_MS = 5 * 60000;
function availableAgain(model, now = Date.now(), retryMs = RETRY_UNAVAILABLE_MS) {
  if (!model || model.available !== false || !model.unavailableAt) return false;
  const since = Date.parse(model.unavailableAt);
  return Number.isFinite(since) && now - since >= retryMs;
}

module.exports = { DEFAULT_POLICY, migratePolicy, validatePolicy, usagePolicy, resolveUsageFallback, AUTO_SWITCH_DEFAULT, quotaLeft, quotaLimit, quotaWindows, windowKind, lowQuota, quotaSwitches, availableAgain, RETRY_UNAVAILABLE_MS };
