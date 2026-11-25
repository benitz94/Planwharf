'use strict';
const fs = require('node:fs/promises');
const crypto = require('node:crypto');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { activityMessages, workerReport } = require('./activity.cjs');
const { summarizeTimeline, checkpointDiff } = require('./project-history.cjs');
const { repoState } = require('./execution.cjs');
const { homeNext } = require('./execution-next.cjs');
const { linkedWorkspace, safeGitWorkspace, gitEnvironment } = require('./project-workspace.cjs');
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
function git(folder, args) {
  return new Promise(resolve => execFile('git', ['--no-optional-locks', '-C', folder, ...args], { windowsHide: true, env: gitEnvironment(process.env), timeout: 5000, maxBuffer: 8 * 1024 * 1024 }, (error, stdout) => resolve(error ? null : args.includes('-z') ? stdout : stdout.trim())));
}
async function containedParent(root, file) {
  try {
    const parent = await fs.realpath(path.dirname(file));
    const relative = path.relative(root, parent);
    return relative === '' || relative !== '..' && !relative.startsWith('..' + path.sep) && !path.isAbsolute(relative);
  } catch { return false; }
}
async function localGit(folder, { planFile } = {}) {
  const linked = linkedWorkspace(folder);
  if (!linked) return { readable: false, reason: 'No local folder linked.' };
  const root = safeGitWorkspace(linked);
  if (!root) return { readable: false, reason: 'Contained local Git history unavailable.' };
  const gitRoot = await git(root, ['rev-parse', '--show-toplevel']);
  if (!gitRoot) return { readable: false, reason: 'Local Git history unavailable.' };
  // Both sides compared as resolved paths: a folder reached through a link (macOS /var is /private/var, a
  // mapped drive on Windows) is the same folder.
  const resolved = file => {
    const full = path.resolve(file), rest = [];
    for (let dir = full; dir && dir !== path.dirname(dir); dir = path.dirname(dir)) { const real = linkedWorkspace(dir); if (real) return path.join(real, ...rest.reverse()); rest.push(path.basename(dir)); }
    return full;
  };
  const relative = planFile ? path.relative(root, resolved(planFile)).replace(/\\/g, '/') : null;
  const scope = '.';
  const prefix = path.relative(linkedWorkspace(gitRoot) || gitRoot, root).replace(/\\/g, '/');
  const localName = name => prefix ? name.startsWith(prefix + '/') ? name.slice(prefix.length + 1) : null : name;
  // Every Git command runs at the canonical linked folder with an explicit scope.
  const files = ['--', '.'];
  if (relative && !relative.startsWith('../') && !path.isAbsolute(relative)) files.push(`:(exclude,literal)${relative}`);
  // Submodule worktrees may redirect to foreign Git metadata; inspect only this repository.
  const [head, status, diff] = await Promise.all([git(root, ['rev-parse', '--verify', 'HEAD']), git(root, ['status', '--ignore-submodules=all', '--porcelain=v1', '--no-renames', '-uall', ...files]), git(root, ['diff', '--ignore-submodules=all', '--submodule=short', '--relative', '--no-renames', '--no-ext-diff', '--no-textconv', 'HEAD', ...files])]);
  // Include untracked contents so edits to an already-dirty file are visible.
  const untracked = await git(root, ['ls-files', '--others', '--exclude-standard', '-z', ...files]);
  let fingerprint = null;
  if (status !== null && diff !== null && untracked !== null) {
    try {
      const contents = [];
      let bytes = 0;
      if (untracked.split('\0').length > 2000) throw new Error('Too many files for reliable comparison');
      for (const name of untracked.split('\0').filter(Boolean)) {
        const file = path.resolve(root, name);
        if (!file.startsWith(root + path.sep) || !await containedParent(root, file)) throw new Error('Outside linked folder');
        const stat = await fs.lstat(file);
        bytes += stat.size;
        if (bytes > 8 * 1024 * 1024) throw new Error('Files too large for reliable comparison');
        contents.push([name, hash(stat.isSymbolicLink() ? await fs.readlink(file) : await fs.readFile(file))]);
      }
      fingerprint = hash(JSON.stringify([status, diff, contents]));
    } catch { /* Unknown is preferable to a fabricated comparison. */ }
  }
  const paths = await git(root, ['status', '--ignore-submodules=all', '--porcelain=v1', '--no-renames', '-z', '-uall', ...files]);
  const changed = [];
  const records = (paths || '').split('\0');
  let detailBytes = 0;
  for (let index = 0; index < records.length; index++) {
    const record = records[index];
    if (!record) continue;
    const statusCode = record.slice(0, 2), name = localName(record.slice(3));
    const from = /R|C/.test(statusCode) ? localName(records[++index]) : null;
    if (!name) continue;
    let fileFingerprint = null;
    try {
      const target = path.resolve(root, name);
      if (!target.startsWith(root + path.sep) || !await containedParent(root, target)) throw new Error('Outside linked folder');
      const stat = await fs.lstat(target);
      detailBytes += stat.size;
      if (detailBytes <= 8 * 1024 * 1024) fileFingerprint = hash(stat.isSymbolicLink() ? await fs.readlink(target) : await fs.readFile(target));
    } catch { if (/D/.test(statusCode)) fileFingerprint = 'deleted'; }
    changed.push({ path: name, from, status: statusCode.trim(), fingerprint: fileFingerprint });
  }
  return { readable: status !== null, root, scope, head, changedFiles: status === null ? null : status.split(/\r?\n/).filter(Boolean).length, fingerprint, files: changed };
}
async function recentActions(row) {
  const messages = [], reports = [];
  for (const part of row.dispatch || []) {
    if (!part.logPath) continue;
    try {
      const file = await fs.open(part.logPath, 'r');
      let text;
      try {
        const stat = await file.stat(), bytes = Math.min(stat.size, 1024 * 1024), buffer = Buffer.alloc(bytes);
        await file.read(buffer, 0, bytes, stat.size - bytes);
        text = buffer.toString('utf8');
      } finally { await file.close(); }
      for (const action of activityMessages(text).slice(-12)) messages.push({ worker: part.label || part.modelId, text: action });
      const report = workerReport(text);
      if (report) reports.push({ worker: part.label || part.modelId, text: report });
    } catch {}
  }
  if (!messages.length) for (const text of activityMessages(row.workerTail || '').slice(-12)) messages.push({ worker: row.workerLabel || 'Worker', text });
  return { actions: messages, reports };
}
async function execution(row, researches = []) {
  if (!row) return null;
  let result = null;
  if (row.resultPath) try {
    const stat = await fs.stat(row.resultPath);
    if (stat.isFile() && stat.size > 0 && stat.size <= 8 * 1024 * 1024) result = hash(Buffer.concat([Buffer.from(row.resultPath + '\0'), await fs.readFile(row.resultPath)]));
  } catch { /* An assigned output path is not evidence of an execution result. */ }
  return { status: row.status, jobId: row.jobId || null, jobTitle: row.jobTitle || '', activity: row.workerActivity || '', ...await recentActions(row), attemptId: row.attemptId || null, checkpoint: row.checkpoint ? hash(JSON.stringify(row.checkpoint)) : null, result,
    research: researches.map(r => ({ id: r.id, status: r.status, attemptId: r.attemptId || null, reportAvailable: Boolean(r.reportAvailable) })).sort((a, b) => a.id.localeCompare(b.id)) };
}
async function compare(previous, current) {
  if (!previous) return { firstVisit: true, changes: [], unavailable: [], details: [] };
  const changes = [], unavailable = [], details = [];
  const revisions = current.revisionIds.filter(id => !previous.revisionIds.includes(id)).length;
  if (revisions) changes.push(`${revisions} new Living Plan revision${revisions === 1 ? '' : 's'}`);
  const a = previous.git, b = current.git;
  if (a?.head && b?.head && a.root === b.root && a.scope === b.scope && a.head !== b.head) {
    const ancestor = await git(b.root, ['merge-base', '--is-ancestor', a.head, b.head]);
    const count = ancestor !== null ? await git(b.root, ['rev-list', '--count', `${a.head}..${b.head}`, '--', b.scope || '.']) : null;
    if (count && /^\d+$/.test(count)) {
      if (Number(count) > 0) {
        changes.push(`${count} commit${count === '1' ? '' : 's'} added`);
        const log = await git(b.root, ['log', '-100', '--format=%h%x09%s', `${a.head}..${b.head}`, '--', b.scope || '.']);
        for (const line of (log || '').split('\n').filter(Boolean)) { const split = line.indexOf('\t'); details.push(`Commit ${line.slice(0, split)}: ${line.slice(split + 1)}`); }
        if (Number(count) > 100) details.push('Showing the latest 100 commit subjects.');
      }
    }
    else unavailable.push('Git history changed; a reliable commit count is unavailable.');
  } else if (a?.head !== b?.head || a?.root !== b?.root || a?.scope !== b?.scope) unavailable.push('No comparable Git baseline.');
  if (a?.fingerprint && b?.fingerprint && a.root === b.root && a.scope === b.scope) {
    if (a.fingerprint !== b.fingerprint) changes.push(b.changedFiles ? `${b.changedFiles} file${b.changedFiles === 1 ? '' : 's'} changed locally` : 'Local changes cleared');
  } else if (a?.readable || b?.readable) unavailable.push('Local file comparison unavailable.');
  for (const file of b?.files || []) {
    const before = (a?.files || []).find(item => item.path === file.path);
    if (!before || before.status !== file.status || file.fingerprint && before.fingerprint !== file.fingerprint) {
      const verb = file.status === '??' || /A/.test(file.status) ? 'Added locally' : /D/.test(file.status) ? 'Deleted locally' : /R/.test(file.status) ? 'Renamed locally' : 'Edited locally';
      details.push(`${verb}: ${file.path}${file.from ? ` (from ${file.from})` : ''}`);
    }
  }
  const latest = current.revisions?.at(-1), beforeRevision = previous.revisions?.at(-1);
  if (latest && beforeRevision && latest.id !== beforeRevision.id) {
    details.push(`Living Plan R${latest.id}: ${latest.title}`);
    if (latest.state !== beforeRevision.state) details.push(`Recorded progress: ${latest.state}`);
    if (latest.direction !== beforeRevision.direction) details.push(`Updated goal: ${latest.direction}`);
    for (const decision of latest.closed || []) if (!(beforeRevision.closed || []).includes(decision)) details.push(`Decision recorded: ${decision}`);
  }
  const old = previous.execution, now = current.execution;
  if (now && old) {
    if (old.status !== now.status || old.jobId !== now.jobId || old.attemptId !== now.attemptId) changes.push(`Project work ${now.status === 'complete' ? 'completed' : 'changed'}`);
    if (now.checkpoint && old.checkpoint !== now.checkpoint) changes.push('New checkpoint saved');
    if (now.result && old.result !== now.result) changes.push(old.result ? 'Execution result updated' : 'New execution result available');
    const oldActions = new Set((old.actions || []).map(action => `${action.worker}: ${action.text}`));
    const newActions = (now.actions || []).filter(action => !oldActions.has(`${action.worker}: ${action.text}`));
    if (newActions.length) changes.push('New worker activity recorded');
    if (old.status !== now.status || old.jobId !== now.jobId || old.result !== now.result || newActions.length) {
      if (now.jobTitle) details.push(`Work item: ${now.jobTitle} (${now.status})`);
      for (const action of newActions) details.push(`${action.worker}: ${action.text}`);
    }
    for (const research of now.research || []) {
      const before = (old.research || []).find(r => r.id === research.id);
      if (!before || before.status !== research.status || before.attemptId !== research.attemptId || before.reportAvailable !== research.reportAvailable) changes.push(`Research ${research.status === 'complete' ? 'completed' : 'changed'}`);
    }
  } else if (now && !old) unavailable.push('No comparable execution baseline.');
  const notes = (current.noteIds || []).filter(id => !(previous.noteIds || []).includes(id)).length;
  if (notes) changes.push(`${notes} new user note(s)`);
  return { firstVisit: false, changes, unavailable, details };
}
async function projectHome(project, snapshot, baseline, options) {
  const revision = project.revisions.at(-1);
  const row = snapshot.projects.find(r => r.id === project.project.id);
  const researches = (snapshot.researches || []).filter(r => r.projectId === project.project.id);
  const local = await localGit(project.project.folder, options);
  const evidence = await execution(row, researches);
  const durable = snapshot.execution || {};
  const megaTasks = (durable.megaTasks || []).filter(mega => mega.projectId === project.project.id);
  const mega = megaTasks.find(task => task.id === row?.megaTaskId) || megaTasks.at(-1);
  const subTasks = (durable.subTasks || []).filter(sub => sub.megaTaskId === mega?.id);
  const results = (durable.results || []).filter(result => megaTasks.some(mega => mega.id === result.megaTaskId));
  const technicalTimeline = (durable.timeline || []).filter(event => event.projectId === project.project.id);
  const timeline = summarizeTimeline(technicalTimeline, durable);
  const resumeTargets = (durable.checkpoints || []).filter(cp => cp.projectId === project.project.id && ['PAUSED', 'FAILED'].includes(megaTasks.find(mega => mega.id === cp.megaTaskId)?.status)).map(cp => ({ ...cp, researchId: snapshot.researches.find(record => record.megaTaskId === cp.megaTaskId)?.id }));
  const { reports = [], ...executionBaseline } = evidence || {};
  const current = { viewedAt: new Date().toISOString(), timelineEventIds: technicalTimeline.map(event => event.id), noteIds: (project.home?.notes || []).map(note => note.id), revisionIds: project.revisions.map(r => r.id), revisions: [{ id: revision.id, title: revision.title, state: revision.state, direction: revision.direction, closed: revision.closed }], git: local, execution: evidence ? { ...executionBaseline, reportFingerprint: hash(JSON.stringify(reports)) } : null };
  const running = (row?.dispatch || []).filter(p => p.alive).length + researches.filter(r => r.alive && r.status === 'watching').length;
  const labels = { watching: running ? 'Workers running' : 'Waiting for worker progress', needs_approval: 'Waiting for approval', waiting_on_you: 'Waiting for your answer', propose_grill: 'Waiting for your answer', checkpointed: 'Paused — checkpoint saved', complete: 'Completed', failed: 'Blocked', blocked_no_worker: 'Blocked', blocked_no_folder: 'Blocked', idle: 'Idle' };
  const next = homeNext(project, row, durable);
  const action = !linkedWorkspace(project.project.folder) ? null : mega ? mega.status === 'READY_FOR_APPROVAL' ? 'approve' : ['PAUSED', 'FAILED'].includes(mega.status) && resumeTargets.length ? 'resume' : null
    : row?.status === 'needs_approval' ? 'approve' : row?.canResume ? 'resume' : revision.open.length ? 'grill' : null;
  const completed = results.filter(result => result.status === 'COMPLETE');
  const workers = results.filter(result => result.megaTaskId === mega?.id && result.pid).map(result => ({ modelId: result.assignedModelId, provider: result.provider, cli: result.cli, subTaskId: result.subTaskId, attemptId: result.attemptId, status: result.status }));
  for (const sub of subTasks.filter(sub => sub.status === 'RUNNING')) workers.push({ modelId: sub.assignedModelId, provider: sub.provider, cli: sub.cli, subTaskId: sub.id, attemptId: sub.attemptIds.at(-1), status: sub.status });
  const since = await compare(baseline === undefined ? project.home?.lastVisit : baseline, current);
  const previousIds = new Set((baseline === undefined ? project.home?.lastVisit : baseline)?.timelineEventIds || []);
  const delta = summarizeTimeline(technicalTimeline.filter(event => !previousIds.has(event.id)), durable);
  if (delta.length && !since.firstVisit) { since.changes.push(`${delta.length} project milestone${delta.length === 1 ? '' : 's'} recorded`); since.details.push(...delta.slice(-8).map(event => event.summary)); }
  return { projectId: project.project.id, goal: revision.direction, progress: completed.length ? completed.slice(-3).map(result => result.summary).join(' ') : revision.state || 'No progress summary recorded.',
    progressDetails: { jobTitle: row?.jobTitle || '', status: row?.status || 'idle', actions: mega ? [] : current.execution?.actions || [], reports: mega ? [] : reports, files: local.files || [], megaTask: mega || null, subTasks, results,
      completed: completed.map(result => result.summary), pending: subTasks.filter(sub => sub.status !== 'COMPLETE'), tests: results.flatMap(result => result.tests), blockers: [...(mega?.blockers || []), ...results.filter(result => subTasks.some(sub => ['FAILED', 'BLOCKED'].includes(sub.status) && sub.resultId === result.resultId)).flatMap(result => result.blockers)],
      workers, gitProgress: mega?.repoAfter || mega?.repoBefore || null },
    timeline: technicalTimeline, timelineSummary: timeline, technicalTimeline, checkpointDiffs: resumeTargets.map(cp => ({ checkpointId: cp.id, ...checkpointDiff(cp, repoState(linkedWorkspace(project.project.folder), require('./orchestrator.js').defaultInspect(linkedWorkspace(project.project.folder))), revision.id, subTasks.filter(sub => sub.status === 'COMPLETE').map(sub => sub.id)) })), resumeTargets, actionTarget: { projectId: project.project.id, ...(mega ? { megaTaskId: mega.id } : {}), ...(action === 'resume' && resumeTargets.length === 1 ? { checkpointId: resumeTargets[0].id, megaTaskId: resumeTargets[0].megaTaskId, researchId: resumeTargets[0].researchId } : mega?.detached ? { researchId: snapshot.researches.find(record => record.megaTaskId === mega.id)?.id } : {}) },
    local, now: running ? 'Workers running' : mega && ['PAUSED', 'FAILED'].includes(mega.status) ? `${mega.status === 'FAILED' ? 'Failed' : 'Paused'} — checkpoint saved (${subTasks.filter(sub => sub.status !== 'COMPLETE').length} slices remaining)` : mega?.status === 'COMPLETE' ? 'Completed' : mega?.status === 'READY_FOR_APPROVAL' ? 'Waiting for approval' : labels[row?.status] || 'Idle', running, jobTitle: mega?.title || row?.jobTitle || '', next, action,
    notes: project.home?.notes || [], baseline: baseline === undefined ? project.home?.lastVisit || null : baseline, current,
    since };
}
module.exports = { projectHome, compare, localGit };
