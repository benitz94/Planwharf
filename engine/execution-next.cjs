'use strict';

// Presentation only: retain durable graph order and never schedule or mutate work.
function executionNext(mega, durable = {}) {
  if (!mega) return '';
  const subs = (durable.subTasks || []).filter(sub => sub.megaTaskId === mega.id);
  const results = (durable.results || []).filter(result => result.megaTaskId === mega.id);
  const cp = (durable.checkpoints || []).find(cp => cp.id === mega.currentCheckpointId && cp.megaTaskId === mega.id && cp.projectId === mega.projectId);
  const text = value => typeof value === 'string' ? value.trim() : '';
  const label = sub => sub.title ? `${sub.title} (${sub.id})` : sub.id;
  function recovery() {
    const failed = subs.filter(sub => ['FAILED', 'BLOCKED'].includes(sub.status));
    const currentResults = failed.map(sub => results.find(result => result.resultId === sub.resultId && result.subTaskId === sub.id)).filter(Boolean);
    const action = currentResults.map(result => text(result.nextAction)).find(Boolean);
    if (action) return action;
    const blockers = [...(mega.blockers || []), ...currentResults.flatMap(result => result.blockers || [])].map(text).filter(Boolean);
    if (blockers.length) return `Resolve blockers: ${blockers.join('; ')}`;
    if (failed.length) return `Review ${label(failed[0])} and resume from an explicit checkpoint.`;
    return '';
  }
  function remaining() {
    const active = subs.find(sub => sub.status === 'RUNNING');
    if (active) return `Work in progress: ${label(active)}.`;
    const pending = subs.filter(sub => sub.status === 'PENDING');
    const prerequisites = sub => (sub.dependencies || []).filter(id => subs.find(parent => parent.id === id)?.status !== 'COMPLETE');
    const runnable = pending.find(sub => prerequisites(sub).length === 0);
    if (runnable) return `Next SubTask: ${label(runnable)}.`;
    if (pending.length) {
      const sub = pending[0];
      const waiting = prerequisites(sub).map(id => { const parent = subs.find(parent => parent.id === id); return parent ? label(parent) : id; });
      return `Waiting for prerequisite ${waiting.join(', ')} before ${label(sub)}.`;
    }
    return recovery();
  }
  if (mega.status === 'READY_FOR_APPROVAL') return text(mega.nextAction) || 'Review the proposed slices and approve this MegaTask.';
  if (mega.status === 'COMPLETE') return text(results.filter(result => result.status === 'COMPLETE').at(-1)?.nextAction) || 'Review completed evidence.';
  if (mega.status === 'PAUSED') return text(cp?.nextAction) || remaining() || 'Review remaining work and resume this checkpoint.';
  if (['FAILED', 'BLOCKED'].includes(mega.status)) return text(cp?.nextAction) || recovery() || 'Review failed work and resume from an explicit checkpoint.';
  if (mega.status === 'RUNNING') return mega.deadlineError ? text(mega.nextAction) : remaining() || 'Reconciling completed work.';
  return 'Review execution state.';
}

// Home presents one deterministic action; imported continuation metadata is not authority.
function homeNext(item, row = {}, durable = {}) {
  const { linkedWorkspace } = require('./project-workspace.cjs');
  if (!linkedWorkspace(item?.project?.folder)) return 'Relink the project folder on this PC.';
  const tasks = (durable.megaTasks || []).filter(task => task.projectId === item.project.id);
  const mega = tasks.find(task => task.id === row?.megaTaskId) || tasks.at(-1);
  if (mega?.status === 'READY_FOR_APPROVAL') return 'Approve the prepared MegaTask.';
  if (mega) {
    if (['PAUSED', 'FAILED'].includes(mega.status) && (durable.checkpoints || []).some(cp => cp.projectId === item.project.id && cp.megaTaskId === mega.id)) return 'Resume from checkpoint.';
    return executionNext(mega, durable);
  }
  if (row?.status === 'needs_approval') return 'Approve the prepared job.';
  if (row?.canResume) return 'Resume from checkpoint.';
  if (item.revisions?.at(-1)?.open?.length) return 'Tell the lead what to build.';
  return 'Review the Living Plan.';
}

module.exports = { executionNext, homeNext };
