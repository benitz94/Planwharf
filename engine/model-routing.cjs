'use strict';

// These are CLI adapters, not a list of every model sold by their vendors.
// Explicitly configured routes let compatible CLIs accept new exact model IDs.
const { adapters: ADAPTERS } = require('./provider-adapters.cjs');

function modelRouteError(model) {
  if (!model || typeof model !== 'object') return 'Model configuration is required';
  if (model.available === false) return model.unavailableReason || 'Model unavailable';
  const provider = model.adapter || model.provider;
  const adapter = Object.hasOwn(ADAPTERS, provider) ? ADAPTERS[provider] : null;
  if (!adapter) return 'Provider/model mismatch: unsupported provider adapter ' + String(provider);
  if (adapter.executionSupported === false) return adapter.unavailableReason;
  const cli = model.cli;
  if (typeof cli !== 'string' || !cli.trim() || cli !== cli.trim() || /[\x00-\x1f\x7f]/.test(cli)) {
    return 'Provider/model mismatch: an exact nonempty CLI model ID is required for ' + provider;
  }
  if (Buffer.byteLength(cli, 'utf8') > 512) return 'CLI model ID exceeds the 512-byte configuration limit';
  if (model.modelRouting !== undefined && model.modelRouting !== 'configured') {
    return 'Invalid modelRouting: expected "configured" or an omitted value';
  }
  if (model.modelRouting !== 'configured' && !adapter.nativeModel.test(cli)) {
    return 'Provider/model mismatch: ' + provider + '/' + cli + '. Set modelRouting to "configured" only when this exact CLI supports the model.';
  }
  return null;
}

function resolveCliModel(model) {
  return modelRouteError(model) ? null : model.cli;
}

// Why a model is outside the project's saving mode. Null when the mode allows it.
function savingModeError(model, mode, tier) {
  const saving = require('./saving-mode.cjs');
  const saved = saving.persisted(mode);
  if (saved === 'normal' || saving.allows(saved, model, tier)) return null;
  const name = model?.label || model?.id || 'This model';
  return `${name} is ${saving.modelClass(model, tier)}, and ${saving.label(saved)} does not allow it.`;
}

module.exports = { modelRouteError, resolveCliModel, savingModeError };
