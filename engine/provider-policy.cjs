'use strict';
const { validatePolicy, migratePolicy } = require('./model-policy.cjs');
const DEFAULT_PROVIDER_POLICY = Object.freeze({ minRemainingPercent: null, warningRemainingPercent: null, fallbackProviderId: null, alwaysApprove: false });
function validateProviderPolicy(value) {
  value = migratePolicy(value);
  if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(key => !Object.hasOwn(DEFAULT_PROVIDER_POLICY, key))) throw new Error('Invalid provider policy fields.');
  const policy = { ...DEFAULT_PROVIDER_POLICY, ...value };
  const { fallbackProviderId, ...common } = policy;
  validatePolicy(common);
  if (fallbackProviderId !== null && (typeof fallbackProviderId !== 'string' || !fallbackProviderId.trim())) throw new Error('Fallback must be an explicit provider ID or null.');
  return policy;
}
function providerPolicyFor(provider, models, settings = {}) {
  if (settings.providerPolicies?.[provider.id]) return { policy: validateProviderPolicy(settings.providerPolicies[provider.id]), source: 'saved' };
  if (provider.policy) return { policy: validateProviderPolicy(provider.policy), source: 'configured' };
  const siblings = models.filter(model => (model.providerId || model.provider) === provider.id);
  const legacy = siblings.filter(model => settings.modelPolicies?.[model.id] || model.policy || model.alwaysApprove !== undefined).map(model => ({ model,
    policy: validatePolicy({ alwaysApprove: model.alwaysApprove ?? false, ...migratePolicy(model.policy || {}), ...migratePolicy(settings.modelPolicies?.[model.id] || {}) }) }));
  if (!legacy.length) return { policy: { ...DEFAULT_PROVIDER_POLICY }, source: 'default' };
  const minima = legacy.map(row => row.policy.minRemainingPercent).filter(value => value !== null);
  const minRemainingPercent = minima.length ? Math.max(...minima) : null;
  const warnings = legacy.map(row => row.policy.warningRemainingPercent).filter(value => value !== null && minRemainingPercent !== null && value > minRemainingPercent);
  const fallbackIds = legacy.map(row => models.find(model => model.id === row.policy.fallbackModelId)).map(model => model ? model.providerId || model.provider : null);
  const fallbackProviderId = fallbackIds[0] && fallbackIds.every(id => id === fallbackIds[0]) && fallbackIds[0] !== provider.id ? fallbackIds[0] : null;
  return { policy: { minRemainingPercent, warningRemainingPercent: warnings.length ? Math.max(...warnings) : null, fallbackProviderId, alwaysApprove: legacy.every(row => row.policy.alwaysApprove) }, source: 'legacy' };
}
module.exports = { DEFAULT_PROVIDER_POLICY, validateProviderPolicy, providerPolicyFor };
