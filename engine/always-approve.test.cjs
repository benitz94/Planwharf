'use strict';
// "Always approve" is opt-in: a fresh install never lets a crew run commands without the user's OK.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { launchSpec } = require('./provider-runtime.cjs');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

const roster = [
  { id: 'opus', provider: 'claude', cli: 'claude-opus-4-1', plan: 'Claude', enabled: true, kind: 'premium' },
  { id: 'luna', provider: 'codex', cli: 'gpt-6-luna', plan: 'ChatGPT', enabled: true, kind: 'default' },
  { id: 'grok', provider: 'grok', cli: 'grok-4.7', plan: 'Grok', enabled: true, kind: 'default' }
];

function withExecutables(run) {
  const keys = ['PLANWHARF_CLAUDE', 'PLANWHARF_CODEX', 'PLANWHARF_GROK'];
  const saved = Object.fromEntries(keys.map(key => [key, process.env[key]]));
  for (const key of keys) process.env[key] = process.execPath;
  try { return run(); } finally { for (const key of keys) if (saved[key] === undefined) delete process.env[key]; else process.env[key] = saved[key]; }
}

test('a fresh install approves nothing by itself', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-approve-default-'));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, models: roster, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => [] });
  const view = await orchestra.snapshot();
  assert.equal(view.models.every(model => model.alwaysApprove === false), true, 'no model works without asking by default');
  assert.equal(view.providers.every(provider => provider.policy.alwaysApprove === false), true);
  // The user's OK is saved per AI tool and kept after a restart.
  assert.equal((await orchestra.setProviderPolicy('claude', { alwaysApprove: true })).ok, true);
  const again = await createOrchestrator({ dir, readPlan: async () => plan, models: roster, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => [] }).snapshot();
  assert.equal(again.models.find(model => model.id === 'opus').alwaysApprove, true);
  assert.equal(again.models.find(model => model.id === 'luna').alwaysApprove, false);
});

test('a worker that needs the OK does not start without it; a lead reads without it', () => withExecutables(() => {
  for (const provider of ['claude', 'grok']) {
    const model = { id: provider, provider, cli: provider === 'claude' ? 'claude-opus-4-1' : 'grok-4.7' };
    assert.throws(() => launchSpec(model, os.tmpdir(), 'Fixture'), /PERMISSION_NEEDED/, provider + ' worker');
    const lead = launchSpec(model, os.tmpdir(), 'Fixture', true);
    assert.equal(lead.args[lead.args.indexOf('--permission-mode') + 1], 'dontAsk', provider + ' lead stays restricted');
    const allowed = launchSpec({ ...model, policy: { alwaysApprove: true } }, os.tmpdir(), 'Fixture');
    assert.equal(allowed.args[allowed.args.indexOf('--permission-mode') + 1], 'bypassPermissions');
  }
  // Codex works inside its sandbox without the OK: the project folder only, no network.
  const codex = launchSpec({ id: 'luna', provider: 'codex', cli: 'gpt-6-luna' }, os.tmpdir(), 'Fixture');
  assert.ok(codex.args.includes('sandbox_workspace_write.network_access=false'));
  assert.ok(!codex.args.includes('--dangerously-bypass-approvals-and-sandbox'));
}));
