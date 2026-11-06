'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { EventEmitter } = require('node:events');
const { execFileSync, spawn } = require('node:child_process');
const Plan = require('../plan.js');
const Quota = require('../quota.js');
const { projectHome, compare } = require('./project-home.cjs');
const { createAppWindows } = require('./app-windows.cjs');
const { createServer } = require('../server.cjs');
test('quota remaining bands and explicit text including shared provider pools', () => {
  for (const [remaining, band] of [[90, 'green'], [60, 'orange'], [40, 'yellow'], [10, 'red'], [0, 'red'], [75, 'green'], [50, 'orange'], [25, 'yellow'], [1, 'red']]) {
    const plan = { readable: true, pct: 100 - remaining, leftPct: remaining };
    assert.equal(Quota.describe(plan).band, band);
    assert.match(Quota.html(plan), /REMAINING/);
    if (!remaining) assert.match(Quota.html(plan), /EXHAUSTED/);
  }
  assert.deepEqual(Quota.describe({ readable: false }), { remaining: null, band: 'grey', text: 'REMAINING UNREAD' });
  assert.equal(Quota.describe({ readable: true, pct: 18 }).remaining, 82);
  const provider = { id: 'ChatGPT', readable: true, leftPct: 82 };
  const models = [{ id: 'astra', plan: 'ChatGPT' }, { id: 'sol', plan: 'ChatGPT' }];
  assert.deepEqual(models.map(m => Quota.describe([provider].find(p => p.id === m.plan)).text), ['82% REMAINING', '82% REMAINING']);
});
test('one application window opens, switches views, focuses and reopens without backend lifecycle coupling', () => {
  const opened = [], focused = []; let clock = 100;
  const windows = createAppWindows({ now: () => clock, focus: view => focused.push(view), open: (url, view) => { const child = new EventEmitter(); child.kill = () => {}; opened.push({ url, view, child }); return child; } });
  windows.openApp('http://127.0.0.1:4173');
  assert.deepEqual(opened.map(v => v.view), ['planwharf']);
  windows.heartbeat({ view: 'planwharf', id: 'p' });
  windows.openView('orchestrator', 'http://127.0.0.1:4173/#orchestrator');
  assert.deepEqual(focused, ['planwharf']); assert.equal(opened.length, 1);
  const switchRequest = windows.heartbeat({ view: 'planwharf', id: 'p', focusSeen: 0 });
  assert.equal(switchRequest.focus, true); assert.equal(switchRequest.requestedView, 'orchestrator');
  assert.equal(windows.heartbeat({ view: 'planwharf', id: 'p', focusSeen: switchRequest.focusVersion }).focus, false);
  windows.heartbeat({ view: 'planwharf', id: 'p', closed: true }); opened[0].child.emit('exit');
  assert.deepEqual(windows.status(), { planwharf: false, orchestrator: false });
  windows.openView('orchestrator', 'http://127.0.0.1:4173/orchestrator.html');
  assert.equal(opened.length, 2); assert.equal(opened[1].url, 'http://127.0.0.1:4173/');
  windows.openView('planwharf', opened[0].url); assert.equal(opened.length, 2, 'Launch in flight must not open a second window');
  windows.heartbeat({ view: 'planwharf', id: 'p2' });
  clock += 16000; assert.deepEqual(windows.status(), { planwharf: false, orchestrator: false });
});

test('desktop launcher configures a single shared window by default', async () => {
  const { listen } = require('../planwharf.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-single-window-'));
  const planFile = path.join(dir, 'notebook.json');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  let server;
  try {
    ({ server } = await listen({ planFile, attemptsDir: path.join(dir, 'attempts'), port: 0, models: [], fetchPlans: async () => [] }));
    const heartbeat = server.windows.heartbeat({ id: 'shared', view: 'orchestrator' });
    assert.deepEqual(heartbeat.views, { planwharf: true, orchestrator: true });
    assert.equal(heartbeat.requestedView, 'planwharf', 'Both views use the same desktop window');
    assert.match(heartbeat.title, /^Planwharf/);
  } finally {
    if (server) { await server.shutdown(); await new Promise(resolve => server.close(resolve)); }
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('project notes and visits persist, are isolated, and do not conflict with revisions', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phase1-'));
  const planFile = path.join(dir, 'notebook.json');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  let server, url;
  async function start() { server = createServer({planFile, models: [], fetchPlans: async () => [] }); await new Promise(r => server.listen(0, '127.0.0.1', r)); url = `http://127.0.0.1:${server.address().port}`; }
  async function stop() { await new Promise(r => server.close(r)); }
  async function post(endpoint, input) { const r = await fetch(url + endpoint, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) }); assert.ok(r.ok, await r.clone().text()); return r.json(); }
  try {
    await start();
    const before = await (await fetch(url + '/api/plan')).json();
    const [a, b] = before.plan.projects;
    const id = a.project.id;
    const first = await post('/api/project-home', { projectId: id, visit: true });
    assert.equal(first.goal, a.revisions.at(-1).direction); assert.equal(first.since.firstVisit, true); assert.deepEqual(first.since.changes, []);
    const added = await post('/api/notes', { projectId: id, action: 'add', text: 'Personal reminder <script>' });
    const note = added.notes[0]; assert.ok(note.id); assert.ok(Date.parse(note.createdAt));
    await post('/api/notes', { projectId: id, action: 'pin', id: note.id, pinned: true });
    assert.equal((await (await fetch(url + '/api/plan')).json()).version, before.version);
    await stop(); await start();
    const restored = await post('/api/project-home', { projectId: id });
    assert.equal(restored.notes[0].pinned, true); assert.equal(restored.notes[0].text, note.text); assert.ok(restored.since.details.includes(note.text)); assert.ok(restored.timeline.some(event => event.type === 'USER_NOTE')); assert.equal(restored.timeline.filter(event => event.type === 'MEGATASK_PROPOSED').length, 0, 'Notes never become work');
    const other = await post('/api/project-home', { projectId: b.project.id }); assert.equal(other.since.firstVisit, true); assert.deepEqual(other.notes, []);
    const latest = a.revisions.at(-1);
    const draft = { title: 'Validation change', direction: latest.direction, state: latest.state, closed: latest.closed, open: latest.open };
    await post('/api/revisions', { projectId: id, version: before.version, draft });
    const changed = await post('/api/project-home', { projectId: id }); assert.ok(changed.since.changes.includes('1 new Living Plan revision')); assert.ok(changed.timeline.some(event => event.type === 'PLAN_REVISION' && event.reference.planRevisionId === 3));
    await post('/api/notes', { projectId: id, action: 'delete', id: note.id });
    assert.deepEqual((await post('/api/project-home', { projectId: id })).notes, []);
    assert.equal((await post('/api/project-home', { projectId: b.project.id })).since.firstVisit, true);
  } finally { if (server?.listening) await stop(); fs.rmSync(dir, { recursive: true, force: true }); }
});
test('local Git comparison counts actual commits and same-file dirty edits', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phase1-git-'));
  const git = args => execFileSync('git', ['-C', dir, ...args], { stdio: 'pipe' });
  try {
    git(['init']); git(['config', 'user.name', 'Isolated validation']); git(['config', 'user.email', 'validation@example.invalid']);
    fs.writeFileSync(path.join(dir, 'file.txt'), 'one'); git(['add', '.']); git(['commit', '-m', 'First']);
    const project = Plan.example(); project.project.folder = dir;
    const snap = { projects: [{ id: project.project.id, status: 'idle' }] };
    const first = await projectHome(project, snap);
    const notebookFile = path.join(dir, 'notebook.json');
    fs.writeFileSync(notebookFile, JSON.stringify(project));
    const withNotebook = await projectHome(project, snap, undefined, { planFile: notebookFile });
    fs.writeFileSync(notebookFile, JSON.stringify({ ...project, home: { notes: [], lastVisit: withNotebook.current } }));
    assert.deepEqual((await projectHome(project, snap, withNotebook.current, { planFile: notebookFile })).since.changes, [], 'saving a visit does not fabricate a source change');
    fs.unlinkSync(notebookFile);
    fs.writeFileSync(path.join(dir, 'file.txt'), 'two'); git(['add', '.']); git(['commit', '-m', 'Second']);
    fs.writeFileSync(path.join(dir, 'file.txt'), 'dirty');
    const second = await projectHome(project, snap, first.current);
    assert.deepEqual(second.since.changes, ['1 commit added', '1 file changed locally']);
    fs.writeFileSync(path.join(dir, 'file.txt'), 'edited again');
    const third = await projectHome(project, snap, second.current); assert.deepEqual(third.since.changes, ['1 file changed locally']);
    const unknown = await compare({ ...first.current, git: { readable: false } }, third.current); assert.ok(unknown.unavailable.length); assert.equal(unknown.changes.some(c => c.includes('commit')), false);
    const assigned = await projectHome(project, { projects: [{ id: project.project.id, status: 'idle', resultPath: path.join(dir, 'missing-result.json') }] }, third.current);
    assert.equal(assigned.since.changes.some(text => text.includes('execution result')), false, 'an output path alone does not prove a result exists');
    const resultFile = path.join(dir, 'result.json'); fs.writeFileSync(resultFile, '{"status":"COMPLETE"}');
    const completed = await projectHome(project, { projects: [{ id: project.project.id, status: 'complete', checkpoint: { at: 'test' }, resultPath: resultFile }] }, third.current);
    assert.ok(completed.since.changes.includes('Project work completed'));
    assert.ok(completed.since.changes.includes('New checkpoint saved'));
    assert.ok(completed.since.changes.includes('New execution result available'));
    assert.equal((await projectHome(project, { projects: [{ id: project.project.id, status: 'complete', checkpoint: { at: 'test' }, resultPath: resultFile }] }, completed.current)).since.changes.length, 0);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('relaunch reuses the same backend process and notebook', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phase1-launch-'));
  const planFile = path.join(dir, 'notebook.json'), attemptsDir = path.join(dir, 'attempts');
  fs.copyFileSync(path.join(__dirname, '../fixtures/notebook.json'), planFile);
  const args = [path.join(__dirname, '../planwharf.cjs'), '--no-window', '--plan', planFile, '--attempts-dir', attemptsDir, '--port', '0'];
  const child = spawn(process.execPath, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let url;
  try {
    url = await new Promise((resolve, reject) => { let text = ''; const timer = setTimeout(() => reject(new Error('Launcher timeout')), 10000); child.stdout.on('data', c => { text += c; const match = text.match(/http:\/\/127\.0\.0\.1:\d+/); if (match) { clearTimeout(timer); resolve(match[0]); } }); child.on('error', reject); child.on('exit', () => reject(new Error('Launcher exited'))); });
    const first = await (await fetch(url + '/api/app')).json();
    const output = await new Promise((resolve, reject) => { const second = spawn(process.execPath, args, { windowsHide: true }); let out = ''; second.stdout.on('data', c => out += c); second.on('error', reject); second.on('exit', code => code === 0 ? resolve(out) : reject(new Error('Second launcher failed'))); });
    assert.match(output, /already running/); const app = await (await fetch(url + '/api/app')).json(); assert.equal(app.pid, first.pid); assert.equal(app.pid, child.pid);
    await fetch(url + '/api/app/quit', { method: 'POST' });
    await new Promise(resolve => child.exitCode !== null ? resolve() : child.once('exit', resolve));
    assert.equal(fs.existsSync(path.join(attemptsDir, 'app-instance.json')), false);
  } finally { if (child.exitCode === null) child.kill(); fs.rmSync(dir, { recursive: true, force: true }); }
});
