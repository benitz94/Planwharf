'use strict';
// Project autonomy policy. The owner's setting grants authority; model text only
// proposes the next objective and is never parsed into any other action.

const LIMITS = Object.freeze({ maxMegaTasks: 5, maxNudgesPerHour: 3, maxObjectiveChars: 4000 });
const HOUR_MS = 3600000;

function createSettings(sessionId, at) {
  return { enabled: true, sessionId, enabledAt: at, started: false, approved: [], evaluated: [], nudgedKeys: [], nudgeTimes: [], assumptions: [], stopReason: null };
}

// Rewrites every push URL to a helper that does not exist. A deterrent for
// accidental pushes, not a sandbox: a process can override its own Git config.
function pushGuardEnvironment(env = process.env) {
  const index = Number.parseInt(env.GIT_CONFIG_COUNT, 10) || 0;
  return {
    GIT_CONFIG_COUNT: String(index + 1),
    [`GIT_CONFIG_KEY_${index}`]: 'url.planwharf-push-blocked://.pushInsteadOf',
    [`GIT_CONFIG_VALUE_${index}`]: ''
  };
}

function nextStep({ mode, sessionId, settings, row, alerts = [], now = Date.now() }) {
  const none = reason => ({ type: 'none', reason });
  if (!settings?.enabled) return none('off');
  if (mode !== 'auto') return none('manual-mode');
  if (settings.sessionId !== sessionId) return none('other-session');
  if (settings.stopReason) return none('stopped');
  if (!row) return none('no-project');
  if (row.status === 'checkpointed' || row.status === 'paused' || row.megaTask?.pauseRequested) return none('user-paused');
  if (row.status === 'needs_approval' && row.megaTaskId) {
    if (settings.approved.includes(row.megaTaskId)) return none('already-approved');
    if (settings.approved.length >= LIMITS.maxMegaTasks) return { type: 'limit', reason: 'MEGATASK_LIMIT' };
    return { type: 'approve', megaTaskId: row.megaTaskId };
  }
  if (!settings.started && !row.megaTaskId) return { type: 'start' };
  if (row.status === 'complete' && row.megaTaskId && !settings.evaluated.includes(row.megaTaskId)) return { type: 'evaluate', megaTaskId: row.megaTaskId };
  const alert = alerts.find(item => item.active !== false && !item.ignored && !settings.nudgedKeys.includes(item.key));
  if (!alert) return none('idle');
  if (settings.nudgeTimes.filter(at => now - Date.parse(at) < HOUR_MS).length >= LIMITS.maxNudgesPerHour) return none('nudge-limit');
  return { type: 'nudge', alert };
}

function leadInstructions() {
  return [
    'Reply with one JSON object and nothing else: {"done":true|false,"next":"objective or null","assumptions":["..."]}.',
    'Set done to true only if the goal is met by the recorded results. Otherwise put the single next objective in next.',
    'Decide open questions yourself and list what you assumed. Work stays inside the project folder: never push, merge to a shared branch, or publish a release.'
  ].join('\n');
}

function evaluationPrompt({ name, goal, results }) {
  const lines = (results || []).slice(-6).map(result => `- ${result.status}: ${result.summary || 'no summary'}`);
  return [`Autonomous review of project ${name}. The current MegaTask finished.`, `Goal: ${goal || 'not recorded'}`, 'Recorded results:', ...(lines.length ? lines : ['- none recorded']), leadInstructions()].join('\n');
}

// Text comes from backend-detected facts, so no model runs to raise a nudge.
function nudgePrompt({ name, goal, alert }) {
  return [`Supervisor alert for project ${name}: ${alert.title}. ${alert.message}`, `Goal: ${goal || 'not recorded'}`, 'If active workers are still making progress, set next to null. If the work is stuck, name the objective that recovers it.', leadInstructions()].join('\n');
}

function parseDecision(text) {
  if (typeof text !== 'string') return null;
  const start = text.indexOf('{'), end = text.lastIndexOf('}');
  if (start < 0 || end < start) return null;
  let parsed;
  try { parsed = JSON.parse(text.slice(start, end + 1)); } catch { return null; }
  if (!parsed || typeof parsed.done !== 'boolean') return null;
  const next = typeof parsed.next === 'string' && parsed.next.trim() ? parsed.next.trim().slice(0, LIMITS.maxObjectiveChars) : null;
  if (!parsed.done && parsed.next != null && !next) return null;
  const assumptions = Array.isArray(parsed.assumptions) ? parsed.assumptions.filter(item => typeof item === 'string').map(item => item.slice(0, 500)).slice(0, 10) : [];
  return { done: parsed.done, next: parsed.done ? null : next, assumptions };
}

module.exports = { LIMITS, createSettings, pushGuardEnvironment, nextStep, evaluationPrompt, nudgePrompt, parseDecision };
