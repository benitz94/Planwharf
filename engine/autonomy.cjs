'use strict';
// Project autonomy policy. The owner's setting grants authority; model text only
// proposes the next objective and is never parsed into any other action.

const LIMITS = Object.freeze({ maxMegaTasks: 5, maxNudgesPerHour: 3, maxObjectiveChars: 4000 });
const HOUR_MS = 3600000;
const WORK_ENDED = new Set(['worker failure', 'blocked worker']);

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
  // A checkpoint saved because a worker failed or stopped blocked is not the user's pause: the lead hears about it.
  const workEnded = row.status === 'checkpointed' && WORK_ENDED.has(row.checkpoint?.reason);
  if ((row.status === 'checkpointed' && !workEnded) || row.status === 'paused' || row.megaTask?.pauseRequested) return none('user-paused');
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

function checksText(checks) {
  if (checks === undefined) return [];
  if (!checks) return ['Planwharf found no recognised test entry point (package.json test script or test files) in the project folder.'];
  const verdict = checks.timedOut ? 'TIMED OUT' : checks.exitCode === 0 ? 'PASSED' : `FAILED (exit ${checks.exitCode})`;
  return [`Planwharf ran \`${checks.label}\` in the project folder just now: ${verdict} in ${checks.seconds} s. Output tail:`, '```', checks.output || '(no output)', '```',
    'This run is fresh evidence. Do not run commands or edit files yourself: decide from the goal, the results and this run.'];
}

// True when the evidence is clear enough to end without asking the lead.
function cleanFinish(results, checks) {
  const list = Array.isArray(results) ? results : [];
  if (list.length < 2 || !checks || checks.timedOut || checks.exitCode !== 0) return false;
  return list.every(result => result.status === 'COMPLETE' && !(Array.isArray(result.blockers) && result.blockers.length));
}

function evaluationPrompt({ name, goal, results, checks }) {
  const lines = (results || []).slice(-6).map(result => `- ${result.status}: ${result.summary || 'no summary'}`);
  return [`Autonomous review of project ${name}. The current MegaTask finished.`, `Goal: ${goal || 'not recorded'}`, 'Recorded results:', ...(lines.length ? lines : ['- none recorded']), ...checksText(checks), leadInstructions()].join('\n');
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

// Before approval the lead picks who does each slice, instead of the catalog order.
// Each slice lists only the models whose roles allow it (Worker builds, Verifier checks).
function assignmentPrompt({ name, goal, slices, candidates }) {
  return [`Autonomous planning for project ${name}. Goal: ${goal || 'not recorded'}`,
    'The MegaTask has these slices, run in order. Each lists the models allowed to take it:',
    ...slices.map((slice, index) => `${index + 1}. ${slice}\n   Allowed: ${candidates[index].map(worker => `${worker.id} (${worker.label}, provider ${worker.provider})`).join('; ')}`),
    'Choose one allowed model per slice. Pick the model most likely to do that slice correctly. For each later slice prefer a different provider than the slice before it, so the work is checked independently.',
    'Do not run commands or read files: decide from this message only.',
    'Reply with one JSON object and nothing else: {"assign":["worker id for slice 1", "..."],"why":"one sentence"}.'].join('\n');
}

function parseAssignment(text, count, allowed) {
  const allowedAt = index => Array.isArray(allowed[0]) ? allowed[index] : allowed;
  if (typeof text !== 'string') return null;
  const start = text.indexOf('{'), end = text.lastIndexOf('}');
  if (start < 0 || end < start) return null;
  let parsed; try { parsed = JSON.parse(text.slice(start, end + 1)); } catch { return null; }
  const ids = Array.isArray(parsed?.assign) ? parsed.assign : null;
  if (!ids || ids.length !== count || !ids.every((id, index) => typeof id === 'string' && allowedAt(index).includes(id))) return null;
  return { assign: ids, why: typeof parsed.why === 'string' ? parsed.why.slice(0, 300) : '' };
}

module.exports = { LIMITS, cleanFinish, createSettings, pushGuardEnvironment, nextStep, evaluationPrompt, nudgePrompt, parseDecision, assignmentPrompt, parseAssignment };
