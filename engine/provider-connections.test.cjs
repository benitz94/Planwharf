'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { connection } = require('./provider-connections.cjs');
const { createServer } = require('../server.cjs');
const input = { id: 'deepseek-api', label: 'DeepSeek', adapter: 'api', baseUrl: 'https://api.deepseek.com', authEnv: 'TEST_DEEPSEEK_KEY', apiProtocol: 'openai', modelIds: ['exact-model', 'another-model'], toolsEnabled: true };
test('connection validation rejects secrets, ambiguous IDs and unsupported CLI contracts', () => {
  assert.equal(connection(input).policy.alwaysApprove, false);
  for (const patch of [{ apiKey: 'secret' }, { id: '../bad' }, { baseUrl: 'https://key:secret@example.com' }, { modelIds: ['a','a'] }, { toolsEnabled: undefined }]) assert.throws(() => connection({ ...input, ...patch }));
  assert.throws(() => connection({ id: 'cli', label: 'CLI', adapter: 'configured-cli', modelIds: ['exact'], toolsEnabled: true, cliCommand: process.execPath, cliArgs: [], restrictedArgs: [], yoloArgs: [], outputProtocol: 'codex-jsonl' }));
});
test('connections and provider policies persist without provider calls and future siblings inherit settings', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-connections-'));
  const planFile = path.join(dir, 'notebook.json');
  const plan = structuredClone(require('../fixtures/starter-notebook.json')); plan.projects = [plan.projects[0]];
  plan.projects[0].project = { id: 'isolated', name: 'Isolated connection test', folder: dir }; plan.projects[0].tasks = [];
  fs.writeFileSync(planFile, JSON.stringify(plan));
  let server, base;
  const start = async () => {
    server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), models: [],refreshModels: false, monitorIntervalMs: 60000,
      askModel: () => { throw Error('No inference allowed'); }, spawnWorker: () => { throw Error('No dispatch allowed'); }, fetchPlans: async () => [] });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve)); base = 'http://127.0.0.1:' + server.address().port;
  };
  const request = async (route, value, status = 200) => {
    const response = await fetch(base + route, value === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    const result = await response.json(); assert.equal(response.status, status, JSON.stringify(result)); return result;
  };
  try {
    await start();
    let view = await request('/api/providers/connections', input);
    assert.equal(view.providers[0].id, input.id); assert.equal(view.models.length, 2);
    assert.equal(view.models.every(m => m.alwaysApprove === false), true);
    await request('/api/providers/connections', input, 400);
    await request('/api/providers/policy', { id: input.id, policy: { minRemainingPercent: 30, warningRemainingPercent: 40, fallbackProviderId: 'missing', alwaysApprove: true } }, 400);
    await request('/api/providers/policy', { id: 'missing', policy: {} }, 400);
    const policy = { minRemainingPercent: 30, warningRemainingPercent: 40, fallbackProviderId: null, alwaysApprove: true };
    view = await request('/api/providers/policy', { id: input.id, policy });
    assert.equal(view.models.every(m => m.alwaysApprove === true && m.policy.minRemainingPercent === 30), true);
    await new Promise(resolve => server.close(resolve));
    const file = path.join(dir, 'attempts', 'provider-connections.json');
    const saved = JSON.parse(fs.readFileSync(file)); saved[0].modelIds.push('future-model'); fs.writeFileSync(file, JSON.stringify(saved));
    await start(); view = await request('/api/orchestrator');
    assert.equal(view.models.length, 3); assert.equal(view.models.every(m => m.policy.minRemainingPercent === 30 && m.alwaysApprove === true), true);
    assert.equal(view.models.every(m => m.usagePolicy.state === 'unread'), true);
    assert.deepEqual(view.providers[0].policy, policy);
    assert.deepEqual(JSON.parse(fs.readFileSync(planFile)), plan, 'Personal source/notebook must not be modified by connection setup');
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});
