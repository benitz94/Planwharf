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
function statusFrom(provider, response) {
  if (response?.readable === true) return 'active';
  const text = String(response?.reason || response?.error || '');
  if (expired[provider]?.test(text)) return 'expired';
  if (signedOut[provider]?.test(text)) return 'not signed in';
  return 'unknown';
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
module.exports = { statusFrom, withStatus, blockedStatus, providerError };
