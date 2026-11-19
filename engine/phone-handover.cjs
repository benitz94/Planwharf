'use strict';

const activation = require('./phone-activation.cjs');
const samePath = activation.samePath;

// Pausing the installed listener is recoverable: the process stays up and polling is turned back on.
// Quitting that process would also stop the worker that is doing the handover.
function defaultHandoverPlan({
  installed = {},
  updatedBuild = '',
  backupOk = false,
  tokenPresent = false
} = {}) {
  const reasons = [];
  if (!backupOk) reasons.push('A recoverable backup of the plan, phone settings and bot token is required before polling pauses.');
  if (!updatedBuild) reasons.push('The updated native build is missing.');
  if (installed.path && samePath(installed.path, updatedBuild)) reasons.push('The updated build is the process that is already polling.');
  if (!tokenPresent) reasons.push('No bot token is available, so the paired bot cannot be verified.');
  if (!installed.address) reasons.push('The installed listener address was not recorded, so polling cannot be restored.');
  return {
    allowed: reasons.length === 0,
    blocker: reasons.join(' '),
    pause: installed.address ? { address: String(installed.address), pid: Number.isInteger(installed.pid) ? installed.pid : null, path: String(installed.path || '') } : null,
    build: updatedBuild || '',
    restore: 're-enable telegram polling on the installed listener'
  };
}

function defaultExchangeEvidence({ outbound, inbound, reply } = {}) {
  const clip = value => String(value || '').replace(/\b\d{5,}:[A-Za-z0-9_-]{6,}\b/g, '[redacted]').slice(0, 600);
  const row = item => {
    if (!item || (!item.text && item.messageId == null && item.updateId == null)) return null;
    return {
      messageId: item.messageId == null ? null : item.messageId,
      updateId: item.updateId == null ? null : item.updateId,
      text: clip(item.text)
    };
  };
  return { outbound: row(outbound), inbound: row(inbound), reply: row(reply) };
}

const handoverPlan = activation.handoverPlan || defaultHandoverPlan;
const exchangeEvidence = activation.exchangeEvidence || defaultExchangeEvidence;

function pollBlockerText(value) {
  if (typeof value === 'string') return value;
  if (typeof value === 'number' && Number.isFinite(value)) return String(value);
  if (value instanceof Error && value.message) return value.message;
  if (value && typeof value === 'object') {
    if (typeof value.message === 'string' && value.message) return value.message;
    if (typeof value.description === 'string' && value.description) return value.description;
  }
  return '';
}

let handoverBusy = false;

function refused(plan, extra = {}) {
  return { ok: false, restored: false, plan, evidence: exchangeEvidence({}), ...extra };
}

// The installed app keeps running. Polling moves to the updated build, then comes back.
// One handover at a time: a second caller must not pause the listener again.
async function runHandover({
  installed,
  updatedBuild,
  backupOk,
  token,
  pausePolling,
  pollOnce,
  startBuild,
  sendAlert,
  replyTo,
  askWhenIdle,
  stopBuild,
  resumePolling
} = {}) {
  if (handoverBusy) {
    const plan = handoverPlan({ installed, updatedBuild, backupOk, tokenPresent: Boolean(token) });
    return refused(plan, { blocker: 'A handover is already running.' });
  }
  handoverBusy = true;
  try {
    const plan = handoverPlan({ installed, updatedBuild, backupOk, tokenPresent: Boolean(token) });
    if (!plan.allowed) return refused(plan);
    let paused;
    try { paused = await pausePolling(installed); }
    catch (error) {
      return refused(plan, { blocker: error?.message || 'Polling could not be paused.' });
    }
    if (!paused || paused.ok === false) return refused(plan, { paused });

    let inbound = null;
    let reply = null;
    let started = null;
    let outbound = null;
    let blocker = '';
    let failed = false;
    try {
      const polled = await pollOnce();
      if (polled?.blocked) {
        failed = true;
        blocker = 'The installed listener is still polling.';
      } else if (polled?.error) {
        failed = true;
        blocker = pollBlockerText(polled.error) || 'Handover poll failed.';
      } else if (polled?.ok === false) {
        failed = true;
        blocker = pollBlockerText(polled.reason) || 'Handover poll failed.';
      } else {
        const update = Array.isArray(polled?.updates) ? polled.updates.find(item => item && item.message && item.message.text) : null;
        if (update) {
          inbound = { updateId: update.update_id, text: update.message.text };
          const answered = await replyTo(update.message);
          reply = { messageId: answered?.messageId ?? null, text: answered?.text || '' };
        } else if (typeof askWhenIdle === 'function') {
          const answered = await askWhenIdle();
          reply = { messageId: answered?.messageId ?? null, text: answered?.text || '' };
        }
        const built = await startBuild();
        if (!built || !built.address) failed = true;
        else {
          started = built;
          const alert = await sendAlert(started);
          outbound = { messageId: alert?.messageId ?? null, text: alert?.text || '' };
        }
      }
    } catch (error) {
      failed = true;
      blocker = error?.message || 'Handover failed.';
    }
    if (started?.address && typeof stopBuild === 'function') {
      try { await stopBuild(started); }
      catch { /* The listener still has to poll again. */ }
    }
    let restored = { ok: false };
    if (typeof resumePolling === 'function') {
      try { restored = (await resumePolling(installed)) || { ok: false }; }
      catch { restored = { ok: false }; }
    }
    return {
      ok: Boolean(restored?.ok) && !failed && Boolean(outbound?.text),
      restored: Boolean(restored?.ok),
      plan,
      paused,
      ...(blocker ? { blocker } : {}),
      ...(started ? { started: { address: started.address, model: started.model || '', paired: Boolean(started.paired) } } : {}),
      evidence: exchangeEvidence({ outbound, inbound, reply })
    };
  } finally {
    handoverBusy = false;
  }
}

module.exports = { runHandover, handoverPlan, exchangeEvidence };

if (require.main === module) {
  require('./phone-handover-live.cjs');
}
