'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const saving = require('./saving-mode.cjs');

test('three modes, and an unknown value is Normal', () => {
  assert.deepEqual(saving.MODES, ['normal', 'safe', 'ultra-safe']);
  assert.equal(saving.DEFAULT, 'normal');
  assert.equal(saving.normalize(' Normal '), 'normal');
  assert.equal(saving.normalize('SAFE'), 'safe');
  assert.equal(saving.normalize('ultra safe'), 'ultra-safe');
  assert.equal(saving.normalize('ultra_safe'), 'ultra-safe');
  assert.equal(saving.normalize('nope'), null);
  assert.equal(saving.persisted(undefined), 'normal');
  assert.equal(saving.persisted('nope'), 'normal');
  assert.equal(saving.persisted('sicura'), 'safe');
  assert.equal(saving.persisted('ultra sicura'), 'ultra-safe');
});

test('class follows leadTier, and a Team weight wins', () => {
  const flagship = { id: 'fable', weight: 'premium', leadTier: 0, weightSource: 'lineup' };
  const mid = { id: 'sonnet', weight: 'default', leadTier: 1, weightSource: 'lineup' };
  const light = { id: 'haiku', weight: 'light', leadTier: 2, weightSource: 'lineup' };
  assert.equal(saving.modelClass(flagship), 'flagship');
  assert.equal(saving.modelClass(mid), 'mid');
  assert.equal(saving.modelClass(light), 'light');
  // Effort in the name does not change class: the caller passes the leadTier rank.
  const flashHigh = { id: 'flash', label: 'Gemini 3.8 Flash (High)', weight: 'light', leadTier: 2, weightSource: 'lineup' };
  assert.equal(saving.modelClass(flashHigh, 2), 'light');
  const overridden = { id: 'fable', weight: 'light', leadTier: 0, weightSource: 'team' };
  assert.equal(saving.modelClass(overridden, 0), 'light');
  assert.equal(saving.teamWeight('premium'), 'premium');
  assert.equal(saving.teamWeight('flagship'), 'premium');
  assert.equal(saving.teamWeight('mid'), 'default');
  assert.equal(saving.teamWeight('nope'), null);
});

test('Safe drops flagships and Ultra safe keeps light models only', () => {
  const flagship = { leadTier: 0, weight: 'premium', weightSource: 'lineup' };
  const mid = { leadTier: 1, weight: 'default', weightSource: 'lineup' };
  const light = { leadTier: 2, weight: 'light', weightSource: 'lineup' };
  assert.equal(saving.allows('normal', flagship), true);
  assert.equal(saving.allows('normal', mid), true);
  assert.equal(saving.allows('normal', light), true);
  assert.equal(saving.allows('safe', flagship), false);
  assert.equal(saving.allows('safe', mid), true);
  assert.equal(saving.allows('safe', light), true);
  assert.equal(saving.allows('ultra-safe', flagship), false);
  assert.equal(saving.allows('ultra-safe', mid), false);
  assert.equal(saving.allows('ultra-safe', light), true);
  assert.deepEqual(saving.allowedClasses('safe'), ['mid', 'light']);
  assert.deepEqual(saving.allowedClasses('ultra-safe'), ['light']);
});

test('chat orders name the three modes in English and Italian', () => {
  assert.deepEqual(saving.order('mode normal'), { action: 'saving-mode', savingMode: 'normal' });
  assert.deepEqual(saving.order('modalità normale'), { action: 'saving-mode', savingMode: 'normal' });
  assert.deepEqual(saving.order('mode safe'), { action: 'saving-mode', savingMode: 'safe' });
  assert.deepEqual(saving.order('modalità sicura'), { action: 'saving-mode', savingMode: 'safe' });
  assert.deepEqual(saving.order('mode ultra safe'), { action: 'saving-mode', savingMode: 'ultra-safe' });
  assert.deepEqual(saving.order('modalità ultra sicura'), { action: 'saving-mode', savingMode: 'ultra-safe' });
  assert.equal(saving.order('mode talk'), null);
  assert.equal(saving.order('mode plan'), null);
});

test('labels and stop reasons name the mode', () => {
  assert.equal(saving.label('safe', 'en'), 'Safe');
  assert.equal(saving.label('safe', 'it'), 'Sicura');
  assert.equal(saving.label('ultra-safe', 'it'), 'Ultra sicura');
  assert.equal(saving.label('normal', 'en'), 'Normal');
  const reason = saving.blockedReason({
    mode: 'safe', title: 'Fix the list', role: 'Build',
    leftOut: ['Fable'], underLimit: ['Haiku']
  });
  assert.match(reason, /Stopped: Safe allows no model for Build/);
  assert.match(reason, /leaves out Fable/);
  assert.match(reason, /under their limit: Haiku/);
  assert.match(saving.leadFallbackNote('ultra-safe', { label: 'Haiku' }), /keeps Haiku/);
  assert.match(saving.verifyLightNote('ultra-safe'), /light model to verify/);
  assert.match(saving.leadInstruction('safe'), /mid and light/);
  assert.equal(saving.leadInstruction('normal'), '');
});
