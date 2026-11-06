// Legacy provider and transport fixtures; Phase 2 approval/result contracts are covered by phase2 tests.
'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const Plan = require('../plan.js');
const { createServer } = require('../server.cjs');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
const { launchSpec } = require('./providers.cjs');
const { promptFor } = require('./runner.cjs');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-research-dispatch-'));
const source = path.join(dir, 'source');
fs.mkdirSync(source);
fs.writeFileSync(path.join(source, 'personal.txt'), 'Original source evidence.');
const plan = Plan.example();
plan.tasks = [];
plan.project = { id: 'research-demo', name: 'Research demo', folder: source };
const planFile = path.join(dir, 'notebook.json');
fs.writeFileSync(planFile, JSON.stringify(plan));
const models = [
  { id: 'astra-fixture', label: 'GPT-6 Astra', cli: 'gpt-6-astra', provider: 'codex', kind: 'premium', enabled: true, available: true, limit: 200 },
  { id: 'luna-fixture', label: 'GPT-6 Luna', cli: 'gpt-6-luna', provider: 'codex', kind: 'local', enabled: true, available: true, limit: 200 },
  { id: 'off-fixture', label: 'Disabled worker', cli: 'gpt-6-sol', provider: 'codex', enabled: false, available: true, limit: 200 },
  { id: 'grok-fixture', label: 'Grok fixture', cli: 'grok-4.6', provider: 'grok', enabled: true, available: true, limit: 200 }
];
const action = { type: 'dispatch_research', projectId: plan.project.id, modelId: 'luna-fixture', title: 'PS5 telemetry evidence', detail: 'Research original GitHub projects extracting PS5 telemetry. Compare GT7 and F1; cite sources. Do not implement or modify project source.' };
const launches = [];
const alive = new Set();
const spawnWorker = info => { const pid = 600000 + launches.length; launches.push(info); alive.add(pid); fs.writeFileSync(info.logPath, 'Reading original GitHub source.\n'); return { pid, logPath: info.logPath }; };
let replyAction = action;
let base;
const server = createServer({ phase2: false,planFile, attemptsDir: path.join(dir, 'attempts'), models, inspectFolder: () => ({ fileCount: 1, names: ['personal.txt'], git: false }), fetchPlans: async () => [],
  spawnWorker, isProcessAlive: pid => alive.has(pid), killWorker: pid => alive.delete(pid),
  askModel: async info => {
    const answer = JSON.stringify({ answer: 'Research assignment proposed.', actions: [replyAction] });
    info.onProgress({ answer, thinking: 'Selecting the requested worker.' });
    const live = await (await fetch(base + '/api/orchestrator')).json();
    assert.equal(live.thinking.draft, '', 'Structured actions must not flash in the chat draft');
    return { answer };
  }
});
let cancelledOrchestra;

async function main() {
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  base = `http://127.0.0.1:${server.address().port}`;
  const post = async (route, data) => { const response = await fetch(base + route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return { status: response.status, body: await response.json() }; };
  assert.equal((await post('/api/orchestrator/lead', { id: 'astra-fixture' })).status, 200);
  const ask = question => post('/api/orchestrator/ask', { question, projectId: plan.project.id });
  const asked = await ask('Per favore fai cercare al worker GPT-6 Luna come GitHub recupera la telemetria PS5, senza modificare i sorgenti.');
  assert.equal(asked.status, 200);
  assert.equal(asked.body.actionResults[0].ok, true);
  assert.match(asked.body.answer, /Notebook: started GPT-6 Luna \(codex\/gpt-6-luna\), process/);
  assert.equal(launches.length, 1);
  assert.equal(launches[0].worker.id, 'luna-fixture');
  assert.equal(launches[0].job.kind, 'research');
  assert.notEqual(launches[0].folder, source);
  assert.ok(launches[0].folder.startsWith(path.join(dir, 'attempts', 'research')));
  assert.match(launches[0].job.detail, /Read-only source folder/);
  assert.doesNotMatch(promptFor({ kind: 'research', detail: 'Research.' }), /Implement the job/);
  assert.equal(asked.body.snapshot.fleet.find(item => item.id === 'luna-fixture').busy, true);
  const record = asked.body.snapshot.researches[0];
  assert.equal(record.status, 'watching');
  assert.match(record.activity, /GitHub/);
  assert.equal((await fetch(base + '/api/orchestrator/research/report?id=' + record.id)).status, 404);

  const duplicate = await ask('Please research PS5 telemetry with GPT-6 Luna.');
  assert.equal(duplicate.body.actionResults[0].duplicate, true);
  assert.equal(launches.length, 1);
  const discussion = await ask('What is the telemetry format?');
  assert.equal(discussion.body.actionResults[0].ok, false);
  assert.equal(launches.length, 1, 'A question cannot authorize a lead-created worker action');
  replyAction = { ...action, modelId: 'astra-fixture' };
  const substituted = await ask('Please research using GPT-6 Luna.');
  assert.equal(substituted.body.actionResults[0].ok, false);
  assert.match(substituted.body.answer, /different model/);
  for (const modelId of ['unknown-model', 'off-fixture', 'grok-fixture']) {
    replyAction = { ...action, modelId };
    const rejected = await ask('Please research PS5 telemetry with a worker.');
    assert.equal(rejected.body.actionResults[0].ok, false);
  }
  replyAction = { ...action, type: 'execute_shell' };
  assert.equal((await ask('Please research PS5 telemetry.')).body.actionResults[0].ok, false);
  assert.equal(launches.length, 1);

  const stopped = await post('/api/orchestrator/research/stop', { id: record.id });
  assert.equal(stopped.body.research.status, 'checkpointed');
  const resumed = await post('/api/orchestrator/research/resume', { id: record.id });
  assert.equal(resumed.status, 200);
  assert.equal(launches.length, 2);
  assert.notEqual(launches[0].folder, launches[1].folder);
  assert.match(launches[1].job.detail, /Previous isolated evidence folders/);
  fs.writeFileSync(launches[1].resultPath, 'STATUS: COMPLETE\nEXIT: 0\n');
  const missingReport = await (await fetch(base + '/api/orchestrator')).json();
  assert.equal(missingReport.researches[0].status, 'failed', 'Successful exit alone is insufficient research evidence');
  const restarted = await post('/api/orchestrator/research/resume', { id: record.id });
  assert.equal(restarted.status, 200);
  fs.writeFileSync(path.join(launches[2].folder, 'report.md'), '# Findings\nVerified original repository: https://github.com/example/telemetry\n');
  fs.writeFileSync(launches[2].resultPath, 'STATUS: COMPLETE\n');
  const noExit = await (await fetch(base + '/api/orchestrator')).json();
  assert.equal(noExit.researches[0].status, 'watching', 'A report and worker-authored marker cannot replace provider exit evidence');
  fs.writeFileSync(launches[2].resultPath, 'STATUS: COMPLETE\nEXIT: 0\n');
  const complete = await (await fetch(base + '/api/orchestrator')).json();
  assert.equal(complete.researches[0].status, 'complete');
  const report = await (await fetch(base + '/api/orchestrator/research/report?id=' + record.id)).json();
  assert.match(report.report, /Verified original repository/);
  assert.equal((await fetch(base + '/api/orchestrator/research/report?id=..%2Fpersonal.txt')).status, 404);
  assert.equal(fs.readFileSync(path.join(source, 'personal.txt'), 'utf8'), 'Original source evidence.');
  assert.deepEqual(JSON.parse(fs.readFileSync(planFile, 'utf8')), plan, 'Research never replaces or mutates notebook tasks');

  const savedOverride = process.env.PLANWHARF_CODEX;
  process.env.PLANWHARF_CODEX = process.execPath;
  try {
    const spec = launchSpec(models[1], launches[0].folder, 'Research.', false, true);
    assert.ok(!spec.args.includes('--dangerously-bypass-approvals-and-sandbox'));
    assert.equal(spec.args[spec.args.indexOf('--sandbox') + 1], 'workspace-write');
    assert.ok(spec.args.includes('sandbox_workspace_write.writable_roots=[]'));
    assert.ok(spec.args.includes('sandbox_workspace_write.network_access=true'));
    assert.ok(spec.args.includes('approval_policy="never"'));
  } finally { if (savedOverride === undefined) delete process.env.PLANWHARF_CODEX; else process.env.PLANWHARF_CODEX = savedOverride; }

  // Cancel exactly while dispatch awaits its notebook read: no process may start.
  let blockRead = false;
  let entered;
  let release;
  const waiting = new Promise(resolve => { entered = resolve; });
  const released = new Promise(resolve => { release = resolve; });
  const cancelDir = path.join(dir, 'cancel-attempts');
  cancelledOrchestra = createOrchestrator({ dir: cancelDir, models, attempts: createAttemptEngine({ dir: cancelDir }), fetchPlans: async () => [],
    readPlan: async () => { if (blockRead) { blockRead = false; entered(); await released; } return plan; },
    readRepository: async () => ({ ok: true }), inspectFolder: () => ({ fileCount: 1, names: [], git: false }),
    spawnWorker: () => { throw new Error('A cancelled research dispatch must not launch'); },
    askModel: async () => { blockRead = true; return { answer: JSON.stringify({ answer: 'Proposed.', actions: [action] }) }; }
  });
  await cancelledOrchestra.setOrchestrator('astra-fixture');
  const pending = cancelledOrchestra.ask('Please research with GPT-6 Luna.', plan.project.id);
  await waiting; await cancelledOrchestra.abortAsk(); release();
  assert.equal((await pending).cancelled, true);
  assert.equal((await cancelledOrchestra.snapshot()).researches.length, 0);
  replyAction = { ...action, title: 'Another bounded investigation' };
  const last = await ask('Please research another topic with GPT-6 Luna.');
  assert.equal(last.body.actionResults[0].ok, true);
  await server.shutdown();
  const checkpoint = JSON.parse(fs.readFileSync(path.join(dir, 'attempts', 'orchestrator.json'), 'utf8')).researches.find(item => item.id === last.body.actionResults[0].research.id);
  assert.equal(checkpoint.status, 'checkpointed', 'Shutdown stops research and preserves its records');
  assert.equal(alive.has(checkpoint.pid), false);
  console.log('OK: conversational research dispatch receipts, isolated exact-model workers, gates, reports, stop/resume, and cancellation.');
}

main().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (cancelledOrchestra) await cancelledOrchestra.shutdown();
  await new Promise(resolve => server.close(resolve));
  fs.rmSync(dir, { recursive: true, force: true });
});
