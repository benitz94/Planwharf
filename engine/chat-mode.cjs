'use strict';
// Per-project chat mode. Talk, Plan and Auto are shown. Pipeline is reserved for
// job 30: it is never accepted, never shown and never restored.

const VISIBLE = Object.freeze(['talk', 'plan', 'auto']);
const RESERVED = Object.freeze(['pipeline']);
const DEFAULT = 'plan';
const SUGGESTED = /^\s*\**Suggested next goal:\**\s*(.+)$/im;

function normalize(value) {
  const mode = String(value || '').trim().toLowerCase();
  return VISIBLE.includes(mode) ? mode : null;
}

// Talk and Plan are saved. Auto lasts one session, so a restart reads Plan.
// Anything else, including the reserved Pipeline value, reads as Plan.
function persisted(value) {
  const mode = normalize(value);
  return mode === 'talk' || mode === 'plan' ? mode : DEFAULT;
}

function talkInstruction() {
  return [
    'This project is in Talk: a conversation to think an idea through.',
    'Do not suggest a goal for the crew.',
    'Do not write a line that starts with "Suggested next goal:".',
    'Do not offer to start work. Nothing starts from this conversation.'
  ].join(' ');
}

function suggestedGoal(answer) {
  const match = String(answer || '').match(SUGGESTED);
  return match && match[1].trim() ? match[1].trim() : null;
}

function withoutSuggestion(answer) {
  return String(answer || '')
    .replace(/^\s*\**Suggested next goal:\**\s*.+(?:\r?\n)?/gim, '')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

// A running goal, including one waiting for approval. Switching must not cancel it.
function workInFlight(row) {
  if (!row || typeof row !== 'object') return false;
  const mega = String(row.megaTask && row.megaTask.status || '').toUpperCase();
  if (mega === 'RUNNING' || mega === 'READY_FOR_APPROVAL') return true;
  if (row.canStop === true) return true;
  if (row.status === 'watching' || row.status === 'needs_approval') return true;
  return Array.isArray(row.dispatch) && row.dispatch.some(part => part && part.alive);
}

// Short orders. mode manual selects Plan: there is no separate visible Manual position.
// mode auto selects Auto. The approval mode stays auto or manual, as those orders do today.
function order(text) {
  const command = String(text || '').trim().toLowerCase().replace(/[.!?]+$/g, '').trim();
  if (!command) return null;
  if (/^(?:mode talk|mode chat|modalit[aà] chat)$/.test(command)) return pack('talk');
  if (/^(?:mode plan|modalit[aà] (?:plan|piano))$/.test(command)) return pack('plan');
  if (/^(?:mode pipeline|modalit[aà] pipeline)$/.test(command)) return { action: 'reject', mode: 'pipeline' };
  const wantsMode = /\b(mode|modalit[aà]|switch to|set to|passa a|passa ad)(?:\b|(?<=[à]))/i.test(command);
  if (!wantsMode) return null;
  if (/\bpipeline\b/i.test(command)) return { action: 'reject', mode: 'pipeline' };
  if (/\b(automatic|auto)\b/i.test(command) && !/\bauto start\b/i.test(command)) return pack('auto');
  if (/\b(manual|manuale)\b/i.test(command)) return pack('plan', 'manual');
  if (/\btalk\b/i.test(command) || /\bchat\b/i.test(command)) return pack('talk');
  if (/\b(?:plan|piano)\b/i.test(command)) return pack('plan');
  return null;
}

function pack(chatMode, approval) {
  const mode = chatMode === 'auto' ? 'auto' : approval === 'manual' ? 'manual' : chatMode;
  return { action: 'mode', mode, chatMode };
}

function label(mode) {
  return { talk: 'Talk', plan: 'Plan', auto: 'Auto' }[mode] || 'Plan';
}

module.exports = {
  VISIBLE, RESERVED, DEFAULT, normalize, persisted, talkInstruction,
  suggestedGoal, withoutSuggestion, workInFlight, order, label
};
