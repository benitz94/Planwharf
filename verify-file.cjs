'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const Plan = require('./plan.js');
const { createServer, readPlan, writePlan } = require('./server.cjs');
const file = path.join(__dirname, `.verify-plan-${crypto.randomUUID()}.json`);
const attemptsDir = path.join(__dirname, `.attempts-${path.basename(file, '.json')}`);
const servers = [];
async function start(options = {}) {
  const server = createServer({ planFile: file, attemptsDir, ...options });
  servers.push(server);
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  return { server, base: `http://127.0.0.1:${server.address().port}` };
}
async function stop(server) { await new Promise(resolve => server.close(resolve)); }
async function post(base, payload, headers = {}) {
  return fetch(base + '/api/revisions', { method: 'POST', headers: { 'Content-Type': 'application/json', ...headers }, body: JSON.stringify(payload) });
}
(async () => {
  const original = Plan.example();
  await writePlan(file, original);
  const first = await start();
  const loaded = await (await fetch(first.base + '/api/plan')).json();
  assert.deepEqual(loaded.plan, original);
  const draft = { title: 'Turni confermati', direction: original.revisions[1].direction, state: 'Quattro volontari confermati.', closed: [...original.revisions[1].closed], open: [] };
  const savedResponse = await post(first.base, { version: loaded.version, draft });
  assert.equal(savedResponse.status, 201);
  const saved = await savedResponse.json();
  assert.equal(saved.plan.revisions[2].mode, 'manual');
  assert.deepEqual(saved.plan.revisions.slice(0, 2), original.revisions);
  assert.deepEqual(saved.plan.tasks, original.tasks);
  assert.equal(saved.plan.tasks[0].revision_id, 2);
  assert.deepEqual(await readPlan(file), saved.plan);
  await stop(first.server);
  const restarted = await start();
  const reopened = await (await fetch(restarted.base + '/api/plan')).json();
  assert.deepEqual(reopened, saved);
  const snapshot = await fs.readFile(file, 'utf8');
  assert.equal((await post(restarted.base, { version: loaded.version, draft })).status, 409);
  assert.equal((await post(restarted.base, { version: saved.version, draft: { ...draft, mode: 'grill' } })).status, 400);
  assert.equal((await post(restarted.base, { version: saved.version, draft: { ...draft, title: '' } })).status, 400);
  assert.equal((await post(restarted.base, { version: saved.version, draft }, { Origin: 'http://other.example' })).status, 403);
  assert.equal((await fetch(restarted.base + '/server.cjs')).status, 404);
  assert.equal((await fetch(restarted.base + '/')).status, 200);
  assert.equal(await fs.readFile(file, 'utf8'), snapshot);
  const failing = await start({ save: async () => { throw new Error('Disk write unavailable'); } });
  const failed = await post(failing.base, { version: saved.version, draft });
  assert.equal(failed.status, 500);
  assert.ok((await failed.json()).error.includes('No revision'));
  assert.equal(await fs.readFile(file, 'utf8'), snapshot);
  const concurrent = await Promise.all([post(restarted.base, { version: saved.version, draft }), post(restarted.base, { version: saved.version, draft })]);
  assert.deepEqual(concurrent.map(result => result.status).sort(), [201, 409]);
  const latest = await readPlan(file);
  assert.equal(latest.revisions.length, 4);
  assert.deepEqual(latest.revisions.slice(0, 3), saved.plan.revisions);
  assert.equal(latest.tasks[0].revision_id, 2);
  await fs.writeFile(file, JSON.stringify({ ...original, tasks: [{ ...original.tasks[0], revision_id: 99 }] }));
  assert.equal((await fetch(restarted.base + '/api/plan')).status, 500);
  assert.equal((await post(restarted.base, { version: saved.version, draft })).status, 500);
  console.log('OK: lettura/scrittura file, riavvio server, append immutabile, conflitto concorrente, input rifiutati e guasto salvataggio.');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  for (const server of servers) if (server.listening) await stop(server);
  await fs.rm(file, { force: true });
  await fs.rm(attemptsDir, { recursive: true, force: true });
});
