'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { once } = require('node:events');
const Plan = require('../plan.js');
const { createServer } = require('../server.cjs');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-request-'));
const planFile = path.join(dir, 'notebook.json');
const plan = Plan.example();
plan.tasks = [];
plan.project = { id: 'isolated-chat', name: 'Isolated chat' };
fs.writeFileSync(planFile, JSON.stringify(plan));
const calls = [];
const server = createServer({
  planFile,
  attemptsDir: path.join(dir, 'attempts'),
  models: [{ id: 'astra-fixture', label: 'GPT-6 Astra', provider: 'codex', cli: 'gpt-6-astra', enabled: true, available: true, kind: 'premium', limit: 200 }],
  askModel: async info => { calls.push(info); return { answer: 'Isolated answer.', ms: 1 }; },
  fetchPlans: async () => [],
  inspectFolder: () => ({ fileCount: 0, names: [], git: false }),
  spawnWorker: () => { throw new Error('Chat validation must never dispatch a worker'); }
});

async function main() {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  const base = `http://127.0.0.1:${server.address().port}`;
  const post = (route, data) => fetch(base + route, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data)
  });
  const lead = await post('/api/orchestrator/lead', { id: 'astra-fixture' });
  assert.equal(lead.status, 200, await lead.text());
  const question = 'Unicode discussion: \u00e8 \u65e5\u672c\u8a9e \u{1f3ce}\u{fe0f}.\n'.repeat(4000);
  assert.ok(Buffer.byteLength(JSON.stringify({ question })) > 65536);
  const asked = await post('/api/orchestrator/ask', { question });
  assert.equal(asked.status, 200, await asked.clone().text());
  assert.equal((await asked.json()).answer, 'Isolated answer.');
  assert.equal(calls.length, 1);
  assert.equal(calls[0].question, question.trim(), 'Full Unicode question reaches the selected lead');
  const excessive = await post('/api/orchestrator/ask', { question: 'x'.repeat(1024 * 1024) });
  assert.equal(excessive.status, 413);
  assert.match((await excessive.json()).error, /Chat request too large/);
  assert.equal(calls.length, 1, 'Oversized chat is rejected before invoking a lead');
  const other = await post('/api/settings', { projectsRoot: 'x'.repeat(65536) });
  assert.equal(other.status, 413, 'Other routes retain their 64 KiB request limit');
  assert.match((await other.json()).error, /Revision too large/);
  assert.deepEqual(JSON.parse(fs.readFileSync(planFile, 'utf8')), plan, 'Chat must not change notebook content');
  assert.doesNotMatch(fs.readFileSync(path.join(__dirname, '../chat.html'), 'utf8'), /maxlength="2000"/, 'The chat box accepts long messages');
  console.log('OK: isolated HTTP chat accepts >64 KiB Unicode, enforces 1 MiB, preserves other request limits and notebook data.');
}

main().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  await new Promise(resolve => server.close(resolve));
  fs.rmSync(dir, { recursive: true, force: true });
});
