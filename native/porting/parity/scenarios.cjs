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
      return value.split(root).join('<ROOT>').split(JSON.stringify(root).slice(1, -1)).join('<ROOT>').replace(/\\/g, '/');
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
      if (!(/^(fstat|read|write|close)(Sync)?$/.test(key) && Number.isInteger(args[0]))) guard(args[0]);
      if (/^(rename|copyFile|link|symlink)/.test(key)) guard(args[1]);
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
  const context = vm.createContext({ Buffer, URL, AbortSignal, Date: FixedDate, structuredClone, process: processDouble,
    fetch: overrides.fetch || denied('fetch'), setTimeout, clearTimeout, console: { log: denied('console'), error: denied('console') } });
  const builtins = {
    fs: fileApi(overrides.fs || fs), 'fs/promises': fileApi(fs.promises), path,
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
    const content = typeof overrides.transform === 'function' ? overrides.transform(file, fs.readFileSync(file, 'utf8')) : fs.readFileSync(file, 'utf8');
    const fn = vm.runInContext('(function(require,module,exports,__filename,__dirname){\n' + content + '\n})', context, { filename: file });
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
  } else if (module === 'execution') {
    assert.deepEqual(get('helpers-and-no-verdict-matrix').result.plans, ['retry-same', 'switch', 'stop']);
    assert.ok(get('helpers-and-no-verdict-matrix').result.delivered[0]);
    assert.equal(get('helpers-and-no-verdict-matrix').result.delivered[1], null);
    assert.equal(get('proposal-approval-and-recovery').result.recoveredSame, true);
    assert.equal(get('addition-kill-failure-resets-verify').result.store.execution.subTasks.find(sub => /^Verify/.test(sub.title)).status, 'PENDING');
    assert.equal(get('paused-addition-resumes').result.store.execution.megaTasks[0].status, 'RUNNING');
    assert.equal(get('first-answer-after-start').result.first.ok, true);
    assert.equal(get('delegation-replaces-after-start').result.decision.answer, 'suggested answer');
  } else if (module === 'project-home') {
    assert.equal(get('local-git-success-and-failure').result.success.readable, true);
    assert.equal(get('local-git-success-and-failure').result.failure.readable, false);
    assert.equal(get('durable-megatask-outranks-row').result.home.action, 'approve');
    assert.match(get('durable-megatask-outranks-row').result.home.next, /Approve/);
  } else if (module === 'orchestrator-team-models') {
    const isolation = get('project-team-role-isolation-and-account-scope').result;
    assert.equal(isolation.alpha.selectedOrchestratorId, 'opus');
    assert.equal(isolation.alpha.models.find(m => m.id === 'opus').roles.worker, false);
    assert.equal(isolation.beta.selectedOrchestratorId, 'astra');
    assert.equal(isolation.beta.models.find(m => m.id === 'opus').roles.worker, true);
    assert.equal(isolation.switchedAccount.find(m => m.id === 'astra').enabled, false);
    const handover = get('lead-automatic-handover-on-quota-depletion').result;
    assert.equal(handover.before.orchestratorId, 'opus');
    assert.equal(handover.after.orchestratorId, 'astra');
    assert.match(handover.after.log[0].text, /quota of Opus 5\.5 is at 12% \(switch at 35%\)\./);
    const lockout = get('twenty-four-hour-credit-lockout').result;
    assert.equal(lockout.evaluations[0].opus.creditsNeeded, true);
    assert.equal(lockout.evaluations[2].opus.available, false);
    assert.equal(lockout.evaluations[3].opus.available, true);
    const saving = get('saving-mode-candidate-filtering').result;
    assert.equal(saving.normal.picked.id, 'opus');
    assert.equal(saving.safe.picked.id, 'sonnet');
    assert.equal(saving.ultraSafe.picked.id, 'haiku');
    assert.equal(saving.teamWeightOverride.picked.id, 'haiku');
  } else if (module === 'orchestrator-job-building') {
    const plan = get('plan-by-lead').result;
    assert.equal(plan.ok, true);
    assert.equal(plan.replanned, true);
    assert.equal(plan.subTasks.length, 3);
    const assign = get('assign-by-lead').result;
    assert.equal(assign.ok, true);
    assert.equal(assign.assigned, true);
    const start = get('start-auto-task').result;
    assert.equal(start.grill.phase, 'grill');
    assert.equal(start.plan.phase, 'plan');
    assert.equal(start.plan.job.timing, 'deadline');
    const open = get('open-work-plan').result;
    assert.equal(open.ok, true);
    assert.equal(open.planExists, true);
    const decide = get('decide').result;
    assert.equal(decide.completion.status, 'complete');
    assert.equal(decide.mutation.approved, false);
    assert.equal(decide.advance.status, 'needs_approval');
  } else if (module === 'orchestrator-lead-questions') {
    const streamed = get('streamed-envelope-project-isolation-and-unsolicited-research');
    assert.equal(streamed.result.recordedAnswerContainsPrivateBeta, false);
    assert.doesNotMatch(JSON.stringify(streamed.requests[0].state), /PRIVATE_BETA_GOAL/);
    assert.equal(streamed.result.turn.actionResults[0].ok, false);
    assert.equal(streamed.events.length, 2);
    const process = get('process-failure-retry-then-alternate-provider');
    assert.deepEqual(process.events.map(event => event.modelId), ['astra', 'astra', 'opus']);
    for (const reason of ['provider-usage-limit', 'credits-needed']) {
      const immediate = get('immediate-handover-' + reason);
      assert.deepEqual(immediate.events.map(event => event.modelId), ['astra', 'opus']);
    }
    const strict = get('unreadable-autonomy-decision-strict-retry');
    assert.equal(strict.requests.length, 2);
    assert.equal(strict.requests[1].question, strict.requests[0].question + '\n\nYour previous reply could not be read. Reply with only the JSON object, with no other text before or after it.');
    const cancelled = get('mid-turn-cancellation-preserves-only-stop-record');
    assert.equal(cancelled.result.cancelled, true);
    assert.equal(cancelled.result.asks[0].answer, 'Stopped.');
    assert.doesNotMatch(JSON.stringify(cancelled.result.asks), /must not be persisted/);
  } else if (module === 'orchestrator-state') {
    for (const row of rows) {
      assert.ok(!row.error, 'Unexpected scenario error in ' + row.name + ': ' + row.error);
      assert.ok(Array.isArray(row.fileEffects), 'Missing fileEffects in ' + row.name);
      assert.ok(row.effects && typeof row.effects === 'object', 'Missing persisted effects in ' + row.name);
    }
    const load = get('fresh-and-legacy-load-unknown-fields-and-persist-bytes');
    assert.equal(load.result.fresh.endsWithNewline, true);
    assert.equal(load.result.fresh.singleLine, true);
    assert.equal(load.result.legacy.mode, 'manual');
    assert.equal(load.result.legacy.grillPropose, false);
    assert.equal(load.result.legacy.alphaChatMode, 'plan');
    assert.equal(load.result.legacy.betaChatMode, 'talk');
    assert.equal(load.result.legacy.alphaSavingMode, 'normal');
    assert.equal(load.result.legacy.betaSavingMode, 'safe');
    assert.equal(load.result.legacy.autonomyEnabled, false);
    assert.equal(load.result.legacy.preservedRowField, 'keep-me');
    assert.equal(load.result.legacy.preservedAskField, 42);
    assert.equal(load.result.legacy.preservedExecField, 'keep-exec');
    assert.ok(load.result.legacy.hydratedSessionIds.includes('hist-sess-1'));
    const atomic = get('atomic-persist-retry-and-failure');
    assert.equal(atomic.result.afterRetryMode, 'auto');
    assert.equal(atomic.result.afterFailMode, 'auto');
    assert.match(atomic.result.permError, /EPERM/);
    assert.deepEqual(atomic.result.remainingTmps, []);
    assert.ok(atomic.fileEffects.some(e => e.operation === 'rename' && e.ok === false && e.code === 'EBUSY'));
    assert.ok(atomic.fileEffects.some(e => e.operation === 'rename' && e.ok === true));
    const life = get('lifecycle-snapshots-and-execution-row-sharing');
    assert.equal(life.result.approval.status, 'needs_approval');
    assert.equal(life.result.watching.status, 'watching');
    assert.equal(life.result.watching.sharedMegaTaskId, true);
    assert.equal(life.result.complete.status, 'complete');
    assert.ok(life.result.activityTotal >= 1);
    const cp = get('checkpoint-hydration-on-startup');
    assert.equal(cp.result.alpha.status, 'checkpointed');
    assert.equal(cp.result.beta.status, 'checkpointed');
    assert.equal(cp.requests.filter(r => r.kind === 'spawnWorker').length, 0);
    const rec = get('alive-and-dead-worker-restart-reconciliation');
    assert.equal(rec.result.firstTick.alpha.workerAlive, true);
    assert.equal(rec.result.firstTick.beta.workerAlive, false);
    assert.equal(rec.result.secondTickBeta.workerAlive, false);
    assert.equal(rec.requests.filter(r => r.kind === 'spawnWorker').length, 0);
    const res = get('research-v1-and-v2-lifecycle-and-row-sharing');
    assert.equal(res.result.v1.watchingAlive, true);
    assert.equal(res.result.v1.completeStatus, 'complete');
    assert.equal(res.result.v1.reportAvailable, true);
    assert.equal(res.result.v2.executionVersion, 2);
    assert.equal(res.result.v2.approvedOk, true);
    assert.ok(res.result.v2.researchDispatchLen >= 1);
    assert.equal(res.result.v2.projectRowDispatchLen, 0);
    const arch = get('archive-cleanup-focus-switching-and-two-project-isolation');
    assert.equal(arch.result.focusBeta.ok, true);
    assert.equal(arch.result.focusInvalid.ok, false);
    assert.equal(arch.result.blockedBeta.ok, false);
    assert.equal(arch.result.archivedAlpha.ok, true);
    assert.equal(arch.result.selectedAfter, 'beta');
    assert.deepEqual(arch.result.remainingProjects, ['beta']);
    assert.deepEqual(arch.result.remainingAsks, ['ask-b']);
    assert.deepEqual(arch.result.remainingResearches, ['res-b']);
    assert.deepEqual(arch.result.remainingMegaTasks, ['MT-b']);
    assert.equal(arch.result.archiveCount, 1);
    assert.equal(arch.result.archiveProjectId, 'alpha');
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

// Phase 5 bridge recordings use only an in-memory attempt ledger and explicit process doubles.
// They intentionally exercise the unchanged JavaScript lifecycle without an orchestrator host.
function executionFixture(root, extra = {}) {
  const store = extra.store || { usage: {}, execution: { megaTasks: [], subTasks: [], results: [], checkpoints: [], timeline: [], decisions: [] } };
  store.usage ||= {};
  const closed = [], launches = [], persisted = [];
  const model = { id: 'worker', label: 'Fixture worker', provider: 'fixture', cli: 'fixture-cli', enabled: true, available: true, roles: { worker: true } };
  const api = sandbox(root).load('execution.cjs');
  const bridge = api.createExecutionBridge({
    store, dir: root, models: () => [model], now: () => '2023-11-14T22:13:20.000Z', leadId: () => 'lead-exact',
    attempts: {
      claim: value => ({ ok: true, attempt: { id: 'attempt-' + (closed.length + launches.length + 1) }, request: plain(value) }),
      attachResult: value => { closed.push({ attached: plain(value) }); return { ok: true }; },
      closeAttempt: value => { closed.push(plain(value)); return { ok: true }; }
    },
    rowFor: () => extra.row || (extra.row = { dispatch: [] }), inspect: () => ({ git: false, dirty: [], names: [], fileCount: 0 }),
    spawnWorker: info => { launches.push(plain(info)); return { pid: 77 }; }, isProcessAlive: id => extra.live?.has(id) || false,
    killWorker: extra.killWorker || (() => true), persist: () => persisted.push({ at: 'persist' }), sessionId: 'session-fixed'
  });
  return { api, bridge, store, closed, launches, persisted, model };
}
scenario('execution', 'helpers-and-no-verdict-matrix', { categories: ['summary', 'no-verdict', 'repository'] }, ({ root }) => {
  const { api } = executionFixture(root);
  const before = 'a'.repeat(40), after = 'b'.repeat(40);
  return {
    summaries: [api.splitSummary([{ id: 'a', title: 'Build', assignedModelId: 'worker' }], id => id), api.splitSummary([{ id: 'a', title: 'Build', assignedModelId: 'worker' }, { id: 'b', title: 'Verify', assignedModelId: 'worker', dependencies: ['a'] }], id => id, 'Fixture')],
    plans: [0, 1, 2].map(api.noVerdictPlan),
    delivered: [
      api.deliveredWithoutVerdict({ folder: root, before, after, exitCode: 0, scope: ['src'], names: ['src/a.js'] }),
      api.deliveredWithoutVerdict({ folder: root, before, after, exitCode: 0, scope: ['src'], names: ['outside.js'] }),
      api.deliveredWithoutVerdict({ folder: root, before, after, exitCode: 1, names: ['src/a.js'] })
    ],
    repo: api.repoState(root, { git: true, commitId: 'head-fixed', dirty: ['?? missing.txt'] })
  };
});
scenario('execution', 'proposal-approval-and-recovery', { category: 'approval-and-persistence' }, ({ root }) => {
  const box = executionFixture(root);
  const item = { project: { id: 'project', folder: root }, revisions: [{ id: 4, direction: 'Fixture goal' }] };
  const job = { id: 'job', revision_id: 4, title: 'Fixture work', detail: 'Exercise proposal', timing: 'deadline', deadlineAt: '2023-11-15T00:00:00.000Z' };
  const mega = box.bridge.propose(item, job, [{ title: 'Build fixture', modelId: 'worker', scope: ['src/a.js'] }, { title: 'Verify fixture', modelId: 'worker', dependsOn: [0], type: 'AUDIT' }]);
  const approved = box.bridge.approve('project', mega.id);
  // A repeated proposal is recovery/idempotence, not a second task.
  const recovered = box.bridge.propose(item, job, []);
  delete mega.approvedBy;
  delete approved.approvedBy;
  if (box.store?.execution?.megaTasks) {
    for (const m of box.store.execution.megaTasks) delete m.approvedBy;
  }
  return { mega, approved, recoveredSame: recovered.id === mega.id, persisted: box.persisted.length, store: box.store };
});
for (const mode of ['addition-kill-failure-resets-verify', 'paused-addition-resumes', 'first-answer-after-start', 'delegation-replaces-after-start']) {
  scenario('execution', mode, { safetyDiscrepancy: mode }, ({ root }) => {
    const mega = { id: 'MT-fixed', projectId: 'project', taskId: 'job', title: 'Fixture work', goal: 'Goal', status: mode === 'paused-addition-resumes' ? 'PAUSED' : 'RUNNING', approvedAt: 'yes', authorizedSessionId: 'session-fixed', pauseRequested: mode === 'paused-addition-resumes', jobFingerprint: '7c635f0996c3e96cba77d8dbe78c05153906baf54e5d3033fd3abc70bd4752db', repoBefore: { repoHead: null, dirtyFiles: [], comparable: true, gitReadable: false, fingerprint: null, disposition: 'NOT APPLICABLE' } };
    const verify = { id: 'MT-fixed-ST-02', megaTaskId: mega.id, title: 'Verify the work independently', status: mode === 'addition-kill-failure-resets-verify' ? 'RUNNING' : 'PENDING', dependencies: ['MT-fixed-ST-01'], assignedModelId: 'worker', proposedProvider: 'fixture', proposedCli: 'fixture-cli', attemptIds: mode === 'addition-kill-failure-resets-verify' ? ['attempt-fixed'] : [], resultId: null };
    const build = { id: 'MT-fixed-ST-01', megaTaskId: mega.id, title: 'Build fixture', type: 'IMPLEMENTATION', status: mode === 'paused-addition-resumes' ? 'PENDING' : 'COMPLETE', dependencies: [], assignedModelId: 'worker', proposedProvider: 'fixture', proposedCli: 'fixture-cli', attemptIds: [], resultId: null };
    const decision = { id: 'D-fixed', projectId: 'project', megaTaskId: mega.id, answer: mode === 'delegation-replaces-after-start' ? 'old answer' : '', suggestion: 'suggested answer' };
    const row = { dispatch: mode === 'addition-kill-failure-resets-verify' ? [{ subTaskId: verify.id, pid: 44, alive: true, observedAlive: true, attemptId: 'attempt-fixed' }] : [] };
    const store = { execution: { megaTasks: [mega], subTasks: [build, verify], results: [], checkpoints: mode === 'paused-addition-resumes' ? [{ id: 'CP-fixed', megaTaskId: mega.id }] : [], timeline: [], decisions: [decision] } };
    const box = executionFixture(root, { store, row, live: mode === 'addition-kill-failure-resets-verify' ? new Set([44, 77]) : mode === 'paused-addition-resumes' ? new Set([77]) : new Set(), killWorker: mode === 'addition-kill-failure-resets-verify' ? () => { throw new Error('Synthetic kill failure'); } : () => true });
    if (mode === 'first-answer-after-start') return { first: box.bridge.answerDecision('project', decision.id, 'owner answer'), decision, persisted: box.persisted.length };
    if (mode === 'delegation-replaces-after-start') return { delegated: box.bridge.delegateDecision('project', decision.id, 'Lead fixture'), decision, persisted: box.persisted.length };
    const item = { project: { id: 'project', folder: null }, revisions: [{ id: 1, direction: 'Goal' }] };
    const appended = box.bridge.appendAddition(item, { id: 'job', detail: 'Goal' }, mega.id, { addition: 'Add `src/new.js`.' });
    return { appended, closed: box.closed, launches: box.launches, persisted: box.persisted.length, row, store };
  });
}
for (const mode of ['no-crew', 'no-linked-folder', 'cyclic-dependency']) {
  scenario('execution', 'proposal-guard-' + mode, { category: 'approval-and-dispatch', mode }, ({ root }) => {
    const box = executionFixture(root);
    const item = { project: { id: 'project', folder: mode === 'no-linked-folder' ? null : root }, revisions: [{ id: 1, direction: 'Goal' }] };
    const job = { id: 'job', title: 'Fixture work', detail: 'Goal' };
    const parts = mode === 'no-crew' ? [] : mode === 'cyclic-dependency'
      ? [{ title: 'Build', modelId: 'worker', dependsOn: [0] }]
      : [{ title: 'Build', modelId: 'worker' }];
    try { return { proposed: box.bridge.propose(item, job, parts), store: box.store, launches: box.launches }; }
    catch (error) { return { error: error.message, store: box.store, launches: box.launches }; }
  });
}
scenario('execution', 'dead-worker-retries-once', { category: 'result-refusal-and-recovery', tries: ['retry-same', 'switch', 'stop'] }, ({ root }) => {
  const mega = { id: 'MT-retry', projectId: 'project', taskId: 'job', title: 'Retry fixture', status: 'RUNNING', approvedAt: 'yes', authorizedSessionId: 'session-fixed', repoBefore: { repoHead: null } };
  const sub = { id: 'MT-retry-ST-01', megaTaskId: mega.id, title: 'Build fixture', type: 'IMPLEMENTATION', status: 'RUNNING', assignedModelId: 'worker', proposedProvider: 'fixture', proposedCli: 'fixture-cli', provider: 'fixture', cli: 'fixture-cli', attemptIds: ['attempt-dead'], resultId: null, dependencies: [] };
  const row = { dispatch: [{ subTaskId: sub.id, attemptId: 'attempt-dead', pid: 66, alive: true, folder: null, projectFolder: null, logPath: path.join(root, 'dead.log'), exitPath: path.join(root, 'dead.exit.json'), structuredResultPath: path.join(root, 'dead.json') }] };
  const store = { usage: {}, execution: { megaTasks: [mega], subTasks: [sub], results: [], checkpoints: [], timeline: [], decisions: [] } };
  const box = executionFixture(root, { store, row, live: new Set() });
  const item = { project: { id: 'project', folder: null }, revisions: [{ id: 1, direction: 'Goal' }] };
  const result = box.bridge.advance(item, { id: 'job', detail: 'Goal' }, [], false);
  return { result, closed: box.closed, persisted: box.persisted.length, store, row };
});
scenario('execution', 'stop-kill-failure-preserves-process', { category: 'stop-and-recovery' }, ({ root }) => {
  const mega = { id: 'MT-stop', projectId: 'project', taskId: 'job', title: 'Stop fixture', status: 'RUNNING', approvedAt: 'yes', authorizedSessionId: 'session-fixed', repoBefore: { repoHead: null } };
  const sub = { id: 'MT-stop-ST-01', megaTaskId: mega.id, title: 'Build fixture', status: 'RUNNING', assignedModelId: 'worker', proposedProvider: 'fixture', proposedCli: 'fixture-cli', provider: 'fixture', cli: 'fixture-cli', attemptIds: ['attempt-live'], resultId: null, dependencies: [] };
  const row = { dispatch: [{ subTaskId: sub.id, attemptId: 'attempt-live', pid: 88, alive: true, structuredResultPath: path.join(root, 'live.json') }] };
  const store = { usage: {}, execution: { megaTasks: [mega], subTasks: [sub], results: [], checkpoints: [], timeline: [], decisions: [] } };
  const box = executionFixture(root, { store, row, live: new Set([88]), killWorker: () => false });
  try { box.bridge.stop({ project: { id: 'project', folder: null }, revisions: [{ id: 1 }] }, { id: 'job' }, mega.id); }
  catch (error) { return { error: error.message, row, store, persisted: box.persisted.length }; }
  throw new Error('Expected synthetic kill failure');
});
function homeFolder(root) {
  const folder = path.join(root, 'project é');
  fs.mkdirSync(path.join(folder, '.git', 'objects'), { recursive: true });
  fs.writeFileSync(path.join(folder, '.git', 'HEAD'), 'ref: refs/heads/main\n');
  fs.writeFileSync(path.join(folder, '.git', 'config'), '[core]\nrepositoryformatversion = 0\n');
  return folder;
}
function gitReplies(exchange, folder, mode = 'success') {
  return { execFile(command, args, options, callback) {
    assert.equal(command, 'git');
    const gitArgs = args.slice(3), joined = gitArgs.join(' ');
    let stdout = '', error = null;
    if (mode === 'failure') error = Object.assign(new Error('Synthetic Git failure'), { code: 1 });
    else if (joined === 'rev-parse --show-toplevel') stdout = folder + '\n';
    else if (joined === 'rev-parse --verify HEAD') stdout = 'a'.repeat(40) + '\n';
    else if (joined.startsWith('status ') && gitArgs.includes('-z')) stdout = ' M src/é.js\0';
    else if (joined.startsWith('status ')) stdout = ' M src/é.js\n';
    else if (joined.startsWith('diff ')) stdout = 'diff --git a/src/é.js b/src/é.js\n';
    else if (joined.startsWith('ls-files ')) stdout = '';
    exchange({ kind: 'git', command, args: plain(args), cwd: null, timeout: options.timeout, maxBuffer: options.maxBuffer }, { error: error ? { code: error.code } : null, stdout });
    queueMicrotask(() => callback(error, stdout, error ? 'Synthetic Git failure' : ''));
  } };
}
scenario('project-home', 'local-git-success-and-failure', { categories: ['bounded-git', 'failure'] }, async ({ root, exchange }) => {
  const folder = homeFolder(root);
  const success = await sandbox(root, { builtins: { child_process: gitReplies(exchange, folder) } }).load('project-home.cjs').localGit(folder, { planFile: path.join(folder, '.planwharf', 'plan.md') });
  const failure = await sandbox(root, { builtins: { child_process: gitReplies(exchange, folder, 'failure') } }).load('project-home.cjs').localGit(folder);
  return { success, failure };
});
scenario('project-home', 'durable-megatask-outranks-row', { categories: ['home', 'durable-state', 'relink-priority'] }, async ({ root, exchange }) => {
  const folder = homeFolder(root);
  const api = sandbox(root, { builtins: { child_process: gitReplies(exchange, folder) } }).load('project-home.cjs');
  const project = { project: { id: 'project', folder }, revisions: [{ id: 3, title: 'Fixture plan', direction: 'Ship fixture', state: 'Recorded progress', open: ['Need review'], closed: [] }], home: { notes: [{ id: 'N-1', text: 'Keep exact identity.' }] } };
  const snapshot = { projects: [{ id: 'project', megaTaskId: 'MT-ready', status: 'watching', canResume: true, jobTitle: 'Stale row title', dispatch: [] }], researches: [], execution: {
    megaTasks: [{ id: 'MT-running', projectId: 'other', status: 'RUNNING' }, { id: 'MT-ready', projectId: 'project', title: 'Durable proposal', status: 'READY_FOR_APPROVAL', blockers: [] }],
    subTasks: [{ id: 'ST-ready', megaTaskId: 'MT-ready', title: 'Build fixture', status: 'PENDING', assignedModelId: 'worker', provider: 'fixture', cli: 'fixture-cli', attemptIds: [] }], results: [], checkpoints: [], timeline: [{ id: 'EV-fixed', projectId: 'project', type: 'MEGATASK_PROPOSED', summary: 'Durable proposal prepared.' }]
  } };
  const home = await api.projectHome(project, snapshot, null, { planFile: path.join(folder, '.planwharf', 'plan.md') });
  const relink = await api.projectHome({ ...project, project: { id: 'missing', folder: null } }, { projects: [{ id: 'missing', status: 'checkpointed', canResume: true }], researches: [], execution: {} }, null);
  return { home, relink: { next: relink.next, action: relink.action, local: relink.local } };
});

function orchestratorSandbox(root) {
  return sandbox(root, {
    builtins: {
      async_hooks: require('node:async_hooks'),
      events: require('node:events'),
      '../plan.js': require('../../../plan.js'),
      '../quota.js': require('../../../quota.js')
    }
  });
}

function fakeAttempts() {
  return {
    getActiveAttempt: () => null,
    claim: () => ({ ok: false, reason: 'stub' }),
    attachResult: () => ({ ok: true }),
    closeAttempt: () => ({ ok: true }),
    getResult: () => null
  };
}

function teamPlan(projectIds) {
  return {
    schema: 2,
    projects: projectIds.map(id => ({ project: { id, name: id, folder: null }, revisions: [{ id: 1, direction: 'Goal' }], tasks: [] }))
  };
}

scenario('orchestrator-team-models', 'project-team-role-isolation-and-account-scope', { category: 'project-teams' }, async ({ root }) => {
  const api = orchestratorSandbox(root).load('orchestrator.js');
  const providers = [
    { id: 'codex-main', adapter: 'codex', quotaPoolId: 'chatgpt', enabled: true, account: { key: 'acct-alpha', type: 'normal' } },
    { id: 'claude-main', adapter: 'claude', quotaPoolId: 'claude', enabled: true, account: { key: 'acct-beta', type: 'business' } }
  ];
  const models = [
    { id: 'astra', label: 'Astra', provider: 'codex-main', providerId: 'codex-main', adapter: 'codex', cli: 'gpt-6-astra', plan: 'chatgpt', quotaPoolId: 'chatgpt', kind: 'premium', enabled: true, available: true, limit: 200, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
    { id: 'opus', label: 'Opus', provider: 'claude-main', providerId: 'claude-main', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'premium', enabled: true, available: true, limit: 200, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } }
  ];
  const plan = teamPlan(['alpha', 'beta']);
  const orchestra = api.createOrchestrator({
    models,
    providers,
    readPlan: async () => plan,
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  await orchestra.setModelRoles('astra', { worker: true, orchestrator: true });
  await orchestra.setModelRoles('opus', { worker: true, orchestrator: true });
  await orchestra.setOrchestrator('astra');
  await orchestra.setModelRoles('opus', { worker: false, verifier: true }, 'alpha');
  await orchestra.setOrchestrator('opus', 'alpha');
  await orchestra.setModelRoles('opus', { worker: true, verifier: false, orchestrator: false }, 'beta');
  await orchestra.setModelRoles('astra', { worker: false, reviewer: true }, 'beta');
  const viewFor = async projectId => {
    const snap = await orchestra.withTeam(projectId, () => orchestra.snapshot());
    return { selectedOrchestratorId: snap.selectedOrchestratorId, orchestratorId: snap.orchestratorId, models: snap.models };
  };
  const alpha = await viewFor('alpha');
  const beta = await viewFor('beta');
  const shared = await viewFor(undefined);
  providers[0].account = { key: 'acct-second', type: 'normal' };
  const switchedAccount = (await orchestra.snapshot()).models;
  return { alpha, beta, shared, switchedAccount };
});

scenario('orchestrator-team-models', 'lead-automatic-handover-on-quota-depletion', { category: 'lead-handover' }, async ({ root, exchange }) => {
  const api = orchestratorSandbox(root).load('orchestrator.js');
  const providers = [
    { id: 'claude', adapter: 'claude', quotaPoolId: 'claude', enabled: true },
    { id: 'codex', adapter: 'codex', quotaPoolId: 'chatgpt', enabled: true }
  ];
  const models = [
    { id: 'opus', label: 'Opus 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
    { id: 'astra', label: 'Astra', provider: 'codex', providerId: 'codex', adapter: 'codex', cli: 'gpt-6-astra', plan: 'chatgpt', quotaPoolId: 'chatgpt', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
    { id: 'sonnet', label: 'Sonnet 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-sonnet-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'default', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } }
  ];
  const plan = teamPlan(['alpha']);
  let call = 0;
  const orchestra = api.createOrchestrator({
    models,
    providers,
    readPlan: async () => plan,
    attempts: fakeAttempts(),
    plansTtl: 0,
    fetchPlans: async () => {
      call++;
      const depleted = call >= 2;
      const reply = [
        { id: 'claude', readable: true, leftPct: depleted ? 12 : 80, windows: [{ name: '5-hour', leftPct: depleted ? 12 : 80 }, { name: 'weekly', leftPct: 85 }] },
        { id: 'chatgpt', readable: true, leftPct: 75, windows: [{ name: '5-hour', leftPct: 75 }, { name: 'weekly', leftPct: 85 }] }
      ];
      return exchange({ kind: 'fetchPlans', call }, reply);
    },
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  await orchestra.setOrchestrator('opus');
  const beforeSnap = await orchestra.snapshot();
  const afterSnap = await orchestra.tick();
  const project = afterSnap.projects.find(p => p.id === 'alpha');
  return {
    before: {
      orchestratorId: beforeSnap.orchestratorId,
      selectedOrchestratorId: beforeSnap.selectedOrchestratorId,
      opusLowQuota: beforeSnap.models.find(m => m.id === 'opus').lowQuota
    },
    after: {
      orchestratorId: afterSnap.orchestratorId,
      selectedOrchestratorId: afterSnap.selectedOrchestratorId,
      orchestratorLabel: afterSnap.orchestratorLabel,
      astraRoles: afterSnap.models.find(m => m.id === 'astra').roles,
      log: project.log
    }
  };
});

scenario('orchestrator-team-models', 'twenty-four-hour-credit-lockout', { category: 'credit-lockout' }, async ({ root }) => {
  const api = orchestratorSandbox(root).load('orchestrator.js');
  const providers = [
    { id: 'claude', adapter: 'claude', quotaPoolId: 'claude', enabled: true },
    { id: 'codex', adapter: 'codex', quotaPoolId: 'chatgpt', enabled: true }
  ];
  const models = [
    { id: 'opus', label: 'Opus 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
    { id: 'astra', label: 'Astra', provider: 'codex', providerId: 'codex', adapter: 'codex', cli: 'gpt-6-astra', plan: 'chatgpt', quotaPoolId: 'chatgpt', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } }
  ];
  fs.writeFileSync(path.join(root, 'orchestrator.json'), JSON.stringify({
    orchestratorId: 'astra',
    creditsNeeded: { opus: { at: '2023-11-14T22:13:20.000Z' } }
  }, null, 2) + '\n');
  let currentNow = '2023-11-14T22:13:20.000Z';
  const plan = teamPlan(['alpha']);
  const orchestra = api.createOrchestrator({
    dir: root,
    models,
    providers,
    readPlan: async () => plan,
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    now: () => currentNow,
    sessionId: 'session-fixed'
  });
  const timestamps = [
    '2023-11-14T22:13:20.000Z',
    '2023-11-15T10:13:20.000Z',
    '2023-11-15T22:13:19.000Z',
    '2023-11-15T22:13:20.000Z'
  ];
  const evaluations = [];
  for (const at of timestamps) {
    currentNow = at;
    const snap = await orchestra.snapshot();
    const picked = api.pickWorker(snap.models, { job: { title: 'Build feature', detail: 'Implement' } });
    evaluations.push({ at, opus: snap.models.find(m => m.id === 'opus'), pickedId: picked ? picked.id : null });
  }
  return { evaluations };
});

scenario('orchestrator-team-models', 'saving-mode-candidate-filtering', { category: 'saving-mode' }, async ({ root }) => {
  const api = orchestratorSandbox(root).load('orchestrator.js');
  const saving = orchestratorSandbox(root).load('saving-mode.cjs');
  const providers = [{ id: 'claude', adapter: 'claude', quotaPoolId: 'claude', enabled: true }];
  const models = [
    { id: 'opus', label: 'Opus 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
    { id: 'sonnet', label: 'Sonnet 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-sonnet-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'default', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
    { id: 'haiku', label: 'Haiku 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-haiku-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'local', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } }
  ];
  const plan = teamPlan(['alpha']);
  const orchestra = api.createOrchestrator({
    models,
    providers,
    readPlan: async () => plan,
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  const job = { title: 'Build feature', detail: 'Implement saving mode filtering' };
  const evaluate = async mode => {
    const snap = await orchestra.withTeam('alpha', () => orchestra.snapshot());
    const allowed = Object.fromEntries(snap.models.map(m => [m.id, saving.allows(mode, m, m.leadTier)]));
    const picked = api.pickWorker(snap.models, { job, savingMode: mode });
    const crew = api.pickWorkers(snap.models, { job, savingMode: mode });
    return { allowed, picked, crewIds: crew.map(w => w.id) };
  };
  await orchestra.setSavingMode('alpha', 'normal');
  const normal = await evaluate('normal');
  await orchestra.setSavingMode('alpha', 'safe');
  const safe = await evaluate('safe');
  await orchestra.setSavingMode('alpha', 'ultra-safe');
  const ultraSafe = await evaluate('ultra-safe');
  await orchestra.setSavingMode('alpha', 'safe');
  await orchestra.setModelWeight('sonnet', 'premium');
  const teamWeightOverride = await evaluate('safe');
  return { normal, safe, ultraSafe, teamWeightOverride, modelsAfterOverride: (await orchestra.snapshot()).models };
});

function jobBuildingSandbox(root, overrides = {}) {
  const { builtins: extraBuiltins, ...rest } = overrides;
  return sandbox(root, {
    builtins: {
      async_hooks: require('node:async_hooks'),
      events: require('node:events'),
      '../plan.js': require('../../../plan.js'),
      '../quota.js': require('../../../quota.js'),
      ...extraBuiltins
    },
    transform: (file, content) => {
      if (path.basename(file) === 'orchestrator.js') {
        return content.replace(
          'return { stopPullRequestFix,',
          'return { bridge, planByLead, assignByLead, openWorkPlan, decide, stopPullRequestFix,'
        );
      }
      return content;
    },
    ...rest
  });
}

function jobBuildingModels() {
  const providers = [
    { id: 'codex', adapter: 'codex', quotaPoolId: 'chatgpt', enabled: true },
    { id: 'claude', adapter: 'claude', quotaPoolId: 'claude', enabled: true }
  ];
  const models = [
    { id: 'astra', label: 'Astra', provider: 'codex', providerId: 'codex', adapter: 'codex', cli: 'gpt-6-astra', plan: 'chatgpt', quotaPoolId: 'chatgpt', kind: 'default', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true, canVerify: true } },
    { id: 'opus', label: 'Opus 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true, canVerify: true } },
    { id: 'haiku', label: 'Haiku 5.5', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-haiku-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'local', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true, canVerify: true } }
  ];
  return { providers, models };
}

function cleanOrchFiles(root) {
  for (const name of fs.readdirSync(root)) {
    if (name.startsWith('orchestrator.json') || name.startsWith('invocations.jsonl')) {
      fs.rmSync(path.join(root, name), { force: true, recursive: true });
    }
  }
}

scenario('orchestrator-job-building', 'plan-by-lead', { category: 'lead-split' }, async ({ root, exchange }) => {
  const { providers, models } = jobBuildingModels();
  const projectFolder = path.join(root, 'project');
  fs.mkdirSync(projectFolder, { recursive: true });
  fs.writeFileSync(path.join(projectFolder, 'package.json'), '{"name":"project"}\n');
  const item = { project: { id: 'alpha', name: 'Alpha', folder: projectFolder }, revisions: [{ id: 1, direction: 'Implement service and tests' }] };
  const job = { id: 'job-1', title: 'Implement service and tests', detail: 'Build service' };
  const askModel = async ({ question, model, projectId, folder }) => {
    const answer = JSON.stringify({
      parts: [
        { title: 'Build endpoints', detail: 'Write service endpoints', files: ['src/service.js'], dependsOn: [], size: 'light', model: 'opus' },
        { title: 'Build tests', detail: 'Write unit tests', files: ['tests/service.test.js'], dependsOn: [0], size: 'normal', model: 'opus' }
      ],
      verify: 'haiku',
      why: 'Two-part split'
    });
    exchange({ kind: 'askModel', modelId: model.id, question }, { answer });
    return { answer };
  };
  const orch = jobBuildingSandbox(root).load('orchestrator.js').createOrchestrator({
    dir: root,
    models,
    providers,
    readPlan: async () => ({ schema: 2, projects: [item] }),
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    askModel,
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  await orch.setOrchestrator('astra');
  const mega = orch.bridge.propose(item, job, [{ title: 'Implement feature', modelId: 'astra', type: 'IMPLEMENTATION' }]);
  const ok = await orch.planByLead('alpha', item, mega.id);
  const subTasks = orch.bridge.view('alpha').subTasks;
  cleanOrchFiles(root);
  return { ok, replanned: ok, subTasks: subTasks.map(s => ({ id: s.id, title: s.title, assignedModelId: s.assignedModelId, dependencies: s.dependencies })) };
});

scenario('orchestrator-job-building', 'assign-by-lead', { category: 'lead-assignment' }, async ({ root, exchange }) => {
  const { providers, models } = jobBuildingModels();
  const projectFolder = path.join(root, 'project');
  fs.mkdirSync(projectFolder, { recursive: true });
  fs.writeFileSync(path.join(projectFolder, 'package.json'), '{"name":"project"}\n');
  const item = { project: { id: 'alpha', name: 'Alpha', folder: projectFolder }, revisions: [{ id: 1, direction: 'Build two components' }] };
  const job = { id: 'job-2', title: 'Build two components', detail: 'Dual component implementation' };
  const askModel = async ({ question, model, projectId, folder }) => {
    const answer = JSON.stringify({
      assign: ['opus', 'astra'],
      why: 'Optimized crew matching'
    });
    exchange({ kind: 'askModel', modelId: model.id, question }, { answer });
    return { answer };
  };
  const orch = jobBuildingSandbox(root).load('orchestrator.js').createOrchestrator({
    dir: root,
    models,
    providers,
    readPlan: async () => ({ schema: 2, projects: [item] }),
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    askModel,
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  await orch.setOrchestrator('astra');
  const mega = orch.bridge.propose(item, job, [
    { title: 'Build service', modelId: 'astra', type: 'IMPLEMENTATION' },
    { title: 'Build tests', modelId: 'opus', type: 'IMPLEMENTATION' }
  ]);
  await orch.assignByLead('alpha', item, mega.id);
  const subTasks = orch.bridge.view('alpha').subTasks;
  cleanOrchFiles(root);
  return { ok: true, assigned: true, subTasks: subTasks.map(s => ({ id: s.id, assignedModelId: s.assignedModelId })) };
});

scenario('orchestrator-job-building', 'start-auto-task', { category: 'auto-start' }, async ({ root }) => {
  const { providers, models } = jobBuildingModels();
  const projectFolder = path.join(root, 'project');
  fs.mkdirSync(path.join(projectFolder, '.git'), { recursive: true });
  fs.writeFileSync(path.join(projectFolder, '.git', 'HEAD'), 'ref: refs/heads/main\n');
  fs.writeFileSync(path.join(projectFolder, '.git', 'config'), '[core]\nrepositoryformatversion = 0\n');
  fs.writeFileSync(path.join(projectFolder, 'package.json'), '{"name":"project"}\n');
  const item = {
    project: { id: 'alpha', name: 'Alpha', folder: projectFolder },
    revisions: [{ id: 'rev-1', direction: 'Build web service and automated tests' }],
    tasks: []
  };
  const orch = jobBuildingSandbox(root, {
    builtins: {
      child_process: {
        spawnSync(cmd, args) {
          if (args.includes('--is-inside-work-tree')) return { status: 1, stdout: '' };
          return { status: 0, stdout: '' };
        }
      }
    }
  }).load('orchestrator.js').createOrchestrator({
    dir: root,
    models,
    providers,
    readPlan: async () => ({ schema: 2, projects: [item] }),
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  await orch.setGrillPropose(true);
  const grill = await orch.startAutoTask('alpha', { timing: 'discretion' });
  const plan = await orch.startAutoTask('alpha', { timing: 'deadline', deadlineAt: '2023-11-15T00:00:00.000Z', grillContext: 'Autonomous mode: no interview' });
  cleanOrchFiles(root);
  return { grill, plan };
});

scenario('orchestrator-job-building', 'open-work-plan', { category: 'work-plan' }, async ({ root, exchange }) => {
  const { providers, models } = jobBuildingModels();
  const projectFolder = path.join(root, 'project');
  fs.mkdirSync(path.join(projectFolder, '.git'), { recursive: true });
  fs.writeFileSync(path.join(projectFolder, '.git', 'HEAD'), 'ref: refs/heads/main\n');
  fs.writeFileSync(path.join(projectFolder, '.git', 'config'), '[core]\nrepositoryformatversion = 0\n');
  const worktreesDir = path.join(root, 'worktrees', 'alpha', 'MT-orphan');
  fs.mkdirSync(worktreesDir, { recursive: true });
  const box = jobBuildingSandbox(root, {
    builtins: {
      child_process: {
        spawnSync(cmd, args) {
          exchange({ kind: 'git', command: cmd, args }, { status: 0 });
          if (args.includes('--is-inside-work-tree')) return { status: 1, stdout: '' };
          return { status: 0, stdout: '' };
        }
      }
    }
  });
  const orch = box.load('orchestrator.js').createOrchestrator({
    dir: root,
    models,
    providers,
    readPlan: async () => ({ schema: 2, projects: [] }),
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  const item = { project: { id: 'alpha', folder: projectFolder }, revisions: [{ id: 1, direction: 'Implement service endpoints' }] };
  const res = orch.openWorkPlan('alpha', item, [
    { role: 'Build', model: 'Astra', title: 'Build endpoints' },
    { role: 'Verify', model: 'Haiku', title: 'Verify service' }
  ]);
  const planFile = path.join(projectFolder, '.planwharf', 'plan.md');
  const out = {
    ok: res.ok,
    planExists: fs.existsSync(planFile),
    chat: res.chat || null,
    orphanSwept: !fs.existsSync(worktreesDir)
  };
  cleanOrchFiles(root);
  return out;
});

scenario('orchestrator-job-building', 'decide', { category: 'decide-lifecycle' }, async ({ root }) => {
  const { providers, models } = jobBuildingModels();
  const box = jobBuildingSandbox(root);
  const api = box.load('orchestrator.js');
  const resultFile = path.join(root, 'result.txt');
  fs.writeFileSync(resultFile, 'STATUS: COMPLETE\nAll tests passed.\n');
  const job1 = { id: 'job-1', title: 'Task 1', detail: 'Detail 1', status: 'running' };
  const item = {
    project: { id: 'alpha', name: 'Alpha', folder: root },
    revisions: [{ id: 1, direction: 'Goal' }],
    tasks: [job1]
  };
  fs.writeFileSync(path.join(root, 'orchestrator.json'), JSON.stringify({
    projects: {
      alpha: {
        jobId: 'job-1',
        jobFingerprint: api.jobFingerprint(job1),
        dispatch: [{ resultPath: resultFile, stoppedByUser: false }]
      }
    }
  }, null, 2) + '\n');
  const orch = api.createOrchestrator({
    dir: root,
    models,
    providers,
    readPlan: async () => ({ schema: 2, projects: [item] }),
    attempts: fakeAttempts(),
    fetchPlans: async () => [],
    now: () => '2023-11-14T22:13:20.000Z',
    sessionId: 'session-fixed'
  });
  const row1 = orch.decide(item);
  const completion = { status: row1.status, doing: row1.doing, waitingFor: row1.waitingFor };

  // Mutation reset
  row1.approved = true;
  row1.holdApproval = true;
  const job2 = { id: 'job-2', title: 'Task 2', detail: 'Detail 2', status: 'running' };
  item.tasks = [job2];
  const row2 = orch.decide(item);
  const mutation = { approved: row2.approved, holdApproval: row2.holdApproval, jobId: row2.jobId };

  // Advance (executionVersion: 2)
  const jobV2 = { id: 'job-v2', executionVersion: 2, title: 'V2 Work', detail: 'Advance work', status: 'running' };
  item.tasks = [jobV2];
  orch.bridge.propose(item, jobV2, [{ title: 'Advance work', modelId: 'astra', type: 'IMPLEMENTATION' }]);
  const row3 = orch.decide(item);
  const advance = { status: row3.status, jobId: row3.jobId };

  cleanOrchFiles(root);
  return { completion, mutation, advance };
});

function leadQuestionsSandbox(root) {
  return sandbox(root, {
    builtins: {
      async_hooks: require('node:async_hooks'),
      events: require('node:events'),
      '../plan.js': require('../../../plan.js'),
      '../quota.js': require('../../../quota.js')
    },
    transform: (file, content) => path.basename(file) === 'orchestrator.js'
      ? content.replace('return { stopPullRequestFix,', 'return { askLeadDecision, stopPullRequestFix,')
      : content
  });
}

function leadQuestionModels() {
  return {
    providers: [
      { id: 'codex', adapter: 'codex', quotaPoolId: 'chatgpt', enabled: true },
      { id: 'claude', adapter: 'claude', quotaPoolId: 'claude', enabled: true }
    ],
    models: [
      { id: 'astra', label: 'Astra', provider: 'codex', providerId: 'codex', adapter: 'codex', cli: 'gpt-6-astra', plan: 'chatgpt', quotaPoolId: 'chatgpt', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } },
      { id: 'opus', label: 'Opus', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'premium', enabled: true, available: true, limit: 100, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true } }
    ]
  };
}

function leadQuestionPlan() {
  return {
    schema: 2,
    projects: [
      { project: { id: 'alpha', name: 'Alpha', folder: null }, revisions: [{ id: 'rev-a', direction: 'Ship Alpha safely' }], tasks: [] },
      { project: { id: 'beta', name: 'Beta', folder: null }, revisions: [{ id: 'rev-b', direction: 'PRIVATE_BETA_GOAL' }], tasks: [] }
    ]
  };
}

function leadQuestionRequest(info) {
  return {
    kind: 'askModel',
    modelId: info.model.id,
    question: info.question,
    projectId: info.projectId || null,
    allowActions: Object.hasOwn(info, 'allowActions') ? info.allowActions : null,
    state: info.state
  };
}

function leadQuestionResult(result) {
  return {
    ok: result.ok,
    question: result.question || null,
    answer: result.answer || null,
    thinking: result.thinking || null,
    ms: result.ms ?? null,
    projectId: result.projectId || null,
    orchestratorId: result.orchestratorId || null,
    actionResults: result.actionResults || []
  };
}

scenario('orchestrator-lead-questions', 'streamed-envelope-project-isolation-and-unsolicited-research',
  { category: 'lead-turn', projectId: 'alpha' }, async ({ root, exchange, events }) => {
    const { providers, models } = leadQuestionModels();
    const plan = leadQuestionPlan();
    let orchestra;
    const askModel = async info => {
      info.onProgress({ thinking: 'Inspecting Alpha', answer: '', ms: 4 });
      events.push({ type: 'progress', thinking: 'Inspecting Alpha', answer: '', ms: 4 });
      info.onProgress({ thinking: 'Inspecting Alpha', answer: 'Draft answer', ms: 8 });
      events.push({ type: 'progress', thinking: 'Inspecting Alpha', answer: 'Draft answer', ms: 8 });
      const reply = exchange(leadQuestionRequest(info), {
        answer: JSON.stringify({
          answer: 'Alpha is ready for review.',
          actions: [{ type: 'dispatch_research', projectId: 'alpha', modelId: 'opus', title: 'Unasked research', detail: 'Do not run' }]
        }),
        thinking: 'Checked only Alpha.',
        ms: 12
      });
      return reply;
    };
    orchestra = leadQuestionsSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: root, models, providers, readPlan: async () => plan, attempts: fakeAttempts(),
      fetchPlans: async () => [], askModel, now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-fixed'
    });
    await orchestra.setOrchestrator('astra');
    const result = await orchestra.ask('What is the current status?', 'alpha', 'chat-fixed');
    const firstRequest = JSON.stringify(result.asks[0]);
    cleanOrchFiles(root);
    return {
      turn: leadQuestionResult(result),
      recordedAnswerContainsPrivateBeta: firstRequest.includes('PRIVATE_BETA_GOAL'),
      persistedAskCount: result.asks.length
    };
  });

scenario('orchestrator-lead-questions', 'process-failure-retry-then-alternate-provider',
  { category: 'handover', failures: 2 }, async ({ root, exchange, events }) => {
    const { providers, models } = leadQuestionModels();
    let call = 0;
    const askModel = async info => {
      call += 1;
      events.push({ type: 'invoke', call, modelId: info.model.id });
      const reply = call <= 2
        ? { answer: 'Synthetic process failure ' + call, failure: 'process', diagnostic: { code: 'PROVIDER_EXECUTION_FAILED', message: 'Synthetic process failure ' + call }, ms: call }
        : { answer: 'Recovered through Opus.', thinking: 'Alternate provider answered.', ms: 9 };
      return exchange(leadQuestionRequest(info), reply);
    };
    const orchestra = leadQuestionsSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: root, models, providers, readPlan: async () => leadQuestionPlan(), attempts: fakeAttempts(),
      fetchPlans: async () => [], askModel, now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-fixed'
    });
    await orchestra.setOrchestrator('astra');
    const result = await orchestra.ask('Explain Alpha status.', 'alpha', 'chat-fixed');
    cleanOrchFiles(root);
    return { turn: leadQuestionResult(result), calls: call };
  });

for (const failure of ['PROVIDER_USAGE_LIMIT', 'CREDITS_NEEDED']) {
  scenario('orchestrator-lead-questions', 'immediate-handover-' + failure.toLowerCase().replaceAll('_', '-'),
    { category: 'handover', failure }, async ({ root, exchange, events }) => {
      const { providers, models } = leadQuestionModels();
      let call = 0;
      const askModel = async info => {
        call += 1;
        events.push({ type: 'invoke', call, modelId: info.model.id });
        const reply = call === 1
          ? failure === 'PROVIDER_USAGE_LIMIT'
            ? { answer: 'Quota exhausted.', failure: 'provider', diagnostic: { code: 'PROVIDER_USAGE_LIMIT', message: 'Quota exhausted.' }, ms: 2 }
            : { answer: 'This model requires usage credits.', failure: 'provider', diagnostic: { code: 'PROVIDER_EXECUTION_FAILED', message: 'Credits needed.' }, ms: 2 }
          : { answer: 'Immediate handover succeeded.', thinking: 'Second provider.', ms: 5 };
        return exchange(leadQuestionRequest(info), reply);
      };
      const orchestra = leadQuestionsSandbox(root).load('orchestrator.js').createOrchestrator({
        dir: root, models, providers, readPlan: async () => leadQuestionPlan(), attempts: fakeAttempts(),
        fetchPlans: async () => [], askModel, now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-fixed'
      });
      await orchestra.setOrchestrator('astra');
      const result = await orchestra.ask('Summarize Alpha.', 'alpha', 'chat-fixed');
      cleanOrchFiles(root);
      return { turn: leadQuestionResult(result), calls: call };
    });
}

scenario('orchestrator-lead-questions', 'unreadable-autonomy-decision-strict-retry',
  { category: 'autonomy', projectId: 'alpha' }, async ({ root, exchange, events }) => {
    const { providers, models } = leadQuestionModels();
    const question = 'Decide whether Alpha is complete.';
    let call = 0;
    const askModel = async info => {
      call += 1;
      events.push({ type: 'invoke', call, modelId: info.model.id, question: info.question });
      const reply = call === 1
        ? { answer: 'I cannot format that decision.', ms: 3 }
        : { answer: '{"done":false,"next":"Run the isolated checks","assumptions":["The fixture is synthetic"],"summary":"A strict reply was accepted."}', ms: 4 };
      return exchange(leadQuestionRequest(info), reply);
    };
    const orchestra = leadQuestionsSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: root, models, providers, readPlan: async () => leadQuestionPlan(), attempts: fakeAttempts(),
      fetchPlans: async () => [], askModel,
      prepareTask: async (projectId, objective) => ({ ok: true, task: { id: 'task-fixed', projectId, objective } }),
      now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-fixed'
    });
    await orchestra.setOrchestrator('astra');
    const settings = { enabled: true, assumptions: [], approved: [], continued: [], evaluated: [] };
    const item = leadQuestionPlan().projects[0];
    await orchestra.askLeadDecision('alpha', item, settings, question);
    const snapshot = await orchestra.snapshot('alpha');
    cleanOrchFiles(root);
    return {
      calls: call,
      assumptions: settings.assumptions,
      stopReason: settings.stopReason || null,
      notes: snapshot.projects[0].log.map(entry => entry.text)
    };
  });

scenario('orchestrator-lead-questions', 'mid-turn-cancellation-preserves-only-stop-record',
  { category: 'cancellation', projectId: 'alpha' }, async ({ root, exchange, events }) => {
    const { providers, models } = leadQuestionModels();
    let orchestra;
    const askModel = async info => {
      info.onProgress({ thinking: 'Halfway', answer: 'Unsafe partial draft', ms: 7 });
      events.push({ type: 'progress', thinking: 'Halfway', answer: 'Unsafe partial draft', ms: 7 });
      const stopped = await orchestra.abortAsk('alpha');
      events.push({ type: 'abort', aborted: stopped.aborted, asks: stopped.asks.length });
      return exchange(leadQuestionRequest(info), { answer: 'This answer must not be persisted.', thinking: 'Too late', ms: 11 });
    };
    orchestra = leadQuestionsSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: root, models, providers, readPlan: async () => leadQuestionPlan(), attempts: fakeAttempts(),
      fetchPlans: async () => [], askModel, now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-fixed'
    });
    await orchestra.setOrchestrator('astra');
    const result = await orchestra.ask('Cancel this Alpha turn.', 'alpha', 'chat-fixed');
    const asks = result.asks.map(item => ({ question: item.question, answer: item.answer, thinking: item.thinking, order: item.order }));
    cleanOrchFiles(root);
    return { ok: result.ok, cancelled: result.cancelled, asks };
  });

function stateSandbox(root, overrides = {}) {
  let uuidSeq = 0;
  const { builtins: extraBuiltins, ...rest } = overrides;
  return sandbox(root, {
    builtins: {
      async_hooks: require('node:async_hooks'),
      events: require('node:events'),
      crypto: {
        createHash: require('node:crypto').createHash,
        randomUUID: () => '00000000-0000-4000-8000-' + String(++uuidSeq).padStart(12, '0')
      },
      '../plan.js': require('../../../plan.js'),
      '../quota.js': require('../../../quota.js'),
      ...extraBuiltins
    },
    ...rest
  });
}

function stateModels() {
  return {
    providers: [
      { id: 'codex', adapter: 'codex', quotaPoolId: 'chatgpt', enabled: true },
      { id: 'claude', adapter: 'claude', quotaPoolId: 'claude', enabled: true }
    ],
    models: [
      { id: 'astra', label: 'Astra', provider: 'codex', providerId: 'codex', adapter: 'codex', cli: 'gpt-6-astra', plan: 'chatgpt', quotaPoolId: 'chatgpt', kind: 'premium', enabled: true, available: true, limit: 100, cost: 1, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true, canResearch: true, canVerify: true } },
      { id: 'opus', label: 'Opus', provider: 'claude', providerId: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', quotaPoolId: 'claude', kind: 'default', enabled: true, available: true, limit: 100, cost: 1, capabilities: { canUseTools: true, canInspectFilesystem: true, canCode: true, canResearch: true, canVerify: true } }
    ]
  };
}

function stateAttempts(root) {
  let seq = 0;
  const active = new Map();
  const results = new Map();
  return {
    getActiveAttempt: ({ projectId, taskId } = {}) => {
      for (const att of active.values()) {
        if (att.projectId === projectId && (!taskId || att.taskId === taskId)) return att;
      }
      return null;
    },
    claim: ({ taskId, projectId, modelId }) => {
      const id = 'attempt-' + (++seq);
      const att = { id, taskId, projectId, modelId, startedAt: '2023-11-14T22:13:20.000Z', folder: path.join(root, 'attempts', id) };
      active.set(id, att);
      return { ok: true, attempt: att };
    },
    attachResult: ({ attemptId, resultPath }) => {
      results.set(attemptId, { attemptId, resultPath });
      return { ok: true };
    },
    closeAttempt: ({ attemptId, status } = {}) => {
      const att = active.get(attemptId);
      if (att) {
        att.status = status || 'closed';
        active.delete(attemptId);
      }
      return { ok: true };
    },
    getResult: attemptId => results.get(attemptId) || null
  };
}

function stateInspect(folder) {
  return {
    folder,
    fileCount: 2,
    names: ['README.md', 'src'],
    docs: [{ path: 'README.md' }],
    git: true,
    head: '1111111111111111111111111111111111111111',
    branch: 'main',
    lastCommit: '1111111 Fix fixture',
    commitId: '1111111111111111111111111111111111111111',
    lastSubject: 'Fix fixture',
    recent: ['1111111 Fix fixture'],
    dirty: [' M src/app.js'],
    diffStat: ' src/app.js | 2 +-',
    error: null
  };
}

scenario('orchestrator-state', 'fresh-and-legacy-load-unknown-fields-and-persist-bytes',
  { category: 'load-and-persist' }, async ({ root }) => {
    const { providers, models } = stateModels();
    const freshDir = path.join(root, 'fresh');
    const freshPlan = { schema: 2, projects: [{ project: { id: 'alpha', name: 'Alpha', folder: null }, revisions: [{ id: 1, direction: 'Fresh goal' }], tasks: [] }] };
    const freshOrch = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: freshDir, models, providers, readPlan: async () => freshPlan, attempts: fakeAttempts(),
      fetchPlans: async () => [], now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-fresh'
    });
    const freshSnap = await freshOrch.tick();
    const freshBytes = fs.readFileSync(path.join(freshDir, 'orchestrator.json'), 'utf8');

    const legacyDir = path.join(root, 'legacy');
    fs.mkdirSync(legacyDir, { recursive: true });
    const legacyInput = {
      mode: 'invalid-mode',
      grillPropose: true,
      grillKind: 'docs',
      chatModes: { alpha: 'auto', beta: 'talk' },
      savingModes: { alpha: 'bogus', beta: 'safe' },
      modelWeights: { astra: 'invalid-weight', opus: 'premium' },
      autonomy: { alpha: { enabled: true, sessionId: 'old-session' } },
      projects: {
        alpha: { status: 'idle', customRowField: 'keep-me', nestedUnknown: { depth: 2 } }
      },
      asks: [
        { id: 'ask-1', projectId: 'alpha', sessionId: 'hist-sess-1', at: '2023-11-14T20:00:00.000Z', question: 'Q?', answer: 'A.', customAskField: 42 }
      ],
      execution: { megaTasks: [], subTasks: [], results: [], checkpoints: [], timeline: [], customExecField: 'keep-exec' }
    };
    fs.writeFileSync(path.join(legacyDir, 'orchestrator.json'), JSON.stringify(legacyInput, null, 2) + '\n');
    const legacyPlan = {
      schema: 2,
      projects: [
        { project: { id: 'alpha', name: 'Alpha', folder: null }, revisions: [{ id: 1, direction: 'Legacy A' }], tasks: [] },
        { project: { id: 'beta', name: 'Beta', folder: null }, revisions: [{ id: 1, direction: 'Legacy B' }], tasks: [] }
      ]
    };
    const legacyOrch = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: legacyDir, models, providers, readPlan: async () => legacyPlan, attempts: fakeAttempts(),
      fetchPlans: async () => [], now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-legacy'
    });
    const legacySnap = await legacyOrch.snapshot();
    const legacyBytes = fs.readFileSync(path.join(legacyDir, 'orchestrator.json'), 'utf8');
    const legacyParsed = JSON.parse(legacyBytes);
    return {
      fresh: {
        mode: freshSnap.mode,
        sessionCount: freshSnap.sessions.length,
        endsWithNewline: freshBytes.endsWith('\n'),
        singleLine: freshBytes.indexOf('\n') === freshBytes.length - 1
      },
      legacy: {
        mode: legacySnap.mode,
        grillPropose: legacySnap.grillPropose,
        grillKind: legacySnap.grillKind,
        alphaChatMode: legacySnap.projects.find(p => p.id === 'alpha').chatMode,
        betaChatMode: legacySnap.projects.find(p => p.id === 'beta').chatMode,
        alphaSavingMode: legacySnap.projects.find(p => p.id === 'alpha').savingMode,
        betaSavingMode: legacySnap.projects.find(p => p.id === 'beta').savingMode,
        autonomyEnabled: legacyParsed.autonomy.alpha.enabled,
        modelWeights: legacyParsed.modelWeights,
        preservedRowField: legacyParsed.projects.alpha.customRowField,
        preservedRowNested: legacyParsed.projects.alpha.nestedUnknown,
        preservedAskField: legacyParsed.asks[0].customAskField,
        preservedExecField: legacyParsed.execution.customExecField,
        hydratedSessionIds: legacyParsed.sessions.map(s => s.id),
        endsWithNewline: legacyBytes.endsWith('\n')
      }
    };
  });

scenario('orchestrator-state', 'atomic-persist-retry-and-failure',
  { category: 'atomic-persist' }, async ({ root, exchange }) => {
    const { providers, models } = stateModels();
    const dir = path.join(root, 'state');
    let renameCalls = 0;
    let failAll = false;
    const customFs = {
      ...fs,
      renameSync(from, to) {
        renameCalls++;
        if (failAll) {
          const err = Object.assign(new Error('EPERM: persistent lock'), { code: 'EPERM' });
          exchange({ kind: 'rename', call: renameCalls, failAll: true }, { ok: false, code: 'EPERM' });
          throw err;
        }
        if (renameCalls === 1) {
          exchange({ kind: 'rename', call: renameCalls }, { ok: false, code: 'EBUSY' });
          throw Object.assign(new Error('EBUSY: busy'), { code: 'EBUSY' });
        }
        if (renameCalls === 2) {
          exchange({ kind: 'rename', call: renameCalls }, { ok: false, code: 'EPERM' });
          throw Object.assign(new Error('EPERM: locked'), { code: 'EPERM' });
        }
        exchange({ kind: 'rename', call: renameCalls }, { ok: true });
        return fs.renameSync(from, to);
      }
    };
    const orch = stateSandbox(root, { fs: customFs }).load('orchestrator.js').createOrchestrator({
      dir, models, providers, readPlan: async () => teamPlan(['alpha']), attempts: fakeAttempts(),
      fetchPlans: async () => [], now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-atomic'
    });
    const autoSnap = await orch.setMode('auto');
    const afterRetryMode = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8')).mode;

    failAll = true;
    let permError = null;
    try { await orch.setMode('manual'); }
    catch (error) { permError = error.message; }
    const afterFailMode = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8')).mode;
    const remainingTmps = fs.readdirSync(dir).filter(name => name.endsWith('.tmp'));
    return {
      retryCount: renameCalls,
      autoSnapMode: autoSnap.mode,
      afterRetryMode,
      permError,
      afterFailMode,
      remainingTmps
    };
  });

scenario('orchestrator-state', 'lifecycle-snapshots-and-execution-row-sharing',
  { category: 'lifecycle-snapshots' }, async ({ root, exchange }) => {
    const { providers, models } = stateModels();
    const dir = path.join(root, 'state');
    const projectFolder = path.join(root, 'project-alpha');
    fs.mkdirSync(projectFolder, { recursive: true });
    fs.writeFileSync(path.join(projectFolder, 'README.md'), '# Alpha\n');
    const job = { id: 'job-1', executionVersion: 2, title: 'Build Alpha', detail: 'Implement feature', status: 'running' };
    const plan = {
      schema: 2,
      projects: [{ project: { id: 'alpha', name: 'Alpha', folder: projectFolder }, revisions: [{ id: 1, direction: 'Ship Alpha' }], tasks: [job] }]
    };
    const livePids = new Set();
    const launchedParts = [];
    let pidSeq = 100;
    const orch = stateSandbox(root, {
      builtins: {
        child_process: {
          spawnSync(cmd, args) {
            const reply = args.includes('--is-inside-work-tree') ? { status: 1, stdout: '' } : { status: 0, stdout: '' };
            exchange({ kind: 'process', command: cmd, args: plain(args) }, reply);
            return reply;
          }
        }
      }
    }).load('orchestrator.js').createOrchestrator({
      dir, models, providers,
      readPlan: async () => plan,
      attempts: stateAttempts(root),
      fetchPlans: async () => [],
      inspectFolder: stateInspect,
      readRepository: async () => ({ provider: 'github', slug: 'owner/alpha', url: 'https://github.com/owner/alpha' }),
      isProcessAlive: pid => livePids.has(pid),
      spawnWorker: info => {
        const pid = ++pidSeq;
        livePids.add(pid);
        fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
        fs.writeFileSync(info.logPath, 'Running Alpha build\n');
        launchedParts.push({
          pid,
          attemptId: info.attemptId,
          subTaskId: info.subTaskId,
          megaTaskId: info.megaTaskId,
          provider: info.worker.providerId || info.worker.provider,
          cli: info.worker.cli,
          resultPath: info.resultPath,
          exitPath: info.exitPath,
          structuredResultPath: info.structuredResultPath
        });
        exchange({ kind: 'spawnWorker', projectId: info.projectId, attemptId: info.attemptId, modelId: info.worker.id }, { pid });
        return { pid, logPath: info.logPath };
      },
      now: () => '2023-11-14T22:13:20.000Z',
      sessionId: 'session-life'
    });
    const approvalSnap = await orch.tick('alpha');
    const approvalProj = approvalSnap.projects[0];

    const approveResult = await orch.approve('alpha', approvalProj.megaTaskId);
    const watchingSnap = await orch.snapshot('alpha');
    const watchingProj = watchingSnap.projects[0];
    const execViewWatching = orch.executionView('alpha');

    let completeSnap = watchingSnap;
    let completeProj = watchingProj;
    for (let round = 0; round < 3 && completeProj.status === 'watching'; round++) {
      for (const part of launchedParts) {
        if (!livePids.has(part.pid)) continue;
        if (part.resultPath) {
          fs.mkdirSync(path.dirname(part.resultPath), { recursive: true });
          fs.writeFileSync(part.resultPath, 'STATUS: COMPLETE\nEXIT: 0\n');
        }
        if (part.exitPath) {
          fs.mkdirSync(path.dirname(part.exitPath), { recursive: true });
          fs.writeFileSync(part.exitPath, JSON.stringify({
            attemptId: part.attemptId,
            exitCode: 0,
            provider: part.provider,
            model: part.cli,
            createdAt: '2023-11-14T22:13:20.000Z'
          }));
        }
        if (part.structuredResultPath) {
          fs.mkdirSync(path.dirname(part.structuredResultPath), { recursive: true });
          fs.writeFileSync(part.structuredResultPath, JSON.stringify({
            resultId: 'R-' + part.attemptId,
            attemptId: part.attemptId,
            subTaskId: part.subTaskId,
            megaTaskId: part.megaTaskId,
            status: 'COMPLETE',
            summary: 'Completed slice',
            filesChanged: [],
            tests: [{ command: 'test', status: 'PASSED', evidence: 'ok' }],
            blockers: [],
            nextAction: 'Done'
          }));
        }
        livePids.delete(part.pid);
      }
      completeSnap = await orch.tick('alpha');
      completeProj = completeSnap.projects[0];
    }
    const page = await orch.activitySessionPage('alpha', 0, 20);
    return {
      approveOk: approveResult.ok,
      approveReason: approveResult.reason || null,
      approval: { status: approvalProj.status, step: approvalProj.step, pipeline: approvalProj.pipeline, canStart: approvalProj.canStart, crew: approvalProj.crew },
      watching: {
        status: watchingProj.status, step: watchingProj.step, pipeline: watchingProj.pipeline,
        workerPid: watchingProj.workerPid, workerAlive: watchingProj.workerAlive, canStop: watchingProj.canStop,
        crew: watchingProj.crew, dispatchLen: watchingProj.dispatch.length,
        sharedMegaTaskId: Boolean(watchingProj.megaTaskId && execViewWatching.megaTasks[0]?.id === watchingProj.megaTaskId)
      },
      complete: { status: completeProj.status, step: completeProj.step, pipeline: completeProj.pipeline, workerAlive: completeProj.workerAlive },
      activityTotal: page.total,
      activitySessionsLen: page.sessions.length
    };
  });

scenario('orchestrator-state', 'checkpoint-hydration-on-startup',
  { category: 'checkpoint-hydration' }, async ({ root, exchange }) => {
    const { providers, models } = stateModels();
    const dir = path.join(root, 'state');
    const cpDir = path.join(dir, 'checkpoints');
    fs.mkdirSync(cpDir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({
      projects: { alpha: { status: 'idle', checkpoint: null } }
    }) + '\n');
    fs.writeFileSync(path.join(cpDir, 'alpha.json'), JSON.stringify({
      id: 'CP-alpha', projectId: 'alpha', jobId: 'job-a', jobTitle: 'Interrupted Alpha', reason: 'stop', at: '2023-11-14T21:00:00.000Z'
    }));
    fs.writeFileSync(path.join(cpDir, 'beta.json'), JSON.stringify({
      id: 'CP-beta', jobId: 'job-b', jobTitle: 'Interrupted Beta', reason: 'shutdown', at: '2023-11-14T21:05:00.000Z'
    }));
    const plan = teamPlan(['alpha', 'beta']);
    const orch = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir, models, providers, readPlan: async () => plan, attempts: fakeAttempts(),
      fetchPlans: async () => [],
      spawnWorker: info => { exchange({ kind: 'spawnWorker', unexpected: true, projectId: info.projectId }, { pid: 999 }); return { pid: 999 }; },
      now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-cp'
    });
    const snap = await orch.snapshot();
    const persisted = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    return {
      alpha: {
        status: snap.projects.find(p => p.id === 'alpha').status,
        step: snap.projects.find(p => p.id === 'alpha').step,
        canResume: snap.projects.find(p => p.id === 'alpha').canResume,
        workerAlive: snap.projects.find(p => p.id === 'alpha').workerAlive,
        checkpointPath: snap.projects.find(p => p.id === 'alpha').checkpoint?.path || null
      },
      beta: {
        status: snap.projects.find(p => p.id === 'beta').status,
        step: snap.projects.find(p => p.id === 'beta').step,
        canResume: snap.projects.find(p => p.id === 'beta').canResume,
        checkpointPath: snap.projects.find(p => p.id === 'beta').checkpoint?.path || null
      },
      persistedAlphaStatus: persisted.projects.alpha.status,
      persistedBetaStatus: persisted.projects.beta.status
    };
  });

scenario('orchestrator-state', 'alive-and-dead-worker-restart-reconciliation',
  { category: 'restart-reconcile' }, async ({ root, exchange }) => {
    const { providers, models } = stateModels();
    const dir = path.join(root, 'state');
    fs.mkdirSync(dir, { recursive: true });
    const aliveLog = path.join(dir, 'alive.log');
    const deadLog = path.join(dir, 'dead.log');
    fs.writeFileSync(aliveLog, 'Still working on Alpha\n');
    fs.writeFileSync(deadLog, 'Crashed on Beta\n');
    const aliveResult = path.join(dir, 'alive.result.txt');
    const deadResult = path.join(dir, 'dead.result.txt');
    fs.writeFileSync(aliveResult, 'STATUS: WATCHING\n');
    fs.writeFileSync(deadResult, 'STATUS: WATCHING\n');
    fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({
      projects: {
        alpha: {
          status: 'watching', jobId: 'job-a', jobTitle: 'Alive job', workerPid: 201, workerAlive: true, workerLog: aliveLog, resultPath: aliveResult,
          dispatch: [{ subTaskId: 'ST-alive', attemptId: 'att-alive', pid: 201, alive: true, logPath: aliveLog, resultPath: aliveResult, stoppedByUser: false }]
        },
        beta: {
          status: 'watching', jobId: 'job-b', jobTitle: 'Dead job', workerPid: 202, workerAlive: true, workerLog: deadLog, resultPath: deadResult,
          dispatch: [{ subTaskId: 'ST-dead', attemptId: 'att-dead', pid: 202, alive: true, logPath: deadLog, resultPath: deadResult, stoppedByUser: false }]
        }
      }
    }) + '\n');
    const livePids = new Set([201]);
    const plan = {
      schema: 2,
      projects: [
        { project: { id: 'alpha', name: 'Alpha', folder: null }, revisions: [{ id: 1, direction: 'Goal A' }], tasks: [{ id: 'job-a', title: 'Alive job', status: 'running' }] },
        { project: { id: 'beta', name: 'Beta', folder: null }, revisions: [{ id: 1, direction: 'Goal B' }], tasks: [{ id: 'job-b', title: 'Dead job', status: 'running' }] }
      ]
    };
    const orch = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir, models, providers, readPlan: async () => plan, attempts: fakeAttempts(), fetchPlans: async () => [],
      isProcessAlive: pid => {
        const alive = livePids.has(pid);
        exchange({ kind: 'isProcessAlive', pid }, { alive });
        return alive;
      },
      spawnWorker: info => { exchange({ kind: 'spawnWorker', unexpected: true, projectId: info.projectId }, { pid: 999 }); return { pid: 999 }; },
      now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-reconcile'
    });
    const first = await orch.tick();
    const second = await orch.tick();
    const alpha1 = first.projects.find(p => p.id === 'alpha');
    const beta1 = first.projects.find(p => p.id === 'beta');
    const beta2 = second.projects.find(p => p.id === 'beta');
    return {
      firstTick: {
        alpha: { status: alpha1.status, workerAlive: alpha1.workerAlive, workerPid: alpha1.workerPid },
        beta: { status: beta1.status, workerAlive: beta1.workerAlive, workerPid: beta1.workerPid }
      },
      secondTickBeta: { status: beta2.status, workerAlive: beta2.workerAlive, workerPid: beta2.workerPid }
    };
  });

scenario('orchestrator-state', 'research-v1-and-v2-lifecycle-and-row-sharing',
  { category: 'research' }, async ({ root, exchange }) => {
    const { providers, models } = stateModels();
    const dir1 = path.join(root, 'v1');
    const dir2 = path.join(root, 'v2');
    const plan = teamPlan(['alpha']);
    const livePids = new Set();
    let pidSeq = 300;
    const makeSpawn = label => info => {
      const pid = ++pidSeq;
      livePids.add(pid);
      fs.mkdirSync(path.dirname(info.logPath), { recursive: true });
      fs.writeFileSync(info.logPath, 'Researching ' + label + '\n');
      exchange({ kind: 'spawnWorker', label, projectId: info.projectId, attemptId: info.attemptId }, { pid });
      return { pid, logPath: info.logPath };
    };

    const orch1 = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: dir1, models, providers, readPlan: async () => plan, attempts: stateAttempts(dir1), fetchPlans: async () => [],
      isProcessAlive: pid => livePids.has(pid),
      killWorker: pid => { livePids.delete(pid); return true; },
      spawnWorker: makeSpawn('v1'),
      now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-res-v1'
    });
    const d1 = await orch1.dispatchResearch({ type: 'dispatch_research', projectId: 'alpha', modelId: 'astra', title: 'Research v1', detail: 'Check v1' });
    const snap1Watching = await orch1.snapshot('alpha');
    fs.writeFileSync(d1.research.reportPath, '# V1 Report\nFindings.\n');
    fs.writeFileSync(d1.research.resultPath, 'STATUS: COMPLETE\nEXIT: 0\n');
    livePids.delete(d1.research.pid);
    const snap1Complete = await orch1.snapshot('alpha');
    const report1 = orch1.researchReport(d1.research.id);

    const orch2 = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir: dir2, phase2: true, models, providers, readPlan: async () => plan, attempts: stateAttempts(dir2), fetchPlans: async () => [],
      isProcessAlive: pid => livePids.has(pid),
      spawnWorker: makeSpawn('v2'),
      now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-res-v2'
    });
    const d2 = await orch2.dispatchResearch({ type: 'dispatch_research', projectId: 'alpha', modelId: 'astra', title: 'Research v2', detail: 'Check v2' });
    const approved2 = await orch2.approveResearch(d2.research.id, { projectId: 'alpha', megaTaskId: d2.research.megaTaskId });
    const res2Record = approved2.snapshot.researches.find(r => r.id === d2.research.id);
    const alphaRow = approved2.snapshot.projects.find(p => p.id === 'alpha');
    return {
      v1: {
        watchingAlive: snap1Watching.researches[0].alive,
        completeStatus: snap1Complete.researches[0].status,
        reportAvailable: snap1Complete.researches[0].reportAvailable,
        reportText: report1.ok ? report1.report : null
      },
      v2: {
        proposalStatus: d2.research.status,
        executionVersion: d2.research.executionVersion,
        approvedOk: approved2.ok,
        researchDispatchLen: (res2Record.dispatch || []).length,
        researchAlive: res2Record.alive,
        projectRowDispatchLen: alphaRow.dispatch.length
      }
    };
  });

scenario('orchestrator-state', 'archive-cleanup-focus-switching-and-two-project-isolation',
  { category: 'archive-and-focus' }, async ({ root }) => {
    const { providers, models } = stateModels();
    const dir = path.join(root, 'state');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({
      selectedProjectId: 'alpha',
      projects: {
        alpha: { status: 'complete', jobId: 'job-a' },
        beta: { status: 'idle', jobId: 'job-b' }
      },
      autonomy: { alpha: { enabled: false }, beta: { enabled: true, sessionId: 'session-focus' } },
      projectTeams: { alpha: { orchestratorId: 'astra', roles: {} }, beta: { orchestratorId: 'opus', roles: {} } },
      projectTools: { alpha: { default: { bash: true } }, beta: { default: { bash: false } } },
      asks: [
        { id: 'ask-a', projectId: 'alpha', question: 'QA', answer: 'AA' },
        { id: 'ask-b', projectId: 'beta', question: 'QB', answer: 'AB' }
      ],
      researches: [
        { id: 'res-a', projectId: 'alpha', status: 'complete' },
        { id: 'res-b', projectId: 'beta', status: 'watching' }
      ],
      workerHistory: [
        { id: 'alpha', status: 'complete', dispatch: [] },
        { id: 'beta', status: 'complete', dispatch: [] }
      ],
      execution: {
        megaTasks: [
          { id: 'MT-a', projectId: 'alpha', taskId: 'job-a', status: 'COMPLETE' },
          { id: 'MT-b', projectId: 'beta', taskId: 'job-b', status: 'READY_FOR_APPROVAL' }
        ],
        subTasks: [
          { id: 'ST-a', megaTaskId: 'MT-a', status: 'COMPLETE' },
          { id: 'ST-b', megaTaskId: 'MT-b', status: 'PENDING' }
        ],
        results: [{ id: 'R-a', megaTaskId: 'MT-a', status: 'COMPLETE' }],
        checkpoints: [{ id: 'CP-a', projectId: 'alpha', megaTaskId: 'MT-a' }],
        timeline: [{ id: 'EV-a', projectId: 'alpha', type: 'NOTE' }, { id: 'EV-b', projectId: 'beta', type: 'NOTE' }]
      }
    }) + '\n');
    const plan = teamPlan(['alpha', 'beta']);
    const orch = stateSandbox(root).load('orchestrator.js').createOrchestrator({
      dir, models, providers, readPlan: async () => plan, attempts: fakeAttempts(), fetchPlans: async () => [],
      spawnWorker: () => { throw new Error('Unexpected worker launch'); },
      now: () => '2023-11-14T22:13:20.000Z', sessionId: 'session-focus'
    });
    const focusBeta = await orch.setFocus('beta');
    const focusInvalid = await orch.setFocus('missing');
    const blockedBeta = await orch.archiveProject('beta', 'Cannot archive watching research');
    const archivedAlpha = await orch.archiveProject('alpha', 'Removed by owner');
    const persisted = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8'));
    return {
      focusBeta,
      focusInvalid,
      blockedBeta,
      archivedAlpha,
      selectedAfter: persisted.selectedProjectId,
      remainingProjects: Object.keys(persisted.projects),
      remainingAsks: persisted.asks.map(a => a.id),
      remainingResearches: persisted.researches.map(r => r.id),
      remainingMegaTasks: persisted.execution.megaTasks.map(m => m.id),
      remainingSubTasks: persisted.execution.subTasks.map(s => s.id),
      remainingResults: persisted.execution.results.length,
      archiveCount: persisted.projectArchive.length,
      archiveProjectId: persisted.projectArchive[0].projectId
    };
  });

if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
