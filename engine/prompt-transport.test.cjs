'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { askOrchestrator, askPrompt, promptFor } = require('./runner.cjs');

const hash = text => crypto.createHash('sha256').update(text, 'utf8').digest('hex');
const model = { provider: 'codex', cli: 'gpt-6-astra', label: 'GPT-6 Astra' };
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-prompt-transport-'));
const savedOverride = process.env.PLANWHARF_CODEX;

// Node runs the isolated script named "exec" as our CLI, preserving the actual
// provider argv and OS pipe behavior without contacting any model or project.
const cliFile = path.join(dir, 'exec');
const receivingCli = `
const fs = require('node:fs');
const crypto = require('node:crypto');
let prompt = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', chunk => { prompt += chunk; });
process.stdin.on('end', () => {
  fs.writeFileSync('received.json', JSON.stringify({ args: process.argv.slice(2), prompt }));
  const text = crypto.createHash('sha256').update(prompt, 'utf8').digest('hex');
  console.log(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text } }));
});
`;

async function main() {
  process.env.PLANWHARF_CODEX = process.execPath;
  fs.writeFileSync(cliFile, receivingCli);
  const historyText = 'Prior instruction: Unicode telemetry \u{1f3ce}\u{fe0f} \u00e8 \u65e5\u672c\u8a9e.\n'.repeat(3000);
  const state = { focus: { name: 'Isolated fixture' }, history: [{ user: historyText, orchestrator: 'Prior answer.' }] };
  const before = JSON.stringify(state);
  const info = { model, folder: dir, state, question: 'Consider this long conversation in full.' };
  const expected = askPrompt(info);
  assert.ok(expected.length > 32767);
  assert.equal(expected.split('Prior instruction:').length - 1, 3000, 'History must appear once');
  assert.equal(JSON.stringify(state), before, 'Prompt assembly must not mutate history');
  const lead = await askOrchestrator(info);
  assert.equal(lead.answer, hash(expected), 'Real CLI child receives the complete UTF-8 conversation');
  const receivedLead = JSON.parse(fs.readFileSync(path.join(dir, 'received.json'), 'utf8'));
  assert.equal(receivedLead.prompt, expected);
  assert.equal(receivedLead.args.at(-1), '-');
  assert.ok(receivedLead.args.every(arg => arg.length < 32767));
  assert.equal(receivedLead.args[receivedLead.args.indexOf('-m') + 1], 'gpt-6-astra');

  const opts = { folder: dir, result: path.join(dir, 'worker-result.txt'), title: 'Isolated worker', detail: historyText, worker: 'Fixture', attempt: 'isolated', model };
  const optsFile = path.join(dir, 'worker-input.json');
  fs.writeFileSync(optsFile, JSON.stringify(opts));
  const bootstrap = path.join(dir, 'worker-bootstrap.cjs');
  fs.writeFileSync(bootstrap, `
const opts = require('./worker-input.json');
const args = ['node', 'runner'];
// A worker only starts inside its linked project folder.
args.push('--linked-folder', opts.folder);
for (const key of ['folder', 'result', 'title', 'detail', 'worker', 'attempt']) args.push('--' + key, opts[key]);
args.push('--model-json', JSON.stringify(opts.model));
require(${JSON.stringify(path.join(__dirname, 'runner.cjs'))}).run(args);
`);
  const worker = spawnSync(process.execPath, [bootstrap], { encoding: 'utf8', timeout: 10000, env: process.env });
  assert.equal(worker.error, undefined);
  assert.equal(worker.status, 0, worker.stderr);
  assert.match(fs.readFileSync(opts.result, 'utf8'), /STATUS: COMPLETE/);
  const receivedWorker = JSON.parse(fs.readFileSync(path.join(dir, 'received.json'), 'utf8'));
  assert.equal(receivedWorker.prompt, promptFor(opts));
  assert.equal(receivedWorker.args.at(-1), '-');

  // Exercise a provider exiting while its large input is still being written.
  fs.writeFileSync(cliFile, "process.stderr.write('isolated early exit'); process.exit(7);\n");
  const rejected = await askOrchestrator({ model, folder: dir, question: 'x'.repeat(4 * 1024 * 1024) });
  assert.match(rejected.answer, /CLI failed/);
  opts.detail = 'x'.repeat(4 * 1024 * 1024);
  fs.writeFileSync(optsFile, JSON.stringify(opts));
  fs.writeFileSync(opts.result, 'STATUS: WATCHING\n');
  const failedWorker = spawnSync(process.execPath, [bootstrap], { encoding: 'utf8', timeout: 10000, env: process.env });
  assert.equal(failedWorker.error, undefined);
  assert.notEqual(failedWorker.status, 0);
  assert.match(fs.readFileSync(opts.result, 'utf8'), /STATUS: FAILED/);
  assert.doesNotMatch(failedWorker.stderr, /Unhandled 'error' event/);
  console.log('OK: full long Unicode lead and worker prompts over real stdin pipes; early provider exit is handled.');
}

main().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => {
  if (savedOverride === undefined) delete process.env.PLANWHARF_CODEX;
  else process.env.PLANWHARF_CODEX = savedOverride;
  fs.rmSync(dir, { recursive: true, force: true });
});
