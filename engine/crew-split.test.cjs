'use strict';
// The lead splits the work into parts that can run at the same time, each with the files it may change.
const test = require('node:test');
const assert = require('node:assert/strict');
const autonomy = require('./autonomy.cjs');

const builders = [{ id: 'opus', label: 'Opus 5.5', provider: 'claude', tier: 'premium', left: 60 }, { id: 'haiku', label: 'Haiku 5.5', provider: 'claude', tier: 'light', left: 60 },
  { id: 'luna', label: 'GPT-6 Luna', provider: 'codex', tier: 'light', left: 70 }];
const verifiers = [{ id: 'sol', label: 'GPT-6.1 Sol', provider: 'codex', tier: 'default', left: 70 }, { id: 'opus', label: 'Opus 5.5', provider: 'claude', tier: 'premium', left: 60 }];

test('the split question gives the lead the rules, the crew and the project files', () => {
  const prompt = autonomy.splitPrompt({ name: 'Engine', goal: 'Port three modules', builders, verifiers, files: ['engine/quota.js', 'engine/skills.cjs'], said: 'I suggest Opus for the hard part.' });
  assert.match(prompt, /up to 10 workers at the same time/);
  assert.match(prompt, /must touch different files/);
  assert.match(prompt, /integration part/);
  assert.match(prompt, /opus \(Opus 5\.5, provider claude, premium, 60% quota left\)/);
  assert.match(prompt, /engine\/quota\.js/);
  assert.match(prompt, /I suggest Opus for the hard part/);
  assert.match(prompt, /"parts":\[/);
});

test('a valid split is read; overlapping parts are put in order; the check of the whole comes last', () => {
  const reply = JSON.stringify({ why: 'Two light ports in parallel, the hard one on Opus.', verify: 'sol', parts: [
    { title: 'Port quota', detail: 'Port engine/quota.js.', files: ['native/Planwharf.Engine/Quota.cs', 'native/Planwharf.Engine.Tests/QuotaTests.cs'], dependsOn: [], size: 'light', model: 'luna' },
    { title: 'Port skills', detail: 'Port engine/skills.cjs.', files: ['native/Planwharf.Engine/Skills.cs'], dependsOn: [], size: 'heavy', model: 'opus' },
    { title: 'Skills tests', detail: 'Tests for skills.', files: ['native/Planwharf.Engine'], dependsOn: [], size: 'light', model: 'haiku' }] });
  const split = autonomy.parseSplit(reply, { builders: builders.map(m => m.id), verifiers: verifiers.map(m => m.id) });
  assert.equal(split.parts.length, 3);
  assert.deepEqual(split.parts.map(part => part.model), ['luna', 'opus', 'haiku']);
  // Part 3 changes the whole engine folder, which holds the files of parts 1 and 2: it waits for both.
  assert.deepEqual(split.parts[2].dependsOn, [0, 1]);
  assert.deepEqual(split.parts[1].dependsOn, [], 'separate files run at the same time');
  assert.equal(split.verify, 'sol');
  assert.equal(split.why, 'Two light ports in parallel, the hard one on Opus.');
});

test('a split that breaks the rules is refused, and the work keeps its usual plan', () => {
  const ok = { title: 'A', detail: 'Do A.', files: ['a.cs'], dependsOn: [], size: 'normal', model: 'opus' };
  const allowed = { builders: builders.map(m => m.id), verifiers: verifiers.map(m => m.id) };
  const read = parts => autonomy.parseSplit(JSON.stringify({ parts, verify: 'sol', why: 'x' }), allowed);
  assert.equal(read([{ ...ok, model: 'nobody' }]), null, 'a model not allowed to build');
  assert.equal(read([{ ...ok, files: [] }]), null, 'a part with no files');
  assert.equal(read([{ ...ok, files: ['../outside.cs'] }]), null, 'a file outside the project');
  assert.equal(read([{ ...ok, files: ['/etc/passwd'] }]), null, 'an absolute path');
  assert.equal(read([{ ...ok, dependsOn: [0] }]), null, 'a part that waits for itself');
  assert.equal(read([{ ...ok, size: 'huge' }]), null, 'an unknown size');
  assert.equal(read(Array.from({ length: 11 }, () => ok)), null, 'more than 10 parts');
  assert.equal(read([]), null, 'no part');
  assert.equal(autonomy.parseSplit(JSON.stringify({ parts: [ok], verify: 'haiku', why: 'x' }), allowed), null, 'a check by a model not allowed to verify');
  assert.equal(autonomy.parseSplit('I would split it in three.', allowed), null, 'no JSON');
});

test('full loop: the lead splits the work, the parts run together, each within its files, then one check of the whole', async () => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-split-')), folder = path.join(dir, 'project');
  fs.mkdirSync(path.join(folder, 'test'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'test', 'ok.test.cjs'), "require('node:test')('ok', () => {});\n");
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan: provider, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('a', 'codex', 'gpt-6-luna'), model('b', 'grok', 'grok-4.7'), model('c', 'claude', 'claude-opus-5-5')];
  const launches = [], asks = [];
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, refreshModels: false, fetchPlans: async () => [], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Slice by ' + info.worker.id, filesChanged: [], tests: [], blockers: [], nextAction: 'None' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async info => {
      asks.push(info.question);
      if (/Your crew can work in parallel/.test(info.question)) return { answer: JSON.stringify({ why: 'Two separate modules at once.', verify: 'b', parts: [
        { title: 'Port quota', detail: 'Port the quota module.', files: ['src/quota.cs'], dependsOn: [], size: 'light', model: 'a' },
        { title: 'Port skills', detail: 'Port the skills module.', files: ['src/skills.cs'], dependsOn: [], size: 'heavy', model: 'c' }] }) };
      return { answer: '{"done":true,"next":null,"assumptions":["split worked"]}' };
    } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return response.json(); };
  try {
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: 'Split', direction: 'Port two modules and write tests.', folder }, createFolder: false });
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'a' });
    await api('/api/focus', { projectId });
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    let view, deadline = Date.now() + 60000;
    while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (view.autonomy?.[projectId]?.stopReason) break; await new Promise(resolve => setTimeout(resolve, 100)); }
    const timeline = view.execution.timeline.filter(event => event.projectId === projectId);
    assert.equal(view.autonomy[projectId].stopReason, 'GOAL_REACHED', JSON.stringify(timeline.map(event => event.type + ' ' + event.summary)));
    assert.ok(timeline.some(event => event.summary === 'Lead split the work into 2 parts and a check of the whole: Port quota (A) · Port skills (C) · ' + launches[2].job.title + ' (B). Why: Two separate modules at once.'),
      JSON.stringify(timeline.filter(event => event.type === 'MEGATASK_PROPOSED').map(event => event.summary)));
    assert.deepEqual(launches.slice(0, 2).map(launch => launch.worker.id).sort(), ['a', 'c'], 'both parts start together, before the check');
    assert.equal(launches[2].worker.id, 'b', 'the check of the whole comes last');
    assert.match(launches.find(launch => launch.worker.id === 'a').job.detail, /Your scope: change only these files or folders, relative to the project folder: src\/quota\.cs\./);
    assert.doesNotMatch(launches[2].job.detail, /Your scope/, 'the check of the whole may touch every file');
    assert.match(asks.find(question => /Your crew can work in parallel/.test(question)), /test\/ok\.test\.cjs|Project files/);
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a model under the switch threshold is not offered for a part, so it is not moved off right away', async () => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-crew-split-')), folder = path.join(dir, 'project');
  fs.mkdirSync(path.join(folder, 'test'), { recursive: true });
  fs.writeFileSync(path.join(folder, 'test', 'ok.test.cjs'), "require('node:test')('ok', () => {});\n");
  const planFile = path.join(dir, 'plan.json');
  const starter = JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), 'utf8'));
  for (const project of starter.projects) project.tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(starter));
  const model = (id, provider, cli) => ({ id, label: id.toUpperCase(), provider, adapter: provider, cli, enabled: true, available: true, kind: 'premium', cost: 8, plan: provider, providerCatalog: true, orchestratorEligible: true,
    capabilities: { canOrchestrate: true, canCode: true, canUseTools: true }, roles: { orchestrator: true, worker: true, verifier: true, reviewer: false, supervisor: false }, policy: { alwaysApprove: true, minRemainingPercent: null, warningRemainingPercent: null, fallbackModelId: null } });
  const models = [model('a', 'codex', 'gpt-6-luna'), model('b', 'grok', 'grok-4.7'), model('c', 'claude', 'claude-opus-5-5')];
  const launches = [], asks = [];
  const denied = () => { throw Error('windows are not used here'); };
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models, refreshModels: false, fetchPlans: async () => [{ id: 'ChatGPT', label: 'ChatGPT', readable: true, leftPct: 15, windows: [{ name: '5 hours', leftPct: 15 }] }], monitorIntervalMs: 50,
    openFolder: denied, openUrl: denied, openWatchWindow: denied, openPlanWindow: denied,
    spawnWorker(info) {
      launches.push(info);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true }); fs.writeFileSync(info.logPath, 'worked\n');
      fs.mkdirSync(path.dirname(info.structuredResultPath), { recursive: true }); fs.writeFileSync(info.resultPath, 'done');
      fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status: 'COMPLETE', summary: 'Slice by ' + info.worker.id, filesChanged: [], tests: [], blockers: [], nextAction: 'None' }));
      fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: 0, resultVerified: true, signal: null, provider: info.worker.provider, model: info.worker.cli, createdAt: new Date().toISOString() }));
      return { pid: 900000 + launches.length };
    },
    isProcessAlive: () => false, killWorker: () => {}, abortAsk: () => {},
    askModel: async info => {
      asks.push(info.question);
      if (/Your crew can work in parallel/.test(info.question)) return { answer: JSON.stringify({ why: 'Two separate modules at once.', verify: 'c', parts: [
        { title: 'Port quota', detail: 'Port the quota module.', files: ['src/quota.cs'], dependsOn: [], size: 'light', model: 'b' },
        { title: 'Port skills', detail: 'Port the skills module.', files: ['src/skills.cs'], dependsOn: [], size: 'heavy', model: 'c' }] }) };
      return { answer: '{"done":true,"next":null,"assumptions":["split worked"]}' };
    } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = 'http://127.0.0.1:' + server.address().port;
  const api = async (route, data) => { const response = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); return response.json(); };
  try {
    const current = await api('/api/plan');
    const created = await api('/api/projects', { version: current.version, project: { name: 'Split', direction: 'Port two modules and write tests.', folder }, createFolder: false });
    const projectId = created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead', { id: 'a' });
    await api('/api/focus', { projectId });
    // Planwharf reads the quota first, as it does on its own a few seconds after starting.
    await api('/api/orchestrator');
    await api('/api/orchestrator/mode', { mode: 'auto' });
    await api('/api/orchestrator/autonomy', { projectId, enabled: true });
    let view, deadline = Date.now() + 60000;
    while (Date.now() < deadline) { view = await api('/api/orchestrator'); if (view.autonomy?.[projectId]?.stopReason) break; await new Promise(resolve => setTimeout(resolve, 100)); }
    const question = asks.find(text => /Your crew can work in parallel/.test(text));
    assert.ok(question, 'the lead was asked to split the work');
    assert.doesNotMatch(question, /\ba \(A,/, 'the model at 15% is not offered: ' + question.split('\n').filter(line => /allowed/.test(line)).join(' / '));
    assert.match(question, /\bb \(B,/);
    assert.match(question, /\bc \(C,/);
  } finally { await server.shutdown(); server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
