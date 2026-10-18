'use strict';

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const Plan = require('../plan.js');
const { planExhausted, emptyPlans } = require('./planUsage.js');
const { createRepositoryInspector } = require('./repository.cjs');
const { modelRouteError } = require('./model-routing.cjs');
const { createExecutionBridge } = require('./execution.cjs');
const { executionNext } = require('./execution-next.cjs');
const { loadCatalog, providerViews } = require('./provider-catalog.cjs');
const { adapterFor } = require('./provider-adapters.cjs');
const { diagnoseProvider } = require('./provider-diagnostics.cjs');
const { createInvocationLedger } = require('./invocations.cjs');
const { compactState, recentContext } = require('./briefing.cjs');
const Quota = require('../quota.js');
const { validatePolicy, migratePolicy, usagePolicy, resolveUsageFallback } = require('./model-policy.cjs');
const { validateProviderPolicy, providerPolicyFor } = require('./provider-policy.cjs');
const { ROLE_IDS, withModelRoles, roleEligibility, permitsAssignment, assignmentRole } = require('./model-roles.cjs');
const { assignmentSession, projectActivitySessions, pagedProjectActivitySessions, readRecordedTail, SESSION_LIMIT } = require('./activity-sessions.cjs');

const LABELS = {
  waiting_on_you: 'Waiting on you',
  propose_grill: 'Grill first',
  idle: 'Idle',
  blocked_no_folder: 'Needs a folder',
  blocked_no_worker: 'No eligible worker',
  needs_approval: 'Plan ready',
  watching: 'Watching',
  complete: 'Evidence complete',
  failed: 'Stopped',
  checkpointed: 'Paused'
};

const PIPELINE_STEPS = [
  { id: 'check', label: 'Check' },
  { id: 'grill', label: 'Grill' },
  { id: 'plan', label: 'Plan' },
  { id: 'approve', label: 'Approve' },
  { id: 'assign', label: 'Assign' },
  { id: 'watch', label: 'Watch' }
];

function stepOf(row) {
  if (!row) return 'idle';
  switch (row.status) {
    case 'blocked_no_folder': return 'check';
    case 'propose_grill':
    case 'waiting_on_you': return 'grill';
    case 'idle': return row.pendingAutoStart ? 'plan' : 'idle';
    case 'needs_approval': return 'approve';
    case 'blocked_no_worker': return 'assign';
    case 'watching':
    case 'checkpointed':
    case 'failed': return 'watch';
    case 'complete': return 'done';
    default: return row.status || 'idle';
  }
}

function pipelineOf(row) {
  const step = stepOf(row);
  const order = PIPELINE_STEPS.map(item => item.id);
  const nowIndex = step === 'done' ? order.length : order.indexOf(step);
  return PIPELINE_STEPS.map((item, i) => {
    let state = 'todo';
    if (step === 'idle') state = 'todo';
    else if (step === 'done' || (nowIndex >= 0 && i < nowIndex)) state = 'done';
    else if (i === nowIndex) state = 'now';
    if (item.id === 'grill' && row && row.grillSkipped && state !== 'now') {
      state = nowIndex > i ? 'skip' : 'todo';
    }
    return { id: item.id, label: item.label, state };
  });
}

const ORCH_NAME = 'Orchestrator';

function orchestratorDoing(row) {
  if (!row) return 'Idle.';
  const n = Array.isArray(row.dispatch) && row.dispatch.length ? row.dispatch.length : (row.workerLabel ? 1 : 0);
  if (row.status === 'watching') {
    const alive = Array.isArray(row.dispatch)
      ? row.dispatch.filter(part => part && part.alive).length
      : (row.workerAlive ? 1 : 0);
    const files = row.facts && Number.isFinite(row.facts.fileCount) ? row.facts.fileCount : null;
    const folder = files == null ? 'project folder' : `${files} files in the project folder`;
    if (/Hit an error/i.test(row.workerActivity || '')) {
      return `Worker looks unhealthy. Monitoring for recovery; I will switch models if it stalls or dies.`;
    }
    return `Monitoring ${n} worker${n === 1 ? '' : 's'} (${alive} alive). Watching process, worker log, and ${folder}.`;
  }
  if (row.status === 'needs_approval') {
    return n > 1
      ? `Split the job into ${n} slices across available workers. Waiting for Approve.`
      : 'Picked a worker. Waiting for Approve.';
  }
  if (row.status === 'propose_grill' || row.status === 'waiting_on_you') return 'Holding assign until grill is closed.';
  if (row.status === 'complete') return 'Verified STATUS: COMPLETE and folder output.';
  if (row.status === 'failed') return 'Stopped monitoring. No remaining worker, or every slice failed.';
  if (row.status === 'checkpointed') return checkpointTalk(row.checkpoint && row.checkpoint.reason).orch;
  if (row.status === 'blocked_no_folder') return 'Cannot inspect: no local folder.';
  if (row.status === 'blocked_no_worker') return 'No eligible worker. Turn a model on, or wait until its provider usage resets.';
  if (row.status === 'idle') return 'Reading the notebook. No job to split.';
  return LABELS[row.status] || 'Idle.';
}

function crewOf(row, leadLabel, execution = {}) {
  const crew = [{
    role: 'Orchestrator',
    name: leadLabel || (row && row.orchestratorLabel) || ORCH_NAME,
    doing: (row && row.orchestratorDoing) || orchestratorDoing(row)
  }];
  const liveWorkers = row && row.status !== 'idle' && row.status !== 'propose_grill' && row.status !== 'waiting_on_you' && row.status !== 'blocked_no_folder';
  const dispatch = liveWorkers && Array.isArray(row.dispatch) ? row.dispatch : [];
  if (dispatch.length) {
    dispatch.forEach(part => {
      const sub = (execution.subTasks || row.subTasks || []).find(sub => sub.id === part.subTaskId && (!part.megaTaskId || sub.megaTaskId === part.megaTaskId));
      const retainedRole = part.activitySession?.role;
      const role = retainedRole === 'reviewer' || (!retainedRole && assignmentRole(sub || part) === 'reviewer') ? 'Reviewer' : 'Worker';
      let doing = part.slice || 'On the plan';
      if (part.historicalAssignment) {
        crew.push({ role, name: part.label || part.modelId, doing: `${part.assignmentJobTitle || 'Historical assignment'}: ${part.activity || 'Original activity is not retained.'}` });
        return;
      }
      if (row.status === 'needs_approval') doing = `${part.slice || 'Whole job'}. Waiting for Approve.`;
      if (row.status === 'watching') doing = part.activity || row.workerActivity
        || `${part.slice || 'Whole job'}. ${part.pid ? `Process ${part.pid}${part.alive ? ' running' : ' stopped'}.` : 'Claimed.'}`;
      if (row.status === 'complete') doing = 'Evidence complete.';
      if (row.status === 'checkpointed') doing = checkpointTalk(row.checkpoint && row.checkpoint.reason).orch;
      if (row.status === 'blocked_no_worker') doing = 'No remaining provider usage.';
      crew.push({ role, name: part.label || part.modelId, doing });
    });
  } else if (row && row.workerLabel && !String(row.workerLabel).includes(' + ')) {
    crew.push({
      role: 'Worker',
      name: row.workerLabel,
      doing: row.status === 'watching'
        ? (row.workerActivity
          || (row.workerPid
            ? `Process ${row.workerPid}${row.workerAlive ? ' running' : ' stopped'}.`
            : 'Claimed.'))
        : row.status === 'needs_approval'
          ? 'Picked. Waiting for Approve.'
          : (row.waitingFor || '')
    });
  } else if (!(row && row.workerLabel)) {
    crew.push({
      role: 'Worker',
      name: 'None yet',
      doing: row && (row.status === 'propose_grill' || row.status === 'waiting_on_you')
        ? 'Not assigned until grill is closed.'
        : 'Not assigned yet.'
    });
  }
  return crew;
}

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function notebookProjects(plan) {
  if (!plan) return [];
  return plan.schema === 2 ? plan.projects : [plan];
}

function normalizeModels(models) {
  return (Array.isArray(models) ? models : []).filter(item => item && typeof item.id === 'string').map(item => {
    // Legacy roster-only fixtures have no route. Real configured routes must be valid.
    const routeError = item.provider !== undefined || item.cli !== undefined ? modelRouteError(item) : null;
    return {
      ...item,
      id: item.id,
      label: typeof item.label === 'string' && item.label.trim() ? item.label : item.id,
      enabled: item.enabled === true && (item.providerCatalog || item.available !== false && !routeError),
      available: item.available !== false && !routeError,
      unavailableReason: item.unavailableReason || routeError || null,
      availabilityVerified: item.availabilityVerified,
      availabilityWarning: item.availabilityWarning || null,
      provider: item.provider,
      modelRouting: item.modelRouting,
      plan: typeof item.plan === 'string' && item.plan.trim() ? item.plan : 'Unnamed plan',
      kind: item.kind === 'premium' || item.kind === 'local' ? item.kind : 'default',
      cost: Number.isFinite(item.cost) && item.cost >= 0 ? item.cost : 1,
      limit: Number.isFinite(item.limit) && item.limit > 0 ? item.limit : 50,
      cli: typeof item.cli === 'string' && item.cli.trim() ? item.cli : undefined
    };
  });
}

function usedOf(usage, id) {
  const row = usage && usage[id];
  return row && Number.isFinite(row.used) ? row.used : 0;
}

function usageOf(item) {
  const used = item && Number.isFinite(item.used) ? item.used : 0;
  const limit = item && Number.isFinite(item.limit) && item.limit > 0 ? item.limit : 0;
  const remaining = item && Number.isFinite(item.remaining) ? item.remaining : Math.max(0, limit - used);
  const pct = limit ? Math.min(100, Math.round((used * 100) / limit)) : 0;
  const leftPct = limit ? Math.max(0, 100 - pct) : 100;
  return { used, limit, remaining, pct, leftPct };
}

function canOrchestrate(item) {
  return Boolean(item && item.roles?.orchestrator !== false && item.enabled && item.available !== false && item.providerEnabled !== false && item.capabilities?.canUseTools !== false && item.capabilities?.canInspectFilesystem !== false && (item.orchestratorEligible ?? (item.kind === 'premium' || item.kind === 'default')));
}

function jobKind(job) {
  const text = `${job && job.title ? job.title : ''} ${job && job.detail ? job.detail : ''}`.toLowerCase();
  if (/\b(hard|difficult|escalat\w*|premium|complex)\b/.test(text)) return 'hard';
  if (/\b(implement|code|fix|bug|test|build|write)\b/.test(text)) return 'build';
  return 'inspect';
}

function pickWorker(models, options) {
  const opts = options && typeof options === 'object' ? options : {};
  const usage = opts.usage || {};
  const plans = Array.isArray(opts.plans) ? opts.plans : [];
  const kind = jobKind(opts.job);
  const skip = new Set(Array.isArray(opts.skip) ? opts.skip.filter(Boolean) : []);
  const pool = normalizeModels(models)
    .map((item, index) => ({ ...item, index }))
    .filter(item => item.enabled && item.available !== false && permitsAssignment(item, opts.job) && (assignmentRole(opts.job) === 'reviewer' ? true : opts.job?.kind === 'research' ? item.capabilities?.canResearch !== false : jobKind(opts.job) === 'build' ? item.capabilities?.canCode !== false : true) && item.providerEnabled !== false && !skip.has(item.id) && !usagePolicy(item, plans).blocked && (item.providerCatalog || usedOf(usage, item.id) + item.cost <= item.limit) && !planExhausted(plans, item.quotaPoolId || item.plan));
  if (!pool.length) return null;
  const chosen = pool[0];
  chosen.why = `${kind} job · ${chosen.label} on ${chosen.plan}; user catalog order`;
  return chosen;
}

function sliceCount(job, facts, eligible) {
  const kind = jobKind(job);
  const dirty = facts && Array.isArray(facts.dirty) ? facts.dirty.length : 0;
  const pool = Number.isFinite(eligible) && eligible > 0 ? eligible : 1;
  if (pool <= 1) return 1;
  if (kind === 'hard') return Math.min(3, pool);
  if (kind === 'build') return Math.min(3, pool);
  if (dirty >= 4) return Math.min(2, pool);
  return 1;
}

function pickWorkers(models, options) {
  const opts = options && typeof options === 'object' ? options : {};
  const skip = new Set(Array.isArray(opts.skip) ? opts.skip.filter(Boolean) : []);
  const baseUsage = opts.usage || {};
  const eligible = normalizeModels(models).filter(item => (
    item.enabled && item.available !== false && permitsAssignment(item, opts.job) && (assignmentRole(opts.job) === 'reviewer' ? true : opts.job?.kind === 'research' ? item.capabilities?.canResearch !== false : jobKind(opts.job) === 'build' ? item.capabilities?.canCode !== false : true) && item.providerEnabled !== false && !skip.has(item.id) && !usagePolicy(item, opts.plans || []).blocked && (item.providerCatalog || usedOf(baseUsage, item.id) + item.cost <= item.limit) && !planExhausted(opts.plans, item.quotaPoolId || item.plan)
  )).length;
  const want = Math.max(1, Number(opts.count) || sliceCount(opts.job, opts.facts, eligible));
  const workers = [];
  const usage = { ...baseUsage };
  let pool = Array.isArray(models) ? models.slice() : [];
  for (let i = 0; i < want; i++) {
    const chosen = pickWorker(pool, { ...opts, usage });
    if (!chosen) break;
    workers.push(chosen);
    pool = pool.filter(item => item && item.id !== chosen.id);
    usage[chosen.id] = { used: usedOf(usage, chosen.id) + chosen.cost };
  }
  return workers;
}

function dispatchPlan(workers, job, facts) {
  const list = Array.isArray(workers) ? workers.filter(Boolean) : [];
  if (!list.length) return [];
  const dirty = facts && Array.isArray(facts.dirty) ? facts.dirty.length : 0;
  const slices = assignmentRole(job) === 'reviewer'
    ? ['Review the approved scope and report findings.', 'Review correctness and edge cases; report findings.', 'Review tests and verification evidence; report findings.']
    : list.length === 1
    ? ['Whole job']
    : list.length === 2
      ? [
        dirty ? 'Inspect uncommitted changes, docs, and pin the slice' : 'Inspect the folder, git, and docs. Pin the slice.',
        'Implement the bounded slice and add tests.'
      ]
      : [
        dirty ? 'Inspect uncommitted changes, docs, and pin the slice' : 'Inspect the folder, git, and docs. Pin the slice. Do not implement yet.',
        'Implement the bounded slice in the project folder.',
        'Add and run tests for the slice. Fix what fails.'
      ];
  return list.map((worker, index) => ({
    modelId: worker.id,
    label: worker.label || worker.id,
    slice: slices[index] || `Parallel slice ${index + 1}`,
    why: worker.why || '',
    pid: null,
    logPath: null,
    resultPath: null,
    alive: false,
    activity: ''
  }));
}

function isDocName(name) {
  const value = String(name || '').replace(/\\/g, '/');
  const base = value.split('/').pop();
  if (/^docs\/.+\.md$/i.test(value)) return true;
  return /^(readme(\.md)?|agents\.md|claude\.md|skill\.md|contributing\.md)$/i.test(base);
}

function listDocs(folder) {
  const found = [];
  if (!folder || typeof folder !== 'string') return found;
  try {
    for (const name of fs.readdirSync(folder)) {
      if (name === '.' || name === '..') continue;
      if (isDocName(name)) found.push(name);
    }
    const docsDir = path.join(folder, 'docs');
    try {
      if (fs.statSync(docsDir).isDirectory()) {
        for (const name of fs.readdirSync(docsDir)) {
          if (/\.md$/i.test(name)) found.push('docs/' + name);
        }
      }
    } catch (_) {}
  } catch (_) {}
  return found.slice(0, 12);
}

function collectDocs(facts) {
  if (facts && Array.isArray(facts.docs) && facts.docs.length) {
    return facts.docs.map(name => String(name).trim()).filter(Boolean).slice(0, 12);
  }
  const names = facts && Array.isArray(facts.names) ? facts.names : [];
  return names.filter(isDocName).slice(0, 12);
}

function parseGrillQuestions(text) {
  if (typeof text !== 'string' || !text.trim()) return [];
  const raw = text.trim();
  const jsonMatch = raw.match(/\[[\s\S]*\]/);
  if (jsonMatch) {
    try {
      const parsed = JSON.parse(jsonMatch[0]);
      if (Array.isArray(parsed)) {
        return parsed.map(item => {
          if (typeof item === 'string') return item.trim();
          if (item && typeof item === 'object') {
            const title = String(item.title || item.question || '').trim();
            const body = String(item.body || '').trim();
            const recommend = String(item.recommend || item.recommended || '').trim();
            const line = [title, body].filter(Boolean).join(': ');
            return recommend ? `${line} Recommended: ${recommend}` : line;
          }
          return '';
        }).filter(Boolean).slice(0, 8);
      }
    } catch (_) {}
  }
  const lines = raw.split(/\r?\n/).map(line => line.replace(/^\s*(?:\d+[.)]|[-*]|❓\s*\*\*Q\d+\*\*\s*[-—–]?)\s*/i, '').replace(/\*\*/g, '').trim()).filter(Boolean);
  return lines.filter(line => /recommended:/i.test(line) || /\?/.test(line)).slice(0, 8);
}

function grillPrompt(item, facts, skillText, docs) {
  const latest = item.revisions.at(-1);
  return [
    'You are running Matt Pocock grilling for this Planwharf project.',
    skillText ? `Skill:\n${skillText}` : 'Ask the frontier in one round. Word each question so yes accepts your recommended answer.',
    `Project: ${item.project.name}`,
    latest && latest.direction ? `Goal: ${latest.direction}` : '',
    item.project.folder ? `Folder: ${item.project.folder}` : '',
    facts && facts.lastCommit ? `Latest commit: ${facts.lastCommit}` : '',
    facts && Array.isArray(facts.names) && facts.names.length ? `Top files: ${facts.names.slice(0, 12).join(', ')}` : '',
    docs && docs.length ? `Docs: ${docs.join(', ')}` : '',
    'Return ONLY a JSON array of 3 to 6 strings.',
    'Each string is one frontier question about THIS project, with "Recommended: ..." in the same string.',
    'Do not ask for facts listed above. Do not implement.'
  ].filter(Boolean).join('\n\n');
}

function grillDone(item) {
  return (item && Array.isArray(item.revisions) ? item.revisions : []).some(revision => (
    revision
    && (revision.mode === 'grill' || revision.title === 'Grill me' || revision.title === 'Grill with the docs')
  ));
}

function clip(text, n) {
  const value = String(text || '').replace(/\s+/g, ' ').trim();
  if (value.length <= n) return value;
  return value.slice(0, n - 1) + '…';
}

function tailLog(file, maxChars) {
  if (typeof file !== 'string' || !file.trim()) return '';
  try {
    const raw = fs.readFileSync(file, 'utf8');
    return raw.slice(-Math.max(200, maxChars || 1600)).replace(/\r/g, '').trim();
  } catch (_) {
    return '';
  }
}

function lastActivity(tail) {
  if (/[{}]|"event"|"step_update"/.test(String(tail || ''))) return activityMessages(tail).at(-1) || '';
  const lines = String(tail || '').split('\n').map(line => line.trim()).filter(Boolean);
  if (!lines.length) return '';
  return clip(lines[lines.length - 1], 180);
}

const { interpretActivity, activityMessages } = require('./activity.cjs');

function pickAskedProject(snap, projectId, question) {
  const rows = snap && Array.isArray(snap.projects) ? snap.projects : [];
  if (!rows.length) return null;
  if (typeof projectId === 'string' && projectId.trim()) {
    return rows.find(row => row.id === projectId) || null;
  }
  const q = String(question || '').toLowerCase();
  const named = rows.find(row => q.includes(String(row.name || '').toLowerCase()) || q.includes(String(row.id || '').toLowerCase()));
  if (named) return named;
  return rows.length === 1 ? rows[0] : null;
}

function packState(snap, row) {
  const facts = row && row.facts ? row.facts : null;
  const checkpoint = row && row.checkpoint ? {
    reason: row.checkpoint.reason || null,
    at: row.checkpoint.at || null,
    path: row.checkpoint.path || null,
    jobTitle: row.checkpoint.jobTitle || null,
    plan: Array.isArray(row.checkpoint.plan) ? row.checkpoint.plan.slice(0, 12) : []
  } : null;
  return {
    orchestrator: (snap && snap.orchestratorLabel) || ORCH_NAME,
    orchestratorId: snap && snap.orchestratorId,
    executionHandoff: row && snap?.execution ? {
      megaTasks: snap.execution.megaTasks.filter(mega => mega.projectId === row.id),
      subTasks: snap.execution.subTasks.filter(sub => snap.execution.megaTasks.some(mega => mega.projectId === row.id && mega.id === sub.megaTaskId)),
      results: snap.execution.results.filter(result => snap.execution.megaTasks.some(mega => mega.projectId === row.id && mega.id === result.megaTaskId)),
      checkpoints: snap.execution.checkpoints.filter(cp => cp.projectId === row.id),
      repository: row.facts, notes: row.notes || []
    } : null,
    researches: snap && Array.isArray(snap.researches) ? snap.researches.filter(research => !row || research.projectId === row.id) : [],
    mode: snap && snap.mode,
    grill: {
      propose: snap && snap.grillPropose !== false,
      kind: snap && snap.grillKind ? snap.grillKind : (snap && snap.grillPropose === false ? 'off' : 'me')
    },
    focus: row ? {
      id: row.id,
      name: row.name,
      folder: row.folder || (facts && facts.folder) || null,
      github: row.github || null,
      repository: row.repository || null,
      status: row.status,
      label: row.label,
      goal: row.goal || '',
      state: row.stateArt || '',
      open: Array.isArray(row.open) ? row.open.slice(0, 12) : [],
      analysis: row.analysis || '',
      job: row.jobTitle || null,
      plan: Array.isArray(row.plan) ? row.plan.slice(0, 16) : [],
      checkpoint,
      orchestratorDoing: row.orchestratorDoing || '',
      waitingFor: row.waitingFor || '',
      next: row.next || '',
      files: facts && facts.fileCount,
      git: facts ? Boolean(facts.git) : null,
      lastCommit: facts && facts.lastCommit || '',
      dirty: facts && Array.isArray(facts.dirty) ? facts.dirty.slice(0, 12) : [],
      workers: (row.dispatch || []).map(part => ({
        id: part.modelId,
        name: part.label,
        slice: part.slice,
        pid: part.pid || null,
        alive: Boolean(part.alive),
        activity: part.activity || '',
        log: lastActivity(part.logPath ? tailLog(part.logPath) : '')
      })),
      monitoring: row.monitoring || null,
      lastLog: lastActivity(row.workerTail)
    } : null,
    others: (snap && Array.isArray(snap.projects) ? snap.projects : [])
      .filter(() => !row)
      .map(item => ({ id: item.id, name: item.name, folder: item.folder || null, github: item.github || null, repository: item.repository || null, status: item.status, job: item.jobTitle || null })),
    fleet: (snap && Array.isArray(snap.fleet) ? snap.fleet : []).map(item => ({
      id: item.id,
      label: item.label,
      role: item.role,
      plan: item.plan,
      exhausted: Boolean(item.exhausted),
      enabled: item.enabled,
      roles: item.roles,
      roleEligibility: item.roleEligibility,
      lead: Boolean(item.lead)
    })),
    plans: (snap && Array.isArray(snap.plans) ? snap.plans : []).map(item => ({
      id: item.id,
      label: item.label,
      readable: Boolean(item.readable),
      exhausted: Boolean(item.exhausted),
      pct: item.pct,
      leftPct: item.leftPct,
      windows: item.windows || []
    }))
  };
}

function briefRow(row, lead) {
  if (!row) return 'No project.';
  const who = lead || ORCH_NAME;
  const bits = [
    `Speaking as ${who}. ${row.name} is ${row.label}.`,
    row.orchestratorDoing || '',
    row.jobTitle ? `Job: ${row.jobTitle}.` : 'No running job.',
  ];
  const parts = Array.isArray(row.dispatch) ? row.dispatch : [];
  if (parts.length) {
    parts.forEach((part, index) => {
      bits.push(`Worker ${index + 1} ${part.label || part.modelId}: ${part.slice || 'slice'}. ${part.activity || (part.pid ? `Process ${part.pid}${part.alive ? ' alive' : ' stopped'}` : 'Not started.')}`);
    });
  } else {
    bits.push(row.workerLabel ? `Worker: ${row.workerLabel}.` : 'No worker assigned.');
  }
  if (row.monitoring) {
    bits.push(`I am monitoring processes, worker logs, and the project folder: ${row.monitoring.alive}/${row.monitoring.workers} alive, ${Number.isFinite(row.monitoring.folderFiles) ? row.monitoring.folderFiles + ' files' : 'file count unavailable'}.`);
  }
  if (row.waitingFor) bits.push(`Waiting for: ${row.waitingFor}.`);
  if (row.next) bits.push(`Next: ${row.next}`);
  const last = lastActivity(row.workerTail);
  if (last) bits.push(`Last worker output: ${last}`);
  return bits.map(item => String(item || '').trim()).filter(Boolean).join(' ');
}

function answerQuestion(question, snap, projectId) {
  const q = String(question || '').trim();
  if (!q) return { ok: false, reason: 'empty question', answer: '', projectId: null };
  const lead = (snap && snap.orchestratorLabel) || ORCH_NAME;
  const rows = snap && Array.isArray(snap.projects) ? snap.projects : [];
  const all = /\b(all projects|tutti|fleet|ogni progetto)\b/i.test(q);
  if (all && rows.length) {
    return { ok: true, answer: rows.map(row => briefRow(row, lead)).join('\n'), projectId: null };
  }
  const row = pickAskedProject(snap, projectId, q);

  if (!row) return { ok: true, answer: `${lead}: no projects in the notebook.`, projectId: null };
  const ql = q.toLowerCase();
  const workerHit = (row.dispatch || []).find(part => {
    const label = String(part.label || '').toLowerCase();
    const id = String(part.modelId || '').toLowerCase();
    return (label && ql.includes(label)) || (id && ql.includes(id));
  });
  if (workerHit) {
    return {
      ok: true,
      projectId: row.id,
      answer: `${lead} on ${row.name}: ${workerHit.label || workerHit.modelId} has slice "${workerHit.slice || 'whole job'}". ${workerHit.activity || 'No interpreted activity yet.'} Process ${workerHit.pid || 'none'} is ${workerHit.alive ? 'alive' : 'stopped'}.`
    };
  }
  if (/\b(split|spacchett|slice|why two|perch[eé]|quanti worker)\b/i.test(q)) {
    const parts = row.dispatch || [];
    return {
      ok: true,
      projectId: row.id,
      answer: parts.length > 1
        ? `${lead} split ${row.name} into ${parts.length} slices: ${parts.map(part => `${part.label} — ${part.slice}`).join('; ')}.`
        : `${lead} did not split ${row.name}. One worker on the whole job.`
    };
  }
  if (/\b(quota|remaining|residu|left|utilizzo|usage|percent)\b/i.test(q)) {
    const plans = snap && Array.isArray(snap.plans) ? snap.plans : [];
    const list = plans.length
      ? plans.map(item => {
        if (!item.readable) return `${item.label}: ${item.reason || 'not readable'}`;
        const windows = Array.isArray(item.windows) && item.windows.length
          ? item.windows.map(win => `${win.name} ${win.pct}% used (${win.leftPct}% left)`).join(', ')
          : `${item.pct}% used (${item.leftPct}% left)`;
        return item.exhausted ? `${item.label}: exhausted (${windows})` : `${item.label}: ${windows}`;
      })
      : (snap && Array.isArray(snap.models) ? snap.models : []).map(item => {
        const used = usageOf(item);
        return `${item.label}: ${used.pct}% used (${used.leftPct}% left)`;
      });
    return {
      ok: true,
      projectId: row.id,
      answer: list.length ? `${lead} usage: ${list.join('; ')}.` : `${lead}: no plans loaded.`
    };
  }
  if (/\b(monitor|vivo|alive|stall|watch|controll)\b/i.test(q)) {
    return {
      ok: true,
      projectId: row.id,
      answer: `${lead}: ${row.orchestratorDoing || 'not monitoring a run.'} ${row.monitoring ? `${row.monitoring.alive}/${row.monitoring.workers} processes alive, ${Number.isFinite(row.monitoring.folderFiles) ? row.monitoring.folderFiles + ' files' : 'file count unavailable'}.` : ''} Waiting for: ${row.waitingFor || 'nothing named'}.`
    };
  }
  if (/\b(next|prossimo|waiting|aspet)\b/i.test(q)) {
    return { ok: true, projectId: row.id, answer: `${lead} on ${row.name}: waiting for ${row.waitingFor || 'nothing'}. Next: ${row.next || 'nothing queued.'}` };
  }
  if (/\b(log|output|detto|said)\b/i.test(q)) {
    const last = lastActivity(row.workerTail);
    return { ok: true, projectId: row.id, answer: last ? `${lead} last worker output on ${row.name}: ${last}` : `${lead}: no worker log yet on ${row.name}.` };
  }
  return { ok: true, projectId: row.id, answer: briefRow(row, lead) };
}

function looksLikeQuestion(question) {
  const t = String(question || '').trim();
  if (/\?$/.test(t)) return true;
  return /^(how |what |why |who |where |when |which |quanto |quale |come sta|come mai|che (ne |sta |fa )|chi |perch|status\b|quota\b|how far)/i.test(t);
}

function looksLikeWork(question) {
  const t = String(question || '').trim();
  if (/^(do it|fallo|procedi|smista|go)$/i.test(t)) return true;
  return /\b(implement(?:a|are)?|smista|dispatch|assign(?:a)? |metti i worker|dai il lavoro|do the (?:job|task|work)|fai (?:il |la )(?:job|task|lavoro)|fallo(?: fare)?|lavora(?:ci)?(?: su)?|procedi|esegui|costruisci|continua(?:re)?|continue\b|go ahead|vai avanti|fix (?:the |this )|sistema )\b/i.test(t);
}
function freshTaskIntent(question) {
  return /^(?:please\s+|per favore\s+)?(?:(?:vorrei|voglio|puoi|can you)\s+(?:fare\s+|fai\s+|an?\s+)?)?(?:implement(?:a|are)?|build|fix|sistema|esegui|fai|audit|review|analizza|controlla|verifica)\b/i.test(String(question || '').trim());
}

function checkpointTalk(reason) {
  const why = reason || 'deadline';
  if (why === 'application update') {
    return { activity: 'Paused for an application update. Checkpoint saved.',
      note: 'Paused for an application update. Checkpoint saved. Press Resume after restart.',
      orch: 'Saved unfinished work before updating.',
      doing: (name, title) => `Paused ${name}: ${title}. Update checkpoint saved.` };
  }
  if (why === 'stop' || why === 'shutdown') {
    return {
      activity: 'Stopped. Checkpoint saved.',
      note: 'Stopped. Checkpoint saved. Press Resume to continue.',
      orch: 'Stopped. Checkpoint saved.',
      doing: (name, title) => `Stopped ${name} · ${title}. Checkpoint saved.`
    };
  }
  if (why === 'deadline') {
    return {
      activity: 'Deadline reached. Checkpoint saved.',
      note: 'Deadline reached. Checkpoint saved. Resume next session.',
      orch: 'Paused at deadline. Checkpoint saved.',
      doing: (name, title) => `Deadline reached on ${name} · ${title}. Checkpoint saved.`
    };
  }
  return {
    activity: 'Job ended without completing. Checkpoint saved.',
    note: `Job ended (${why}). Checkpoint saved. Press Resume to continue.`,
    orch: 'Job ended without completing. Checkpoint saved.',
    doing: (name, title) => `${name} · ${title} ended without completing (${why}). Checkpoint saved.`
  };
}

function parseOrder(question, snap, projectId) {
  const q = String(question || '').trim();
  if (!q) return null;
  const ql = q.toLowerCase();
  const row = pickAskedProject(snap, projectId, q);
  const commandText = ql.replace(/[.!]+$/, '').trim();
  const projectCommand = verbs => {
    const match = commandText.match(new RegExp(`^(?:${verbs})(?:\\s+(.+))?$`, 'i'));
    if (!match) return false;
    const target = (match[1] || '').replace(/^on\s+/, '');
    return !target || Boolean(row && [row.id, row.name].some(name => String(name || '').toLowerCase() === target));
  };
  const models = snap && Array.isArray(snap.models) ? snap.models : [];
  const named = models.find(item => {
    const label = String(item.label || '').toLowerCase();
    const id = String(item.id || '').toLowerCase();
    return (label.length > 2 && ql.includes(label)) || (id && ql.includes(id));
  });
  const off = /\b(off|disable|spegni|disattiva|turn off)\b/i.test(q);
  const on = /\b(on|enable|accendi|attiva|turn on)\b/i.test(q);
  if (named && off && !on) return { action: 'model', id: named.id, enabled: false, label: named.label };
  if (named && on && !off) return { action: 'model', id: named.id, enabled: true, label: named.label };
  if (named && /\b(orchestrator|as lead|use as|usa come)\b/i.test(ql)) {
    return { action: 'lead', id: named.id, label: named.label };
  }
  if (projectCommand('stop|ferma|kill|arresta')) {
    return { action: 'stop', projectId: row && row.id, name: row && row.name };
  }
  if (projectCommand('resume|riprendi')) {
    return { action: 'resume', projectId: row && row.id, name: row && row.name };
  }
  if (projectCommand('approve run|approva|approve')) {
    return { action: 'approve', projectId: row && row.id, name: row && row.name };
  }
  if (projectCommand('skip grill|salta grill|skip the grill')) {
    return { action: 'skip', projectId: row && row.id, name: row && row.name };
  }
  if (/\bgrill\b/i.test(ql) && /\b(off|disable|spegni|disattiva)\b/i.test(ql)) {
    return { action: 'grillPropose', enabled: false };
  }
  if (/\bgrill with the docs\b/i.test(ql)) {
    return { action: 'grillPropose', enabled: true, kind: 'docs' };
  }
  if (/\bgrill me\b/i.test(ql) && !/\bskip\b/i.test(ql)) {
    return { action: 'grillPropose', enabled: true, kind: 'me' };
  }
  if (/\bgrill\b/i.test(ql) && /\b(on|enable|accendi|attiva)\b/i.test(ql) && !named) {
    return { action: 'grillPropose', enabled: true };
  }
  if (projectCommand('auto start|avvia|start')) {
    return { action: 'start', projectId: row && row.id, name: row && row.name };
  }
  const wantsMode = /\b(mode|modalit|switch to|set to|passa a)\b/i.test(ql);
  if (wantsMode && /\b(automatic|auto)\b/i.test(ql) && !/\bauto start\b/i.test(ql)) {
    return { action: 'mode', mode: 'auto' };
  }
  if (wantsMode && /\b(manual|manuale)\b/i.test(ql)) {
    return { action: 'mode', mode: 'manual' };
  }
  return null;
}

function researchIntent(question, history = []) {
  const q = String(question || '').trim();
  if (/^(research|investigate|cerca|ricerca|indaga)\b/i.test(q)) return true;
  if (/\b(fai|fa|manda|assegna|chiedi|vorrei|voglio|puoi|can you|please|have|ask|assign|delegate|send|avvia)\b[\s\S]*\b(research|researching|ricerca|ricerche|cercare|cerca|investiga|indaga|investigate)\b/i.test(q)) return true;
  return /^(fallo|procedi|vai|do it|go ahead|yes|s[iì])(?:[.!\s]|$)/i.test(q)
    && history.length > 0 && /\b(research|ricerca|ricerche|cercare|investigat\w*)\b/i.test(history[0].question + ' ' + history[0].answer);
}

function leadEnvelope(answer, actions) {
  if (actions !== undefined) return { answer, actions };
  const text = String(answer || '').trim();
  const raw = text.replace(/^```(?:json)?\s*/i, '').replace(/\s*```$/, '');
  if (!raw.startsWith('{')) return { answer: text, actions: [] };
  try {
    const parsed = JSON.parse(raw);
    if (!parsed || typeof parsed.answer !== 'string' || !Array.isArray(parsed.actions) || Object.keys(parsed).some(key => !['answer', 'actions'].includes(key))) {
      return { answer: text, actions: [], error: 'Invalid lead action envelope; no workers were dispatched.' };
    }
    return parsed;
  } catch (_) { return { answer: text, actions: [], error: 'Unreadable lead action envelope; no workers were dispatched.' }; }
}

function fleetOf(models, projects, leadId, plans) {
  const list = Array.isArray(models) ? models : [];
  const rows = Array.isArray(projects) ? projects : [];
  return list.map(item => {
    const assignments = [];
    rows.forEach(row => {
      const parts = Array.isArray(row.dispatch) ? row.dispatch : [];
      const hits = parts.filter(part => part && part.modelId === item.id);
      if (hits.length) {
        hits.forEach(part => {
          assignments.push({
            projectId: row.id,
            project: row.name,
            taskTitle: part.assignmentJobTitle || (part.historicalAssignment ? 'Historical assignment title not retained' : row.jobTitle) || '',
            attemptId: part.attemptId || null,
            historicalAssignment: Boolean(part.historicalAssignment),
            status: part.assignmentStatus || row.status,
            slice: part.slice || '',
            pid: part.pid || null,
            alive: Boolean(part.alive),
            activity: part.activity || (part.historicalAssignment ? 'Original activity is not retained for this historical assignment.' : row.workerActivity || '')
          });
        });
      } else if (row.modelId === item.id) {
        assignments.push({
          projectId: row.id,
          project: row.name,
          status: row.status,
          slice: '',
          pid: row.workerPid || null,
          alive: Boolean(row.workerAlive),
          activity: row.workerActivity || ''
        });
      }
    });
    const working = assignments.some(job => job.status === 'watching');
    const state = working ? 'working' : item.available === false ? 'unavailable' : item.providerEnabled === false ? 'provider-disabled' : !item.enabled ? 'restricted' : assignments.length ? 'assigned' : 'idle';
    const eligible = canOrchestrate(item);
    const lead = Boolean(leadId && item.id === leadId);
    const exhausted = planExhausted(plans, item.plan);
    return {
      id: item.id,
      label: item.label,
      plan: item.plan || '',
      providerId: item.providerId || item.provider,
      quotaPoolId: item.quotaPoolId || item.plan,
      orchestratorEligible: item.orchestratorEligible ?? (item.kind === 'premium' || item.kind === 'default'),
      workerEligible: item.workerEligible !== false,
      reviewerEligible: item.reviewerEligible !== false,
      supervisorEligible: roleEligibility(item).supervisor,
      roles: item.roles || withModelRoles(item).roles,
      roleEligibility: item.roleEligibility || roleEligibility(item),
      providerEnabled: item.providerEnabled !== false,
      kind: item.kind,
      enabled: Boolean(item.enabled),
      available: item.available !== false,
      unavailableReason: item.unavailableReason || null,
      catalogExcluded: item.catalogExcluded === true,
      availabilityVerified: item.availabilityVerified === true,
      availabilityCheckedAt: item.availabilityCheckedAt || null,
      availabilityWarning: item.availabilityWarning || null,
      accountType: item.accountType || 'unread',
      accountKey: item.accountKey || null,
      provider: item.provider || null,
      cli: item.cli || null,
      exhausted,
      role: lead ? 'Lead' : item.roles ? ROLE_IDS.filter(role => item.roles[role]).map(role => role[0].toUpperCase() + role.slice(1)).join(' / ') || 'No allowed roles' : (eligible ? 'Eligible' : 'Worker'),
      eligible,
      lead,
      state: exhausted && !working ? 'exhausted' : state,
      busy: working,
      assignments
    };
  });
}

function collectInterventions(projects) {
  const items = [];
  (projects || []).forEach(row => {
    (row.log || []).forEach(line => {
      const text = String((line && line.text) || '');
      if (/Switched from|No remaining worker|worker failed|stalled|\bstall\b|no verdict|Checkpoint saved/i.test(text)) {
        items.push({ at: line.at, projectId: row.id, project: row.name, text });
      }
    });
  });
  return items.sort((a, b) => String(b.at || '').localeCompare(String(a.at || ''))).slice(0, 12);
}

function analysisOf(row) {
  if (!row) return '';
  const bits = [];
  if (row.orchestratorDoing) bits.push(row.orchestratorDoing);
  if (row.workerActivity && row.workerActivity !== row.orchestratorDoing) bits.push(row.workerActivity);
  if (/Hit an error/i.test(row.workerActivity || '')) {
    bits.push('This worker looks inefficient or unhealthy. I will switch if it stalls or dies.');
  }
  if (Array.isArray(row.tried) && row.tried.length) bits.push(`Already tried: ${row.tried.join(', ')}.`);
  if (row.waitingFor) bits.push(`Waiting for: ${row.waitingFor}.`);
  return bits.join(' ');
}

function resultStatus(file) {
  if (!file) return 'MISSING';
  try {
    const raw = fs.readFileSync(file, 'utf8');
    if (/(^|\n)\s*STATUS:\s*COMPLETE\b/i.test(raw)) return 'COMPLETE';
    if (/(^|\n)\s*STATUS:\s*FAILED\b/i.test(raw)) return 'FAILED';
    if (/(^|\n)\s*STATUS:\s*BLOCKED\b/i.test(raw)) return 'BLOCKED';
    if (/(^|\n)\s*STATUS:\s*WATCHING\b/i.test(raw)) return 'WATCHING';
    return 'OTHER';
  } catch (_) {
    return 'MISSING';
  }
}

function logStat(file) {
  if (!file) return '0|0';
  try {
    const st = fs.statSync(file);
    return `${st.size}|${Math.floor(st.mtimeMs)}`;
  } catch (_) {
    return '0|0';
  }
}

function progressKey(facts, logPath, dispatch) {
  const fileCount = facts && Number.isFinite(facts.fileCount) ? facts.fileCount : 0;
  const names = facts && Array.isArray(facts.names) ? facts.names.join(',') : '';
  const dirty = facts && Array.isArray(facts.dirty) ? facts.dirty.length : 0;
  const logs = Array.isArray(dispatch) && dispatch.length
    ? dispatch.map(part => logStat(part && part.logPath)).join(';')
    : logStat(logPath);
  return `${fileCount}|${names}|${dirty}|${logs}`;
}

function runGit(folder, args) {
  try {
    const { spawnSync } = require('node:child_process');
    const result = spawnSync('git', ['--no-optional-locks', '-C', folder, ...args], {
      encoding: 'utf8',
      timeout: 4000,
      windowsHide: true,
      maxBuffer: 65536
    });
    if (result.error || result.status !== 0) return '';
    const output = String(result.stdout || '');
    return args[0] === 'status' ? output.replace(/\r?\n$/, '') : output.trim();
  } catch (_) {
    return '';
  }
}

function defaultInspect(folder) {
  if (!folder || typeof folder !== 'string') return { error: 'no folder' };
  try {
    const st = fs.statSync(folder);
    if (!st.isDirectory()) return { error: 'not a directory', folder };
    const names = fs.readdirSync(folder).filter(name => name !== '.' && name !== '..');
    const gitDir = path.join(folder, '.git');
    const git = fs.existsSync(gitDir);
    let head = '';
    if (git) {
      try { head = fs.readFileSync(path.join(gitDir, 'HEAD'), 'utf8').trim(); } catch (_) {}
    }
    const branch = git ? (runGit(folder, ['rev-parse', '--abbrev-ref', 'HEAD']) || '') : '';
    const lastCommit = git ? runGit(folder, ['log', '-1', '--format=%h %s']) : '';
    const lastSubject = git ? runGit(folder, ['log', '-1', '--format=%s']) : '';
    const recent = git ? runGit(folder, ['log', '-5', '--oneline']).split(/\r?\n/).filter(Boolean).slice(0, 5) : [];
    const dirty = git ? runGit(folder, ['status', '--porcelain']).split(/\r?\n/).filter(Boolean).slice(0, 20) : [];
    const diffStat = git ? runGit(folder, ['diff', '--stat', 'HEAD']) : '';
    const docs = listDocs(folder);
    return {
      folder,
      fileCount: names.length,
      names: names.slice(0, 24),
      docs,
      git,
      head,
      branch,
      lastCommit,
      commitId: git ? runGit(folder, ['rev-parse', '--verify', 'HEAD']) || null : null,
      lastSubject,
      recent,
      dirty,
      diffStat: clip(diffStat, 400)
    };
  } catch (_) {
    return { error: 'folder not found', folder };
  }
}

function jobFromFacts(revision, facts) {
  const goal = revision && typeof revision.direction === 'string' ? revision.direction.trim() : '';
  const git = Boolean(facts && facts.git);
  const subject = (facts && facts.lastSubject) || 'latest repo changes';
  const title = git
    ? (clip(`Continue: ${subject}`, 120) || 'Continue from the repo')
    : (clip(goal, 120) || clip(`Continue: ${subject}`, 120) || 'Continue from the repo');
  const dirty = facts && Array.isArray(facts.dirty) ? facts.dirty.slice(0, 12) : [];
  const recent = facts && Array.isArray(facts.recent) ? facts.recent.slice(0, 5) : [];
  const detail = [
    goal ? `Goal: ${clip(goal, 400)}` : '',
    facts && facts.lastCommit ? `Latest commit: ${facts.lastCommit}` : (facts && facts.git ? 'Git present.' : 'No git history.'),
    recent.length ? `Recent:\n${recent.join('\n')}` : '',
    dirty.length ? `Uncommitted:\n${dirty.join('\n')}` : 'Working tree clean.',
    'Read the repo, take the latest changes, and proceed with the notebook plan.'
  ].filter(Boolean).join('\n\n');
  return { title, detail };
}

function planLines(item, facts, shown, active, extras) {
  const extra = extras && typeof extras === 'object' ? extras : {};
  const latest = item.revisions.at(-1);
  const goal = latest && latest.direction ? clip(latest.direction, 180) : '';
  const lines = [`Stay inside ${item.project.folder}.`];
  if (goal) lines.push(`Notebook goal: ${goal}`);
  if (facts && facts.git) {
    lines.push(`Latest git: ${facts.lastCommit || facts.head || 'present'}${facts.branch ? ` on ${facts.branch}` : ''}.`);
    if (facts.dirty && facts.dirty.length) lines.push(`Uncommitted: ${facts.dirty.length} path(s). Read those changes.`);
    else lines.push('Working tree clean. Proceed from the latest commit.');
  } else {
    lines.push(`Read-only inspect (${facts && facts.fileCount ? facts.fileCount : 0} top-level names).`);
  }
  lines.push('Proceed with the plan from those changes.');
  const dispatch = Array.isArray(extra.dispatch) ? extra.dispatch : [];
  if (dispatch.length > 1) {
    lines.push(`Split into ${dispatch.length} slices for parallel workers.`);
    dispatch.forEach((part, index) => {
      lines.push(`Slice ${index + 1}: ${part.label || part.modelId} — ${part.slice}.`);
    });
  } else {
    lines.push(`${active ? 'Assigned' : 'Pick'} ${shown.label || shown.id}${shown.why ? ` (${shown.why})` : ''}.`);
  }
  const job = extra.job || (item.tasks || []).find(task => task.status === 'running');
  if (job && job.timing === 'deadline' && job.deadlineAt) {
    lines.push(`Finish by ${job.deadlineAt}. At that time the orchestrator delivers the output or writes a checkpoint.`);
  } else {
    lines.push('Timing is at the worker’s discretion.');
  }
  if (extra.checkpoint && extra.checkpoint.path) {
    lines.push(`Resume from checkpoint ${extra.checkpoint.path}.`);
  }
  lines.push('Watch the worker process, the result file (STATUS: COMPLETE), and the project folder.');
  return lines;
}

function emptyEntry() {
  return {
    status: 'idle',
    doing: '',
    plan: [],
    facts: null,
    jobId: null,
    jobTitle: null,
    attemptId: null,
    modelId: null,
    approved: false,
    waitingFor: '',
    next: '',
    workerLabel: null,
    resultPath: null,
    watchingSince: null,
    holdApproval: false,
    grillSkipped: false,
    docs: [],
    log: [],
    pendingAutoStart: null,
    checkpoint: null,
    dispatch: [],
    resumeRequested: false,
    deadlineHonored: false,
    workerPid: null,
    workerLog: null,
    workerAlive: false,
    tried: [],
    progressAt: null,
    progressKey: null,
    workerTail: '',
    workerActivity: ''
  };
}

function jobFingerprint(job) {
  return crypto.createHash('sha256').update(JSON.stringify([job.id, job.title, job.detail, job.revision_id || null, ...(job.goldModel ? [job.goldModel] : [])])).digest('hex');
}
function checkpointMatches(checkpoint, job) {
  if (!checkpoint || !job) return false;
  if (checkpoint.jobId && checkpoint.jobId !== job.id) return false;
  if (checkpoint.jobFingerprint) return checkpoint.jobFingerprint === jobFingerprint(job);
  if (checkpoint.jobTitle && checkpoint.jobTitle !== job.title) return false;
  if (typeof checkpoint.detail === 'string' && checkpoint.detail !== job.detail) return false;
  return true;
}

function createOrchestrator(options) {
  const opts = options && typeof options === 'object' ? { ...options } : {};
  if (typeof opts.readPlan !== 'function') throw new Error('orchestrator requires readPlan');
  if (!opts.attempts) throw new Error('orchestrator requires attempts');
  const attempts = opts.attempts;
  const sentinel = opts.sentinel || null;
  const catalog = loadCatalog(opts.providers ? { schema: 3, providers: opts.providers } : opts.models || []);
  const models = opts.providers ? opts.models || catalog.models : Array.isArray(opts.models) ? opts.models : catalog.models;
  const providerDefinitions = opts.providers || catalog.providers;
  const sessionId = opts.sessionId || crypto.randomUUID();
  const inspect = typeof opts.inspectFolder === 'function' ? opts.inspectFolder : defaultInspect;
  const repositoryPending = new Map();
  const readRepository = typeof opts.readRepository === 'function' ? opts.readRepository : createRepositoryInspector();
  const now = typeof opts.now === 'function' ? opts.now : () => new Date().toISOString();
  const dir = typeof opts.dir === 'string' && opts.dir.trim() ? opts.dir : null;
  const stateFile = dir ? path.join(dir, 'orchestrator.json') : null;
  const ledger = dir ? createInvocationLedger(dir) : null;
  const supervisor = require('./supervisor.cjs').createSupervisor({ dir, now, review: reviewSnapshot });
  const askProvider = opts.askModel, launchWorker = opts.spawnWorker, abortProvider = opts.abortAsk;
  let activeLeadInvocation = null;
  if (typeof abortProvider === 'function' && ledger) opts.abortAsk = () => {
    if (activeLeadInvocation) ledger.finish(activeLeadInvocation, 'BLOCKED');
    return abortProvider();
  };
  if (typeof askProvider === 'function' && ledger) opts.askModel = async info => {
    const id = ledger.start({ role: 'orchestrator', projectId: info.projectId, megaTaskId: info.state?.executionHandoff?.megaTask?.id,
      provider: info.model.providerId || info.model.provider, model: info.model.cli, modelId: info.model.id, accountKey: info.model.accountKey, accountType: info.model.accountType, reason: info.allowActions === false ? 'clarification' : 'user reasoning request' });
    activeLeadInvocation = id;
    try {
      const result = await options.askModel(info);
      ledger.finish(id, result?.failure ? 'FAILED' : 'COMPLETE', result?.processExit, result?.tokens);
      return result;
    } catch (error) { ledger.finish(id, 'FAILED'); throw error; }
    finally { if (activeLeadInvocation === id) activeLeadInvocation = null; }
  };
  if (typeof launchWorker === 'function' && ledger) opts.spawnWorker = info => {
    const id = ledger.start({ role: 'worker', projectId: info.projectId, megaTaskId: info.megaTaskId, subTaskId: info.subTaskId, attemptId: info.attemptId,
      provider: info.worker.providerId || info.worker.provider, model: info.worker.cli, modelId: info.worker.id, accountKey: info.worker.accountKey, accountType: info.worker.accountType, reason: info.job?.kind === 'research' ? 'approved research' : 'approved work' });
    try { const receipt = options.spawnWorker(info); ledger.update(id, { pid: receipt?.pid, exitPath: info.exitPath, resultPath: info.resultPath }); if (!receipt?.pid) ledger.finish(id, 'FAILED'); return receipt; }
    catch (error) { ledger.finish(id, 'FAILED'); throw error; }
  };

  let store = { mode: 'manual', grillPropose: true, grillKind: 'me', usage: {}, enabled: {}, providerEnabled: {}, catalogVisibility: {}, modelPolicies: {}, providerPolicies: {}, projects: {}, asks: [], orchestratorId: null };
  let thinkingLive = null;
  let askToken = 0;
  let plansLive = emptyPlans();
  let plansAt = 0;
  let plansInflight = null;
  const loadPlans = typeof opts.fetchPlans === 'function' ? opts.fetchPlans : null;
  const plansTtl = Number.isFinite(opts.plansTtl) && opts.plansTtl >= 0 ? opts.plansTtl : 45000;
  if (stateFile) {
    try {
      const parsed = JSON.parse(fs.readFileSync(stateFile, 'utf8'));
      if (parsed && typeof parsed === 'object') {
        store = {
          mode: parsed.mode === 'auto' ? 'auto' : 'manual',
          grillPropose: parsed.grillPropose === false ? false : true,
          grillKind: parsed.grillKind === 'docs' ? 'docs' : (parsed.grillPropose === false || parsed.grillKind === 'off' ? 'off' : 'me'),
          usage: parsed.usage && typeof parsed.usage === 'object' ? parsed.usage : {},
          enabled: parsed.enabled && typeof parsed.enabled === 'object' ? parsed.enabled : {},
          providerEnabled: parsed.providerEnabled || {},
          catalogVisibility: parsed.catalogVisibility || {},
          modelPolicies: parsed.modelPolicies || {},
          providerPolicies: parsed.providerPolicies || {},
          modelRoles: parsed.modelRoles || {},
          accountModelRoles: parsed.accountModelRoles || {},
          accountBindings: parsed.accountBindings || {},
          lastAccountKeys: parsed.lastAccountKeys || {},
          projects: parsed.projects && typeof parsed.projects === 'object'
            ? Object.fromEntries(Object.entries(parsed.projects).map(([id, row]) => [
              id,
              row && typeof row === 'object' ? { ...emptyEntry(), ...row } : emptyEntry()
            ]))
            : {},
          asks: Array.isArray(parsed.asks) ? parsed.asks : [],
          researches: Array.isArray(parsed.researches) ? parsed.researches : [],
          workerHistory: Array.isArray(parsed.workerHistory) ? parsed.workerHistory : [],
          archivedProjects: parsed.archivedProjects && typeof parsed.archivedProjects === 'object' ? parsed.archivedProjects : {},
          execution: parsed.execution,
          briefingBaselines: parsed.briefingBaselines || {},
          selectedProjectId: parsed.selectedProjectId || null,
          sessions: Array.isArray(parsed.sessions) ? parsed.sessions : [],
          goldModelId: typeof parsed.goldModelId === 'string' ? parsed.goldModelId : null,
          orchestratorId: typeof parsed.orchestratorId === 'string' && parsed.orchestratorId.trim() ? parsed.orchestratorId : null
        };
      }
    } catch (_) {}
  }
  store.providerPolicies ||= {};
  store.modelPolicies ||= {};
  store.modelRoles ||= {};
  store.accountModelRoles ||= {};
  store.accountBindings ||= {};
  store.lastAccountKeys ||= {};
  store.briefingBaselines ||= {};
  if (!Array.isArray(store.asks)) store.asks = [];
  if (!Array.isArray(store.researches)) store.researches = [];
  if (!Array.isArray(store.workerHistory)) store.workerHistory = [];
  if (!Array.isArray(store.sessions)) store.sessions = [];
  const knownSessions = new Map(store.sessions.map(session => [session.id, session]));
  const historicalSessions = [...store.asks, ...store.researches, ...Object.values(store.projects).flatMap(row => row.dispatch || []), ...store.workerHistory.flatMap(row => row.dispatch || [])];
  for (const record of historicalSessions) if (record.sessionId && !knownSessions.has(record.sessionId)) knownSessions.set(record.sessionId, { id: record.sessionId, startedAt: record.startedAt || record.at || null });
  store.sessions = [...knownSessions.values()];
  store.sessions.push({ id: sessionId, startedAt: now() });
  let startupProjects = null;
  function rememberStartup(plan) {
    if (!startupProjects) startupProjects = new Map(notebookProjects(plan).map(item => [item.project.id, new Set((item.tasks || []).filter(task => task.status === 'running').map(task => task.id))]));
  }

  function persist() {
    if (!stateFile) return;
    fs.mkdirSync(path.dirname(stateFile), { recursive: true });
    const temporary = stateFile + '.' + crypto.randomUUID() + '.tmp';
    fs.writeFileSync(temporary, JSON.stringify(store) + '\n', { encoding: 'utf8', flag: 'wx' });
    fs.renameSync(temporary, stateFile);
  }

  const bridge = createExecutionBridge({ store, attempts, dir, rowFor: (id, megaTaskId) => store.researches.find(record => record.megaTaskId === megaTaskId) || entry(id), inspect, spawnWorker: opts.spawnWorker,
    isProcessAlive: opts.isProcessAlive, killWorker: opts.killWorker, models: roster,
    resolveWorker: (worker, sub) => sub?.type === 'RESEARCH' || sub?.goldModel && assignmentRole(sub) === 'worker' ? worker : resolveUsageFallback(worker, roster(), plansLive, candidate => permitsAssignment(candidate, sub) && candidate.providerEnabled !== false && (sub?.type === 'AUDIT' || candidate.capabilities?.canCode !== false) && (candidate.providerCatalog || candidate.remaining >= candidate.cost)),
    workerEligible: (worker, sub) => permitsAssignment(worker, sub) && (assignmentRole(sub) === 'reviewer' ? true : sub?.type === 'RESEARCH' ? worker.capabilities?.canResearch !== false : worker.capabilities?.canCode !== false) && worker.providerEnabled !== false && !usagePolicy(worker, plansLive).blocked && !planExhausted(plansLive, worker.quotaPoolId || worker.plan) && (worker.providerCatalog || worker.remaining >= worker.cost),
    now, persist, sessionId, leadId: () => leadModel()?.id || null });

  function entry(id) {
    if (!store.projects[id]) store.projects[id] = emptyEntry();
    return store.projects[id];
  }

  function note(id, text) {
    const row = entry(id);
    const line = { at: now(), text };
    row.log = [line, ...(Array.isArray(row.log) ? row.log : [])].slice(0, 12);
  }

  function resultPathFor(attemptId) {
    if (!dir) return '';
    return path.join(dir, 'results', `${attemptId}.txt`);
  }

  function writeWatchFile(file, body) {
    if (!file) return;
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, body, 'utf8');
  }

  function collectPids(row) {
    const pids = Array.isArray(row.dispatch)
      ? row.dispatch.filter(part => part && !part.stoppedByUser).map(part => part.pid).filter(pid => Number.isInteger(pid))
      : [];
    const stoppedPid = (row.dispatch || []).some(part => part && part.stoppedByUser && part.pid === row.workerPid);
    if (row.workerPid && !stoppedPid && !pids.includes(row.workerPid)) pids.push(row.workerPid);
    return pids;
  }

  function killDispatch(row) {
    collectPids(row).forEach(pid => {
      if (typeof opts.killWorker === 'function') {
        try { opts.killWorker(pid); } catch (_) {}
      } else {
        try { process.kill(pid); } catch (_) {}
      }
    });
    if (Array.isArray(row.dispatch)) {
      row.dispatch.forEach(part => {
        if (!part) return;
        part.pid = null;
        part.alive = false;
      });
    }
    row.workerPid = null;
    row.workerAlive = false;
  }

  function checkpointFile(id) {
    return dir ? path.join(dir, 'checkpoints', `${id}.json`) : null;
  }

  function readCheckpointFile(id) {
    const file = checkpointFile(id);
    if (!file) return null;
    try {
      const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (!parsed || typeof parsed !== 'object') return null;
      return { ...parsed, path: file, projectId: parsed.projectId || id };
    } catch (_) {
      return null;
    }
  }

  function paintCheckpoint(row, name, title) {
    const talk = checkpointTalk(row.checkpoint && row.checkpoint.reason);
    const jobTitle = title || (row.checkpoint && row.checkpoint.jobTitle) || row.jobTitle || 'job';
    row.status = 'checkpointed';
    row.doing = talk.doing(name || 'project', jobTitle);
    row.waitingFor = 'Resume from checkpoint';
    row.next = 'Press Resume to continue from the checkpoint.';
    row.orchestratorDoing = talk.orch;
    row.workerAlive = false;
    row.workerActivity = talk.activity;
    row.workerLabel = null;
    row.watchingSince = null;
    return row.checkpoint;
  }

  function hydrateCheckpoints() {
    if (!dir) return;
    const folder = path.join(dir, 'checkpoints');
    let names = [];
    try { names = fs.readdirSync(folder); } catch (_) { names = []; }
    const ids = new Set(Object.keys(store.projects || {}));
    names.forEach(name => {
      if (name.endsWith('.json')) ids.add(name.slice(0, -5));
    });
    let restored = false;
    ids.forEach(id => {
      const row = entry(id);
      const disk = readCheckpointFile(id);
      if (!row.checkpoint && disk) {
        row.checkpoint = disk;
        restored = true;
      } else if (row.checkpoint && disk && !row.checkpoint.path) {
        row.checkpoint.path = disk.path;
        restored = true;
      }
      if (row.checkpoint && !row.resumeRequested && row.status !== 'watching' && row.status !== 'complete' && row.status !== 'needs_approval') {
        paintCheckpoint(row, id, row.checkpoint.jobTitle);
        restored = true;
      }
    });
    if (restored) persist();
  }

  hydrateCheckpoints();

  function liveAttempt(id, job) {
    return attempts.getActiveAttempt({ projectId: id, taskId: job && job.id })
      || attempts.getActiveAttempt({ projectId: id });
  }

  function holdSavedCheckpoint(item, job) {
    const id = item.project.id;
    const row = entry(id);
    if (!row.checkpoint) {
      const disk = readCheckpointFile(id);
      if (disk && checkpointMatches(disk, job)) row.checkpoint = disk;
    }
    if (row.checkpoint && !checkpointMatches(row.checkpoint, job)) row.checkpoint = null;
    if (!row.checkpoint || row.resumeRequested) return false;
    if (liveAttempt(id, job)) return false;
    paintCheckpoint(row, item.project.name, (job && job.title) || row.checkpoint.jobTitle);
    return true;
  }

  function writeCheckpoint(id, item, job, facts, reason) {
    const row = entry(id);
    const why = reason || 'deadline';
    killDispatch(row);
    const payload = {
      at: now(),
      projectId: id,
      jobId: job && job.id,
      jobFingerprint: job ? jobFingerprint(job) : null,
      jobTitle: job && job.title,
      detail: job && job.detail,
      facts: facts || row.facts,
      plan: clone(row.plan || []),
      dispatch: clone(row.dispatch || []),
      reason: why,
      deadlineAt: job && job.deadlineAt || null
    };
    const file = checkpointFile(id);
    if (file) {
      fs.mkdirSync(path.dirname(file), { recursive: true });
      const temporary = file + '.' + crypto.randomUUID() + '.tmp';
      const fd = fs.openSync(temporary, 'wx');
      try { fs.writeFileSync(fd, JSON.stringify(payload, null, 2) + '\n', 'utf8'); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
      fs.renameSync(temporary, file);
    }
    row.checkpoint = { ...payload, path: file };
    const live = attempts.getActiveAttempt({ projectId: id, taskId: job && job.id })
      || attempts.getActiveAttempt({ projectId: id });
    if (live && typeof attempts.closeAttempt === 'function') {
      attempts.closeAttempt({ attemptId: live.id, reason: why });
    }
    row.attemptId = null;
    row.modelId = null;
    row.approved = false;
    row.resumeRequested = false;
    row.watchingSince = null;
    const talk = checkpointTalk(why);
    row.workerActivity = talk.activity;
    note(id, talk.note);
    return row.checkpoint;
  }

  function applyCheckpoint(id, item, job, facts, reason) {
    const row = entry(id);
    writeCheckpoint(id, item, job, facts, reason);
    return paintCheckpoint(row, item.project.name, (job && job.title) || 'job');
  }

  function markProgress(row, facts) {
    const key = progressKey(facts, row.workerLog, row.dispatch);
    const at = now();
    if (row.progressKey !== key) {
      row.progressKey = key;
      row.progressAt = at;
    } else if (!row.progressAt) {
      row.progressAt = at;
    }
    return key;
  }

  function refreshActivity(row, facts) {
    const parts = Array.isArray(row.dispatch) ? row.dispatch : [];
    parts.forEach(part => {
      if (!part) return;
      if (part.stoppedByUser) { part.alive = false; part.activity = 'Stopped by you. Resume the project to continue this slice.'; return; }
      part.alive = Boolean(part.pid) && typeof opts.isProcessAlive === 'function'
        ? Boolean(opts.isProcessAlive(part.pid))
        : Boolean(part.pid && row.workerAlive);
      const tail = tailLog(part.logPath);
      part.activity = interpretActivity(tail, facts, part.alive);
    });
    row.workerLog = (parts[0] && parts[0].logPath) || row.workerLog;
    row.workerPid = (parts[0] && parts[0].pid) || row.workerPid;
    row.workerAlive = parts.length
      ? parts.some(part => part.alive)
      : (Boolean(row.workerPid) && typeof opts.isProcessAlive === 'function'
        ? Boolean(opts.isProcessAlive(row.workerPid))
        : Boolean(row.workerPid));
    const tails = parts.map(part => tailLog(part.logPath)).filter(Boolean);
    row.workerTail = tails.join('\n---\n') || tailLog(row.workerLog);
    row.workerActivity = parts.map(part => part.activity).filter(Boolean).join(' · ')
      || interpretActivity(row.workerTail, facts, row.workerAlive);
    const files = facts && Number.isFinite(facts.fileCount) ? facts.fileCount : 0;
    row.monitoring = {
      workers: parts.length || (row.workerPid ? 1 : 0),
      alive: parts.filter(part => part.alive).length || (row.workerAlive ? 1 : 0),
      logMoving: Boolean(row.progressAt && row.progressKey && !String(row.progressKey).endsWith('|0|0')),
      folderFiles: files,
      lastProgress: row.progressAt || null
    };
    row.orchestratorDoing = orchestratorDoing(row);
    return row.workerActivity;
  }

  function launchAssigned(item, job, facts, chosen) {
    const id = item.project.id;
    const row = entry(id);
    const assigned = row.dispatch?.length ? row.dispatch.map(part => roster().find(model => model.id === part.modelId)) : [chosen];
    if (assigned.some(model => !model?.enabled || model.available === false || !permitsAssignment(model, job))) {
      row.status = 'blocked_no_worker'; row.next = 'Review the assigned models: their enabled roles do not permit this work.';
      return false;
    }
    const claimed = attempts.claim({
      taskId: job.id,
      projectId: id,
      modelId: chosen.id,
      candidates: roster()
    });
    if (!claimed || !claimed.ok) {
      row.status = 'blocked_no_worker';
      row.doing = `Could not assign a worker: ${claimed && claimed.reason ? claimed.reason : 'claim failed'}.`;
      row.waitingFor = 'A successful claim';
      row.next = claimed && claimed.reason ? claimed.reason : 'Claim failed.';
      row.workerLabel = null;
      return false;
    }
    row.attemptId = claimed.attempt.id;
    row.modelId = claimed.attempt.modelId;
    if (!Array.isArray(row.dispatch) || !row.dispatch.length) {
      row.dispatch = dispatchPlan([chosen], job, facts);
    }
    row.workerLabel = row.dispatch.map(part => part.label).join(' + ');
    const file = resultPathFor(claimed.attempt.id);
    row.resultPath = file || null;
    if (file) {
      attempts.attachResult({ attemptId: claimed.attempt.id, resultPath: file });
      const names = (facts.names || []).join(', ');
      writeWatchFile(file, [
        `PROJECT: ${item.project.name}`,
        `JOB: ${job.title}`,
        `WORKER: ${row.workerLabel}`,
        row.dispatch.length > 1 ? `SLICES:\n${row.dispatch.map((part, index) => `${index + 1}. ${part.label} — ${part.slice}`).join('\n')}` : '',
        job.timing === 'deadline' && job.deadlineAt ? `DEADLINE: ${job.deadlineAt}` : 'TIMING: discretion',
        row.checkpoint && row.checkpoint.path ? `CHECKPOINT: ${row.checkpoint.path}` : '',
        `FOLDER: ${item.project.folder}`,
        `GIT: ${facts.git ? facts.lastCommit || facts.head || 'yes' : 'no'}`,
        facts.branch ? `BRANCH: ${facts.branch}` : '',
        facts.dirty && facts.dirty.length ? `DIRTY: ${facts.dirty.length}` : 'DIRTY: 0',
        `FILES: ${facts.fileCount}`,
        names ? `TOP: ${names}` : '',
        'STATUS: WATCHING',
        ''
      ].filter(Boolean).join('\n'));
    }
    row.dispatch.forEach((part, index) => {
      const model = roster().find(itemModel => itemModel.id === part.modelId);
      if (model) {
        if (!store.usage[model.id]) store.usage[model.id] = { used: 0 };
        if (index > 0 || model.id !== chosen.id) store.usage[model.id].used += model.cost;
        else if (index === 0) store.usage[model.id].used += model.cost;
      }
      part.resultPath = index === 0 ? file : (dir ? path.join(dir, 'results', `${claimed.attempt.id}-s${index + 1}.txt`) : null);
      part.logPath = dir ? path.join(dir, 'logs', `${claimed.attempt.id}-s${index + 1}.log`) : null;
      if (index === 0 && dir) part.logPath = path.join(dir, 'logs', `${claimed.attempt.id}.log`);
      if (part.resultPath && index > 0) {
        writeWatchFile(part.resultPath, [
          `PROJECT: ${item.project.name}`,
          `JOB: ${job.title}`,
          `SLICE: ${part.slice}`,
          `WORKER: ${part.label}`,
          'STATUS: WATCHING',
          ''
        ].join('\n'));
      }
    });
    note(id, `Assigned ${row.workerLabel} to ${job.title} (${row.dispatch.length} slice${row.dispatch.length === 1 ? '' : 's'}). Watching for COMPLETE.`);
    row.watchingSince = now();
    row.progressAt = now();
    row.workerTail = '';
    row.workerActivity = 'Assigned. Waiting for the first worker output.';
    if (typeof opts.spawnWorker === 'function') {
      try {
        for (let index = 0; index < row.dispatch.length; index++) {
          const part = row.dispatch[index];
          const launched = opts.spawnWorker({
            projectId: item.project.id,
            folder: item.project.folder,
            resultPath: part.resultPath,
            job: {
              kind: job.kind || '',
              title: job.title,
              detail: [
                job.detail,
                `Slice: ${part.slice}`,
                row.checkpoint && row.checkpoint.path
                  ? `Resume from checkpoint ${row.checkpoint.path} (${row.checkpoint.reason || 'saved'} at ${row.checkpoint.at || 'unknown'}).${Array.isArray(row.checkpoint.plan) && row.checkpoint.plan.length ? `\n${row.checkpoint.plan.join('\n')}` : ''}`
                  : ''
              ].filter(Boolean).join('\n\n')
            },
            worker: roster().find(model => model.id === part.modelId) || { id: part.modelId, label: part.label },
            attemptId: `${claimed.attempt.id}-s${index + 1}`,
            logPath: part.logPath,
            slice: part.slice
          });
          if (!launched || !Number.isInteger(launched.pid) || launched.pid <= 0) throw new Error('worker did not start');
          part.pid = launched.pid;
          part.sessionId = sessionId;
          part.startedAt = now();
          part.logPath = launched.logPath || part.logPath;
          part.alive = true;
          note(id, `Started ${part.label} process ${launched.pid} on slice: ${part.slice}`);
        }
        row.workerPid = row.dispatch[0].pid;
        row.workerLog = row.dispatch[0].logPath;
      } catch (error) {
        row.workerPid = null;
        row.workerLog = null;
        if (file) {
          const raw = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
          writeWatchFile(file, (raw.replace(/(^|\n)\s*STATUS:\s*\S+/i, '$1STATUS: FAILED') || `STATUS: FAILED\n`) + `REASON: ${error.message || 'worker did not start'}\n`);
        }
        note(id, `Worker failed to start: ${error.message || 'worker did not start'}.`);
        return false;
      }
    }
    row.progressKey = progressKey(facts, row.workerLog, row.dispatch);
    return true;
  }

  function failover(item, job, facts, reason) {
    const id = item.project.id;
    const row = entry(id);
    const fromId = row.modelId;
    const fromLabel = row.workerLabel || fromId || 'worker';
    killDispatch(row);
    const live = attempts.getActiveAttempt({ projectId: id, taskId: job.id })
      || attempts.getActiveAttempt({ projectId: id });
    if (live && typeof attempts.closeAttempt === 'function') {
      attempts.closeAttempt({ attemptId: live.id, reason: reason || 'stalled' });
    }
    const usedIds = [fromId, ...((row.dispatch || []).map(part => part && part.modelId))].filter(Boolean);
    row.tried = Array.from(new Set([...(row.tried || []), ...usedIds]));
    row.workerPid = null;
    row.workerAlive = false;
    row.attemptId = null;
    const next = pickWorker(roster(), { job, usage: store.usage, facts, skip: row.tried, plans: plansLive });
    if (!next) {
      note(id, `Stopped. No remaining worker after ${reason || 'stall'}.`);
      applyCheckpoint(id, item, job, facts, reason || 'failed');
      return false;
    }
    const replacements = pickWorkers(roster(), { job, usage: store.usage, facts, skip: row.tried, plans: plansLive });
    const nextCrew = replacements.length ? replacements : [next];
    row.dispatch = dispatchPlan(nextCrew, job, facts);
    row.plan = planLines(item, facts, nextCrew[0] || next, false, { dispatch: row.dispatch, job, checkpoint: row.checkpoint });
    const lead = nextCrew[0] || next;
    const ok = launchAssigned(item, job, facts, lead);
    if (ok) note(id, `Switched from ${fromLabel} to ${lead.label || lead.id}: ${reason || 'stall'}.`);
    return ok;
  }

  function leadModel() {
    const list = roster();
    if (store.orchestratorId) {
      const picked = list.find(item => item.id === store.orchestratorId);
      if (picked && canOrchestrate(picked) && !usagePolicy(picked, plansLive).blocked && !planExhausted(plansLive, picked.quotaPoolId || picked.plan)) return picked;
      const fallback = resolveUsageFallback(picked, list, plansLive, canOrchestrate);
      if (fallback && fallback.id !== picked.id) return { ...fallback, fallbackFrom: picked.id };
    }
    return null;
  }

  function roster() {
    return normalizeModels(models).map(item => {
      const legacyEnabled = store.enabled[item.id] ?? item.enabled;
      const provider = providerDefinitions.find(p => p.id === (item.providerId || item.provider));
      const account = provider?.account;
      const scope = account?.key ? provider.id + ':' + account.key : null;
      const emptyRoles = () => Object.fromEntries(ROLE_IDS.map(role => [role, false]));
      const legacy = store.modelRoles[item.id] || item.roles || (legacyEnabled ? {} : emptyRoles());
      if (scope && !store.accountModelRoles[scope]) {
        const first = !store.accountBindings[provider.id];
        store.accountBindings[provider.id] ||= scope;
        store.accountModelRoles[scope] = Object.fromEntries(models.filter(model => model.providerId === provider.id || model.provider === provider.id).map(model => [model.id,
          first ? withModelRoles(model, store.modelRoles[model.id] || model.roles || ((store.enabled[model.id] ?? model.enabled) ? {} : emptyRoles())).roles : emptyRoles()]));
        persist();
      }
      const permissions = scope ? store.accountModelRoles[scope][item.id] || emptyRoles()
        : provider && store.accountBindings[provider.id] ? emptyRoles() : legacy;
      item.accountKey = account?.key || null;
      item.accountType = account?.type || 'unread';
      item.accountScope = scope;
      item.label = require('./provider-accounts.cjs').accountLabel(item.label, account);
      item.providerEnabled = provider ? (store.providerEnabled[provider.id] ?? provider.enabled) : true;
      item.policy = validatePolicy({ alwaysApprove: item.alwaysApprove ?? true, ...migratePolicy(item.policy || {}), ...migratePolicy(store.modelPolicies[item.id] || {}) });
      if (provider) {
        const shared = providerPolicyFor(provider, models, store).policy;
        item.policy = { minRemainingPercent: shared.minRemainingPercent, warningRemainingPercent: shared.warningRemainingPercent, fallbackModelId: null, alwaysApprove: shared.alwaysApprove };
        item.fallbackProviderId = shared.fallbackProviderId;
        item.policyScope = provider.id;
      }
      item.alwaysApprove = item.policy.alwaysApprove;
      item.usagePolicy = usagePolicy(item, plansLive);
      const used = usedOf(store.usage, item.id);
      const resolved = withModelRoles({ ...item, used, remaining: Math.max(0, item.limit - used) }, permissions);
      resolved.enabled = ROLE_IDS.some(role => resolved.roles[role]);
      return resolved;
    });
  }

  function researchModel(modelId) {
    const worker = roster().find(model => model.id === modelId);
    if (!worker) throw new Error('Requested research model is not in the roster: ' + modelId);
    if (!permitsAssignment(worker, { kind: 'research' })) throw new Error('The requested model is not enabled for the Worker role: ' + modelId);
    if (!worker.enabled || worker.available === false) throw new Error('Requested research model is disabled or unavailable: ' + modelId);
    if (!adapterFor(worker)?.sourceProtectedResearch) throw new Error('Source-protected research is not supported by the requested adapter: ' + worker.provider);
    const routeError = modelRouteError(worker);
    if (routeError) throw new Error(routeError);
    if (usagePolicy(worker, plansLive).blocked) throw new Error(usagePolicy(worker, plansLive).reason);
    if (planExhausted(plansLive, worker.plan) || worker.remaining < worker.cost) throw new Error('Requested research model has no remaining usage: ' + modelId);
    return worker;
  }

  function finishResearch(record, status, reason) {
    record.status = status;
    record.reason = reason || '';
    record.updatedAt = now();
    if (record.attemptId) attempts.closeAttempt({ attemptId: record.attemptId, reason: reason || status });
  }

  function refreshResearch() {
    for (const record of store.researches) {
      if (record.executionVersion === 2) {
        record.alive = (record.dispatch || []).some(part => part.alive);
        const part = (record.dispatch || []).at(-1);
        record.pid = part?.pid || null; record.folder = part?.folder || null; record.sessionId = part?.sessionId || record.sessionId;
        record.logPath = part?.logPath || null; record.workerTail = part ? tailLog(part.logPath) : '';
        const result = store.execution.results.filter(result => result.megaTaskId === record.megaTaskId).at(-1);
        record.reportPath = result?.reportPath || null; record.reportAvailable = Boolean(record.reportPath && fs.existsSync(record.reportPath));
        record.activity = result?.summary || record.doing || 'Awaiting MegaTask approval.';
        continue;
      }
      record.alive = Boolean(record.pid) && typeof opts.isProcessAlive === 'function' ? Boolean(opts.isProcessAlive(record.pid)) : Boolean(record.pid && record.status === 'watching');
      const tail = tailLog(record.logPath);
      if (tail !== record.workerTail) record.updatedAt = now();
      record.workerTail = tail;
      record.activity = interpretActivity(record.workerTail, null, record.alive);
      record.reportAvailable = false;
      try { record.reportAvailable = fs.statSync(record.reportPath).isFile() && fs.statSync(record.reportPath).size > 0; } catch (_) {}
      if (record.status !== 'watching') continue;
      const status = resultStatus(record.resultPath);
      let exitedSuccessfully = false;
      try { exitedSuccessfully = /(^|\n)\s*EXIT:\s*0\s*(?:\n|$)/i.test(fs.readFileSync(record.resultPath, 'utf8')); } catch (_) {}
      if (status === 'COMPLETE' && exitedSuccessfully) finishResearch(record, record.reportAvailable ? 'complete' : 'failed', record.reportAvailable ? 'Report and successful provider exit recorded.' : 'Provider exited successfully without the required report.md.');
      else if (status === 'FAILED') finishResearch(record, 'failed', 'Research provider or runner failed; inspect the log and result evidence.');
      else if (record.pid && typeof opts.isProcessAlive === 'function' && !record.alive) finishResearch(record, 'failed', 'Research process exited without successful completion evidence.');
    }
  }

  function launchResearch(record) {
    if (shuttingDown) throw new Error('The notebook is shutting down.');
    if (!dir || typeof opts.spawnWorker !== 'function') throw new Error('Research worker launcher or attempt storage is unavailable.');
    const worker = researchModel(record.modelId);
    const claim = attempts.claim({ taskId: record.id, projectId: 'research:' + record.id, modelId: worker.id, candidates: roster() });
    if (!claim.ok) throw new Error(claim.reason || 'Research attempt claim failed.');
    record.attemptId = claim.attempt.id;
    record.folder = path.join(dir, 'research', record.id, record.attemptId);
    record.reportPath = path.join(record.folder, 'report.md');
    record.resultPath = path.join(record.folder, 'result.txt');
    record.logPath = path.join(record.folder, 'worker.log');
    fs.mkdirSync(record.folder, { recursive: true });
    writeWatchFile(record.resultPath, 'STATUS: WATCHING\n');
    attempts.attachResult({ attemptId: record.attemptId, resultPath: record.resultPath });
    record.status = 'watching';
    record.sessionId = sessionId;
    record.startedAt = now();
    record.reason = '';
    record.workerLabel = worker.label || worker.id;
    record.provider = worker.provider;
    record.cli = worker.cli;
    record.updatedAt = now();
    try {
      const launched = opts.spawnWorker({ projectId: record.projectId, folder: record.folder, resultPath: record.resultPath, logPath: record.logPath, attemptId: record.attemptId, worker,
        job: { kind: 'research', title: record.title, detail: [record.detail,
          record.sourceFolder ? 'Read-only source folder: ' + record.sourceFolder : '',
          record.github ? 'Source GitHub repository: ' + record.github : '',
          record.attempts && record.attempts.length ? 'Previous isolated evidence folders (read to continue): ' + record.attempts.map(attempt => attempt.folder).join(', ') : '',
          'Write findings and citations to report.md in this isolated folder. Linked project source must remain unchanged.'
        ].filter(Boolean).join('\n\n') }, slice: 'Research only; report.md; no source changes' });
      if (!launched || !Number.isInteger(launched.pid) || launched.pid <= 0) throw new Error('Research worker did not return a process ID.');
      record.pid = launched.pid;
      record.logPath = launched.logPath || record.logPath;
      record.alive = true;
      record.reportAvailable = false;
      if (!store.usage[worker.id]) store.usage[worker.id] = { used: 0 };
      store.usage[worker.id].used += worker.cost;
    } catch (error) { finishResearch(record, 'failed', error.message); throw error; }
  }

  async function dispatchResearch(action, stillAuthorized = () => true, goldModel = null) {
    try {
      if (!action || typeof action !== 'object' || Object.keys(action).some(key => !['type', 'projectId', 'modelId', 'title', 'detail'].includes(key)) || action.type !== 'dispatch_research') throw new Error('Invalid research dispatch action.');
      for (const key of ['projectId', 'modelId', 'title', 'detail']) if (typeof action[key] !== 'string' || !action[key].trim()) throw new Error('Research action requires ' + key + '.');
      if (action.title.length > 200 || action.detail.length > 12000) throw new Error('Research instructions are too large (title 200, detail 12000 characters).');
      const plan = await opts.readPlan();
      const project = notebookProjects(plan).find(item => item.project.id === action.projectId);
      if (!project) throw new Error('Research project not found: ' + action.projectId);
      await refreshPlans(false);
      researchModel(action.modelId);
      if (!dir || typeof opts.spawnWorker !== 'function') throw new Error('Research worker launcher or attempt storage is unavailable.');
      const duplicate = store.researches.find(record => record.status === 'watching' && record.projectId === action.projectId && record.modelId === action.modelId && record.title === action.title && record.detail === action.detail);
      if (!stillAuthorized()) throw new Error('Research request was cancelled before worker launch.');
      if (duplicate) return { ok: true, duplicate: true, research: clone(duplicate) };
      const record = { id: 'research-' + crypto.randomUUID(), projectId: action.projectId, projectName: project.project.name, modelId: action.modelId,
        title: action.title, detail: action.detail, sourceFolder: project.project.folder || null, github: project.project.github || null, createdAt: now(), attempts: [] };
      store.researches.push(record);
      if (opts.phase2) {
        Object.assign(record, { executionVersion: 2, job: { id: record.id, title: record.title, detail: record.detail, revision_id: project.revisions.at(-1).id, kind: 'research', detached: true, ...(goldModel ? { goldModel } : {}) },
          status: 'needs_approval', workerLabel: researchModel(action.modelId).label, provider: researchModel(action.modelId).provider, cli: researchModel(action.modelId).cli, sessionId, startedAt: null });
        const mega = bridge.propose(project, record.job, [{ modelId: action.modelId, slice: action.title, detail: action.detail, type: 'RESEARCH', dependsOn: [] }]);
        record.megaTaskId = mega.id; bridge.paint(mega); persist();
        return { ok: true, proposal: true, research: clone(record), snapshot: snapshot(plan) };
      }
      try { launchResearch(record); record.attempts.push({ attemptId: record.attemptId, folder: record.folder }); }
      catch (error) { finishResearch(record, 'failed', error.message); throw error; }
      finally { persist(); }
      return { ok: true, research: clone(record) };
    } catch (error) { return { ok: false, reason: error.message }; }
  }

  async function stopResearch(id, request = {}) {
    const record = store.researches.find(item => item.id === id);
    if (!record) return { ok: false, reason: 'Research job not found.' };
    if (record.executionVersion === 2) {
      if (request.projectId !== record.projectId || request.megaTaskId !== record.megaTaskId) return { ok: false, reason: 'Research target project and MegaTask must be explicit.' };
      const plan = await opts.readPlan(), item = notebookProjects(plan).find(item => item.project.id === request.projectId);
      if (!item) return { ok: false, reason: 'Research project no longer exists.' };
      try { bridge.stop(item, record.job, record.megaTaskId); return { ok: true, research: clone(record), snapshot: await tick() }; }
      catch (error) { return { ok: false, reason: error.message }; }
    }
    refreshResearch();
    if (record.status === 'watching') {
      if (record.alive && (typeof opts.killWorker !== 'function' || !opts.killWorker(record.pid))) return { ok: false, reason: 'Could not stop the research process.' };
      record.alive = false;
      finishResearch(record, 'checkpointed', 'Research stopped; existing evidence preserved.');
      persist();
    }
    return { ok: true, research: clone(record), snapshot: await tick() };
  }

  async function resumeResearch(id, request = {}) {
    const record = store.researches.find(item => item.id === id);
    if (!record) return { ok: false, reason: 'Research job not found.' };
    if (record.executionVersion === 2) {
      if (request.projectId !== record.projectId || request.megaTaskId !== record.megaTaskId) return { ok: false, reason: 'Research target project and MegaTask must be explicit.' };
      const plan = await opts.readPlan(), item = notebookProjects(plan).find(item => item.project.id === request.projectId);
      if (!item) return { ok: false, reason: 'Research project no longer exists.' };
      try { const resumed = bridge.resume(item, record.job, request); return resumed.ok ? { ...resumed, research: clone(record), snapshot: await tick() } : resumed; }
      catch (error) { return { ok: false, reason: error.message }; }
    }
    refreshResearch();
    if (!['checkpointed', 'failed'].includes(record.status)) return { ok: false, reason: 'Only stopped or failed research can resume.' };
    try { launchResearch(record); record.attempts.push({ attemptId: record.attemptId, folder: record.folder }); persist(); return { ok: true, research: clone(record), snapshot: await tick() }; }
    catch (error) { persist(); return { ok: false, reason: error.message }; }
  }

  function researchReport(id) {
    const record = store.researches.find(item => item.id === id);
    if (!record) return { ok: false, reason: 'Research job not found.' };
    refreshResearch();
    const root = path.resolve(dir, 'research', record.executionVersion === 2 ? record.megaTaskId : record.id);
    const reportPath = path.resolve(record.reportPath || root);
    if (!reportPath.startsWith(root + path.sep)) return { ok: false, reason: 'Invalid research report path.' };
    try {
      const canonicalRoot = fs.realpathSync(root);
      const canonicalReport = fs.realpathSync(reportPath);
      if (!canonicalReport.startsWith(canonicalRoot + path.sep)) return { ok: false, reason: 'Research report points outside its isolated folder.' };
      return { ok: true, id, status: record.status, report: fs.readFileSync(canonicalReport, 'utf8') };
    }
    catch (_) { return { ok: false, reason: 'Research report is not available yet.' }; }
  }

  function refreshPhase2Research(plan, schedule, projectId) {
    for (const record of store.researches.filter(record => record.executionVersion === 2 && (!projectId || record.projectId === projectId))) {
      const item = notebookProjects(plan).find(item => item.project.id === record.projectId);
      if (item) bridge.advance(item, record.job, [], schedule);
    }
  }

  async function approveResearch(id, request) {
    const record = store.researches.find(record => record.id === id && record.projectId === request.projectId && record.megaTaskId === request.megaTaskId);
    if (!record || record.executionVersion !== 2) return { ok: false, reason: 'Research proposal does not belong to this project and MegaTask.' };
    const plan = await opts.readPlan();
    if (!notebookProjects(plan).some(item => item.project.id === record.projectId)) return { ok: false, reason: 'Research project no longer exists.' };
    try { bridge.approve(record.projectId, record.megaTaskId); return { ok: true, snapshot: await tick() }; }
    catch (error) { return { ok: false, reason: error.message }; }
  }

  function snapshot(plan, projectId) {
    rememberStartup(plan);
    const projects = notebookProjects(plan);
    for (const item of projects.filter(item => !projectId || item.project.id === projectId)) {
      bridge.observe(item);
      const legacy = entry(item.project.id);
      if (opts.phase2 && !legacy.megaTaskId && legacy.status === 'watching' && (legacy.dispatch || []).some(part => part.pid)) {
        const parts = legacy.dispatch;
        for (const part of parts) part.alive = Boolean(part.pid && opts.isProcessAlive?.(part.pid));
        if (!parts.some(part => part.alive)) {
          const complete = parts.every(part => { try { const raw = fs.readFileSync(part.resultPath, 'utf8'); return resultStatus(part.resultPath) === 'COMPLETE' && /^EXIT:\s*0\s*$/m.test(raw); } catch { return false; } });
          legacy.status = complete ? 'complete' : 'failed'; legacy.workerAlive = false; legacy.holdApproval = true;
          legacy.doing = complete ? 'Historical completion and successful provider exit recorded.' : 'Historical worker ended without validated completion. Review the saved attempt before preparing new work.';
          const active = legacy.attemptId && attempts.getResult(legacy.attemptId);
          if (active?.status === 'active' && active.projectId === item.project.id) attempts.closeAttempt({ attemptId: active.id, reason: complete ? 'legacy completion' : 'technical failure: dead legacy worker without validated completion' });
          persist();
        }
      }
      const job = (item.tasks || []).find(task => task.executionVersion === 2 && task.status === 'running');
      if (job && bridge.find(item.project.id, job.id)) bridge.advance(item, job, [], false);
    }
    const lead = leadModel();
    refreshPhase2Research(plan, false, projectId);
    refreshResearch();
    const view = {
      updatedAt: now(),
      sessionId,
      selectedProjectId: store.selectedProjectId || null,
      execution: clone(store.execution),
      sessions: clone(store.sessions),
      workerHistory: clone(store.workerHistory),
      mode: store.mode === 'auto' ? 'auto' : 'manual',
      grillPropose: store.grillPropose !== false,
      grillKind: store.grillPropose === false ? 'off' : (store.grillKind === 'docs' ? 'docs' : 'me'),
      models: roster(),
      researches: clone(store.researches),
      asks: clone(store.asks || []),
      thinking: thinkingLive ? clone(thinkingLive) : null,
      asking: Boolean(thinkingLive),
      orchestratorId: lead ? lead.id : null,
      selectedOrchestratorId: store.orchestratorId || null,
      goldModelId: store.goldModelId || null,
      orchestratorUnavailableReason: store.orchestratorId && !lead ? 'Selected orchestrator is unavailable or blocked by its usage policy. Choose an eligible model or configure an explicit fallback.' : null,
      orchestratorLabel: lead ? lead.label : ORCH_NAME,
      orchestrators: roster().filter(canOrchestrate).map(item => ({ id: item.id, label: item.label, providerId: item.providerId || item.provider, plan: item.plan })),
      projects: projects.map(item => {
        const row = entry(item.project.id);
        const latest = item.revisions.at(-1);
        const docs = Array.isArray(row.docs) && row.docs.length ? row.docs : collectDocs(row.facts);
        const docsInspected = Boolean(row.facts && (!row.facts.folder || row.facts.folder === item.project.folder));
        const docsAvailability = docs.length ? 'available' : !item.project.folder && !item.project.github ? 'unlinked' : docsInspected && !item.project.github ? 'none' : 'unknown';
        const terminal = ['complete', 'failed', 'checkpointed'].includes(row.status);
        // Stored process flags are historical evidence, not current liveness.
        const dispatch = clone(row.dispatch || []).map((part, index) => {
          if (!part) return part;
          const alive = Boolean(part.pid) && typeof opts.isProcessAlive === 'function'
            ? Boolean(opts.isProcessAlive(part.pid)) : Boolean(part.pid && part.alive);
          const sub = row.subTasks?.find(sub => sub.id === part.subTaskId);
          const result = store.execution.results.find(result => result.attemptId === part.attemptId);
          const assignmentMega = store.execution.megaTasks.find(mega => mega.id === (part.megaTaskId || result?.megaTaskId));
          const historicalAssignment = Boolean(assignmentMega && assignmentMega.id !== row.megaTaskId);
          let diagnostic = null;
          try { diagnostic = JSON.parse(fs.readFileSync(part.resultPath + '.diagnostic.json', 'utf8')); } catch {}
          const activitySession = index < SESSION_LIMIT ? assignmentSession({
            projectId: item.project.id, row, part: { ...part, megaTaskId: assignmentMega?.id || part.megaTaskId, observedAlive: !terminal && !historicalAssignment && alive }, execution: store.execution, historical: historicalAssignment,
            tail: readRecordedTail(part.logPath, dir), index
          }) : null;
          return { ...part, diagnostic, activitySession, historicalAssignment,
            assignmentJobTitle: assignmentMega?.title || null,
            assignmentStatus: historicalAssignment ? ({ COMPLETE: 'complete', FAILED: 'failed', BLOCKED: 'blocked' }[result?.status] || 'historical') : row.status,
            ...(historicalAssignment ? { activity: result?.summary || 'Original activity is not retained for this historical assignment.' } : {}),
            observedAlive: alive, recordedSubTaskStatus: sub?.status || null, validatedResultStatus: result?.status || null, alive: !terminal && !historicalAssignment && !part.stoppedByUser && alive };
        });
        const workerAlive = !terminal && (dispatch.length ? dispatch.some(part => part && part.alive)
          : (Boolean(row.workerPid) && typeof opts.isProcessAlive === 'function'
            ? Boolean(opts.isProcessAlive(row.workerPid)) : Boolean(row.workerPid && row.workerAlive)));
        return {
          id: item.project.id,
          name: item.project.name,
          projectSkills: typeof opts.projectSkillState === 'function' ? clone(opts.projectSkillState(item.project.id)) : null,
          folder: item.project.folder || null,
          github: item.project.github || null,
          repository: row.repository && row.repository.folder === (item.project.folder || null) && row.repository.github === (item.project.github || null) ? clone(row.repository) : null,
          goal: latest && latest.direction ? latest.direction : '',
          notes: clone(item.home?.notes || []),
          stateArt: latest && latest.state ? latest.state : '',
          open: clone((latest && latest.open) || []),
          status: row.status,
          label: opts.phase2 && !row.megaTaskId && row.status === 'complete' ? 'Legacy completion recorded' : LABELS[row.status] || row.status,
          doing: opts.phase2 && !row.megaTaskId && row.status === 'complete' ? 'Historical completion recorded. This run predates structured Phase 2 results.' : row.doing,
          plan: clone(row.plan || []),
          facts: row.facts ? clone(row.facts) : null,
          goldModel: (item.tasks || []).find(task => task.id === row.jobId)?.goldModel || null,
          jobTitle: row.jobTitle,
          jobId: row.jobId,
          attemptId: row.attemptId,
          megaTaskId: row.megaTaskId || null,
          megaTask: row.megaTask || null,
          subTasks: row.subTasks || [],
          checkpointId: row.checkpoint?.id || null,
          resumableCheckpoints: bridge.view(item.project.id).checkpoints.filter(cp => ['PAUSED', 'FAILED'].includes(bridge.target(item.project.id, cp.megaTaskId).status)),
          modelId: row.modelId,
          workerLabel: row.workerLabel || null,
          waitingFor: row.waitingFor || '',
          next: row.megaTaskId ? executionNext(store.execution.megaTasks.find(mega => mega.id === row.megaTaskId && mega.projectId === item.project.id), store.execution) : row.next || '',
          step: stepOf(row),
          pipeline: pipelineOf(row),
          crew: crewOf({ ...row, dispatch, orchestratorDoing: orchestratorDoing(row), orchestratorLabel: lead ? lead.label : ORCH_NAME }, lead ? lead.label : ORCH_NAME, store.execution),
          resultPath: row.resultPath || null,
          watchingSince: row.watchingSince || null,
          approved: Boolean(row.approved),
          sessionId: row.authorizedSessionId || null,
          canStart: true,
          canResume: Boolean(row.status !== 'complete' && row.checkpoint && (item.tasks || []).some(task => task.status === 'running' && checkpointMatches(row.checkpoint, task))),
          canStop: Boolean(
            row.status === 'watching'
            && (item.tasks || []).some(task => task.status === 'running')
          ),
          pendingAutoStart: Boolean(row.pendingAutoStart),
          dispatch,
          activitySessionPagination: { available: true, total: pagedProjectActivitySessions({ projectId: item.project.id, row, history: store.workerHistory, execution: store.execution, offset: Number.MAX_SAFE_INTEGER }).total, pageSize: SESSION_LIMIT },
          activitySessions: projectActivitySessions({ projectId: item.project.id, row, current: dispatch.map(part => part?.activitySession),
            history: store.workerHistory, execution: store.execution, readTail: file => readRecordedTail(file, dir) }),
          checkpoint: row.checkpoint ? clone(row.checkpoint) : null,
          timing: ((item.tasks || []).find(task => task.status === 'running') || {}).timing || (row.pendingAutoStart && row.pendingAutoStart.timing) || null,
          deadlineAt: ((item.tasks || []).find(task => task.status === 'running') || {}).deadlineAt || (row.pendingAutoStart && row.pendingAutoStart.deadlineAt) || null,
          // Unknown linked projects can request explicit inspection; this is not a claim that docs exist.
          canGrillDocs: docsAvailability === 'available' || docsAvailability === 'unknown',
          docsAvailability,
          docs: clone(docs),
          grillSkipped: Boolean(row.grillSkipped),
          log: clone(row.log || []),
          workerPid: row.workerPid || null,
          workerAlive,
          workerLog: row.workerLog || null,
          workerTail: row.workerTail || '',
          workerActivity: row.workerActivity || '',
          orchestratorDoing: orchestratorDoing(row),
          orchestratorId: lead ? lead.id : null,
          orchestratorLabel: lead ? lead.label : ORCH_NAME,
          monitoring: row.monitoring ? { ...clone(row.monitoring), alive: dispatch.length ? dispatch.filter(part => part && part.alive).length : (workerAlive ? 1 : 0) } : null,
          progressAt: row.progressAt || null,
          tried: clone(row.tried || [])
        };
      })
    };
    view.projects.forEach(row => {
      row.analysis = analysisOf(row);
    });
    view.orchestratorFallbackFrom = lead?.fallbackFrom || null;
    for (const research of view.researches) {
      try { research.diagnostic = JSON.parse(fs.readFileSync(research.resultPath + '.diagnostic.json', 'utf8')); } catch {}
    }
    view.modelPolicySupported = true;
    view.usageAlerts = [];
    view.providerPolicySupported = true;
    view.plans = clone(plansLive);
    view.providers = providerViews(providerDefinitions, view.models, plansLive, store).map(provider => {
      const configured = providerPolicyFor(providerDefinitions.find(item => item.id === provider.id), models, store);
      const effective = { minRemainingPercent: configured.policy.minRemainingPercent, warningRemainingPercent: configured.policy.warningRemainingPercent, fallbackModelId: null, alwaysApprove: configured.policy.alwaysApprove };
      return { ...provider, policy: configured.policy, policySource: configured.source, usagePolicy: usagePolicy({ policy: effective, quotaPoolId: provider.quotaPoolId }, plansLive) };
    });
    view.usageAlerts = view.providers.filter(provider => ['warning', 'limit', 'unread'].includes(provider.usagePolicy.state)).map(provider => ({ providerId: provider.id, label: provider.label, ...provider.usagePolicy }));
    const researchRows = store.researches.map(record => ({ id: record.projectId, name: record.projectName, status: record.status,
      dispatch: [{ modelId: record.modelId, pid: record.pid, alive: record.alive, slice: 'Research: ' + record.title, activity: record.activity }] }));
    view.fleet = fleetOf(view.models, view.projects.concat(researchRows), view.orchestratorId, plansLive);
    view.interventions = collectInterventions(view.projects);
    ledger?.reconcile(view, opts.isProcessAlive);
    view.aiUsage = ledger?.view() || { orchestrator: [], workers: [], recent: [] };
    view.supervisor = supervisor.observe(view);
    return view;
  }

  async function reviewSnapshot({ projectId, modelId, question, facts, history }) {
    const model = roster().find(model => model.id === modelId);
    if (!model || !model.enabled || model.available === false || model.providerEnabled === false || model.roles?.supervisor !== true
      || usagePolicy(model, plansLive).blocked || planExhausted(plansLive, model.quotaPoolId || model.plan)
      || !model.providerCatalog && model.remaining < model.cost
      || !require('./supervisor.cjs').supported(model)) throw new Error('The selected Supervisor is unavailable, not permitted, or blocked by quota. No fallback was used.');
    if (typeof askProvider !== 'function') throw new Error('The model transport is unavailable. Live evidence remains available.');
    const invocation = ledger?.start({ role: 'supervisor', projectId, megaTaskId: facts.megaTaskId, provider: model.providerId || model.provider, model: model.cli, modelId, accountKey: model.accountKey, accountType: model.accountType, reason: 'Supervisor advisory question' });
    try {
      store.usage[model.id] = { used: usedOf(store.usage, model.id) + model.cost };
      persist();
      const result = await askProvider({ purpose: 'supervisor', allowActions: false, question, projectId, model, folder: null,
        state: { facts, history } });
      ledger?.finish(invocation, result?.failure ? 'FAILED' : 'COMPLETE', result?.processExit, result?.tokens);
      return { ...(typeof result === 'string' ? { answer: result } : result), provider: model.providerId || model.provider, model: model.cli };
    } catch (error) { ledger?.finish(invocation, 'FAILED'); throw error; }
  }

  async function supervisorRequest(action, input) {
    const snap = snapshot(await opts.readPlan());
    if (action === 'settings') return supervisor.settings(input, snap);
    if (action === 'ignore') { supervisor.ignore(input.projectId, input.alertId); return supervisor.view(snap); }
    if (action === 'ask') { await supervisor.ask(snap, input.projectId, input.question); return snapshot(await opts.readPlan()).supervisor; }
    throw new Error('Unsupported Supervisor action.');
  }

  function bindAccount(providerId, key, { commit = true } = {}) {
    const previous = store.lastAccountKeys[providerId];
    const changed = previous !== undefined && previous !== key;
    if (commit && previous !== key) { store.lastAccountKeys[providerId] = key; persist(); }
    return changed;
  }

  function invalidateAccountCache(providerId) {
    const provider = providerDefinitions.find(provider => provider.id === providerId);
    if (!provider) return;
    plansAt = 0;
    plansLive = plansLive.map(pool => pool.id !== provider.quotaPoolId ? pool : {
      id: pool.id, label: require('./provider-accounts.cjs').accountLabel(provider.label, provider.account),
      readable: false, exhausted: false, pct: null, leftPct: null, windows: [], reason: 'CLI account changed; remaining quota must be refreshed.'
    });
  }

  async function refreshPlans(force) {
    if (!loadPlans) return plansLive;
    const age = Date.now() - plansAt;
    if (!force && plansAt && age < plansTtl) return plansLive;
    if (plansInflight) return plansInflight;
    plansInflight = Promise.resolve()
      .then(() => loadPlans())
      .then(list => {
        if (!Array.isArray(list) || !list.length) throw new Error('Provider usage response is unreadable.');
        plansLive = list;
        plansAt = Date.now();
        plansInflight = null;
        return plansLive;
      })
      .catch(() => {
        plansInflight = null;
        plansLive = (plansLive.length ? plansLive : emptyPlans()).map(pool => ({ ...pool, readable: false, leftPct: null, pct: null, exhausted: false, windows: [], reason: 'Provider usage refresh failed; previous percentages cannot verify a current limit.' }));
        plansAt = Date.now();
        return plansLive;
      });
    return plansInflight;
  }

  function decide(item) {
    const id = item.project.id;
    const row = entry(id);
    const latest = item.revisions.at(-1);
    const job = (item.tasks || []).find(task => task.status === 'running') || null;
    if (job?.executionVersion === 2 && (!(latest?.open || []).length || job.source === 'chat')) {
      row.jobId = job.id; row.jobFingerprint = jobFingerprint(job); row.jobTitle = job.title; row.jobDetail = job.detail; row.jobRevisionId = job.revision_id;
      try {
        const existing = bridge.find(id, job.id);
        // Unapproved or paused work has no reason to inspect its source on every poll.
        const facts = existing && existing.status !== 'RUNNING' && row.facts ? row.facts : inspect(item.project.folder); row.facts = facts;
        const parts = job.proposedSubTasks?.length ? job.proposedSubTasks.map(sub => ({ ...sub, modelId: job.goldModel && assignmentRole(sub) === 'worker' ? job.goldModel.id : sub.assignedModelId, slice: sub.title }))
          : dispatchPlan(pickWorkers(roster(), { job, usage: store.usage, facts, plans: plansLive }), job, facts);
        return bridge.advance(item, job, parts);
      } catch (error) { row.status = 'blocked_no_worker'; row.doing = error.message; row.next = error.message; return row; }
    }
    const recordedFiles = (row.dispatch || []).map(part => part && part.resultPath).filter(Boolean);
    if (!recordedFiles.length && row.resultPath) recordedFiles.push(row.resultPath);
    if (job && row.jobId === job.id && row.jobFingerprint === jobFingerprint(job)
      && !(row.dispatch || []).some(part => part && part.stoppedByUser)
      && recordedFiles.length && recordedFiles.every(file => resultStatus(file) === 'COMPLETE')) {
      row.status = 'complete';
      row.doing = `Evidence complete for ${job.title} on ${item.project.name}.`;
      row.waitingFor = 'Nothing. Evidence is complete.';
      row.next = 'Read the result, or add a new job.';
      row.workerActivity = 'Evidence complete.';
      row.orchestratorDoing = 'Verified STATUS: COMPLETE and folder output.';
    }
    // A completed assignment must never be dispatched again on a later poll.
    const completedJob = job || (item.tasks || []).find(task => task.id === row.jobId && task.status === 'complete');
    if (row.status === 'complete' && completedJob && row.jobId === completedJob.id
      && (!row.jobFingerprint || row.jobFingerprint === jobFingerprint(completedJob))) {
      settleCompletedAttempt(row, id, completedJob);
      return row;
    }
    const open = job && job.source === 'chat' ? [] : latest && Array.isArray(latest.open) ? latest.open : [];
    const stallMs = Number.isFinite(opts.stallMs) && opts.stallMs >= 0 ? opts.stallMs : 90 * 1000;
    const worker = pickWorker(roster(), { job, usage: store.usage, facts: row.facts, skip: row.tried, plans: plansLive });

    if (job && row.jobId && (row.jobId !== job.id || (row.jobFingerprint && row.jobFingerprint !== jobFingerprint(job)))) {
      row.approved = false;
      row.holdApproval = false;
      row.attemptId = null;
      row.modelId = null;
      row.plan = [];
      row.dispatch = [];
      row.tried = [];
      row.resumeRequested = false;
      note(id, 'Job changed. Approval reset.');
    }
    row.jobId = job ? job.id : null;
    row.jobFingerprint = job ? jobFingerprint(job) : null;
    row.jobTitle = job ? job.title : null;
    row.jobDetail = job ? job.detail : null;
    row.jobRevisionId = job ? job.revision_id : null;
    const ongoing = (row.dispatch || []).some(part => part && !part.stoppedByUser && part.pid && typeof opts.isProcessAlive === 'function' && opts.isProcessAlive(part.pid));
    if (opts.enforceSessionBoundary && startupProjects?.get(id)?.has(job?.id) && row.authorizedSessionId !== sessionId && !ongoing) {
      if (row.status === 'complete') return row;
      row.approved = false;
      row.holdApproval = true;
    }

    if (job && holdSavedCheckpoint(item, job)) return row;

    if (!job) {
      const leftover = attempts.getActiveAttempt({ projectId: id });
      if (leftover && leftover.status === 'active' && typeof attempts.closeAttempt === 'function') {
        attempts.closeAttempt({ attemptId: leftover.id, reason: 'no running job' });
        note(id, 'Closed a leftover claim. No running job on this project.');
      }
      row.workerPid = null;
      row.workerLog = null;
      row.workerAlive = false;
      row.tried = [];
      row.progressAt = null;
      row.progressKey = null;
      row.workerTail = '';
      row.workerActivity = '';
    }

    if (open.length) {
      row.status = 'waiting_on_you';
      row.doing = `${open.length} open question${open.length === 1 ? '' : 's'} on ${item.project.name}. Grill me first. The orchestrator does not assign a worker yet.`;
      row.waitingFor = 'Grill me answers';
      row.next = row.pendingAutoStart
        ? 'In For you open Grill me, answer, Save answers. Or Skip grill on this Monitor row to plan anyway.'
        : 'In For you open Grill me, answer the questions, then Save answers.';
      row.workerLabel = null;
      row.watchingSince = null;
      row.facts = null;
      row.dispatch = [];
      row.approved = false;
      row.holdApproval = false;
      return row;
    }
    if (!job && row.pendingAutoStart && store.grillPropose !== false && !row.grillSkipped && !grillDone(item)) {
      let docs = [];
      if (item.project.folder) {
        const facts = inspect(item.project.folder);
        if (facts && !facts.error) {
          row.facts = facts;
          docs = collectDocs(facts);
        } else {
          row.facts = facts || null;
        }
      } else {
        row.facts = null;
      }
      row.docs = docs;
      row.status = 'propose_grill';
      row.doing = docs.length
        ? `Auto start on ${item.project.name} proposes grill first. Grill me, or Grill with the docs (${docs.join(', ')}). Skip if you will not rely on it.`
        : `Auto start on ${item.project.name} proposes grill first. Grill me, or set a folder to Grill with the docs. Skip if you will not rely on it.`;
      row.waitingFor = 'Grill me, Grill with the docs, or skip';
      row.next = docs.length ? 'Grill with the docs, or Grill me.' : 'Grill me. Set a folder to grill with the docs.';
      row.workerLabel = null;
      row.attemptId = null;
      row.watchingSince = null;
      row.dispatch = [];
      row.approved = false;
      row.holdApproval = false;
      return row;
    }
    if (!job) {
      row.status = 'idle';
      row.doing = row.pendingAutoStart
        ? `Auto start is waiting to plan ${item.project.name} after grill.`
        : `No job on ${item.project.name}. Auto start to check the project${store.grillPropose !== false ? ', grill if Grill me or Grill with the docs is on,' : ''} then approve the plan.`;
      row.waitingFor = 'Auto start';
      row.next = store.grillPropose !== false
        ? 'Grill me or Grill with the docs starts the orchestrator with that grill. Auto start skips grill when both are off.'
        : 'Auto start skips grill. Turn Grill me or Grill with the docs on to grill first.';
      row.workerLabel = null;
      row.attemptId = null;
      row.watchingSince = null;
      row.facts = null;
      row.docs = [];
      row.dispatch = [];
      row.approved = false;
      row.holdApproval = false;
      return row;
    }
    if (!item.project.folder) {
      row.status = 'blocked_no_folder';
      row.doing = `${item.project.name} has a job (${job.title}) but no local folder. Set place so the orchestrator can inspect the repo.`;
      row.waitingFor = 'A local folder';
      row.next = 'Set place with a folder on this PC.';
      row.workerLabel = null;
      return row;
    }
    const facts = inspect(item.project.folder);
    row.facts = facts;
    const workers = pickWorkers(roster(), { job, usage: store.usage, facts, plans: plansLive });
    if (facts && facts.error) {
      row.status = 'blocked_no_folder';
      row.doing = `Cannot inspect ${item.project.name}: ${facts.error}.`;
      row.waitingFor = 'A readable folder';
      row.next = `Fix the folder: ${facts.error}.`;
      row.workerLabel = null;
      return row;
    }
    if (!worker) {
      row.status = 'blocked_no_worker';
      row.doing = 'No eligible worker. Turn a model on, or wait until ChatGPT, Grok, or Gemini usage resets.';
      row.waitingFor = 'An enabled model whose provider still has usage';
      row.next = 'Turn a model on, or wait until a plan limit resets.';
      row.workerLabel = null;
      return row;
    }
    const active = attempts.getActiveAttempt({ projectId: id, taskId: job.id })
      || attempts.getActiveAttempt({ projectId: id });
    const assigned = active ? roster().find(itemModel => itemModel.id === active.modelId) : null;
    const shown = assigned || worker;
    const keepDispatch = Array.isArray(row.dispatch) && row.dispatch.some(part => part && (part.pid || part.resultPath));
    if (!keepDispatch) {
      row.dispatch = dispatchPlan(workers.length ? workers : (worker ? [worker] : []), job, facts);
    }
    row.plan = planLines(item, facts, shown, Boolean(active), { dispatch: row.dispatch, job, checkpoint: row.checkpoint });
    if (store.mode === 'auto' && !row.holdApproval) row.approved = true;

    const deadlineDue = job.timing === 'deadline' && job.deadlineAt && Date.parse(now()) >= Date.parse(job.deadlineAt) && !row.deadlineHonored;
    if (!row.approved) {
      row.status = 'needs_approval';
      const who = row.dispatch.length > 1
        ? row.dispatch.map(part => part.label).join(' + ')
        : (worker.label || worker.id);
      row.doing = `Execution plan ready for ${item.project.name} · ${job.title}. Approve to assign ${who}${worker.why ? ` (${worker.why})` : ''}.`;
      row.waitingFor = 'Your Approve run';
      row.next = `Approve to assign ${who}.`;
      row.workerLabel = who;
      row.watchingSince = null;
      return row;
    }
    if (deadlineDue && !active) {
      applyCheckpoint(id, item, job, facts, 'deadline');
      row.workerLabel = null;
      return row;
    }

    if (!active) {
      if (!launchAssigned(item, job, facts, worker)) return row;
    } else {
      row.attemptId = active.id;
      row.modelId = active.modelId;
      row.workerLabel = (assigned && (assigned.label || assigned.id)) || active.modelId;
      row.resultPath = active.resultPath || row.resultPath || resultPathFor(active.id) || null;
      if (!row.watchingSince) row.watchingSince = now();
    }

    const watching = attempts.getActiveAttempt({ projectId: id, taskId: job.id })
      || attempts.getActiveAttempt({ projectId: id });
    const pausedSlices = (row.dispatch || []).some(part => part && part.stoppedByUser);
    const sentinelTick = !pausedSlices && watching && sentinel && typeof sentinel.tick === 'function' ? sentinel.tick(watching) : null;
    const sliceFiles = (row.dispatch || []).map(part => part && part.resultPath).filter(Boolean);
    const allSlicesDone = sliceFiles.length
      ? sliceFiles.every(file => resultStatus(file) === 'COMPLETE')
      : resultStatus(row.resultPath) === 'COMPLETE';
    if (pausedSlices && (row.dispatch || []).every(part => part.stoppedByUser || resultStatus(part.resultPath) === 'COMPLETE')) {
      applyCheckpoint(id, item, job, facts, 'stop');
      return row;
    }
    if (allSlicesDone || (sentinelTick && sentinelTick.complete && sliceFiles.length <= 1)) {
      row.status = 'complete';
      row.doing = `Evidence complete for ${job.title} on ${item.project.name}.`;
      row.waitingFor = 'Nothing. Evidence is complete.';
      row.next = 'Read the result, or add a new job.';
      row.workerActivity = 'Evidence complete.';
      row.orchestratorDoing = 'Verified STATUS: COMPLETE and folder output.';
      settleCompletedAttempt(row, id, job);
      note(id, sliceFiles.length > 1 ? 'All worker slices reported STATUS: COMPLETE.' : 'Sentinel saw STATUS: COMPLETE.');
      return row;
    }
    const modelName = (roster().find(itemModel => itemModel.id === row.modelId) || {}).label || row.modelId || (worker && worker.label);
    if (deadlineDue) {
      applyCheckpoint(id, item, job, facts, 'deadline');
      row.workerLabel = modelName;
      return row;
    }
    row.workerAlive = Boolean(row.workerPid) && typeof opts.isProcessAlive === 'function'
      ? Boolean(opts.isProcessAlive(row.workerPid))
      : Boolean(row.workerPid);
    markProgress(row, facts);
    refreshActivity(row, facts);
    const sliceResults = (row.dispatch || []).map(part => part && part.resultPath).filter(Boolean);
    const failedFile = (sliceResults.length ? sliceResults : [row.resultPath]).some(file => resultStatus(file) === 'FAILED');
    const progressAge = row.progressAt ? Date.parse(now()) - Date.parse(row.progressAt) : 0;
    const stalled = Number.isFinite(progressAge) && progressAge >= stallMs;
    const dead = Array.isArray(row.dispatch) && row.dispatch.length
      ? row.dispatch.some(part => (
        part && part.pid
        && typeof opts.isProcessAlive === 'function'
        && !opts.isProcessAlive(part.pid)
        && resultStatus(part.resultPath) !== 'COMPLETE'
      ))
      : Boolean(row.workerPid) && typeof opts.isProcessAlive === 'function' && !row.workerAlive;
    const noVerdict = Boolean(sentinelTick && sentinelTick.closed);
    if (!pausedSlices && (failedFile || dead || stalled || noVerdict)) {
      const reason = failedFile ? 'worker failed' : (dead || noVerdict ? 'no verdict' : 'stall');
      if (failover(item, job, facts, reason)) {
        row.workerAlive = Boolean(row.workerPid) && typeof opts.isProcessAlive === 'function'
          ? Boolean(opts.isProcessAlive(row.workerPid))
          : Boolean(row.workerPid);
        markProgress(row, facts);
        refreshActivity(row, facts);
      } else {
        if (row.status !== 'checkpointed') applyCheckpoint(id, item, job, facts, reason);
        return row;
      }
    }
    row.status = 'watching';
    const gitBit = facts.git ? ` git ${facts.head || 'yes'}` : '';
    const crew = row.dispatch.length > 1 ? ` Dispatch: ${row.dispatch.map(part => part.label).join(' + ')}.` : '';
    const proc = row.workerPid
      ? ` Worker process ${row.workerPid}${row.workerAlive ? ' is running' : ' stopped'}.`
      : '';
    const activity = row.workerActivity ? ` ${row.workerActivity}` : '';
    row.orchestratorDoing = orchestratorDoing(row);
    row.doing = `${row.orchestratorDoing} ${modelName} on ${item.project.name} · ${job.title}.${proc}${activity} Inspected ${facts.fileCount} top-level names${gitBit}.${crew}`;
    row.waitingFor = row.workerActivity || (row.workerAlive
      ? 'Worker process, result file, project files'
      : 'A result file with STATUS: COMPLETE');
    row.next = row.workerAlive
      ? 'Keep watching the worker. Stall switches to the next model.'
      : (row.workerPid ? 'Worker process stopped. Switching if another model remains.' : 'Keep watching.');
    row.workerLabel = row.dispatch.length > 1 ? row.dispatch.map(part => part.label).join(' + ') : modelName;
    return row;
  }

  let shuttingDown = false;
  function settleCompletedAttempt(row, projectId, job) {
    row.workerAlive = false;
    for (const part of row.dispatch || []) if (part) part.alive = false;
    row.monitoring = { ...row.monitoring, alive: 0 };
    const files = (row.dispatch || []).map(part => part && part.resultPath).filter(Boolean);
    if (!files.length && row.resultPath) files.push(row.resultPath);
    // Historical UI state alone is insufficient evidence to close a claim.
    if (!files.length || !files.every(file => resultStatus(file) === 'COMPLETE')) return;
    const active = attempts.getActiveAttempt({ projectId, taskId: job.id });
    if (active && active.id === row.attemptId) attempts.closeAttempt({ attemptId: active.id, reason: 'complete' });
  }

  function refreshRepositoryMetadata(plan, projectId) {
    // Refresh metadata and inspection refs without merging or overwriting source files.
    for (const item of notebookProjects(plan).filter(item => (!projectId || item.project.id === projectId) && item.project.github)) {
      const row = entry(item.project.id);
      const checked = Date.parse(row.repository?.checkedAt || '');
      if (repositoryPending.has(item.project.id) || Number.isFinite(checked) && Date.now() - checked < (opts.repositoryPollMs ?? 300000)) continue;
      const work = Promise.resolve().then(() => readRepository(item.project)).then(repository => {
        if (shuttingDown) return;
        row.repository = repository;
        if (item.project.folder) row.facts = inspect(item.project.folder);
        persist();
      }).catch(error => {
        row.repository = { folder: item.project.folder || null, github: item.project.github, checkedAt: now(), error: 'Repository refresh failed.', comparison: { status: 'unknown', ahead: null, behind: null } };
      }).finally(() => repositoryPending.delete(item.project.id));
      repositoryPending.set(item.project.id, work);
    }
  }

  async function tick(projectId) {
    await refreshPlans(false);
    let plan = await opts.readPlan();
    rememberStartup(plan);
    refreshRepositoryMetadata(plan, projectId);
    if (!shuttingDown) for (const item of notebookProjects(plan).filter(item => !projectId || item.project.id === projectId)) decide(item);
    refreshPhase2Research(plan, !shuttingDown, projectId);
    refreshResearch();
    if (typeof opts.completeTasks === 'function') {
      const completed = notebookProjects(plan).filter(item => !projectId || item.project.id === projectId).flatMap(item => {
        const row = entry(item.project.id);
        const job = (item.tasks || []).find(task => task.status === 'running' && task.id === row.jobId);
        const files = (row.dispatch || []).map(part => part && part.resultPath).filter(Boolean);
        if (!files.length && row.resultPath) files.push(row.resultPath);
        return row.status === 'complete' && job && row.jobFingerprint === jobFingerprint(job)
          && (job.executionVersion === 2 ? bridge.find(item.project.id, job.id)?.status === 'COMPLETE' : files.length && files.every(file => resultStatus(file) === 'COMPLETE'))
          ? [{ projectId: item.project.id, taskId: job.id, fingerprint: row.jobFingerprint, attemptId: row.attemptId, megaTaskId: row.megaTaskId || null }] : [];
      });
      if (completed.length) plan = await opts.completeTasks(completed);
    }
    persist();
    return snapshot(plan, projectId);
  }

  async function setMode(mode) {
    store.mode = mode === 'auto' ? 'auto' : 'manual';
    persist();
    return tick();
  }

  async function setGrillPropose(enabled, kind) {
    store.grillPropose = enabled !== false;
    if (!store.grillPropose) store.grillKind = 'off';
    else if (kind === 'docs' || kind === 'me') store.grillKind = kind;
    else if (store.grillKind === 'off' || !store.grillKind) store.grillKind = 'me';
    persist();
    return tick();
  }

  async function skipGrill(projectId) {
    if (typeof projectId !== 'string' || !projectId.trim()) return { ok: false, reason: 'project not found' };
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(row => row.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    const row = entry(projectId);
    row.grillSkipped = true;
    row.grillAsked = true;
    note(projectId, 'Skipped grill proposal for this project.');
    persist();
    return { ok: true, snapshot: await tick() };
  }

  function docsFor(folder) {
    return collectDocs(inspect(folder));
  }

  async function runOrder(order) {
    if (!order || !order.action) return { ok: false, reason: 'invalid order' };
    if (order.action === 'quota') { await refreshPlans(); return { ok: true, snapshot: snapshot(await opts.readPlan()) }; }
    if (order.action === 'state') return { ok: true, snapshot: snapshot(await opts.readPlan()) };
    if (order.action === 'endGrill') return { ok: true, snapshot: snapshot(await opts.readPlan(), order.projectId) };
    if (order.action === 'model') return setModelEnabled(order.id, order.enabled);
    if (order.action === 'lead') return setOrchestrator(order.id);
    if (order.action === 'mode') {
      const snapshot = await setMode(order.mode);
      return { ok: true, snapshot };
    }
    if (order.action === 'grillPropose') {
      const snapshot = await setGrillPropose(order.enabled, order.kind);
      return { ok: true, snapshot };
    }
    if (!order.projectId) return { ok: false, reason: 'project not found' };
    if (order.action === 'stop') return stop(order.projectId);
    if (order.action === 'resume') return resume(order.projectId);
    if (order.action === 'approve') return approve(order.projectId, order.megaTaskId);
    if (order.action === 'skip') {
      if (typeof opts.applySkip === 'function') return opts.applySkip(order.projectId);
      return skipGrill(order.projectId);
    }
    if (order.action === 'start') {
      if (typeof opts.applyStart === 'function') return opts.applyStart(order.projectId, { timing: 'discretion', ...(order.goldModel ? { goldModel: order.goldModel } : {}), ...(order.grillContext ? { grillContext: order.grillContext } : {}) });
      const prepared = await startAutoTask(order.projectId, { timing: 'discretion', ...(order.goldModel ? { goldModel: order.goldModel } : {}), ...(order.grillContext ? { grillContext: order.grillContext } : {}) });
      return { ...prepared, snapshot: prepared.snapshot || await tick() };
    }
    if (order.action === 'watching') {
      return { ok: true, snapshot: await tick() };
    }
    return { ok: false, reason: 'unknown order' };
  }

  async function finishCancelled() {
    thinkingLive = null;
    return {
      ok: true,
      cancelled: true,
      asks: clone(store.asks || []),
      snapshot: await tick()
    };
  }

  async function abortAsk() {
    askToken += 1;
    const live = thinkingLive;
    thinkingLive = null;
    if (typeof opts.abortAsk === 'function') {
      try { opts.abortAsk(); } catch (_) {}
    }
    if (live && live.question) {
      const lead = leadModel();
      const started = live.startedAt ? Date.parse(live.startedAt) : NaN;
      const item = {
        at: now(),
        question: live.question,
        answer: 'Stopped.',
        thinking: live.thinking || null,
        ms: Number.isFinite(live.ms) ? live.ms : (Number.isFinite(started) ? Date.now() - started : null),
        projectId: live.projectId || null,
        chatSessionId: live.chatSessionId || null,
        sessionId,
        orchestratorId: lead ? lead.id : null,
        order: 'abort'
      };
      store.asks = [item, ...(Array.isArray(store.asks) ? store.asks : [])];
      persist();
    }
    return { ok: true, aborted: Boolean(live), asks: clone(store.asks || []), snapshot: await tick() };
  }

  async function ask(question, projectId, chatSessionId, requestMode = 'default', requestedGrillKind = null) {
    if (requestedGrillKind !== null && !['me', 'docs'].includes(requestedGrillKind)) return { ok: false, reason: 'Invalid Grill kind.' };
    if (!['default', 'gold'].includes(requestMode)) return { ok: false, reason: 'Invalid request mode.' };
    const selectedGold = requestMode === 'gold' ? roster().find(model => model.id === store.goldModelId) : null;
    if (requestMode === 'gold' && (!selectedGold || !selectedGold.enabled || selectedGold.available === false || selectedGold.providerEnabled === false || !permitsAssignment(selectedGold, {}))) return { ok: false, reason: 'Choose an available Gold model with the Worker role enabled. No substitute will be used.' };
    const goldModel = selectedGold ? { id: selectedGold.id, provider: selectedGold.providerId || selectedGold.provider, cli: selectedGold.cli } : null;
    const q = String(question || '').trim();
    if (!q) return { ok: false, reason: 'empty question' };
    if (Buffer.byteLength(q, 'utf8') > 1024 * 1024) return { ok: false, reason: 'message too long (maximum 1 MiB)' };
    const token = ++askToken;
    const askedAt = Date.now();
    await refreshPlans(false);
    let snap = snapshot(await opts.readPlan(), projectId || store.selectedProjectId);
    if (projectId === undefined && store.selectedProjectId) projectId = store.selectedProjectId;
    if (projectId !== undefined && !(snap.projects || []).some(row => row.id === projectId)) return { ok: false, reason: 'Selected project no longer exists. Select a project before sending.' };
    const mentionedProjects = (snap.projects || []).filter(row => [row.id, row.name].some(name => name && q.toLowerCase().includes(name.toLowerCase())));
    const explicitlyNamedCommand = mentionedProjects.find(row => [row.id, row.name].some(name => {
      const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      return new RegExp(`^(?:start|stop|resume|approve(?: run)?|avvia|ferma|riprendi|approva)\\s+${escaped}[.!?]*$`, 'i').test(q);
    }));
    if (opts.phase2 && explicitlyNamedCommand) projectId = explicitlyNamedCommand.id;
    if (projectId && mentionedProjects.some(row => row.id !== projectId)) return { ok: false, reason: 'Message names a different project from the selected recipient. Select the intended project before sending.' };
    if (!projectId && mentionedProjects.length > 1) return { ok: false, reason: 'Message names multiple projects. Select the intended recipient.' };
    if (token !== askToken) return finishCancelled();
    const contextProjectId = projectId || (mentionedProjects.length === 1 ? mentionedProjects[0].id : null);
    // Project and chat are independent boundaries; a reused chat ID never grants
    // access to another project's discussion or legacy unscoped records.
    const conversation = store.asks.filter(item =>
      (!contextProjectId || item.projectId === contextProjectId)
      && (!chatSessionId || item.chatSessionId === chatSessionId && (contextProjectId || item.projectId == null)));
    const typedGrill = /^grill me[.!?]*$/i.test(q) ? 'me' : /^grill with (?:the )?docs[.!?]*$/i.test(q) ? 'docs' : null;
    const previousGrill = conversation.find(turn => turn.projectId === projectId)?.grillKind || null;
    const requestedGrill = requestedGrillKind || typedGrill;
    const preliminaryOrder = requestedGrill ? null : /^(?:stop|finish|end) grill(?:ing)?[.!?]*$/i.test(q) ? { action: 'endGrill', projectId } : parseOrder(q, snap, projectId);
    const grillKind = requestedGrill || (!preliminaryOrder ? previousGrill : null);
    if (grillKind && !pickAskedProject(snap, projectId, q)) return { ok: false, reason: 'Select the project before starting Grill.' };
    const researchRequested = !grillKind && researchIntent(q, conversation);
    if (researchRequested && !pickAskedProject(snap, projectId, q)) return { ok: false, reason: 'Select a project or name it explicitly before dispatching research.' };
    const statusKind = /^(?:quota(?: remaining)?|remaining quota|how much quota is left\??|provider status|model status)$/i.test(q) ? 'quota'
      : /^(?:status|current (?:process )?state|current checkpoint(?: identity)?|selected project)$/i.test(q) ? 'state' : null;
    const order = grillKind || researchRequested ? null : statusKind ? { action: statusKind, projectId } : preliminaryOrder;
    if (order && ['start', 'stop', 'resume', 'approve', 'skip', 'watching'].includes(order.action) && !order.projectId) return { ok: false, reason: 'Select a project or name it explicitly before issuing a command.' };
    if (goldModel && order && order.action !== 'start') return { ok: false, reason: 'Gold applies to new work requests or Start. Use Default to approve or resume the existing task; its saved model constraint stays active.' };
    if (order) order.goldModel = goldModel;
    if (order?.action === 'start' && previousGrill) order.grillContext = conversation.filter(turn => turn.projectId === projectId && turn.grillKind).slice(0, 12).reverse().map(turn => `User: ${turn.question}
Orchestrator: ${turn.answer}`).join('\n\n').slice(-24000);
    if (order?.action === 'approve') order.megaTaskId = snap.projects.find(row => row.id === order.projectId)?.megaTaskId;
    let currentTask = null;
    if (!order && !grillKind && !researchRequested && freshTaskIntent(q) && typeof opts.prepareTask === 'function') {
      const target = pickAskedProject(snap, projectId, q);
      if (!target) return { ok: false, reason: 'Select the project for this new task.' };
      const prepared = await opts.prepareTask(target.id, q, () => token === askToken, goldModel);
      if (!prepared.ok) return prepared;
      currentTask = prepared.task;
      snap = prepared.snapshot;
    }
    let answer;
    let diagnostic = null;
    let invokedLead = null;
    let thinking = '';
    let ms = null;
    const actionResults = [];
    if (order) {
      const acted = await runOrder(order);
      snap = acted.snapshot || await tick(order.projectId);
      const who = (snap && snap.orchestratorLabel) || ORCH_NAME;
      if (acted.ok && order.action === 'quota') {
        answer = (snap.plans || []).map(pool => `${pool.label || pool.id}: ${Quota.describe(pool).text}${(pool.resetAt || pool.windows?.find(win => win.leftPct === pool.leftPct)?.resetAt) ? '; resets ' + (pool.resetAt || pool.windows.find(win => win.leftPct === pool.leftPct).resetAt) : ''}`).join('\n') || 'REMAINING UNREAD';
      } else if (acted.ok && order.action === 'state') {
        const selected = snap.projects.find(row => row.id === order.projectId);
        answer = selected ? `${selected.name}: ${selected.status}. ${selected.next || ''}${selected.checkpoint ? ' Checkpoint: ' + (selected.checkpoint.id || selected.checkpoint.path || 'saved') : ''}` : 'No project selected.';
      } else if (acted.ok && order.action === 'model') {
        answer = `${who}: ${order.label || order.id} is now ${order.enabled ? 'on' : 'off'}.`;
      } else if (acted.ok && order.action === 'lead') {
        answer = `${who}: orchestrator lead is now ${order.label || order.id}.`;
      } else if (acted.ok && order.action === 'mode') {
        answer = `${who}: mode is now ${order.mode === 'auto' ? 'Automatic' : 'Manual'}.`;
      } else if (acted.ok && order.action === 'endGrill') {
        answer = 'Grill session ended. Use Grill me to begin a new conversation.';
      } else if (acted.ok && order.action === 'grillPropose') {
        const kind = order.kind === 'docs' ? 'Grill with the docs' : (order.kind === 'me' ? 'Grill me' : 'Grill');
        answer = `${who}: ${kind} is now ${order.enabled ? 'on' : 'off'}. The orchestrator skips grill when both are off.`;
      } else if (acted.ok && order.action === 'start') {
        const phase = acted.phase === 'grill'
          ? 'Grill first, then workers can start'
          : (acted.phase === 'plan' ? 'Plan ready. Approve to assign workers' : 'started');
        answer = `${who}: ${phase} on ${order.name || order.projectId}. I dispatch; I do not do the job.`;
      } else if (acted.ok && order.action === 'approve') {
        const rowNow = (snap.projects || []).find(item => item.id === order.projectId);
        const crew = rowNow && Array.isArray(rowNow.dispatch) && rowNow.dispatch.length
          ? rowNow.dispatch.map(part => part.label).join(' + ')
          : (rowNow && rowNow.workerLabel) || 'workers';
        answer = `${who}: assigned ${crew} on ${order.name || order.projectId}. I dispatch; I do not do the job.`;
      } else if (acted.ok && order.action === 'watching') {
        const rowNow = (snap.projects || []).find(item => item.id === order.projectId);
        const crew = rowNow && Array.isArray(rowNow.dispatch) && rowNow.dispatch.length
          ? rowNow.dispatch.map(part => part.label).join(' + ')
          : (rowNow && rowNow.workerLabel) || 'workers';
        answer = `${who}: ${order.name || order.projectId} is already with ${crew}. I dispatch; I do not do the job.`;
      } else if (acted.ok && order.action === 'skip') {
        answer = `${who}: skipped grill on ${order.name || order.projectId}.`;
      } else if (acted.ok) {
        answer = `${who}: ${order.action} on ${order.name || order.projectId}.`;
      } else {
        answer = `${who}: could not ${order.action}: ${acted.reason || 'failed'}.`;
      }
    } else {
      const row = pickAskedProject(snap, projectId, q);
      if (row) {
        const inspected = await inspectRepository(row.id);
        if (token !== askToken) return finishCancelled();
        if (inspected.ok) {
          snap = inspected.snapshot;
          Object.assign(row, snap.projects.find(item => item.id === row.id));
        }
      }
      const fallback = answerQuestion(q, snap, projectId);
      const lead = leadModel();
      const history = recentContext(conversation || []);
      if (grillKind && opts.mattSkillsEnabled?.(row?.id || projectId) === false) {
        answer = 'Matt skills are disabled. Enable Use Matt skills to start Matt grilling.';
      } else if (grillKind && typeof opts.askModel !== 'function') {
        answer = 'An orchestrator model connection is required for Matt grilling. Select an available lead and retry.';
      } else if (typeof opts.askModel === 'function' && !lead) {
        answer = 'Pick an orchestrator model first (GPT-6 Astra, Grok 4.7, …). I coordinate workers; I do not do the job.';
      } else if (typeof opts.askModel === 'function' && lead) {
        if (token !== askToken) return finishCancelled();
        const started = Date.now();
        thinkingLive = {
          question: q,
          projectId: contextProjectId,
          chatSessionId: chatSessionId || null,
          lead: lead.label,
          thinking: '',
          draft: '',
          startedAt: now(),
          ms: 0
        };
        try {
          invokedLead = lead;
          const spoken = await opts.askModel({
            question: q,
            model: lead,
            ...(grillKind ? { grillKind, allowActions: false } : {}),
            projectId: (row && row.id) || fallback.projectId,
            folder: row && (row.folder || (row.facts && row.facts.folder)) || null,
            github: row && row.github || null,
            state: Object.assign({}, compactState(packState(snap, row), store.briefingBaselines[row?.id]), { history, contextArchive: contextProjectId ? null : stateFile, earlierTurns: Math.max(0, (conversation || []).length - history.length), currentRequest: { projectId: row?.id || null, text: q, taskId: currentTask?.id || null, requestMode, goldModel, grillKind } }),
            onProgress: part => {
              if (!part || typeof part !== 'object') return;
              thinkingLive = {
                question: q,
                projectId: contextProjectId,
                chatSessionId: chatSessionId || null,
                lead: lead.label,
                thinking: typeof part.thinking === 'string' ? part.thinking : (thinkingLive && thinkingLive.thinking) || '',
                draft: typeof part.answer === 'string' ? (/^\s*(\{|```)/.test(part.answer) ? '' : part.answer) : (thinkingLive && thinkingLive.draft) || '',
                startedAt: thinkingLive && thinkingLive.startedAt,
                ms: Number.isFinite(part.ms) ? part.ms : Date.now() - started
              };
            }
          });
          const unpacked = spoken && typeof spoken === 'object' && !Array.isArray(spoken)
            ? {
              answer: typeof spoken.answer === 'string' ? spoken.answer.trim() : '',
              thinking: typeof spoken.thinking === 'string' ? spoken.thinking.trim() : '',
              ms: Number.isFinite(spoken.ms) ? spoken.ms : Date.now() - started
            }
            : {
              answer: typeof spoken === 'string' ? spoken.trim() : '',
              thinking: '',
              ms: Date.now() - started
            };
          if (row) store.briefingBaselines[row.id] = { head: row.facts?.lastCommit || '', dirty: (row.facts?.dirty || []).slice(0, 12) };
          thinking = unpacked.thinking;
          ms = unpacked.ms;
          diagnostic = spoken?.diagnostic || null;
          if (diagnostic?.code === 'PROVIDER_MODEL_UNAVAILABLE') {
            const rejected = models.find(model => model.id === lead.id);
            if (rejected) { rejected.available = false; rejected.unavailableReason = diagnostic.message; }
          }
          const envelope = spoken?.failure ? { answer: unpacked.answer, actions: [] } : leadEnvelope(unpacked.answer, spoken && typeof spoken === 'object' ? spoken.actions : undefined);
          if (envelope.answer) answer = envelope.answer;
          else answer = `${lead.label} returned no answer. The provider CLI in the project folder produced nothing.`;
          if (envelope.error) answer += '\n\n' + envelope.error;
          if (!Array.isArray(envelope.actions) || envelope.actions.length > 3) {
            answer += '\n\nInvalid lead actions; no workers were dispatched.';
          } else if (envelope.actions.length && !researchRequested) {
            answer += '\n\nResearch was not dispatched: this message did not authorize research work.';
            actionResults.push({ ok: false, reason: 'No research work authorization in the current message.' });
          } else {
            for (const action of envelope.actions) {
              if (token !== askToken) return finishCancelled();
              const mentioned = roster().filter(model => [model.label, model.cli].some(name => typeof name === 'string' && q.toLowerCase().includes(name.toLowerCase())));
              const requested = mentioned.length > 1 ? mentioned.filter(model => model.id !== lead.id) : mentioned;
              const receipt = goldModel && action?.modelId !== goldModel.id
                ? { ok: false, reason: 'Gold model requires the exact worker ' + goldModel.id + '. No model was substituted.' }
                : requested.length === 1 && action && action.modelId !== requested[0].id
                ? { ok: false, reason: 'The action chose a different model from the requested worker: ' + requested[0].id + '. No model was substituted.' }
                : action && row && action.projectId !== row.id
                  ? { ok: false, reason: 'Research action targets a different project from the selected recipient.' }
                  : await dispatchResearch(action, () => token === askToken, goldModel);
              actionResults.push(receipt);
              answer += receipt.ok
                ? receipt.proposal ? `\n\nResearch MegaTask ${receipt.research.megaTaskId} proposed for ${receipt.research.projectName}. Review the objective and approve this MegaTask before any worker starts.` : `\n\nNotebook: ${receipt.duplicate ? 'already running' : 'started'} ${receipt.research.workerLabel} (${receipt.research.provider}/${receipt.research.cli}), process ${receipt.research.pid}, for research "${receipt.research.title}". Source files are protected; findings will be saved to report.md.`
                : '\n\nResearch was not dispatched: ' + receipt.reason;
            }
          }
        } catch (error) {
          if (token !== askToken) return finishCancelled();
          ms = Date.now() - started;
          diagnostic = error?.code === 'PROJECT_SKILL_INTERVIEW_UNAVAILABLE' || grillKind && /Matt .*skill/i.test(error?.message || '') ? null : diagnoseProvider(lead, error?.message || 'CLI error');
          answer = diagnostic ? diagnostic.message : error.message;
        }
        thinkingLive = null;
        if (token !== askToken) return finishCancelled();
      } else {
        answer = fallback.answer;
      }
    }
    if (token !== askToken) return finishCancelled();
    if (!Number.isFinite(ms)) ms = Date.now() - askedAt;
    if (currentTask) answer += `\n\nNew task prepared from this message: ${currentTask.title}. Approve this task to assign workers. No previous checkpoint was resumed.`;
    const row = pickAskedProject(snap, projectId, q);
    const lead = invokedLead || leadModel();
    const item = {
      at: now(),
      question: q,
      answer,
      diagnostic,
      thinking: thinking || null,
      ms: Number.isFinite(ms) ? ms : null,
      projectId: (row && row.id) || null,
      sessionId,
      chatSessionId: chatSessionId || null,
      requestMode, goldModel, grillKind,
      orchestratorId: lead ? lead.id : null,
      order: order ? order.action : null
    };
    if (actionResults.length) item.actionResults = clone(actionResults);
    store.asks = [item, ...(Array.isArray(store.asks) ? store.asks : [])];
    persist();
    refreshResearch();
    snap = snapshot(await opts.readPlan(), projectId);
    return {
      ok: true,
      question: q,
      answer,
      thinking: item.thinking,
      ms: item.ms,
      projectId: item.projectId,
      at: item.at,
      orchestratorId: item.orchestratorId,
      asks: clone(store.asks),
      snapshot: snap,
      order: order ? order.action : null,
      actionResults
    };
  }

  async function setOrchestrator(id) {
    if (typeof id !== 'string' || !id.trim()) {
      store.orchestratorId = null;
      persist();
      return { ok: true, snapshot: await tick() };
    }
    const found = roster().find(item => item.id === id);
    if (!found) return { ok: false, reason: 'model not present' };
    if (!canOrchestrate(found)) return { ok: false, reason: 'model not eligible' };
    store.orchestratorId = found.id;
    persist();
    return { ok: true, snapshot: await tick() };
  }

  async function setModelEnabled(id, enabled) {
    if (typeof id !== 'string' || !id.trim()) return { ok: false, reason: 'invalid data' };
    const model = roster().find(item => item.id === id);
    if (!model) return { ok: false, reason: 'model not present' };
    if (enabled && !model.available && !model.providerCatalog) return { ok: false, reason: model.unavailableReason || 'model unavailable' };
    store.enabled[id] = Boolean(enabled);
    store.modelRoles[id] = Object.fromEntries(ROLE_IDS.map(role => [role, Boolean(enabled && roleEligibility(model)[role] && (role !== 'supervisor' || model.roles.supervisor))]));
    if (model.accountScope) store.accountModelRoles[model.accountScope][id] = { ...store.modelRoles[id] };
    persist();
    return { ok: true, snapshot: await tick() };
  }

  async function setGoldModel(id) {
    const model = roster().find(model => model.id === id);
    if (id !== null && (!model || !roleEligibility(model).worker)) return { ok: false, reason: 'Choose a configured model compatible with the Worker role.' };
    store.goldModelId = id; persist();
    return { ok: true, snapshot: snapshot(await opts.readPlan()) };
  }

  async function setRoleForAll(role, enabled) {
    if (!ROLE_IDS.includes(role) || typeof enabled !== 'boolean') return { ok: false, reason: 'Invalid bulk role settings.' };
    for (const model of roster()) {
      store.modelRoles[model.id] = { ...model.roles, [role]: enabled && roleEligibility(model)[role] };
      if (model.accountScope) store.accountModelRoles[model.accountScope][model.id] = { ...store.modelRoles[model.id] };
    }
    if (role === 'orchestrator' && !enabled) store.orchestratorId = null;
    persist();
    return { ok: true, snapshot: snapshot(await opts.readPlan()) };
  }

  async function setModelRoles(id, roles) {
    const model = roster().find(model => model.id === id);
    if (!model) return { ok: false, reason: 'Model not present.' };
    if (!roles || typeof roles !== 'object' || Array.isArray(roles) || !Object.keys(roles).length || Object.entries(roles).some(([role, enabled]) => !ROLE_IDS.includes(role) || typeof enabled !== 'boolean')) return { ok: false, reason: 'Roles must contain Orchestrator, Worker, Reviewer or Supervisor boolean flags.' };
    const eligibility = roleEligibility(model);
    for (const [role, enabled] of Object.entries(roles)) if (enabled && !eligibility[role]) return { ok: false, reason: `This model is not compatible with the ${role} role.` };
    store.modelRoles[id] = { ...model.roles, ...roles };
    if (model.accountScope) store.accountModelRoles[model.accountScope][id] = { ...store.modelRoles[id] };
    if (store.orchestratorId === id && !withModelRoles(model, store.modelRoles[id]).roles.orchestrator) store.orchestratorId = null;
    persist();
    return { ok: true, snapshot: snapshot(await opts.readPlan()) };
  }

  async function setProviderPolicy(id, input) {
    const provider = providerDefinitions.find(provider => provider.id === id);
    if (!provider) return { ok: false, reason: 'Provider not present.' };
    try {
      if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('Invalid provider policy.');
      const policy = validateProviderPolicy({ ...providerPolicyFor(provider, models, store).policy, ...migratePolicy(input) });
      if (policy.fallbackProviderId && policy.fallbackProviderId !== '@automatic' && (policy.fallbackProviderId === id || !providerDefinitions.some(provider => provider.id === policy.fallbackProviderId))) throw new Error('Fallback must refer to another configured provider.');
      store.providerPolicies[id] = policy;
      persist();
      return { ok: true, snapshot: snapshot(await opts.readPlan()) };
    } catch (error) { return { ok: false, reason: error.message }; }
  }

  async function setModelPolicy(id, input) {
    const model = roster().find(model => model.id === id);
    if (!model) return { ok: false, reason: 'Model not present.' };
    try {
      validatePolicy(input);
      const policy = validatePolicy({ ...model.policy, ...migratePolicy(input) });
      if (policy.fallbackModelId && (policy.fallbackModelId === id || !models.some(model => model.id === policy.fallbackModelId))) throw new Error('Fallback must refer to another configured model.');
      store.modelPolicies[id] = policy;
      persist();
      return { ok: true, snapshot: snapshot(await opts.readPlan()) };
    } catch (error) { return { ok: false, reason: error.message }; }
  }

  async function setCatalogSettings(input) {
    const provider = providerDefinitions.find(p => p.id === input.providerId);
    if (!provider) return { ok: false, reason: 'Provider not present.' };
    if (typeof input.enabled === 'boolean') store.providerEnabled[provider.id] = input.enabled;
    if (typeof input.showAll === 'boolean') store.catalogVisibility[provider.id] = input.showAll;
    if (typeof input.allModelsEnabled === 'boolean') for (const model of roster().filter(m => (m.providerId || m.provider) === provider.id)) {
      store.enabled[model.id] = input.allModelsEnabled;
      store.modelRoles[model.id] = Object.fromEntries(ROLE_IDS.map(role => [role, Boolean(input.allModelsEnabled && roleEligibility(model)[role] && (role !== 'supervisor' || model.roles.supervisor))]));
      if (model.accountScope) store.accountModelRoles[model.accountScope][model.id] = { ...store.modelRoles[model.id] };
    }
    persist(); return { ok: true, snapshot: snapshot(await opts.readPlan()) };
  }

  async function approve(projectId, megaTaskId) {
    if (typeof projectId !== 'string' || !projectId.trim()) return { ok: false, reason: 'invalid data' };
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(row => row.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    const latest = item.revisions.at(-1);
    if (latest.open && latest.open.length && !(item.tasks || []).some(task => task.status === 'running' && task.source === 'chat')) return { ok: false, reason: 'open questions remain' };
    if (!(item.tasks || []).some(task => task.status === 'running')) return { ok: false, reason: 'no running job' };
    if (!item.project.folder) return { ok: false, reason: 'no folder' };
    const row = entry(projectId);
    const job = item.tasks.find(task => task.status === 'running');
    if (megaTaskId && job.executionVersion !== 2) return { ok: false, reason: 'The legacy job has no matching MegaTask approval target.' };
    if (job.executionVersion === 2) {
      decide(item);
      const mega = bridge.find(projectId, job.id);
      if (!mega) return { ok: false, reason: 'No MegaTask proposal is available.' };
      if (typeof megaTaskId !== 'string') return { ok: false, reason: 'Approve requires the explicit proposed MegaTask ID.' };
      if (megaTaskId && megaTaskId !== mega.id) return { ok: false, reason: 'Approval targets a different MegaTask from the current proposal.' };
      try { bridge.approve(projectId, megaTaskId); } catch (error) { return { ok: false, reason: error.message }; }
      row.authorizedSessionId = sessionId;
      return { ok: true, megaTaskId: mega.id, snapshot: await tick(projectId) };
    }
    row.approved = true;
    row.authorizedSessionId = sessionId;
    row.holdApproval = false;
    note(projectId, 'User approved the execution plan. Passing the job to the worker.');
    persist();
    const view = await tick(projectId);
    return { ok: true, snapshot: view };
  }

  function readTiming(input) {
    const src = input && typeof input === 'object' ? input : {};
    if (src.timing === 'deadline') {
      if (typeof src.deadlineAt !== 'string' || !Number.isFinite(Date.parse(src.deadlineAt))) {
        return { ok: false, reason: 'deadline required' };
      }
      return { ok: true, timing: 'deadline', deadlineAt: new Date(src.deadlineAt).toISOString() };
    }
    return { ok: true, timing: 'discretion', deadlineAt: null };
  }

  async function composeGrillRound(item, facts, kind) {
    const latest = item.revisions.at(-1);
    const docs = kind === 'docs' ? collectDocs(facts) : [];
    const skillsEnabled = opts.mattSkillsEnabled?.(item.project.id) !== false;
    const skillText = skillsEnabled && typeof opts.readSkill === 'function'
      ? (opts.readSkill('skills/productivity/grilling/SKILL.md', item.project.id) || '')
      : '';
    const docsSkill = skillsEnabled && kind === 'docs' && typeof opts.readSkill === 'function'
      ? (opts.readSkill('skills/engineering/grill-with-docs/SKILL.md', item.project.id) || '')
      : '';
    const lead = leadModel();
    if (skillsEnabled && typeof opts.askModel === 'function' && lead) {
      try {
        const spoken = await opts.askModel({
          question: grillPrompt(item, facts, [docsSkill, skillText].filter(Boolean).join('\n\n'), docs),
          model: lead,
          clarificationKind: kind,
          projectId: item.project.id,
          folder: item.project.folder || null,
          state: { name: item.project.name, goal: latest && latest.direction, folder: item.project.folder, docs },
          allowActions: false
        });
        const text = spoken && typeof spoken === 'object' && !Array.isArray(spoken) ? spoken.answer : spoken;
        const parsed = parseGrillQuestions(leadEnvelope(text).answer);
        if (parsed.length) return parsed;
      } catch (error) {
        if (error.code === 'PROJECT_SKILL_INTERVIEW_UNAVAILABLE') throw error;
      }
    }
    return Plan.grillFrontier({
      name: item.project.name,
      goal: latest && latest.direction,
      folder: item.project.folder,
      docs
    });
  }

  function releaseRunningJob(id, item, facts) {
    const job = (item.tasks || []).find(task => task.status === 'running');
    if (!job) return false;
    const row = entry(id);
    if (job.executionVersion === 2) {
      const mega = bridge.find(id, job.id);
      if (mega?.status === 'RUNNING') bridge.stop(item, job, mega.id);
      if (mega && mega.status !== 'COMPLETE') { mega.status = 'CANCELLED'; bridge.event(id, 'MEGATASK_CANCELLED', `Replaced ${mega.title} with a new Start request.`, { megaTaskId: mega.id }); }
      Object.assign(row, { jobId: null, attemptId: null, megaTaskId: null, megaTask: null, subTasks: [], dispatch: [], approved: false });
      persist(); return true;
    }
    const livePids = Array.isArray(row.dispatch) && row.dispatch.some(part => part && part.pid);
    if (row.status === 'watching' || livePids) {
      writeCheckpoint(id, item, job, facts && !facts.error ? facts : row.facts, 'stop');
    } else {
      killDispatch(row);
      const live = attempts.getActiveAttempt({ projectId: id, taskId: job.id })
        || attempts.getActiveAttempt({ projectId: id });
      if (live && typeof attempts.closeAttempt === 'function') {
        attempts.closeAttempt({ attemptId: live.id, reason: 'restart' });
      }
    }
    row.jobId = null;
    row.attemptId = null;
    row.modelId = null;
    row.approved = false;
    row.holdApproval = false;
    row.resumeRequested = false;
    row.watchingSince = null;
    return true;
  }

  async function startAutoTask(projectId, options) {
    if (typeof projectId !== 'string' || !projectId.trim()) return { ok: false, reason: 'project not found' };
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(row => row.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    if (!item.project.folder) return { ok: false, reason: 'no folder' };
    const row = entry(projectId);
    row.authorizedSessionId = sessionId;
    const source = options && options.timing ? options : (row.pendingAutoStart || options);
    const timing = readTiming(source);
    if (!timing.ok) return timing;
    const facts = inspect(item.project.folder);
    if (facts && facts.error) return { ok: false, reason: facts.error };
    const clearJob = releaseRunningJob(projectId, item, facts);
    const latest = item.revisions.at(-1);
    const continuing = Boolean(row.pendingAutoStart);
    const goldModel = source?.goldModel || row.pendingAutoStart?.goldModel || null;
    row.pendingAutoStart = { timing: timing.timing, deadlineAt: timing.deadlineAt, ...(goldModel ? { goldModel } : {}) };
    row.facts = facts;
    if (!continuing) row.grillAsked = false;
    const open = latest && Array.isArray(latest.open) ? latest.open : [];
    const genericOpen = Plan.isGenericFrontier(open);
    const liveOpen = open.length && !genericOpen;
    const sessionGrill = store.grillPropose !== false && store.grillKind !== 'off' && !row.grillSkipped && !source?.grillContext;
    if (liveOpen && !source?.grillContext) {
      note(projectId, 'Auto start: waiting on Matt grilling answers.');
      persist();
      await tick(projectId);
      return { ok: true, phase: 'grill', job: null, facts, clearJob };
    }
    if (sessionGrill && !row.grillAsked) {
      const docs = collectDocs(facts);
      const wanted = store.grillKind === 'docs' || store.grillKind === 'me' ? store.grillKind : null;
      if (wanted === 'docs' && !docs.length) return { ok: false, reason: 'No docs in the folder to grill with' };
      const kind = wanted === 'me' ? 'me' : (wanted === 'docs' || docs.length ? 'docs' : 'me');
      let questions;
      try { questions = await composeGrillRound(item, facts, kind); } catch (error) {
        row.pendingAutoStart = null;
        note(projectId, 'Clarification interview unavailable: ' + error.message);
        persist();
        return { ok: false, reason: error.message };
      }
      row.grillAsked = true;
      note(projectId, opts.mattSkillsEnabled?.(projectId) === false ? `Auto start: clarification (${kind}), Matt skills disabled.` : `Auto start: clarification interview (${kind}); inspect Skill delivery records for instruction provenance.`);
      persist();
      await tick(projectId);
      return {
        ok: true,
        phase: 'grill',
        job: null,
        facts,
        questions,
        title: kind === 'docs' ? 'Grill with the docs' : 'Grill me',
        kind,
        replace: genericOpen,
        clearJob
      };
    }
    const job = jobFromFacts(latest, facts);
    row.jobId = null;
    row.holdApproval = true;
    row.approved = false;
    row.pendingAutoStart = null;
    note(projectId, 'Read the repo and latest changes. Plan ready for approval.');
    persist();
    const payload = { title: job.title, detail: source?.grillContext ? job.detail + '\n\nGrill discussion for this task:\n' + source.grillContext : job.detail, ...(source?.grillContext ? { source: 'chat' } : {}), revision_id: latest.id, timing: timing.timing, ...(goldModel ? { goldModel } : {}) };
    if (timing.timing === 'deadline') payload.deadlineAt = timing.deadlineAt;
    return { ok: true, phase: 'plan', job: payload, facts, clearJob };
  }

  async function resume(projectId, request = {}) {
    if (typeof projectId !== 'string' || !projectId.trim()) return { ok: false, reason: 'invalid data' };
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(row => row.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    if (!(item.tasks || []).some(task => task.status === 'running')) return { ok: false, reason: 'no running job' };
    const row = entry(projectId);
    const job = item.tasks.find(task => task.status === 'running');
    if ((request.megaTaskId || request.checkpointId) && job.executionVersion !== 2) return { ok: false, reason: 'The explicit checkpoint does not belong to this legacy job.' };
    if (job.executionVersion === 2) {
      try {
        const resumed = bridge.resume(item, job, request);
        if (!resumed.ok) return resumed;
        return { ...resumed, snapshot: await tick(projectId) };
      } catch (error) { return { ok: false, reason: error.message }; }
    }
    if (!row.checkpoint) {
      const disk = readCheckpointFile(projectId);
      if (disk) row.checkpoint = disk;
    }
    if (!row.checkpoint) return { ok: false, reason: 'no checkpoint' };
    if (!item.tasks.some(task => task.status === 'running' && checkpointMatches(row.checkpoint, task))) return { ok: false, reason: 'checkpoint belongs to a different job' };
    killDispatch(row);
    row.dispatch = [];
    row.tried = [];
    row.attemptId = null;
    row.modelId = null;
    row.workerLog = null;
    row.resumeRequested = true;
    row.authorizedSessionId = sessionId;
    if (row.checkpoint.reason === 'deadline') row.deadlineHonored = true;
    row.approved = true;
    row.holdApproval = false;
    note(projectId, 'Resuming from checkpoint.');
    persist();
    return { ok: true, snapshot: await tick(projectId) };
  }

  function prepareForNewTask(projectId) {
    const row = entry(projectId);
    if (row.megaTaskId) {
      const mega = bridge.target(projectId, row.megaTaskId);
      if (mega.status !== 'COMPLETE') { mega.status = 'CANCELLED'; bridge.event(projectId, 'MEGATASK_CANCELLED', `Superseded ${mega.title} with a new work request.`, { megaTaskId: mega.id }); }
    }
    if (row.checkpoint?.path && fs.existsSync(row.checkpoint.path)) {
      const archive = path.join(path.dirname(row.checkpoint.path), 'history', `${projectId}-${crypto.randomUUID()}.json`);
      fs.mkdirSync(path.dirname(archive), { recursive: true });
      fs.copyFileSync(row.checkpoint.path, archive, fs.constants.COPYFILE_EXCL);
    }
    if ((row.dispatch || []).some(part => part && (part.pid || part.startedAt || part.resultPath))) {
      store.workerHistory.push({ id: projectId, name: projectId, jobTitle: row.jobTitle, attemptId: row.attemptId, status: 'checkpointed', dispatch: clone(row.dispatch).map(part => ({ ...part, alive: false })), workerTail: row.workerTail || '', sessionId: row.authorizedSessionId || null });
    }
    const active = attempts.getActiveAttempt({ projectId });
    if (active && typeof attempts.closeAttempt === 'function') attempts.closeAttempt({ attemptId: active.id, reason: 'Superseded by a new chat request; existing execution evidence preserved.' });
    Object.assign(row, { approved: false, holdApproval: true, authorizedSessionId: sessionId, resumeRequested: false, checkpoint: null, dispatch: [], plan: [], tried: [], jobId: null, jobFingerprint: null, attemptId: null, modelId: null, workerPid: null, workerAlive: false, workerLog: null, resultPath: null, workerTail: '', status: 'needs_approval' });
    persist();
  }

  async function stopWorker(projectId, pid) {
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(item => item.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    const row = entry(projectId);
    const job = item.tasks.find(task => task.status === 'running');
    if (job?.executionVersion === 2) {
      try { bridge.stopWorker(item, job, row.megaTaskId, pid); return { ok: true, snapshot: await tick(projectId) }; }
      catch (error) { return { ok: false, reason: error.message }; }
    }
    const part = (row.dispatch || []).find(part => part && part.pid === pid && !part.stoppedByUser);
    if (!part || row.status !== 'watching') return { ok: false, reason: 'Worker is no longer active on this project.' };
    if (typeof opts.killWorker !== 'function' || !opts.killWorker(pid)) return { ok: false, reason: 'Could not stop the worker process.' };
    part.stoppedByUser = true;
    part.alive = false;
    part.activity = 'Stopped by you. Resume the project to continue this slice.';
    note(projectId, `Stopped worker ${part.modelId}, process ${pid}. Other slices continue; automatic replacement is suspended.`);
    persist();
    return { ok: true, snapshot: await tick(projectId) };
  }

  async function stop(projectId, megaTaskId) {
    if (typeof projectId !== 'string' || !projectId.trim()) return { ok: false, reason: 'invalid data' };
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(row => row.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    const job = (item.tasks || []).find(task => task.status === 'running') || null;
    if (!job) return { ok: false, reason: 'no running job' };
    const row = entry(projectId);
    if (megaTaskId && job.executionVersion !== 2) return { ok: false, reason: 'The legacy job has no matching MegaTask stop target.' };
    if (job.executionVersion === 2) {
      try { bridge.stop(item, job, megaTaskId || row.megaTaskId); return { ok: true, snapshot: await tick(projectId) }; }
      catch (error) { return { ok: false, reason: error.message }; }
    }
    if (row.status !== 'watching') return { ok: false, reason: 'nothing to stop' };
    const facts = item.project.folder ? inspect(item.project.folder) : (row.facts || {});
    applyCheckpoint(projectId, item, job, facts && !facts.error ? facts : row.facts, 'stop');
    persist();
    return { ok: true, snapshot: await tick(projectId) };
  }

  async function checkpointForUpdate() {
    const previous = shuttingDown;
    shuttingDown = true; askToken += 1; thinkingLive = null;
    if (typeof opts.abortAsk === 'function') opts.abortAsk();
    try {
      const plan = await opts.readPlan();
      const failures = [];
      for (const item of notebookProjects(plan)) {
        for (const job of (item.tasks || []).filter(task => task.status === 'running')) {
          const row = entry(item.project.id);
          row.approved = false; row.holdApproval = true; row.resumeRequested = false;
          try {
            const mega = job.executionVersion === 2 ? bridge.find(item.project.id, job.id) : null;
            if (mega) bridge.checkpointForUpdate(item, job, mega.id);
            else {
              for (const pid of collectPids(row)) {
                const stopped = opts.killWorker?.(pid);
                if (opts.isProcessAlive?.(pid) || stopped !== true && typeof opts.isProcessAlive !== 'function') throw new Error('Worker process could not be stopped: ' + pid);
              }
              applyCheckpoint(item.project.id, item, job, row.facts, 'application update');
            }
          } catch (error) { failures.push(`${item.project.id}/${job.id}: ${error.message}`); }
        }
      }
      for (const record of store.researches.filter(row => ['watching', 'needs_approval'].includes(row.status))) {
        try {
          if (record.executionVersion === 2) {
            bridge.checkpointForUpdate({ project: { id: record.projectId, name: record.projectName, folder: record.sourceFolder } }, record.job, record.megaTaskId);
          } else {
            if (record.alive && (!opts.killWorker?.(record.pid) || opts.isProcessAlive?.(record.pid))) throw new Error('Research worker could not be stopped: ' + record.pid);
            record.alive = false; finishResearch(record, 'checkpointed', 'Application update; research evidence preserved.');
          }
        } catch (error) { failures.push(`${record.id}: ${error.message}`); }
      }
      persist();
      if (failures.length) throw new Error('Checkpoint failed: ' + failures.join('; '));
      return { ok: true };
    } finally { shuttingDown = previous; }
  }

  async function shutdown() {
    shuttingDown = true;
    askToken += 1;
    thinkingLive = null;
    if (typeof opts.abortAsk === 'function') opts.abortAsk();
    if (typeof opts.abortSupervisor === 'function') opts.abortSupervisor();
    refreshResearch();
    for (const record of store.researches.filter(item => item.status === 'watching')) {
      if (record.executionVersion === 2) {
        const item = { project: { id: record.projectId, name: record.projectName, folder: record.sourceFolder } };
        bridge.stop(item, record.job, record.megaTaskId); continue;
      }
      const stopped = !record.alive || (typeof opts.killWorker === 'function' && opts.killWorker(record.pid));
      if (stopped) { record.alive = false; finishResearch(record, 'checkpointed', 'Notebook shutdown; research evidence preserved.'); }
      else record.reason = 'Notebook shutdown could not stop the research process; inspect its PID before resuming.';
    }
    const watching = Object.entries(store.projects).filter(([, row]) => row.status === 'watching');
    if (!watching.length) { persist(); return; }
    let plan;
    try { plan = await opts.readPlan(); }
    catch (_) {
      // Preserve the last known job even when the notebook becomes unreadable during shutdown.
      plan = { schema: 2, projects: watching.map(([id, row]) => ({
        project: { id, name: id },
        tasks: [{ id: row.jobId, title: row.jobTitle, detail: row.jobDetail, revision_id: row.jobRevisionId, status: 'running' }]
      })) };
    }
    for (const item of notebookProjects(plan)) {
      const row = entry(item.project.id);
      const job = (item.tasks || []).find(task => task.status === 'running');
      if (job && row.status === 'watching') {
        if (job.executionVersion === 2) bridge.stop(item, job, row.megaTaskId);
        else applyCheckpoint(item.project.id, item, job, row.facts, 'shutdown');
      }
    }
    persist();
  }

  function pidFor(attempt) {
    if (!attempt || typeof attempt !== 'object') return null;
    const attemptId = attempt.id;
    if (typeof attemptId !== 'string' || !attemptId) return null;
    for (const id of Object.keys(store.projects)) {
      const row = store.projects[id];
      if (row && row.attemptId === attemptId) return row.workerPid || null;
    }
    const research = store.researches.find(record => record.attemptId === attemptId);
    if (research) return research.pid || null;
    return null;
  }

  async function activitySessionPage(projectId, offset = 0, limit = SESSION_LIMIT) {
    const plan = await opts.readPlan();
    if (!notebookProjects(plan).some(item => item.project.id === projectId)) throw new Error('Select an existing project.');
    if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(limit) || limit < 1 || limit > SESSION_LIMIT) throw new Error('Invalid activity session page.');
    return { projectId, ...pagedProjectActivitySessions({ projectId, row: store.projects[projectId] || {}, history: store.workerHistory, execution: store.execution, offset, limit, readTail: file => readRecordedTail(file, dir) }) };
  }

  async function inspectRepository(projectId, options) {
    const plan = await opts.readPlan();
    const item = notebookProjects(plan).find(item => item.project.id === projectId);
    if (!item) return { ok: false, reason: 'project not found' };
    const repository = await readRepository(item.project, options);
    const row = entry(projectId);
    row.repository = repository;
    if (item.project.folder) row.facts = inspect(item.project.folder);
    persist();
    return { ok: true, repository, snapshot: snapshot(plan, projectId) };
  }

  async function setFocus(projectId) {
    const plan = await opts.readPlan();
    if (!notebookProjects(plan).some(item => item.project.id === projectId)) return { ok: false, reason: 'Selected project no longer exists.' };
    store.selectedProjectId = projectId; persist(); return { ok: true, selectedProjectId: projectId };
  }
  async function pauseAfterCurrent(projectId, megaTaskId) {
    const plan = await opts.readPlan(), item = notebookProjects(plan).find(item => item.project.id === projectId);
    const job = item?.tasks.find(task => task.status === 'running' && task.executionVersion === 2);
    if (!job) return { ok: false, reason: 'No running MegaTask on this project.' };
    try { bridge.stop(item, job, megaTaskId, true); return { ok: true, snapshot: await tick(projectId) }; }
    catch (error) { return { ok: false, reason: error.message }; }
  }
  function restoreImported(projectId, execution) {
    bridge.restore(projectId, execution);
    return { ok: true, execution: bridge.view(projectId) };
  }
  return { tick, activitySessionPage, supervisorRequest, checkpointForUpdate, refreshRepositories: async () => refreshRepositoryMetadata(await opts.readPlan()), shutdown, bindAccount, invalidateAccountCache, approve, startAutoTask, resume, stop, stopWorker, pauseAfterCurrent, setFocus, executionView: bridge.view, restoreImported, prepareForNewTask, setMode, setGrillPropose, skipGrill, docsFor, inspectRepository, setModelEnabled, setModelRoles, setRoleForAll, setGoldModel, setModelPolicy, setProviderPolicy, setCatalogSettings, setOrchestrator, ask, abortAsk, dispatchResearch, approveResearch, stopResearch, resumeResearch, researchReport, pidFor, snapshot: async projectId => { refreshResearch(); return snapshot(await opts.readPlan(), projectId); } };
}

module.exports = { jobFingerprint, createOrchestrator, pickWorker, pickWorkers, normalizeModels, jobKind, defaultInspect, jobFromFacts, planLines, dispatchPlan, listDocs, collectDocs, grillDone, tailLog, lastActivity, interpretActivity, progressKey, sliceCount, answerQuestion, parseOrder, looksLikeWork, looksLikeQuestion, packState, canOrchestrate, usageOf, planExhausted, ORCH_NAME, LABELS, stepOf, pipelineOf, crewOf, PIPELINE_STEPS, parseGrillQuestions, grillPrompt };
