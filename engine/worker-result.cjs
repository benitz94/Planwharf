'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const statuses = ['COMPLETE', 'FAILED', 'BLOCKED'];
function readJSON(file) {
  try { if (fs.statSync(file).size > 1024 * 1024) return null; return JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, '')); } catch { return null; }
}
function validateResult(result, identity) {
  if (!result || typeof result !== 'object' || Array.isArray(result)) return false;
  for (const key of ['resultId', 'attemptId', 'subTaskId', 'megaTaskId']) if (typeof result[key] !== 'string' || !result[key] || result[key] !== identity[key]) return false;
  if (!statuses.includes(result.status) || typeof result.summary !== 'string' || !result.summary.trim() || typeof result.nextAction !== 'string'
    || result.createdAt !== undefined && (typeof result.createdAt !== 'string' || !Number.isFinite(Date.parse(result.createdAt)))) return false;
  for (const key of ['filesChanged', 'tests', 'blockers']) if (!Array.isArray(result[key])) return false;
  if (!result.filesChanged.every(file => typeof file === 'string') || !result.blockers.every(item => typeof item === 'string')) return false;
  if (!result.tests.every(test => test && typeof test.command === 'string' && ['PASSED', 'FAILED', 'NOT_RUN'].includes(test.status) && typeof test.evidence === 'string')) return false;
  if (result.sources !== undefined && (!Array.isArray(result.sources) || !result.sources.every(source => typeof source === 'string'))) return false;
  return true;
}
function readValidatedResult(file, identity, exitFile) {
  const result = readJSON(file), exit = readJSON(exitFile);
  if (!validateResult(result, identity) || !exit || exit.attemptId !== identity.attemptId || !Number.isInteger(exit.exitCode)) return null;
  if (identity.provider && exit.provider !== identity.provider || identity.cli && exit.model !== identity.cli) return null;
  if (result.status === 'COMPLETE' && exit.exitCode !== 0) return null;
  // The runner stamps the exit record, so a worker need not spend a call on the clock.
  return { ...result, createdAt: result.createdAt || exit.createdAt, exitCode: exit.exitCode, evidencePath: file };
}
function resultContract(opts) {
  if (!opts.structuredResultPath) return '';
  const template = { resultId: opts.resultId, attemptId: opts.attempt, subTaskId: opts.subTaskId, megaTaskId: opts.megaTaskId,
    status: 'COMPLETE | FAILED | BLOCKED', summary: 'What actually happened', filesChanged: [], tests: [{ command: 'Exact command or check', status: 'PASSED | FAILED | NOT_RUN', evidence: 'Observed outcome; do not invent execution' }], blockers: [], nextAction: 'Remaining work or recommended next step' };
  if (require('./structured-final.cjs').supportsFinalResult(opts.model)) return `Planwharf structured result contract:\nFinish by returning this object as your structured final output, with these exact identity fields. Do not write a result file; Planwharf saves it and records the time:\n${JSON.stringify(template, null, 2)}\nUse one status from the listed choices. Record only observed work and tests. No tests means an empty array. Exit 0 alone is not completion. For research, include reportPath (the absolute report.md path), sources (URLs), and recommendedNextAction.`;
  return `Planwharf structured result contract:\nWrite one UTF-8 JSON object to ${opts.structuredResultPath}. If a write_result tool is provided, submit the JSON through that tool instead of writing the result with write_file. Use these exact identity fields:\n${JSON.stringify(template, null, 2)}\nUse one status from the listed choices. Record only observed work and tests. No tests means an empty array. Exit 0 alone is not completion. For research, include reportPath (the absolute report.md path), sources (URLs), and recommendedNextAction. Keep the text execution evidence separate.`;
}
// A COMPLETE result naming a file that is not there is a false claim. A missing
// path is accepted only when Git tracks it, which is what a deletion looks like.
function missingClaims(result, folder) {
  if (result?.status !== 'COMPLETE' || !Array.isArray(result.filesChanged)) return [];
  return result.filesChanged.filter(file => {
    if (typeof file !== 'string' || !file.trim()) return false;
    if (fs.existsSync(path.resolve(folder, file))) return false;
    return spawnSync('git', ['ls-files', '--error-unmatch', '--', file], { cwd: folder, windowsHide: true, stdio: 'ignore' }).status !== 0;
  });
}
function rejectMissingClaims(result, folder) {
  const missing = missingClaims(result, folder);
  if (!missing.length) return result;
  return { ...result, status: 'FAILED', blockers: [...result.blockers, 'Planwharf found no such file: ' + missing.join(', ')] };
}
function reportExists(result, folder) {
  if (!result.reportPath || path.resolve(result.reportPath) !== path.join(path.resolve(folder), 'report.md')) return false;
  try { const stat = fs.lstatSync(result.reportPath); return stat.isFile() && !stat.isSymbolicLink() && stat.size > 0; } catch { return false; }
}
module.exports = { readJSON, validateResult, readValidatedResult, resultContract, reportExists, missingClaims, rejectMissingClaims };
