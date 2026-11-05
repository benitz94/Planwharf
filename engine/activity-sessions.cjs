'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { activityEvents, activityCapabilities } = require('./activity.cjs');
const { sanitize } = require('./activity-events.cjs');
const { validateResult } = require('./worker-result.cjs');
const { assignmentRole } = require('./model-roles.cjs');
const { technicalEvidence } = require('./activity-evidence.cjs');
const SESSION_LIMIT = 24, EVENT_LIMIT = 50, TAIL_BYTES = 32768;
const text = value => typeof value === 'string' && value ? sanitize(value, 160) : null;
const timestamp = value => typeof value === 'string' && Number.isFinite(Date.parse(value)) ? value : null;
const list = value => Array.isArray(value) ? value : [];

function readRecordedTail(file, root) {
  if (!file || !root) return '';
  let fd;
  try {
    const base = fs.realpathSync(root), target = fs.realpathSync(file);
    const relative = path.relative(base, target);
    if (!relative || relative === '..' || relative.startsWith('..' + path.sep) || path.isAbsolute(relative)) return '';
    fd = fs.openSync(target, 'r');
    const stat = fs.fstatSync(fd);
    if (!stat.isFile()) return '';
    const length = Math.min(stat.size, TAIL_BYTES), start = stat.size - length;
    const buffer = Buffer.alloc(length);
    const read = fs.readSync(fd, buffer, 0, length, start);
    let tail = buffer.subarray(0, read).toString('utf8');
    if (start > 0) tail = tail.includes('\n') ? tail.slice(tail.indexOf('\n') + 1) : '';
    return tail;
  } catch { return ''; }
  finally { if (fd !== undefined) fs.closeSync(fd); }
}

function assignmentSession({ projectId, row = {}, part = {}, execution = {}, tail = '', historical = false, index = 0 }) {
  if (!projectId || part.projectId && part.projectId !== projectId) return null;
  const megaTaskId = part.megaTaskId || row.megaTaskId || null;
  const mega = list(execution.megaTasks).find(mega => mega.id === megaTaskId && mega.projectId === projectId);
  if (megaTaskId && !mega) return null;
  const sub = list(execution.subTasks).find(sub => sub.id === part.subTaskId && sub.megaTaskId === megaTaskId);
  if (part.subTaskId && !sub) return null;
  const attemptId = part.attemptId || null;
  const candidate = mega && sub && attemptId ? list(execution.results).find(result => result.attemptId === attemptId && result.megaTaskId === mega.id && result.subTaskId === sub.id) : null;
  const result = candidate && (!candidate.projectId || candidate.projectId === projectId)
    && (!(part.providerId || part.provider) || (part.providerId || part.provider) === candidate.provider)
    && (!part.cli || part.cli === candidate.cli)
    && (!part.modelId || !candidate.assignedModelId || part.modelId === candidate.assignedModelId) ? candidate : null;
  // Never relabel historical work from today's mutable model catalog.
  const provider = part.providerId || part.provider || result?.provider || null;
  const model = part.cli || result?.cli || null;
  const adapter = part.adapter || (['codex', 'grok', 'antigravity', 'claude'].includes(provider) ? provider : null);
  const identity = result && { resultId: 'R-' + attemptId, attemptId, megaTaskId, subTaskId: sub.id };
  const validated = Boolean(result && provider && model && result.provider === provider && result.cli === model && result.evidenceKind !== 'TECHNICAL' && validateResult(result, identity) && (result.status !== 'COMPLETE' || result.exitCode === 0));
  const role = sub ? assignmentRole(sub) : ['worker', 'reviewer'].includes(part.role) ? part.role : null;
  const events = activityEvents(tail, { provider: provider || 'unknown', adapter: adapter || 'unknown', model: model || '' }).slice(-EVENT_LIMIT);
  const alive = historical ? false : part.observedAlive === true;
  const processState = historical ? 'recorded' : alive ? 'running' : part.observedAlive === false && Number.isInteger(part.pid) && part.pid > 0 ? 'ended' : 'unknown';
  const status = part.stoppedByUser ? 'stopped' : alive ? role === 'reviewer' ? 'reviewing' : 'running'
    : validated && result.status === 'COMPLETE' ? 'completed'
    : validated && result.status === 'FAILED' ? 'failed'
    : validated && result.status === 'BLOCKED' ? 'waiting'
    : row.status === 'checkpointed' ? 'checkpointed'
    : sub?.status === 'PENDING' ? 'pending' : sub?.status === 'BLOCKED' ? 'waiting'
    : result?.status === 'FAILED' || sub?.status === 'FAILED' || row.status === 'failed' ? 'failed' : 'unknown';
  const warnings = [];
  if (!provider || !model) warnings.push('Exact provider/model identity was not retained for this assignment.');
  if (!attemptId) warnings.push('Exact attempt identity was not retained for this assignment.');
  if (!role) warnings.push('Assignment role was not retained.');
  if (!events.length) warnings.push('No readable assignment output is retained in this view.');
  if (!validated) warnings.push('No matching backend-validated structured result is recorded.');
  if (candidate && !result) warnings.push('Recorded result identity does not match this assignment.');
  if (!adapter) warnings.push('Adapter identity was not retained; detailed protocol capability is unknown.');
  return {
    id: [projectId, megaTaskId || 'legacy', attemptId || 'unrecorded-' + index].map(value => encodeURIComponent(value)).join(':'),
    projectId: text(projectId), jobId: text(mega?.taskId || row.jobId), megaTaskId: text(megaTaskId), subTaskId: text(part.subTaskId), attemptId: text(attemptId),
    recordedSessionId: text(part.sessionId || row.sessionId || row.authorizedSessionId),
    role, provider: text(provider), adapter: text(adapter), model: text(model), modelId: text(part.modelId || result?.assignedModelId),
    startedAt: timestamp(part.startedAt || result?.startedAt),
    completedAt: validated && sub.resultId === result.resultId ? timestamp(sub.completedAt) : null,
    reportedResultAt: timestamp(result?.createdAt),
    processState, state: status, historical,
    validatedResultStatus: validated ? result.status : null,
    recordedResultStatus: text(result?.status), exitCode: result && Number.isInteger(result.exitCode) ? result.exitCode : null,
    capabilities: activityCapabilities({ adapter: adapter || 'unknown' }),
    latestActivity: events.at(-1) || null,
    latestConfirmedActivity: events.filter(event => event.provenance === 'observed').at(-1) || null,
    reportedOutcomes: events.filter(event => event.provenance === 'reported' && ['test', 'build', 'warning'].includes(event.classification)).slice(-5),
    technicalEvidence: technicalEvidence(tail, { provider: provider || 'unknown', adapter: adapter || 'unknown' }),
    events, warnings
  };
}

function projectActivitySessions({ projectId, row, current = [], history = [], execution = {}, readTail = () => '' }) {
  const sessions = current.filter(Boolean).slice(0, SESSION_LIMIT);
  const knownAttempts = new Set(sessions.map(session => session.attemptId).filter(Boolean));
  const add = session => {
    if (!session || sessions.length >= SESSION_LIMIT || session.attemptId && knownAttempts.has(session.attemptId)) return;
    sessions.push(session);
    if (session.attemptId) knownAttempts.add(session.attemptId);
  };
  for (const record of list(history).filter(record => record.id === projectId).slice(-SESSION_LIMIT).reverse()) {
    for (const [index, part] of list(record.dispatch).slice(0, SESSION_LIMIT).entries()) {
      if (sessions.length >= SESSION_LIMIT) break;
      add(assignmentSession({ projectId, row: record, part, execution, historical: true, index, tail: readTail(part.logPath) }));
    }
  }
  const megaIds = new Set(list(execution.megaTasks).filter(mega => mega.projectId === projectId).map(mega => mega.id));
  for (const result of list(execution.results).filter(result => megaIds.has(result.megaTaskId)).slice(-SESSION_LIMIT).reverse()) {
    if (sessions.length >= SESSION_LIMIT) break;
    add(assignmentSession({ projectId, row: {}, part: { ...result, modelId: result.assignedModelId }, execution, historical: true }));
  }
  return sessions;
}
// Enumerate metadata first. Never read a log until its exact assignment is on
// the requested page. Callers must supply the same source-contained readTail
// callback used for snapshots (file => readRecordedTail(file, notebookRoot)).
function pagedProjectActivitySessions({ projectId, row = {}, currentParts = row.dispatch || [], history = [], execution = {}, offset = 0, limit = SESSION_LIMIT, readTail = () => '' }) {
  const pageLimit = Number.isSafeInteger(limit) ? Math.min(SESSION_LIMIT, Math.max(1, limit)) : SESSION_LIMIT;
  const pageOffset = Number.isSafeInteger(offset) ? Math.max(0, offset) : 0;
  const metadata = [], known = new Set();
  const megas = new Map(list(execution.megaTasks).filter(mega => mega?.projectId === projectId).map(mega => [mega.id, mega]));
  const subs = new Map(list(execution.subTasks).filter(Boolean).map(sub => [sub.id, sub]));
  const add = (part, record, historical, index) => {
    if (!part || typeof part !== 'object' || !projectId || part.projectId && part.projectId !== projectId) return;
    const megaTaskId = part.megaTaskId || record.megaTaskId;
    if (megaTaskId && !megas.has(megaTaskId)) return;
    if (part.subTaskId && subs.get(part.subTaskId)?.megaTaskId !== megaTaskId) return;
    const key = part.attemptId ? JSON.stringify([megaTaskId || null, part.subTaskId || null, part.attemptId]) : 'legacy-' + index;
    if (known.has(key)) return;
    known.add(key); metadata.push({ part, record, historical, index });
  };
  let index = 0;
  if ((!row.projectId || row.projectId === projectId) && (!row.id || row.id === projectId)) for (const part of list(currentParts)) add(part, row, false, index++);
  for (const record of list(history).filter(record => record?.id === projectId).reverse()) {
    for (const part of list(record.dispatch)) add(part, record, true, index++);
  }
  for (const result of list(execution.results).filter(result => result && megas.has(result.megaTaskId) && (!result.projectId || result.projectId === projectId)).reverse()) {
    // Worker result payloads permit extra fields and cannot authorize a source
    // log read. Only an actual retained dispatch/history descriptor owns a log.
    const { logPath: untrustedLogPath, ...resultMetadata } = result;
    add({ ...resultMetadata, modelId: result.assignedModelId }, {}, true, index++);
  }
  const sessions = metadata.slice(pageOffset, pageOffset + pageLimit).map(({ part, record, historical, index }) => assignmentSession({ projectId, row: record, part, historical, index, execution, tail: part.logPath ? readTail(part.logPath) : '' })).filter(Boolean);
  return { sessions, total: metadata.length, offset: pageOffset, limit: pageLimit };
}
module.exports = { assignmentSession, projectActivitySessions, pagedProjectActivitySessions, readRecordedTail, SESSION_LIMIT, EVENT_LIMIT, TAIL_BYTES };
