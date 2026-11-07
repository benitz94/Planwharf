'use strict';

// Each project keeps its own team: lead and roles. A project without its own team uses the shared one.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Plan = require('../plan.js');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-project-teams-'));
let plan = Plan.example();
plan = Plan.createProject(plan, { name: 'Alpha', direction: 'Build alpha.' });
plan = Plan.createProject(plan, { name: 'Beta', direction: 'Build beta.' });
const [alpha, beta] = plan.projects.slice(-2).map(item => item.project.id);
const models = [
  { id: 'astra', label: 'Astra', provider: 'codex', cli: 'gpt-6-astra', kind: 'premium', enabled: true, available: true, limit: 200 },
  { id: 'opus', label: 'Opus', provider: 'claude', cli: 'claude-opus-5-5', kind: 'premium', enabled: true, available: true, limit: 200 }
];
const asked = [];
const make = () => createOrchestrator({
  dir,
  models,
  readPlan: async () => plan,
  attempts: createAttemptEngine({ dir }),
  fetchPlans: async () => [],
  inspectFolder: () => ({ fileCount: 0, names: [], git: false }),
  spawnWorker: () => { throw new Error('Team validation must not dispatch workers'); },
  askModel: async info => { asked.push(info.model?.id || info.orchestrator?.id || info.lead?.id || null); return { answer: 'ok', ms: 1 }; }
});

async function main() {
  const orchestra = make();
  const rolesIn = async (projectId, id) => (await orchestra.withTeam(projectId, () => orchestra.snapshot())).models.find(model => model.id === id).roles;
  const leadIn = async projectId => (await orchestra.withTeam(projectId, () => orchestra.snapshot())).selectedOrchestratorId;

  // Shared team: both models build.
  for (const id of ['astra', 'opus']) assert.equal((await orchestra.setModelRoles(id, { worker: true, orchestrator: true })).ok, true);
  assert.equal((await orchestra.setOrchestrator('astra')).ok, true);

  // Alpha gets its own team: Opus only verifies, and leads.
  assert.equal((await orchestra.setModelRoles('opus', { worker: false, verifier: true }, alpha)).ok, true);
  assert.equal((await orchestra.setOrchestrator('opus', alpha)).ok, true);
  assert.equal((await rolesIn(alpha, 'opus')).worker, false);
  assert.equal((await rolesIn(alpha, 'opus')).verifier, true);
  assert.equal(await leadIn(alpha), 'opus');
  assert.equal((await rolesIn(alpha, 'astra')).worker, true, 'A new project team starts as a copy of the shared team');

  // Beta and the shared team are unchanged.
  assert.equal((await rolesIn(beta, 'opus')).worker, true);
  assert.equal(await leadIn(beta), 'astra');
  assert.equal((await rolesIn(undefined, 'opus')).worker, true);
  assert.equal(await leadIn(undefined), 'astra');

  // The lead that answers in a project chat is that project's lead.
  const answer = await orchestra.ask('Who leads here?', alpha);
  assert.notEqual(answer.ok, false, JSON.stringify(answer).slice(0, 300));
  const store = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
  assert.equal(store.asks.find(item => item.projectId === alpha)?.orchestratorId, 'opus');

  // Teams survive a restart.
  const again = make();
  assert.equal((await again.withTeam(alpha, () => again.snapshot())).selectedOrchestratorId, 'opus');
  assert.equal((await again.withTeam(beta, () => again.snapshot())).selectedOrchestratorId, 'astra');
  console.log('project-teams: ok');
}

main().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => { try { fs.rmSync(dir, { recursive: true, force: true }); } catch {} });
