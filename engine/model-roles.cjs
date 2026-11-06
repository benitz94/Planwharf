'use strict';
const ROLE_IDS = ['orchestrator', 'worker', 'verifier', 'reviewer', 'supervisor'];
// The verify slice checks and fixes the builder's work. Only Verifier models take it.
const VERIFY_SLICE = 'Verify the work independently against the job';
const isVerifySlice = value => typeof value === 'string' && value.startsWith(VERIFY_SLICE);
// Only the API advisory transport has a verified no-tool execution contract.
const supportsSupervisor = model => (model.adapter || model.provider) === 'api';
function roleEligibility(model) {
  const tools = model.capabilities?.canUseTools !== false && model.capabilities?.canInspectFilesystem !== false;
  return {
    orchestrator: tools && (model.orchestratorEligible ?? model.capabilities?.orchestratorEligible ?? model.capabilities?.canOrchestrate ?? model.kind !== 'local'),
    worker: model.workerEligible !== false && model.capabilities?.workerEligible !== false,
    verifier: model.workerEligible !== false && model.capabilities?.workerEligible !== false,
    reviewer: tools && (model.reviewerEligible ?? model.capabilities?.reviewerEligible ?? model.workerEligible !== false),
    supervisor: supportsSupervisor(model) && model.supervisorEligible !== false && model.capabilities?.supervisorEligible !== false
  };
}
function withModelRoles(model, preferences = {}) {
  const eligibility = roleEligibility(model);
  const chosen = role => preferences[role] ?? model.roles?.[role];
  // A model saved before the Verifier role existed keeps verifying where it could work.
  const fallback = role => role === 'supervisor' ? false : role === 'verifier' ? (chosen('worker') ?? true) : true;
  const roles = Object.fromEntries(ROLE_IDS.map(role => [role, Boolean(eligibility[role] && (chosen(role) ?? fallback(role)))]));
  return { ...model, roleEligibility: eligibility, roles };
}
function assignmentRole(job = {}) {
  if (job?.type === 'AUDIT' || job?.kind === 'audit' || job?.kind === 'review') return 'reviewer';
  return job?.kind === 'verify' || isVerifySlice(job?.title) || isVerifySlice(job?.detail) || isVerifySlice(job?.slice) ? 'verifier' : 'worker';
}
function permitsAssignment(model, job) {
  const role = assignmentRole(job);
  const resolved = withModelRoles(model);
  if (role === 'worker' && job?.goldModel && (model.id !== job.goldModel.id || (model.providerId || model.provider) !== job.goldModel.provider || model.cli !== job.goldModel.cli)) return false;
  return resolved.roles[role] === true;
}
module.exports = { ROLE_IDS, VERIFY_SLICE, isVerifySlice, roleEligibility, withModelRoles, assignmentRole, permitsAssignment, supportsSupervisor };
