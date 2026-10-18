'use strict';
const crypto = require('node:crypto');
const Plan = require('../plan.js');
const { summarizeTimeline } = require('./project-history.cjs');
// Export only known metadata. Never collect source, auth profiles or raw log files.
const forbidden = /^(?:credentials?|auth|authEnv|access.?token|refresh.?token|tokens?|api.?key|password|secret|environment|env|workerTail|thinking|log)$/i;
function sanitize(value) {
  if (typeof value === 'string') return value.replace(/\bBearer\s+[A-Za-z0-9._~+\/-]+/gi, 'Bearer [REDACTED]').replace(/\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,})\b/g, '[REDACTED]');
  if (Array.isArray(value)) return value.map(sanitize);
  if (!value || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value).filter(([key]) => !forbidden.test(key)).map(([key, item]) => [key, sanitize(item)]));
}
function exportProject(project, execution, settings = {}) {
  const id = project.project.id;
  const megaTasks = execution.megaTasks.filter(mega => mega.projectId === id);
  const ids = new Set(megaTasks.map(mega => mega.id));
  const data = { megaTasks, subTasks: execution.subTasks.filter(sub => ids.has(sub.megaTaskId)), results: execution.results.filter(result => ids.has(result.megaTaskId)),
    checkpoints: execution.checkpoints.filter(cp => cp.projectId === id), timeline: execution.timeline.filter(event => event.projectId === id) };
  const summary = summarizeTimeline(data.timeline, data);
  return sanitize({ format: 'planwharf-project-export', schema: 1, exportedAt: new Date().toISOString(), restoreSupported: true,
    project: project.project, revisions: project.revisions, tasks: project.tasks, notes: project.home?.notes || [], execution: data, timeline: summary,
    settings: { providerPolicies: settings.providerPolicies, modelPolicies: settings.modelPolicies, modelRoles: settings.modelRoles, accountModelRoles: settings.accountModelRoles, mode: settings.mode, orchestratorId: settings.orchestratorId, enabled: settings.enabled, providerEnabled: settings.providerEnabled },
    markdown: `# ${project.project.name}\n\n${project.revisions.at(-1).direction}\n\n` + summary.map(event => `- ${event.createdAt}: ${event.summary}`).join('\n') });
}
function remapExecution(execution, projectId) {
  const source = execution && typeof execution === 'object' ? execution : {};
  const megaMap = new Map();
  const checkpointMap = new Map();
  const megaTasks = [];
  for (const mega of Array.isArray(source.megaTasks) ? source.megaTasks : []) {
    if (!mega || typeof mega !== 'object' || typeof mega.id !== 'string') continue;
    const id = 'MT-' + crypto.randomUUID();
    megaMap.set(mega.id, id);
    const status = ['COMPLETE', 'FAILED', 'CANCELLED', 'READY_FOR_APPROVAL', 'PAUSED'].includes(mega.status) ? mega.status : 'PAUSED';
    megaTasks.push({
      ...mega,
      id,
      projectId,
      status,
      repoBefore: mega.repoBefore && typeof mega.repoBefore === 'object' ? mega.repoBefore : { repoHead: null, dirtyFiles: [], comparable: false, gitReadable: false }
    });
  }
  for (const checkpoint of Array.isArray(source.checkpoints) ? source.checkpoints : []) {
    if (!checkpoint || typeof checkpoint !== 'object' || typeof checkpoint.id !== 'string' || !megaMap.has(checkpoint.megaTaskId)) continue;
    checkpointMap.set(checkpoint.id, 'CP-' + crypto.randomUUID());
  }
  for (const mega of megaTasks) {
    const mapped = checkpointMap.get(mega.currentCheckpointId);
    const fallback = [...checkpointMap.entries()].reverse().find(([from, to], index, all) => {
      const original = (source.checkpoints || []).find(item => item.id === from);
      return original && megaMap.get(original.megaTaskId) === mega.id;
    });
    mega.currentCheckpointId = mapped || fallback?.[1] || null;
  }
  const subMap = new Map();
  const subTasks = (Array.isArray(source.subTasks) ? source.subTasks : []).filter(item => item && megaMap.has(item.megaTaskId)).map(item => {
    const id = 'ST-' + crypto.randomUUID();
    if (typeof item.id === 'string') subMap.set(item.id, id);
    return {
      ...item,
      id,
      megaTaskId: megaMap.get(item.megaTaskId),
      assignedModelId: item.assignedModelId || 'unassigned'
    };
  });
  for (const sub of subTasks) {
    sub.dependencies = Array.isArray(sub.dependencies) ? sub.dependencies.map(id => subMap.get(id) || id) : [];
  }
  const results = (Array.isArray(source.results) ? source.results : []).filter(item => item && megaMap.has(item.megaTaskId)).map(item => ({ ...item, megaTaskId: megaMap.get(item.megaTaskId) }));
  const checkpoints = (Array.isArray(source.checkpoints) ? source.checkpoints : []).filter(item => item && checkpointMap.has(item.id)).map(item => ({
    ...item, id: checkpointMap.get(item.id), projectId, megaTaskId: megaMap.get(item.megaTaskId)
  }));
  const timeline = (Array.isArray(source.timeline) ? source.timeline : []).filter(item => item && typeof item === 'object').map(item => ({
    ...item,
    id: 'EV-' + crypto.randomUUID(),
    projectId,
    reference: item.reference && item.reference.megaTaskId && megaMap.has(item.reference.megaTaskId)
      ? { ...item.reference, megaTaskId: megaMap.get(item.reference.megaTaskId), checkpointId: checkpointMap.get(item.reference.checkpointId) || item.reference.checkpointId }
      : item.reference
  }));
  return sanitize({ megaTasks, subTasks, results, checkpoints, timeline });
}
function continueFrom(execution) {
  const paused = (execution?.megaTasks || []).find(mega => mega.status === 'PAUSED' && mega.currentCheckpointId);
  if (paused) return { action: 'resume', megaTaskId: paused.id, checkpointId: paused.currentCheckpointId };
  const ready = (execution?.megaTasks || []).find(mega => mega.status === 'READY_FOR_APPROVAL');
  if (ready) return { action: 'approve', megaTaskId: ready.id };
  return { action: null };
}
function importProject(notebook, bundle, place = {}) {
  const clean = sanitize(bundle);
  if (!clean || clean.format !== 'planwharf-project-export' || clean.schema !== 1) throw new Error('Import a Planwharf project export (schema 1).');
  const next = Plan.restoreProject(notebook, {
    name: clean.project?.name,
    revisions: clean.revisions,
    tasks: clean.tasks,
    notes: clean.notes
  }, place);
  const project = next.projects.at(-1);
  const execution = remapExecution(clean.execution, project.project.id);
  return { notebook: next, project, execution, continue: continueFrom(execution) };
}
module.exports = { exportProject, importProject, remapExecution, continueFrom, sanitize };
