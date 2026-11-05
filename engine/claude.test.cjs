'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { parseModels, listClaude } = require('./claude.cjs');
const { createAskStream } = require('./runner.cjs');
const { launchSpec, executable } = require('./providers.cjs');
const { adapterFor } = require('./provider-adapters.cjs');
const { modelRouteError } = require('./model-routing.cjs');
const { loadCatalog } = require('./provider-catalog.cjs');
const cli = 'claude-sonnet-fixture';

test('catalog resolves CLI aliases only from explicit resolvedModel, deduplicates and rejects unsafe IDs', () => {
  const result = parseModels([{ value: 'default', resolvedModel: cli, displayName: 'Default' }, { value: 'sonnet', resolvedModel: cli, displayName: 'Sonnet' }, { value: 'opus' }, { value: 'claude-bad\n' }, { value: 'gpt-fixture' }, { value: 'claude-' + 'x'.repeat(513) }]);
  assert.equal(result.ok, true);
  assert.deepEqual(result.models.map(m => [m.cli, m.label]), [[cli, 'Sonnet']]);
  assert.equal(parseModels([]).ok, false);
  assert.equal(parseModels(null).ok, false);
});
test('missing CLI and unauthenticated login never enumerate or claim model access', async () => {
  assert.equal((await listClaude({ command: null })).ok, false);
  let calls = 0;
  const result = await listClaude({ command: 'fixture', run: (_command, args, options, done) => {
    calls++; assert.deepEqual(args, ['auth', 'status']); assert.ok(options.timeout > 0);
    done(null, '{"loggedIn":false}');
  } });
  assert.equal(calls, 1); assert.equal(result.ok, false);
});
test('native launch preserves long Unicode stdin and explicit permissions without fallback', () => {
  const saved = process.env.PLANWHARF_CLAUDE;
  process.env.PLANWHARF_CLAUDE = process.execPath;
  try {
    const prompt = 'Unicode è 日本語 🛟\n'.repeat(10000);
    for (const alwaysApprove of [true, false]) {
      const spec = launchSpec({ provider: 'claude', cli, alwaysApprove }, '.', prompt, true);
      assert.equal(spec.stdin, prompt); assert.ok(!spec.args.includes(prompt));
      assert.equal(spec.args[spec.args.indexOf('--model') + 1], cli);
      assert.equal(spec.args[spec.args.indexOf('--permission-mode') + 1], alwaysApprove ? 'bypassPermissions' : 'dontAsk');
      assert.ok(spec.args.includes('--strict-mcp-config')); assert.ok(spec.args.includes('--no-session-persistence'));
      assert.ok(!spec.args.includes('--fallback-model')); assert.ok(!spec.args.includes('--bare'));
    }
    assert.throws(() => launchSpec({ provider: 'claude', cli }, '.', 'probe', false, true), /Research source protection/);
    process.env.PLANWHARF_CLAUDE = __dirname;
    assert.equal(executable('claude'), null, 'Invalid explicit executable must not silently fall back to the installed CLI');
  } finally { if (saved === undefined) delete process.env.PLANWHARF_CLAUDE; else process.env.PLANWHARF_CLAUDE = saved; }
  assert.match(modelRouteError({ provider: 'claude', cli: 'sonnet' }), /mismatch/);
  const catalog = loadCatalog(require('../models.json'));
  const provider = catalog.providers.find(p => p.adapter === 'claude');
  assert.ok(provider); assert.ok(provider.models.every(m => !m.enabled && !m.available));
});
test('stream requires final success and exact identity; extracts observed usage', () => {
  const stream = createAskStream(cli);
  const text = [
    { type: 'system', subtype: 'init', model: cli },
    { type: 'assistant', message: { model: cli, content: [{ type: 'text', text: 'partial' }] } },
    { type: 'result', subtype: 'success', is_error: false, result: 'final', usage: { input_tokens: 10, output_tokens: 20, cache_read_input_tokens: 5 } }
  ].map(row => JSON.stringify(row)).join('\n');
  for (const chunk of text.match(/.{1,37}/gs)) stream.push(chunk);
  const out = stream.finish(); assert.equal(out.failure, undefined); assert.equal(out.answer, 'final');
  assert.deepEqual(out.tokens, { input: 10, output: 20, cachedInput: 5 });
  for (const rows of [[], [{ type: 'system', subtype: 'init', model: cli }], [{ type: 'result', subtype: 'success', is_error: false, result: 'no identity' }]]) {
    const missing = createAskStream(cli); rows.forEach(row => missing.push(JSON.stringify(row) + '\n'));
    assert.equal(missing.finish().failure, 'PROVIDER_PROTOCOL_CHANGED');
  }
  const wrong = createAskStream(cli);
  wrong.push(JSON.stringify({ type: 'assistant', message: { model: 'claude-other', content: [] } }) + '\n');
  assert.equal(wrong.finish().failure, 'PROVIDER_MODEL_IDENTITY_CHANGED');
  const failed = createAskStream(cli);
  failed.push(JSON.stringify({ type: 'result', subtype: 'error_max_turns', is_error: true, errors: ['Turn limit'] }) + '\n');
  assert.ok(failed.finish().failure);
  for (const errors of [null, 'malformed', [null, 'Turn limit']]) {
    const malformed = createAskStream(cli);
    malformed.push(JSON.stringify({ type: 'result', subtype: 'error_max_turns', is_error: true, errors }) + '\n');
    assert.equal(malformed.finish().failure, 'provider'); assert.ok(malformed.finish().error);
  }
});
test('unsupported source-protected research remains unavailable', () => {
  assert.equal(adapterFor('claude').sourceProtectedResearch, false);
});

test('isolated notebook exposes Claude catalog and persists explicit roles without dispatch', async t => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-claude-http-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const planFile = path.join(dir, 'notebook.json');
  fs.copyFileSync(path.join(__dirname, '../fixtures/starter-notebook.json'), planFile);
  const before = fs.readFileSync(planFile);
  const provider = { id: 'claude', label: 'Claude Code', adapter: 'claude', quotaPoolId: 'Claude', enabled: true, models: [] };
  let discoveries = 0;
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), providers: [provider], refreshModels: true,
    skillsEnabled: false, syncSkills: false, fetchPlans: async () => [require('./claude-usage.cjs').parseClaudeUsage({ five_hour: { utilization: 18 }, seven_day: { utilization: 16 } })],
    discoverModels: async p => { assert.equal(p.adapter, 'claude'); discoveries++; return parseModels([{ value: 'sonnet', resolvedModel: cli, displayName: 'Fixture Sonnet' }]); },
    spawnWorker: () => { throw Error('Inspection must never launch a worker'); }, askModel: () => { throw Error('Inspection must never invoke inference'); } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const base = 'http://127.0.0.1:' + server.address().port;
  const response = await fetch(base + '/api/orchestrator'); assert.equal(response.status, 200);
  const state = await response.json();
  const model = state.models.find(m => m.cli === cli);
  assert.equal(state.plans.find(p => p.id === 'Claude').leftPct, 82);
  assert.equal(state.providers.find(p => p.id === 'claude').availability, 'AVAILABLE');
  assert.ok(model); assert.equal(model.provider, 'claude'); assert.equal(model.adapter, 'claude');
  assert.equal(model.available, true); assert.equal(model.enabled, false); assert.equal(discoveries, 1);
  assert.equal(model.roleEligibility.orchestrator, true); assert.equal(model.roleEligibility.reviewer, true);
  assert.equal(model.roleEligibility.supervisor, false); assert.equal(model.capabilities.canResearch, false);
  const roles = { orchestrator: true, worker: true, reviewer: false, supervisor: false };
  const saved = await fetch(base + '/api/models/roles', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: model.id, roles }) });
  assert.equal(saved.status, 200);
  const enabled = (await saved.json()).models.find(m => m.id === model.id);
  assert.equal(enabled.enabled, true); assert.deepEqual(enabled.roles, roles);
  assert.ok(fs.readFileSync(planFile).equals(before), 'Catalog and role changes leave the isolated notebook untouched');
});
