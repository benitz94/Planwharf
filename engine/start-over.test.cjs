'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const Plan = require('../plan.js');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

test('Start over keeps the project and archives its history; nothing is deleted', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-start-over-'));
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-start-over-folder-'));
  let plan = Plan.example();
  for (const id of ['alpha', 'beta']) plan = Plan.createProject(plan, { name: id, direction: 'Build ' + id, folder, github: 'https://github.com/you/' + id });
  plan = Plan.removeProject(plan, { id: plan.projects[0].project.id });
  assert.deepEqual(plan.projects.map(item => item.project.id), ['alpha', 'beta']);
  fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({ orchestratorId: 'fixture', asks: [
    { projectId: 'alpha', question: 'alpha-old-question', answer: 'alpha-old-answer', at: '2026-11-08T10:00:00Z' },
    { projectId: 'beta', question: 'beta-question', answer: 'beta-answer', at: '2026-11-08T10:00:00Z' }
  ], projectTeams: { alpha: { orchestratorId: 'fixture', roles: {} } } }));
  const orchestra = createOrchestrator({ dir, models: [{ id: 'fixture', label: 'Fixture', provider: 'codex', cli: 'gpt-6-luna', kind: 'premium', enabled: true, available: true, limit: 200 }],
    readPlan: async () => plan, attempts: createAttemptEngine({ dir }), fetchPlans: async () => [],
    inspectFolder: () => ({ git: false, names: [], fileCount: 0 }), spawnWorker: () => { throw Error('no workers'); }, askModel: async () => ({ answer: 'ok', actions: [] }) });

  const reset = Plan.resetProject(plan, { id: 'alpha', direction: 'Not set yet.' });
  const alpha = reset.projects.find(item => item.project.id === 'alpha');
  assert.deepEqual(alpha.project, plan.projects[0].project, 'same id, name, folder and GitHub link');
  assert.equal(alpha.tasks.length, 0);
  assert.equal(alpha.revisions.length, 1);
  assert.deepEqual(reset.projects[1], plan.projects[1], 'other projects are untouched');

  const archived = await orchestra.archiveProject('alpha', 'start over', { keepTeam: true, notebook: plan.projects[0] });
  assert.equal(archived.ok, true);
  plan = reset;
  const saved = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
  assert.equal(saved.asks.some(item => item.projectId === 'alpha'), false, 'the live chat starts empty');
  assert.equal(saved.asks.some(item => item.projectId === 'beta'), true, 'another project keeps its chat');
  assert.ok(saved.projectTeams.alpha, 'Start over keeps the team');
  const record = saved.projectArchive.at(-1);
  assert.equal(record.reason, 'start over');
  assert.equal(record.state.asks[0].question, 'alpha-old-question', 'the old chat is archived, not deleted');
  assert.equal(record.notebook.project.id, 'alpha');

  // Removing archives too, team included, so a new project with the same name starts clean.
  await orchestra.archiveProject('beta', 'removed', { notebook: plan.projects[1] });
  const after = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
  assert.equal(after.asks.length, 0);
  assert.equal(after.projectArchive.length, 2);
  await orchestra.shutdown();
});
