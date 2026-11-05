'use strict';

const assert = require('node:assert/strict');
const { modelRouteError, resolveCliModel } = require('./model-routing.cjs');
const { normalizeModels, pickWorker } = require('./orchestrator.js');
const { launchSpec } = require('./providers.cjs');
const { cliModelOf } = require('./runner.cjs');

for (const [provider, cli] of [['codex', 'gpt-6-astra'], ['grok', 'grok-4.6'], ['antigravity', 'claude-opus-future'], ['antigravity', 'gemini-future'], ['claude', 'claude-sonnet-fixture']]) {
  assert.equal(resolveCliModel({ provider, cli }), cli);
}
for (const cli of ['qwen/future-exact-id', 'kimi-future-exact-id', 'Claude/custom-id', 'new-vendor:model+version']) {
  const entry = { id: 'fixture', provider: 'antigravity', cli, modelRouting: 'configured', enabled: true };
  assert.equal(modelRouteError(entry), null);
  assert.equal(resolveCliModel(entry), cli);
  const normalized = normalizeModels([entry])[0];
  assert.equal(normalized.cli, cli);
  assert.equal(normalized.provider, 'antigravity');
  assert.equal(normalized.modelRouting, 'configured');
  assert.equal(normalized.enabled, true);
  assert.equal(pickWorker([entry]).cli, cli, 'Selection preserves the exact configured route');
}
const foreign = { id: 'fixture', provider: 'codex', cli: 'grok-4.6', enabled: true };
assert.match(modelRouteError(foreign), /mismatch/);
const invalid = normalizeModels([foreign])[0];
assert.equal(invalid.available, false);
assert.equal(invalid.enabled, false);
assert.match(invalid.unavailableReason, /mismatch/);
assert.equal(pickWorker([foreign]), null);
for (const provider of ['qwen', 'kimi', '__proto__', 'bogus']) {
  assert.match(modelRouteError({ provider, cli: 'any-exact-id', modelRouting: 'configured' }), /unsupported provider/);
}
assert.equal(resolveCliModel({ id: 'grok-label', cli: 'grok-4.6' }), null, 'Labels and IDs never infer a provider');
assert.equal(resolveCliModel({ provider: 'codex', cli: 'gpt-future', available: false }), null);
assert.equal(modelRouteError({ provider: 'codex', cli: 'gpt-future', available: false, unavailableReason: 'Account rejected this model' }), 'Account rejected this model');
assert.equal(normalizeModels([{ id: 'legacy', enabled: true }])[0].enabled, true, 'Roster-only legacy fixtures remain compatible');
for (const cli of ['', ' ', ' qwen', 'qwen ', 'qwen\n', 'qwen\x00', 'x'.repeat(513), '界'.repeat(171)]) {
  assert.ok(modelRouteError({ provider: 'antigravity', cli, modelRouting: 'configured' }));
}
assert.match(modelRouteError({ provider: 'codex', cli: 'gpt-future', modelRouting: 'guess' }), /Invalid modelRouting/);
const savedExecutable = process.env.PLANWHARF_ANTIGRAVITY;
try {
  process.env.PLANWHARF_ANTIGRAVITY = process.execPath;
  for (const cli of ['qwen/future-exact-id', 'kimi-future-exact-id', 'claude/custom-id']) {
    const configured = normalizeModels([{ id: 'generic', label: 'Any display label', provider: 'antigravity', cli, modelRouting: 'configured', enabled: true }])[0];
    assert.equal(cliModelOf(configured), cli);
    const spec = launchSpec(configured, '.', 'Isolated fixture prompt');
    assert.equal(spec.provider, 'antigravity');
    assert.equal(spec.model, cli);
    assert.equal(spec.args[spec.args.indexOf('--model') + 1], cli);
  }
} finally {
  if (savedExecutable === undefined) delete process.env.PLANWHARF_ANTIGRAVITY;
  else process.env.PLANWHARF_ANTIGRAVITY = savedExecutable;
}
console.log('OK: configured exact model IDs, explicit adapters, compatibility guards, unavailable routes and bounded IDs.');
