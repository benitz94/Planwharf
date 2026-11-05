'use strict';
// Optional Technical view only. Never infer an operation or validated outcome.
const LIMITS = Object.freeze({ tailBytes: 32768, lineBytes: 16384, fieldBytes: 4096, commandBytes: 2048, totalBytes: 16384, entries: 20 });
function redact(value) {
  return String(value ?? '').replace(/\r\n?/g, '\n')
    .replace(/-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-]*PRIVATE KEY-----|$)/g, '[REDACTED PRIVATE KEY]')
    .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\|$)/g, '')
    .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
    .replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/g, '')
    .replace(/\b(Bearer\s+)[^\s,;"']+/gi, '$1[REDACTED]')
    .replace(/((?:\\?["'])?(?:api[_-]?key|access[_-]?(?:key|token)|refresh[_-]?token|token|password|secret|authorization|cookie)(?:\\?["'])?\s*[=:]\s*)(?:\\?"[^"\n]*\\?"|\\?'[^'\n]*\\?'|[^\s,;&]+)/gi, '$1[REDACTED]')
    .replace(/(--(?:api[-_]?key|access[-_]?token|token|password|secret)(?:=|\s+))(?:"[^"\n]*"|'[^'\n]*'|[^\s]+)/gi, '$1[REDACTED]')
    .replace(/\b(?:sk-[\w-]{8,}|gh[pousr]_[\w]{8,}|github_pat_[\w]+)\b/g, '[REDACTED]')
    .replace(/(https?:\/\/)[^\s/@]+:[^\s/@]+@/gi, '$1[REDACTED]@')
    .replace(/^[A-Za-z0-9+/=]{48,}$/gm, '[REDACTED ENCODED VALUE]')
    .replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
function technicalEvidence(tail, context = {}) {
  let truncated = false, bytes = 0;
  const entries = [], warningSet = new Set();
  const warn = message => warningSet.add(message);
  const field = (value, maximum = LIMITS.fieldBytes) => {
    const safe = redact(value), encoded = Buffer.from(safe);
    if (encoded.length <= maximum) return safe;
    truncated = true;
    // Redact before clipping and avoid an invalid trailing UTF-8 sequence.
    const marker = '\n[excerpt truncated]';
    return encoded.subarray(0, Math.max(0, maximum - Buffer.byteLength(marker))).toString('utf8').replace(/\uFFFD$/g, '') + marker;
  };
  const append = entry => {
    const size = Buffer.byteLength(JSON.stringify(entry));
    entries.push(entry); bytes += size;
    while (entries.length > LIMITS.entries || bytes > LIMITS.totalBytes) {
      bytes -= Buffer.byteLength(JSON.stringify(entries.shift())); truncated = true;
    }
  };
  const adapter = context.adapter || context.provider;
  const known = ['codex', 'api', 'antigravity', 'grok', 'claude'].includes(adapter);
  if (!known) warn('Adapter identity is unknown; protocol excerpts are not verified provider evidence.');
  let raw = Buffer.from(String(tail || ''));
  if (raw.length > LIMITS.tailBytes) {
    raw = raw.subarray(raw.length - LIMITS.tailBytes); truncated = true;
    const newline = raw.indexOf(10); raw = newline < 0 ? Buffer.alloc(0) : raw.subarray(newline + 1);
  }
  let plain = [];
  const flushPlain = () => {
    if (!plain.length) return;
    const output = field(plain.join('\n')); plain = [];
    if (output) append({ protocol: 'plain', provenance: 'reported', output });
  };
  for (const line of raw.toString('utf8').split('\n')) {
    if (!line.trim()) { if (plain.length) plain.push(''); continue; }
    if (Buffer.byteLength(line) > LIMITS.lineBytes) { flushPlain(); truncated = true; warn('An oversized source record was omitted.'); continue; }
    let row;
    try { row = JSON.parse(line); } catch {
      if (/^\s*[\[{]|"(?:event|step_update|tool_name|tool_info|item|type)"\s*:/.test(line)) { flushPlain(); warn('Malformed structured evidence was omitted.'); }
      else plain.push(line);
      continue;
    }
    flushPlain();
    if (!row || typeof row !== 'object' || Array.isArray(row)) { warn('Unknown structured evidence was omitted.'); continue; }
    const item = row.item, step = row.step_update;
    let entry;
    if (item?.type === 'command_execution' && ['item.started', 'item.completed', 'item.updated'].includes(row.type)) {
      if (known && !['codex', 'api'].includes(adapter)) { warn('Evidence protocol does not match the configured adapter; record omitted.'); continue; }
      entry = { protocol: adapter === 'api' ? 'planwharf-api-bridge' : 'item-events', provenance: known ? 'observed' : 'unknown', eventType: row.type };
      if (typeof item.command === 'string') entry.command = field(item.command, LIMITS.commandBytes);
      if (typeof item.aggregated_output === 'string') entry.output = field(item.aggregated_output);
      if (Number.isInteger(item.exit_code)) entry.exitCode = item.exit_code;
      if (adapter === 'api') warn('API bridge command field may contain only a tool name; arguments and output may be unavailable.');
    } else if (step && typeof step === 'object' && ['tool', 'tool_call'].includes(step.step_type)) {
      if (known && adapter !== 'antigravity') { warn('Evidence protocol does not match the configured adapter; record omitted.'); continue; }
      entry = { protocol: 'antigravity-step', provenance: known ? 'observed' : 'unknown', eventType: 'step_update' };
      const tool = step.tool_name || step.tool_info?.name;
      if (typeof tool === 'string') entry.tool = field(tool, 120);
      if (typeof step.state === 'string') entry.state = field(step.state, 80);
      // Source/code/write payloads and arbitrary result objects are never copied.
      if (['run_command', 'exec_command', 'command_execution', 'shell', 'terminal', 'bash'].includes(tool)) {
        const parameters = step.tool_info?.parameters || step.parameters;
        const command = parameters?.CommandLine || parameters?.command || parameters?.cmd;
        if (typeof command === 'string') entry.command = field(command, LIMITS.commandBytes);
      }
      warn('Antigravity excerpts contain retained tool command/state metadata only; detailed output is not verified.');
    } else if (adapter === 'claude') {
      const block = row.content_block || row.event?.content_block;
      const tool = block?.type === 'tool_use' ? block : row.type === 'assistant' && Array.isArray(row.message?.content) ? row.message.content.find(part => part?.type === 'tool_use') : null;
      if (!tool || typeof tool.name !== 'string') { warn('Unsupported structured payload omitted; raw source content is not displayed.'); continue; }
      entry = { protocol: 'message-events', provenance: 'observed', eventType: field(row.type, 80), tool: field(tool.name, 120) };
      // File payloads, read results and arbitrary tool outputs stay in the raw log.
      if (['Bash', 'PowerShell'].includes(tool.name) && typeof tool.input?.command === 'string') entry.command = field(tool.input.command, LIMITS.commandBytes);
      warn('Claude Code excerpts contain retained tool metadata only; detailed output is not verified.');
    } else { warn('Unsupported structured payload omitted; raw source content is not displayed.'); continue; }
    if (typeof row.timestamp === 'string' && Number.isFinite(Date.parse(row.timestamp))) entry.sourceTimestamp = field(row.timestamp, 80);
    append(entry);
  }
  flushPlain();
  if (truncated) warn('Technical evidence is a bounded truncated excerpt, not the full source log.');
  if (!entries.length) warn('No supported original technical evidence is retained for this assignment.');
  warn('Only the supplied retained assignment tail is available; earlier source records may be absent.');
  warn('Reported output and zero exit codes do not establish validated success.');
  return { entries, truncated, warnings: [...warningSet] };
}
module.exports = { technicalEvidence, redact, LIMITS };
