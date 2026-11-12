'use strict';
const { adapterFor } = require('./provider-adapters.cjs');
const defaults = { codex: ['ChatGPT', 'ChatGPT'], grok: ['Grok', 'Grok'], antigravity: ['Gemini', 'Gemini'], claude: ['Claude Code', 'Claude'] };
function providerConfiguration(provider) {
  const keys = ['baseUrl', 'authEnv', 'subscriptionIncluded', 'apiProtocol', 'maxOutputTokens', 'maxTurns', 'requestTimeoutMs', 'cliCommand', 'cliArgs', 'yoloArgs', 'restrictedArgs', 'outputProtocol'];
  return Object.fromEntries(keys.filter(key => provider[key] !== undefined).map(key => [key, provider[key]]));
}
function loadCatalog(config) {
  const explicit = !Array.isArray(config);
  if (explicit && (!config || config.schema !== 3 || !Array.isArray(config.providers))) throw new Error('Invalid provider catalog: expected schema 3 providers.');
  const providers = explicit ? config.providers.map(p => ({ ...p, models: [...(p.models || [])] })) : [];
  if (!explicit) for (const model of config || []) {
    const id = model.providerId || model.provider || model.plan || 'legacy';
    let provider = providers.find(p => p.id === id);
    if (!provider) { const fallback = defaults[model.provider] || [model.plan || id, model.plan || id]; provider = { id, label: fallback[0], quotaPoolId: model.quotaPoolId || fallback[1], adapter: model.adapter || model.provider, enabled: true, models: [] }; providers.push(provider); }
    provider.models.push(model);
  }
  const ids = new Set(), modelIds = new Set(), models = [];
  for (const provider of providers) {
    if (typeof provider.id !== 'string' || !provider.id || ids.has(provider.id)) throw new Error('Provider IDs must be nonempty and unique.');
    ids.add(provider.id); provider.label ||= provider.id; provider.quotaPoolId ||= provider.id; provider.enabled = provider.enabled !== false;
    for (const original of provider.models) {
      if (!original.id || modelIds.has(original.id)) throw new Error('Model IDs must be nonempty and unique.');
      modelIds.add(original.id);
      const model = { ...original, providerId: provider.id, provider: explicit || original.provider ? provider.id : undefined, adapter: provider.adapter, quotaPoolId: provider.quotaPoolId, plan: provider.quotaPoolId,
        orchestratorEligible: original.orchestratorEligible ?? original.capabilities?.orchestratorEligible ?? original.capabilities?.canOrchestrate ?? (original.kind !== 'local'), workerEligible: original.workerEligible ?? original.capabilities?.workerEligible ?? true,
        reviewerEligible: original.reviewerEligible ?? original.capabilities?.reviewerEligible ?? (original.workerEligible !== false),
        providerCatalog: explicit || original.providerCatalog === true, capabilities: { ...(adapterFor(provider)?.capabilities || {}), ...(original.capabilities || {}) }, providerConfig: providerConfiguration(provider) };
      if (explicit) { model.kind ||= 'default'; model.cost ??= 1; model.limit ??= Number.MAX_SAFE_INTEGER; }
      model.capabilities.orchestratorEligible = model.orchestratorEligible; model.capabilities.workerEligible = model.workerEligible; model.capabilities.reviewerEligible = model.reviewerEligible;
      models.push(model);
    }
    provider.models = models.filter(m => m.providerId === provider.id);
    provider.source = provider.adapter || null;
  }
  return { providers, models };
}
// How to get an AI tool that is not installed. Only commands the tools document; otherwise a plain sentence.
const INSTALL_HINTS = {
  claude: 'Install it with npm install -g @anthropic-ai/claude-code, then run claude once to sign in.',
  codex: 'Install it with npm install -g @openai/codex, then run codex once to sign in.',
  grok: 'Install the Grok CLI and sign in, then look for models again.',
  antigravity: 'Install Antigravity and sign in, then look for models again.'
};
// The program's own name: the subscription label (ChatGPT, Gemini) is not what gets installed.
const TOOL_NAMES = { claude: 'Claude Code', codex: 'Codex', grok: 'The Grok CLI', antigravity: 'Antigravity' };
const notInstalledReason = provider => `${TOOL_NAMES[provider.adapter] || provider.label || provider.id} is not installed on this computer. ${INSTALL_HINTS[provider.adapter] || 'Install it and sign in, then look for models again.'}`;
function providerViews(providers, models, plans = [], settings = {}) {
  return providers.map(provider => {
    const rows = models.filter(m => m.providerId === provider.id || !m.providerId && m.provider === provider.id);
    const pool = plans.find(p => p.id === provider.quotaPoolId);
    const remaining = pool?.readable === true && Number.isFinite(pool.leftPct) ? Math.max(0, Math.min(100, pool.leftPct)) : null;
    const available = provider.available !== false && Boolean(adapterFor(provider)) && adapterFor(provider)?.executionSupported !== false && rows.some(m => m.available !== false);
    const enabled = settings.providerEnabled?.[provider.id] ?? provider.enabled;
    // Installed unless the last check found no program for it on this computer.
    const installed = !rows.some(m => m.notInstalled === true);
    return { ...provider, label: require('./provider-accounts.cjs').accountLabel(provider.label, provider.account), enabled, available, installed, installHint: installed ? null : notInstalledReason(provider),
      availability: !installed ? 'NOT_INSTALLED' : !available ? 'UNAVAILABLE' : pool?.readable && (pool.exhausted || remaining === 0) ? 'EXHAUSTED' : remaining === null ? 'UNREAD' : 'AVAILABLE',
      quotaReadable: remaining !== null, remainingPercent: remaining, resetAt: pool?.resetAt || pool?.windows?.find(w => w.leftPct === remaining)?.resetAt || null,
      quota: pool || { id: provider.quotaPoolId, label: provider.label, readable: false, leftPct: null, exhausted: false }, showAll: settings.catalogVisibility?.[provider.id] === true, models: rows };
  });
}
module.exports = { loadCatalog, providerViews, providerConfiguration, notInstalledReason, INSTALL_HINTS };
