'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phase2-'));
const file = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
const starter = require('../fixtures/starter-notebook.json').projects[0];
const plan = { schema: 2, projects: ['raceengineer', 'ai-companion-desktop'].map(id => ({ ...structuredClone(starter), project: { id, name: id, folder: path.join(dir, id) }, tasks: [] })) };
for (const item of plan.projects) fs.mkdirSync(item.project.folder);
fs.writeFileSync(file, JSON.stringify(plan));
const models = [1, 2, 3].map(n => ({ id: `fixture-${n}`, label: `Fixture ${n}`, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: n === 3 ? 'premium' : 'default', cost: 1, limit: 100 }));
const launches = [], live = new Set(), killed = [];
let pid = 1000, head = 'initial', server, base, killAllowed = true;
async function start() {
  server = createServer({ planFile: file, attemptsDir, models, fetchPlans: null,refreshModels: false, monitorIntervalMs: 60000,
    inspectFolder: folder => ({ folder, git: true, head, dirty: [], names: [], fileCount: 0 }),
    spawnWorker: info => { const processId = ++pid; launches.push({ ...info, pid: processId }); live.add(processId); return { pid: processId }; },
    isProcessAlive: processId => live.has(processId), killWorker: processId => { if (!killAllowed) return false; killed.push(processId); return live.delete(processId); },
    askModel: () => { throw new Error('Displaying progress must not call a model.'); } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = `http://127.0.0.1:${server.address().port}`;
}
async function request(route, data, status = 200) {
  const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
  const result = await response.json(); assert.equal(response.status, status, JSON.stringify(result)); return result;
}
async function propose(projectId, count = 3, kind) {
  const result = await request('/api/orchestrator/propose', { projectId, title: 'Isolated MegaTask', goal: 'Verify durable execution', ...(kind ? { kind } : {}),
    subTasks: Array.from({ length: count }, (_, index) => ({ title: `Slice ${index + 1}`, detail: 'Fixture slice', assignedModelId: 'fixture-' + (index % 3 + 1), dependsOn: index ? [index - 1] : [] })) }, 201);
  return result.snapshot.projects.find(row => row.id === projectId).megaTaskId;
}
function finish(info, status = 'COMPLETE') {
  const result = { resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status, summary: `Finished ${info.job.title}`,
    filesChanged: ['fixture.txt'], tests: [{ command: 'fixture check', status: 'PASSED', evidence: 'Isolated observed fixture' }], blockers: [], nextAction: 'Run the next isolated probe', createdAt: new Date().toISOString() };
  if (info.job.kind === 'research') { result.reportPath = path.join(info.folder, 'report.md'); result.sources = ['https://example.com/original-source']; fs.writeFileSync(result.reportPath, 'Isolated research report.'); }
  fs.writeFileSync(info.structuredResultPath, JSON.stringify(result));
  fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: status === 'FAILED' ? 1 : 0, provider: info.worker.provider, model: info.worker.cli }));
  fs.writeFileSync(info.resultPath, `STATUS: ${status}\nEXIT: ${status === 'FAILED' ? 1 : 0}\n`); live.delete(info.pid);
}
(async () => {
  await start();
  await request('/api/focus', { projectId: 'raceengineer' });
  const mtRace = await propose('raceengineer');
  assert.equal(launches.length, 0, 'Proposal does not launch');
  assert.match((await request('/api/project-home', { projectId: 'raceengineer' })).next, /approve/i);
  await request('/api/orchestrator/approve', { projectId: 'ai-companion-desktop', megaTaskId: mtRace }, 400);
  await request('/api/orchestrator/approve', { projectId: 'raceengineer', megaTaskId: mtRace });
  assert.equal(launches.length, 1, 'Dependencies wait without processes');
  const runningHome = await request('/api/project-home', { projectId: 'raceengineer' });
  assert.match(runningHome.next, /Work in progress: Slice 1/); assert.doesNotMatch(runningHome.next, /approv/i);
  assert.equal(runningHome.next, (await request('/api/orchestrator?readOnly=1')).projects[0].next);
  assert.equal(launches[0].folder, plan.projects[0].project.folder);
  const firstSubId = launches[0].subTaskId;
  finish(launches[0]);
  const pendingHome = await request('/api/project-home', { projectId: 'raceengineer' });
  assert.match(pendingHome.next, /Next SubTask: Slice 2/);
  assert.equal(launches.length, 1, 'Home reconciles completion without dispatching the pending dependency');
  assert.equal(pendingHome.next, (await request('/api/orchestrator?readOnly=1')).projects[0].next);
  assert.equal(launches.length, 1, 'Read-only Orchestrator projection cannot spawn workers');
  await request('/api/orchestrator');
  assert.equal(launches.length, 2);
  assert.match((await request('/api/project-home', { projectId: 'raceengineer' })).next, /Work in progress: Slice 2/);
  const interrupted = launches[1];
  await request('/api/orchestrator/stop', { projectId: 'raceengineer', megaTaskId: mtRace });
  assert.ok(killed.includes(interrupted.pid)); assert.equal(live.size, 0);
  const race = await request('/api/execution?projectId=raceengineer');
  const cp = race.checkpoints[0];
  assert.deepEqual(cp.completedSubTaskIds, [firstSubId]);
  assert.ok(cp.activeAttemptIds.includes(interrupted.attemptId));
  assert.equal(race.subTasks[2].attemptIds.length, 0, 'Dependent task never ran');
  const home = await request('/api/project-home', { projectId: 'raceengineer', visit: true });
  assert.match(home.now, /Paused.*checkpoint saved/); assert.ok(home.progress.includes('Finished')); assert.ok(home.timeline.some(event => event.type === 'CHECKPOINT_SAVED'));
  assert.equal(home.next, 'Resume from checkpoint.'); assert.equal(home.next, (await request('/api/orchestrator?readOnly=1')).projects[0].next);
  await request('/api/focus', { projectId: 'ai-companion-desktop' });
  assert.equal((await request('/api/orchestrator?readOnly=1')).selectedProjectId, 'ai-companion-desktop');
  await request('/api/orchestrator/resume', { projectId: 'ai-companion-desktop', megaTaskId: mtRace, checkpointId: cp.id }, 400);
  assert.equal(launches.length, 2);
  await new Promise(resolve => server.close(resolve)); server = null; await start();
  await request('/api/orchestrator/lead', { id: 'fixture-3' });
  head = 'external-commit';
  const confirmation = await request('/api/orchestrator/resume', { projectId: 'raceengineer', megaTaskId: mtRace, checkpointId: cp.id }, 409);
  assert.equal(confirmation.requiresConfirmation, true); assert.equal(launches.length, 2);
  await request('/api/orchestrator/resume', { projectId: 'raceengineer', megaTaskId: mtRace, checkpointId: cp.id, confirmChanges: true });
  assert.equal(launches.length, 3);
  assert.equal(launches[2].subTaskId, interrupted.subTaskId);
  assert.notEqual(launches[2].attemptId, interrupted.attemptId);
  assert.ok(launches[2].job.detail.includes(firstSubId), 'Compact handoff contains durable completed work');
  await request('/api/orchestrator/stop', { projectId: 'raceengineer', megaTaskId: mtRace });
  await request('/api/orchestrator/resume', { projectId: 'raceengineer' }, 400);
  const current = await request('/api/execution?projectId=raceengineer'), lastCP = current.checkpoints.at(-1);
  await request('/api/orchestrator/resume', { projectId: 'raceengineer', megaTaskId: mtRace, checkpointId: lastCP.id });
  finish(launches.at(-1)); await request('/api/orchestrator');
  finish(launches.at(-1)); await request('/api/orchestrator');
  const complete = await request('/api/execution?projectId=raceengineer');
  assert.equal(complete.megaTasks[0].status, 'COMPLETE');
  assert.equal(complete.megaTasks[0].orchestratorModelId, 'fixture-3');
  assert.equal(complete.subTasks[0].attemptIds.length, 1, 'Completed work not replayed');
  assert.equal(complete.subTasks[1].attemptIds.length, 3);
  assert.equal(JSON.parse(fs.readFileSync(file)).projects[0].tasks[0].status, 'complete');
  const after = await request('/api/project-home', { projectId: 'raceengineer', baseline: home.current });
  assert.ok(after.since.details.some(text => text.includes('Completed Isolated MegaTask')));
  const mtAI = await propose('ai-companion-desktop', 1);
  await request('/api/orchestrator/approve', { projectId: 'ai-companion-desktop', megaTaskId: mtAI });
  live.delete(launches.at(-1).pid);
  const stale = await request('/api/orchestrator?readOnly=1');
  const ai = stale.projects.find(row => row.id === 'ai-companion-desktop');
  assert.equal(ai.workerAlive, false); assert.equal(ai.megaTask.status, 'FAILED');
  assert.equal((await request('/api/execution?projectId=ai-companion-desktop')).results.at(-1).status, 'FAILED');
  // Independent research proposals preserve the existing implementation task.
  const aiTasks = JSON.stringify(JSON.parse(fs.readFileSync(file)).projects[1].tasks);
  const beforeResearch = launches.length;
  const proposedResearch = await request('/api/orchestrator/research/propose', { projectId: 'ai-companion-desktop', modelId: 'fixture-1', title: 'Telemetry research fixture', detail: 'Read isolated sources and write a report.' }, 201);
  const research = proposedResearch.research;
  assert.equal(proposedResearch.proposal, true); assert.equal(launches.length, beforeResearch);
  await request('/api/orchestrator/research/approve', { id: research.id, projectId: 'raceengineer', megaTaskId: research.megaTaskId }, 400);
  await request('/api/orchestrator/research/approve', { id: research.id, projectId: research.projectId, megaTaskId: research.megaTaskId });
  const researchWorker = launches.at(-1);
  assert.ok(researchWorker.folder.startsWith(attemptsDir + path.sep));
  assert.equal(researchWorker.job.kind, 'research');
  finish(researchWorker); await request('/api/orchestrator');
  const researchHome = await request('/api/project-home', { projectId: research.projectId });
  assert.ok(researchHome.progress.includes('Telemetry research fixture'));
  assert.ok(researchHome.progressDetails.results.some(result => result.reportPath && result.sources?.length));
  assert.equal(JSON.stringify(JSON.parse(fs.readFileSync(file)).projects[1].tasks), aiTasks, 'Research does not replace implementation task');
  const report = await request('/api/orchestrator/research/report?id=' + research.id);
  assert.match(report.report, /Isolated research report/);
  // Graceful pause waits for the active slice and never launches the dependent slice.
  const graceful = await propose('raceengineer', 2);
  await request('/api/orchestrator/approve', { projectId: 'raceengineer', megaTaskId: graceful });
  const currentWorker = launches.at(-1), beforePause = launches.length;
  await request('/api/orchestrator/pause', { projectId: 'raceengineer', megaTaskId: graceful });
  assert.equal(live.has(currentWorker.pid), true);
  finish(currentWorker); await request('/api/orchestrator');
  assert.equal(launches.length, beforePause);
  const gracefulState = await request('/api/execution?projectId=raceengineer'), gracefulCP = gracefulState.checkpoints.at(-1);
  assert.equal(gracefulState.megaTasks.at(-1).status, 'PAUSED');
  await request('/api/orchestrator/resume', { projectId: 'raceengineer', megaTaskId: graceful, checkpointId: gracefulCP.id });
  assert.equal(launches.length, beforePause + 1); finish(launches.at(-1), 'BLOCKED');
  await request('/api/orchestrator');
  const blocked = await request('/api/execution?projectId=raceengineer');
  assert.equal(blocked.subTasks.at(-1).status, 'BLOCKED');
  assert.equal(blocked.megaTasks.at(-1).status, 'PAUSED');
  const blockedCP = blocked.checkpoints.at(-1), remainingSub = blocked.subTasks.at(-1);
  await request('/api/orchestrator/resume', { projectId: 'raceengineer', megaTaskId: graceful, checkpointId: blockedCP.id, assignments: { [remainingSub.id]: 'fixture-3' } });
  assert.equal(launches.at(-1).worker.id, 'fixture-3', 'Explicit reassignment keeps the same SubTask');
  killAllowed = false;
  await request('/api/orchestrator/stop', { projectId: 'raceengineer', megaTaskId: graceful }, 400);
  assert.equal((await request('/api/orchestrator?readOnly=1')).projects[0].workerAlive, true, 'Failed termination does not fabricate an inactive process');
  killAllowed = true; finish(launches.at(-1), 'FAILED');
  await request('/api/orchestrator');
  const failed = await request('/api/execution?projectId=raceengineer');
  assert.equal(failed.subTasks.at(-1).status, 'FAILED');
  assert.equal(failed.results.at(-1).status, 'FAILED');
  // Simulate a crash by restoring the saved running state in this isolated notebook.
  const aiState = await request('/api/execution?projectId=ai-companion-desktop'), aiCP = aiState.checkpoints.find(cp => cp.megaTaskId === mtAI);
  await request('/api/orchestrator/resume', { projectId: 'ai-companion-desktop', megaTaskId: mtAI, checkpointId: aiCP.id });
  const savedState = fs.readFileSync(path.join(attemptsDir, 'orchestrator.json')), savedAttempts = fs.readFileSync(path.join(attemptsDir, 'attempts.json'));
  const crashed = launches.at(-1); live.delete(crashed.pid);
  await new Promise(resolve => server.close(resolve)); server = null;
  fs.writeFileSync(path.join(attemptsDir, 'orchestrator.json'), savedState); fs.writeFileSync(path.join(attemptsDir, 'attempts.json'), savedAttempts);
  const beforeRestart = launches.length; await start();
  const repaired = await request('/api/orchestrator?readOnly=1');
  assert.equal(repaired.projects.find(row => row.id === 'ai-companion-desktop').megaTask.status, 'FAILED');
  assert.equal(launches.length, beforeRestart, 'Restart reconciliation never dispatches a stale dead attempt');
  await request('/api/focus', { projectId: 'ai-companion-desktop' });
  const untouchedRace = JSON.stringify(await request('/api/execution?projectId=raceengineer'));
  const workOrder = await request('/api/orchestrator/ask', { question: 'Audit Sayaku input handling and prepare a bounded work order.' });
  assert.equal(JSON.parse(fs.readFileSync(file)).projects[1].tasks.at(-1).projectId, 'ai-companion-desktop', 'Shared focus is the default operational recipient');
  assert.ok(workOrder.answer.includes('New task prepared from this message'));
  assert.equal(JSON.stringify(await request('/api/execution?projectId=raceengineer')), untouchedRace, 'Sayaku work order cannot touch RaceEngineer attempts or checkpoints');
  assert.equal(launches.length, beforeRestart, 'New work orders wait for their MegaTask approval');
  console.log('PASS Phase 2: proposal/approval binding, dependencies, targeting/focus, structured completion, stop/checkpoint, explicit resume, external changes, alternate lead, stable SubTasks/fresh Attempts, Home/timeline, dead-PID failure.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (server?.listening) await new Promise(resolve => server.close(resolve));
  const resolved = path.resolve(dir); assert.ok(resolved.startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(resolved).startsWith('pw-phase2-'));
  fs.rmSync(resolved, { recursive: true, force: true });
});
