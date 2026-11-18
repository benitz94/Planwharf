'use strict';

// Only specific provider responses can establish a subscription state. Quota
// exhaustion and transport errors do not imply that a subscription expired.
const expired = {
  claude: /(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)|subscription required/i,
  codex: /(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)|your plan has expired/i,
  grok: /(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)/i,
  antigravity: /(?:subscription|plan) (?:is |has )?(?:expired|inactive|not active)/i
};
const signedOut = {
  claude: /(?:not logged in|login expired|sign in to claude|subscription credentials are unavailable)/i,
  codex: /(?:not logged in|login expired|sign in with codex)/i,
  grok: /(?:not logged in|login expired|sign in with grok)/i,
  antigravity: /(?:not logged in|login expired|sign in to agy)/i
};
function providerKey(provider) {
  const key = String(provider || '').split('@')[0];
  return expired[key] ? key : String(provider || '');
}
function statusFrom(provider, response) {
  if (response?.readable === true) return 'active';
  const key = providerKey(provider);
  const text = String(response?.reason || response?.error || '');
  if (expired[key]?.test(text)) return 'expired';
  if (signedOut[key]?.test(text)) return 'not signed in';
  return 'unknown';
}
function notePlanRefusal(plans, model, reason) {
  const list = Array.isArray(plans) ? plans.map(plan => ({ ...plan })) : [];
  const status = statusFrom(model?.adapter || model?.provider || model?.providerId, { reason });
  if (!model || (status !== 'expired' && status !== 'not signed in')) return { plans: list, status: null };
  const pool = model.quotaPoolId || model.plan;
  if (typeof pool !== 'string' || !pool) return { plans: list, status: null };
  const row = { id: pool, planStatus: status, readable: false, exhausted: false, leftPct: null, pct: null, windows: [],
    reason: `${model.companyId || model.providerId || model.provider || pool} plan ${status}.` };
  const index = list.findIndex(plan => plan && plan.id === pool);
  if (index < 0) list.push(row);
  else list[index] = { ...list[index], ...row };
  return { plans: list, status };
}
function mergePlans(previous, next) {
  const incoming = (Array.isArray(next) ? next : []).filter(plan => plan && typeof plan.id === 'string');
  const prior = new Map((Array.isArray(previous) ? previous : []).filter(plan => plan && typeof plan.id === 'string').map(plan => [plan.id, plan]));
  const merged = incoming.map(plan => {
    const old = prior.get(plan.id);
    if (!old || plan.readable === true || plan.planStatus === 'active' || plan.planStatus === 'expired' || plan.planStatus === 'not signed in') return plan;
    if (old.planStatus !== 'expired' && old.planStatus !== 'not signed in') return plan;
    return { ...plan, planStatus: old.planStatus, reason: old.reason || plan.reason };
  });
  const seen = new Set(merged.map(plan => plan.id));
  for (const old of prior.values()) {
    if (seen.has(old.id) || (old.planStatus !== 'expired' && old.planStatus !== 'not signed in')) continue;
    merged.push({ ...old, readable: false, exhausted: false, leftPct: null, pct: null, windows: [] });
  }
  return merged;
}
function withStatus(provider, response) {
  return { ...response, planStatus: statusFrom(provider, response) };
}
function blockedStatus(plan) { return plan?.planStatus === 'expired' || plan?.planStatus === 'not signed in'; }
function providerError(provider, body, label) {
  const text = typeof body === 'string' ? body.slice(0, 8192) : JSON.stringify(body || {}).slice(0, 8192);
  const state = statusFrom(provider, { reason: text });
  if (state === 'expired') return { planStatus: state, reason: `${label} plan expired.` };
  if (state === 'not signed in') return { planStatus: state, reason: `${label} is not signed in.` };
  return null;
}
module.exports = { statusFrom, withStatus, blockedStatus, providerError, notePlanRefusal, mergePlans };
