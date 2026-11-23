'use strict';

/**
 * Job 39: Cross-project and pipeline decisions inbox, Auto mode and lead delegation.
 */

function nowIso() {
  return new Date().toISOString();
}

function isStepStarted(subTasks = [], megaTaskId) {
  if (!megaTaskId || !Array.isArray(subTasks)) return false;
  return subTasks.some(sub => sub.megaTaskId === megaTaskId && (sub.status === 'RUNNING' || sub.status === 'COMPLETE'));
}

function isAutoDecide(projectId, settings = {}, projectData = null) {
  if (projectData && typeof projectData.autoDecide === 'boolean') {
    return projectData.autoDecide;
  }
  if (settings && settings.autoDecide && typeof settings.autoDecide[projectId] === 'boolean') {
    return settings.autoDecide[projectId];
  }
  if (settings && typeof settings.defaultAutoDecide === 'boolean') {
    return settings.defaultAutoDecide;
  }
  return false;
}

function autoDecideDecision(decision, { leadLabel = 'Lead', reason = null, now = nowIso } = {}) {
  const chosen = String(decision.suggestion || 'yes').trim();
  const autoReason = reason || (decision.suggestion ? `Followed recommended suggestion: ${decision.suggestion}` : 'Safe default accepted.');
  decision.answer = chosen;
  decision.answeredBy = `Decided by ${leadLabel}`;
  decision.leadLabel = leadLabel;
  decision.autoReason = autoReason;
  decision.answeredAt = typeof now === 'function' ? now() : now;
  return decision;
}

function collectDecisions({ projects = [], execution = {}, pipelineJobs = [], settings = {}, now = nowIso } = {}) {
  const allDecisions = [];
  const projectMap = new Map();
  for (const p of projects) {
    if (p && p.id) {
      projectMap.set(p.id, p);
    } else if (p && p.project && p.project.id) {
      projectMap.set(p.project.id, p.project);
    }
  }

  const megaTasks = Array.isArray(execution.megaTasks) ? execution.megaTasks : [];
  const subTasks = Array.isArray(execution.subTasks) ? execution.subTasks : [];
  const decisions = Array.isArray(execution.decisions) ? execution.decisions : [];

  for (const d of decisions) {
    const project = projectMap.get(d.projectId);
    const projectName = project?.name || d.projectId || 'Project';
    const mega = megaTasks.find(m => m.id === d.megaTaskId);
    const workTitle = mega?.title || mega?.goal || d.megaTaskId || 'Work';
    const stepStarted = isStepStarted(subTasks, d.megaTaskId);

    allDecisions.push({
      id: d.id,
      projectId: d.projectId,
      projectName,
      workId: d.megaTaskId,
      workTitle,
      isPipeline: false,
      text: d.text || '',
      suggestion: d.suggestion || '',
      breaks: Boolean(d.breaks),
      at: d.at || (typeof now === 'function' ? now() : now),
      answer: d.answer || '',
      answeredAt: d.answeredAt || null,
      answeredBy: d.answeredBy || (d.answer ? 'owner' : null),
      autoReason: d.autoReason || null,
      stepStarted
    });
  }

  for (const job of pipelineJobs) {
    if (!job) continue;
    const project = projectMap.get(job.projectId);
    const projectName = project?.name || job.projectId || 'Pipeline';
    const workTitle = job.title || job.id || 'Pipeline job';

    if (Array.isArray(job.questions)) {
      for (const q of job.questions) {
        if (!q) continue;
        allDecisions.push({
          id: `pipeline:${job.id}:${q.id}`,
          pipelineJobId: job.id,
          pipelineQuestionId: q.id,
          projectId: job.projectId,
          projectName,
          workId: job.id,
          workTitle,
          isPipeline: true,
          text: q.text || '',
          suggestion: q.suggestion || '',
          breaks: Boolean(q.breaks),
          at: q.at || job.updatedAt || job.createdAt || (typeof now === 'function' ? now() : now),
          answer: q.answer || '',
          answeredAt: q.answeredAt || null,
          answeredBy: q.answeredBy || (q.answer ? 'owner' : null),
          autoReason: q.autoReason || null,
          stepStarted: job.status === 'specifying' || job.status === 'building' || job.status === 'merged'
        });
      }
    }
  }

  function sortComparator(a, b) {
    // Breaking decisions come first
    if (a.breaks !== b.breaks) {
      return a.breaks ? -1 : 1;
    }
    // Newest first
    const timeA = new Date(a.answeredAt || a.at || 0).getTime();
    const timeB = new Date(b.answeredAt || b.at || 0).getTime();
    return timeB - timeA;
  }

  const open = allDecisions.filter(d => !d.answer).sort(sortComparator);
  const answered = allDecisions.filter(d => Boolean(d.answer)).sort(sortComparator);

  return {
    open,
    answered,
    openCount: open.length,
    all: allDecisions
  };
}

function delegateDecision(execution, projectId, decisionId, { leadLabel = 'Lead', reason = null, now = nowIso } = {}) {
  if (!execution || !Array.isArray(execution.decisions)) {
    return { ok: false, reason: 'Execution state not available.' };
  }
  const decision = execution.decisions.find(item => item.projectId === projectId && item.id === decisionId);
  if (!decision) {
    return { ok: false, reason: 'Decision not found.' };
  }
  autoDecideDecision(decision, { leadLabel, reason, now });
  return { ok: true, decision };
}

function answerDecision(execution, projectId, decisionId, answer, { who = 'owner', subTasks = [], now = nowIso } = {}) {
  if (!execution || !Array.isArray(execution.decisions)) {
    return { ok: false, reason: 'Execution state not available.' };
  }
  const decision = execution.decisions.find(item => item.projectId === projectId && item.id === decisionId);
  if (!decision) {
    return { ok: false, reason: 'Decision not found.' };
  }
  if (decision.answer && isStepStarted(subTasks, decision.megaTaskId)) {
    return { ok: false, reason: 'The step that needs this decision has already started.' };
  }
  decision.answer = String(answer || '').trim();
  decision.answeredBy = who === 'owner' ? 'owner' : who;
  decision.answeredAt = typeof now === 'function' ? now() : now;
  return { ok: true, decision };
}

module.exports = {
  isAutoDecide,
  autoDecideDecision,
  collectDecisions,
  delegateDecision,
  answerDecision,
  isStepStarted
};
