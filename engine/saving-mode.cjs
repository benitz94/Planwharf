'use strict';

// Per-project saving mode. Normal keeps today's picking. Safe drops flagship models.
// Ultra safe keeps light models only. This is not a second picker: the existing
// pickers ask allows() and then choose as they do now.

const MODES = Object.freeze(['normal', 'safe', 'ultra-safe']);
const DEFAULT = 'normal';
const WEIGHT_CLASS = Object.freeze({ premium: 'flagship', default: 'mid', light: 'light' });
const TIER_CLASS = Object.freeze(['flagship', 'mid', 'light']);
const LABELS = Object.freeze({
  en: { normal: 'Normal', safe: 'Safe', 'ultra-safe': 'Ultra safe' },
  it: { normal: 'Normale', safe: 'Sicura', 'ultra-safe': 'Ultra sicura' }
});

function normalize(value) {
  const mode = String(value || '').trim().toLowerCase().replace(/[_]+/g, '-').replace(/\s+/g, ' ');
  if (mode === 'normal' || mode === 'normale') return 'normal';
  if (mode === 'safe' || mode === 'sicura' || mode === 'sicuro') return 'safe';
  if (mode === 'ultra-safe' || mode === 'ultra safe' || mode === 'ultrasafe' || mode === 'ultra sicura' || mode === 'ultra sicuro') return 'ultra-safe';
  return null;
}

function persisted(value) {
  return normalize(value) || DEFAULT;
}

// A weight the owner set in Team (premium, default, light). Anything else is not an override.
function teamWeight(value) {
  const weight = String(value || '').trim().toLowerCase();
  if (weight === 'premium' || weight === 'flagship') return 'premium';
  if (weight === 'default' || weight === 'mid') return 'default';
  if (weight === 'light') return 'light';
  return null;
}

// Team weight wins. Otherwise tier is leadTier: 0 flagship, 1 mid, 2 light.
function modelClass(model, tier) {
  if (model && model.weightSource === 'team' && WEIGHT_CLASS[model.weight]) return WEIGHT_CLASS[model.weight];
  const ranked = Number.isInteger(tier) ? tier : Number.isInteger(model?.leadTier) ? model.leadTier : null;
  if (ranked === 0 || ranked === 1 || ranked === 2) return TIER_CLASS[ranked];
  if (model && WEIGHT_CLASS[model.weight]) return WEIGHT_CLASS[model.weight];
  return 'mid';
}

function allowedClasses(mode) {
  const saved = persisted(mode);
  if (saved === 'safe') return ['mid', 'light'];
  if (saved === 'ultra-safe') return ['light'];
  return ['flagship', 'mid', 'light'];
}

function allows(mode, model, tier) {
  return allowedClasses(mode).includes(modelClass(model, tier));
}

// Exact chat orders. Chat modes (Talk, Plan) stay with their own parser.
function order(text) {
  const command = String(text || '').trim().toLowerCase().replace(/[.!?]+$/g, '').trim();
  if (!command) return null;
  if (/^(?:mode normal|modalit[aà] normale)$/.test(command)) return { action: 'saving-mode', savingMode: 'normal' };
  if (/^(?:mode safe|modalit[aà] sicura)$/.test(command)) return { action: 'saving-mode', savingMode: 'safe' };
  if (/^(?:mode ultra safe|mode ultra-safe|modalit[aà] ultra sicura)$/.test(command)) return { action: 'saving-mode', savingMode: 'ultra-safe' };
  return null;
}

function label(mode, language) {
  const lang = language === 'it' ? 'it' : 'en';
  return LABELS[lang][persisted(mode)];
}

function blockedReason({ mode, title, role, leftOut = [], underLimit = [] } = {}) {
  const name = label(mode);
  const step = title ? `"${String(title).slice(0, 80)}"` : 'this step';
  const job = role || 'Build';
  const parts = [];
  if (leftOut.length) parts.push(`${name} leaves out ${leftOut.join(', ')}`);
  if (underLimit.length) parts.push(`under their limit: ${underLimit.join(', ')}`);
  const why = parts.length ? ' ' + parts.join('. ') + '.' : '';
  return `Stopped: ${name} allows no model for ${job} on ${step}.${why} Change the saving mode in Team, or give an allowed model the role.`;
}

function leadFallbackNote(mode, model) {
  const name = model?.label || model?.id || 'a model';
  return `${label(mode)} allows no lead. Planwharf keeps ${name}, the best model of the lowest class that can still answer.`;
}

function verifyLightNote(mode) {
  return `${label(mode)} uses a light model to verify. The usual strong model for Verify gives way to this mode.`;
}

function leadInstruction(mode) {
  const saved = persisted(mode);
  if (saved === 'safe') return 'Saving mode is Safe: do not choose a flagship model for the lead or the crew. Use mid and light models only.';
  if (saved === 'ultra-safe') return 'Saving mode is Ultra safe: use light models only for the lead and the crew. A light model verifies, and that replaces the usual strong model for Verify.';
  return '';
}

function changedSentence(mode, language) {
  const name = label(mode, language);
  if (language === 'it') return `La modalità di risparmio è ora ${name}. Vale dal prossimo passo.`;
  return `Saving mode is now ${name}. It applies from the next step.`;
}

module.exports = {
  MODES, DEFAULT, LABELS, WEIGHT_CLASS, normalize, persisted, teamWeight, modelClass,
  allowedClasses, allows, order, label, blockedReason, leadFallbackNote, verifyLightNote, leadInstruction, changedSentence
};
