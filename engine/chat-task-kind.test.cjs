'use strict';
// A chat message always becomes an implementation task, including one that says review.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Plan = require('../plan.js');
const { createServer } = require('../server.cjs');

test('a chat message that says review makes an implementation task', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-review-chat-'));
  const planFile = path.join(dir, 'notebook.json');
  const plan = Plan.example();
  plan.tasks = [];
  plan.project = { ...plan.project, id: 'isolated', name: 'Isolated' };
  fs.writeFileSync(planFile, JSON.stringify(plan));
  const server = createServer({
    planFile,
    attemptsDir: path.join(dir, 'attempts'),
    refreshModels: false,
    fetchPlans: async () => [],
    monitorIntervalMs: 60000,
    models: [{ id: 'lead', label: 'Lead', provider: 'codex', cli: 'gpt-fixture', enabled: true, available: true, kind: 'premium' }],
    askModel: async () => ({ answer: 'Noted.', actions: [] }),
    spawnWorker: () => { throw new Error('A review message must not launch a worker'); }
  });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;
    const post = async (route, data) => {
      const response = await fetch(base + route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
      const body = await response.json();
      assert.equal(response.status, 200, JSON.stringify(body));
      return body;
    };
    await post('/api/orchestrator/lead', { id: 'lead' });
    const question = 'review the module and say what is wrong';
    await post('/api/orchestrator/ask', { question, projectId: 'isolated' });
    const task = JSON.parse(fs.readFileSync(planFile, 'utf8')).tasks.at(-1);
    assert.equal(task.detail, question);
    assert.equal(task.kind, 'implementation');
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
