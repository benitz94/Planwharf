'use strict';
// Claude can return the worker result as schema-checked final output, which saves
// the file-writing tool call at the end of an attempt. Grok is excluded on purpose:
// on 2026-11-03, grok-4.7 with --json-schema made one model call and reported files
// and passing tests that did not exist. See docs/PROJECT-AUTONOMY.md.

const ADAPTERS = new Set(['claude']);
const MAX_OUTPUT_CHARS = 8 * 1024 * 1024;
const strings = { type: 'array', items: { type: 'string' } };

function supportsFinalResult(model) {
  return ADAPTERS.has(model?.adapter || model?.provider);
}

function resultSchema(identity) {
  const fixed = Object.fromEntries(['resultId', 'attemptId', 'subTaskId', 'megaTaskId'].map(key => [key, { type: 'string', enum: [identity[key]] }]));
  return {
    type: 'object',
    required: [...Object.keys(fixed), 'status', 'summary', 'filesChanged', 'tests', 'blockers', 'nextAction'],
    properties: {
      ...fixed,
      status: { type: 'string', enum: ['COMPLETE', 'FAILED', 'BLOCKED'] },
      summary: { type: 'string' },
      filesChanged: strings,
      tests: { type: 'array', items: { type: 'object', required: ['command', 'status', 'evidence'], properties: { command: { type: 'string' }, status: { type: 'string', enum: ['PASSED', 'FAILED', 'NOT_RUN'] }, evidence: { type: 'string' } } } },
      blockers: strings,
      nextAction: { type: 'string' },
      reportPath: { type: 'string' },
      sources: strings,
      recommendedNextAction: { type: 'string' }
    }
  };
}

// Format captured from Claude Code stream-json output, 2026-11-03.
function readFinalResult(adapter, output) {
  if (typeof output !== 'string' || !output.trim()) return null;
  const parse = text => { try { return JSON.parse(text); } catch { return null; } };
  if (adapter !== 'claude') return null;
  const lines = output.trim().split(/\r?\n/);
  for (let index = lines.length - 1; index >= 0; index -= 1) {
    const event = parse(lines[index]);
    if (event?.type === 'result') return event.is_error ? null : event.structured_output ?? null;
  }
  return null;
}

module.exports = { MAX_OUTPUT_CHARS, supportsFinalResult, resultSchema, readFinalResult };
