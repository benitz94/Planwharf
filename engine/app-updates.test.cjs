'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const zlib = require('node:zlib');
const { createUpdateManager, digest, newer, unpack, verifyLocal, installation } = require('./app-updates.cjs');
const { apply } = require('./apply-update.cjs');
const { createServer } = require('../server.cjs');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-updates-'));
  const sources = { 'planwharf.cjs': 'old app', 'server.cjs': 'old server', 'chat.html': 'old page',
    'engine/app-updates.cjs': fs.readFileSync(path.join(__dirname, 'app-updates.cjs'), 'utf8'),
    'engine/apply-update.cjs': fs.readFileSync(path.join(__dirname, 'apply-update.cjs'), 'utf8'), 'fixtures/starter-notebook.json': '{}' };
  const manifest = { schema: 1, version: 'v1.0.0', files: {} };
  for (const [name, data] of Object.entries(sources)) { const file = path.join(root, name); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, data); manifest.files[name] = digest(Buffer.from(data)); }
  fs.writeFileSync(path.join(root, '.planwharf-install.json'), JSON.stringify(manifest));
  const bundle = { schema: 1, version: 'v1.0.1', files: Object.entries({ ...sources, 'planwharf.cjs': 'new app' }).map(([name, data]) => ({ path: name, sha256: digest(Buffer.from(data)), data: Buffer.from(data).toString('base64') })) };
  const bytes = zlib.gzipSync(JSON.stringify(bundle));
  const release = { tag_name: bundle.version, html_url: 'https://github.com/benitz94/Planwharf/releases/tag/' + bundle.version,
    assets: [{ name: 'planwharf-update.json.gz', digest: 'sha256:' + digest(bytes), browser_download_url: 'https://github.com/benitz94/Planwharf/releases/download/' + bundle.version + '/planwharf-update.json.gz' }] };
  let downloads = 0;
  const fetchImpl = async url => { if (url.endsWith('/latest')) return new Response(JSON.stringify(release)); downloads++; return new Response(bytes); };
  return { root, manifest, bundle, bytes, release, fetchImpl, get downloads() { return downloads; }, cleanup() { assert.ok(root.startsWith(os.tmpdir() + path.sep)); fs.rmSync(root, { recursive: true, force: true }); } };
}
test('stable version comparison and packages reject protected files, traversal and corrupt checksums', () => {
  assert.equal(newer('v1.2.0', 'v1.1.9'), true); assert.equal(newer('v1.2.0', 'v1.2.0'), false); assert.equal(newer('v1.2.0-beta', 'v1.1.0'), false);
  const f = fixture();
  try {
    for (const name of ['plan.json', '.git/config', 'engine/../plan.json', 'C:/secret', '.attempts-plan/logs.txt', 'plan.local.json', 'models.json.settings.json']) {
      const broken = structuredClone(f.bundle); broken.files.push({ path: name, sha256: digest(Buffer.from('data')), data: Buffer.from('data').toString('base64') });
      assert.throws(() => unpack(zlib.gzipSync(JSON.stringify(broken)), 'v1.0.1'), /Invalid update file/);
    }
    f.bundle.files[0].data = Buffer.from('bad bytes').toString('base64');
    assert.throws(() => unpack(zlib.gzipSync(JSON.stringify(f.bundle)), 'v1.0.1'), /checksum/);
  } finally { f.cleanup(); }
});
test('checking never downloads; confirmation, verified stage, checkpoints and restart occur in that order', async () => {
  const f = fixture(), events = [];
  try {
    const manager = createUpdateManager({ root: f.root, fetchImpl: f.fetchImpl, activeCount: async () => 2,
      checkpoint: async () => events.push('checkpoint'), restart: async info => { events.push('restart'); assert.ok(fs.existsSync(path.join(info.stage, 'package.json.gz'))); } });
    const checked = await manager.check(); assert.equal(checked.canInstall, true); assert.equal(f.downloads, 0);
    assert.deepEqual(await manager.install(false), { requiresConfirmation: true }); assert.equal(f.downloads, 0); assert.deepEqual(events, []);
    assert.equal((await manager.install(true)).ok, true); assert.equal(f.downloads, 1); assert.deepEqual(events, ['checkpoint', 'restart']);
    assert.equal(fs.readFileSync(path.join(f.root, 'planwharf.cjs'), 'utf8'), 'old app', 'The running process never replaces its own files');
  } finally { f.cleanup(); }
});
test('checkpoint failure, local edits, Git checkouts and missing verified assets prevent replacement', async () => {
  const f = fixture(); let restart = 0;
  try {
    const manager = createUpdateManager({ root: f.root, fetchImpl: f.fetchImpl, checkpoint: async () => { throw new Error('Checkpoint disk failure'); }, restart: async () => restart++ });
    await manager.check(); await assert.rejects(manager.install(true), /Checkpoint disk failure/); assert.equal(restart, 0);
    fs.writeFileSync(path.join(f.root, 'server.cjs'), 'user changed code'); await manager.check();
    await assert.rejects(manager.install(true), /local changes/); assert.equal(restart, 0);
    fs.mkdirSync(path.join(f.root, '.git')); const git = await manager.check(); assert.equal(git.canInstall, false); assert.match(git.reason, /Git checkout/);
    fs.rmdirSync(path.join(f.root, '.git')); delete f.release.assets[0].digest;
    assert.equal((await manager.check()).canInstall, false);
    assert.equal(fs.readFileSync(path.join(f.root, 'server.cjs'), 'utf8'), 'user changed code');
  } finally { f.cleanup(); }
});
test('applying an update preserves notebooks, settings, credentials and evidence; failures restore old files', () => {
  const f = fixture();
  try {
    const personal = { 'plan.json': 'personal notebook', 'plan.json.settings.json': 'personal settings', '.attempts-plan/orchestrator.json': 'checkpoint evidence', '.attempts-plan/logs/history.log': 'raw history', '.credentials/key': 'isolated fixture credential' };
    for (const [name, value] of Object.entries(personal)) { const file = path.join(f.root, name); fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, value); }
    const stage = path.join(f.root, '.planwharf-updates/test'); fs.mkdirSync(stage, { recursive: true }); fs.writeFileSync(path.join(stage, 'package.json.gz'), f.bytes);
    const job = { root: f.root, stage, installed: f.manifest, version: 'v1.0.1' };
    fs.writeFileSync(path.join(f.root, 'chat.html.update-tmp'), 'existing evidence');
    assert.throws(() => apply(job), /temporary file/);
    assert.equal(fs.readFileSync(path.join(f.root, 'planwharf.cjs'), 'utf8'), 'old app');
    fs.unlinkSync(path.join(f.root, 'chat.html.update-tmp'));
    apply(job); assert.equal(installation(f.root).version, 'v1.0.1'); assert.equal(fs.readFileSync(path.join(f.root, 'planwharf.cjs'), 'utf8'), 'new app');
    for (const [name, value] of Object.entries(personal)) assert.equal(fs.readFileSync(path.join(f.root, name), 'utf8'), value);
    verifyLocal(f.root, installation(f.root), Object.keys(f.manifest.files));
  } finally { f.cleanup(); }
});
test('two active projects checkpoint durably; an unkillable worker blocks the update', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-update-checkpoints-'));
  const plan = structuredClone(require('../fixtures/starter-notebook.json')); plan.projects = ['one', 'two'].map(id => ({ ...structuredClone(plan.projects[0]), project: { id, name: id, folder: dir }, tasks: [{ id: 'job-' + id, projectId: id, revision_id: 1, title: 'Isolated task', detail: 'Fixture', status: 'running', executionVersion: 2 }] }));
  for (const project of plan.projects) project.revisions.at(-1).open = [];
  const attempts = createAttemptEngine({ dir: path.join(dir, 'attempts') }), live = new Set(); let killAllowed = true;
  const orchestra = createOrchestrator({ readPlan: async () => plan, attempts, dir, phase2: true,
    models: [{ id: 'fixture', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, cost: 1, limit: 100 }],
    inspectFolder: () => ({ git: false, dirty: [], names: [] }), spawnWorker: () => { const pid = 1000 + live.size; live.add(pid); return { pid }; },
    isProcessAlive: pid => live.has(pid), killWorker: pid => killAllowed ? live.delete(pid) : false });
  try {
    let snap = await orchestra.tick();
    for (const project of snap.projects) await orchestra.approve(project.id, project.megaTaskId);
    assert.equal(live.size, 2); killAllowed = false;
    await assert.rejects(orchestra.checkpointForUpdate(), /Checkpoint failed/); assert.equal(live.size, 2);
    killAllowed = true; await orchestra.checkpointForUpdate(); assert.equal(live.size, 0);
    for (const project of plan.projects) {
      const durable = orchestra.executionView(project.project.id); assert.ok(durable.checkpoints.length); assert.equal(durable.megaTasks[0].status, 'PAUSED');
    }
    const stored = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'))); assert.ok(stored.execution.checkpoints.length >= 2);
  } finally { killAllowed = true; await orchestra.shutdown(); assert.ok(dir.startsWith(os.tmpdir() + path.sep)); fs.rmSync(dir, { recursive: true, force: true }); }
});
test('update API requires consent and does not checkpoint on checks or declined confirmation', async () => {
  const f = fixture(), file = path.join(f.root, 'notebook.json'); let checkpoints = 0;
  const plan = require('../plan.js').example(); plan.tasks = []; plan.project = { id: 'isolated', name: 'Isolated' }; fs.writeFileSync(file, JSON.stringify(plan));
  const manager = createUpdateManager({ root: f.root, fetchImpl: f.fetchImpl, activeCount: async () => 1, checkpoint: async () => { checkpoints++; }, restart: async () => {} });
  const server = createServer({ planFile: file, attemptsDir: path.join(f.root, 'attempts'), models: [], fetchPlans: null,refreshModels: false, updateManager: manager });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const post = (route, data) => fetch('http://127.0.0.1:' + server.address().port + route, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) });
  try {
    assert.equal((await post('/api/app/updates/check', {})).status, 200); assert.equal(checkpoints, 0);
    const response = await post('/api/app/updates/install', {}); assert.equal(response.status, 409); assert.equal((await response.json()).requiresConfirmation, true); assert.equal(checkpoints, 0);
    assert.equal((await post('/api/app/updates/install', { confirmRunning: 'yes' })).status, 400);
    assert.equal((await post('/api/app/updates/install', { confirmRunning: true })).status, 200); assert.equal(checkpoints, 1);
  } finally { await new Promise(resolve => server.close(resolve)); f.cleanup(); }
});

test('detached helper applies verified files and restarts with the same notebook arguments', async () => {
  const f = fixture();
  try {
    const newScript = "require('node:fs').writeFileSync('restarted.json', JSON.stringify(process.argv.slice(2)));";
    f.bundle.files[0].data = Buffer.from(newScript).toString('base64'); f.bundle.files[0].sha256 = digest(Buffer.from(newScript));
    const stage = path.join(f.root, '.planwharf-updates/helper-test'); fs.mkdirSync(stage, { recursive: true });
    fs.writeFileSync(path.join(stage, 'package.json.gz'), zlib.gzipSync(JSON.stringify(f.bundle)));
    fs.copyFileSync(path.join(__dirname, 'apply-update.cjs'), path.join(stage, 'apply-update.cjs'));
    fs.copyFileSync(path.join(__dirname, 'app-updates.cjs'), path.join(stage, 'app-updates.cjs'));
    const args = [path.join(f.root, 'planwharf.cjs'), '--plan', path.join(f.root, 'personal.json'), '--attempts-dir', path.join(f.root, 'attempts'), '--no-window'];
    const job = { root: f.root, stage, installed: f.manifest, version: 'v1.0.1', parentPid: 2147483647, node: process.execPath, args };
    const jobFile = path.join(stage, 'job.json'); fs.writeFileSync(jobFile, JSON.stringify(job));
    await promisify(execFile)(process.execPath, [path.join(stage, 'apply-update.cjs'), jobFile], { timeout: 10000 });
    for (let i = 0; i < 100 && !fs.existsSync(path.join(f.root, 'restarted.json')); i++) await new Promise(resolve => setTimeout(resolve, 20));
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(f.root, 'restarted.json'), 'utf8')), args.slice(1));
    assert.equal(JSON.parse(fs.readFileSync(path.join(stage, 'result.json'))).ok, true);
    assert.equal(installation(f.root).version, 'v1.0.1');
  } finally { f.cleanup(); }
});

test('desktop boot checks updates once without installing or launching any worker', async () => {
  const f = fixture(); const file = path.join(f.root, 'boot-notebook.json');
  const plan = require('../plan.js').example(); plan.tasks = []; plan.project = { id: 'isolated-boot', name: 'Isolated boot' }; fs.writeFileSync(file, JSON.stringify(plan));
  let checks = 0, installs = 0, app;
  const updater = { check: async () => { checks++; return {}; }, snapshot: () => ({}), install: async () => { installs++; }, busy: false };
  try {
    app = await require('../planwharf.cjs').runApp({ planFile: file, attemptsDir: path.join(f.root, 'boot-attempts'), port: 0, noWindow: true, models: [], fetchPlans: null,updateManager: updater,
      windows: { closeAll() {}, status() { return {}; } } });
    assert.equal(checks, 1); assert.equal(installs, 0);
    assert.deepEqual(JSON.parse(fs.readFileSync(file)), plan);
  } finally { if (app?.server.listening) await new Promise(resolve => app.server.close(resolve)); f.cleanup(); }
});
