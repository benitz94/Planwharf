'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { readJSON, readValidatedResult, validateResult, reportExists } = require('./worker-result.cjs');
const { executionNext } = require('./execution-next.cjs');
const { adapterFor } = require('./provider-adapters.cjs');
const { checkpointDiff } = require('./project-history.cjs');
const clone = value => structuredClone(value);
const fingerprint = job => crypto.createHash('sha256').update(JSON.stringify([job.id, job.title, job.detail, job.revision_id || null, ...(job.goldModel ? [job.goldModel] : [])])).digest('hex');

function observedCommits(folder, before, after) {
  if (!folder || before === after || !/^[a-f0-9]{40,64}$/.test(before || '') || !/^[a-f0-9]{40,64}$/.test(after || '')) return [];
  const result = spawnSync('git', ['--no-optional-locks', '-C', folder, 'rev-list', '--max-count=1000', `${before}..${after}`], { encoding: 'utf8', timeout: 4000, windowsHide: true, maxBuffer: 256 * 1024 });
  return result.status === 0 ? result.stdout.trim().split(/\r?\n/).filter(id => /^[a-f0-9]{40,64}$/.test(id)) : [after];
}

function repoState(folder, facts = {}) {
  const dirtyFiles = [...(facts.dirty || [])];
  const contents = [];
  let bytes = 0, comparable = !facts.error;
  for (const record of dirtyFiles) {
    const prefix = /^[MADRCU?!] \S/.test(record) ? 2 : 3;
    const name = record.slice(prefix).split(' -> ').at(-1).replace(/^"|"$/g, '');
    const file = path.resolve(folder || '.', name);
    if (!file.startsWith(path.resolve(folder || '.') + path.sep)) { comparable = false; continue; }
    try {
      const stat = fs.lstatSync(file); bytes += stat.size;
      if (!stat.isFile() || stat.isSymbolicLink() || bytes > 8 * 1024 * 1024) { comparable = false; continue; }
      contents.push([name, crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')]);
    } catch { contents.push([name, 'unavailable']); comparable = false; }
  }
  const repoHead = facts.commitId || facts.lastCommit || facts.head || null;
  return { repoHead, dirtyFiles, comparable, gitReadable: Boolean(facts.git), fingerprint: comparable ? crypto.createHash('sha256').update(JSON.stringify([repoHead, dirtyFiles, contents])).digest('hex') : null,
    disposition: !facts.git ? 'NOT APPLICABLE' : dirtyFiles.length ? 'UNCOMMITTED' : 'COMMITTED' };
}

function createExecutionBridge(options) {
  const { store, attempts, dir, rowFor, inspect, spawnWorker, isProcessAlive, killWorker, models, now, persist, leadId } = options;
  store.execution ||= { megaTasks: [], subTasks: [], results: [], checkpoints: [], timeline: [], observations: {} };
  const data = store.execution;
  for (const key of ['megaTasks', 'subTasks', 'results', 'checkpoints', 'timeline']) if (!Array.isArray(data[key])) data[key] = [];
  data.observations ||= {};
  const subtasks = mega => data.subTasks.filter(sub => sub.megaTaskId === mega.id);
  const inspectProject = item => item.project.folder ? inspect(item.project.folder) : { git: false, dirty: [], names: [], fileCount: 0 };
  function event(projectId, type, summary, reference, key) {
    if (key && data.timeline.some(item => item.key === key)) return;
    data.timeline.push({ id: 'EV-' + crypto.randomUUID(), projectId, type, summary, reference, createdAt: now(), ...(key ? { key } : {}) });
  }
  function observe(item) {
    for (const revision of item.revisions) event(item.project.id, 'PLAN_REVISION', `Living Plan R${revision.id}: ${revision.title || revision.direction}`, { planRevisionId: revision.id }, `revision:${item.project.id}:${revision.id}`);
    for (const note of item.home?.notes || []) event(item.project.id, 'USER_NOTE', note.text, { noteId: note.id }, `note:${item.project.id}:${note.id}`);
  }
  function find(projectId, jobId) { return data.megaTasks.find(mega => mega.projectId === projectId && mega.taskId === jobId); }
  function target(projectId, megaTaskId) {
    const mega = data.megaTasks.find(mega => mega.id === megaTaskId && mega.projectId === projectId);
    if (!mega) throw new Error('MegaTask does not belong to the target project.');
    return mega;
  }
  function propose(item, job, parts) {
    let mega = find(item.project.id, job.id);
    if (mega) {
      if (mega.jobFingerprint !== fingerprint(job)) throw new Error('MegaTask job identity changed. Prepare a new objective.');
      // Restore timing for MegaTasks saved before deadline enforcement existed.
      if (mega.timing === undefined) { mega.timing = job.timing || 'discretion'; mega.deadlineAt = job.timing === 'deadline' ? job.deadlineAt : null; }
      return mega;
    }
    if (!parts.length) throw new Error(job.goldModel ? `Gold model ${job.goldModel.provider}/${job.goldModel.cli} (${job.goldModel.id}) is unavailable, disabled or blocked by quota. No substitute will be used.` : 'No eligible workers for the proposed MegaTask.');
    const id = 'MT-' + crypto.randomUUID(), facts = inspectProject(item);
    if (!item.project.folder && job.kind !== 'research' && !parts.every(part => part.type === 'RESEARCH')) throw new Error('Set a local source folder before proposing implementation work.');
    if (facts?.error) throw new Error(facts.error);
    mega = { id, projectId: item.project.id, taskId: job.id, planRevisionId: job.revision_id, title: job.title, goal: job.detail || item.revisions.at(-1).direction,
      createdAt: now(), approvedAt: null, status: 'READY_FOR_APPROVAL', orchestratorModelId: leadId(), currentCheckpointId: null, completedAt: null,
      jobFingerprint: fingerprint(job), detached: Boolean(job.detached), repoBefore: repoState(item.project.folder, facts), nextAction: 'Review the proposed slices and approve this MegaTask.', pauseRequested: false };
    mega.timing = job.timing || 'discretion'; mega.deadlineAt = job.timing === 'deadline' ? job.deadlineAt : null;
    const ids = parts.map((_, index) => `${id}-ST-${String(index + 1).padStart(2, '0')}`);
    const subs = parts.map((part, index) => ({ id: ids[index], megaTaskId: id, title: part.slice || part.title, detail: part.detail || part.slice || job.detail, status: 'PENDING', assignedModelId: part.modelId,
      ...(job.goldModel ? { goldModel: { ...job.goldModel } } : {}),
      attemptIds: [], resultId: null, createdAt: now(), completedAt: null, type: part.type || (job.kind === 'research' ? 'RESEARCH' : job.kind === 'audit' ? 'AUDIT' : 'IMPLEMENTATION'),
      dependencies: part.dependsOn ? part.dependsOn.map(index => ids[index]) : part.dependencies || (index ? [ids[index - 1]] : []),
      proposedProvider: models().find(model => model.id === part.modelId)?.provider || null, proposedCli: models().find(model => model.id === part.modelId)?.cli || null, provider: null, cli: null }));
    for (const sub of subs) for (const dependency of sub.dependencies) if (!ids.includes(dependency) || ids.indexOf(dependency) >= ids.indexOf(sub.id)) throw new Error('Dependencies must reference an earlier SubTask in the same MegaTask.');
    data.megaTasks.push(mega); data.subTasks.push(...subs);
    mega.blockers = proposalBlockers(mega);
    if (mega.blockers.length) mega.nextAction = 'Resolve the proposal blockers before approval: ' + mega.blockers.join(' ');
    if (!job.detached) rowFor(mega.projectId, mega.id).megaTaskId = id;
    event(mega.projectId, 'MEGATASK_PROPOSED', `${mega.title}: ${subs.length} proposed slice(s).`, { megaTaskId: id });
    persist();
    return mega;
  }
  function approve(projectId, megaTaskId) {
    const mega = target(projectId, megaTaskId);
    if (mega.status !== 'READY_FOR_APPROVAL') throw new Error('Only a proposed MegaTask can be approved.');
    mega.blockers = proposalBlockers(mega);
    if (mega.blockers.length) { persist(); throw new Error(mega.blockers.join(' ')); }
    mega.approvedAt = now(); mega.status = 'RUNNING'; mega.approvedMegaTaskId = mega.id; mega.authorizedSessionId = options.sessionId;
    event(projectId, 'MEGATASK_APPROVED', `Approved ${mega.title}.`, { megaTaskId }); persist();
    return mega;
  }
  function identity(mega, sub, attemptId) { return { megaTaskId: mega.id, subTaskId: sub.id, attemptId, resultId: 'R-' + attemptId, provider: sub.provider, cli: sub.cli }; }
  function proposalBlockers(mega) {
    if (typeof spawnWorker !== 'function') return ['The worker launcher is unavailable. Start Planwharf through its desktop launcher.'];
    return subtasks(mega).flatMap(sub => {
      const worker = models().find(model => model.id === sub.assignedModelId);
      const candidate = options.resolveWorker ? options.resolveWorker(worker, sub) : worker;
      if (!candidate?.enabled || candidate.available === false || options.workerEligible && !options.workerEligible(candidate, sub)) return [`Worker ${sub.assignedModelId} is unavailable or has insufficient remaining quota.`];
      if (!worker || worker.provider !== sub.proposedProvider || worker.cli !== sub.proposedCli) return [`Provider/model configuration changed for ${sub.assignedModelId}; review a new proposal.`];
      if (sub.type === 'RESEARCH' && !adapterFor(candidate)?.sourceProtectedResearch) return [`Source-protected research is not supported by ${worker.provider}.`];
      return [];
    });
  }
  function technical(mega, sub, part, status, summary, partial) {
    const exit = readJSON(part.exitPath);
    const exitCode = exit?.attemptId === part.attemptId && exit.provider === sub.provider && exit.model === sub.cli && Number.isInteger(exit.exitCode) ? exit.exitCode : null;
    return { ...identity(mega, sub, part.attemptId), status, summary, filesChanged: partial?.filesChanged || [], tests: partial?.tests || [], blockers: [summary, ...(partial?.blockers || [])],
      nextAction: 'Review the evidence and resume the remaining work from an explicit checkpoint.', createdAt: now(), exitCode, evidenceKind: 'TECHNICAL', evidencePath: part.logPath || null };
  }
  function collect(mega, sub, part, result) {
    if (data.results.some(item => item.resultId === result.resultId)) return;
    result = { ...result, assignedModelId: sub.assignedModelId, provider: sub.provider, cli: sub.cli, pid: part.pid, startedAt: part.startedAt };
    data.results.push(result); sub.resultId = result.resultId; sub.status = result.status;
    part.validatedResultStatus = result.status; part.recordedSubTaskStatus = sub.status;
    sub.completedAt = result.status === 'COMPLETE' ? now() : null;
    attempts.attachResult({ attemptId: part.attemptId, resultPath: part.structuredResultPath, status: result.status, exitCode: result.exitCode });
    attempts.closeAttempt({ attemptId: part.attemptId, reason: result.status.toLowerCase() });
    event(mega.projectId, result.status === 'COMPLETE' ? 'SUBTASK_COMPLETED' : 'SUBTASK_FAILED', `${sub.title}: ${result.summary}`, { megaTaskId: mega.id, subTaskId: sub.id, resultId: result.resultId });
  }
  function reconcile(mega) {
    const row = rowFor(mega.projectId, mega.id);
    for (const sub of subtasks(mega).filter(sub => sub.status === 'RUNNING')) {
      const part = (row.dispatch || []).find(part => part.subTaskId === sub.id && part.attemptId === sub.attemptIds.at(-1));
      if (!part) { sub.status = 'FAILED'; continue; }
      if (!part.pid) { const receipt = readJSON(part.exitPath + '.process.json'); if (receipt?.attemptId === part.attemptId && Number.isInteger(receipt.pid)) part.pid = receipt.pid; }
      part.observedAlive = Boolean(part.pid) && Boolean(isProcessAlive?.(part.pid)); part.alive = part.observedAlive;
      if (part.observedAlive) continue;
      const result = readValidatedResult(part.structuredResultPath, identity(mega, sub, part.attemptId), part.exitPath);
      const valid = result && (sub.type !== 'RESEARCH' || result.status !== 'COMPLETE' || reportExists(result, part.folder));
      collect(mega, sub, part, valid ? result : technical(mega, sub, part, 'FAILED', 'Worker process ended without a valid structured result and exit evidence.'));
    }
    row.workerAlive = (row.dispatch || []).some(part => part.alive);
    row.monitoring = { ...row.monitoring, workers: (row.dispatch || []).filter(part => part.pid).length, alive: (row.dispatch || []).filter(part => part.alive).length };
    return subtasks(mega);
  }
  function launch(item, job, mega, sub) {
    const row = rowFor(mega.projectId, mega.id);
    const requested = models().find(model => model.id === sub.assignedModelId && model.enabled && model.available !== false);
    if (requested && (requested.provider !== sub.proposedProvider || requested.cli !== sub.proposedCli)) { sub.status = 'BLOCKED'; mega.nextAction = 'Review changed provider/model configuration before continuing.'; return; }
    const worker = options.resolveWorker ? options.resolveWorker(requested, sub) : requested;
    if (worker && worker.id !== sub.assignedModelId) {
      const from = sub.assignedModelId;
      sub.fallbackFrom = from; sub.assignedModelId = worker.id; sub.proposedProvider = worker.provider; sub.proposedCli = worker.cli;
      event(mega.projectId, 'MODEL_USAGE_FALLBACK', 'Usage limit: switched from ' + from + ' to ' + worker.id + '.', { megaTaskId: mega.id, subTaskId: sub.id, fromModelId: from, modelId: worker.id });
      persist();
    }
    if (!worker || typeof spawnWorker !== 'function' || options.workerEligible && !options.workerEligible(worker, sub)) { sub.status = 'BLOCKED'; mega.nextAction = `Enable the exact worker ${sub.assignedModelId}, verify its remaining quota, and resume.`; return; }
    if (worker.provider !== sub.proposedProvider || worker.cli !== sub.proposedCli) { sub.status = 'BLOCKED'; mega.nextAction = 'Review changed provider/model configuration and explicitly reassign this slice on Resume.'; return; }
    if (sub.type === 'RESEARCH' && !adapterFor(worker)?.sourceProtectedResearch) { sub.status = 'BLOCKED'; mega.nextAction = 'Source-protected research is not supported by this adapter.'; return; }
    const claim = attempts.claim({ taskId: sub.id, projectId: mega.detached ? 'research:' + mega.id : mega.projectId, modelId: worker.id, candidates: models(), megaTaskId: mega.id, subTaskId: sub.id, research: Boolean(mega.detached) });
    if (!claim.ok) { sub.status = 'BLOCKED'; mega.nextAction = claim.reason; return; }
    const attemptId = claim.attempt.id;
    const folder = sub.type === 'RESEARCH' ? path.join(dir, 'research', mega.id, attemptId) : item.project.folder;
    if (sub.type === 'RESEARCH') fs.mkdirSync(folder, { recursive: true });
    const part = { modelId: worker.id, label: worker.label, provider: worker.provider, cli: worker.cli, subTaskId: sub.id, megaTaskId: mega.id, attemptId, slice: sub.title,
      resultPath: path.join(dir, 'results', attemptId + '.txt'), structuredResultPath: path.join(dir, 'results', attemptId + '.json'), exitPath: path.join(dir, 'results', attemptId + '.exit.json'),
      logPath: path.join(dir, 'logs', attemptId + '.log'), folder, pid: null, alive: false, startedAt: now(), sessionId: options.sessionId };
    if (sub.type === 'RESEARCH') Object.assign(part, { resultPath: path.join(folder, 'result.txt'), structuredResultPath: path.join(folder, 'result.json'), exitPath: path.join(folder, 'exit.json') });
    fs.mkdirSync(path.dirname(part.resultPath), { recursive: true }); fs.writeFileSync(part.resultPath, 'STATUS: WATCHING\n');
    attempts.attachResult({ attemptId, resultPath: part.structuredResultPath });
    sub.attemptIds.push(attemptId); sub.status = 'RUNNING'; sub.provider = worker.provider; sub.cli = worker.cli;
    row.dispatch ||= []; row.dispatch = row.dispatch.filter(existing => existing.subTaskId !== sub.id); row.dispatch.push(part);
    row.attemptId = attemptId; row.modelId = worker.id; row.resultPath = part.resultPath;
    // Persist identity and input ownership before any worker process is launched.
    persist();
    try {
      const handoff = { megaTask: mega, subTasks: subtasks(mega), results: data.results.filter(result => result.megaTaskId === mega.id), checkpoint: data.checkpoints.find(cp => cp.id === mega.currentCheckpointId), notes: (item.home?.notes || []).filter(note => note.aiRelevant === true) };
      const launched = spawnWorker({ ...part, projectId: mega.projectId, worker, ...identity(mega, sub, attemptId), job: { title: sub.title, kind: sub.type === 'RESEARCH' ? 'research' : sub.type === 'AUDIT' ? 'audit' : job.kind,
        detail: `${job.detail}\n\nSubTask: ${sub.detail}\n\nSource project (read only for research): ${item.project.folder}\n\nDurable handoff:\n${JSON.stringify(handoff)}` } });
      if (!Number.isInteger(launched?.pid) || launched.pid <= 0) throw new Error('Worker did not start.');
      Object.assign(part, { pid: launched.pid, alive: true, observedAlive: true, logPath: launched.logPath || part.logPath });
      row.workerPid = part.pid; row.workerLog = part.logPath;
      store.usage[worker.id] ||= { used: 0 }; store.usage[worker.id].used += worker.cost || 1;
      event(mega.projectId, 'SUBTASK_STARTED', `${worker.label || worker.id} started ${sub.title}.`, { megaTaskId: mega.id, subTaskId: sub.id, attemptId, provider: worker.provider, cli: worker.cli });
    } catch (error) { collect(mega, sub, part, technical(mega, sub, part, 'FAILED', 'Worker launch failed: ' + error.message)); }
    persist();
  }
  function checkpoint(item, job, mega, reason, interrupted = []) {
    const subs = subtasks(mega), currentRepo = repoState(item.project.folder, inspectProject(item));
    const cp = { id: 'CP-' + crypto.randomUUID(), projectId: mega.projectId, megaTaskId: mega.id, createdAt: now(), reason, planRevisionId: mega.planRevisionId,
      repoHead: currentRepo.repoHead, repoState: currentRepo, dirtyFiles: currentRepo.dirtyFiles, completedSubTaskIds: subs.filter(sub => sub.status === 'COMPLETE').map(sub => sub.id),
      pendingSubTaskIds: subs.filter(sub => ['PENDING', 'BLOCKED'].includes(sub.status)).map(sub => sub.id), failedSubTaskIds: subs.filter(sub => sub.status === 'FAILED').map(sub => sub.id),
      activeAttemptIds: [...new Set([...interrupted, ...(mega.interruptedAttemptIds || [])])], resultIds: subs.map(sub => sub.resultId).filter(Boolean), summary: `${subs.filter(sub => sub.status === 'COMPLETE').length}/${subs.length} slices complete. ${reason}.`,
      nextAction: mega.nextAction || 'Review remaining work and resume this checkpoint.', jobId: job.id, jobFingerprint: fingerprint(job), at: now() };
    currentRepo.commitIds = observedCommits(item.project.folder, mega.repoBefore.repoHead, currentRepo.repoHead);
    currentRepo.sourceChangesObserved = currentRepo.gitReadable && currentRepo.fingerprint && mega.repoBefore.fingerprint ? currentRepo.fingerprint !== mega.repoBefore.fingerprint : null;
    data.checkpoints.push(cp); mega.currentCheckpointId = cp.id; mega.repoAfter = currentRepo; mega.status = 'PAUSED'; mega.pauseRequested = false;
    rowFor(mega.projectId, mega.id).checkpoint = cp;
    event(mega.projectId, 'CHECKPOINT_SAVED', cp.summary, { megaTaskId: mega.id, checkpointId: cp.id }); persist(); return cp;
  }
  function paint(mega) {
    const row = rowFor(mega.projectId, mega.id), subs = subtasks(mega);
    row.jobId = mega.taskId; row.jobTitle = mega.title; row.jobFingerprint = mega.jobFingerprint; row.jobRevisionId = mega.planRevisionId;
    row.megaTaskId = mega.id; row.megaTask = clone(mega); row.subTasks = clone(subs);
    row.status = { READY_FOR_APPROVAL: 'needs_approval', RUNNING: 'watching', PAUSED: 'checkpointed', COMPLETE: 'complete', FAILED: 'failed', CANCELLED: 'idle' }[mega.status] || 'idle';
    row.doing = `${mega.title}: ${subs.filter(sub => sub.status === 'COMPLETE').length}/${subs.length} slices complete.`;
    if (mega.deadlineError) row.doing += ` Deadline reached. ${mega.deadlineError}`;
    row.workerActivity = row.doing; row.next = executionNext(mega, data);
    row.waitingFor = mega.status === 'READY_FOR_APPROVAL' ? `Approval of MegaTask ${mega.id}` : mega.status === 'PAUSED' ? 'Resume an explicit checkpoint' : mega.status === 'COMPLETE' ? 'Nothing. Structured results are complete.' : 'Structured worker results';
    row.workerLabel = (row.dispatch || []).filter(part => part.pid).map(part => part.label).join(' + ') || null;
    row.plan = [`MegaTask: ${mega.title}`, `Objective: ${mega.goal}`, `Latest git: ${mega.repoBefore.repoHead || 'Not applicable'}`, ...subs.map(sub => `${sub.title} — ${sub.assignedModelId}; ${sub.dependencies.length ? 'after ' + sub.dependencies.map(id => subs.find(parent => parent.id === id)?.title).join(', ') : 'parallel-ready'}; ${sub.status}`)];
    row.approved = Boolean(mega.approvedAt); return row;
  }
  function deadlineDue(mega) {
    return mega.timing === 'deadline' && !mega.deadlineHonoredAt
      && (Boolean(mega.deadlineReachedAt) || Number.isFinite(Date.parse(mega.deadlineAt)) && Date.parse(now()) >= Date.parse(mega.deadlineAt));
  }
  function complete(item, mega) {
    mega.status = 'COMPLETE'; mega.completedAt = now(); mega.repoAfter = repoState(item.project.folder, inspectProject(item));
    mega.deadlineError = null;
    mega.repoAfter.commitIds = observedCommits(item.project.folder, mega.repoBefore.repoHead, mega.repoAfter.repoHead);
    mega.repoAfter.sourceChangesObserved = mega.repoAfter.gitReadable && mega.repoAfter.fingerprint && mega.repoBefore.fingerprint ? mega.repoAfter.fingerprint !== mega.repoBefore.fingerprint : null;
    mega.nextAction = data.results.filter(result => result.megaTaskId === mega.id).at(-1)?.nextAction || 'Review the completed results.';
    event(mega.projectId, 'MEGATASK_COMPLETED', `Completed ${mega.title}. ${mega.repoAfter.disposition}.`, { megaTaskId: mega.id });
    if (mega.repoAfter.repoHead && mega.repoAfter.repoHead !== mega.repoBefore.repoHead) event(mega.projectId, 'GIT_COMMIT_OBSERVED', `Repository HEAD changed: ${mega.repoAfter.repoHead}`, { megaTaskId: mega.id, repoHead: mega.repoAfter.repoHead }, `commit:${mega.projectId}:${mega.repoAfter.repoHead}`);
  }
  function enforceDeadline(item, job, mega) {
    if (!mega.deadlineReachedAt) {
      mega.deadlineReachedAt = now();
      event(mega.projectId, 'DEADLINE_REACHED', `Finish by ${mega.deadlineAt} reached for ${mega.title}.`, { megaTaskId: mega.id }, `deadline:${mega.id}`);
    }
    mega.pauseRequested = true;
    persist();
    try {
      stop(item, job, mega.id, false, 'deadline');
      mega.deadlineHonoredAt = now(); mega.deadlineError = null;
    } catch (error) {
      mega.deadlineError = error.message;
      mega.nextAction = `Deadline reached. ${error.message} The backend will retry termination; no further slices will start.`;
      event(mega.projectId, 'DEADLINE_TERMINATION_FAILED', mega.nextAction, { megaTaskId: mega.id }, `deadline-failed:${mega.id}`);
    }
    persist();
  }
  function advance(item, job, parts, schedule = true) {
    observe(item);
    const mega = propose(item, job, parts), subs = reconcile(mega);
    if (mega.status === 'RUNNING') {
      if (deadlineDue(mega)) {
        if (subs.every(sub => sub.status === 'COMPLETE')) complete(item, mega);
        // Read-only projections reconcile evidence but never terminate processes.
        else if (schedule) enforceDeadline(item, job, mega);
      } else if (subs.some(sub => sub.status === 'FAILED')) {
        mega.pauseRequested = true; mega.nextAction = 'Review failed results and resume only the remaining work.';
        if (!subs.some(sub => sub.status === 'RUNNING')) { checkpoint(item, job, mega, 'worker failure'); mega.status = 'FAILED'; }
      } else if (mega.pauseRequested) {
        if (subs.every(sub => sub.status === 'COMPLETE')) complete(item, mega);
        else if (!subs.some(sub => sub.status === 'RUNNING')) checkpoint(item, job, mega, 'pause after current tasks');
      } else if (mega.authorizedSessionId !== options.sessionId && !subs.some(sub => sub.status === 'RUNNING') && subs.some(sub => sub.status !== 'COMPLETE')) {
        mega.nextAction = 'Resume the remaining work explicitly after backend restart.';
        checkpoint(item, job, mega, 'backend restarted');
      } else {
        for (const sub of schedule ? subs.filter(sub => sub.status === 'PENDING') : []) {
          if (deadlineDue(mega)) { enforceDeadline(item, job, mega); break; }
          if (subs.filter(sub => sub.status === 'RUNNING').length >= 3) break;
          if (sub.dependencies.every(id => data.subTasks.find(parent => parent.id === id)?.status === 'COMPLETE')) launch(item, job, mega, sub);
        }
        reconcile(mega);
        if (mega.status === 'RUNNING' && !deadlineDue(mega)) {
          if (subs.every(sub => sub.status === 'COMPLETE')) complete(item, mega);
          else if (subs.some(sub => sub.status === 'BLOCKED') && !subs.some(sub => sub.status === 'RUNNING')) checkpoint(item, job, mega, 'blocked worker');
        }
      }
    }
    paint(mega); persist(); return rowFor(mega.projectId, mega.id);
  }
  function stop(item, job, megaTaskId, graceful = false, reason = 'stop') {
    const mega = target(item.project.id, megaTaskId);
    if (mega.taskId !== job.id) throw new Error('MegaTask does not belong to the current job.');
    if (mega.status !== 'RUNNING') throw new Error('MegaTask is not running.');
    mega.pauseRequested = true;
    if (graceful) { mega.nextAction = 'Finish active slices, then save a checkpoint.'; paint(mega); persist(); return mega; }
    reconcile(mega);
    const active = subtasks(mega).filter(sub => sub.status === 'RUNNING');
    const interrupted = active.flatMap(sub => sub.attemptIds.slice(-1));
    mega.interruptedAttemptIds = [...new Set([...(mega.interruptedAttemptIds || []), ...interrupted])];
    const failures = [];
    for (const sub of active) {
      const part = rowFor(mega.projectId, mega.id).dispatch.find(part => part.subTaskId === sub.id);
      try {
        if (part.alive) {
          const terminated = killWorker?.(part.pid);
          if (isProcessAlive?.(part.pid) || terminated !== true && typeof isProcessAlive !== 'function') { failures.push(String(part.pid)); continue; }
        }
      } catch (error) { failures.push(`${part.pid}: ${error.message}`); continue; }
      part.alive = false; part.observedAlive = false;
      const partial = readJSON(part.structuredResultPath);
      const result = readValidatedResult(part.structuredResultPath, identity(mega, sub, part.attemptId), part.exitPath);
      collect(mega, sub, part, result && (sub.type !== 'RESEARCH' || result.status !== 'COMPLETE' || reportExists(result, part.folder)) ? result : technical(mega, sub, part, 'BLOCKED', reason === 'deadline' ? 'Interrupted at the Finish by deadline; partial evidence retained.' : 'Interrupted by Stop; partial evidence retained.', validateResult(partial, identity(mega, sub, part.attemptId)) ? partial : null));
    }
    const row = rowFor(mega.projectId, mega.id);
    row.workerAlive = (row.dispatch || []).some(part => part.alive);
    row.monitoring = { ...row.monitoring, alive: (row.dispatch || []).filter(part => part.alive).length };
    if (failures.length) { persist(); throw new Error(`Could not terminate worker process tree(s): ${failures.join(', ')}. Scheduling is paused; these processes are still active.`); }
    mega.deadlineError = null;
    // A successful explicit Stop also resolves any pending deadline termination.
    if (mega.deadlineReachedAt) mega.deadlineHonoredAt ||= now();
    if (reason === 'deadline' && subtasks(mega).every(sub => sub.status === 'COMPLETE')) {
      complete(item, mega); paint(mega); persist(); return mega;
    }
    mega.nextAction = reason === 'deadline' ? 'Deadline reached. Review partial results and explicitly resume this checkpoint to continue beyond the original deadline.' : 'Review remaining slices and resume this checkpoint.';
    checkpoint(item, job, mega, reason, interrupted); paint(mega); persist(); return mega;
  }
  function resume(item, job, request) {
    const choices = data.checkpoints.filter(cp => cp.projectId === item.project.id && ['PAUSED', 'FAILED'].includes(data.megaTasks.find(mega => mega.id === cp.megaTaskId)?.status));
    if ((!request.megaTaskId || !request.checkpointId) && choices.length !== 1) throw new Error('Choose an explicit MegaTask and checkpoint for this project.');
    const cp = choices.find(cp => cp.id === (request.checkpointId || choices[0]?.id) && cp.megaTaskId === (request.megaTaskId || choices[0]?.megaTaskId));
    if (!cp) throw new Error('Checkpoint does not belong to this project and MegaTask.');
    const mega = target(item.project.id, cp.megaTaskId);
    if (cp.jobFingerprint !== fingerprint(job)) throw new Error('Checkpoint belongs to a different job.');
    reconcile(mega);
    const current = repoState(item.project.folder, inspectProject(item));
    const diff = checkpointDiff(cp, current, item.revisions.at(-1).id, subtasks(mega).filter(sub => sub.status === 'COMPLETE').map(sub => sub.id));
    if ((!current.comparable || !cp.repoState.comparable || current.fingerprint !== cp.repoState.fingerprint || item.revisions.at(-1).id !== cp.planRevisionId) && !request.confirmChanges) {
      return { ok: false, requiresConfirmation: true, reason: 'Repository or Living Plan changed since the checkpoint. Review changes and explicitly confirm before continuing.', checkpointId: cp.id, megaTaskId: mega.id,
        diff, before: { ...cp.repoState, planRevisionId: cp.planRevisionId }, current: { ...current, planRevisionId: item.revisions.at(-1).id } };
    }
    if (request.assignments !== undefined) {
      if (!request.assignments || typeof request.assignments !== 'object' || Array.isArray(request.assignments)) throw new Error('Worker assignments must map SubTask IDs to exact model IDs.');
      for (const [id, modelId] of Object.entries(request.assignments)) {
        const sub = subtasks(mega).find(sub => sub.id === id && sub.status !== 'COMPLETE');
        if (!sub || !models().some(model => model.id === modelId && model.enabled && model.available !== false && (!options.workerEligible || options.workerEligible(model, sub)))) throw new Error('Invalid remaining SubTask or exact worker model.');
      }
    }
    for (const sub of subtasks(mega)) if (sub.status !== 'COMPLETE') {
      sub.status = 'PENDING'; sub.resultId = null;
      if (request.assignments?.[sub.id]) { sub.assignedModelId = request.assignments[sub.id]; const worker = models().find(model => model.id === sub.assignedModelId); sub.proposedProvider = worker.provider; sub.proposedCli = worker.cli; }
    }
    mega.status = 'RUNNING'; mega.pauseRequested = false; mega.interruptedAttemptIds = []; mega.orchestratorModelId = leadId(); mega.currentCheckpointId = cp.id; mega.authorizedSessionId = options.sessionId;
    mega.deadlineError = null;
    // Explicit Resume authorizes continuation beyond a deadline already reached.
    if (cp.reason === 'deadline') mega.deadlineHonoredAt ||= now();
    event(mega.projectId, 'MEGATASK_RESUMED', `Resumed ${mega.title} from ${cp.id}.`, { megaTaskId: mega.id, checkpointId: cp.id, orchestratorModelId: mega.orchestratorModelId }); persist();
    return { ok: true, megaTaskId: mega.id, checkpointId: cp.id };
  }
  function stopWorker(item, job, megaTaskId, pid) {
    const mega = target(item.project.id, megaTaskId);
    if (mega.status !== 'RUNNING' || mega.taskId !== job.id) throw new Error('Worker does not belong to this running MegaTask.');
    const part = rowFor(mega.projectId, mega.id).dispatch.find(part => part.pid === pid && part.alive);
    const sub = part && subtasks(mega).find(sub => sub.id === part.subTaskId && sub.status === 'RUNNING');
    if (!sub) throw new Error('Worker is no longer active on this project.');
    mega.pauseRequested = true; persist();
    if (!killWorker?.(pid) || isProcessAlive?.(pid)) throw new Error('Could not terminate the worker process tree. Scheduling is paused.');
    part.alive = false; part.observedAlive = false;
    const partial = readJSON(part.structuredResultPath);
    collect(mega, sub, part, technical(mega, sub, part, 'BLOCKED', 'Worker stopped by you; remaining workers may finish.', validateResult(partial, identity(mega, sub, part.attemptId)) ? partial : null));
    mega.interruptedAttemptIds = [...(mega.interruptedAttemptIds || []), part.attemptId];
    mega.nextAction = 'Finish other active slices, then resume remaining work from the checkpoint.';
    persist(); return mega;
  }
  function view(projectId) {
    const megas = data.megaTasks.filter(mega => mega.projectId === projectId);
    const ids = new Set(megas.map(mega => mega.id));
    return clone({ megaTasks: megas, subTasks: data.subTasks.filter(sub => ids.has(sub.megaTaskId)), results: data.results.filter(result => ids.has(result.megaTaskId)),
      checkpoints: data.checkpoints.filter(cp => cp.projectId === projectId), timeline: data.timeline.filter(item => item.projectId === projectId) });
  }
  function restore(projectId, incoming) {
    const payload = incoming && typeof incoming === 'object' ? clone(incoming) : {};
    for (const mega of Array.isArray(payload.megaTasks) ? payload.megaTasks : []) {
      if (mega && mega.projectId === projectId && !data.megaTasks.some(item => item.id === mega.id)) data.megaTasks.push(mega);
    }
    const known = new Set(data.megaTasks.filter(mega => mega.projectId === projectId).map(mega => mega.id));
    for (const sub of Array.isArray(payload.subTasks) ? payload.subTasks : []) if (sub && known.has(sub.megaTaskId) && !data.subTasks.some(item => item.id === sub.id)) data.subTasks.push(sub);
    for (const result of Array.isArray(payload.results) ? payload.results : []) if (result && known.has(result.megaTaskId)) data.results.push(result);
    for (const checkpoint of Array.isArray(payload.checkpoints) ? payload.checkpoints : []) if (checkpoint && checkpoint.projectId === projectId && !data.checkpoints.some(item => item.id === checkpoint.id)) data.checkpoints.push(checkpoint);
    for (const eventItem of Array.isArray(payload.timeline) ? payload.timeline : []) if (eventItem && eventItem.projectId === projectId) data.timeline.push(eventItem);
    const last = data.megaTasks.filter(mega => mega.projectId === projectId).at(-1);
    if (last) {
      const row = rowFor(projectId, last.id);
      const checkpoint = data.checkpoints.find(item => item.id === last.currentCheckpointId) || data.checkpoints.filter(item => item.megaTaskId === last.id).at(-1);
      if (checkpoint) row.checkpoint = checkpoint;
      paint(last);
    }
    persist();
  }
  function checkpointForUpdate(item, job, megaTaskId) {
    const mega = target(item.project.id, megaTaskId);
    if (mega.taskId !== job.id) throw new Error('Update checkpoint belongs to another task.');
    if (mega.status === 'RUNNING') return stop(item, job, megaTaskId, false, 'application update');
    if (mega.status === 'COMPLETE') return mega;
    if (!mega.currentCheckpointId) checkpoint(item, job, mega, 'application update');
    paint(mega); persist(); return mega;
  }
  return { propose, approve, advance, stop, stopWorker, resume, view, find, target, observe, event, reconcile, paint, checkpointForUpdate, restore };
}
module.exports = { createExecutionBridge, repoState };
