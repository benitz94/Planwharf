'use strict';
// Frozen acceptance lives outside trial workspaces. Child execution is bounded
// process isolation, not an OS security sandbox for hostile implementations.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const hash = value => crypto.createHash('sha256').update(value).digest('hex');

const COMMON = `Use CommonJS and Node.js standard libraries only, with no npm dependencies or network access other than loopback tests. Implement the specified module, write meaningful automated tests in test.cjs runnable with node --test test.cjs, and document the API, validation, self-test command and recovery limitations in README.md. You may inspect files, self-plan and run your tests normally. Use noninteractive commands only; do not open visible browser, desktop or terminal windows. Do not inspect or modify files outside your own trial workspace. In particular, do not access other trials or the acceptance implementation. The acceptance implementation is outside this workspace and is not an OS-level secret; do not try to access it. No implementation plan or solution is supplied. Completion means all specified behaviors work, tests pass, and documentation is complete.`;
const TASKS = {
  config: {
    class: 'small', title: 'Validated configuration utility', file: 'config.cjs',
    contract: `Implement config.cjs exporting parseConfig(text). text is a JSON string containing a plain object. Return a new object with exactly {port,host,retries,logLevel}. Defaults: port 3000, host '127.0.0.1', retries 2, logLevel 'info'. port must be an integer 1..65535; retries an integer 0..10; host a string 1..253 characters with no whitespace; logLevel one of debug/info/warn/error. Missing fields receive defaults, but explicit null or incorrect types are invalid. Reject unknown keys, arrays, null, invalid JSON, non-string input, fractional/out-of-range numbers and whitespace hosts by throwing an Error. Do not coerce strings into numbers, mutate global state or cache mutable result objects.`,
    checks: ['defaults', 'valid-boundaries', 'invalid-values', 'unknown-and-malformed', 'independent-results', 'deliverables']
  },
  'rest-api': {
    class: 'medium', title: 'Persistent REST item service', file: 'api.cjs',
    contract: `Implement api.cjs exporting createServer({file,onStage}), synchronously returning an unbound node:http Server. Validate an existing store during the factory call and throw immediately if malformed. Handle rejected mutation promises as HTTP error responses, not unhandled rejections. Array order in the disk store is unspecified; GET responses must be sorted. The caller calls server.listen(0,'127.0.0.1') and closes it. No server starts at import. Persist all items as JSON at the exact file path; create missing parent directories, treat a nonexistent file as empty, and reject an existing malformed store rather than silently resetting it. One server process owns a store; cross-process simultaneous writers are outside scope. Store format is an object {version:1,items:[...]}. Each item has exactly {id,title,done}; id matches /^[a-z][a-z0-9-]{0,31}$/, title is trimmed to 1..80 characters, done is boolean (defaults false on POST). Reject unknown body properties and incorrect types.
Routes: GET /items -> 200 array sorted by id; GET /items/:id -> 200 item or 404 JSON {error:string}; POST /items with {id,title,done?} -> 201 item and Location '/items/'+id, duplicate id ->409; PATCH /items/:id accepts a nonempty subset of {title,done}, preserves id, ->200 item or404; DELETE /items/:id ->204 no body or404. Unknown routes ->404. Malformed JSON and validation errors ->400 JSON {error:string}. Responses containing JSON must have application/json Content-Type. Reject request bodies over 16384 bytes with413 and never commit them. Successful mutations must be durable before the response. Serialize concurrent writes to avoid lost updates. Use atomic replacement of the store file so an interrupted commit leaves either old or new complete data. Await optional onStage(stage) immediately before replacement ('beforeCommit') and immediately after durable replacement ('afterCommit'), before sending the response. Tests may terminate the process synchronously in that hook. Recover correctly on a new process after either fault; beforeCommit preserves old data, afterCommit preserves new data even without a response. Leftover temporary files must not override the committed store. No authentication/public deployment is required; this is a local service.`,
    checks: ['crud', 'validation', 'parallel-writes', 'restart', 'before-commit-crash', 'after-commit-crash', 'corrupt-store', 'deliverables']
  },
  scheduler: {
    class: 'complex', title: 'Recoverable dependency scheduler', file: 'scheduler.cjs',
    contract: `Implement scheduler.cjs exporting createScheduler({file,concurrency,execute,onStage}). concurrency must be integer 1..8; execute must be a function. The factory returns {enqueue,run,snapshot}. enqueue(jobs) is async, accepts a batch of {id,dependsOn?,payload?}, defaults dependsOn=[] and payload=null. IDs match /^[a-z][a-z0-9-]{0,31}$/; reject duplicate IDs, unknown fields, invalid dependency arrays, repeated/self/unknown dependencies, cycles, and IDs already present. Dependencies may reference the same batch or previously enqueued jobs. Reject an invalid batch atomically: no partial changes to memory or storage. snapshot() returns a detached JSON object {version:1,jobs:[...]} with jobs sorted by id; each job has id,dependsOn,payload,status,attempt and optional result/error. Status values: pending/running/completed/failed/blocked. Initial attempt=0. run() returns a Promise of the final snapshot and starts all ready work up to concurrency, picking ready jobs in id order; it resolves only when no work remains running or runnable. Concurrent run() calls join the same drain (no duplicate execution). Call execute({id,payload,attempt}) asynchronously once per claimed attempt. Persist status running and incremented attempt BEFORE execute. Persist completed with its JSON result after success; rejected execution becomes failed with string error. Transitively mark descendants of failed or blocked jobs blocked, never execute them, and continue independent jobs. Completed work is never executed again on repeated run() or restart. Allow enqueue of additional work after a finished run.
Storage: a JSON checkpoint {version:1,jobs:[...]} at file, atomically replaced on EVERY accepted enqueue, claim and terminal transition. Create missing parent directories; reject corrupt existing checkpoints. One scheduler process owns a file. On restart load all jobs; previously running jobs become pending, retaining attempt count, so the next claim increments it. This is at-least-once recovery: interrupted execute may repeat, acknowledged completed results must not repeat. Await optional onStage(stage,jobId) after a durable claimed ('claimed') or terminal completed ('completed') checkpoint and before moving on. Tests may synchronously terminate the process at those exact stages. A crash after claimed must recover the job as pending and execute attempt 2; after completed must retain the result and never replay it. Stale temporary files must not override committed checkpoints. Full JSON checkpoint inspection is the recovery invariant; a separate append-only journal is optional, not required. Use bounded finite tests, document at-least-once side-effect limitations, and include stress/concurrency and crash/restart tests.`,
    checks: ['graph-and-concurrency', 'failed-dependencies', 'batch-validation', 'joined-runs', 'detached-snapshots', 'restart-completed', 'claimed-crash', 'completed-crash', 'corrupt-checkpoint', 'deliverables']
  }
};

function task(id) { if (!Object.hasOwn(TASKS, id)) throw Error('Unknown benchmark task: ' + id); return TASKS[id]; }
function specification(id) { const item = task(id); return `${item.title}\n\n${item.contract}\n\n${COMMON}\n`; }
function listTasks() { return Object.entries(TASKS).map(([id, item]) => ({ id, class: item.class, title: item.title, module: item.file, checkCount: item.checks.length, specificationHash: hash(specification(id)) })); }
function setupTask(id, directory) {
  const item = task(id), workspace = path.resolve(directory);
  fs.mkdirSync(workspace, { recursive: true });
  if (fs.readdirSync(workspace).length) throw Error('Starter directory must be empty');
  const files = {
    'TASK.md': specification(id),
    [item.file]: "'use strict';\n// Implement the public contract in TASK.md.\nmodule.exports = {};\n",
    'README.md': '# ' + item.title + '\n\nImplementation and self-test documentation goes here.\n',
    '.gitignore': 'node_modules/\n*.tmp\n'
  };
  for (const [name, content] of Object.entries(files)) fs.writeFileSync(path.join(workspace, name), content, { flag: 'wx' });
  const starterHash = hash(JSON.stringify(Object.entries(files).sort(([a], [b]) => a.localeCompare(b))));
  return { taskId: id, workspace, files: Object.keys(files).sort(), prompt: specification(id), starterHash };
}

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function rejects(action) { await assert.rejects(async () => action()); }
async function withApi(candidate, scratch, action, options = {}) {
  const server = candidate.createServer({ file: path.join(scratch, 'store.json'), ...options });
  assert.equal(typeof server?.listen, 'function');
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
  const base = 'http://127.0.0.1:' + server.address().port;
  const request = async (method, route, body, status) => {
    const response = await fetch(base + route, { method, ...(body === undefined ? {} : { body: typeof body === 'string' ? body : JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }), signal: AbortSignal.timeout(3000) });
    const text = await response.text();
    if (status !== undefined) assert.equal(response.status, status, text.slice(0, 300));
    if (text) assert.match(response.headers.get('content-type') || '', /application\/json/i);
    return { status: response.status, body: text ? JSON.parse(text) : null, location: response.headers.get('location') };
  };
  try { return await action(request, server); }
  finally { server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve)); }
}
function store(scratch) { return path.join(scratch, 'store.json'); }
function checkpoint(scratch) { const value = JSON.parse(fs.readFileSync(store(scratch), 'utf8')); assert.equal(value.version, 1); assert.ok(Array.isArray(value.jobs)); return value; }
function scheduler(candidate, scratch, options = {}) { return candidate.createScheduler({ file: store(scratch), concurrency: 2, execute: async job => job.id, ...options }); }
function statuses(snapshot) { return Object.fromEntries(snapshot.jobs.map(job => [job.id, job.status])); }

// These exact stage-fault workers execute the candidate in a NEW process. They
// intentionally exit after durable stage notifications rather than emulating a
// restart by deleting require.cache in the grader process.
async function stageWorker(id, workspace, scratch, stage) {
  const candidate = require(path.join(workspace, task(id).file));
  if (id === 'rest-api') {
    const server = candidate.createServer({ file: store(scratch), onStage: name => { if (name === stage) process.exit(86); } });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    await fetch('http://127.0.0.1:' + server.address().port + '/items', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: 'new', title: 'new item' }) });
    server.closeAllConnections?.(); await new Promise(resolve => server.close(resolve));
  } else if (id === 'scheduler') {
    const queue = scheduler(candidate, scratch, { execute: async job => job.id, onStage: name => { if (name === stage) process.exit(86); } });
    await queue.run();
  }
}
function subprocess(mode, id, workspace, scratch, label, timeoutMs = 5000) {
  const env = { ...Object.fromEntries(['PATH', 'SystemRoot', 'WINDIR'].filter(key => process.env[key]).map(key => [key, process.env[key]])), HOME: scratch, USERPROFILE: scratch, TMP: scratch, TEMP: scratch, TMPDIR: scratch };
  return spawnSync(process.execPath, [__filename, mode, id, workspace, scratch, label], { cwd: workspace, env, windowsHide: true, encoding: 'utf8', timeout: timeoutMs, maxBuffer: 1024 * 1024 });
}
function assertStageFault(id, workspace, scratch, stage) {
  const result = subprocess('--stage-worker', id, workspace, scratch, stage);
  assert.equal(result.error, undefined, result.error?.message);
  assert.equal(result.status, 86, 'Fault stage was not reached: ' + (result.stderr || result.stdout).slice(0, 400));
}

async function configProbe(name, candidate) {
  const parse = candidate.parseConfig; assert.equal(typeof parse, 'function');
  if (name === 'defaults') assert.deepEqual(parse('{}'), { port: 3000, host: '127.0.0.1', retries: 2, logLevel: 'info' });
  if (name === 'valid-boundaries') {
    assert.deepEqual(parse('{"port":1,"host":"::1","retries":0,"logLevel":"debug"}'), { port: 1, host: '::1', retries: 0, logLevel: 'debug' });
    assert.equal(parse('{"port":65535,"retries":10,"logLevel":"error"}').port, 65535);
    assert.equal(parse('{"logLevel":"warn"}').logLevel, 'warn');
  }
  if (name === 'invalid-values') for (const value of [{ port: 0 }, { port: 65536 }, { port: 1.5 }, { port: '80' }, { retries: -1 }, { retries: 11 }, { retries: 1.5 }, { retries: false }, { host: '' }, { host: false }, { host: 'a b' }, { host: 'a\tb' }, { host: 'a'.repeat(254) }, { logLevel: 'INFO' }, { logLevel: null }, { port: null }]) assert.throws(() => parse(JSON.stringify(value)), JSON.stringify(value));
  if (name === 'unknown-and-malformed') for (const value of ['[]', 'null', 'true', '1', '"x"', '{', '{"unknown":1}', '{"__proto__":{}}', {}, null]) assert.throws(() => parse(value));
  if (name === 'independent-results') { const first = parse('{}'); first.port = 12; assert.equal(parse('{}').port, 3000); assert.equal(parse('{"port":80}').port, 80); }
}

async function apiProbe(name, candidate, workspace, scratch) {
  assert.equal(typeof candidate.createServer, 'function');
  if (name === 'crud') await withApi(candidate, scratch, async request => {
    assert.deepEqual((await request('GET', '/items', undefined, 200)).body, []);
    const made = await request('POST', '/items', { id: 'alpha', title: '  First  ' }, 201);
    assert.deepEqual(made.body, { id: 'alpha', title: 'First', done: false }); assert.equal(made.location, '/items/alpha');
    assert.deepEqual((await request('GET', '/items/alpha', undefined, 200)).body, made.body);
    await request('POST', '/items', { id: 'alpha', title: 'duplicate' }, 409);
    assert.deepEqual((await request('PATCH', '/items/alpha', { done: true }, 200)).body, { id: 'alpha', title: 'First', done: true });
    assert.deepEqual((await request('PATCH', '/items/alpha', { title: '  Updated  ' }, 200)).body, { id: 'alpha', title: 'Updated', done: true });
    await request('DELETE', '/items/alpha', undefined, 204); await request('GET', '/items/alpha', undefined, 404);
    await request('PATCH', '/items/alpha', { title: 'gone' }, 404); await request('DELETE', '/items/alpha', undefined, 404); await request('GET', '/unknown', undefined, 404);
  });
  if (name === 'validation') await withApi(candidate, scratch, async request => {
    for (const body of ['{', null, [], {}, { id: '../x', title: 'bad' }, { id: 'a', title: ' ' }, { id: 'a', title: 'x'.repeat(81) }, { id: 'a', title: 'a', done: 'yes' }, { id: 'a', title: 'a', extra: true }]) { const response = await request('POST', '/items', body, 400); assert.equal(typeof response.body.error, 'string'); }
    await request('POST', '/items', { id: 'a', title: 'ok' }, 201);
    for (const body of [{}, { id: 'b' }, { title: null }, { done: 1 }]) await request('PATCH', '/items/a', body, 400);
    await request('POST', '/items', { id: 'huge', title: 'x'.repeat(17000) }, 413);
    assert.deepEqual((await request('GET', '/items', undefined, 200)).body, [{ id: 'a', title: 'ok', done: false }]);
  });
  if (name === 'parallel-writes') await withApi(candidate, scratch, async request => {
    await Promise.all(Array.from({ length: 16 }, (_, i) => request('POST', '/items', { id: 'j' + String(i).padStart(2, '0'), title: 'Item ' + i }, 201)));
    const items = (await request('GET', '/items', undefined, 200)).body;
    assert.equal(items.length, 16); assert.deepEqual(items.map(item => item.id), Array.from({ length: 16 }, (_, i) => 'j' + String(i).padStart(2, '0')));
    const disk = JSON.parse(fs.readFileSync(store(scratch), 'utf8')); assert.equal(disk.version, 1); assert.deepEqual([...disk.items].sort((a,b)=>a.id.localeCompare(b.id)), items);
  });
  if (name === 'restart') {
    const written = subprocess('--prepare-api', 'rest-api', workspace, scratch, 'seed'); assert.equal(written.status, 0, written.stderr);
    fs.writeFileSync(store(scratch) + '.tmp', '{stale incomplete replacement');
    await withApi(candidate, scratch, async request => assert.deepEqual((await request('GET', '/items', undefined, 200)).body, [{ id: 'old', title: 'updated item', done: true }]));
  }
  if (name === 'before-commit-crash' || name === 'after-commit-crash') {
    fs.writeFileSync(store(scratch), JSON.stringify({ version: 1, items: [{ id: 'old', title: 'old item', done: false }] }));
    assertStageFault('rest-api', workspace, scratch, name === 'before-commit-crash' ? 'beforeCommit' : 'afterCommit');
    const disk = JSON.parse(fs.readFileSync(store(scratch), 'utf8')); assert.equal(disk.version, 1);
    const expected = name === 'before-commit-crash' ? ['old'] : ['new', 'old'];
    assert.deepEqual(disk.items.map(item => item.id).sort(), expected);
    // The restart below is a new server in a new probe process relative to the
    // terminated stage-worker, not another call inside that worker.
    await withApi(candidate, scratch, async request => assert.deepEqual((await request('GET', '/items', undefined, 200)).body.map(item => item.id), expected));
  }
  if (name === 'corrupt-store') for (const value of ['{broken', '{"version":2,"items":[]}', '{"version":1,"items":{}}']) { fs.writeFileSync(store(scratch), value); await rejects(() => candidate.createServer({ file: store(scratch) })); }
}

async function schedulerProbe(name, candidate, workspace, scratch) {
  assert.equal(typeof candidate.createScheduler, 'function');
  if (name === 'graph-and-concurrency') {
    let active = 0, peak = 0; const finished = new Set(), seen = [];
    const jobs = Array.from({ length: 24 }, (_, i) => ({ id: 'j' + String(i).padStart(2, '0'), dependsOn: i >= 12 ? ['j' + String(i - 12).padStart(2, '0')] : [], payload: i }));
    const queue = scheduler(candidate, scratch, { concurrency: 3, execute: async job => {
      const source = jobs.find(item => item.id === job.id); for (const dep of source.dependsOn) assert.ok(finished.has(dep), 'Dependency completed before execution');
      active++; peak = Math.max(peak, active); assert.ok(active <= 3); seen.push(job.id); assert.equal(job.attempt, 1); assert.equal(job.payload, source.payload);
      const disk = checkpoint(scratch).jobs.find(item => item.id === job.id); assert.equal(disk.status, 'running'); assert.equal(disk.attempt, 1);
      await wait(12); active--; finished.add(job.id); return { value: job.payload * 2 };
    } });
    await queue.enqueue(jobs); const result = await queue.run();
    assert.equal(seen.length, 24); assert.equal(new Set(seen).size, 24); assert.equal(peak, 3, 'Must actually use requested concurrency');
    assert.deepEqual(seen.slice(0, 3), ['j00', 'j01', 'j02']); assert.ok(result.jobs.every(job => job.status === 'completed' && job.attempt === 1));
    assert.deepEqual(result.jobs.map(job => job.id), jobs.map(job => job.id));
    assert.equal(result.jobs.find(job => job.id === 'j23').result.value, 46); assert.deepEqual(checkpoint(scratch), result);
  }
  if (name === 'failed-dependencies') {
    const seen = []; const queue = scheduler(candidate, scratch, { execute: async job => { seen.push(job.id); if (job.id === 'a') throw Error('injected failure'); return job.id; } });
    await queue.enqueue([{ id: 'a' }, { id: 'b', dependsOn: ['a'] }, { id: 'c', dependsOn: ['b'] }, { id: 'independent' }]);
    const result = await queue.run(); assert.deepEqual(statuses(result), { a: 'failed', b: 'blocked', c: 'blocked', independent: 'completed' }); assert.equal(typeof result.jobs[0].error, 'string'); assert.deepEqual(seen.sort(), ['a', 'independent']);
  }
  if (name === 'batch-validation') {
    for (const concurrency of [0, 9, 1.5, '2']) assert.throws(() => scheduler(candidate, scratch, { concurrency }));
    const queue = scheduler(candidate, scratch); await queue.enqueue([{ id: 'existing' }]); const before = JSON.stringify(queue.snapshot()), disk = fs.readFileSync(store(scratch), 'utf8');
    for (const batch of [[{ id: 'new' }, { id: 'new' }], [{ id: 'a', dependsOn: ['b'] }, { id: 'b', dependsOn: ['a'] }], [{ id: 'existing' }], [{ id: 'a', dependsOn: ['missing'] }], [{ id: 'a', dependsOn: ['a'] }], [{ id: '../bad' }], [{ id: 'a', dependsOn: 'existing' }], [{ id: 'a', dependsOn: ['existing', 'existing'] }], [{ id: 'a', unknown: 1 }], null, {}]) {
      await rejects(() => queue.enqueue(batch)); assert.equal(JSON.stringify(queue.snapshot()), before); assert.equal(fs.readFileSync(store(scratch), 'utf8'), disk);
    }
  }
  if (name === 'joined-runs') {
    const seen = []; const queue = scheduler(candidate, scratch, { execute: async job => { seen.push(job.id); await wait(20); return job.id; } });
    await queue.enqueue([{ id: 'a' }, { id: 'b' }]); await Promise.all([queue.run(), queue.run(), queue.run()]); await queue.run(); assert.deepEqual(seen.sort(), ['a', 'b']);
    await queue.enqueue([{ id: 'c', dependsOn: ['a'] }]); const result = await queue.run(); assert.equal(result.jobs.length, 3); assert.equal(seen.filter(id => id === 'c').length, 1);
  }
  if (name === 'detached-snapshots') { const queue = scheduler(candidate, scratch); await queue.enqueue([{ id: 'a', payload: { nested: 1 } }]); const view = queue.snapshot(); view.jobs[0].status = 'completed'; view.jobs[0].payload.nested = 9; view.jobs.push({ id: 'fake' }); assert.equal(queue.snapshot().jobs.length, 1); assert.equal(queue.snapshot().jobs[0].status, 'pending'); assert.equal(queue.snapshot().jobs[0].payload.nested, 1); }
  if (name === 'restart-completed') {
    const queue = scheduler(candidate, scratch); await queue.enqueue([{ id: 'a' }]);
    const result = subprocess('--stage-worker', 'scheduler', workspace, scratch, 'no-fault'); assert.equal(result.status, 0, result.stderr);
    fs.writeFileSync(store(scratch) + '.tmp', '{stale incomplete replacement');
    let calls = 0; const restarted = scheduler(candidate, scratch, { execute: async () => { calls++; return 'wrong'; } }); const report = await restarted.run(); assert.equal(calls, 0); assert.equal(report.jobs[0].result, 'a'); assert.equal(report.jobs[0].attempt, 1);
  }
  if (name === 'claimed-crash' || name === 'completed-crash') {
    const queue = scheduler(candidate, scratch); await queue.enqueue([{ id: 'a' }, { id: 'b', dependsOn: ['a'] }]);
    assertStageFault('scheduler', workspace, scratch, name === 'claimed-crash' ? 'claimed' : 'completed');
    const crash = checkpoint(scratch), first = crash.jobs.find(job => job.id === 'a'); assert.equal(first.status, name === 'claimed-crash' ? 'running' : 'completed'); assert.equal(first.attempt, 1);
    const calls = []; const restarted = scheduler(candidate, scratch, { execute: async job => { calls.push({ id: job.id, attempt: job.attempt }); return job.id; } });
    assert.equal(restarted.snapshot().jobs.find(job => job.id === 'a').status, name === 'claimed-crash' ? 'pending' : 'completed');
    const result = await restarted.run(); assert.ok(result.jobs.every(job => job.status === 'completed'));
    assert.deepEqual(calls, name === 'claimed-crash' ? [{ id: 'a', attempt: 2 }, { id: 'b', attempt: 1 }] : [{ id: 'b', attempt: 1 }]);
    assert.deepEqual(checkpoint(scratch), result);
  }
  if (name === 'corrupt-checkpoint') for (const value of ['{bad', '{"version":2,"jobs":[]}', '{"version":1,"jobs":{}}']) { fs.writeFileSync(store(scratch), value); assert.throws(() => scheduler(candidate, scratch)); }
}

async function probe(id, name, workspace, scratch) {
  const item = task(id);
  if (name === 'deliverables') {
    assert.ok(fs.readFileSync(path.join(workspace, 'README.md'), 'utf8').trim().length >= 120, 'Document API, tests and limitations');
    assert.ok(fs.statSync(path.join(workspace, 'test.cjs')).isFile(), 'Self-tests required');
    const result = spawnSync(process.execPath, ['--test', 'test.cjs'], { cwd: workspace, windowsHide: true, timeout: 5000, encoding: 'utf8', maxBuffer: 1024 * 1024 });
    assert.equal(result.error, undefined, result.error?.message); assert.equal(result.status, 0, (result.stdout + result.stderr).slice(-1000)); return;
  }
  const candidate = require(path.join(workspace, item.file));
  if (id === 'config') return configProbe(name, candidate);
  if (id === 'rest-api') return apiProbe(name, candidate, workspace, scratch);
  return schedulerProbe(name, candidate, workspace, scratch);
}

async function evaluateTask(id, workspace, options = {}) {
  const item = task(id); workspace = path.resolve(workspace);
  const timeoutMs = options.timeoutMs ?? 10000;
  if (!Number.isInteger(timeoutMs) || timeoutMs < 100 || timeoutMs > 60000) throw Error('timeoutMs must be 100..60000 per acceptance check');
  const evidence = options.evidenceDir ? path.resolve(options.evidenceDir) : fs.mkdtempSync(path.join(os.tmpdir(), 'planwharf-acceptance-'));
  if (evidence === workspace || evidence.startsWith(workspace + path.sep)) throw Error('Acceptance evidence must be outside model workspace');
  fs.mkdirSync(evidence, { recursive: true });
  const checks = [];
  for (const name of item.checks) {
    const scratch = fs.mkdtempSync(path.join(evidence, name + '-'));
    const result = subprocess('--probe', id, workspace, scratch, name, timeoutMs);
    let value;
    try { value = JSON.parse((result.stdout || '').trim().split(/\r?\n/).filter(line => line.startsWith('BENCHMARK_RESULT ')).at(-1).slice(17)); } catch {}
    const passed = !result.error && result.status === 0 && value?.passed === true;
    const detail = result.error?.message || value?.detail || (result.stderr || '').slice(-1000) || (passed ? undefined : 'Acceptance probe exited without a valid result');
    checks.push({ name, passed, ...(detail ? { detail } : {}) });
    fs.writeFileSync(path.join(scratch, 'probe.json'), JSON.stringify({ name, passed, exitCode: result.status, signal: result.signal, error: result.error?.message, stdout: (result.stdout || '').slice(-16000), stderr: (result.stderr || '').slice(-16000) }, null, 2));
  }
  const passed = checks.filter(check => check.passed).length;
  const report = { taskId: id, passed, total: checks.length, outcome: passed === checks.length ? 'independently-accepted' : passed ? 'completed-with-defects' : 'failed', checks, specificationHash: hash(specification(id)), graderHash: hash(fs.readFileSync(__filename)), evidenceDir: evidence, scope: 'Deterministic behavioral checks; documentation/self-test presence does not establish maintainability or test coverage quality. Child processes are not an OS sandbox.' };
  fs.writeFileSync(path.join(evidence, id + '-acceptance.json'), JSON.stringify(report, null, 2)); return report;
}

module.exports = { listTasks, setupTask, specification, evaluateTask };
if (require.main === module) {
  const [mode, id, workspace, scratch, name] = process.argv.slice(2);
  (async () => {
    if (mode === '--stage-worker') return stageWorker(id, workspace, scratch, name);
    if (mode === '--prepare-api') return withApi(require(path.join(workspace, 'api.cjs')), scratch, async request => {
      await request('POST', '/items', { id: 'old', title: 'old item' }, 201);
      await request('POST', '/items', { id: 'deleted', title: 'discard me' }, 201);
      await request('PATCH', '/items/old', { title: 'updated item', done: true }, 200);
      await request('DELETE', '/items/deleted', undefined, 204);
    });
    if (mode !== '--probe' || !task(id).checks.includes(name)) throw Error('Internal benchmark mode required');
    try { await probe(id, name, workspace, scratch); console.log('BENCHMARK_RESULT ' + JSON.stringify({ passed: true })); }
    catch (error) { console.log('BENCHMARK_RESULT ' + JSON.stringify({ passed: false, detail: String(error.message).slice(0, 1500) })); process.exitCode = 1; }
  })().catch(error => { console.error(error.message); process.exitCode = 1; });
}
