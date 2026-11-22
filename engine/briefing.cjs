'use strict';
const clip = (value, limit = 1200) => typeof value === 'string' ? value.slice(0, limit) : value;
function compactState(state, previous = null) {
  const handoff = state.executionHandoff;
  const mega = handoff?.megaTasks?.at(-1);
  const checkpoint = handoff?.checkpoints?.filter(cp => cp.megaTaskId === mega?.id).at(-1);
  const focus = state.focus ? { ...state.focus, repository: state.focus.repository ? { remote: { accessible: state.focus.repository.remote?.accessible, private: state.focus.repository.remote?.private, branch: state.focus.repository.remote?.branch, sha: state.focus.repository.remote?.sha }, comparison: state.focus.repository.comparison ? { status: state.focus.repository.comparison.status, ahead: state.focus.repository.comparison.ahead, behind: state.focus.repository.comparison.behind, error: clip(state.focus.repository.comparison.error) } : null } : null, analysis: clip(state.focus.analysis), state: clip(state.focus.state),
    plan: state.focus.plan?.slice(0, 8).map(text => clip(text, 400)), lastLog: undefined,
    workers: state.focus.workers?.map(({ log, ...worker }) => ({ ...worker, activity: clip(worker.activity, 300) })) } : null;
  const repository = focus ? { head: focus.lastCommit, dirty: focus.dirty } : null;
  const packed = { ...state, focus, ...(focus && focus.savingMode ? { savingMode: focus.savingMode } : {}),
    others: state.others?.map(({ repository, ...project }) => project),
    executionHandoff: handoff ? { megaTask: mega ? { id: mega.id, title: mega.title, goal: clip(mega.goal, 2400), status: mega.status, nextAction: clip(mega.nextAction) } : null,
      subTasks: handoff.subTasks?.filter(sub => sub.megaTaskId === mega?.id && sub.status !== 'COMPLETE').map(sub => ({ id: sub.id, title: sub.title, status: sub.status, assignedModelId: sub.assignedModelId, dependencies: sub.dependencies })),
      results: handoff.results?.filter(result => result.megaTaskId === mega?.id).slice(-3).map(result => ({ subTaskId: result.subTaskId, status: result.status, summary: clip(result.summary), nextAction: clip(result.nextAction), evidencePath: result.evidencePath })),
      checkpoint: checkpoint ? { id: checkpoint.id, completedSubTaskIds: checkpoint.completedSubTaskIds, pendingSubTaskIds: checkpoint.pendingSubTaskIds, failedSubTaskIds: checkpoint.failedSubTaskIds, nextAction: clip(checkpoint.nextAction) } : null,
      notes: handoff.notes?.filter(note => note.aiRelevant === true).slice(-12).map(note => ({ id: note.id, text: clip(note.text) })) } : null,
    researches: state.researches?.filter(research => research.projectId === focus?.id).slice(-5).map(research => ({ id: research.id, title: research.title, status: research.status, reportPath: research.reportPath })),
    repositoryDelta: previous ? { headChanged: previous.head !== repository?.head, dirtyChanged: JSON.stringify(previous.dirty) !== JSON.stringify(repository?.dirty), current: repository } : { initialInspection: true, current: repository } };
  return packed;
}
function recentContext(conversation) {
  return conversation.slice(0, 6).reverse().map(turn => ({ user: clip(turn.question, 1600), orchestrator: clip(turn.answer, 2400) }));
}
module.exports = { compactState, recentContext };
