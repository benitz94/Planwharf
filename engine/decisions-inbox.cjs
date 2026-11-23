'use strict';

/**
 * Job 39: Cross-project and pipeline decisions inbox, Auto mode and lead delegation.
 */

function nowIso() {
  return new Date().toISOString();
}

function isStepStarted(subTasks = [], megaTaskId) {
  if (!megaTaskId || !Array.isArray(subTasks)) return false;
  return subTasks.some(sub => sub && sub.megaTaskId === megaTaskId && (sub.status === 'RUNNING' || sub.status === 'COMPLETE'));
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
  if (!decision) return null;
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
  for (const p of (projects || [])) {
    if (p && p.id) {
      projectMap.set(p.id, p);
    } else if (p && p.project && p.project.id) {
      projectMap.set(p.project.id, p.project);
    }
  }

  const megaTasks = Array.isArray(execution?.megaTasks) ? execution.megaTasks : [];
  const subTasks = Array.isArray(execution?.subTasks) ? execution.subTasks : [];
  const decisions = Array.isArray(execution?.decisions) ? execution.decisions : [];

  for (const d of decisions) {
    if (!d) continue;
    const project = projectMap.get(d.projectId);
    const projectName = project?.name || d.projectId || 'Project';
    const mega = megaTasks.find(m => m && m.id === d.megaTaskId);
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

  for (const job of (pipelineJobs || [])) {
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
    if (Boolean(a?.breaks) !== Boolean(b?.breaks)) {
      return a?.breaks ? -1 : 1;
    }
    // Newest first, with NaN resilience
    const rawA = a?.answeredAt || a?.at || 0;
    const rawB = b?.answeredAt || b?.at || 0;
    const timeA = Number.isNaN(new Date(rawA).getTime()) ? 0 : new Date(rawA).getTime();
    const timeB = Number.isNaN(new Date(rawB).getTime()) ? 0 : new Date(rawB).getTime();
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

function delegateDecision(execution, projectId, decisionId, { pipelineJobs = [], leadLabel = 'Lead', reason = null, now = nowIso } = {}) {
  if (decisionId && String(decisionId).startsWith('pipeline:')) {
    const parts = String(decisionId).split(':');
    const jobId = parts[1];
    const qId = parts.slice(2).join(':');
    const job = (pipelineJobs || []).find(j => j && (jobId ? j.id === jobId : j.projectId === projectId));
    const question = job && Array.isArray(job.questions) ? job.questions.find(q => q && q.id === qId) : null;
    if (!question) {
      return { ok: false, reason: 'Pipeline decision not found.' };
    }
    autoDecideDecision(question, { leadLabel, reason, now });
    return { ok: true, decision: question };
  }
  if (!execution || !Array.isArray(execution.decisions)) {
    return { ok: false, reason: 'Execution state not available.' };
  }
  const decision = execution.decisions.find(item => item && item.projectId === projectId && item.id === decisionId);
  if (!decision) {
    return { ok: false, reason: 'Decision not found.' };
  }
  autoDecideDecision(decision, { leadLabel, reason, now });
  return { ok: true, decision };
}

function answerDecision(execution, projectId, decisionId, answer, { pipelineJobs = [], who = 'owner', subTasks = [], now = nowIso } = {}) {
  if (decisionId && String(decisionId).startsWith('pipeline:')) {
    const parts = String(decisionId).split(':');
    const jobId = parts[1];
    const qId = parts.slice(2).join(':');
    const job = (pipelineJobs || []).find(j => j && (jobId ? j.id === jobId : j.projectId === projectId));
    const question = job && Array.isArray(job.questions) ? job.questions.find(q => q && q.id === qId) : null;
    if (!question) {
      return { ok: false, reason: 'Pipeline decision not found.' };
    }
    const stepStarted = job.status === 'specifying' || job.status === 'building' || job.status === 'merged';
    if (question.answer && stepStarted) {
      return { ok: false, reason: 'The step that needs this decision has already started.' };
    }
    const val = String(answer || '').trim();
    question.answer = (val === 'accepted' && question.suggestion) ? question.suggestion : (val || 'yes');
    question.answeredBy = who === 'owner' ? 'owner' : who;
    question.answeredAt = typeof now === 'function' ? now() : now;
    return { ok: true, decision: question };
  }
  if (!execution || !Array.isArray(execution.decisions)) {
    return { ok: false, reason: 'Execution state not available.' };
  }
  const decision = execution.decisions.find(item => item && item.projectId === projectId && item.id === decisionId);
  if (!decision) {
    return { ok: false, reason: 'Decision not found.' };
  }
  if (decision.answer && isStepStarted(subTasks, decision.megaTaskId)) {
    return { ok: false, reason: 'The step that needs this decision has already started.' };
  }
  const val = String(answer || '').trim();
  decision.answer = (val === 'accepted' && decision.suggestion) ? decision.suggestion : (val || 'yes');
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
