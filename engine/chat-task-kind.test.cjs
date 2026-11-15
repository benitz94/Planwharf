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
    for (const question of [
      'review the module and say what is wrong',
      'audit the security vulnerabilities',
      'AUDIT this codebase for compliance',
      'please audit the code and dependencies',
      'can you review the latest changes'
    ]) {
      await post('/api/orchestrator/ask', { question, projectId: 'isolated' });
      const task = JSON.parse(fs.readFileSync(planFile, 'utf8')).tasks.at(-1);
      assert.equal(task.detail, question);
      assert.equal(task.kind, 'implementation');
    }
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('the proposal route accepts audit kind and AUDIT subtask type', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-propose-audit-'));
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
    spawnWorker: () => { throw new Error('A proposal must not launch a worker'); }
  });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}`;
    const post = async (route, data, expectedStatus = 200) => {
      const response = await fetch(base + route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
      const body = await response.json();
      assert.equal(response.status, expectedStatus, JSON.stringify(body));
      return body;
    };
    await post('/api/orchestrator/lead', { id: 'lead' });

    // The proposal route still accepts audit for work saved earlier
    const proposal = await post('/api/orchestrator/propose', {
      projectId: 'isolated',
      title: 'Audit work',
      goal: 'Audit this project',
      kind: 'audit',
      subTasks: [{
        title: 'Audit step',
        detail: 'Inspect the code',
        assignedModelId: 'lead',
        type: 'AUDIT'
      }]
    }, 201);
    assert.equal(proposal.ok, true);
    const savedTask = JSON.parse(fs.readFileSync(planFile, 'utf8')).tasks.at(-1);
    assert.equal(savedTask.title, 'Audit work');
    assert.equal(savedTask.kind, 'audit');
    assert.equal(savedTask.proposedSubTasks[0].type, 'AUDIT');

    // Invalid kind or invalid subtask type is rejected
    await post('/api/orchestrator/propose', {
      projectId: 'isolated',
      title: 'Bad work',
      goal: 'Invalid kind',
      kind: 'invalid',
      subTasks: [{ title: 'Step', detail: 'Inspect', assignedModelId: 'lead' }]
    }, 400);
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
