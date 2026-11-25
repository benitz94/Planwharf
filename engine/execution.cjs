'use strict';
// The role a step plays, as the chat shows it.
const stepRole = sub => sub.type === 'AUDIT' ? 'Review' : sub.type === 'RESEARCH' ? 'Research' : /^Verify the work independently/.test(String(sub.title || '')) ? 'Verify' : 'Build';
// Up to this many steps of one work run at the same time; the lead decides how many it needs.
const MAX_PARALLEL = 10;
// A step limited to some files is told so, and why: other workers change the rest at the same time.
const scopeText = (sub, isolated = false) => Array.isArray(sub.scope) && sub.scope.length
  ? `\n\nYour scope: change only these files or folders, relative to ${isolated ? 'this worker folder' : 'the project folder'}: ${sub.scope.join(', ')}. ${isolated ? 'Other workers have their own folders.' : 'Other workers are changing other files in this folder at the same time.'} A change outside your scope stops your part.`
    + (Array.isArray(sub.scopeViolation) && sub.scopeViolation.length ? `\nLast time this part was stopped for changing files outside its scope: ${sub.scopeViolation.join(', ')}. Do not touch them: if the work needs them, say so in your result and stop.` : '')
  : '';
const slash = file => String(file).replace(/\\/g, '/').replace(/^\.\//, '').replace(/\/+$/, '');
const inScope = (file, scope) => scope.some(entry => file === entry || file.startsWith(entry + '/'));
// Files that differ from the last commit, as git lists them (renames count by their new name).
function changedFiles(folder) {
  const root = linkedWorkspace(folder);
  if (!root) return null;
  const listed = spawnSync('git', ['status', '--porcelain', '-z', '--untracked-files=all'], { cwd: root, encoding: 'utf8', windowsHide: true, timeout: 15000, maxBuffer: 16 * 1024 * 1024 });
  if (listed.status !== 0) return null;
  const out = [], rows = listed.stdout.split('\0');
  for (let index = 0; index < rows.length; index += 1) {
    const row = rows[index];
    if (row.length < 4) continue;
    out.push(slash(row.slice(3)));
    if (/^[RC]/.test(row)) index += 1; // the old name of a rename follows
  }
  return out;
}
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const planFile = require('./plan-file.cjs');
const { spawnSync } = require('node:child_process');
const { readJSON, readValidatedResult, validateResult, reportExists } = require('./worker-result.cjs');
const { linkedWorkspace, within, safeGitWorkspace, gitEnvironment } = require('./project-workspace.cjs');
const { executionNext } = require('./execution-next.cjs');
const { adapterFor } = require('./provider-adapters.cjs');
const { checkpointDiff } = require('./project-history.cjs');
const { refusalReason } = require('./provider-refusal.cjs');
const timeLeft = require('./time-left.cjs');
const stepOutcomes = require('./step-outcomes.cjs');
const worktrees = require('./worktrees.cjs');
const clone = value => structuredClone(value);
const fingerprint = job => crypto.createHash('sha256').update(JSON.stringify([job.id, job.title, job.detail, job.revision_id || null, ...(job.goldModel ? [job.goldModel] : [])])).digest('hex');

function observedCommits(folder, before, after) {
  if (!folder || before === after || !/^[a-f0-9]{40,64}$/.test(before || '') || !/^[a-f0-9]{40,64}$/.test(after || '')) return [];
  folder = linkedWorkspace(folder);
  if (!folder || !safeGitWorkspace(folder)) return [];
  const root = spawnSync('git', ['--no-optional-locks', '-C', folder, 'rev-parse', '--show-toplevel'], { encoding: 'utf8', env: gitEnvironment(), timeout: 4000, windowsHide: true, maxBuffer: 65536 });
  if (root.status !== 0 || linkedWorkspace(String(root.stdout || '').trim()) !== folder) return [];
  const result = spawnSync('git', ['--no-optional-locks', '-C', folder, 'rev-list', '--max-count=1000', `${before}..${after}`], { encoding: 'utf8', env: gitEnvironment(), timeout: 4000, windowsHide: true, maxBuffer: 256 * 1024 });
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
      const root = linkedWorkspace(folder);
      if (!root || !within(root, fs.realpathSync(path.dirname(file)))) { comparable = false; continue; }
      const stat = fs.lstatSync(file); bytes += stat.size;
      if (!stat.isFile() || stat.isSymbolicLink() || bytes > 8 * 1024 * 1024) { comparable = false; continue; }
      contents.push([name, crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')]);
    } catch { contents.push([name, 'unavailable']); comparable = false; }
  }
  const repoHead = facts.commitId || facts.lastCommit || facts.head || null;
  return { repoHead, dirtyFiles, comparable, gitReadable: Boolean(facts.git), fingerprint: comparable ? crypto.createHash('sha256').update(JSON.stringify([repoHead, dirtyFiles, contents])).digest('hex') : null,
    disposition: !facts.git ? 'NOT APPLICABLE' : dirtyFiles.length ? 'UNCOMMITTED' : 'COMMITTED' };
}


// A dead worker with no structured result still delivered when it left a new commit
// on the branch, exit 0, and (when the step has a scope) only those files. Exit 0
// alone is not enough: there must be a commit after the step started.
function deliveredWithoutVerdict({ folder, before, after, exitCode, scope, names }) {
  if (exitCode !== 0) return null;
  if (!folder || !before || !after || before === after) return null;
  if (!/^[a-f0-9]{40,64}$/.test(before) || !/^[a-f0-9]{40,64}$/.test(after)) return null;
  const files = (names || []).map(slash).filter(Boolean);
  if (!files.length) return null;
  if (Array.isArray(scope) && scope.length) {
    const owned = scope.map(slash);
    if (files.some(file => !inScope(file, owned))) return null;
  }
  return { filesChanged: files.slice(0, 40), summary: `Delivered on disk without a structured result: ${files.length} file(s) committed after the step started.` };
}

function createExecutionBridge(options) {
  const { store, attempts, dir, rowFor, inspect, spawnWorker, isProcessAlive, killWorker, models, now, persist, leadId } = options;
  store.execution ||= { megaTasks: [], subTasks: [], results: [], checkpoints: [], timeline: [], observations: {} };
  const data = store.execution;
  for (const key of ['megaTasks', 'subTasks', 'results', 'checkpoints', 'timeline', 'decisions']) if (!Array.isArray(data[key])) data[key] = [];
  data.observations ||= {};
  const subtasks = mega => data.subTasks.filter(sub => sub.megaTaskId === mega.id);
  const inspectProject = item => { const folder = linkedWorkspace(item.project.folder); return folder ? inspect(folder) : { error: 'Relink a real local project folder before implementation.', git: false, dirty: [], names: [], fileCount: 0 }; };
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
    if (facts?.error && job.kind !== 'research' && !parts.every(part => part.type === 'RESEARCH')) throw new Error(facts.error);
    mega = { id, projectId: item.project.id, taskId: job.id, planRevisionId: job.revision_id, title: job.title, goal: job.detail || item.revisions.at(-1).direction,
      createdAt: now(), approvedAt: null, status: 'READY_FOR_APPROVAL', orchestratorModelId: leadId(), currentCheckpointId: null, completedAt: null,
      jobFingerprint: fingerprint(job), detached: Boolean(job.detached), repoBefore: repoState(item.project.folder, facts), nextAction: 'Review the proposed slices and approve this MegaTask.', pauseRequested: false };
    mega.timing = job.timing || 'discretion'; mega.deadlineAt = job.timing === 'deadline' ? job.deadlineAt : null;
    const ids = parts.map((_, index) => `${id}-ST-${String(index + 1).padStart(2, '0')}`);
    const subs = parts.map((part, index) => ({ id: ids[index], megaTaskId: id, title: part.slice || part.title, detail: part.detail || part.slice || job.detail, status: 'PENDING', assignedModelId: part.modelId,
      ...(job.goldModel ? { goldModel: { ...job.goldModel } } : {}),
      attemptIds: [], resultId: null, createdAt: now(), completedAt: null, type: part.type || (job.kind === 'research' ? 'RESEARCH' : job.kind === 'audit' ? 'AUDIT' : 'IMPLEMENTATION'),
      dependencies: part.dependsOn ? part.dependsOn.map(index => ids[index]) : part.dependencies || (index ? [ids[index - 1]] : []),
      proposedProvider: models().find(model => model.id === part.modelId)?.provider || null, proposedCli: models().find(model => model.id === part.modelId)?.cli || null, provider: null, cli: null,
      ...(Array.isArray(part.scope) && part.scope.length ? { scope: [...part.scope] } : {}), ...(part.size ? { size: part.size } : {}) }));
    for (const sub of subs) for (const dependency of sub.dependencies) if (!ids.includes(dependency) || ids.indexOf(dependency) >= ids.indexOf(sub.id)) throw new Error('Dependencies must reference an earlier SubTask in the same MegaTask.');
    data.megaTasks.push(mega); data.subTasks.push(...subs);
    mega.blockers = proposalBlockers(mega);
    if (mega.blockers.length) mega.nextAction = 'Resolve the proposal blockers before approval: ' + mega.blockers.join(' ');
    if (!job.detached) rowFor(mega.projectId, mega.id).megaTaskId = id;
    event(mega.projectId, 'MEGATASK_PROPOSED', `${mega.title}: ${subs.length} proposed slice(s).`, { megaTaskId: id });
    persist();
    return mega;
  }
  function approve(projectId, megaTaskId, policy = null) {
    const mega = target(projectId, megaTaskId);
    if (mega.status !== 'READY_FOR_APPROVAL') throw new Error('Only a proposed MegaTask can be approved.');
    mega.blockers = proposalBlockers(mega);
    if (mega.blockers.length) { persist(); throw new Error(mega.blockers.join(' ')); }
    mega.approvedAt = now(); mega.status = 'RUNNING'; mega.approvedMegaTaskId = mega.id; mega.authorizedSessionId = options.sessionId; mega.approvedBy = policy ? 'autonomy' : 'owner';
    event(projectId, 'MEGATASK_APPROVED', policy ? `Approved ${mega.title} by the project autonomy setting.` : `Approved ${mega.title}.`, { megaTaskId }); persist();
    return mega;
  }
  function identity(mega, sub, attemptId) { return { megaTaskId: mega.id, subTaskId: sub.id, attemptId, resultId: 'R-' + attemptId, provider: sub.provider, cli: sub.cli }; }
  function proposalBlockers(mega) {
    if (typeof spawnWorker !== 'function') return ['The worker launcher is unavailable. Start Planwharf through its desktop launcher.'];
    return subtasks(mega).flatMap(sub => {
      const worker = models().find(model => model.id === sub.assignedModelId);
      const candidate = options.resolveWorker ? options.resolveWorker(worker, sub) : worker;
      if (!candidate?.enabled || candidate.available === false || options.workerEligible && !options.workerEligible(candidate, sub)) {
        // Waiting for quota does not hold the approval: the step starts by itself when a model of the crew has quota.
        const why = options.explainNoWorker ? options.explainNoWorker(worker, sub) : null;
        if (why?.waiting) return [];
        return [why?.message || `Worker ${sub.assignedModelId} is unavailable or has insufficient remaining quota.`];
      }
      // A step switched to another model of the crew is fine; only a changed configuration of the same model needs a new proposal.
      if (candidate.id === worker?.id && (worker.provider !== sub.proposedProvider || worker.cli !== sub.proposedCli)) return [`Provider/model configuration changed for ${sub.assignedModelId}; review a new proposal.`];
      if (!worker && candidate.id === sub.assignedModelId) return [`Provider/model configuration changed for ${sub.assignedModelId}; review a new proposal.`];
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
  // A part limited to some files that changed others is stopped, and told why when it runs again.
  // Its own report is always checked. An isolated worktree can also be checked against its base
  // while other parts run; the shared folder is checked only when no other part worked concurrently.
  function scopeCheck(mega, sub, part, result) {
    if (result.status !== 'COMPLETE' || !Array.isArray(sub.scope) || !sub.scope.length) return result;
    // A part that waits for others may also change their files: they are finished, so no one else works on
    // them, and fixing or wiring them in is what an integration part is for.
    const all = subtasks(mega);
    const earlier = new Set(), queue = [...(sub.dependencies || [])];
    while (queue.length) { const id = queue.pop(); if (earlier.has(id)) continue; earlier.add(id); queue.push(...(all.find(other => other.id === id)?.dependencies || [])); }
    // Work that continues a stopped one may also change what the stopped work finished (its parts are
    // over), unless a part of this work that does not come before it owns the file.
    const continued = data.megaTasks.filter(other => other.projectId === mega.projectId && other.id !== mega.id && String(other.status).toUpperCase() === 'CANCELLED').map(other => other.id);
    const finishedBefore = data.results.filter(item => item.status === 'COMPLETE' && continued.includes(item.megaTaskId)).flatMap(item => item.filesChanged || []).map(slash);
    const busy = all.filter(other => other.id !== sub.id && !earlier.has(other.id)).flatMap(other => other.scope || []).map(slash);
    const scope = [...sub.scope, ...all.filter(other => earlier.has(other.id) && other.status === 'COMPLETE').flatMap(other => other.scope || []),
      ...finishedBefore.filter(file => !inScope(file, busy))].map(slash);
    const outside = new Set((result.filesChanged || []).map(slash).filter(file => file && !inScope(file, scope)));
    const others = all.filter(other => other.id !== sub.id);
    // Only when no other part worked while this one did can a changed file be put down to this part.
    const alone = !others.some(other => other.status === 'RUNNING' || (other.finishedAt && part.startedAt && other.finishedAt > part.startedAt));
    if (alone || part.branch) {
      const before = new Set((mega.repoBefore?.dirtyFiles || []).map(record => slash(record.slice(/^[MADRCU?!] \S/.test(record) ? 2 : 3).split(' -> ').at(-1).replace(/^"|"$/g, ''))));
      const owned = others.filter(other => other.status === 'COMPLETE').flatMap(other => (other.scope || []).map(slash));
      for (const file of changedFiles(part.folder) || []) if (!before.has(file) && !inScope(file, scope) && !inScope(file, owned)) outside.add(file);
      if (part.branch && part.baseHead) {
        try { for (const file of worktrees.git(part.folder, ['diff', '--name-only', `${part.baseHead}..HEAD`]).split(/\r?\n/).filter(Boolean)) if (!inScope(file, scope)) outside.add(file); }
        catch { outside.add('(commit inspection unavailable)'); }
      }
    }
    // The scope keeps two workers off the same file at the same time. A part that waits for others (the
    // one that wires them together) runs when they are done: only a file of a part that may still be
    // working, or is still to start, is a conflict for it. Its own list may leave out files nobody owns.
    if (earlier.size) for (const file of [...outside]) if (!inScope(file, busy)) outside.delete(file);
    if (!outside.size) { delete sub.scopeViolation; return result; }
    sub.scopeViolation = [...outside].slice(0, 20);
    return { ...result, status: 'BLOCKED', blockers: [...(result.blockers || []), `Changed files outside its scope: ${sub.scopeViolation.join(', ')}.`],
      summary: `Stopped: changed files outside its scope (${sub.scopeViolation.join(', ')}). ${result.summary}` };
  }
  function collect(mega, sub, part, result) {
    if (data.results.some(item => item.resultId === result.resultId)) return;
    result = scopeCheck(mega, sub, part, result);
    if (sub.type === 'INTEGRATION' && result.status === 'COMPLETE') {
      try {
        for (const branch of (mega.worktreeParts || []).filter(branch => mega.activeGroup?.includes(branch.subId))) worktrees.git(part.projectFolder, ['merge-base', '--is-ancestor', branch.branch, 'HEAD']);
        if (worktrees.workChanges(worktrees.git(part.projectFolder, ['status', '--porcelain']))) throw new Error('Work branch has uncommitted changes');
        mega.integratedSubIds = [...new Set([...(mega.integratedSubIds || []), ...(mega.activeGroup || [])])];
        mega.activeGroup = null; mega.awaitingIntegration = false;
      } catch (error) { result = { ...result, status: 'BLOCKED', summary: `Integration is unfinished: ${error.message}`, blockers: [...(result.blockers || []), error.message] }; }
    }
    sub.finishedAt = now();
    result = { ...result, assignedModelId: sub.assignedModelId, provider: sub.provider, cli: sub.cli, pid: part.pid, startedAt: part.startedAt };
    data.results.push(result); sub.resultId = result.resultId; sub.status = result.status;
    // A decision the worker could not make is kept with its work, so the owner is asked under that work.
    for (const line of String(result.summary || '').split(/\r?\n/)) {
      const at = line.toLowerCase().indexOf('needs your decision:');
      if (at < 0) continue;
      const raw = line.slice(at + 'needs your decision:'.length).trim();
      if (!raw || data.decisions.some(item => item.projectId === mega.projectId && item.megaTaskId === mega.id && item.text === raw.split('|')[0].trim())) continue;
      const parts = raw.split('|').map(part => part.trim());
      const text = parts[0];
      const suggestion = (parts.find(part => part.toLowerCase().startsWith('suggestion:')) || '').slice('suggestion:'.length).trim();
      const breaks = /breaks:\s*yes/i.test(raw);
      const isAuto = typeof options.isAutoDecide === 'function'
        ? options.isAutoDecide(mega.projectId, item)
        : Boolean(item?.project?.autoDecide || store?.autoDecide?.[mega.projectId] || store?.defaultAutoDecide);
      const dec = {
        id: result.resultId + ':' + data.decisions.length,
        projectId: mega.projectId,
        megaTaskId: mega.id,
        text,
        suggestion,
        breaks,
        at: now(),
        answer: breaks ? '' : (suggestion || 'accepted'),
        answeredBy: breaks ? null : 'system',
        answeredAt: breaks ? null : now()
      };
      if (isAuto) {
        const leadLabel = typeof options.leadLabel === 'function' ? options.leadLabel(mega.projectId) : 'Lead';
        dec.answer = suggestion || 'yes';
        dec.answeredBy = `Decided by ${leadLabel}`;
        dec.autoReason = suggestion ? `Followed recommended suggestion: ${suggestion}` : 'Safe default accepted.';
        dec.answeredAt = now();
        event(mega.projectId, 'DECISION_AUTO_ANSWERED', `${dec.answeredBy}: ${text} -> ${dec.answer}`, { megaTaskId: mega.id, decisionId: dec.id, breaks });
      }
      data.decisions.push(dec);
    }
    part.validatedResultStatus = result.status; part.recordedSubTaskStatus = sub.status;
    sub.completedAt = result.status === 'COMPLETE' ? now() : null;
    attempts.attachResult({ attemptId: part.attemptId, resultPath: part.structuredResultPath, status: result.status, exitCode: result.exitCode });
    attempts.closeAttempt({ attemptId: part.attemptId, reason: result.status.toLowerCase() });
    // A step stopped to change its model did not fail: it is shown as a change, not as an error.
    event(mega.projectId, result.status === 'COMPLETE' ? 'SUBTASK_COMPLETED' : result.modelChange ? 'SUBTASK_REASSIGNED' : 'SUBTASK_FAILED', `${sub.title}: ${result.summary}`, { megaTaskId: mega.id, subTaskId: sub.id, resultId: result.resultId });
    if (result.status === 'COMPLETE' && sub.type !== 'RESEARCH') {
      try { planFile.tickStep(part.projectFolder, { title: sub.title, role: stepRole(sub), model: part.label, commit: part.branch ? `${part.branch} ${worktrees.git(part.folder, ['rev-parse', '--short', 'HEAD'])}` : planFile.commitNote(part.projectFolder) }); }
      catch { /* the step result is already saved */ }
    }
    // How long this step took, for the time-left estimate. No prompt, path or project name.
    try {
      appendStepTime(dir, timeLeft.stepRecord({
        role: stepRole(sub), model: sub.assignedModelId, size: sub.size,
        files: Array.isArray(result.filesChanged) ? result.filesChanged.length : 0,
        startedAt: part.startedAt, endedAt: sub.finishedAt, status: result.status, modelChange: Boolean(result.modelChange)
      }));
    } catch { /* the step result is already saved */ }
    try {
      let tokens = null;
      if (part.structuredResultPath) {
        try { tokens = JSON.parse(fs.readFileSync(part.structuredResultPath + '.usage.json', 'utf8')); } catch {}
      }
      const role = stepRole(sub);
      const isSwitch = Boolean(result.modelChange);
      if (isSwitch) {
        appendStepOutcome(dir, {
          model: sub.assignedModelId, role, size: sub.size, kind: sub.type || mega.kind,
          switch: true, refusal: null, accepted: false, fixed: 0,
          startedAt: part.startedAt, endedAt: sub.finishedAt, quota: tokens
        });
      } else if (result.status !== 'COMPLETE') {
        appendStepOutcome(dir, {
          model: sub.assignedModelId, role, size: sub.size, kind: sub.type || mega.kind,
          switch: false, refusal: null, accepted: false, fixed: 0,
          tests: result.tests, testsReported: result.testsReported, testsPassed: result.testsPassed,
          startedAt: part.startedAt, endedAt: sub.finishedAt, quota: tokens
        });
      } else if (role === 'Verify') {
        const filesFixed = Array.isArray(result.filesChanged) ? result.filesChanged.length : 0;
        const accepted = filesFixed === 0;
        appendStepOutcome(dir, {
          model: sub.assignedModelId, role: 'Verify', size: sub.size, kind: sub.type || mega.kind,
          accepted, fixed: filesFixed,
          tests: result.tests, testsReported: result.testsReported, testsPassed: result.testsPassed,
          switch: false, refusal: null,
          startedAt: part.startedAt, endedAt: sub.finishedAt, quota: tokens
        });
        mega.recordedOutcomeSubIds = [...(mega.recordedOutcomeSubIds || []), sub.id];
        for (const bSub of (data.subTasks || []).filter(s => s.megaTaskId === mega.id && stepRole(s) === 'Build' && s.status === 'COMPLETE')) {
          if (mega.recordedOutcomeSubIds.includes(bSub.id)) continue;
          mega.recordedOutcomeSubIds.push(bSub.id);
          const bRes = data.results.filter(r => r.subTaskId === bSub.id && r.status === 'COMPLETE').at(-1) || data.results.filter(r => r.subTaskId === bSub.id).at(-1);
          const bPart = (rowFor(mega.projectId, mega.id).dispatch || []).filter(p => p.subTaskId === bSub.id).at(-1);
          let bTokens = null;
          if (bPart?.structuredResultPath) {
            try { bTokens = JSON.parse(fs.readFileSync(bPart.structuredResultPath + '.usage.json', 'utf8')); } catch {}
          }
          appendStepOutcome(dir, {
            model: bSub.assignedModelId, role: 'Build', size: bSub.size, kind: bSub.type || mega.kind,
            accepted, fixed: filesFixed,
            tests: bRes?.tests, testsReported: bRes?.testsReported, testsPassed: bRes?.testsPassed,
            switch: false, refusal: null,
            startedAt: bPart?.startedAt, endedAt: bSub.finishedAt, quota: bTokens
          });
        }
      } else {
        const hasVerify = (data.subTasks || []).some(s => s.megaTaskId === mega.id && stepRole(s) === 'Verify');
        if (!hasVerify) {
          mega.recordedOutcomeSubIds = [...(mega.recordedOutcomeSubIds || []), sub.id];
          appendStepOutcome(dir, {
            model: sub.assignedModelId, role, size: sub.size, kind: sub.type || mega.kind,
            accepted: true, fixed: 0,
            tests: result.tests, testsReported: result.testsReported, testsPassed: result.testsPassed,
            switch: false, refusal: null,
            startedAt: part.startedAt, endedAt: sub.finishedAt, quota: tokens
          });
        }
      }
    } catch { /* the step result is already saved */ }
  }
  // A provider that refused the slice for credits or quota did no work: close that attempt,
  // put the model on cooldown, and give the slice to another eligible model.
  function rerouteRefusal(mega, sub, part) {
    if (typeof options.onRefusal !== 'function' || typeof options.replacement !== 'function' || (sub.refusalReroutes || 0) >= 2) return false;
    let text = ''; try { text = fs.readFileSync(part.logPath, 'utf8'); } catch {}
    const reason = refusalReason(text);
    if (!reason) return false;
    try {
      appendStepOutcome(dir, {
        model: sub.assignedModelId, role: stepRole(sub), size: sub.size, kind: sub.type || mega.kind,
        refusal: reason, switch: false, accepted: false, fixed: 0,
        startedAt: part.startedAt, endedAt: now()
      });
    } catch {}
    options.onRefusal(sub.assignedModelId, reason);
    const next = options.replacement(sub, [sub.assignedModelId]);
    if (!next) return false;
    attempts.closeAttempt({ attemptId: part.attemptId, reason: 'provider refused' });
    const from = sub.assignedModelId;
    Object.assign(sub, { status: 'PENDING', refusalReroutes: (sub.refusalReroutes || 0) + 1, fallbackFrom: from, assignedModelId: next.id, proposedProvider: next.provider, proposedCli: next.cli });
    part.alive = false; part.refused = reason;
    event(mega.projectId, 'MODEL_REFUSAL_FALLBACK', `${from} refused the slice (${reason}). Reassigned to ${next.id}.`, { megaTaskId: mega.id, subTaskId: sub.id, fromModelId: from, modelId: next.id });
    persist();
    return true;
  }

  // A dead worker with no result and no commit gets one more try on the same model, then one
  // try on the next eligible model. A commit already on the branch is a delivery, not a retry.
  // Two models in all: after the second miss the step fails and the owner decides.
  function committedSince(mega, part) {
    const folder = linkedWorkspace(part.folder);
    const before = part.baseHead || mega.repoBefore?.repoHead;
    if (!folder || !before || !safeGitWorkspace(part.projectFolder)) return null;
    const after = spawnSync('git', ['--no-optional-locks', '-C', folder, 'rev-parse', 'HEAD'], { encoding: 'utf8', env: gitEnvironment(), timeout: 4000, windowsHide: true }).stdout.trim();
    if (!/^[a-f0-9]{40,64}$/.test(after) || after === before) return null;
    const listed = spawnSync('git', ['--no-optional-locks', '-C', folder, 'diff', '--name-only', before + '..' + after], { encoding: 'utf8', env: gitEnvironment(), timeout: 4000, windowsHide: true });
    const names = listed.status === 0 ? listed.stdout.trim().split(/\r?\n/).filter(Boolean) : [];
    if (!names.length) return null;
    return { filesChanged: names.slice(0, 40), summary: 'Delivered on disk: the step committed before the process ended, with no result file.' };
  }
  function rerouteNoVerdict(mega, sub, part) {
    const tries = sub.noVerdictTries || 0;
    if (noVerdictPlan(tries) === 'stop') return false;
    let text = ''; try { text = fs.readFileSync(part.logPath, 'utf8'); } catch {}
    attempts.closeAttempt({ attemptId: part.attemptId, reason: 'no verdict' });
    sub.takeover = { fromModelId: sub.assignedModelId, at: now(), filesChanged: [], tests: [], summary: 'The previous attempt ended without a result. Continue from the folder; do not start over.', logTail: text.slice(-1500) };
    part.alive = false;
    if (noVerdictPlan(tries) === 'retry-same') {
      Object.assign(sub, { status: 'PENDING', noVerdictTries: 1, resultId: null });
      event(mega.projectId, 'MODEL_NO_VERDICT_RETRY', sub.assignedModelId + ' ended without a result. Same model tries once more.', { megaTaskId: mega.id, subTaskId: sub.id, modelId: sub.assignedModelId });
      persist();
      return true;
    }
    if (typeof options.replacement !== 'function') return false;
    const next = options.replacement(sub, [sub.assignedModelId, ...(sub.noVerdictFrom || [])]);
    if (!next) return false;
    const from = sub.assignedModelId;
    try {
      appendStepOutcome(dir, {
        model: from, role: stepRole(sub), size: sub.size, kind: sub.type || mega.kind,
        switch: true, refusal: null, accepted: false, fixed: 0,
        startedAt: part.startedAt, endedAt: now()
      });
    } catch {}
    Object.assign(sub, { status: 'PENDING', noVerdictTries: 2, noVerdictFrom: [...(sub.noVerdictFrom || []), from], fallbackFrom: from, assignedModelId: next.id, proposedProvider: next.provider, proposedCli: next.cli, resultId: null });
    event(mega.projectId, 'MODEL_NO_VERDICT_SWITCH', from + ' ended without a result twice. Reassigned to ' + next.id + '.', { megaTaskId: mega.id, subTaskId: sub.id, fromModelId: from, modelId: next.id });
    persist();
    return true;
  }
  function deliveryOf(mega, sub, part) {
    const exit = readJSON(part.exitPath);
    if (!exit || exit.attemptId !== part.attemptId || !Number.isInteger(exit.exitCode)) return null;
    if (exit.provider && exit.provider !== sub.provider) return null;
    if (exit.model && exit.model !== sub.cli) return null;
    const folder = linkedWorkspace(part.folder);
    if (!folder || !safeGitWorkspace(part.projectFolder)) return null;
    const before = part.baseHead || mega.repoBefore?.repoHead;
    const after = spawnSync('git', ['--no-optional-locks', '-C', folder, 'rev-parse', 'HEAD'], { encoding: 'utf8', env: gitEnvironment(), timeout: 4000, windowsHide: true }).stdout.trim();
    const listed = spawnSync('git', ['--no-optional-locks', '-C', folder, 'diff', '--name-only', before + '..' + after], { encoding: 'utf8', env: gitEnvironment(), timeout: 4000, windowsHide: true });
    const names = listed.status === 0 ? listed.stdout.trim().split(/\r?\n/).filter(Boolean) : [];
    return deliveredWithoutVerdict({ folder, before, after, exitCode: exit.exitCode, scope: sub.scope, names });
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
      if (!valid && rerouteRefusal(mega, sub, part)) continue;
      if (!valid) {
        const delivered = deliveryOf(mega, sub, part) || committedSince(mega, part);
        if (delivered) { collect(mega, sub, part, technical(mega, sub, part, 'COMPLETE', delivered.summary, delivered)); continue; }
        if (rerouteNoVerdict(mega, sub, part)) continue;
        collect(mega, sub, part, technical(mega, sub, part, 'FAILED', 'Worker process ended without a valid structured result and exit evidence.'));
        continue;
      }
      collect(mega, sub, part, result);
    }
    row.workerAlive = (row.dispatch || []).some(part => part.alive);
    row.monitoring = { ...row.monitoring, workers: (row.dispatch || []).filter(part => part.pid).length, alive: (row.dispatch || []).filter(part => part.alive).length };
    return subtasks(mega);
  }
  function takeoverText(sub) {
    const note = sub.takeover;
    if (!note) return '';
    const lines = [`HANDOFF: you take over this step from ${note.fromModelId}, which was stopped before finishing.`,
      'Continue from the current state of the project folder. Do not start over: first look at what is already there (git status and the files below), keep what works, then finish the step.'];
    if (note.filesChanged.length) lines.push('Files it changed: ' + note.filesChanged.join(', '));
    if (note.tests.length) lines.push('Tests it reported: ' + note.tests.map(test => typeof test === 'string' ? test : [test.command, test.status].filter(Boolean).join(' ')).join('; '));
    if (note.summary) lines.push('Its last report: ' + note.summary);
    if (note.logTail) lines.push('Its last activity (log tail):\n' + note.logTail);
    return lines.join('\n') + '\n\n';
  }
  function parallelBuilds(mega) {
    if (mega.activeGroup?.length) return subtasks(mega).filter(sub => mega.activeGroup.includes(sub.id));
    const ready = subtasks(mega).filter(sub => sub.status === 'PENDING' && sub.type === 'IMPLEMENTATION' && stepRole(sub) === 'Build'
      && sub.dependencies.every(id => data.subTasks.find(parent => parent.id === id)?.status === 'COMPLETE' && (!(mega.worktreeParts || []).some(part => part.subId === id) || mega.integratedSubIds?.includes(id))));
    return ready.length > 1 ? ready : [];
  }
  // The number of a part's branch (<work branch>--part-N) is unique in the project, not only in its work: a
  // work that continues a stopped one must not take a branch the earlier parts' worktrees still hold (job 45:
  // "'port/45-phase5--part-1' is already used by worktree", 2026-11-25). A branch that exists is skipped; the
  // number stays with the part, so the part finds its worktree again when it runs again.
  function partIndex(folder, mega, sub, workBranch) {
    if (Number.isInteger(sub.worktreeIndex)) return sub.worktreeIndex;
    const taken = data.megaTasks.filter(other => other.projectId === mega.projectId).flatMap(other => (other.worktreeParts || []).map(part => Number(String(part.branch || '').match(/--part-(\d+)$/)?.[1]) || 0))
      .concat(data.subTasks.filter(other => other.megaTaskId && data.megaTasks.some(m => m.id === other.megaTaskId && m.projectId === mega.projectId)).map(other => other.worktreeIndex || 0));
    let index = Math.max(subtasks(mega).indexOf(sub) + 1, ...taken.map(n => n + 1));
    const exists = number => { try { worktrees.git(folder, ['show-ref', '--verify', '--quiet', `refs/heads/${worktrees.branchFor(workBranch, number)}`]); return true; } catch { return false; } };
    for (let tries = 0; tries < 200 && exists(index); tries += 1) index += 1;
    sub.worktreeIndex = index;
    return index;
  }
  function startable(mega) {
    const subs = subtasks(mega);
    return subs.some(sub => sub.status === 'PENDING' && stepRole(sub) === 'Build'
      && sub.dependencies.every(id => data.subTasks.find(parent => parent.id === id)?.status === 'COMPLETE'));
  }
  function allIntegrated(mega) { return !mega.activeGroup?.length && (mega.worktreeParts || []).every(part => mega.integratedSubIds?.includes(part.subId)); }
  function integrateParts(item, mega) {
    const group = mega.activeGroup || [];
    if (!group.length || mega.awaitingIntegration) return;
    if (!group.every(id => subtasks(mega).find(sub => sub.id === id)?.status === 'COMPLETE')) return;
    const parts = (mega.worktreeParts || []).filter(part => group.includes(part.subId));
    if (!parts.length) { mega.activeGroup = null; return; }
    if (parts.length !== group.length) return;
    const result = worktrees.integrate({ folder: item.project.folder, parts });
    if (result.ok) { mega.integratedSubIds = [...new Set([...(mega.integratedSubIds || []), ...group])]; mega.activeGroup = null;
      event(mega.projectId, 'PARTS_INTEGRATED', `Integrated ${parts.length} part branches.`, { megaTaskId: mega.id }); return; }
    const subs = subtasks(mega);
    const existing = subs.find(sub => sub.type === 'INTEGRATION');
    const id = existing?.id || `${mega.id}-ST-${String(subs.length + 1).padStart(2, '0')}`;
    const detail = `Integrate the part branches into the work branch in order: ${parts.map(part => part.branch).join(', ')}. `
      + `The merge of ${result.branch || 'the parts'} stopped. Conflicting files: ${(result.conflicts || []).join(', ') || 'none reported'}. ${result.reason} `
      + 'Resolve the conflict in the project folder, commit the integration, and report the result. Keep every part branch until integration succeeds.';
    if (existing) Object.assign(existing, { detail, status: 'PENDING' });
    else {
      const source = parts.map(part => subs.find(sub => sub.id === part.subId)).at(-1);
      data.subTasks.push({ id, megaTaskId: mega.id, title: 'Integrate parallel parts', detail, status: 'PENDING', assignedModelId: source.assignedModelId,
        attemptIds: [], resultId: null, createdAt: now(), completedAt: null, type: 'INTEGRATION', dependencies: parts.map(part => part.subId),
        proposedProvider: source.proposedProvider, proposedCli: source.proposedCli, provider: null, cli: null });
    }
    for (const sub of subs) if (sub.status === 'PENDING' && sub.id !== id && !sub.dependencies.includes(id)) sub.dependencies.push(id);
    mega.awaitingIntegration = true;
    event(mega.projectId, 'PARTS_CONFLICT', detail, { megaTaskId: mega.id, subTaskId: id });
  }
  function launch(item, job, mega, sub) {
    const row = rowFor(mega.projectId, mega.id);
    const requested = models().find(model => model.id === sub.assignedModelId && model.enabled && model.available !== false);
    if (requested && (requested.provider !== sub.proposedProvider || requested.cli !== sub.proposedCli)) { sub.status = 'BLOCKED'; mega.nextAction = 'Review changed provider/model configuration before continuing.'; return; }
    const worker = options.resolveWorker ? options.resolveWorker(requested, sub) : requested;
    if (worker && worker.id !== sub.assignedModelId) {
      const from = sub.assignedModelId;
      sub.fallbackFrom = from; sub.assignedModelId = worker.id; sub.proposedProvider = worker.provider; sub.proposedCli = worker.cli;
      event(mega.projectId, 'MODEL_USAGE_FALLBACK', 'Switched from ' + from + ' to ' + worker.id + ': the assigned model is out of quota, disabled or no longer has this role.', { megaTaskId: mega.id, subTaskId: sub.id, fromModelId: from, modelId: worker.id });
      persist();
    }
    if (!worker || typeof spawnWorker !== 'function' || options.workerEligible && !options.workerEligible(worker, sub)) {
      // No model of the crew has quota: the step waits and starts by itself when one has. Anything else blocks it with the reason.
      const why = typeof spawnWorker === 'function' && options.explainNoWorker ? options.explainNoWorker(requested, sub) : null;
      if (why?.waiting) { sub.status = 'PENDING'; sub.waitingFor = why.message; mega.nextAction = why.message; return; }
      sub.status = 'BLOCKED'; sub.waitingFor = null;
      mega.nextAction = why?.message || `Enable the exact worker ${sub.assignedModelId}, verify its remaining quota, and resume.`;
      return;
    }
    sub.waitingFor = null;
    if (worker.provider !== sub.proposedProvider || worker.cli !== sub.proposedCli) { sub.status = 'BLOCKED'; mega.nextAction = 'Review changed provider/model configuration and explicitly reassign this slice on Resume.'; return; }
    if (sub.type === 'RESEARCH' && !adapterFor(worker)?.sourceProtectedResearch) { sub.status = 'BLOCKED'; mega.nextAction = 'Source-protected research is not supported by this adapter.'; return; }
    const claim = attempts.claim({ taskId: sub.id, projectId: mega.detached ? 'research:' + mega.id : mega.projectId, modelId: worker.id, candidates: models(), megaTaskId: mega.id, subTaskId: sub.id, research: Boolean(mega.detached) });
    if (!claim.ok) { sub.status = 'BLOCKED'; mega.nextAction = claim.reason; return; }
    const attemptId = claim.attempt.id;
    let folder = sub.type === 'RESEARCH' ? path.join(dir, 'research', mega.id, attemptId) : item.project.folder;
    let worktree = null;
    if (sub.type === 'IMPLEMENTATION' && parallelBuilds(mega).includes(sub) && worktrees.repository(item.project.folder)) {
      try {
        const workBranch = worktrees.git(item.project.folder, ['branch', '--show-current']);
        worktree = worktrees.create({ folder: item.project.folder, dir, projectId: mega.projectId, megaId: mega.id, subId: sub.id,
          index: partIndex(item.project.folder, mega, sub, workBranch), workBranch });
        if (worktree) {
          folder = worktree.folder;
          mega.worktreeParts ||= [];
          if (!mega.worktreeParts.some(part => part.subId === sub.id)) mega.worktreeParts.push({ subId: sub.id, ...worktree });
        }
      } catch (error) {
        const part = { attemptId, exitPath: '', logPath: '', structuredResultPath: '', projectFolder: item.project.folder, folder: item.project.folder };
        collect(mega, sub, part, technical(mega, sub, part, 'FAILED', 'Worktree creation failed: ' + error.message));
        return;
      }
    }
    if (sub.type === 'RESEARCH') fs.mkdirSync(folder, { recursive: true });
    const part = { projectFolder: item.project.folder, modelId: worker.id, label: worker.label, provider: worker.provider, cli: worker.cli, subTaskId: sub.id, megaTaskId: mega.id, attemptId, slice: sub.title,
      resultPath: path.join(dir, 'results', attemptId + '.txt'), structuredResultPath: path.join(dir, 'results', attemptId + '.json'), exitPath: path.join(dir, 'results', attemptId + '.exit.json'),
      logPath: path.join(dir, 'logs', attemptId + '.log'), folder, ...(worktree ? { branch: worktree.branch, baseHead: worktrees.git(folder, ['rev-parse', 'HEAD']) } : {}), pid: null, alive: false, startedAt: now(), sessionId: options.sessionId };
    if (sub.type === 'RESEARCH') Object.assign(part, { resultPath: path.join(folder, 'result.txt'), structuredResultPath: path.join(folder, 'result.json'), exitPath: path.join(folder, 'exit.json') });
    fs.mkdirSync(path.dirname(part.resultPath), { recursive: true }); fs.writeFileSync(part.resultPath, 'STATUS: WATCHING\n');
    attempts.attachResult({ attemptId, resultPath: part.structuredResultPath });
    // The mode in force when the step starts stays with that step. A later change waits for the next one.
    if (typeof options.freezeSavingMode === 'function') options.freezeSavingMode(mega.projectId, sub);
    sub.attemptIds.push(attemptId); sub.status = 'RUNNING'; sub.provider = worker.provider; sub.cli = worker.cli;
    row.dispatch ||= []; row.dispatch = row.dispatch.filter(existing => existing.subTaskId !== sub.id); row.dispatch.push(part);
    row.attemptId = attemptId; row.modelId = worker.id; row.resultPath = part.resultPath;
    // Persist identity and input ownership before any worker process is launched.
    persist();
    try {
      const handoff = { megaTask: mega, subTasks: subtasks(mega), results: data.results.filter(result => result.megaTaskId === mega.id), checkpoint: data.checkpoints.find(cp => cp.id === mega.currentCheckpointId), notes: (item.home?.notes || []).filter(note => note.aiRelevant === true) };
      const launched = spawnWorker({ ...part, linkedFolder: item.project.folder, projectId: mega.projectId, worker, ...identity(mega, sub, attemptId), job: { title: sub.title, kind: sub.type === 'RESEARCH' ? 'research' : sub.type === 'AUDIT' ? 'audit' : job.kind,
        detail: `${takeoverText(sub)}${job.detail}\n\nSubTask: ${sub.detail}${scopeText(sub, Boolean(worktree))}\n\n${sub.type === 'RESEARCH' ? `Source project (read only for research): ${item.project.folder}` : `Project folder (work here): ${folder}`}\n\nDurable handoff:\n${JSON.stringify(handoff)}` } });
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
    // Cancelled work is history: it never becomes, or stays, the project's current work.
    if (mega.status === 'CANCELLED') {
      if (!row.megaTaskId || row.megaTaskId === mega.id) Object.assign(row, { jobId: null, megaTaskId: null, megaTask: null, subTasks: [], dispatch: [], status: 'idle' });
      return;
    }
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
    if (mega.worktreeParts?.length) {
      try { worktrees.cleanup({ folder: item.project.folder, dir, projectId: mega.projectId, megaId: mega.id, parts: mega.worktreeParts }); }
      catch (error) { mega.nextAction = `Completed; worktree cleanup needs attention: ${error.message}`; }
    }
    event(mega.projectId, 'MEGATASK_COMPLETED', `Completed ${mega.title}. ${mega.repoAfter.disposition}.`, { megaTaskId: mega.id });
    try {
      for (const s of (data.subTasks || []).filter(sub => sub.megaTaskId === mega.id && sub.status === 'COMPLETE')) {
        if (mega.recordedOutcomeSubIds?.includes(s.id)) continue;
        mega.recordedOutcomeSubIds = [...(mega.recordedOutcomeSubIds || []), s.id];
        const sRes = data.results.filter(r => r.subTaskId === s.id && r.status === 'COMPLETE').at(-1) || data.results.filter(r => r.subTaskId === s.id).at(-1);
        const sPart = (rowFor(mega.projectId, mega.id).dispatch || []).filter(p => p.subTaskId === s.id).at(-1);
        let sTokens = null;
        if (sPart?.structuredResultPath) {
          try { sTokens = JSON.parse(fs.readFileSync(sPart.structuredResultPath + '.usage.json', 'utf8')); } catch {}
        }
        appendStepOutcome(dir, {
          model: s.assignedModelId, role: stepRole(s), size: s.size, kind: s.type || mega.kind,
          accepted: true, fixed: 0,
          tests: sRes?.tests, testsReported: sRes?.testsReported, testsPassed: sRes?.testsPassed,
          switch: false, refusal: null,
          startedAt: sPart?.startedAt, endedAt: s.finishedAt, quota: sTokens
        });
      }
    } catch { /* safe */ }
    try {
      const summary = data.results.filter(result => result.megaTaskId === mega.id && result.status === 'COMPLETE').map(result => result.summary).filter(Boolean).slice(-3).join(' ') || mega.title;
      planFile.finish(item.project.folder, { summary, pushed: planFile.pushed(item.project.folder) });
    } catch { /* the mega task is already complete */ }
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
    const mega = propose(item, job, parts);
    let subs = reconcile(mega);
    if (mega.status === 'RUNNING') {
      if (deadlineDue(mega)) {
        if (subs.every(sub => sub.status === 'COMPLETE') && allIntegrated(mega)) complete(item, mega);
        // Read-only projections reconcile evidence but never terminate processes.
        else if (schedule) enforceDeadline(item, job, mega);
      } else if (subs.some(sub => sub.status === 'FAILED')) {
        mega.pauseRequested = true; mega.nextAction = 'Review failed results and resume only the remaining work.';
        if (!subs.some(sub => sub.status === 'RUNNING')) { checkpoint(item, job, mega, 'worker failure'); mega.status = 'FAILED'; }
      } else if (mega.pauseRequested) {
        if (subs.every(sub => sub.status === 'COMPLETE') && allIntegrated(mega)) complete(item, mega);
        else if (!subs.some(sub => sub.status === 'RUNNING')) checkpoint(item, job, mega, 'pause after current tasks');
      } else if (mega.authorizedSessionId !== options.sessionId && !subs.some(sub => sub.status === 'RUNNING') && subs.some(sub => sub.status !== 'COMPLETE')) {
        mega.nextAction = 'Resume the remaining work explicitly after backend restart.';
        checkpoint(item, job, mega, 'backend restarted');
      } else {
        if (schedule && !subs.some(sub => sub.status === 'FAILED')) integrateParts(item, mega);
        subs = subtasks(mega);
        if (schedule && !mega.activeGroup?.length) {
          const group = parallelBuilds(mega);
          if (group.length) mega.activeGroup = group.map(sub => sub.id);
        }
        for (const sub of schedule ? subs.filter(sub => sub.status === 'PENDING') : []) {
          if (mega.activeGroup?.length && !mega.activeGroup.includes(sub.id) && sub.type !== 'INTEGRATION') continue;
          if (sub.type !== 'INTEGRATION' && sub.dependencies.some(id => (mega.worktreeParts || []).some(part => part.subId === id) && !mega.integratedSubIds?.includes(id))) continue;
          if (stepRole(sub) === 'Verify' && subs.some(other => other.id !== sub.id && stepRole(other) === 'Build' && other.status !== 'COMPLETE')) continue;
          if (deadlineDue(mega)) { enforceDeadline(item, job, mega); break; }
          if (subs.filter(sub => sub.status === 'RUNNING').length >= MAX_PARALLEL) break;
          if (sub.dependencies.every(id => data.subTasks.find(parent => parent.id === id)?.status === 'COMPLETE')) launch(item, job, mega, sub);
        }
        reconcile(mega);
        if (mega.status === 'RUNNING' && !deadlineDue(mega)) {
          if (subs.every(sub => sub.status === 'COMPLETE') && allIntegrated(mega)) complete(item, mega);
          // A blocked part does not abandon the parts that do not need it: the work stops only when nothing
          // else can start (the next round launches them; the scheduler starts at most ten at a time).
          else if (subs.some(sub => sub.status === 'BLOCKED') && !subs.some(sub => sub.status === 'RUNNING') && !startable(mega)) checkpoint(item, job, mega, 'blocked worker');
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
    // Each stop of a work saves a checkpoint; Resume without a named one continues from the latest of each
    // stopped work (job 45 was stopped by the owner, then blocked: two checkpoints, and Resume was refused).
    const all = data.checkpoints.filter(cp => cp.projectId === item.project.id && ['PAUSED', 'FAILED'].includes(data.megaTasks.find(mega => mega.id === cp.megaTaskId)?.status));
    const latest = megaId => { const mega = data.megaTasks.find(item => item.id === megaId); const own = all.filter(cp => cp.megaTaskId === megaId); return own.find(cp => cp.id === mega?.currentCheckpointId) || own.at(-1); };
    const choices = request.checkpointId ? all : [...new Set(all.map(cp => cp.megaTaskId))].map(latest);
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
  function dropWork(item, megaTaskId) {
    const mega = target(item.project.id, megaTaskId);
    if (subtasks(mega).some(sub => sub.status === 'RUNNING')) throw new Error('Stop every worker before dropping its worktrees.');
    if (mega.worktreeParts?.length) worktrees.cleanup({ folder: item.project.folder, dir, projectId: mega.projectId, megaId: mega.id, parts: mega.worktreeParts, discardBranches: true });
    return mega;
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
  // The lead may reassign the slices of a proposed MegaTask before approval.
  // The lead's own split replaces the proposed steps, before anything has started: the parts it named,
  // each with the files it may change, then one check of the whole work.
  function replan(projectId, megaTaskId, parts, why = '') {
    const mega = target(projectId, megaTaskId);
    if (mega.status !== 'READY_FOR_APPROVAL') throw new Error('Only a proposal that has not started can be split again.');
    const old = data.subTasks.filter(sub => sub.megaTaskId === mega.id);
    if (old.some(sub => sub.status !== 'PENDING' || sub.attemptIds?.length)) throw new Error('A step of this proposal already ran.');
    const ids = parts.map((_, index) => `${mega.id}-ST-${String(index + 1).padStart(2, '0')}`);
    const type = old[0]?.type || 'IMPLEMENTATION';
    const subs = parts.map((part, index) => {
      const model = models().find(item => item.id === part.modelId);
      if (!model) throw new Error('Model not present: ' + part.modelId);
      return { id: ids[index], megaTaskId: mega.id, title: part.slice, detail: part.detail || part.slice, status: 'PENDING', assignedModelId: model.id,
        attemptIds: [], resultId: null, createdAt: now(), completedAt: null, type: part.type || type,
        dependencies: (part.dependsOn || []).map(parent => { if (!Number.isInteger(parent) || parent < 0 || parent >= index) throw new Error('Dependencies must reference an earlier SubTask in the same MegaTask.'); return ids[parent]; }),
        proposedProvider: model.provider || null, proposedCli: model.cli || null, provider: null, cli: null,
        ...(Array.isArray(part.scope) && part.scope.length ? { scope: [...part.scope] } : {}), ...(part.size ? { size: part.size } : {}) };
    });
    data.subTasks = data.subTasks.filter(sub => sub.megaTaskId !== mega.id).concat(subs);
    mega.blockers = proposalBlockers(mega);
    event(projectId, 'MEGATASK_PROPOSED', splitSummary(subs, id => models().find(item => item.id === id)?.label || id, why), { megaTaskId: mega.id });
    persist();
    return paint(mega);
  }

  function reassign(projectId, megaTaskId, modelIds, why = '') {
    const mega = target(projectId, megaTaskId);
    if (mega.status !== 'READY_FOR_APPROVAL') throw new Error('Only a proposed MegaTask can be reassigned.');
    const subs = subtasks(mega);
    if (!Array.isArray(modelIds) || modelIds.length !== subs.length) throw new Error('One model per slice is required.');
    const chosen = modelIds.map((id, index) => {
      const model = models().find(item => item.id === id && item.enabled && item.available !== false);
      if (!model || options.workerEligible && !options.workerEligible(model, subs[index])) throw new Error(`Model ${id} cannot take slice ${index + 1}.`);
      return model;
    });
    if (subs.every((sub, index) => sub.assignedModelId === chosen[index].id)) return mega;
    subs.forEach((sub, index) => Object.assign(sub, { assignedModelId: chosen[index].id, proposedProvider: chosen[index].provider, proposedCli: chosen[index].cli }));
    mega.blockers = proposalBlockers(mega);
    // Short and readable: the role of each step and its model, then the lead's reason in full.
    event(projectId, 'MEGATASK_ASSIGNED', `Lead assigned ${subs.map((sub, index) => `${stepRole(sub)}: ${chosen[index].label || chosen[index].id}`).join(' · ')}.${why ? ' Why: ' + why : ''}`, { megaTaskId });
    paint(mega); persist();
    return mega;
  }
  // The lead may change the models of slices not yet done, in proposed or stopped work, within the user's roles.
  // The last lines of a worker log, small enough to hand to the next model.
  function logTail(file, max = 3000) {
    try { const text = fs.readFileSync(file, 'utf8'); return text.slice(-max).split('\n').slice(1).join('\n').trim(); } catch { return ''; }
  }
  // by: who changed the crew, as the chat says it: the lead, the user, or Planwharf's automatic switch.
  function reassignRemaining(projectId, megaTaskId, assignments, why = '', by = 'lead') {
    const mega = target(projectId, megaTaskId);
    reconcile(mega);
    if (['COMPLETE', 'CANCELLED'].includes(mega.status)) throw new Error(`This work is ${mega.status.toLowerCase()}: start new work instead.`);
    if (!assignments || typeof assignments !== 'object' || Array.isArray(assignments) || !Object.keys(assignments).length) throw new Error('Name at least one step and its model.');
    const changes = Object.entries(assignments).map(([id, modelId]) => {
      const sub = subtasks(mega).find(item => item.id === id && item.status !== 'COMPLETE');
      if (!sub) throw new Error(`Step ${id} is not a remaining step of this work.`);
      const model = models().find(item => item.id === modelId && item.enabled && item.available !== false);
      if (!model || options.workerEligible && !options.workerEligible(model, sub)) throw new Error(`Model ${modelId} cannot take "${sub.title}": it must be enabled, have this role in Team and have quota.`);
      return [sub, model];
    });
    // A step that is working stops its worker first; it then restarts with the new model. Files it wrote stay.
    for (const [sub, model] of changes.filter(([sub]) => sub.status === 'RUNNING')) {
      const part = rowFor(mega.projectId, mega.id).dispatch.find(item => item.subTaskId === sub.id && item.alive);
      if (!part) continue;
      if (!killWorker?.(part.pid) || isProcessAlive?.(part.pid)) throw new Error(`Could not stop the worker of "${sub.title}"; its model was not changed.`);
      part.alive = false; part.observedAlive = false;
      const partial = readJSON(part.structuredResultPath);
      collect(mega, sub, part, { ...technical(mega, sub, part, 'BLOCKED', `Model changed to ${model.label || model.id}; the step restarts with it.`, validateResult(partial, identity(mega, sub, part.attemptId)) ? partial : null), modelChange: true });
      mega.interruptedAttemptIds = [...(mega.interruptedAttemptIds || []), part.attemptId];
    }
    for (const [sub, model] of changes) {
      // A step that already worked hands over to the new model: who worked on it, what it changed, its last activity.
      const last = (rowFor(mega.projectId, mega.id).dispatch || []).find(item => item.subTaskId === sub.id);
      if (sub.attemptIds?.length && sub.assignedModelId !== model.id) {
        const result = data.results.filter(item => item.subTaskId === sub.id).at(-1);
        sub.takeover = { fromModelId: sub.assignedModelId, at: now(), filesChanged: result?.filesChanged || [], tests: result?.tests || [], summary: result?.summary || '', logTail: last?.logPath ? logTail(last.logPath) : '' };
      }
      Object.assign(sub, { assignedModelId: model.id, proposedProvider: model.provider, proposedCli: model.cli, ...(['BLOCKED', 'RUNNING'].includes(sub.status) ? { status: 'PENDING', resultId: null } : {}) });
    }
    if (mega.status === 'READY_FOR_APPROVAL') mega.blockers = proposalBlockers(mega);
    const who = by === 'user' ? 'You assigned' : by === 'auto' ? 'Planwharf switched' : 'Lead assigned';
    event(projectId, 'MEGATASK_ASSIGNED', `${who} ${changes.map(([sub, model]) => `${stepRole(sub)}: ${model.label || model.id}`).join(' · ')}.${why ? ' Why: ' + why : ''}`, { megaTaskId });
    paint(mega); persist();
    return mega;
  }
  function appendAddition(item, job, megaTaskId, spec = {}) {
    const mega = target(item.project.id, megaTaskId);
    if (mega.status === 'COMPLETE') {
      throw new Error('Work is already complete. Start a new goal instead.');
    }
    reconcile(mega);
    let subs = subtasks(mega);
    const verifySub = subs.find(sub => stepRole(sub) === 'Verify');

    let scope = Array.isArray(spec.files) && spec.files.length ? spec.files.map(slash) : null;
    if (!scope && typeof spec.addition === 'string') {
      const matches = [...spec.addition.matchAll(/`([^`\r\n]+\.[a-zA-Z0-9]+)`/g)].map(m => m[1].trim());
      if (matches.length) scope = matches.map(slash);
    }
    if (!scope && Array.isArray(spec.scope) && spec.scope.length) scope = spec.scope.map(slash);

    const dependencies = [];
    const incompleteBuilds = subs.filter(sub => stepRole(sub) === 'Build' && sub.status !== 'COMPLETE');
    if (scope && scope.length) {
      for (const other of incompleteBuilds) {
        const otherScope = Array.isArray(other.scope) && other.scope.length ? other.scope : null;
        if (!otherScope || otherScope.some(x => scope.some(y => x === y || y.startsWith(x + '/') || x.startsWith(y + '/')))) {
          dependencies.push(other.id);
        }
      }
    } else {
      for (const other of incompleteBuilds) dependencies.push(other.id);
    }

    let worker = null;
    if (spec.modelId) {
      const candidate = models().find(m => m.id === spec.modelId);
      if (candidate && (!options.workerEligible || options.workerEligible(candidate, { type: 'IMPLEMENTATION' }))) {
        worker = candidate;
      }
    }
    if (!worker && typeof options.replacement === 'function') {
      worker = options.replacement({ type: 'IMPLEMENTATION', title: spec.title || spec.addition }, []);
    }
    if (!worker) {
      worker = models().find(m => m.roles?.worker && (!options.workerEligible || options.workerEligible(m, { type: 'IMPLEMENTATION' })));
    }

    const assignedModelId = worker?.id || spec.modelId || 'unassigned';
    const proposedProvider = worker?.provider || null;
    const proposedCli = worker?.cli || null;

    const newIndex = subs.length + 1;
    const id = `${mega.id}-ST-${String(newIndex).padStart(2, '0')}`;
    const rawTitle = spec.title || String(spec.addition || '').split(/\r?\n/)[0].replace(/^Addition to this work:\s*/i, '').trim();
    const title = rawTitle.slice(0, 120) || 'Addition to work';
    const detail = spec.addition || spec.detail || title;

    const newSub = {
      id,
      megaTaskId: mega.id,
      title,
      detail,
      status: 'PENDING',
      assignedModelId,
      attemptIds: [],
      resultId: null,
      createdAt: now(),
      completedAt: null,
      type: 'IMPLEMENTATION',
      dependencies: [...new Set(dependencies)],
      proposedProvider,
      proposedCli,
      provider: null,
      cli: null,
      ...(scope ? { scope } : {})
    };

    if (verifySub) {
      const verifyIdx = data.subTasks.findIndex(s => s.id === verifySub.id);
      if (verifyIdx >= 0) data.subTasks.splice(verifyIdx, 0, newSub);
      else data.subTasks.push(newSub);

      if (!verifySub.dependencies.includes(newSub.id)) {
        verifySub.dependencies.push(newSub.id);
      }
      if (verifySub.status === 'RUNNING') {
        const row = rowFor(mega.projectId, mega.id);
        const part = (row.dispatch || []).find(p => p.subTaskId === verifySub.id && p.alive);
        if (part) {
          if (typeof killWorker === 'function') {
            try { killWorker(part.pid); } catch (_) {}
          }
          part.alive = false;
          part.observedAlive = false;
          attempts.closeAttempt({ attemptId: part.attemptId, reason: 'interrupted by addition' });
        }
        verifySub.status = 'PENDING';
        verifySub.resultId = null;
        verifySub.completedAt = null;
      } else if (verifySub.status === 'COMPLETE' || verifySub.status === 'BLOCKED' || verifySub.status === 'FAILED') {
        verifySub.status = 'PENDING';
        verifySub.resultId = null;
        verifySub.completedAt = null;
      }
    } else {
      data.subTasks.push(newSub);
    }

    if (mega.status === 'PAUSED') {
      const latestCp = data.checkpoints.filter(cp => cp.megaTaskId === mega.id).at(-1);
      if (latestCp) mega.currentCheckpointId = latestCp.id;
      for (const sub of subtasks(mega)) if (sub.status !== 'COMPLETE') {
        sub.status = 'PENDING';
        sub.resultId = null;
      }
      mega.status = 'RUNNING';
      mega.pauseRequested = false;
      mega.interruptedAttemptIds = [];
      mega.nextAction = 'Resume with the new addition step.';
      event(mega.projectId, 'MEGATASK_RESUMED', `Resumed ${mega.title} with addition.`, { megaTaskId: mega.id });
    }

    event(mega.projectId, 'SUBTASK_ADDED', `Added to this work: ${title} (${worker?.label || worker?.id || assignedModelId}).`, { megaTaskId: mega.id, subTaskId: newSub.id });

    const folder = item?.project?.folder;
    if (folder) {
      try {
        const prev = planFile.parse(planFile.read(folder));
        const allSubs = subtasks(mega);
        const steps = allSubs.map(sub => ({
          role: stepRole(sub),
          model: models().find(m => m.id === sub.assignedModelId)?.label || sub.assignedModelId || 'unassigned',
          title: sub.title,
          done: sub.status === 'COMPLETE',
          reset: stepRole(sub) === 'Verify' && sub.status !== 'COMPLETE',
          commit: sub.status === 'COMPLETE' ? (data.results.find(r => r.subTaskId === sub.id)?.summary || 'committed') : ''
        }));
        planFile.writePlan(folder, {
          goal: prev.goal || mega.goal,
          base: prev.base,
          branch: prev.branch || planFile.currentBranch(folder),
          steps
        });
      } catch (_) {}
    }

    paint(mega);
    persist();

    if (mega.status === 'RUNNING') {
      advance(item, job, [], true);
    }

    return { ok: true, subTask: newSub, megaTask: mega };
  }
  function answerDecision(projectId, decisionId, answer, who = 'owner') {
    const decision = data.decisions.find(item => item && item.projectId === projectId && item.id === decisionId);
    if (!decision) return { ok: false, reason: 'Decision not found.' };
    const subs = subtasks({ id: decision.megaTaskId });
    if (decision.answer && subs.some(s => s && (s.status === 'RUNNING' || s.status === 'COMPLETE'))) {
      return { ok: false, reason: 'The step that needs this decision has already started.' };
    }
    const val = String(answer || '').trim();
    decision.answer = (val === 'accepted' && decision.suggestion) ? decision.suggestion : (val || 'yes');
    decision.answeredBy = who === 'owner' ? 'owner' : who;
    decision.answeredAt = now();
    if (typeof persist === 'function') persist();
    return { ok: true, decision };
  }
  function delegateDecision(projectId, decisionId, leadLabel = 'Lead') {
    const decision = data.decisions.find(item => item && item.projectId === projectId && item.id === decisionId);
    if (!decision) return { ok: false, reason: 'Decision not found.' };
    decision.answer = String(decision.suggestion || 'yes').trim();
    decision.answeredBy = `Decided by ${leadLabel}`;
    decision.autoReason = decision.suggestion ? `Followed recommended suggestion: ${decision.suggestion}` : 'Safe default accepted.';
    decision.answeredAt = now();
    if (typeof persist === 'function') persist();
    return { ok: true, decision };
  }
  return { replan, propose, approve, advance, appendAddition, stop, stopWorker, resume, view, find, target, observe, event, reconcile, paint, checkpointForUpdate, restore, reassign, reassignRemaining, dropWork, answerDecision, delegateDecision };
}
function stepTimesFile(dir) { return path.join(dir, 'step-times.jsonl'); }
function readStepTimes(dir) {
  if (!dir) return [];
  try { return timeLeft.parseHistory(fs.readFileSync(stepTimesFile(dir), 'utf8')); }
  catch { return []; }
}
function appendStepTime(dir, record) {
  if (!dir || !record) return;
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(stepTimesFile(dir), timeLeft.historyText(timeLeft.remember(readStepTimes(dir), record)));
}
function stepOutcomesFile(dir) { return stepOutcomes.outcomesFile(dir); }
function readStepOutcomes(dir) { return stepOutcomes.readStepOutcomes(dir); }
function appendStepOutcome(dir, record) { return stepOutcomes.appendStepOutcome(dir, record); }
// The line that announces the lead's split. The last step is the check of the whole when it waits for
// every other step; the rest is built, with or without its own files.
function splitSummary(subs, label, why = '') {
  const last = subs.at(-1);
  const checked = subs.length > 1 && subs.slice(0, -1).every(sub => (last.dependencies || []).includes(sub.id));
  const built = subs.length - (checked ? 1 : 0);
  const shape = built === 1 ? 'Lead kept the work as one build step' : `Lead split the work into ${built} parts`;
  return `${shape}${checked ? ' and a check of the whole' : ''}: ${subs.map(sub => `${sub.title} (${label(sub.assignedModelId)})`).join(' · ')}.${why ? ' Why: ' + why : ''}`;
}
function noVerdictPlan(tries) { return tries >= 2 ? 'stop' : tries === 1 ? 'switch' : 'retry-same'; }
module.exports = { createExecutionBridge, splitSummary, repoState, readStepTimes, appendStepTime, readStepOutcomes, appendStepOutcome, stepOutcomesFile, deliveredWithoutVerdict, noVerdictPlan };
