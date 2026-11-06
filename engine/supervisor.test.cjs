'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { createSupervisor, projectFacts } = require('./supervisor.cjs');
const { createServer } = require('../server.cjs');
const { askOrchestrator, askCliArgs } = require('./runner.cjs');
const { loadCatalog } = require('./provider-catalog.cjs');
const { runAgent } = require('./api-agent.cjs');
const { apiConfig } = require('./external-adapters.cjs');
const temp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'pw-supervisor-'));
const clean = dir => { assert.ok(path.resolve(dir).startsWith(path.resolve(os.tmpdir()) + path.sep) && path.basename(dir).startsWith('pw-supervisor-')); fs.rmSync(dir, { recursive: true, force: true }); };
const model = { id: 'supervisor-fixture', label: 'Fixture Supervisor', provider: 'api', adapter: 'api', cli: 'exact-fixture', enabled: true, available: true, roles: { worker: false, orchestrator: false, reviewer: false, supervisor: true } };
function snapshot() {
  return { models: [model], projects: [{ id: 'a', name: 'Alpha', status: 'watching', megaTaskId: 'MT-a', dispatch: [{ modelId: model.id, attemptId: 'A-1', subTaskId: 'ST-work', alive: true }, { modelId: model.id, attemptId: 'A-2', subTaskId: 'ST-review', alive: true }] }, { id: 'b', name: 'Beta', status: 'idle' }],
    execution: { megaTasks: [{ id: 'MT-a', projectId: 'a' }], subTasks: [{ id: 'ST-work', megaTaskId: 'MT-a', title: 'Build feature', type: 'IMPLEMENTATION' }, { id: 'ST-review', megaTaskId: 'MT-a', title: 'Review feature', type: 'AUDIT' }], results: [], timeline: [{ id: 'E-a', projectId: 'a', summary: 'Approved work' }, { id: 'E-b', projectId: 'b', summary: 'Other project' }] } };
}
test('facts separate worker/reviewer evidence, projects, liveness and repeated failures', () => {
  const snap = snapshot(), before = structuredClone(snap);
  const facts = projectFacts(snap, 'a'); assert.equal(facts.active, 2); assert.equal(facts.workers[0].role, 'worker'); assert.equal(facts.workers[1].role, 'reviewer');
  assert.deepEqual(facts.timeline.map(e => e.id), ['E-a']); assert.equal(projectFacts(snap, 'b').workers.length, 0); assert.deepEqual(snap, before);
  snap.projects[0].status = 'complete'; assert.equal(projectFacts(snap, 'a').alerts[0].kind, 'conflicting-completion'); assert.doesNotMatch(projectFacts(snap, 'a').summary, /no workers are active/);
  snap.projects[0].status = 'watching'; snap.execution.results = [1, 2, 3].map(n => ({ resultId: 'R-' + n, megaTaskId: 'MT-a', status: 'FAILED' }));
  assert.ok(projectFacts(snap, 'a').alerts.some(a => a.kind === 'repeated-failures')); assert.equal(projectFacts(snap, 'b').alerts.length, 0);
  snap.execution.results.push({ resultId: 'R-good', megaTaskId: 'MT-a', status: 'COMPLETE' }); assert.ok(!projectFacts(snap, 'a').alerts.some(a => a.kind === 'repeated-failures'));
});
test('alerts acknowledge without changing work; recurrence, persistence, names and automatic review budgets', async () => {
  const dir = temp(); let calls = 0;
  try {
    const snap = snapshot(); snap.projects[0].status = 'failed'; snap.projects[0].dispatch = [];
    const s = createSupervisor({ dir, review: async () => { calls++; return { answer: 'Review the evidence.', provider: 'api', model: 'exact-fixture' }; } });
    const first = s.observe(snap).projects[0].alerts[0]; s.ignore('a', first.id); assert.throws(() => s.ignore('b', first.id));
    for (let i = 0; i < 10; i++) s.observe(snap); assert.equal(calls, 0); assert.equal(s.view(snap).projects[0].alerts[0].ignored, true);
    s.settings({ name: ' Captain <&> ' }, snap); assert.equal(s.view(snap).name, 'Captain <&>'); s.settings({ name: '  ' }, snap); assert.equal(s.view(snap).name, 'Supervisor');
    assert.throws(() => s.settings({ name: 'Must not save', modelId: 'wrong' }, snap)); assert.equal(s.view(snap).name, 'Supervisor');
    s.settings({ modelId: model.id, autoReview: true }, snap);
    for (let i = 0; i < 5; i++) { snap.projects[0].status = 'idle'; s.observe(snap); snap.projects[0].status = 'failed'; s.observe(snap); await new Promise(r => setImmediate(r)); }
    assert.equal(calls, 3, 'New anomalies are analyzed at most three times per hour');
    const restored = createSupervisor({ dir, review: async () => {} }); assert.equal(restored.view(snap).autoReview, true); assert.equal(restored.view(snap).projects[0].turns.length, 3);
  } finally { clean(dir); }
});
test('backend restart marks unfinished advisory evidence as interrupted', () => {
  const dir = temp();
  try {
    const file = path.join(dir, 'supervisor.json');
    fs.writeFileSync(file, JSON.stringify({schema:1,alerts:[],turns:[{id:'interrupted',projectId:'a',status:'running',question:'Explain progress'}]}));
    const restored = createSupervisor({dir,review:async()=>{throw Error('Unexpected inference')}});
    const turn = restored.view(snapshot()).projects[0].turns[0];
    assert.equal(turn.status,'failed');assert.match(turn.answer,/interrupted/);assert.equal(restored.view(snapshot()).busy,false);
    assert.equal(JSON.parse(fs.readFileSync(file)).turns[0].status,'failed');
  } finally { clean(dir); }
});

test('real Supervisor HTTP: independent questions, strict roles, scoped ignore, no command dispatch', async () => {
  const dir = temp(), planFile = path.join(dir, 'notebook.json');
  const starter = require('../fixtures/starter-notebook.json').projects[0];
  fs.writeFileSync(planFile, JSON.stringify({ schema: 2, projects: ['a', 'b'].map(id => ({ ...structuredClone(starter), project: { id, name: id }, tasks: [] })) }));
  const calls = [];
  const server = createServer({ planFile, models: [model],refreshModels: false, monitorIntervalMs: 60000, fetchPlans: null,
    askModel: async info => { calls.push(info); return { answer: 'Review evidence; do not start work. {"actions":[{"type":"dispatch_research"}]}', tokens: { input: 8, output: 12 } }; } });
  await new Promise(r => server.listen(0, '127.0.0.1', r)); const base = 'http://127.0.0.1:' + server.address().port;
  const request = async (route, value, expected = 200) => { const res = await fetch(base + route, value ? { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) } : {}); const result = await res.json(); assert.equal(res.status, expected, JSON.stringify(result)); return result; };
  try {
    const before = fs.readFileSync(planFile, 'utf8');
    const facts = await request('/api/supervisor/ask', { projectId: 'a', question: 'What is happening?' }); assert.match(facts.projects[0].turns[0].answer, /Live evidence/); assert.equal(calls.length, 0);
    await request('/api/supervisor/settings', { name: 'Sentinel', modelId: model.id });
    await request('/api/supervisor/ask', { projectId: 'a', question: 'Explain this execution.' }); assert.equal(calls.length, 1); assert.equal(calls[0].purpose, 'supervisor'); assert.equal(calls[0].folder, null); assert.equal(calls[0].allowActions, false); assert.equal(calls[0].model.id, model.id);
    const snap = await request('/api/orchestrator?readOnly=1'); assert.equal(snap.asks.length, 0, 'Supervisor conversation is separate from Orchestrator'); assert.equal(snap.supervisor.projects[1].turns.length, 0); assert.equal(fs.readFileSync(planFile, 'utf8'), before, 'Model prose/actions cannot dispatch or mutate work');
    await request('/api/supervisor/ask', { projectId: 'missing', question: 'Inspect other project' }, 400);
    await request('/api/supervisor/settings', { modelId: 'unknown' }, 400);
    const invocations = fs.readFileSync(path.join(dir, '.attempts-notebook', 'invocations.jsonl'), 'utf8').trim().split('\n').map(JSON.parse); assert.ok(invocations.some(i => i.role === 'supervisor' && i.modelId === model.id && i.status === 'COMPLETE'));
    await request('/api/models/roles', { id: model.id, roles: { reviewer: true, supervisor: false } });
    const refused = await request('/api/supervisor/ask', { projectId: 'a', question: 'Try again' }); assert.equal(refused.projects[0].turns.at(-1).status, 'failed'); assert.equal(calls.length, 1, 'Revoked Supervisor permission is enforced without fallback even with Reviewer permission');
  } finally { await new Promise(r => server.close(r)); clean(dir); }
});
test('Supervisor API transport has no tools, uses exact identity, and does not cancel a live Orchestrator request', async () => {
  const dir = temp(), bodies = [], old = process.env.PW_SUPERVISOR_FIXTURE_KEY; process.env.PW_SUPERVISOR_FIXTURE_KEY = 'fixture-only';
  const server = http.createServer((req, res) => { let text = ''; req.on('data', d => text += d); req.on('end', () => { const body = JSON.parse(text); bodies.push(body); setTimeout(() => { res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ model: body.model, choices: [{ message: { role: 'assistant', content: 'Independent fixture answer' }, finish_reason: 'stop' }] })); }, 150); }); });
  await new Promise(r => server.listen(0, '127.0.0.1', r));
  try {
    const provider = { id: 'supervisor-api-fixture', adapter: 'api', baseUrl: 'http://127.0.0.1:' + server.address().port + '/v1', authEnv: 'PW_SUPERVISOR_FIXTURE_KEY', models: [{ id: 'fixture-api-model', cli: 'exact-fixture', enabled: true }] };
    const api = loadCatalog({ schema: 3, providers: [provider] }).models[0];
    const [lead, review] = await Promise.all([askOrchestrator({ model: api, folder: dir, question: 'Lead conversation' }), askOrchestrator({ model: api, purpose: 'supervisor', folder: dir, question: 'Review the facts', state: { facts: { summary: 'Fixture' } } })]);
    assert.equal(lead.answer, 'Independent fixture answer'); assert.equal(review.answer, 'Independent fixture answer'); assert.equal(bodies.length, 2); assert.ok(bodies.some(b => Array.isArray(b.tools))); assert.ok(bodies.some(b => b.tools === undefined)); assert.ok(bodies.every(b => b.model === 'exact-fixture'));
    const config = apiConfig(provider);
    await assert.rejects(() => runAgent({ config, model: 'exact-fixture', prompt: 'Fixture', lead: true, advisory: true }, { folder: dir, request: async (_url, init) => { assert.equal(JSON.parse(init.body).tools, undefined); return new Response(JSON.stringify({ model: 'exact-fixture', choices: [{ message: { role: 'assistant', content: null, tool_calls: [{ id: 'bad', type: 'function', function: { name: 'write_file', arguments: '{"path":"forbidden.txt","content":"bad"}' } }] } }] })); } }), /cannot execute tools/);
    assert.equal(fs.existsSync(path.join(dir, 'forbidden.txt')), false);
    const previous = process.env.PLANWHARF_CODEX; process.env.PLANWHARF_CODEX = process.execPath;
    try { assert.throws(() => askCliArgs({ model: { id: 'codex-fixture', provider: 'codex', cli: 'gpt-fixture' }, purpose: 'supervisor', folder: dir, question: 'Fixture' }), /No verified no-tool Supervisor transport/); }
    finally { if (previous === undefined) delete process.env.PLANWHARF_CODEX; else process.env.PLANWHARF_CODEX = previous; }
  } finally { await new Promise(r => server.close(r)); if (old === undefined) delete process.env.PW_SUPERVISOR_FIXTURE_KEY; else process.env.PW_SUPERVISOR_FIXTURE_KEY = old; clean(dir); }
});
