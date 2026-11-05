'use strict';
// Explicit opt-in: uses the active Claude Code login for two small model turns.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
const { listClaude } = require('../engine/claude.cjs');
const { askOrchestrator, spawnWorker, isProcessAlive, killWorker } = require('../engine/runner.cjs');
const { readValidatedResult } = require('../engine/worker-result.cjs');

async function main() {
  if (process.argv[2] !== '--live' || !/^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(process.argv[3] || '')) throw Error('Usage: node validation/check-claude-cli.cjs --live <exact-Claude-model-ID>');
  const cli = process.argv[3];
  const catalog = await listClaude();
  assert.ok(catalog.ok && catalog.models.some(model => model.cli === cli), 'Active CLI must list the exact requested model');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-claude-live-'));
  const folder = path.join(dir, 'project'); fs.mkdirSync(folder);
  const attempts = path.join(dir, 'attempts'); fs.mkdirSync(attempts);
  const model = { id: 'claude-live', label: 'Isolated Claude CLI check', provider: 'claude', cli, alwaysApprove: false };
  fs.writeFileSync(path.join(folder, 'probe.txt'), 'CLAUDE_PLANWHARF_READ_OK');
  console.log('Isolated evidence directory: ' + dir);
  const lead = await askOrchestrator({ model, folder, question: 'Integration check: use Read to read probe.txt. Reply only with its exact contents. Do not edit files or run commands.' });
  assert.equal(lead.failure, undefined, lead.answer); assert.equal(lead.answer.trim(), 'CLAUDE_PLANWHARF_READ_OK');
  console.log('PASS real coordinator Read and exact model identity');
  const identity = { resultId: 'claude-live-result', attemptId: 'claude-live-attempt', subTaskId: 'claude-live-subtask', megaTaskId: 'claude-live-megatask', provider: 'claude', cli };
  const structuredResultPath = path.join(attempts, 'result.json');
  const exitPath = path.join(attempts, 'exit.json');
  const launched = spawnWorker({ linkedFolder: folder, folder, worker: { ...model, alwaysApprove: true },
    attemptId: identity.attemptId, resultId: identity.resultId, subTaskId: identity.subTaskId, megaTaskId: identity.megaTaskId,
    resultPath: path.join(attempts, 'result.txt'), structuredResultPath, exitPath, logPath: path.join(attempts, 'worker.log'),
    job: { title: 'Isolated Claude integration fixture', detail: 'Create only answer.txt containing CLAUDE_PLANWHARF_WRITE_OK in this temporary project. Read it back to verify its exact contents. Record the observed read-back as a test. Write the assigned structured result. Do not use network, install anything, touch other directories, or start agents. The result/evidence paths supplied by Planwharf are authorized.' } });
  const deadline = Date.now() + 120000;
  while (!fs.existsSync(exitPath) && isProcessAlive(launched.pid) && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 200));
  if (!fs.existsSync(exitPath)) { killWorker(launched.pid); throw Error('Worker did not save exit evidence. Inspect ' + dir); }
  const result = readValidatedResult(structuredResultPath, identity, exitPath);
  assert.ok(result, 'Worker result must match all task identities and exact provider/model exit evidence');
  assert.equal(result.status, 'COMPLETE');
  assert.equal(fs.readFileSync(path.join(folder, 'answer.txt'), 'utf8').trim(), 'CLAUDE_PLANWHARF_WRITE_OK');
  assert.ok(result.tests.some(test => test.status === 'PASSED'));
  assert.equal(fs.readFileSync(path.join(folder, 'probe.txt'), 'utf8'), 'CLAUDE_PLANWHARF_READ_OK');
  console.log(JSON.stringify({ status: 'PASSED', cli, provider: 'claude', catalogModels: catalog.models.length, coordinator: 'Read verified', worker: 'Write and structured completion verified', evidenceDirectory: dir }, null, 2));
  // Preserve raw output and attempts in this isolated directory for review.
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
