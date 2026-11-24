'use strict';
// Executes unchanged source modules. Only this harness and its JSON recordings are writable.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const engine = path.resolve(__dirname, '../../../engine');
const epoch = 1700000000000;
const suites = {};
// A capture runs to completion before the next starts. Independent VM lanes share only this log.
let activeEffects;
function plain(value) { return value === undefined ? null : JSON.parse(JSON.stringify(value)); }
function normalize(value, root) {
  if (typeof value === 'string') {
    // JSON files contain escaped Windows paths: normalize their values, not JSON escapes.
    if (value.includes(root) || value.includes(JSON.stringify(root).slice(1, -1))) {
      try {
        const parsed = JSON.parse(value);
        if (parsed && typeof parsed === 'object') return JSON.stringify(normalize(parsed, root), null, value.includes('\n  ') ? 2 : undefined) + (value.endsWith('\n') ? '\n' : '');
      } catch { /* Plain text reports and error messages are normalized below. */ }
      return value.split(root).join('<ROOT>').replace(/\\/g, '/');
    }
    return value.replaceAll('synthetic-token', '<REDACTED>');
  }
  if (Array.isArray(value)) return value.map(v => normalize(v, root));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, normalize(v, root)]));
  return value;
}
function sandbox(root, overrides = {}) {
  const cache = new Map();
  const denied = name => () => { throw new Error('Unexpected dependency: ' + name); };
  const guard = file => {
    if (typeof file !== 'string' || (path.resolve(file) !== root && !path.resolve(file).startsWith(root + path.sep))) throw new Error('Filesystem outside fixture: ' + file);
  };
  const fileApi = api => new Proxy(api, { get(target, key) {
    const method = target[key];
    if (typeof method !== 'function') return method;
    return (...args) => {
      guard(args[0]); if (/^(rename|copyFile|link|symlink)/.test(key)) guard(args[1]);
      const mutation = /^(mkdir|writeFile|appendFile|rename|copyFile|unlink|rm|rmdir|chmod)/.test(key);
      const effect = mutation ? { operation: key.replace(/Sync$/, ''), path: args[0],
        ...(/^(rename|copyFile)/.test(key) ? { destination: args[1] } : {}),
        ...(/^(writeFile|appendFile)/.test(key) ? { content: String(args[1]) } : {}) } : null;
      const finish = result => { if (effect) activeEffects?.push({ ...effect, ok: true }); return result; };
      const fail = error => { if (effect) activeEffects?.push({ ...effect, ok: false, code: error.code }); throw error; };
      try {
        const result = method.apply(target, args);
        return result && typeof result.then === 'function' ? result.then(finish, fail) : finish(result);
      } catch (error) { return fail(error); }
    };
  } });
  const processDouble = { env: {}, platform: process.platform, argv: [], cwd: () => root, stdout: { write: denied('stdout') }, stderr: { write: denied('stderr') }, ...overrides.process };
  class FixedDate extends Date { constructor(...args) { super(...(args.length ? args : [epoch])); } static now() { return epoch; } }
  const context = vm.createContext({ Buffer, URL, AbortSignal, Date: FixedDate, process: processDouble,
    fetch: overrides.fetch || denied('fetch'), setTimeout, clearTimeout, console: { log: denied('console'), error: denied('console') } });
  const builtins = {
    fs: fileApi(fs), 'fs/promises': fileApi(fs.promises), path,
    os: { homedir: () => root, tmpdir: () => root, platform: () => process.platform },
    crypto: { createHash: require('node:crypto').createHash, randomUUID: () => '00000000-0000-4000-8000-000000000001' }, child_process: { spawn: denied('spawn'), spawnSync: denied('spawnSync'), execFile: denied('execFile') },
    ...overrides.builtins
  };
  function load(name) {
    if (Object.hasOwn(overrides.modules || {}, name)) return overrides.modules[name];
    const file = path.resolve(engine, name.replace(/^\.\//, ''));
    if (path.dirname(file) !== engine) throw new Error('Unexpected module: ' + name);
    if (cache.has(file)) return cache.get(file).exports;
    const module = { exports: {} }; cache.set(file, module);
    const localRequire = id => {
      if (id.startsWith('./')) return load(id);
      const key = id.replace(/^node:/, '');
      if (!Object.hasOwn(builtins, key)) throw new Error('Unexpected builtin: ' + id);
      return builtins[key];
    };
    const fn = vm.runInContext('(function(require,module,exports,__filename,__dirname){' + fs.readFileSync(file, 'utf8') + '\n})', context, { filename: file });
    fn(localRequire, module, module.exports, file, engine);
    return module.exports;
  }
  return { load, process: processDouble };
}
function files(root) {
  const output = {};
  function visit(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      const file = path.join(dir, entry.name);
      if (entry.isDirectory()) visit(file);
      else output[path.relative(root, file).split(path.sep).join('/')] = fs.readFileSync(file, 'utf8');
    }
  }
  visit(root); return output;
}
function scenario(module, name, input, execute) {
  (suites[module] ||= []).push({ name, input, execute });
}
async function capture(test) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-scenario-'));
  const requests = [], replies = [], events = [], fileEffects = [];
  activeEffects = fileEffects;
  const exchange = (request, reply) => { requests.push(plain(request)); replies.push(plain(reply)); return reply; };
  try {
    let outcome;
    try { outcome = { result: plain(await test.execute({ root, exchange, events })) }; }
    catch (error) {
      if (error.code === 'ERR_ASSERTION' || /^(Unexpected dependency|Unexpected builtin|Unexpected module|Filesystem outside fixture)/.test(error.message)) throw error;
      outcome = { error: error.message };
    }
    assert.doesNotMatch(JSON.stringify(outcome), /Unexpected dependency|Unexpected builtin|Unconsumed process replies|Command order\/arguments|Filesystem outside fixture/, 'Harness failures are never source outcomes');
    return normalize({ name: test.name, input: test.input, requests, replies, events, fileEffects, effects: files(root), ...outcome }, root);
  } finally { activeEffects = null; fs.rmSync(root, { recursive: true, force: true }); }
}
async function main() {
  if (process.argv.includes('--self-test')) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-safety-'));
    try {
      const box = sandbox(root);
      assert.throws(() => box.load('../server.cjs'), /Unexpected module/);
      await assert.rejects(box.load('provider-models.cjs').listGrok(), /Filesystem outside fixture/);
    } finally { fs.rmSync(root, { recursive: true, force: true }); }
  }
  let stale = false;
  for (const [module, tests] of Object.entries(suites)) {
    const recorded = { schema: 1, module, scenarios: [] };
    for (const test of tests) recorded.scenarios.push(await capture(test));
    if (process.argv.includes('--self-test')) verify(module, recorded.scenarios);
    const text = JSON.stringify(recorded, null, 2) + '\n';
    const destination = path.join(__dirname, 'scenarios', module + '.json');
    if (process.argv.includes('--check')) {
      if (!fs.existsSync(destination) || fs.readFileSync(destination, 'utf8') !== text) { console.error('Scenario drift: ' + module); stale = true; }
    } else { fs.mkdirSync(path.dirname(destination), { recursive: true }); fs.writeFileSync(destination, text); }
    console.log(module + ': ' + tests.length + ' scenarios');
  }
  if (stale) process.exitCode = 1;
}
module.exports = { sandbox, capture, suites, normalize };

for (const mode of ['success', 'empty', 'invalid', 'failure-recovery', 'ttl', 'single-flight']) {
  scenario('provider-models', 'legacy-' + mode, { mode }, async ({ root, exchange }) => {
    const models = [{ id: 'exact', provider: 'grok', cli: 'grok-exact', available: true, enabled: true }];
    let calls = 0, release;
    const api = sandbox(root).load('provider-models.cjs').createProviderModels(models, {
      now: () => epoch,
      discover: async () => {
        calls++;
        if (mode === 'single-flight') await new Promise(resolve => { release = resolve; });
        return exchange({ kind: 'discover', call: calls }, mode === 'failure-recovery' && calls === 1
          ? { ok: false, reason: 'Synthetic outage' } : { ok: true, ids: mode === 'empty' ? [] : mode === 'invalid' ? ['bad', null, 'grok-exact'] : ['grok-exact'] });
      }
    });
    const first = api.refresh(true);
    if (mode === 'single-flight') { const second = api.refresh(true); release(); await Promise.all([first, second]); }
    else await first;
    if (mode === 'ttl') await api.refresh();
    if (mode === 'failure-recovery') await api.refresh(true);
    return { snapshot: api.snapshot(), models, calls };
  });
}
for (const mode of ['success', 'malformed', 'restart', 'invalidate-recovery', 'stale-generation', 'rename-failure', 'not-installed', 'disabled']) {
  scenario('provider-models', 'catalog-' + mode, { mode }, async ({ root, exchange }) => {
    const provider = { id: 'fixture', adapter: 'codex', quotaPoolId: 'fixture', account: { key: 'fake-account' }, enabled: mode !== 'disabled' };
    const models = [{ id: 'exact', providerId: provider.id, cli: 'gpt-exact', available: true, enabled: true }];
    const cacheFile = path.join(root, 'catalog.json');
    if (mode === 'rename-failure') fs.mkdirSync(cacheFile);
    let calls = 0, release;
    const discover = async p => {
      calls++;
      const reply = mode === 'malformed' ? { ok: true, models: [{ cli: '' }] } : mode === 'restart' && calls > 1 ? { ok: false, reason: 'Synthetic outage' } : { ok: true, ids: ['gpt-exact', 'gpt-new'] };
      exchange({ kind: 'discover', provider: plain(p), call: calls }, reply);
      if (mode === 'stale-generation' && calls === 1) await new Promise(resolve => { release = resolve; });
      return reply;
    };
    const create = () => sandbox(root).load('provider-models.cjs').createProviderModels(models, { providers: [provider], now: () => epoch, cacheFile, discover, findExecutable: () => mode !== 'not-installed' });
    let api = create();
    if (mode === 'stale-generation') {
      const pending = api.refresh(true); await api.invalidate(provider.id); const queued = api.refresh(true); release(); await Promise.all([pending, queued]);
    } else {
      await api.refresh(true);
      if (mode === 'restart') { api = create(); await api.refresh(true); }
      if (mode === 'invalidate-recovery') { await api.invalidate(provider.id); await api.refresh(true); }
      if (mode === 'rename-failure') {
        try { await api.invalidate(provider.id); } catch (error) { return { error: error.message, snapshot: api.snapshot(), models }; }
      }
    }
    return { snapshot: api.snapshot(), models, calls };
  });
}

for (const mode of ['success', 'missing-auth', 'http-error', 'malformed', 'identity', 'registration']) {
  scenario('provider-adapters', mode, { mode }, async ({ root, exchange }) => {
    if (mode !== 'missing-auth') fs.writeFileSync(path.join(root, 'auth.json'), JSON.stringify({ tokens: { access_token: 'synthetic-token', account_id: 'fake-account' } }));
    const box = sandbox(root, { fetch: async (url, options) => {
      const reply = { ok: mode !== 'http-error', status: mode === 'http-error' ? 401 : 200, body: mode === 'malformed' ? {} : { models: [{ slug: 'gpt-exact', display_name: 'Exact' }] } };
      exchange({ kind: 'fetch', url, headers: { ...options.headers, Authorization: '<REDACTED>' }, timeoutMs: 10000 }, reply);
      return { ...reply, json: async () => reply.body };
    } });
    const api = box.load('provider-adapters.cjs'), adapter = api.adapterFor('codex');
    if (mode === 'identity') return adapter.resultIdentity({ provider: 'codex', providerId: 'custom', cli: 'gpt-exact', accountKey: 'fake-account' });
    if (mode === 'registration') return api.registerAdapter('codex', {});
    return adapter.discover({ settingsFolder: root });
  });
}

for (const protocol of ['openai', 'anthropic']) for (const mode of ['success', 'wrong-model', 'http-error', 'invalid-json', 'tool-success', 'tool-recovery', 'turn-limit', 'advisory', 'truncated']) {
  scenario('api-agent', protocol + '-' + mode, { protocol, mode }, async ({ root, exchange, events }) => {
    const api = sandbox(root).load('api-agent.cjs');
    let turn = 0;
    const tool = { id: 'call-1', name: 'write_file', input: { path: 'nested/hello.txt', content: 'Hello Ω' } };
    const job = { model: 'exact-model', prompt: 'Fixture prompt', advisory: mode === 'advisory', lead: mode === 'tool-recovery', config: {
      baseUrl: 'https://fixture.invalid', apiProtocol: protocol, maxOutputTokens: 32, maxTurns: mode === 'turn-limit' ? 1 : 3, requestTimeoutMs: 1000
    } };
    return api.runAgent(job, { folder: root, emit: event => events.push(plain(event)), request: async (url, options) => {
      turn++;
      const useTool = ['tool-success', 'tool-recovery', 'turn-limit', 'advisory'].includes(mode) && turn === 1;
      const model = mode === 'wrong-model' ? 'substituted-model' : job.model;
      const data = protocol === 'anthropic' ? { model, usage: { input_tokens: 2, output_tokens: 3 }, stop_reason: mode === 'truncated' ? 'max_tokens' : 'end_turn', content: useTool ? [{ type: 'tool_use', ...tool }] : [{ type: 'text', text: 'Ready Ω' }] }
        : { model, usage: { prompt_tokens: 2, completion_tokens: 3 }, choices: [{ finish_reason: mode === 'truncated' ? 'length' : 'stop', message: { role: 'assistant', content: useTool ? null : 'Ready Ω', ...(useTool ? { tool_calls: [{ id: tool.id, type: 'function', function: { name: tool.name, arguments: JSON.stringify(tool.input) } }] } : {}) } }] };
      const reply = { status: mode === 'http-error' ? 401 : 200, body: mode === 'invalid-json' ? '{' : JSON.stringify(data) };
      exchange({ kind: 'fetch', url, method: options.method, headers: options.headers, body: JSON.parse(options.body), redirect: options.redirect, timeoutMs: 1000 }, reply);
      return { ok: reply.status === 200, status: reply.status, body: { cancel: async () => events.push({ cancelledBody: true }), async *[Symbol.asyncIterator]() { yield Buffer.from(reply.body); } } };
    } });
  });
}
for (const mode of ['write-read', 'control-file', 'escape', 'lead-write', 'command-denied', 'unknown-tool', 'read-limit']) {
  scenario('api-agent', 'tool-' + mode, { mode }, async ({ root, exchange }) => {
    const api = sandbox(root).load('api-agent.cjs');
    if (mode === 'read-limit') fs.writeFileSync(path.join(root, 'large.txt'), 'x'.repeat(65537));
    const call = mode === 'unknown-tool' ? { name: 'unknown', input: {} } : mode === 'command-denied' ? { name: 'run_command', input: { command: 'never-run', args: [] } }
      : mode === 'read-limit' ? { name: 'read_file', input: { path: 'large.txt' } } : { name: 'write_file', input: { path: mode === 'escape' ? '../escape' : mode === 'control-file' ? '.git/config' : 'hello.txt', content: 'Hello Ω' } };
    exchange({ kind: 'tool', call }, { supplied: false });
    const result = await api.executeTool(call, { folder: root, lead: mode === 'lead-write' });
    return mode === 'write-read' ? { result, text: await api.executeTool({ name: 'read_file', input: { path: 'hello.txt' } }, { folder: root }) } : result;
  });
}
// Each process script is consumed exactly once; unmatched commands fail the recording.
function commands(exchange, script) {
  let cursor = 0, violation;
  function run(command, args, options = {}) {
    const row = script[cursor++];
    assert.ok(row, 'Unexpected command: ' + command + ' ' + args.join(' '));
    try { assert.deepEqual(plain(args), row.args, 'Command order/arguments'); } catch (error) { violation = error; throw error; }
    const reply = { status: row.status || 0, stdout: row.stdout || '', stderr: row.stderr || '' };
    exchange({ kind: 'process', command, args, cwd: options.cwd || null, ...(options.timeout ? { timeout: options.timeout } : {}), ...(options.maxBuffer ? { maxBuffer: options.maxBuffer } : {}) }, reply);
    return reply;
  }
  const spawn = (command, args, options) => {
    const reply = run(command, args, options), child = new EventEmitter();
    child.stdout = new EventEmitter(); child.stderr = new EventEmitter();
    queueMicrotask(() => { child.stdout.emit('data', reply.stdout); child.stderr.emit('data', reply.stderr); child.emit('close', reply.status); });
    return child;
  };
  return { spawnSync: run, spawn, done: () => { if (violation) throw violation; assert.equal(cursor, script.length, 'Unconsumed process replies'); } };
}
for (const mode of ['ordered-merge', 'dirty-root', 'dirty-part', 'conflict-abort', 'probe-failure', 'merge-failure', 'recovery']) {
  scenario('worktrees', mode, { mode }, async ({ root, exchange }) => {
    const first = path.join(root, 'part é 1'), second = path.join(root, 'part 2');
    fs.mkdirSync(first); fs.mkdirSync(second);
    const parts = [{ folder: first, branch: 'work--part-1' }, { folder: second, branch: 'work--part-2' }];
    const status = ['status', '--porcelain'];
    const script = [{ args: status, ...(mode === 'dirty-root' ? { stdout: ' M root.txt' } : mode === 'probe-failure' ? { status: 1, stderr: 'Synthetic probe failure' } : {}) }];
    if (!['dirty-root', 'probe-failure'].includes(mode)) {
      script.push({ args: status, stdout: mode === 'dirty-part' ? ' M pending.txt' : '' });
      if (mode !== 'dirty-part') {
        script.push({ args: status }, { args: ['merge', '--no-ff', '--no-edit', parts[0].branch] },
          { args: ['merge', '--no-ff', '--no-edit', parts[1].branch], status: ['conflict-abort', 'merge-failure', 'recovery'].includes(mode) ? 1 : 0, stderr: 'Synthetic merge failure' });
        if (['conflict-abort', 'merge-failure', 'recovery'].includes(mode)) {
          script.push({ args: ['diff', '--name-only', '--diff-filter=U'], stdout: mode === 'merge-failure' ? '' : 'conflict.txt\n' },
            { args: ['rev-parse', '-q', '--verify', 'MERGE_HEAD'], status: mode === 'merge-failure' ? 1 : 0 });
          if (mode !== 'merge-failure') script.push({ args: ['merge', '--abort'] });
        }
      }
    }
    if (mode === 'recovery') script.push({ args: status }, { args: status }, { args: status }, ...parts.map(p => ({ args: ['merge', '--no-ff', '--no-edit', p.branch] })));
    const fake = commands(exchange, script), api = sandbox(root, { builtins: { child_process: fake } }).load('worktrees.cjs');
    const result = api.integrate({ folder: root, parts });
    const recovery = mode === 'recovery' ? api.integrate({ folder: root, parts }) : null;
    fake.done(); return { result, recovery };
  });
}
for (const mode of ['create', 'existing-branch', 'reuse', 'occupied', 'invalid-index', 'invalid-identity', 'cleanup-dirty', 'discard']) {
  scenario('worktrees', mode, { mode }, async ({ root, exchange }) => {
    const folder = path.join(root, 'project é'); fs.mkdirSync(path.join(folder, '.git'), { recursive: true }); fs.writeFileSync(path.join(folder, '.git/config'), '');
    const input = { folder, dir: root, projectId: 'project', megaId: 'MT', subId: mode === 'invalid-identity' ? '../bad' : 'ST', index: mode === 'invalid-index' ? 0 : 1, workBranch: 'work' };
    const destination = path.join(root, 'worktrees/project/MT/ST'), branch = 'work--part-1';
    const script = [];
    if (['cleanup-dirty', 'discard'].includes(mode)) {
      fs.mkdirSync(destination, { recursive: true });
      script.push({ args: ['check-ref-format', '--branch', branch] }, { args: ['status', '--porcelain'], stdout: ' M recoverable.txt' });
      if (mode === 'discard') script.push({ args: ['worktree', 'remove', '--force', '--force', destination] }, { args: ['branch', '-D', branch] });
    } else {
      script.push({ args: ['rev-parse', '--show-toplevel'], stdout: folder });
      if (mode !== 'invalid-index') script.push({ args: ['check-ref-format', '--branch', branch] });
      if (!['invalid-index', 'invalid-identity'].includes(mode)) {
        script.push({ args: ['worktree', 'prune'] });
        if (['reuse', 'occupied'].includes(mode)) {
          fs.mkdirSync(destination, { recursive: true });
          script.push({ args: ['worktree', 'list', '--porcelain', '-z'], stdout: mode === 'reuse' ? 'worktree ' + destination + '\0' : '' });
          if (mode === 'reuse') script.push({ args: ['branch', '--show-current'], stdout: branch });
        } else script.push({ args: ['show-ref', '--verify', '--quiet', 'refs/heads/' + branch], status: mode === 'existing-branch' ? 0 : 1 },
          { args: mode === 'existing-branch' ? ['worktree', 'add', destination, branch] : ['worktree', 'add', '-b', branch, destination, 'work'] });
      }
    }
    const fake = commands(exchange, script), api = sandbox(root, { builtins: { child_process: fake } }).load('worktrees.cjs');
    try { return ['cleanup-dirty', 'discard'].includes(mode) ? api.cleanup({ ...input, parts: [{ folder: destination, branch, subId: 'ST' }], discardBranches: mode === 'discard' }) : api.create(input); }
    finally { fake.done(); }
  });
}

for (const mode of ['list', 'malformed-list', 'list-failure', 'resolve-clean', 'resolve-conflict', 'prepare', 'closed', 'unusual-branch', 'fetch-failure', 'rerun', 'finish-noop', 'push-refused', 'push-success', 'markers']) {
  scenario('pr-check', mode, { mode }, async ({ root, exchange }) => {
    const worktree = path.join(root, 'repair é'), branch = 'feature', base = 'main';
    fs.mkdirSync(worktree);
    const pr = { headRefName: branch, baseRefName: base, mergeable: mode === 'resolve-conflict' ? 'CONFLICTING' : 'MERGEABLE', state: mode === 'closed' ? 'CLOSED' : 'OPEN', statusCheckRollup: [] };
    if (mode === 'unusual-branch') pr.headRefName = '--bad';
    let script;
    if (mode.includes('list')) script = [{ args: ['pr', 'list', '--state', 'open', '--json', 'number,title,mergeable,headRefName,statusCheckRollup,url'], status: mode === 'list-failure' ? 1 : 0, stderr: 'Synthetic failure', stdout: mode === 'malformed-list' ? '{' : JSON.stringify([{ number: 7, title: 'Fixture', ...pr }]) }];
    else if (mode.startsWith('resolve')) {
      script = [{ args: ['pr', 'view', '7', '--json', 'headRefName,baseRefName,mergeable'], stdout: JSON.stringify(pr) }];
      if (mode === 'resolve-conflict') script.push({ args: ['pr', 'checkout', '7'] }, { args: ['fetch', 'origin', base] }, { args: ['merge', 'origin/main', '--no-edit'], status: 1 }, { args: ['merge', '--abort'] }, { args: ['diff', '--name-only', '--diff-filter=U'] });
    } else if (mode === 'rerun') script = [{ args: ['run', 'rerun', '12', '--failed'] }, { args: ['run', 'rerun', '13', '--failed'], status: 1 }];
    else if (['finish-noop', 'push-refused', 'push-success', 'markers'].includes(mode)) {
      script = [{ args: ['diff', '--name-only', '--diff-filter=U'] }, { args: ['grep', '-l', '-E', '^(<<<<<<< |>>>>>>> )', '--', '.'], status: mode === 'markers' ? 0 : 1, stdout: mode === 'markers' ? 'bad.txt' : '' }];
      if (mode !== 'markers') {
        script.push({ args: ['rev-parse', '--git-dir'], stdout: '.git' }, { args: ['status', '--porcelain'] }, { args: ['rev-parse', 'HEAD'], stdout: mode === 'finish-noop' ? 'before' : 'after' });
        if (mode !== 'finish-noop') script.push({ args: ['push', 'origin', 'HEAD:refs/heads/feature'], status: mode === 'push-refused' ? 1 : 0, stderr: 'remote advanced' });
      }
    } else {
      script = [{ args: ['pr', 'view', '7', '--json', 'headRefName,baseRefName,mergeable,statusCheckRollup,state'], stdout: JSON.stringify(pr) }];
      if (!['closed', 'unusual-branch'].includes(mode)) {
        script.push({ args: ['fetch', 'origin', '+refs/heads/feature:refs/remotes/origin/feature', '+refs/heads/main:refs/remotes/origin/main'], status: mode === 'fetch-failure' ? 1 : 0 });
        if (mode !== 'fetch-failure') script.push({ args: ['worktree', 'add', '--detach', worktree, 'origin/feature'] }, { args: ['rev-parse', 'HEAD'], stdout: 'before' }, { args: ['merge', 'origin/main', '--no-edit'] });
      }
    }
    const fake = commands(exchange, script), api = sandbox(root, { builtins: { child_process: fake } }).load('pr-check.cjs');
    try {
      if (mode.includes('list')) return await api.listPrs(root);
      if (mode.startsWith('resolve')) return await api.resolveConflicts(root, 7);
      if (mode === 'rerun') return await api.rerunNotRun(root, [{ runId: '12' }, { runId: '12' }, { runId: '13' }, {}]);
      if (['finish-noop', 'push-refused', 'push-success', 'markers'].includes(mode)) return await api.finishFix(root, worktree, { before: 'before', branch, conflicts: [] }, 7);
      return await api.prepareFix(root, 7, worktree);
    } finally { fake.done(); }
  });
}
for (const mode of ['blocked-no-token', 'allowed', 'listener', 'installed-model', 'probe-failure', 'local-failure', 'live', 'live-blocked', 'report-collision', 'bad-listeners', 'backup-notebook']) {
  scenario('phone-activation-check', mode, { mode }, async ({ root, exchange, events }) => {
    const attempts = path.join(root, 'attempts'); fs.mkdirSync(attempts);
    if (!['blocked-no-token', 'live-blocked'].includes(mode)) fs.writeFileSync(path.join(attempts, 'telegram-bot.token'), 'synthetic-token');
    const report = path.join(root, mode === 'report-collision' ? 'report.txt' : 'report.md');
    const argv = ['node', 'phone-activation-check.cjs', '--attempts', attempts, '--report', report, '--build', path.join(root, 'build/Planwharf.exe')];
    if (['live', 'live-blocked'].includes(mode)) argv.push('--live', 'yes');
    if (['listener', 'bad-listeners'].includes(mode)) {
      const file = path.join(root, 'listeners.json');
      fs.writeFileSync(file, mode === 'bad-listeners' ? '{' : JSON.stringify([{ path: path.join(root, 'installed/Planwharf.exe'), running: true, pollsTelegram: true, pid: 17 }]));
      argv.push('--listeners', file);
    }
    if (mode === 'backup-notebook') {
      const source = path.join(root, 'source'); fs.mkdirSync(source);
      const starter = path.join(root, 'starter.json'); fs.writeFileSync(starter, '{"schema":2,"projects":[]}\n');
      fs.writeFileSync(path.join(source, 'plan.json'), '{"schema":2,"projects":[]}\n');
      argv.push('--from', source, '--backup-to', path.join(root, 'backup'), '--notebook', path.join(root, 'isolated'), '--starter', starter);
    }
    let complete;
    const finished = new Promise(resolve => { complete = resolve; });
    const installed = ['installed-model', 'local-failure', 'report-collision'].includes(mode);
    const box = sandbox(root, { process: { argv, stdout: { write: text => { events.push({ stdout: text }); complete(); } }, stderr: { write: text => { events.push({ stderr: text }); complete(); } } },
      modules: { './phone-ollama.cjs': {
        listInstalledModels: async options => {
          const reply = { ok: mode !== 'probe-failure', models: installed ? ['qwen2.5:3b'] : [] };
          exchange({ kind: 'listInstalledModels', options }, reply); return reply;
        },
        askOllama: async options => {
          const reply = { ok: mode !== 'local-failure', model: 'qwen2.5:3b', ...(mode === 'local-failure' ? { reason: 'Synthetic local failure' } : { text: 'ready' }) };
          exchange({ kind: 'askOllama', options }, reply); return reply;
        }
      } }, fetch: async url => {
        assert.equal(mode, 'live', 'Blocked activation must never request Telegram');
        const reply = { ok: true, result: [{ update_id: 1 }] };
        exchange({ kind: 'fetch', url: url.replace('synthetic-token', '<REDACTED>') }, reply);
        return { ok: true, json: async () => reply };
      } });
    box.load('phone-activation-check.cjs');
    let timer;
    try { await Promise.race([finished, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Activation main did not finish')), 2000); })]); }
    finally { clearTimeout(timer); }
    // main's catch sets exitCode after stderr.write returns.
    await Promise.resolve();
    // Never retain even synthetic credential bytes in the artifact.
    if (fs.existsSync(path.join(attempts, 'telegram-bot.token'))) fs.writeFileSync(path.join(attempts, 'telegram-bot.token'), '<REDACTED>');
    if (fs.existsSync(path.join(root, 'backup/telegram-bot.token'))) fs.writeFileSync(path.join(root, 'backup/telegram-bot.token'), '<REDACTED>');
    return { exitCode: box.process.exitCode || 0 };
  });
}
function verify(module, rows) {
  const get = name => { const row = rows.find(row => row.name === name); assert.ok(row, name); return row; };
  assert.ok(rows.length > 0);
  for (const row of rows) assert.equal(row.requests.length, row.replies.length, 'Every request has a supplied reply');
  if (module === 'provider-models') {
    assert.equal(get('legacy-single-flight').result.calls, 1);
    assert.equal(get('legacy-ttl').result.calls, 1);
    assert.equal(get('catalog-stale-generation').result.calls, 2);
    assert.equal(get('catalog-success').result.models[1].enabled, false);
    assert.equal(get('catalog-rename-failure').result.models[0].available, false);
  } else if (module === 'provider-adapters') {
    assert.equal(get('success').result.models[0].cli, 'gpt-exact');
    assert.equal(get('missing-auth').requests.length, 0);
    assert.equal(get('http-error').result.ok, false);
  } else if (module === 'api-agent') {
    for (const protocol of ['openai', 'anthropic']) {
      assert.equal(get(protocol + '-success').result.answer, 'Ready Ω');
      assert.equal(get(protocol + '-tool-recovery').requests.length, 2);
      assert.equal(get(protocol + '-tool-success').effects['nested/hello.txt'], 'Hello Ω');
      assert.match(get(protocol + '-wrong-model').error, /IDENTITY_CHANGED/);
      assert.match(get(protocol + '-advisory').error, /PERMISSION_DENIED/);
    }
    assert.equal(get('tool-write-read').effects['hello.txt'], 'Hello Ω');
    assert.match(get('tool-control-file').error, /control files/);
  } else if (module === 'worktrees') {
    assert.equal(get('ordered-merge').result.result.ok, true);
    assert.equal(get('recovery').result.recovery.ok, true);
    assert.equal(get('conflict-abort').requests.at(-1).args[1], '--abort');
    assert.match(get('cleanup-dirty').error, /recoverable/);
  } else if (module === 'pr-check') {
    assert.equal(get('prepare').result.ok, true);
    assert.equal(get('push-refused').result.kept, true);
    assert.equal(get('push-success').result.changed, true);
    assert.equal(get('finish-noop').result.changed, false);
  } else {
    assert.equal(get('allowed').events[0].stdout, 'ALLOWED\nno blocker\n');
    assert.equal(get('live-blocked').requests.length, 1);
    assert.equal(get('live').requests.length, 2);
    assert.equal(get('bad-listeners').result.exitCode, 1);
    assert.ok(get('report-collision').effects['report.txt'].startsWith('{'));
  }
  // This check is deliberately sensitive to a meaningful final-value mutation.
  const changed = plain(rows); changed[0].name += '-mutated';
  assert.notDeepEqual(changed, rows);
}
for (const mode of ['exact-ids', 'empty', 'policy', 'login']) {
  scenario('provider-models', 'grok-list-' + mode, { mode }, async ({ root, exchange }) => {
    const box = sandbox(root, { modules: { './providers.cjs': { executable: () => 'fake-grok' } }, builtins: { child_process: {
      execFile(command, args, options, callback) {
        const reply = { error: ['policy', 'login'].includes(mode) ? { code: 1 } : null,
          stdout: mode === 'exact-ids' ? '\u001b[32mgrok-exact grok-exact grok-2:fast\u001b[0m' : '', stderr: mode === 'policy' ? 'policy lock file is not accessible' : '' };
        exchange({ kind: 'process', command, args, options }, reply); callback(reply.error, reply.stdout, reply.stderr);
      }
    } } });
    return box.load('provider-models.cjs').listGrok();
  });
}
for (const mode of ['missing-method', 'missing-regex', 'registered', 'unknown-adapter', 'account-identity']) {
  scenario('provider-adapters', mode, { mode }, async ({ root, exchange }) => {
    const api = sandbox(root).load('provider-adapters.cjs');
    if (mode === 'unknown-adapter') return { found: Boolean(api.adapterFor('unknown')) };
    if (mode === 'account-identity') return api.adapterFor('codex').resultIdentity({ providerId: 'fixture', cli: 'gpt-exact', accountKey: 'fake-account' });
    const adapter = Object.fromEntries(['executable', 'availability', 'execute', 'environment', 'terminate', 'quota', 'discover', 'resultIdentity'].map(method => [method, (...args) => exchange({ method, args }, { ok: true })]));
    if (mode !== 'missing-regex') adapter.nativeModel = api.adapterFor('codex').nativeModel;
    if (mode === 'missing-method') delete adapter.execute;
    api.registerAdapter('fixture', adapter);
    return { frozen: Object.isFrozen(api.adapterFor('fixture')), reply: await api.adapterFor('fixture').discover({ id: 'fixture' }) };
  });
}
scenario('api-agent', 'concurrent-isolated-tools', { lanes: ['one', 'two'] }, async ({ root, exchange }) => {
  const api = sandbox(root).load('api-agent.cjs');
  return Promise.all(['one', 'two'].map(async lane => {
    const folder = path.join(root, lane); fs.mkdirSync(folder);
    const call = { name: 'write_file', input: { path: 'result.txt', content: lane } };
    exchange({ kind: 'tool', lane, call }, { supplied: false });
    await api.executeTool(call, { folder });
    return api.executeTool({ name: 'read_file', input: { path: 'result.txt' } }, { folder });
  }));
});
scenario('pr-check', 'concurrent-independent-repairs', { branches: ['one', 'two'] }, async ({ root, exchange }) => {
  const api = sandbox(root).load('pr-check.cjs');
  return Promise.all(['one', 'two'].map(branch => api.prepareWorktree(root, { branch, base: 'main' }, path.join(root, branch), async (cwd, command, args) => {
    const reply = { status: 0, stdout: args[0] === 'rev-parse' ? 'head-' + branch : '', stderr: '' };
    exchange({ kind: 'process', command, args, cwd }, reply); return reply;
  })));
});
scenario('worktrees', 'orphan-sweep-retains-live', { liveMegaIds: ['live'] }, ({ root }) => {
  for (const id of ['live', 'orphan']) fs.mkdirSync(path.join(root, 'worktrees/project', id), { recursive: true });
  fs.writeFileSync(path.join(root, 'worktrees/project/orphan/recoverable.txt'), 'Disposable fixture');
  const api = sandbox(root).load('worktrees.cjs');
  return { removed: api.sweepOrphans({ dir: root, liveMegaIds: ['live'] }), liveRetained: fs.existsSync(path.join(root, 'worktrees/project/live')) };
});
scenario('phone-activation-check', 'concurrent-isolated-checks', { lanes: ['one', 'two'] }, async ({ root, exchange, events }) => {
  return Promise.all(['one', 'two'].map(async lane => {
    const dir = path.join(root, lane); fs.mkdirSync(dir);
    let complete; const finished = new Promise(resolve => { complete = resolve; });
    const box = sandbox(root, { process: { argv: ['node', 'check', '--attempts', dir, '--report', path.join(dir, 'report.md')],
      stdout: { write: text => { events.push({ lane, stdout: text }); complete(); } },
      stderr: { write: text => { events.push({ lane, stderr: text }); complete(); } } }, modules: { './phone-ollama.cjs': {
      listInstalledModels: async options => { const reply = { ok: true, models: [] }; exchange({ lane, kind: 'listInstalledModels', options }, reply); return reply; },
      askOllama: () => { throw new Error('Unexpected inference'); }
    } } });
    box.load('phone-activation-check.cjs');
    let timer;
    try { await Promise.race([finished, new Promise((_, reject) => { timer = setTimeout(() => reject(new Error('Activation main did not finish')), 2000); })]); }
    finally { clearTimeout(timer); }
    return { lane, exitCode: box.process.exitCode || 0 };
  }));
});
// Every preparation command failure must consume only the replies reachable before that failure.
for (const stage of ['view', 'add', 'merge']) {
  scenario('pr-check', 'prepare-failure-' + stage, { stage }, async ({ root, exchange }) => {
    const worktree = path.join(root, 'repair');
    const script = [{ args: ['pr', 'view', '7', '--json', 'headRefName,baseRefName,mergeable,statusCheckRollup,state'], status: stage === 'view' ? 1 : 0, stdout: JSON.stringify({ headRefName: 'feature', baseRefName: 'main', state: 'OPEN' }) }];
    if (stage !== 'view') {
      script.push({ args: ['fetch', 'origin', '+refs/heads/feature:refs/remotes/origin/feature', '+refs/heads/main:refs/remotes/origin/main'] },
        { args: ['worktree', 'add', '--detach', worktree, 'origin/feature'], status: stage === 'add' ? 1 : 0 });
      if (stage === 'merge') script.push({ args: ['rev-parse', 'HEAD'], stdout: 'before' }, { args: ['merge', 'origin/main', '--no-edit'], status: 1 },
        { args: ['diff', '--name-only', '--diff-filter=U'] }, { args: ['worktree', 'remove', '--force', worktree] }, { args: ['worktree', 'prune'] });
    }
    const fake = commands(exchange, script), api = sandbox(root, { builtins: { child_process: fake } }).load('pr-check.cjs');
    try { return await api.prepareFix(root, 7, worktree); } finally { fake.done(); }
  });
}
scenario('provider-models', 'switch-away-and-back-rejects-old-generation', { accounts: ['fake-A', 'fake-B', 'fake-A'] }, async ({ root, exchange }) => {
  const provider = { id: 'fixture', adapter: 'codex', account: { key: 'fake-A' }, quotaPoolId: 'fixture' };
  const models = [{ id: 'exact', providerId: 'fixture', cli: 'gpt-exact', available: false }];
  let release, calls = 0;
  const api = sandbox(root).load('provider-models.cjs').createProviderModels(models, { providers: [provider], now: () => epoch,
    discover: async p => {
      calls++; const reply = calls === 1 ? { ok: true, ids: ['gpt-exact', 'gpt-stale'] } : { ok: false, reason: 'Fresh authorization required' };
      exchange({ kind: 'discover', provider: plain(p), call: calls }, reply);
      if (calls === 1) await new Promise(resolve => { release = resolve; });
      return reply;
    }
  });
  const pending = api.refresh(true);
  provider.account.key = 'fake-B'; await api.invalidate('fixture'); api.observeScope('fixture');
  provider.account.key = 'fake-A'; await api.invalidate('fixture'); api.observeScope('fixture');
  const next = api.refresh(true); release(); await Promise.all([pending, next]);
  assert.equal(models.some(model => model.cli === 'gpt-stale'), false);
  return { snapshot: api.snapshot(), models, calls };
});
scenario('api-agent', 'approved-command-fake-process', { command: 'fake-tool', args: ['space Ω'] }, async ({ root, exchange }) => {
  const box = sandbox(root, { builtins: { child_process: { execFile(command, args, options, callback) {
    const reply = { error: { code: 7 }, stdout: 'Synthetic output Ω', stderr: 'Synthetic failure' };
    exchange({ kind: 'process', command, args, options }, reply); callback(reply.error, reply.stdout, reply.stderr);
  } } } });
  return box.load('api-agent.cjs').executeTool({ name: 'run_command', input: { command: 'fake-tool', args: ['space Ω'] } }, { folder: root, alwaysApprove: true });
});
if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
