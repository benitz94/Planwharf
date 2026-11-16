'use strict';
// How long the remaining steps should take. Data only: the caller passes the history,
// the steps and the current time. No files and no clock in here, so it can be ported.

const DEFAULT_MINUTES = { Build: 20, Verify: 10, Review: 20, Research: 20 };
const WORKING_FLOOR = 2;
const HISTORY_LIMIT = 500;
const ROLES = new Set(['Build', 'Verify', 'Review', 'Research']);
const CANONICAL_ROLES = {
  build: 'Build',
  builder: 'Build',
  verify: 'Verify',
  verifier: 'Verify',
  review: 'Review',
  reviewer: 'Review',
  audit: 'Review',
  research: 'Research',
  researcher: 'Research'
};

function canonicalRole(role) {
  if (!role) return null;
  const key = String(role).trim().toLowerCase();
  return CANONICAL_ROLES[key] || (ROLES.has(role) ? role : null);
}

function roleOf(step) {
  if (!step) return 'Build';
  const role = canonicalRole(step.role);
  if (role) return role;
  if (step.role && typeof step.role === 'string') return step.role;
  return step.type === 'AUDIT' ? 'Review' : step.type === 'RESEARCH' ? 'Research' : /^Verify the work independently/.test(String(step.title || '')) ? 'Verify' : 'Build';
}

function stateOf(status) {
  const text = String(status || '').toUpperCase();
  if (text === 'RUNNING' || text === 'WORKING' || text === 'IN_PROGRESS') return 'working';
  if (text === 'PENDING' || text === 'WAITING' || text.startsWith('WAITING')) return 'waiting';
  if (text === 'COMPLETE' || text === 'DONE' || text === 'SUCCEEDED' || text === 'FINISHED') return 'done';
  return 'other';
}

function median(values) {
  const sorted = values.filter(value => Number.isFinite(value)).sort((a, b) => a - b);
  if (!sorted.length) return null;
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

// Same role and model when that pair has happened before, else the same role, else the default.
function typicalMinutes(step, history) {
  const role = roleOf(step);
  const model = String(step.model || step.modelId || '');
  const rows = (history || []).filter(row => row && row.role === role && Number.isFinite(Number(row.minutes)));
  const sameModel = rows.filter(row => String(row.model || '') === model);
  const found = median((sameModel.length ? sameModel : rows).map(row => Number(row.minutes)));
  return found == null ? (DEFAULT_MINUTES[role] ?? 20) : found;
}

function stepMinutes(step, history, now) {
  const typical = typicalMinutes(step, history);
  if (stateOf(step.status) !== 'working') return { minutes: Math.max(0, Math.round(typical)), longer: false, role: roleOf(step) };
  const started = Date.parse(step.startedAt || '');
  const at = Date.parse(now || '');
  const spent = Number.isFinite(started) && Number.isFinite(at) ? Math.max(0, (at - started) / 60000) : 0;
  if (spent > typical) return { minutes: WORKING_FLOOR, longer: true, role: roleOf(step) };
  return { minutes: Math.max(WORKING_FLOOR, Math.round(typical - spent)), longer: false, role: roleOf(step) };
}

// The time left is the critical path: steps that can run together count once (the longest),
// and a step that waits for another adds its own time after that one.
function estimate({ history = [], steps = [], now } = {}) {
  const rows = Array.isArray(history) ? history : [];
  const list = (Array.isArray(steps) ? steps : []).map((step, index) => ({ ...step, id: step && step.id ? String(step.id) : 'step-' + index }));
  const byId = new Map(list.map(step => [step.id, step]));
  const remaining = list.filter(step => stateOf(step.status) === 'working' || stateOf(step.status) === 'waiting');
  if (!remaining.length) return null;
  const each = new Map(remaining.map(step => [step.id, stepMinutes(step, rows, now)]));
  const memo = new Map();
  function finish(step, stack) {
    if (memo.has(step.id)) return memo.get(step.id);
    if (stack.has(step.id)) return each.get(step.id).minutes;
    stack.add(step.id);
    let wait = 0;
    // A step already working has started: earlier steps do not add to its remaining time.
    if (stateOf(step.status) !== 'working') {
      for (const id of Array.isArray(step.dependencies) ? step.dependencies : []) {
        const dep = byId.get(String(id));
        if (dep && dep.id !== step.id && each.has(dep.id)) wait = Math.max(wait, finish(dep, stack));
      }
    }
    stack.delete(step.id);
    const value = wait + each.get(step.id).minutes;
    memo.set(step.id, value);
    return value;
  }
  const minutes = Math.max(...remaining.map(step => finish(step, new Set())));
  const roles = [...new Set([...each.values()].map(item => item.role))];
  const counts = roles.map(role => rows.filter(row => row && row.role === role && Number.isFinite(Number(row.minutes))).length);
  const sure = counts.some(count => count < 5) ? 'rough' : 'based on ' + counts.reduce((sum, count) => sum + count, 0) + ' past steps';
  const at = Date.parse(now || '');
  return {
    minutes,
    endsAt: Number.isFinite(at) ? new Date(at + minutes * 60000).toISOString() : null,
    sure,
    longerThanUsual: [...each.values()].some(item => item.longer)
  };
}

// One history line: role, model id, size when known, files changed, minutes, done or failed.
// Nothing else (no prompt, path or project name) is kept.
function stepRecord({ role, model, size, files, startedAt, endedAt, status, modelChange } = {}) {
  if (modelChange) return null;
  const start = Date.parse(startedAt || '');
  const end = Date.parse(endedAt || '');
  if (!Number.isFinite(start) || !Number.isFinite(end)) return null;
  const name = canonicalRole(role) || (ROLES.has(role) ? role : roleOf({ role, title: '', type: '' }));
  if (!ROLES.has(name)) return null;
  const line = {
    role: name,
    model: String(model || ''),
    files: Number.isFinite(Number(files)) ? Math.max(0, Math.round(Number(files))) : 0,
    minutes: Math.max(0, Math.round((end - start) / 6000) / 10),
    outcome: status === 'COMPLETE' || status === 'done' ? 'done' : 'failed'
  };
  if (size !== undefined && size !== null && size !== '') line.size = typeof size === 'number' && Number.isFinite(size) ? size : String(size).slice(0, 40);
  return line;
}

function parseHistory(text) {
  const rows = [];
  for (const line of String(text || '').split(/\n/)) {
    const trimmed = line.trim();
    if (!trimmed) continue;
    try {
      const row = JSON.parse(trimmed);
      const clean = stepRecord({ ...row, startedAt: '2020-01-01T00:00:00.000Z', endedAt: '2020-01-01T00:00:00.000Z', status: row.outcome === 'done' ? 'COMPLETE' : 'FAILED' });
      if (!clean) continue;
      clean.minutes = Number(row.minutes);
      if (!Number.isFinite(clean.minutes) || clean.minutes < 0) continue;
      if (row.size !== undefined && row.size !== null && row.size !== '') clean.size = typeof row.size === 'number' && Number.isFinite(row.size) ? row.size : String(row.size).slice(0, 40);
      else delete clean.size;
      rows.push(clean);
    } catch { /* a torn last line is dropped */ }
  }
  return rows.slice(-HISTORY_LIMIT);
}

function keepLine(record) {
  if (!record || typeof record !== 'object') return null;
  const role = canonicalRole(record.role);
  if (!role || !ROLES.has(role)) return null;
  const minutes = Number(record.minutes);
  if (!Number.isFinite(minutes) || minutes < 0) return null;
  const line = {
    role,
    model: String(record.model || ''),
    files: Number.isFinite(Number(record.files)) ? Math.max(0, Math.round(Number(record.files))) : 0,
    minutes,
    outcome: record.outcome === 'done' ? 'done' : 'failed'
  };
  if (record.size !== undefined && record.size !== null && record.size !== '') line.size = typeof record.size === 'number' && Number.isFinite(record.size) ? record.size : String(record.size).slice(0, 40);
  return line;
}

function remember(history, record) {
  const rows = (history || []).map(keepLine).filter(Boolean);
  const clean = keepLine(record);
  return (clean ? [...rows, clean] : rows).slice(-HISTORY_LIMIT);
}

function historyText(records) {
  const lines = (records || []).map(keepLine).filter(Boolean).slice(-HISTORY_LIMIT).map(row => JSON.stringify(row));
  return lines.length ? lines.join('\n') + '\n' : '';
}

module.exports = { estimate, roleOf, stepRecord, parseHistory, remember, historyText, DEFAULT_MINUTES, HISTORY_LIMIT };
