'use strict';
// Recognises a provider refusing work for credits, quota or a usage limit, so Planwharf
// can route around that model instead of stopping. Only the end of an output is read:
// a refusal is the last thing a provider prints, and earlier text may quote these words.
const PATTERNS = [
  /requires usage credits/i,
  /you(?:'|’)ve hit your [^.\n]*limit/i,
  /usage limit (?:reached|exceeded)/i,
  /(?:quota|credits?) (?:exceeded|exhausted|depleted)/i,
  /insufficient (?:credits|quota|balance)/i,
  /out of (?:credits|quota)/i,
  /rate limit (?:reached|exceeded)/i,
  /resource[_ ]exhausted/i,
  /\b429\b[^\n]{0,80}(?:too many requests|rate|quota)/i
];
const COOLDOWN_MS = 60 * 60 * 1000;

function refusalReason(text) {
  const tail = String(text || '').slice(-4000);
  for (const pattern of PATTERNS) {
    const match = pattern.exec(tail);
    if (match) {
      const start = tail.lastIndexOf('\n', match.index) + 1;
      const end = tail.indexOf('\n', match.index);
      return tail.slice(start, end < 0 ? undefined : end).replace(/\s+/g, ' ').trim().slice(0, 240) || match[0];
    }
  }
  return null;
}

// store.refusals: { [modelId]: { reason, at, until } }. Expired entries are removed.
function activeRefusal(store, modelId, nowMs) {
  const row = store?.refusals?.[modelId];
  if (!row) return null;
  if (!(Date.parse(row.until) > nowMs)) { delete store.refusals[modelId]; return null; }
  return row;
}

function recordRefusal(store, modelId, reason, nowIso, cooldownMs = COOLDOWN_MS) {
  store.refusals ||= {};
  const until = new Date(Date.parse(nowIso) + cooldownMs).toISOString();
  store.refusals[modelId] = { reason: String(reason).slice(0, 240), at: nowIso, until };
  return store.refusals[modelId];
}

module.exports = { refusalReason, activeRefusal, recordRefusal, COOLDOWN_MS };
