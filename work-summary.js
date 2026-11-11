// A plain-English summary of finished work, built from what Planwharf recorded: no model call, no tokens.
// Used by the chat in the browser and by tests in Node.
(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.PlanwharfWorkSummary = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  const roleOf = sub => sub.type === 'AUDIT' ? 'Review' : sub.type === 'RESEARCH' ? 'Research' : /^Verify the work independently/.test(String(sub.title || '')) ? 'Verify' : 'Build';
  const plural = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`;

  function workSummary(row, { stopReason = null, changedFiles = null, modelLabel = id => id } = {}) {
    const subs = row?.subTasks || [], results = row?.results || [];
    const done = subs.filter(sub => sub.status === 'COMPLETE').length;
    const all = subs.length && done === subs.length;
    const reason = String(stopReason || '').split(':')[0];
    const lines = [];
    // What happened overall.
    if (all && reason === 'GOAL_REACHED') lines.push(`All ${plural(subs.length, 'step')} finished and the lead confirmed the goal.`);
    else if (all && (reason === 'LEAD_UNCONFIRMED' || reason === 'UNREADABLE_LEAD_DECISION')) lines.push(`All ${plural(subs.length, 'step')} finished, but the lead did not confirm the goal: check the result before you rely on it.`);
    else if (all) lines.push(`All ${plural(subs.length, 'step')} finished.`);
    else lines.push(`${done} of ${plural(subs.length, 'step')} finished.`);
    // Who did each step, and who it took over from.
    for (const sub of subs) {
      const from = sub.takeover?.fromModelId || sub.fallbackFrom;
      lines.push(`${roleOf(sub)}: ${modelLabel(sub.assignedModelId) || sub.assignedModelId || 'not assigned'}${from && from !== sub.assignedModelId ? `, taking over from ${modelLabel(from) || from}` : ''}.`);
    }
    // What went wrong; a model change on purpose is not a problem.
    const problems = results.filter(result => ['FAILED', 'BLOCKED'].includes(result.status) && !result.modelChange);
    if (problems.length) for (const result of problems) {
      const sub = subs.find(item => item.id === result.subTaskId);
      lines.push(`Problem in ${sub ? roleOf(sub) : 'a step'}: ${String(result.summary || 'no details recorded').trim()}`);
    } else lines.push('No problems were reported.');
    const tests = results.reduce((sum, result) => sum + (Number.isFinite(result.testsReported) ? result.testsReported : 0), 0);
    if (tests) lines.push(`Workers reported ${plural(tests, 'test run')}.`);
    if (changedFiles > 0) lines.push(`${plural(changedFiles, 'file')} changed and not yet committed: review them, then commit below.`);
    else if (changedFiles === 0) lines.push('No files are left uncommitted.');
    return lines;
  }
  return { workSummary };
});
