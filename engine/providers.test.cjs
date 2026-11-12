'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { launchSpec, executable, providerEnvironment } = require('./providers.cjs');
const inherited = { PATH: 'preserved-path', CODEX_THREAD_ID: 'parent-session' };
const windowsEnv = providerEnvironment('codex', inherited, 'win32');
assert.deepEqual(inherited, { PATH: 'preserved-path', CODEX_THREAD_ID: 'parent-session' }, 'Do not mutate the host environment');
assert.equal(windowsEnv.PATH, inherited.PATH);
assert.equal(windowsEnv.CODEX_THREAD_ID, inherited.CODEX_THREAD_ID);
const certificates = fs.readFileSync(windowsEnv.CODEX_CA_CERTIFICATE, 'utf8').match(/-----BEGIN CERTIFICATE-----[\s\S]+?-----END CERTIFICATE-----/g);
assert.ok(certificates.length > 0);
for (const certificate of certificates) assert.ok(new (require('node:crypto').X509Certificate)(certificate).ca, 'Only trust CA certificates');
assert.equal(windowsEnv.NODE_TLS_REJECT_UNAUTHORIZED, undefined);
assert.equal(providerEnvironment('codex', inherited, 'win32').CODEX_CA_CERTIFICATE, windowsEnv.CODEX_CA_CERTIFICATE);
for (const key of ['CODEX_CA_CERTIFICATE', 'SSL_CERT_FILE']) {
  const explicit = { ...inherited, [key]: 'configured-custom-ca.pem' };
  assert.deepEqual(providerEnvironment('codex', explicit, 'win32'), explicit, 'Respect the operator CA configuration');
}
for (const provider of ['antigravity', 'grok']) assert.deepEqual(providerEnvironment(provider, inherited, 'win32'), inherited);
assert.deepEqual(providerEnvironment('codex', inherited, 'linux'), inherited);
if (process.platform === 'win32') {
  const actual = { PATH: process.env.PATH, CODEX_CA_CERTIFICATE: 'operator-ca.pem' };
  const effective = providerEnvironment('codex', actual);
  assert.equal(effective.CODEX_CA_CERTIFICATE, actual.CODEX_CA_CERTIFICATE);
  for (const directory of actual.PATH.split(';')) {
    const shell = path.join(directory, 'pwsh.exe');
    let present = false; try { fs.lstatSync(shell); present = true; } catch {}
    const denied = /WindowsApps/i.test(directory) && present && require('node:child_process').spawnSync(shell, ['-NoProfile', '-NonInteractive', '-Command', 'exit 0'], { windowsHide: true, stdio: 'ignore', timeout: 2000 }).status !== 0;
    assert.equal(effective.PATH.split(';').includes(directory), !denied, 'Exclude only Store shell paths denied to the current account');
  }
}
const { parseUsage, readAntigravity } = require('./antigravity.cjs');
const { createAskStream } = require('./runner.cjs');
const { pickWorker } = require('./orchestrator.js');
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, '../fixtures/antigravity-usage.json'), 'utf8').replace(/^\uFEFF/, ''));
if (process.platform === 'win32') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-native-cli-'));
  fs.writeFileSync(path.join(dir, 'codex'), '#!/bin/sh\n');
  const savedPath = process.env.PATH;
  const savedOverride = process.env.PLANWHARF_CODEX;
  delete process.env.PLANWHARF_CODEX;
  process.env.PATH = dir;
  assert.equal(executable('codex'), null, 'Reject the extensionless Windows npm shell script');
  const native = path.join(dir, 'node_modules', '@openai', 'codex', 'node_modules', '@openai', 'codex-win32-' + (process.arch === 'arm64' ? 'arm64' : 'x64'), 'vendor', process.arch === 'arm64' ? 'aarch64-pc-windows-msvc' : 'x86_64-pc-windows-msvc', 'bin', 'codex.exe');
  fs.mkdirSync(path.dirname(native), { recursive: true });
  fs.writeFileSync(native, 'native fixture');
  fs.writeFileSync(path.join(dir, 'codex.exe'), 'proxy fixture');
  assert.equal(executable('codex'), native, 'Prefer the npm native binary over the PATH proxy');
  process.env.PATH = savedPath;
  if (savedOverride !== undefined) process.env.PLANWHARF_CODEX = savedOverride;
  fs.rmSync(dir, { recursive: true, force: true });
}
for (const provider of ['grok', 'codex', 'antigravity']) process.env['PLANWHARF_' + provider.toUpperCase()] = process.execPath;
const grok = launchSpec({ provider: 'grok', cli: 'grok-4.6', alwaysApprove: true }, 'D:/demo folder', 'job');
assert.equal(grok.args[grok.args.indexOf('-m') + 1], 'grok-4.6');
assert.ok(!grok.args.includes('-p'));
assert.equal(fs.readFileSync(grok.promptFile, 'utf8'), 'job');
assert.ok(grok.args.includes('--always-approve'));
assert.ok(grok.args.includes('--no-subagents'));
const agy = launchSpec({ provider: 'antigravity', cli: 'gemini-3.1-pro-high', alwaysApprove: true }, 'D:/demo', 'job');
assert.ok(agy.args.includes('--dangerously-skip-permissions'));
assert.equal(agy.cwd, 'D:/demo');
assert.ok(!agy.args.includes('--cwd'));
assert.ok(!agy.args.includes('-p'));
assert.equal(agy.args[agy.args.indexOf('--input-format') + 1], 'stream-json');
assert.equal(JSON.parse(agy.stdin).message.content, 'job');
assert.equal(JSON.parse(agy.stdin).event, 'user');
const codex = launchSpec({ provider: 'codex', cli: 'gpt-6-astra', alwaysApprove: true }, 'D:/demo', 'job');
assert.equal(codex.args[0], 'exec');
assert.ok(codex.args.includes('--dangerously-bypass-approvals-and-sandbox'));
assert.equal(codex.args.at(-1), '-');
assert.equal(codex.stdin, 'job');
assert.ok(!codex.args.includes('job'));
assert.equal(grok.stdin, undefined);
assert.ok(agy.stdin.endsWith('\n'));
for (const model of [{ provider: 'codex', cli: 'grok-4.7' }, { provider: 'grok', cli: 'gemini-pro' }, { provider: 'antigravity', cli: 'grok-4.6' }, { provider: 'bogus', cli: 'gpt-5' }]) assert.throws(() => launchSpec(model, '.', 'job'), /mismatch/);
assert.throws(() => launchSpec({ available: false, unavailableReason: 'Account unavailable' }, '.', 'job'), /Account unavailable/);
for (const [provider, cli] of [['grok', 'grok-4.6'], ['codex', 'gpt-6-astra'], ['antigravity', 'gemini-3.1-pro-high']]) {
  // An approved lead runs as approved; without the OK it is restricted (Antigravity cannot run at all).
  const lead = launchSpec({ provider, cli, alwaysApprove: true }, '.', 'Coordinate only', true);
  if (provider === 'antigravity') assert.throws(() => launchSpec({ provider, cli }, '.', 'Coordinate only', true), /PERMISSION_MODE_UNSUPPORTED/);
  else assert.ok(!launchSpec({ provider, cli }, '.', 'Coordinate only', true).args.includes(provider === 'grok' ? '--always-approve' : '--dangerously-bypass-approvals-and-sandbox'));
  assert.ok(lead.args.includes(provider === 'grok' ? '--always-approve' : provider === 'codex' ? '--dangerously-bypass-approvals-and-sandbox' : '--dangerously-skip-permissions'));
  assert.ok(!lead.args.includes('read-only'));
  assert.ok(!lead.args.includes('plan'));
}
assert.equal(pickWorker([{ id: 'unavailable', enabled: true, available: false }]), null);
const quota = parseUsage(fixture);
assert.equal(quota.readable, true);
assert.equal(quota.windows.length, 2);
assert.equal(quota.leftPct, Math.round(fixture.command.data.groups[0].buckets[1].remaining_fraction * 100));
assert.equal(parseUsage({ status: 'SUCCESS' }).pct, null);
const absent = structuredClone(fixture);
absent.command.data.groups[0].buckets.forEach(b => delete b.remaining_fraction);
assert.equal(parseUsage(absent).readable, false);
const zero = structuredClone(fixture);
zero.command.data.groups[0].buckets[1].remaining_fraction = 0;
assert.equal(parseUsage(zero).exhausted, true);
zero.command.data.groups[0].buckets[1].remaining_fraction = 0.0001;
assert.equal(parseUsage(zero).exhausted, false);
(async () => {
  const failed = await readAntigravity({ command: 'fake', run: (_exe, args, _opts, done) => { assert.deepEqual(args.slice(0, 4), ['-p', '/usage', '--output-format', 'json']); done(new Error('No login'), ''); } });
  assert.equal(failed.pct, null);
  const malformed = await readAntigravity({ command: 'fake', run: (_exe, _args, _opts, done) => done(null, 'broken') });
  assert.equal(malformed.readable, false);
  const stream = createAskStream();
  stream.push('{"type":"item.completed","item":{"type":"agent_message","text":"Coordinating"}}\n');
  assert.equal(stream.finish().answer, 'Coordinating');
  const agyStream = createAskStream();
  agyStream.push(fs.readFileSync(path.join(__dirname, '../fixtures/antigravity-lead.jsonl'), 'utf8').replace(/^\uFEFF/, ''));
  assert.equal(agyStream.finish().answer, 'coordination only.');
  console.log('OK: explicit provider routing, noninteractive workers and leads, verified agy quota schema and failures.');
})().catch(error => { console.error(error); process.exitCode = 1; });
