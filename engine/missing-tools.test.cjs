'use strict';
// An AI tool that is not installed is never shown as usable: its models are unavailable, with how to install it.
const test = require('node:test');
const assert = require('node:assert/strict');
const { createProviderModels } = require('./provider-models.cjs');
const { providerViews } = require('./provider-catalog.cjs');

const providers = [
  { id: 'claude', label: 'Claude Code', adapter: 'claude', enabled: true, quotaPoolId: 'claude' },
  { id: 'codex', label: 'Codex', adapter: 'codex', enabled: true, quotaPoolId: 'codex' }
];

test('a tool that is not installed has no usable models and says how to install it', async () => {
  const models = [
    { id: 'opus', providerId: 'claude', provider: 'claude', adapter: 'claude', cli: 'claude-opus', available: true, enabled: true },
    { id: 'luna', providerId: 'codex', provider: 'codex', adapter: 'codex', cli: 'gpt-6-luna', available: true, enabled: true }
  ];
  const asked = [];
  let installed = { claude: '/bin/claude', codex: null };
  const discovery = createProviderModels(models, { providers, findExecutable: adapter => installed[adapter] ?? null,
    discover: async provider => { asked.push(provider.id); return { ok: true, models: [{ cli: provider.id === 'claude' ? 'claude-opus' : 'gpt-6-luna' }] }; } });
  await discovery.refresh(true);
  assert.deepEqual(asked, ['claude'], 'a missing tool is not asked for its models');
  const luna = models.find(model => model.id === 'luna');
  assert.equal(luna.available, false);
  assert.equal(luna.notInstalled, true);
  assert.match(luna.unavailableReason, /Codex is not installed on this computer/);
  assert.match(luna.unavailableReason, /npm install -g @openai\/codex/);
  assert.equal(models.find(model => model.id === 'opus').available, true);
  const views = providerViews(providers, models);
  assert.equal(views.find(view => view.id === 'codex').availability, 'NOT_INSTALLED');
  assert.equal(views.find(view => view.id === 'codex').installed, false);
  assert.equal(views.find(view => view.id === 'claude').installed, true);
  assert.equal(discovery.snapshot().providers.find(state => state.providerId === 'codex').status, 'not-installed');

  // Installed later: the next check finds it and its models come back.
  installed = { claude: '/bin/claude', codex: '/bin/codex' };
  await discovery.refresh(true);
  assert.equal(luna.available, true);
  assert.equal(luna.notInstalled, false);
  assert.equal(providerViews(providers, models).find(view => view.id === 'codex').availability !== 'NOT_INSTALLED', true);
});
