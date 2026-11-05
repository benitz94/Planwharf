'use strict';

const LIMITS = Object.freeze({ tail: 131072, line: 16384, events: 200, text: 260 });
// Display evidence is deliberately a whitelist. Raw logs remain with their existing owner.
function sanitize(value, limit = LIMITS.text) {
  return String(value ?? '')
    .replace(/-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|$)/g, '[REDACTED PRIVATE KEY]')
    .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
    .replace(/[\u0000-\u0008\u000b-\u001f\u007f]/g, '')
    .replace(/\b(Bearer\s+)[^\s,;"']+/gi, '$1[REDACTED]')
    .replace(/(["']?(?:api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|secret|authorization|cookie)["']?\s*[=:]\s*)(?:"[^"\n]*"|'[^'\n]*'|[^\s,;&]+)/gi, '$1[REDACTED]')
    .replace(/\b(?:sk-[\w-]{8,}|gh[pousr]_[\w]{8,}|github_pat_[\w]+)\b/g, '[REDACTED]')
    .replace(/(https?:\/\/)[^\s/@]+:[^\s/@]+@/gi, '$1[REDACTED]@')
    .replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/\s+/g, ' ').trim().slice(0, limit);
}

// These describe the launcher in provider-runtime.cjs and the API bridge, not a
// promise that a provider emitted a detailed event or that live delivery was tested.
function activityCapabilities(context = {}) {
  const adapter = context.adapter || context.provider;
  const api = adapter === 'api';
  const structured = adapter === 'antigravity' || adapter === 'claude' || api || context.lead === true && (adapter === 'codex' || adapter === 'grok');
  return {
    configuredAdapter: sanitize(adapter || 'unknown', 80),
    expectedFormat: structured ? api ? 'planwharf-api-bridge' : adapter === 'antigravity' || adapter === 'claude' ? 'stream-json' : adapter === 'codex' ? 'json' : 'streaming-messages-json' : adapter === 'codex' || adapter === 'grok' ? 'plain' : 'unknown',
    detailedEventsExpected: structured,
    liveDeliveryVerified: false,
    validatedResults: false
  };
}
function classification(message) {
  if (/^(?:Hit an error|Worker reported an (?:error|unsuccessful))/.test(message)) return 'warning';
  if (/^(?:Finished )?(?:Reading|reading|Searching|searching|Inspecting|inspecting)/.test(message)) return 'read';
  if (/^(?:Finished )?(?:Writing|writing)/.test(message)) return 'working';
  if (/^(?:Finished )?(?:Running tests|running tests)|^Test output:/.test(message)) return 'test';
  if (/^(?:Finished )?(?:Building|building)/.test(message)) return 'build';
  if (/^(?:Finished )?(?:Reviewing|reviewing)/.test(message)) return 'review';
  if (/^(?:Worker returned|Worker finished)/.test(message)) return 'report';
  return 'unknown';
}
function object(value) { return value && typeof value === 'object' && !Array.isArray(value); }
function normalizeEvent(row, context, describe) {
  if (!object(row)) return unknown(context);
  const step = object(row.step_update) ? row.step_update : null;
  const item = object(row.item) ? row.item : null;
  const block = row.content_block || row.event?.content_block;
  if (item?.type === 'file_change' && item.changes !== undefined && !Array.isArray(item.changes)) return unknown(context);
  const protocol = step ? 'antigravity-step' : item || row.type === 'thread.started' || /^turn\./.test(row.type || '') ? 'item-events' : block || row.type === 'assistant' || row.type === 'result' ? 'message-events' : row.event === 'result' || row.event === 'init' ? 'antigravity-result' : 'unknown';
  const compatible = { codex: ['item-events'], api: ['item-events'], antigravity: ['antigravity-step', 'antigravity-result'], grok: ['message-events'], claude: ['message-events'] };
  const adapter = context.adapter || context.provider;
  const expected = Object.hasOwn(compatible, adapter) ? compatible[adapter] : null;
  if (expected && protocol !== 'unknown' && !expected.includes(protocol)) return unknown(context, protocol);
  let message = '';
  try { message = describe(row); } catch { /* Malformed/untrusted protocol fields are unknown evidence. */ }
  if (row.type === 'turn.failed' || row.type === 'error') message = 'Worker reported an error.';
  if (!message) return unknown(context, protocol, row.type || (typeof row.event === 'string' ? row.event : ''));
  const reported = step?.step_type === 'agent_response' || item?.type === 'agent_message' || row.type === 'result' || row.event === 'result' || row.type === 'assistant' && !(Array.isArray(row.message?.content) && row.message.content.some(part => part?.type === 'tool_use'));
  const result = base(context, protocol, message, reported ? 'reported' : 'observed');
  result.technical.eventType = sanitize(typeof row.type === 'string' ? row.type : typeof row.event === 'string' ? row.event : '', 80);
  const tool = step?.tool_name || step?.tool_info?.name || block?.name || (row.type === 'assistant' ? (Array.isArray(row.message?.content) ? row.message.content.find(part => part?.type === 'tool_use')?.name : '') : '');
  if (tool) result.technical.tool = sanitize(tool, 80);
  if (Number.isInteger(item?.exit_code)) result.technical.exitCode = item.exit_code;
  result.state = step?.state === 'ERROR' || step?.state === 'FAILED' || row.type === 'turn.failed' || row.type === 'error' || row.is_error === true || typeof item?.exit_code === 'number' && item.exit_code !== 0 ? 'failed'
    : step?.state === 'DONE' || row.type === 'item.completed' || row.type === 'result' || row.event === 'result' ? 'finished' : 'observed';
  if (row.event === 'result' && row.result?.status && row.result.status !== 'SUCCESS') result.state = 'failed';
  return result;
}
function base(context, protocol, message, provenance) {
  return {
    provider: sanitize(context.providerId || context.provider || 'unknown', 80),
    adapter: sanitize(context.adapter || context.provider || 'unknown', 80),
    model: sanitize(context.model || '', 120),
    protocol, classification: classification(message), provenance,
    state: 'observed', validated: false,
    friendly: sanitize(message), technical: { protocol }
  };
}
function unknown(context, protocol = 'unknown', eventType = '') {
  const result = base(context, protocol, 'Unclassified worker activity.', 'unknown');
  result.state = 'unknown';
  if (eventType) result.technical.eventType = sanitize(eventType, 80);
  return result;
}
function translateEvents(tail, context = {}, describe, describePlain) {
  const events = [];
  // Limit both parsing and returned history. A clipped first line is never promoted
  // to an observed action; supplied context never supplies fabricated timestamps.
  const input = String(tail || '');
  let bounded = input.slice(-LIMITS.tail);
  if (input.length > LIMITS.tail) bounded = bounded.slice(bounded.indexOf('\n') + 1);
  for (const line of bounded.split(/\r?\n/)) {
    if (!line.trim()) continue;
    let event;
    if (line.length > LIMITS.line) event = unknown(context);
    else {
      try { event = normalizeEvent(JSON.parse(line), context, describe); }
      catch {
        event = /^[\s]*[\[{]|"(?:event|step_update|tool_name|tool_info)"\s*:/.test(line)
          ? unknown(context)
          : base(context, 'plain', describePlain(line) || 'Unclassified worker output.', 'reported');
      }
    }
    events.push(event);
    if (events.length > LIMITS.events) events.shift();
  }
  return events;
}
module.exports = { LIMITS, sanitize, activityCapabilities, translateEvents };
