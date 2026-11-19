'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Plan = require('../plan.js');
const { createServer } = require('../server.cjs');
const { diagnoseProvider } = require('./provider-diagnostics.cjs');
const { parseModels, listAntigravity } = require('./antigravity.cjs');
async function fixture(extra = {}) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-grill-chat-')), file = path.join(dir, 'notebook.json');
  const plan = Plan.example(); plan.tasks = []; plan.project = { id: 'isolated', name: 'Isolated Grill' }; fs.writeFileSync(file, JSON.stringify(plan));
  const calls = [], launches = [];
  const server = createServer({ planFile: file, attemptsDir: path.join(dir, 'attempts'), models: [{ id: 'lead', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'default' }], refreshModels: false, fetchPlans: null,
    readRepository: () => ({ local: {}, remote: {}, comparison: {} }), inspectFolder: () => ({ git: false, dirty: [], names: [] }),
    askModel: info => { calls.push(info); return { answer: 'Q1: What is the first outcome? Recommended: a small working prototype.' }; },
    spawnWorker: info => { launches.push(info); throw new Error('Grill must never launch a worker'); }, ...extra });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const request = async (route, input, status = 200) => {
    const response = await fetch('http://127.0.0.1:' + server.address().port + route, input === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) });
    const data = await response.json(); assert.equal(response.status, status, JSON.stringify(data)); return data;
  };
  return { calls, launches, plan, file, request, async close() { await new Promise(resolve => server.close(resolve)); assert.ok(dir.startsWith(os.tmpdir() + path.sep)); fs.rmSync(dir, { recursive: true, force: true }); } };
}
test('Grill starts a real lead turn with Grill me instructions and keeps follow-up decisions in chat without a source folder', async () => {
  const f = await fixture();
  try {
    await f.request('/api/orchestrator/lead', { id: 'lead' });
    const first = await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-session', question: 'Grill me', grillKind: 'me' });
    assert.match(first.answer, /Q1/); assert.equal(first.asks[0].order, null); assert.equal(first.asks[0].grillKind, 'me');
    assert.equal(f.calls[0].grillKind, 'me'); assert.equal(f.calls[0].allowActions, false); assert.match(f.calls[0].extraPrompt, /ACTIVE GRILL ME SESSION/); assert.doesNotMatch(f.calls[0].extraPrompt, /Matt/);
    assert.equal(f.calls[0].state.currentRequest.projectId, 'isolated'); assert.equal(f.calls[0].state.currentRequest.grillKind, 'me');
    await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-session', question: 'Implement the prototype with a minimal API and no database.' });
    assert.equal(f.calls[1].grillKind, 'me'); assert.match(JSON.stringify(f.calls[1].state.history), /Grill me/);
    assert.deepEqual(JSON.parse(fs.readFileSync(f.file)), f.plan, 'Grill never prepares implementation tasks or rewrites project content');
    assert.equal(f.launches.length, 0);
    await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'different-session', question: 'What is this project about?' });
    assert.equal(f.calls[2].grillKind, undefined, 'Grill does not leak into another chat session');
    const stopped = await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-session', question: 'Stop grill' });
    assert.equal(stopped.snapshot.grillPropose, false, 'The clarification interview stays off: it was removed from Planwharf');
    await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-session', question: 'What is the current goal?' });
    assert.equal(f.calls[3].grillKind, undefined, 'An explicit stop ends grilling');
    await f.request('/api/orchestrator/ask', { projectId: 'isolated', question: 'Grill me', grillKind: 'invalid' }, 400);
  } finally { await f.close(); }
});
test('a missing lead is reported in the conversation', async () => {
  const f = await fixture();
  try {
    const result = await f.request('/api/orchestrator/ask', { projectId: 'isolated', question: 'Grill me' });
    assert.match(result.answer, /Pick an orchestrator/); assert.equal(f.calls.length, 0);
  } finally { await f.close(); }
});
test('unrecognized Antigravity model is classified precisely and never replaced with another provider', async () => {
  const models = [{ id: 'lead', provider: 'antigravity', cli: 'gemini-3.1-pro-high', enabled: true, available: true, kind: 'default' }, { id: 'other', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'default' }];
  const diagnostic = diagnoseProvider(models[0], 'invalid model selection (--model "gemini-3.1-pro-high" --effort ""): model gemini-3.1-pro-high is not recognized as a known model or custom model in settings');
  assert.equal(diagnostic.code, 'PROVIDER_MODEL_UNAVAILABLE');
  const f = await fixture({ models, askModel: () => ({ answer: diagnostic.message, failure: 'provider', diagnostic }) });
  try {
    await f.request('/api/orchestrator/lead', { id: 'lead' });
    const result = await f.request('/api/orchestrator/ask', { projectId: 'isolated', question: 'Grill me' });
    assert.equal(result.snapshot.models.find(model => model.id === 'lead').available, false); assert.equal(result.snapshot.orchestratorId, null); assert.equal(result.snapshot.selectedOrchestratorId, 'lead');
    assert.match(result.answer, /PROVIDER_MODEL_UNAVAILABLE/);
  } finally { await f.close(); }
});
test('Antigravity discovery only accepts exact listed IDs and keeps auth failures unread', async () => {
  assert.deepEqual(parseModels('gemini-3.1-pro\nclaude-sonnet-4.6\ngemini-3.1-pro\n').ids, ['gemini-3.1-pro', 'claude-sonnet-4.6']);
  // agy writes the readable name next to each id: Planwharf keeps it for the menus.
  const named = parseModels('gemini-3.8-flash-high     Gemini 3.8 Flash (High)\nclaude-opus-4-6-thinking  Claude Opus 4.6 (Thinking)\ngpt-oss-120b-medium\n');
  assert.deepEqual(named.models, [{ cli: 'gemini-3.8-flash-high', label: 'Gemini 3.8 Flash (High)' }, { cli: 'claude-opus-4-6-thinking', label: 'Claude Opus 4.6 (Thinking)' }, { cli: 'gpt-oss-120b-medium' }]);
  assert.equal(parseModels('Gemini Pro - high effort').ok, false);
  const calls = [];
  const result = await listAntigravity({ command: 'isolated-agy', run: (command, args, opts, callback) => { calls.push(args); callback(new Error('failed'), '', 'Please sign in to view available models.'); } });
  assert.deepEqual(calls, [['models']]); assert.equal(result.ok, false); assert.match(result.reason, /Sign in/);
});

test('explicit Start after chat grilling prepares the discussed task without reopening the old questionnaire', async () => {
  const f = await fixture();
  try {
    f.plan.project.folder = path.dirname(f.file); fs.writeFileSync(f.file, JSON.stringify(f.plan));
    await f.request('/api/orchestrator/lead', { id: 'lead' });
    await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-start', question: 'Grill me' });
    await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-start', question: 'The volunteer team needs a daily schedule.' });
    const started = await f.request('/api/orchestrator/ask', { projectId: 'isolated', chatSessionId: 'grill-start', question: 'Start' });
    assert.equal(started.asks[0].order, 'start'); assert.equal(started.asks[0].grillKind, null);
    const task = JSON.parse(fs.readFileSync(f.file)).tasks.at(-1);
    assert.equal(task.source, 'chat'); assert.match(task.detail, /The volunteer team needs a daily schedule/);
    assert.equal(f.calls.length, 2, 'Start does not invoke a second automatic Grill round');
    assert.equal(f.launches.length, 0, 'Approval remains necessary before worker dispatch');
  } finally { await f.close(); }
});
