'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
// Command-line building must not depend on provider CLIs installed on this machine: point each one at Node.
for (const provider of ['GROK', 'CODEX', 'CLAUDE', 'ANTIGRAVITY']) if (!process.env['PLANWHARF_' + provider]) process.env['PLANWHARF_' + provider] = process.execPath;
const { parseArgs, promptFor, patchStatus, isProcessAlive, killWorker, abortAsk, askPrompt, askCliArgs, cliModelOf, parseAskLine, createAskStream, dropThinkingPrefix } = require('./runner.cjs');

const opts = parseArgs(['node', 'runner.cjs', '--folder', 'D:\\demo', '--result', 'D:\\out.txt', '--title', 'Build it', '--detail', 'Add tests.', '--worker', 'Grok', '--attempt', 'attempt-1']);
assert.equal(opts.folder, 'D:\\demo');
assert.equal(opts.title, 'Build it');
assert.match(promptFor(opts), /Build it/);
assert.match(promptFor(opts), /Add tests/);

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-runner-'));
const file = path.join(dir, 'result.txt');
fs.writeFileSync(file, 'JOB: x\nSTATUS: WATCHING\n');
patchStatus(file, 'COMPLETE', { worker: 'Grok', attempt: 'attempt-1', exitCode: 0 });
assert.match(fs.readFileSync(file, 'utf8'), /STATUS: COMPLETE/);
assert.equal(isProcessAlive(process.pid), true);
assert.equal(isProcessAlive(99999999), false);
assert.equal(killWorker(99999999), false);
assert.equal(abortAsk(), false);
assert.equal(cliModelOf({ id: 'gpt-6-astra', cli: 'grok-4.7' }), null);
assert.equal(cliModelOf({ id: 'gpt-6-astra' }), null);
const spoken = askPrompt({
  question: 'How far did we go with RaceEngineer?',
  folder: 'D:\\benit\\Desktop\\Projects\\RaceEngineer',
  model: { id: 'gpt-6-astra', label: 'GPT-6 Astra', alwaysApprove: true },
  state: { focus: { name: 'RaceEngineer', folder: 'D:\\benit\\Desktop\\Projects\\RaceEngineer', goal: 'Local race telemetry', open: ['Which car?'], checkpoint: { reason: 'stop', path: 'x.json' } } }
});
assert.match(spoken, /GPT-6 Astra/);
assert.match(spoken, /RaceEngineer/);
assert.match(spoken, /Project folder/);
assert.match(spoken, /Do not recite STATUS/);
assert.doesNotMatch(spoken, /Answer ONLY the user question from LIVE STATE/);
assert.match(spoken, /thinking/);
assert.match(spoken, /Local race telemetry/);
assert.match(spoken, /Which car/);
assert.match(spoken, /checkpoint/);
const stream = createAskStream();
stream.push('{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"I will inspect git.\\n"}}}\n');
stream.push('{"type":"stream_event","event":{"delta":{"type":"text_delta","text":"RaceEngineer is a local prototype."}}}\n');
stream.push('{"type":"result","duration_ms":42100,"result":"RaceEngineer is a local prototype."}\n');
const parsed = stream.finish();
assert.match(parsed.thinking, /inspect git/i);
assert.equal(parsed.answer, 'RaceEngineer is a local prototype.');
assert.equal(parsed.ms, 42100);
assert.equal(parseAskLine({ thinking: '', answer: '' }, { type: 'result', duration_ms: 12, result: 'ok' }).ms, 12);
const leaked = createAskStream();
leaked.push('{"type":"stream_event","event":{"delta":{"type":"thinking_delta","thinking":"I will inspect git.\\n"}}}\n');
leaked.push('{"type":"result","duration_ms":900,"result":"I will inspect git.\\nRaceEngineer is a local prototype."}\n');
const cleaned = leaked.finish();
assert.equal(cleaned.answer, 'RaceEngineer is a local prototype.');
assert.match(cleaned.thinking, /inspect git/i);
assert.equal(dropThinkingPrefix('I will inspect git.\nDone.', 'I will inspect git.'), 'Done.');
assert.match(askPrompt({ question: 'How far?', folder: 'D:\\x', model: { label: 'Astra' }, state: {} }), /Do not start the answer with I will inspect/);
assert.match(spoken, /You coordinate/);
assert.match(spoken, /never edit/);
assert.match(spoken, /full local tool access, network access/);
// Without the user's OK the lead is told its tools are restricted, matching how it is launched.
assert.match(askPrompt({ question: 'How far?', folder: 'D:\\x', model: { label: 'Astra' }, state: {} }), /restricted by the configured model permission policy/);
assert.match(spoken, /private GitHub repositories/);
assert.doesNotMatch(spoken, /Stay inside it\. Read only/);
assert.doesNotMatch(spoken, /unless they explicitly order a change/);
const cliArgs = askCliArgs({ question: 'How far?', folder: 'D:\\x', model: { id: 'grok', provider: 'grok', cli: 'grok-4.6', alwaysApprove: true } }).args;
assert.equal(cliArgs.includes('--always-approve'), true);
assert.ok(cliArgs.includes('bypassPermissions'));
assert.ok(cliArgs.includes('--no-subagents'));
assert.equal(cliArgs[cliArgs.indexOf('--max-turns') + 1], '8');
console.log('OK: runner args, prompt, result patch, pid check.');
