'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');

// A task saved as running before Phase 2 (no executionVersion) is closed at start, so the old
// execution path is no longer reachable from the app.
test('an old running task from before Phase 2 is closed at start with a clear reason', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-legacy-close-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const item = structuredClone(require('../fixtures/starter-notebook.json').projects[0]);
  item.project = { ...item.project, id: 'old', name: 'Old project', folder: dir };
  item.tasks = [{ id: 'task-old', projectId: 'old', revision_id: item.revisions.at(-1).id, title: 'Old work', detail: 'Saved before Phase 2', status: 'running' }];
  const planFile = path.join(dir, 'plan.json');
  fs.writeFileSync(planFile, JSON.stringify({ schema: 2, projects: [item] }));
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), refreshModels: false, fetchPlans: null, monitorIntervalMs: 60000, models: [] });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => server.close(resolve)));
  const url = `http://127.0.0.1:${server.address().port}`;
  await (await fetch(url + '/api/orchestrator')).json();
  const saved = JSON.parse(fs.readFileSync(planFile, 'utf8')).projects[0].tasks[0];
  assert.equal(saved.status, 'superseded');
  assert.match(saved.supersededReason, /Let the crew do it/);
  const approve = await fetch(url + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'old' }) });
  assert.notEqual(approve.status, 200);
});
