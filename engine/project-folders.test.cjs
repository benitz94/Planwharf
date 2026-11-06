'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const os = require('node:os');
const { createServer, writePlan } = require('../server.cjs');
let server;
(async () => {
  const dir = await fs.mkdtemp(path.join(os.tmpdir(), 'pw-project-folders-'));
  const planFile = path.join(dir, 'notebook.json');
  const projectsRoot = path.join(dir, 'Projects');
  await fs.mkdir(projectsRoot);
  await writePlan(planFile, JSON.parse(await fs.readFile(path.join(__dirname, '../fixtures/notebook.json'), 'utf8')));
  async function start(save) {
    server = createServer({planFile, models: [], fetchPlans: async () => [], ...(save ? { save } : {}) });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    return `http://127.0.0.1:${server.address().port}`;
  }
  let base = await start();
  async function post(endpoint, value) {
    const response = await fetch(base + endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    return { status: response.status, body: await response.json() };
  }
  assert.equal((await post('/api/settings', { projectsRoot: 'relative' })).status, 400);
  assert.equal((await post('/api/settings', { projectsRoot: path.join(dir, 'missing') })).status, 400);
  assert.equal((await post('/api/settings', { projectsRoot })).status, 200);
  await new Promise(resolve => server.close(resolve));
  base = await start();
  assert.equal((await (await fetch(base + '/api/settings')).json()).projectsRoot, projectsRoot);
  let notebook = await (await fetch(base + '/api/plan')).json();
  const original = structuredClone(notebook.plan.projects);
  for (const expected of ['Calculator', 'Calculator-2', 'Project-CON', '..-escape']) {
    const name = expected.startsWith('Calculator') ? 'Calculator' : expected === 'Project-CON' ? 'CON' : '../escape';
    const result = await post('/api/projects', { version: notebook.version, createFolder: true, project: { name, direction: 'Test folders' } });
    assert.equal(result.status, 201, JSON.stringify(result.body));
    notebook = result.body;
    const folder = notebook.plan.projects.at(-1).project.folder;
    assert.equal(folder, path.join(projectsRoot, expected));
    assert.ok((await fs.stat(folder)).isDirectory());
  }
  assert.deepEqual(notebook.plan.projects.slice(0, original.length), original);
  const before = await fs.readdir(projectsRoot);
  assert.equal((await post('/api/projects', { version: 'stale', createFolder: true, project: { name: 'Stale', direction: 'Test' } })).status, 409);
  assert.deepEqual(await fs.readdir(projectsRoot), before);
  await new Promise(resolve => server.close(resolve));
  base = await start(async () => { throw new Error('Disk full'); });
  assert.equal((await post('/api/projects', { version: notebook.version, createFolder: true, project: { name: 'Rollback', direction: 'Test' } })).status, 500);
  assert.deepEqual(await fs.readdir(projectsRoot), before);
  await new Promise(resolve => server.close(resolve));
  server = null;
  // Test artifacts are deliberately confined to this unique temp directory.
  await fs.rm(dir, { recursive: true, force: true });
  console.log('OK: persistent projects root, unique subfolders, safe names, preserved projects, stale request and failed-save rollback.');
})().catch(async error => { console.error(error); if (server) await new Promise(resolve => server.close(resolve)); process.exitCode = 1; });
