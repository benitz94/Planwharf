'use strict';
const assert = require('node:assert/strict');
const { createProviderModels } = require('./provider-models.cjs');
(async () => {
  const models = [{ id: 'grok-fixture', provider: 'grok', cli: 'grok-fixture', available: true, enabled: true }, { id: 'other', provider: 'grok', cli: 'grok-other', available: false }];
  let result = { ok: true, ids: ['grok-fixture'] };
  const discovery = createProviderModels(models, { discover: async () => result });
  await discovery.refresh(true);
  assert.equal(models[0].availabilityVerified, true);
  result = { ok: false, reason: 'Fixture temporary quota or policy failure.' };
  const failed = await discovery.refresh(true);
  assert.equal(failed.status, 'unread');
  assert.equal(models[0].available, true, 'Failed check cannot revoke previously verified exact-model availability');
  assert.equal(models[0].enabled, true);
  assert.equal(models[0].availabilityVerified, false);
  assert.equal(models[1].available, false, 'Failed check cannot fabricate access to an unverified model');
  assert.equal(models[0].cli, 'grok-fixture');
  result = { ok: true, ids: ['grok-other'] };
  await discovery.refresh(true);
  assert.equal(models[0].available, false);
  assert.equal(models[1].available, true);
  const catalogModels = [
    { id: 'codex-isolated', providerId: 'codex-test', cli: 'gpt-isolated', available: false, unavailableReason: 'Old rejected model probe', roles: { worker: false, reviewer: true } },
    { id: 'grok-isolated', providerId: 'grok-test', cli: 'grok-isolated', available: false }
  ];
  const catalogProviders = [{ id: 'codex-test', adapter: 'codex' }, { id: 'grok-test', adapter: 'grok' }];
  const inspected = [];
  let catalogReadFails = false;
  const isolated = createProviderModels(catalogModels, { providers: catalogProviders, discover: async provider => {
    inspected.push(provider.id);
    if (catalogReadFails) return { ok: false, reason: 'Fixture catalog temporarily unavailable.' };
    return { ok: true, ids: catalogModels.filter(model => model.providerId === provider.id).map(model => model.cli) };
  } });
  await isolated.refresh(true);
  assert.deepEqual(inspected, ['codex-test', 'grok-test'], 'Injected discovery must isolate every provider from real accounts');
  assert.ok(catalogModels.every(model => model.available));
  assert.equal(catalogModels.length, 2, 'Catalogs must not mix exact model IDs between providers');
  assert.equal(catalogModels[0].unavailableReason, null, 'Fresh startup catalog supersedes an old model rejection');
  assert.equal(catalogModels[0].availabilityVerified, true);
  assert.ok(catalogModels[0].availabilityCheckedAt);
  assert.deepEqual(catalogModels[0].roles, { worker: false, reviewer: true }, 'Discovery never changes user role permissions');
  await isolated.refresh(); assert.equal(inspected.length, 2, 'Status cache avoids repeated provider invocations');
  catalogReadFails = true;
  await isolated.refresh(true);
  assert.equal(catalogModels[0].available, true, 'Failed check retains the last known availability');
  assert.equal(catalogModels[0].availabilityVerified, false);
  assert.match(catalogModels[0].availabilityWarning, /temporarily unavailable/);
  assert.equal(isolated.snapshot().providers[0].status, 'unread');

  const fs = require('node:fs'), path = require('node:path'), os = require('node:os');
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-startup-models-'));
  const planFile = path.join(dir, 'notebook.json');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  const cacheFile = path.join(dir, 'catalogs.json');
  const fixtureProviders = ['codex', 'grok', 'antigravity'].map(adapter => ({ id: adapter + '-cache', adapter, account: { key: 'isolated-account' }, label: adapter }));
  const freshModels = () => fixtureProviders.flatMap(provider => [
    { id: provider.id + '-retired', providerId: provider.id, cli: provider.adapter + '-retired', available: true, enabled: true, roles: { orchestrator: true, worker: false, reviewer: true } },
    { id: provider.id + '-current', providerId: provider.id, cli: provider.adapter + '-current', available: false, enabled: true }
  ]);
  const firstModels = freshModels();
  const firstBoot = createProviderModels(firstModels, { providers: fixtureProviders, cacheFile, discover: async provider => ({ ok: true, ids: [provider.adapter + '-current', provider.adapter + '-new'] }) });
  await firstBoot.refresh(true);
  assert.ok(firstModels.filter(model => model.id.endsWith('-retired')).every(model => model.catalogExcluded && !model.available));
  assert.ok(firstModels.filter(model => model.cli.endsWith('-new')).every(model => model.enabled === false && model.available), 'New exact IDs require explicit user role selection');
  const cachedText = fs.readFileSync(cacheFile, 'utf8');
  assert.ok(!cachedText.includes('isolated-account'), 'Account identity is hashed in catalog cache keys');
  const restartedModels = freshModels();
  const offlineBoot = createProviderModels(restartedModels, { providers: fixtureProviders, cacheFile, discover: async () => ({ ok: false, reason: 'Isolated network failure' }) });
  await offlineBoot.refresh(true);
  assert.ok(restartedModels.filter(model => model.id.endsWith('-retired')).every(model => model.catalogExcluded && !model.available), 'Offline restart cannot resurrect retired models');
  assert.ok(restartedModels.filter(model => model.cli.endsWith('-new')).every(model => model.available && !model.enabled), 'Previously discovered IDs survive restart without automatic roles');
  assert.ok(restartedModels.every(model => !model.availabilityVerified));
  assert.deepEqual(restartedModels[0].roles, { orchestrator: true, worker: false, reviewer: true });
  const changedAccountModels = freshModels();
  const changedAccount = createProviderModels(changedAccountModels, { providers: fixtureProviders.map(provider => ({ ...provider, account: { key: 'different-account' } })), cacheFile, discover: async () => ({ ok: false, reason: 'Isolated sign-in failure' }) });
  await changedAccount.refresh(true);
  assert.ok(!changedAccountModels.some(model => model.cli.endsWith('-new')), 'A different account cannot inherit discovered access from a cached login');
  let catalogReply = { ok: true, models: [{ cli: '' }] };
  const validation = createProviderModels(restartedModels, { providers: fixtureProviders, discover: async () => catalogReply });
  await validation.refresh(true);
  assert.equal(validation.snapshot().providers[0].status, 'unread');
  assert.ok(restartedModels.find(model => model.cli === 'codex-current').available, 'Malformed success cannot revoke current access');
  catalogReply = { ok: true, ids: [] };
  await validation.refresh(true);
  assert.ok(restartedModels.every(model => model.catalogExcluded && !model.available), 'Explicit empty catalogs revoke every listed route');
  catalogReply = { ok: true, ids: ['codex-retired'] };
  await validation.refresh(true, 'codex-cache');
  assert.equal(restartedModels[0].catalogExcluded, false, 'A returned exact ID becomes selectable again');
  assert.deepEqual(restartedModels[0].roles, { orchestrator: true, worker: false, reviewer: true });
  let releaseDiscovery, startupCalls = 0;
  const pending = new Promise(resolve => { releaseDiscovery = resolve; });
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), skillsEnabled: false, fetchPlans: null, refreshModels: true,
    models: [{ id: 'gpt-5', cli: 'gpt-5', provider: 'codex', enabled: false, available: false, unavailableReason: 'Old rejected model probe' }, { id: 'retired-startup', cli: 'gpt-retired', provider: 'codex', enabled: true, available: true }],
    discoverModels: () => { startupCalls++; return pending; } });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = 'http://127.0.0.1:' + server.address().port;
    for (let n = 0; n < 3; n++) assert.equal((await (await fetch(origin + '/api/models/status')).json()).checking, true);
    let snapshotReturned = false;
    const response = fetch(origin + '/api/orchestrator?readOnly=1').then(value => { snapshotReturned = true; return value.json(); });
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.equal(snapshotReturned, false, 'Initial snapshot must wait for the fresh startup check');
    releaseDiscovery({ ok: true, ids: ['gpt-5'] });
    const checked = await response;
    const model = checked.models.find(item => item.id === 'gpt-5');
    assert.equal(model.available, true); assert.equal(model.unavailableReason, null);
    assert.equal(model.availabilityVerified, true);
    assert.equal(checked.models.find(item => item.id === 'retired-startup').catalogExcluded, true);
    assert.equal(checked.models.find(item => item.id === 'retired-startup').available, false);
    assert.equal(checked.providerModels.checking, false);
    assert.equal(startupCalls, 1, 'Startup and UI polling perform only one catalog check');
  } finally {
    releaseDiscovery({ ok: false, reason: 'Test cleanup' });
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
  console.log('OK: exact Grok model discovery, preserved access on failed checks and no invented provider/model substitutions.');
})().catch(error => { console.error(error); process.exitCode = 1; });

const test = require('node:test');
function scopedCatalogFixture(t) {
  const fs = require('node:fs'), path = require('node:path'), os = require('node:os');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-catalog-scope-')); t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const provider = { id: 'scope-fixture', adapter: 'codex', account: { key: 'account-b-fixture' } };
  const freshModels = () => [{ id: 'exact-fixture', providerId: provider.id, cli: 'gpt-fixture', available: false, enabled: true, roles: { worker: true, reviewer: false } }];
  return { fs, path, dir, provider, freshModels, cacheFile: path.join(dir, 'catalog.json') };
}
test('account invalidation survives restart and cannot restore cached access until fresh discovery succeeds', async t => {
  const f = scopedCatalogFixture(t), providers = [f.provider];
  const seed = createProviderModels(f.freshModels(), { providers, cacheFile: f.cacheFile, discover: async () => ({ ok: true, ids: ['gpt-fixture'] }) }); await seed.refresh(true);
  const models = f.freshModels(), active = createProviderModels(models, { providers, cacheFile: f.cacheFile, discover: async () => ({ ok: false, reason: 'Synthetic catalog outage' }) });
  await active.invalidate(f.provider.id); await active.refresh(true);
  assert.equal(models[0].available, false); assert.equal(active.snapshot().providers[0].preservingPreviousAvailability, false);
  const restarted = f.freshModels(); let reply = { ok: false, reason: 'Still unavailable' };
  const next = createProviderModels(restarted, { providers, cacheFile: f.cacheFile, discover: async () => reply }); await next.refresh(true);
  assert.equal(restarted[0].available, false, 'A second restart cannot revive historical access for the invalidated login');
  reply = { ok: true, ids: ['gpt-fixture'] }; await next.refresh(true); assert.equal(restarted[0].available, true); assert.equal(restarted[0].availabilityVerified, true);
  assert.deepEqual(restarted[0].roles, { worker: true, reviewer: false });
  const cached = f.freshModels(), offline = createProviderModels(cached, { providers, cacheFile: f.cacheFile, discover: async () => ({ ok: false, reason: 'Same-account temporary outage' }) }); await offline.refresh(true);
  assert.equal(cached[0].available, true, 'Freshly reverified unchanged-account cache remains usable offline');
  assert.doesNotMatch(f.fs.readFileSync(f.cacheFile, 'utf8'), /account-b-fixture/, 'Account scope is persisted only as a digest');
});
test('an old-account discovery cannot grant access after a switch and queued refresh checks the active account', async t => {
  const f = scopedCatalogFixture(t); f.provider.account.key = 'account-a-fixture';
  const models = f.freshModels(); let release, started; const entered = new Promise(resolve => { started = resolve; }); let calls = 0;
  const discovery = createProviderModels(models, { providers: [f.provider], cacheFile: f.cacheFile, discover: async provider => {
    calls++; if (calls === 1) { started(); return await new Promise(resolve => { release = resolve; }); }
    assert.equal(provider.account.key, 'account-b-fixture'); return { ok: false, reason: 'Current account requires fresh authorization' };
  } });
  const pending = discovery.refresh(true); await entered;
  f.provider.account.key = 'account-b-fixture'; await discovery.invalidate(f.provider.id);
  const current = discovery.refresh(true, f.provider.id); release({ ok: true, ids: ['gpt-fixture', 'gpt-a-only'] }); await Promise.all([pending, current]);
  assert.equal(calls, 2); assert.equal(models[0].available, false); assert.equal(models.some(model => model.cli === 'gpt-a-only'), false); assert.equal(discovery.snapshot().providers[0].status, 'unread');
});
test('same-account return and changed configuration reject old in-flight generation results', async t => {
  const f = scopedCatalogFixture(t), models = f.freshModels(); let release, started; const entered = new Promise(resolve => { started = resolve; }); let calls = 0;
  const discovery = createProviderModels(models, { providers: [f.provider], discover: async () => { calls++; if (calls === 1) { started(); return await new Promise(resolve => { release = resolve; }); } return { ok: false, reason: 'Fresh check failed' }; } });
  const pending = discovery.refresh(true); await entered;
  await discovery.invalidate(f.provider.id); // Identity can return to the same key while the old request is pending.
  const fresh = discovery.refresh(true, f.provider.id); release({ ok: true, ids: ['gpt-fixture', 'gpt-stale'] }); await Promise.all([pending, fresh]);
  assert.equal(models[0].available, false); assert.equal(models.some(model => model.cli === 'gpt-stale'), false); assert.equal(calls, 2);
  f.provider.adapter = 'configured-cli'; discovery.snapshot(); assert.equal(models[0].available, false, 'Configuration changes invalidate access even before the next discovery');
});
test('failed durable invalidation rejects without allowing pending stale discovery to clear the block', async t => {
  const f = scopedCatalogFixture(t), models = f.freshModels(); models[0].available = true;
  // A directory at the exact cache-file path reliably rejects atomic replacement.
  f.fs.mkdirSync(f.cacheFile); let release, started; const entered = new Promise(resolve => { started = resolve; });
  const discovery = createProviderModels(models, { providers: [f.provider], cacheFile: f.cacheFile, discover: async () => { started(); return await new Promise(resolve => { release = resolve; }); } });
  const pending = discovery.refresh(true); await entered;
  await assert.rejects(discovery.invalidate(f.provider.id), /invalidation could not be persisted/);
  assert.equal(models[0].available, false);
  release({ ok: true, ids: ['gpt-fixture', 'gpt-stale'] }); await pending;
  assert.equal(models[0].available, false); assert.equal(models.some(model => model.cli === 'gpt-stale'), false);
});
