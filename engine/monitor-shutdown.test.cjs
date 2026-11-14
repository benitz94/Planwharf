'use strict';
// The execution monitor must report a failed pass, recover on the next one, and stay quiet once shutdown has started.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createServer } = require('../server.cjs');

const provider = { id: 'fixture', adapter: 'fixture', quotaPoolId: 'fixture', models: [{ id: 'fixture-model', cli: 'fixture', enabled: true }] };

function listen(dir, extra = {}) {
  const plan = JSON.parse(fs.readFileSync(path.join(__dirname, '../fixtures/notebook.json'), 'utf8'));
  plan.projects[0].project.folder = dir;
  plan.projects[0].project.github = 'https://github.com/example/repo';
  const planFile = path.join(dir, 'plan.json');
  const good = JSON.stringify(plan);
  fs.writeFileSync(planFile, good);
  const server = createServer({
    planFile,
    attemptsDir: path.join(dir, 'attempts'),
    models: [],
    providers: [provider],
    fetchPlans: async () => [],
    monitorIntervalMs: 40,
    inspectFolder: () => ({ names: [], fileCount: 0 }),
    spawnWorker: () => ({ pid: 1 }),
    isProcessAlive: () => false,
    killWorker: () => {},
    ...extra
  });
  return { server, planFile, good };
}

test('a failed monitor pass is reported once and the next pass runs', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-monitor-recover-'));
  let refreshes = 0;
  const { server, planFile, good } = listen(dir, {
    readRepository: async () => { refreshes += 1; return { folder: dir, github: 'https://github.com/example/repo' }; }
  });
  const lines = [];
  const original = console.error;
  console.error = (...args) => { lines.push(args.map(String).join(' ')); };
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const until = async (check, label) => {
      const start = Date.now();
      while (!check()) {
        if (Date.now() - start > 3000) throw new Error(label);
        await new Promise(resolve => setTimeout(resolve, 20));
      }
    };
    await until(() => refreshes >= 1, 'the monitor never read the repository');
    fs.writeFileSync(planFile, '{');
    await until(() => lines.some(line => line.includes('Execution monitoring failed:')), 'a broken notebook was not reported');
    const reported = lines.filter(line => line.includes('Execution monitoring failed:')).length;
    fs.writeFileSync(planFile, good);
    const seen = refreshes;
    await until(() => refreshes > seen, 'the monitor did not recover');
    assert.equal(lines.filter(line => line.includes('Execution monitoring failed:')).length, reported);
    await server.shutdown();
    fs.writeFileSync(planFile, '{');
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(lines.filter(line => line.includes('Execution monitoring failed:')).length, reported, 'shutdown stops the monitor');
  } finally {
    console.error = original;
    await server.shutdown();
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('shutdown during a monitor pass hides that pass and does not start another', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-monitor-shutdown-'));
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let accounts = 0;
  let refreshes = 0;
  const { server, planFile } = listen(dir, {
    readAccount: async () => {
      accounts += 1;
      if (accounts >= 2) await gate;
      return { key: null, type: 'unread', source: 'fixture', checkedAt: null };
    },
    readRepository: async () => { refreshes += 1; return { folder: dir, github: 'https://github.com/example/repo' }; }
  });
  const lines = [];
  const original = console.error;
  console.error = (...args) => { lines.push(args.map(String).join(' ')); };
  try {
    const start = Date.now();
    while (accounts < 1) {
      if (Date.now() - start > 3000) throw new Error('account sync did not start');
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const started = Date.now();
    while (accounts < 2) {
      if (Date.now() - started > 3000) throw new Error('the monitor pass did not start');
      await new Promise(resolve => setTimeout(resolve, 10));
    }
    fs.writeFileSync(planFile, '{');
    const stopping = server.shutdown();
    release();
    await stopping;
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(lines.filter(line => line.includes('Execution monitoring failed:')).length, 0);
    assert.equal(lines.filter(line => line.includes('Project autonomy failed:')).length, 0);
    const after = { accounts, refreshes };
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(accounts, after.accounts);
    assert.equal(refreshes, after.refreshes);
    await server.shutdown();
  } finally {
    console.error = original;
    release();
    await server.shutdown();
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
