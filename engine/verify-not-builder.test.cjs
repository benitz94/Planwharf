'use strict';
// Verify is somebody else's check: when its model refuses, the replacement is never the model that built
// the work, and a company that did not build comes first.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');

test('a Verify step whose model refuses goes to another company, never to the builder', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-verify-not-builder-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const folder = path.join(dir, 'src'); fs.mkdirSync(folder);
  const model = (id, label, provider, cli, plan) => ({ id, label, provider, cli, plan, enabled: true, available: true, cost: 1, limit: 100, kind: 'premium', providerCatalog: true, policy: { alwaysApprove: true }, capabilities: { canCode: true } });
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'p', name: 'p', folder }, revisions: [{ id: 1, direction: 'Ship', open: [] }],
    tasks: [{ id: 'job', projectId: 'p', revision_id: 1, title: 'Work', detail: 'Build it', status: 'running', executionVersion: 2, source: 'chat',
      proposedSubTasks: [{ title: 'Build it', assignedModelId: 'astra', type: 'IMPLEMENTATION', detail: 'Build it', dependsOn: [] },
        { title: 'Verify the work independently against the job', assignedModelId: 'sonnet', type: 'IMPLEMENTATION', detail: 'Check it', dependsOn: [0] }] }] }] };
  const launches = []; let pid = 100; const alive = new Set();
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, fetchPlans: async () => [], plansTtl: 0,
    models: [model('astra', 'GPT-6 Astra', 'codex', 'gpt-6-astra', 'chatgpt'), model('sonnet', 'claude-sonnet-4-6', 'antigravity', 'claude-sonnet-4-6', 'gemini'), model('gemini', 'Gemini 3.8', 'antigravity', 'gemini-3.1-pro', 'gemini'), model('grok', 'Grok 4.7', 'grok', 'grok-4.7', 'grok')],
    attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), inspectFolder: () => ({ git: false, dirty: [], names: ['readme.txt'], fileCount: 1 }),
    spawnWorker: info => {
      launches.push(info.worker.id); pid += 1;
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
      if (info.worker.id === 'sonnet') {
        fs.writeFileSync(info.logPath, 'RESOURCE_EXHAUSTED (429): You have exhausted your capacity on this model. Your quota will reset after 3h.\n');
        if (info.exitPath) fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 1, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      } else {
        fs.writeFileSync(info.logPath, 'worked\n');
        fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
        fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'ok', filesChanged: [], tests: [], blockers: [], nextAction: 'None' }));
        fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      }
      return { pid };
    }, isProcessAlive: () => false, killWorker: () => true });
  for (const id of ['astra', 'sonnet', 'gemini', 'grok']) assert.equal((await orchestra.setModelRoles(id, { worker: true, verifier: true })).ok, true);
  await orchestra.tick();
  const row = (await orchestra.snapshot()).projects.find(item => item.id === 'p');
  const approved = await orchestra.approve('p', row.megaTaskId);
  assert.equal(approved.ok, true, approved.reason);
  for (let i = 0; i < 8; i++) await orchestra.tick();
  assert.equal(launches[0], 'astra', 'Astra builds');
  assert.ok(launches.includes('sonnet'), 'the Verify model was tried: ' + launches.join(','));
  const after = launches.slice(launches.indexOf('sonnet') + 1);
  assert.ok(after.length > 0, 'another model took Verify: ' + launches.join(','));
  assert.ok(!after.includes('astra'), 'never the builder: ' + launches.join(','));
  assert.ok(['gemini', 'grok'].includes(after[0]), 'a company that did not build: ' + launches.join(','));
  const view = await orchestra.snapshot();
  assert.equal(view.projects[0].megaTask?.status, 'COMPLETE', 'and the work finishes');
});
