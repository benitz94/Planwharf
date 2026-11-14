'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnWorker, isProcessAlive, killWorker } = require('./runner.cjs');

test('implementation launch rejects missing real linked folder before creating launch artifacts', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const resultPath = path.join(root, 'results', 'result.txt');
    assert.throws(() => spawnWorker({ folder: root, resultPath, job: { kind: 'implementation' } }), /linked.*folder/i);
    assert.equal(fs.existsSync(path.join(root, 'logs')), false);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');
test('missing project directory never reaches injected inspection or launch', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'alpha', name: 'Alpha', folder: path.join(dir, 'missing') }, revisions: [{ id: 1, direction: 'Implement parser', open: [] }], tasks: [{ id: 'job', revision_id: 1, title: 'Implement parser', detail: 'Small slice', status: 'running', executionVersion: 2 }] }] };
    const inspected = [], launched = [];
    const orchestra = createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }), models: [{ id: 'fixture', provider: 'codex', cli: 'fixture', enabled: true, cost: 1, limit: 100 }], fetchPlans: async () => [], inspectFolder: folder => { inspected.push(folder); return { folder, git: false, names: [] }; }, spawnWorker: info => { launched.push(info); return { pid: 999999 }; } });
    await orchestra.approve('alpha'); await orchestra.tick();
    assert.deepEqual(inspected, []);
    assert.deepEqual(launched, []);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
const { createRepositoryInspector } = require('./repository.cjs');
test('repository inspection never inherits a parent repository outside the linked folder', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const folder = path.join(dir, 'alpha'); fs.mkdirSync(folder);
    require('node:child_process').execFileSync('git', ['init', '--quiet', dir]);
    const commands = [];
    const inspect = createRepositoryInspector({ execute: async (...args) => { commands.push(args); return { ok: true, stdout: dir }; } });
    const result = await inspect({ folder });
    assert.equal(result.local.git, false);
    assert.deepEqual(commands, [], 'No Git probe may discover a parent project');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('worker launch rejects sibling, traversal and symlink destinations before artifacts', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const alpha = path.join(root, 'alpha'), beta = path.join(root, 'alpha-other'); fs.mkdirSync(alpha); fs.mkdirSync(beta);
    const link = path.join(alpha, 'outside'); fs.symlinkSync(beta, link, process.platform === 'win32' ? 'junction' : 'dir');
    for (const folder of [beta, path.join(alpha, '..', 'alpha-other'), link]) {
      assert.throws(() => spawnWorker({ linkedFolder: alpha, folder, resultPath: path.join(root, 'results', 'x'), job: { kind: 'audit' } }), /inside.*linked/i);
    }
    assert.equal(fs.existsSync(path.join(root, 'logs')), false);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
const { repoState } = require('./execution.cjs');
test('execution comparison does not read dirty files through a foreign directory symlink', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta'); fs.mkdirSync(alpha); fs.mkdirSync(beta);
    fs.writeFileSync(path.join(beta, 'private.txt'), 'Foreign evidence');
    fs.symlinkSync(beta, path.join(alpha, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
    const state = repoState(alpha, { git: true, dirty: [' M linked/private.txt'] });
    assert.equal(state.comparable, false);
    assert.equal(state.fingerprint, null);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
test('explicit launch cwd and job folder cannot select a different directory', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta'); fs.mkdirSync(alpha); fs.mkdirSync(beta);
    for (const additional of [{ cwd: beta }, { job: { folder: beta, kind: 'audit' } }, { folder: beta }]) {
      assert.throws(() => spawnWorker({ linkedFolder: alpha, folder: alpha, resultPath: path.join(root, 'results', 'x'), job: { kind: 'audit' }, ...additional }), /linked|cwd|folder/i);
    }
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
test('runner rejects a provider specification that changes the launch cwd', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta'); fs.mkdirSync(alpha); fs.mkdirSync(beta);
    const marker = path.join(beta, 'spawned'), result = path.join(root, 'result.txt');
    const script = path.join(root, 'run.cjs');
    fs.writeFileSync(script, `const { adapters } = require(${JSON.stringify(require.resolve('./provider-adapters.cjs'))});\nadapters.codex.execute = () => ({ command: process.execPath, args: ['-e', ${JSON.stringify(`require('node:fs').writeFileSync(${JSON.stringify(marker)}, process.cwd())`)}], cwd: ${JSON.stringify(beta)}, provider: 'codex', model: 'gpt-fixture' });\nrequire(${JSON.stringify(require.resolve('./runner.cjs'))}).run(['node', 'runner', '--job-file', process.argv[2]]);`);
    const input = path.join(root, 'input.json');
    fs.writeFileSync(input, JSON.stringify({ linkedFolder: alpha, folder: alpha, result, kind: 'audit', model: { provider: 'codex', cli: 'gpt-fixture' } }));
    const child = require('node:child_process').spawnSync(process.execPath, [script, input], { encoding: 'utf8', timeout: 5000 });
    assert.equal(child.status, 1);
    assert.equal(fs.existsSync(marker), false, 'No provider process may enter project beta');
    assert.match(fs.readFileSync(result, 'utf8'), /FAILED/);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('canonical linked directory survives the runner job file and research keeps its isolated directory', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  const previous = process.env.PLANWHARF_CODEX;
  const pids = [];
  try {
    process.env.PLANWHARF_CODEX = process.execPath; // Owned Node fixture cannot invoke a configured provider.
    const alpha = path.join(root, 'alpha'); fs.mkdirSync(alpha);
    for (const kind of ['audit', 'research']) {
      const resultPath = path.join(root, kind + '.txt');
      const receipt = spawnWorker({ linkedFolder: kind === 'research' ? undefined : alpha, folder: alpha, resultPath, worker: { provider: 'codex', cli: 'gpt-fixture' }, job: { kind } });
      pids.push(receipt.pid);
      const input = JSON.parse(fs.readFileSync(receipt.inputPath, 'utf8'));
      assert.equal(input.folder, fs.realpathSync(alpha));
      assert.equal(input.linkedFolder, kind === 'research' ? '' : fs.realpathSync(alpha));
      for (let i = 0; i < 400 && !fs.existsSync(resultPath); i++) await new Promise(resolve => setTimeout(resolve, 25));
      assert.match(fs.readFileSync(resultPath, 'utf8'), /FAILED/);
      fs.unlinkSync(receipt.inputPath);
    }
  } finally {
    if (previous === undefined) delete process.env.PLANWHARF_CODEX; else process.env.PLANWHARF_CODEX = previous;
    // The result can be written before the detached runner releases its Windows cwd.
    for (const pid of pids) {
      for (let i = 0; i < 100 && isProcessAlive(pid); i++) await new Promise(resolve => setTimeout(resolve, 20));
      if (isProcessAlive(pid)) killWorker(pid);
    }
    fs.rmSync(root, { recursive: true, force: true });
  }
});
for (const redirected of ['gitdir', 'HEAD']) test(`repository inspection rejects redirected ${redirected} metadata before commands`, async t => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-containment-'));
  try {
    const alpha = path.join(root, 'alpha'), beta = path.join(root, 'beta'); fs.mkdirSync(alpha); fs.mkdirSync(beta);
    require('node:child_process').execFileSync('git', ['init', '--quiet', beta]);
    if (redirected === 'gitdir') {
      fs.writeFileSync(path.join(alpha, '.git'), 'gitdir: ' + path.join(beta, '.git') + '\n');
    } else {
      require('node:child_process').execFileSync('git', ['init', '--quiet', alpha]);
      fs.unlinkSync(path.join(alpha, '.git', 'HEAD'));
      try {
        fs.symlinkSync(path.join(beta, '.git', 'HEAD'), path.join(alpha, '.git', 'HEAD'));
      } catch (error) {
        if (error.code !== 'EPERM') throw error;
        t.skip('File symlinks unavailable');
        return;
      }
    }
    const commands = [];
    const inspect = createRepositoryInspector({ execute: async (...args) => { commands.push(args); return { ok: true, stdout: alpha }; } });
    assert.equal((await inspect({ folder: alpha })).local.git, false);
    assert.deepEqual(commands, []);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
