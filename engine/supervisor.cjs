'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const label = input => typeof input === 'string' && input.trim() ? Array.from(input.trim()).slice(0, 80).join('') : 'Supervisor';
const supported = require('./model-roles.cjs').supportsSupervisor;
function advisoryError(model, plans = []) {
  if (!model || model.roles?.supervisor !== true) return 'Supervisor permission is required; Reviewer permission does not authorize advisory calls.';
  if (!supported(model)) return 'No verified no-tool Supervisor transport for this adapter. No fallback was used.';
  if (!model.enabled || model.available === false || model.providerEnabled === false) return 'The selected Supervisor model or provider is unavailable. No fallback was used.';
  if (!model.providerCatalog && Number.isFinite(model.remaining) && Number.isFinite(model.cost) && model.remaining < model.cost) return 'The selected Supervisor has no remaining model usage budget. No fallback was used.';
  if (require('./model-policy.cjs').usagePolicy(model, plans).blocked || model.usagePolicy?.blocked || plans.some(pool => pool.id === (model.quotaPoolId || model.plan) && pool.readable && (pool.exhausted || pool.leftPct === 0))) return 'The selected Supervisor is blocked by its quota policy. No fallback was used.';
  return null;
}
const keyFor = value => crypto.createHash('sha256').update(JSON.stringify(value)).digest('hex').slice(0, 24);
function projectFacts(snapshot, projectId, now = Date.now()) {
  const row = snapshot.projects?.find(row => row.id === projectId);
  if (!row) throw new Error('Select an existing project.');
  const subs = snapshot.execution?.subTasks || row.subTasks || [];
  const results = snapshot.execution?.results || [];
  const workers = (row.dispatch || []).filter(part => part && (!row.megaTaskId || (subs.find(sub => sub.id === part.subTaskId)?.megaTaskId || part.megaTaskId || row.megaTaskId) === row.megaTaskId)).map(part => {
    const sub = subs.find(sub => sub.id === part.subTaskId);
    const result = results.find(result => result.attemptId === part.attemptId);
    const model = snapshot.models?.find(model => model.id === part.modelId);
    const role = sub?.type === 'AUDIT' || part.type === 'AUDIT' ? 'reviewer' : 'worker';
    return { id: part.attemptId || String(part.pid || part.modelId), subTaskId: part.subTaskId, role, modelId: part.modelId,
      provider: part.provider || model?.providerId || model?.provider, model: part.cli || model?.cli,
      task: sub?.title || part.slice || row.jobTitle || 'Assigned work', alive: Boolean(part.alive),
      status: part.alive ? 'running' : result?.status || part.recordedSubTaskStatus || (row.status === 'checkpointed' ? 'paused' : 'not running'),
      summary: result?.summary || (part.alive ? `${role === 'reviewer' ? 'Checking' : 'Working on'} ${sub?.title || part.slice || 'the assignment'}.` : 'No active process; check recorded evidence.'),
      activity: String(part.activity || '').slice(0, 700), resultId: result?.resultId || null };
  });
  // Pending reviewers are distinct from workers and are not presented as running.
  for (const sub of subs.filter(sub => sub.megaTaskId === row.megaTaskId && sub.type === 'AUDIT' && !workers.some(w => w.subTaskId === sub.id))) {
    const model = snapshot.models?.find(model => model.id === sub.assignedModelId);
    workers.push({ id: sub.id, subTaskId: sub.id, role: 'reviewer', modelId: sub.assignedModelId, provider: model?.providerId || model?.provider,
      model: model?.cli, task: sub.title, status: sub.status, alive: false, summary: sub.status === 'PENDING' ? 'Waiting for approval or prerequisite work.' : 'See the recorded review result.' });
  }
  const active = workers.filter(w => w.alive).length || (row.workerAlive ? 1 : 0);
  const alerts = [];
  const add = (kind, severity, title, message, evidence) => alerts.push({ key: keyFor([projectId, row.megaTaskId || row.jobId, kind, evidence]),
    projectId, megaTaskId: row.megaTaskId, kind, severity, title, message, evidence,
    actions: ['inspect', 'ask', ...(active && row.megaTaskId && row.status === 'watching' ? ['pause', 'stop'] : []), 'ignore'] });
  if (row.status === 'complete' && active) add('conflicting-completion', 'critical', 'Completion conflicts with active work', 'The backend reports completion but workers are still active. Do not rely on this completion yet.', workers.filter(w => w.alive).map(w => w.id));
  if (row.status === 'failed' || row.status?.startsWith('blocked_')) add('execution-blocked', 'warning', 'Work needs attention', row.next || row.waitingFor || 'The execution is blocked or failed. Review its evidence before resuming.', [row.status, row.attemptId, row.megaTaskId]);
  const relevantResults = results.filter(result => result.megaTaskId === row.megaTaskId);
  for (const result of relevantResults.filter(result => ['FAILED', 'BLOCKED'].includes(result.status))) add('result-' + result.resultId, 'warning', 'An assignment did not finish successfully', result.summary || 'A worker or reviewer reported a blocker.', [result.resultId, result.subTaskId, result.status]);
  for (const worker of workers.filter(w => !w.alive && !w.resultId && w.status === 'RUNNING')) add('missing-result-' + worker.id, 'warning', 'Worker stopped without a validated result', `${worker.task} has no live process and no validated result.`, [worker.id, worker.subTaskId]);
  const projectMegas = new Set((snapshot.execution?.megaTasks || []).filter(m => m.projectId === projectId).map(m => m.id));
  const recent = results.filter(result => projectMegas.has(result.megaTaskId)).slice(-3);
  if (recent.length === 3 && recent.every(result => ['FAILED', 'BLOCKED'].includes(result.status))) add('repeated-failures', 'warning', 'Three consecutive unsuccessful results', 'The recent attempts are not making verified progress. Review the approach before retrying.', recent.map(result => result.resultId));
  if (active && row.progressAt && Number.isFinite(Date.parse(row.progressAt)) && now - Date.parse(row.progressAt) > 10 * 60 * 1000) add('quiet-execution', 'warning', 'No recent progress recorded', 'A process is still alive, but no progress has been recorded for over ten minutes. This is a signal to inspect, not proof of failure.', [row.progressAt, ...workers.filter(w => w.alive).map(w => w.id)]);
  const summary = active ? row.megaTask?.pauseRequested ? `${row.name}: finishing active work before pausing.` : `${row.name}: ${active} active assignment${active === 1 ? '' : 's'}.`
    : row.status === 'complete' ? `${row.name}: the backend recorded completion; no workers are active.`
    : row.status === 'checkpointed' ? `${row.name}: paused with a saved checkpoint.`
    : row.status === 'needs_approval' ? `${row.name}: a plan awaits your approval; workers have not started.`
    : ['failed', 'blocked_no_worker', 'blocked_no_folder'].includes(row.status) ? `${row.name}: execution needs attention.`
    : `${row.name}: no workers are active. ${row.next || ''}`;
  const timeline = (snapshot.execution?.timeline || []).filter(event => event.projectId === projectId).slice(-12).map(event => ({ id: event.id, summary: event.summary, at: event.createdAt }));
  return { projectId, name: row.name, status: row.status, megaTaskId: row.megaTaskId, summary, next: row.next || '', active,
    workers, alerts, timeline, canPause: Boolean(active && row.megaTaskId && row.status === 'watching'), checkpointId: row.checkpointId || null };
}
function createSupervisor({ dir, review, now = () => new Date().toISOString() }) {
  const file = dir ? path.join(dir, 'supervisor.json') : null;
  let store = { schema: 2, name: 'Supervisor', configured: false, modelId: '', autoReview: false, alerts: [], turns: [], autoCalls: [] };
  let migrated = false;
  try { const saved = file ? JSON.parse(fs.readFileSync(file, 'utf8')) : store; if (![1, 2].includes(saved.schema) || !Array.isArray(saved.alerts) || !Array.isArray(saved.turns)) throw Error('Invalid Supervisor storage'); store = { ...store, ...saved }; if (saved.schema === 1) { store.schema = 2; store.legacyModelId = store.modelId; store.modelId = ''; store.autoReview = false; migrated = true; } }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  let busy = false;
  const save = () => { if (!file) return; fs.mkdirSync(dir, { recursive: true }); const temp = file + '.tmp'; fs.writeFileSync(temp, JSON.stringify(store, null, 2)); fs.renameSync(temp, file); };
  // An advisory response cannot continue across backend replacement.
  let interrupted = false;
  for (const turn of store.turns) if (turn.status === 'running') { turn.status = 'failed'; turn.answer = 'This answer was interrupted by a backend restart. Ask again for current evidence.'; turn.finishedAt = now(); interrupted = true; }
  if (interrupted || migrated) save();
  function observe(snapshot) {
    let changed = false; const activeKeys = new Set(), added = [];
    for (const project of snapshot.projects || []) for (const detected of projectFacts(snapshot, project.id, Date.parse(now())).alerts) {
      activeKeys.add(detected.key);
      const old = store.alerts.findLast(alert => alert.key === detected.key && alert.active);
      if (!old) { const record = { ...detected, id: crypto.randomUUID(), active: true, ignored: false, createdAt: now() }; store.alerts.push(record); added.push(record); changed = true; }
    }
    for (const alert of store.alerts) if (alert.active && !activeKeys.has(alert.key)) { alert.active = false; alert.resolvedAt = now(); changed = true; }
    if (changed) save();
    // Event-triggered, bounded analysis; never an LLM status poll.
    if (store.autoReview && store.modelId && !advisoryError(snapshot.models?.find(m => m.id === store.modelId), snapshot.plans) && !busy && added.length && store.autoCalls.filter(at => Date.parse(now()) - Date.parse(at) < 3600000).length < 3) {
      const alert = added[0]; store.autoCalls.push(now()); save();
      void ask(snapshot, alert.projectId, `Explain this anomaly and suggest a safe next action: ${alert.title}. ${alert.message}`, true).catch(() => {});
    }
    return view(snapshot);
  }
  function view(snapshot) {
    return { name: label(store.name), configured: store.configured, modelId: store.modelId, autoReview: store.autoReview, busy,
      projects: (snapshot.projects || []).map(row => { const facts = projectFacts(snapshot, row.id, Date.parse(now())); return { ...facts, alerts: store.alerts.filter(a => a.projectId === row.id && a.active), turns: store.turns.filter(t => t.projectId === row.id).slice(-30) }; }),
      models: (snapshot.models || []).filter(m => m.roles?.supervisor).map(m => ({ id: m.id, label: m.label, provider: m.providerId || m.provider, model: m.cli, available: !advisoryError(m, snapshot.plans), reason: advisoryError(m, snapshot.plans) || '' })) };
  }
  function settings(input, snapshot) {
    const next = { name: store.name, configured: store.configured, modelId: store.modelId, autoReview: store.autoReview };
    if (input.name !== undefined) { next.name = label(input.name); next.configured = true; }
    if (input.modelId !== undefined) {
      if (input.modelId && !snapshot.models.some(m => m.id === input.modelId && m.roles?.supervisor === true && supported(m))) throw Error('Select an eligible Supervisor model; no substitution is allowed.');
      next.modelId = input.modelId;
    }
    if (input.autoReview !== undefined) { if (input.autoReview && !next.modelId) throw Error('Select a Supervisor before enabling anomaly analysis.'); next.autoReview = input.autoReview; }
    if (!next.modelId) next.autoReview = false;
    Object.assign(store, next);
    save(); return view(snapshot);
  }
  function ignore(projectId, alertId) {
    const alert = store.alerts.find(a => a.id === alertId && a.projectId === projectId && a.active);
    if (!alert) throw Error('This alert is no longer active for the selected project.');
    alert.ignored = true; alert.ignoredAt = now(); save();
  }
  async function ask(snapshot, projectId, question, automatic = false) {
    if (busy) throw Error('A Supervisor answer is already in progress.');
    if (typeof question !== 'string' || !question.trim() || question.length > 8000) throw Error('Enter a question of at most 8000 characters.');
    const facts = projectFacts(snapshot, projectId, Date.parse(now()));
    busy = true;
    const turn = { id: crypto.randomUUID(), projectId, question: question.trim(), at: now(), automatic, mode: store.modelId ? 'supervisor' : 'facts', modelId: store.modelId || null, status: 'running' };
    store.turns.push(turn); save();
    try {
      if (store.modelId) {
        const denied = advisoryError(snapshot.models?.find(m => m.id === store.modelId), snapshot.plans);
        if (denied) throw Error(denied);
        const result = await review({ projectId, modelId: store.modelId, question: turn.question, facts,
          history: store.turns.filter(t => t.projectId === projectId && t.status === 'complete').slice(-6).map(t => ({ question: t.question, answer: t.answer })) });
        if (result.failure) throw Error(result.diagnostic?.message || result.answer || 'Supervisor analysis failed.');
        turn.answer = String(result.answer || '').slice(0, 40000); if (!turn.answer.trim()) throw Error('Supervisor returned no answer.');
        turn.provider = result.provider; turn.model = result.model; turn.tokens = result.tokens || null;
      } else {
        const selected = /reviewer|revisor/i.test(question) ? facts.workers.filter(w => w.role === 'reviewer') : facts.workers;
        turn.answer = ['Live evidence (no AI interpretation):', facts.summary,
          ...selected.map(w => `${w.role === 'reviewer' ? 'Reviewer' : 'Worker'} ${w.provider}/${w.model}: ${w.task} — ${w.status}. ${w.summary}`),
          ...facts.alerts.map(a => `Attention: ${a.title}. ${a.message}`), facts.next ? 'Next recorded action: ' + facts.next : '',
          'Select a Supervisor model for interpretation of this evidence.'].filter(Boolean).join('\n\n');
      }
      turn.status = 'complete';
    } catch (error) { turn.status = 'failed'; turn.answer = error.message; }
    finally { busy = false; turn.finishedAt = now(); save(); }
    return view(snapshot);
  }
  return { observe, view, settings, ignore, ask };
}
module.exports = { createSupervisor, projectFacts, label, supported, advisoryError };
