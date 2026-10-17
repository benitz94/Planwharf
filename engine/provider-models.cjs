'use strict';
const { execFile } = require('node:child_process');
const { executable } = require('./providers.cjs');
function listGrok() {
  return new Promise(resolve => {
    const command = executable('grok');
    if (!command) return resolve({ ok: false, reason: 'Grok CLI executable not found.' });
    execFile(command, ['models'], { windowsHide: true, timeout: 15000, maxBuffer: 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) {
        const policy = /policy lock file[\s\S]*not accessible/i.test(stdout + stderr);
        return resolve({ ok: false, reason: policy ? 'Grok CLI cannot access its managed policy lock file. Check the CLI permissions and sign-in, then refresh models.' : 'Grok CLI could not list models for the active login. Check CLI sign-in and refresh models.' });
      }
      const ids = [...new Set((stdout.replace(/\x1b\[[0-9;]*m/g, '').match(/\bgrok-[a-zA-Z0-9][a-zA-Z0-9._:-]*/g) || []))];
      resolve(ids.length ? { ok: true, ids } : { ok: false, reason: 'Grok CLI returned no exact model IDs for the active login.' });
    });
  });
}
function createProviderModels(models, { discover = listGrok, providers, now = Date.now, ttl = 60000, cacheFile } = {}) {
  if (providers) return createCatalogDiscovery(models, providers, { discover, now, ttl, cacheFile });
  let state = { status: 'unknown', checkedAt: null, reason: null, ids: [] }, inflight;
  async function refresh(force = false) {
    if (inflight) return inflight;
    if (!force && state.checkedAt && now() - Date.parse(state.checkedAt) < ttl) return state;
    inflight = (async () => {
      let result;
      try { result = await discover(); } catch { result = { ok: false, reason: 'Grok model discovery failed. Check CLI sign-in and refresh models.' }; }
      const ids = result.ok && Array.isArray(result.ids) ? result.ids.filter(id => typeof id === 'string' && /^grok-[\w.:-]+$/.test(id)) : [];
      for (const model of models.filter(model => model.provider === 'grok')) {
        model.availabilityVerified = Boolean(result.ok);
        model.availabilityWarning = result.ok ? null : result.reason;
        if (result.ok) {
          model.available = ids.includes(model.cli);
          model.unavailableReason = model.available ? null : `The active Grok login does not list the exact model ${model.cli}. No substitute was selected.`;
        }
      }
      state = { status: result.ok ? 'current' : 'unread', checkedAt: new Date(now()).toISOString(), reason: result.ok ? null : result.reason, ids: result.ok ? ids : state.ids, preservingPreviousAvailability: !result.ok };
      return state;
    })().finally(() => { inflight = null; });
    return inflight;
  }
  return { refresh, snapshot: () => ({ ...state, checking: Boolean(inflight) }) };
}
function catalogEntries(result) {
  const rows = Array.isArray(result.models) ? result.models : Array.isArray(result.ids) ? result.ids.map(cli => ({ cli })) : null;
  if (!rows || rows.some(m => !m || typeof m.cli !== 'string' || !m.cli || m.cli.trim() !== m.cli || /[\x00-\x1f\x7f]/.test(m.cli))) return null;
  return rows.map(m => ({ cli: m.cli, ...(typeof m.label === 'string' ? { label: m.label } : {}),
    ...Object.fromEntries(['orchestratorEligible', 'workerEligible'].filter(key => typeof m[key] === 'boolean').map(key => [key, m[key]])),
    capabilities: Object.fromEntries(Object.entries(m.capabilities || {}).filter(([, value]) => typeof value === 'boolean')) }));
}
function createCatalogDiscovery(models, providers, { discover, now, ttl, cacheFile }) {
  const states = new Map(), restored = new Set(), stateScopes = new Map(), generations = new Map(), observedScopes = new Map(), blockedScopes = new Set(); let inflight, saving = Promise.resolve();
  let cache = {};
  if (cacheFile) try {
    const saved = JSON.parse(require('node:fs').readFileSync(cacheFile, 'utf8'));
    if (saved.schema === 1 && saved.catalogs && typeof saved.catalogs === 'object') {
      cache = saved.catalogs;
      for (const key of saved.invalidatedScopes || []) if (typeof key === 'string' && /^[a-f0-9]{64}$/.test(key)) blockedScopes.add(key);
    }
  } catch { /* A missing or unreadable cache cannot verify provider access. */ }
  function cacheKey(provider) {
    return require('node:crypto').createHash('sha256').update(JSON.stringify([provider.id, provider.adapter, provider.account?.key || null, require('./provider-catalog.cjs').providerConfiguration(provider)])).digest('hex');
  }
  for (const provider of providers) observedScopes.set(provider.id, cacheKey(provider));
  function block(provider) {
    const key = cacheKey(provider); blockedScopes.add(key); observedScopes.set(provider.id, key);
    generations.set(provider.id, (generations.get(provider.id) || 0) + 1);
    restored.delete(key); states.delete(provider.id); stateScopes.delete(provider.id);
    for (const model of models.filter(model => model.providerId === provider.id)) {
      model.available = false; model.availabilityVerified = false;
      model.unavailableReason = 'Provider account or configuration changed. Access requires fresh discovery for the current scope.';
      model.availabilityWarning = model.unavailableReason;
    }
  }
  function invalidate(providerId) {
    for (const provider of providers.filter(provider => provider.id === providerId)) block(provider);
    return saveCache(true);
  }
  function ensureScopes(providerId) {
    let changed = false;
    for (const provider of providers.filter(provider => !providerId || provider.id === providerId)) {
      const key = cacheKey(provider);
      if (observedScopes.get(provider.id) !== key) {
        if (states.has(provider.id) || generations.has(provider.id)) { block(provider); changed = true; }
        else observedScopes.set(provider.id, key); // First identity attachment is checked by the server's persisted account binding.
      }
      if (blockedScopes.has(key)) for (const model of models.filter(model => model.providerId === provider.id)) {
        model.available = false; model.availabilityVerified = false;
        model.unavailableReason = 'Access requires fresh discovery for the current provider account or configuration.';
      }
    }
    if (changed) void saveCache();
  }
  const observeScope = providerId => ensureScopes(providerId);
  function applyCatalog(provider, entries, checkedAt, verified) {
    for (const model of models.filter(m => m.providerId === provider.id)) {
      model.available = entries.some(m => m.cli === model.cli);
      model.catalogExcluded = !model.available;
      model.unavailableReason = model.available ? null : `The ${provider.label || provider.id} catalog does not list exact model ${model.cli}. Configuration retained; no substitute selected.`;
    }
    for (const entry of entries) if (!models.some(m => m.providerId === provider.id && m.cli === entry.cli)) {
      models.push({ ...entry, id: provider.id + ':' + entry.cli, providerId: provider.id, provider: provider.id, adapter: provider.adapter, quotaPoolId: provider.quotaPoolId, plan: provider.quotaPoolId,
        label: entry.label || entry.cli, enabled: false, available: true, catalogExcluded: false, availabilityVerified: verified, availabilityCheckedAt: checkedAt, availabilityWarning: null, providerCatalog: true, orchestratorEligible: entry.orchestratorEligible ?? entry.capabilities?.canOrchestrate ?? true,
        workerEligible: entry.workerEligible ?? entry.capabilities?.canCode ?? true, capabilities: { ...entry.capabilities }, providerConfig: require('./provider-catalog.cjs').providerConfiguration(provider) });
    }
  }
  function saveCache(strict = false) {
    if (!cacheFile) return Promise.resolve();
    const pending = saving.then(async () => {
      const fs = require('node:fs/promises');
      const temporary = cacheFile + '.' + require('node:crypto').randomUUID() + '.tmp';
      try {
        await fs.mkdir(require('node:path').dirname(cacheFile), { recursive: true });
        await fs.writeFile(temporary, JSON.stringify({ schema: 1, catalogs: cache, invalidatedScopes: [...blockedScopes] }), { encoding: 'utf8', flag: 'wx' });
        await fs.rename(temporary, cacheFile);
      } catch { if (strict) throw new Error('Provider access invalidation could not be persisted. Access remains blocked; retry before accepting the account change.'); }
      finally { try { await fs.rm(temporary, { force: true }); } catch {} }
    });
    saving = pending.catch(() => {});
    return pending;
  }
  async function refresh(force = false, providerId) {
    ensureScopes();
    if (inflight) return inflight.then(() => refresh(force, providerId));
    inflight = (async () => {
      for (const provider of providers.filter(p => (!providerId || p.id === providerId) && p.enabled !== false)) {
        const key = cacheKey(provider);
        if (observedScopes.get(provider.id) !== key) { block(provider); await saveCache(); }
        const generation = generations.get(provider.id) || 0;
        if (blockedScopes.has(key)) {
          for (const model of models.filter(model => model.providerId === provider.id)) { model.available = false; model.availabilityVerified = false; model.unavailableReason = 'Access requires fresh discovery for the current provider account or configuration.'; }
        }
        if (!restored.has(key)) {
          restored.add(key);
          const saved = cache[key], entries = saved && catalogEntries({ models: saved.models });
          if (!blockedScopes.has(key) && entries && typeof saved.checkedAt === 'string') {
            applyCatalog(provider, entries, saved.checkedAt, false);
            states.set(provider.id, { providerId: provider.id, label: provider.label || provider.id, status: 'unread', checkedAt: saved.checkedAt, reason: 'Using the last verified catalog until startup discovery completes.', ids: entries.map(m => m.cli), preservingPreviousAvailability: true });
            stateScopes.set(provider.id, key);
          }
        }
        const previous = stateScopes.get(provider.id) === key ? states.get(provider.id) : null;
        if (!force && previous && now() - Date.parse(previous.checkedAt) < ttl) continue;
        let result;
        try {
          const adapter = require('./provider-adapters.cjs').adapterFor(provider);
          result = discover !== listGrok ? await discover(provider) : await adapter?.discover?.(provider);
        } catch { result = { ok: false, reason: 'Provider catalog could not be read; previous model configuration is preserved.' }; }
        if (cacheKey(provider) !== key || (generations.get(provider.id) || 0) !== generation) {
          // A completed old-account request is never evidence for the active login.
          if (cacheKey(provider) !== observedScopes.get(provider.id)) { block(provider); await saveCache(); }
          continue;
        }
        result ||= { ok: false, reason: 'Model enumeration unavailable for this adapter.' };
        const entries = catalogEntries(result);
        if (result.ok && !entries) result = { ok: false, reason: 'Provider returned an invalid model catalog; previous availability is retained.' };
        const checkedAt = new Date(now()).toISOString();
        if (result.ok) applyCatalog(provider, entries, checkedAt, true);
        for (const model of models.filter(m => m.providerId === provider.id)) {
          model.availabilityVerified = result.ok === true;
          model.availabilityCheckedAt = checkedAt;
          model.availabilityWarning = result.ok ? null : result.reason || 'Model catalog check failed; previous availability is retained.';
        }
        if (result.ok) {
          blockedScopes.delete(key);
          cache[key] = { checkedAt, models: entries };
          await saveCache();
        }
        if (cacheKey(provider) !== key || (generations.get(provider.id) || 0) !== generation) { ensureScopes(); continue; }
        states.set(provider.id, { providerId: provider.id, label: provider.label || provider.id, status: result.ok ? 'current' : 'unread', checkedAt, reason: result.ok ? null : result.reason, preservingPreviousAvailability: !result.ok && !blockedScopes.has(key), ids: result.ok ? entries.map(m => m.cli) : previous?.ids || [] });
        stateScopes.set(provider.id, key);
      }
      return snapshot();
    })().finally(() => { inflight = null; });
    return inflight;
  }
  function snapshot() { ensureScopes(); const list = providers.map(p => states.get(p.id) || { providerId: p.id, label: p.label || p.id, status: p.enabled === false ? 'disabled' : 'unknown', checkedAt: null, ids: [] }); return { providers: list, checking: Boolean(inflight), status: list.every(s => ['current', 'disabled'].includes(s.status)) ? 'current' : 'unread' }; }
  return { refresh, snapshot, invalidate, observeScope };
}
module.exports = { createProviderModels, listGrok };
