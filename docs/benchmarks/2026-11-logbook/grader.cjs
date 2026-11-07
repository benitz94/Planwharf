'use strict';
// Hidden checks for task/TASK.md: node grader.cjs <workspace> [--skip-suite] [--only <regex>] [--out <file>]
// Works on a private copy, so a workspace cannot alter its own grading state.
const fs = require('node:fs'), os = require('node:os'), path = require('node:path'), cp = require('node:child_process');
const args = process.argv.slice(2);
const source = path.resolve(args[0]);
const skipSuite = args.includes('--skip-suite');
const only = args.includes('--only') ? new RegExp(args[args.indexOf('--only') + 1]) : null;
const outFile = args.includes('--out') ? path.resolve(args[args.indexOf('--out') + 1]) : null;
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'logbook-grade-')), work = path.join(root, 'work');
fs.cpSync(source, work, { recursive: true, filter: file => !/[\\/](\.git|node_modules)([\\/]|$)/.test(file.slice(source.length)) });
const program = path.join(work, 'logbook.js');
const results = [];
let serial = 0;
const fresh = () => path.join(root, 'd' + (++serial));

// ---------- running the program ----------
function parse(stdout) { const text = String(stdout || '').trim(); try { return JSON.parse(text); } catch { return null; } }
function lb(dir, ...rest) {
  let cwd = root;
  if (rest.length && typeof rest[0] === 'object') cwd = rest.shift().cwd;
  const started = Date.now();
  const r = cp.spawnSync(process.execPath, [program, ...(dir === null ? [] : [dir]), ...rest], { encoding: 'utf8', timeout: 30000, cwd, windowsHide: true });
  return { code: r.status, out: parse(r.stdout), stdout: r.stdout || '', ms: Date.now() - started };
}
function lbAsync(dir, ...rest) {
  return new Promise(resolve => {
    const child = cp.spawn(process.execPath, [program, dir, ...rest], { cwd: root, windowsHide: true });
    let stdout = ''; child.stdout.on('data', chunk => { stdout += chunk; });
    child.on('close', code => resolve({ code, out: parse(stdout), stdout }));
  });
}
const ok = r => r.code === 0 && r.out?.ok === true;
const err = (r, code) => r.code === 1 && r.out?.ok === false && r.out.code === code && typeof r.out.error === 'string' && r.out.error.length > 0;
const usage = r => r.code === 2 && r.out?.ok === false && r.out.code === 'USAGE';
const same = (a, b) => JSON.stringify(sortKeys(a)) === JSON.stringify(sortKeys(b));
function sortKeys(value) {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, sortKeys(value[key])]));
  return value;
}
function expect(condition, message) { if (!condition) throw new Error(message); return true; }
const want = (r, expected, label = '') => expect(same(r.out, expected), `${label} got ${r.stdout.trim().slice(0, 300)} (exit ${r.code})`);
const wantErr = (r, code, label = '') => expect(err(r, code), `${label} expected ${code}, got ${r.stdout.trim().slice(0, 200)} (exit ${r.code})`);

// ---------- store helpers ----------
const dbPath = dir => path.join(dir, 'db.json');
const readDb = dir => JSON.parse(fs.readFileSync(dbPath(dir), 'utf8'));
const writeDb = (dir, db) => fs.writeFileSync(dbPath(dir), JSON.stringify(db));
function ready() { const dir = fresh(); const r = lb(dir, 'init'); expect(ok(r), 'init failed: ' + r.stdout); return dir; }
function withStream(name = 's', count = 0, type = 't') {
  const dir = ready();
  expect(ok(lb(dir, 'stream-create', name)), 'stream-create failed');
  for (let i = 0; i < count; i++) expect(ok(lb(dir, 'append', name, type, JSON.stringify({ n: i + 1 }))), 'append failed');
  return dir;
}
// Builds a large store directly; the task says external edits must be honoured.
function bigStream(db, name, count, start = 1) {
  const events = [];
  for (let seq = start; seq < start + count; seq++) { const id = `${name}-${seq}`; events.push({ seq, type: 't', id, payload: { n: seq } }); db.eventIds[id] = { stream: name, seq }; }
  db.streams[name] = { nextSeq: start + count, compactBefore: start, events };
  return db;
}
const emptyDb = () => ({ version: 1, streams: {}, cursors: {}, eventIds: {} });
function invariants(dir) {
  const db = readDb(dir);
  expect(db.version === 1 && db.streams && db.cursors && db.eventIds, 'store structure');
  const ids = new Map();
  for (const [name, stream] of Object.entries(db.streams)) {
    expect(stream.compactBefore >= 1 && stream.nextSeq >= stream.compactBefore, `${name}: counters`);
    expect(stream.events.length === stream.nextSeq - stream.compactBefore, `${name}: ${stream.events.length} events for ${stream.compactBefore}..${stream.nextSeq}`);
    stream.events.forEach((event, i) => { expect(event.seq === stream.compactBefore + i, `${name}: seq gap at ${event.seq}`); expect(!ids.has(event.id), `duplicate id ${event.id}`); ids.set(event.id, { stream: name, seq: event.seq }); });
  }
  expect(Object.keys(db.eventIds).length === ids.size, `eventIds has ${Object.keys(db.eventIds).length} entries for ${ids.size} events`);
  for (const [id, at] of ids) expect(same(db.eventIds[id], at), `eventIds[${id}]`);
  for (const [name, cursor] of Object.entries(db.cursors)) {
    const stream = db.streams[cursor.stream];
    expect(stream, `cursor ${name} points to a missing stream`);
    expect(cursor.pos >= stream.compactBefore - 1, `cursor ${name} behind compaction`);
  }
  return db;
}
const deadPid = () => cp.spawnSync(process.execPath, ['-e', ''], { windowsHide: true }).pid;
const writeFile = (name, text) => { const file = path.join(root, name); fs.writeFileSync(file, text); return file; };
const jsonl = items => items.map(item => typeof item === 'string' ? item : JSON.stringify(item)).join('\n') + '\n';
const ev = (stream, id, type = 't', payload = {}) => ({ stream, type, id, payload });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

const checks = [];
const check = (category, name, run) => checks.push({ category, name, run });

// ---------- deliverables ----------
check('deliverables', 'README.md is present', () => expect(fs.existsSync(path.join(work, 'README.md')), 'no README.md'));
check('deliverables', 'logbook.test.js passes with node --test', () => {
  if (skipSuite) return true;
  expect(fs.existsSync(path.join(work, 'logbook.test.js')), 'no logbook.test.js');
  const r = cp.spawnSync(process.execPath, ['--test', 'logbook.test.js'], { cwd: work, encoding: 'utf8', timeout: 300000, windowsHide: true });
  return expect(r.status === 0 && /# pass\s+[1-9]/.test(r.stdout + r.stderr), `exit ${r.status}: ${(r.stdout + r.stderr).slice(-300)}`);
});

// ---------- command line ----------
check('cli', 'stdout is exactly one JSON line ending in a newline', () => {
  const dir = ready();
  for (const r of [lb(dir, 'status'), lb(dir, 'head', 'missing'), lb(dir, 'frobnicate')]) expect(/^[^\n]+\n$/.test(r.stdout) && r.out, `stdout was ${JSON.stringify(r.stdout)}`);
  return true;
});
check('cli', 'missing datadir, missing command and unknown command are USAGE', () =>
  expect(usage(lb(null)), 'no args') && expect(usage(lb(fresh())), 'no command') && expect(usage(lb(fresh(), 'frobnicate')), 'unknown command'));
check('cli', 'usage errors come before NOT_INITIALIZED', () => {
  const dir = fresh();
  return expect(usage(lb(dir, 'append', 's', 't')), 'append with 2 args') && expect(usage(lb(dir, 'status', 'extra')), 'status with arg') && expect(usage(lb(dir, 'read', 's', '--bogus', '1')), 'unknown flag') && expect(!fs.existsSync(dir), 'datadir was created');
});
check('cli', 'wrong positional counts are USAGE', () => {
  const dir = ready();
  return [['stream-create'], ['stream-create', 'a', 'b'], ['get', 's'], ['compact', 's'], ['cursor-create', 'c'], ['poll'], ['import'], ['head', 'a', 'b'], ['init', 'x']].every(argv => expect(usage(lb(dir, ...argv)), argv.join(' ')));
});
check('cli', 'flags of another command, repeated flags and missing values are USAGE', () => {
  const dir = withStream('s', 1);
  return expect(usage(lb(dir, 'append', 's', 't', '{}', '--force')), '--force on append')
    && expect(usage(lb(dir, 'read', 's', '--id', 'x')), '--id on read')
    && expect(usage(lb(dir, 'poll', 'c', '--from', '1')), '--from on poll')
    && expect(usage(lb(dir, 'stream-delete', 's', '--force', '--force')), 'repeated --force')
    && expect(usage(lb(dir, 'read', 's', '--count', '1', '--count', '2')), 'repeated --count')
    && expect(usage(lb(dir, 'read', 's', '--from')), 'value missing')
    && expect(usage(lb(dir, 'stream-delete', 's', '--force=yes')), '--force=yes')
    && expect(readDb(dir).streams.s.nextSeq === 2, 'store changed');
});
check('cli', 'flags may appear anywhere after the command', () => {
  const dir = withStream('s');
  want(lb(dir, 'append', '--id', 'k1', 's', 't', '{}'), { ok: true, stream: 's', seq: 1, id: 'k1' }, 'flag first');
  want(lb(dir, 'append', 's', '--id', 'k2', 't', '{}'), { ok: true, stream: 's', seq: 2, id: 'k2' }, 'flag between');
  return expect(lb(dir, 'read', '--count', '1', 's', '--from', '2').out?.events?.[0]?.id === 'k2', 'read flags');
});
check('cli', 'a value flag takes the next argument even if it starts with --', () => {
  const dir = withStream('s');
  want(lb(dir, 'append', 's', 't', '{}', '--id', '--force'), { ok: true, stream: 's', seq: 1, id: '--force' });
  return wantErr(lb(dir, 'read', 's', '--count', '--from'), 'INVALID_COUNT');
});
check('cli', 'NOT_INITIALIZED for a missing directory and for a directory without db.json', () => {
  const missing = fresh(), empty = fresh(); fs.mkdirSync(empty);
  for (const argv of [['status'], ['stream-list'], ['append', 's', 't', '{}'], ['poll', 'c'], ['import', 'nofile']]) { wantErr(lb(missing, ...argv), 'NOT_INITIALIZED', argv[0]); wantErr(lb(empty, ...argv), 'NOT_INITIALIZED', argv[0]); }
  return expect(lb(missing, 'status').out.error === 'Store not initialized', 'message');
});
check('cli', 'error output has ok false, a message and a code', () => {
  const r = lb(ready(), 'head', 'nope');
  return expect(r.code === 1 && same(Object.keys(r.out).sort(), ['code', 'error', 'ok']), r.stdout);
});

// ---------- init and store file ----------
check('store', 'init creates nested directories and the exact empty store', () => {
  const dir = path.join(fresh(), 'a', 'b');
  want(lb(dir, 'init'), { ok: true, version: 1 });
  return expect(same(readDb(dir), emptyDb()), JSON.stringify(readDb(dir))) && expect(!fs.existsSync(path.join(dir, 'lock')), 'lock left behind');
});
check('store', 'init twice is ALREADY_INITIALIZED and leaves data untouched', () => {
  const dir = withStream('s', 2);
  const before = fs.readFileSync(dbPath(dir));
  wantErr(lb(dir, 'init'), 'ALREADY_INITIALIZED');
  fs.writeFileSync(dbPath(dir), 'not json');
  wantErr(lb(dir, 'init'), 'ALREADY_INITIALIZED', 'corrupt store');
  fs.writeFileSync(dbPath(dir), before);
  return expect(fs.readFileSync(dbPath(dir)).equals(before), 'changed');
});
check('store', 'an external edit made while unlocked is honoured', () => {
  const dir = withStream('s', 1);
  const db = readDb(dir);
  db.streams.s.events.push({ seq: 2, type: 'edited', id: 'manual', payload: { x: 1 } }); db.streams.s.nextSeq = 3; db.eventIds.manual = { stream: 's', seq: 2 };
  db.streams.other = { nextSeq: 1, compactBefore: 1, events: [] };
  writeDb(dir, db);
  want(lb(dir, 'get', 's', '2'), { ok: true, event: { seq: 2, type: 'edited', id: 'manual', payload: { x: 1 } } });
  want(lb(dir, 'append', 's', 't', '{}'), { ok: true, stream: 's', seq: 3, id: 's-3' });
  return wantErr(lb(dir, 'append', 'other', 't', '{}', '--id', 'manual'), 'DUPLICATE_ID');
});
check('store', 'a corrupt db.json is CORRUPT_STORE and stays untouched', () => {
  const dir = ready();
  for (const text of ['{', '[]', '{"version":2,"streams":{},"cursors":{},"eventIds":{}}', '{"version":1,"streams":{},"cursors":{}}', '{"version":1,"streams":[],"cursors":{},"eventIds":{}}', 'null']) {
    fs.writeFileSync(dbPath(dir), text);
    wantErr(lb(dir, 'status'), 'CORRUPT_STORE', text);
    wantErr(lb(dir, 'stream-create', 'x'), 'CORRUPT_STORE', text);
    expect(fs.readFileSync(dbPath(dir), 'utf8') === text, 'file changed for ' + text);
    expect(!fs.existsSync(path.join(dir, 'lock')), 'lock left behind');
  }
  return true;
});
check('store', 'a leftover db.json.tmp is ignored and overwritten', () => {
  const dir = withStream('s', 1);
  fs.writeFileSync(path.join(dir, 'db.json.tmp'), '{"garbage":');
  want(lb(dir, 'head', 's'), { ok: true, stream: 's', nextSeq: 2, count: 1, compactBefore: 1 });
  return expect(ok(lb(dir, 'append', 's', 't', '{}')), 'append') && expect(readDb(dir).streams.s.nextSeq === 3, 'not saved');
});
check('store', 'read-only commands do not rewrite db.json', () => {
  const dir = withStream('s', 3);
  lb(dir, 'cursor-create', 'c', 's');
  const old = new Date('2001-01-01T00:00:00Z');
  fs.utimesSync(dbPath(dir), old, old);
  const before = fs.readFileSync(dbPath(dir));
  for (const argv of [['stream-list'], ['read', 's'], ['get', 's', '1'], ['head', 's'], ['cursor-list'], ['status'], ['read', 's', '--from', '9'], ['get', 's', '9']]) lb(dir, ...argv);
  return expect(fs.statSync(dbPath(dir)).mtimeMs === old.getTime(), 'mtime changed') && expect(fs.readFileSync(dbPath(dir)).equals(before), 'content changed');
});
check('store', 'db.json is replaced by a rename, never rewritten in place', () => {
  const dir = withStream('s', 1);
  const identity = () => fs.statSync(dbPath(dir), { bigint: true }).ino;
  for (const argv of [['append', 's', 't', '{}'], ['stream-create', 'x'], ['cursor-create', 'c', 's'], ['poll', 'c'], ['compact', 's', '2'], ['import', writeFile('ino.jsonl', jsonl([ev('s', 'ino1')]))]]) {
    const before = identity();
    expect(ok(lb(dir, ...argv)), argv[0] + ' failed');
    expect(identity() !== before, argv[0] + ' rewrote db.json in place (same file identity)');
  }
  return true;
});
check('store', 'the lock is removed after success and after errors', () => {
  const dir = withStream('s', 1);
  for (const argv of [['append', 's', 't', '{}'], ['append', 's', 'BAD', '{}'], ['head', 'nope'], ['get', 's', '0'], ['status']]) { lb(dir, ...argv); expect(!fs.existsSync(path.join(dir, 'lock')), 'lock left after ' + argv.join(' ')); }
  return true;
});
check('store', 'names such as constructor and ids such as __proto__ are ordinary keys', () => {
  const dir = ready();
  wantErr(lb(dir, 'head', 'constructor'), 'STREAM_NOT_FOUND', 'before creation');
  wantErr(lb(dir, 'append', 'tostring', 't', '{}'), 'STREAM_NOT_FOUND', 'tostring');
  wantErr(lb(dir, 'poll', 'constructor'), 'NOT_FOUND', 'cursor');
  want(lb(dir, 'stream-create', 'constructor'), { ok: true, stream: 'constructor' });
  want(lb(dir, 'append', 'constructor', 't', '{"__proto__":{"x":1}}', '--id', '__proto__'), { ok: true, stream: 'constructor', seq: 1, id: '__proto__' });
  want(lb(dir, 'append', 'constructor', 't', '{"__proto__":{"x":1}}', '--id', '__proto__'), { ok: true, stream: 'constructor', seq: 1, id: '__proto__', idempotent: true }, 'idempotent');
  wantErr(lb(dir, 'append', 'constructor', 't', '{}', '--id', '__proto__'), 'DUPLICATE_ID');
  want(lb(dir, 'cursor-create', 'hasownproperty', 'constructor'), { ok: true, cursor: 'hasownproperty', stream: 'constructor', pos: 0 });
  const raw = fs.readFileSync(dbPath(dir), 'utf8');
  expect(/"eventIds":\{"__proto__":\{"stream":"constructor","seq":1\}\}/.test(raw.replace(/\s/g, '')), 'eventIds in db.json: ' + raw.slice(0, 300));
  want(lb(dir, 'get', 'constructor', '1'), { ok: true, event: { seq: 1, type: 't', id: '__proto__', payload: JSON.parse('{"__proto__":{"x":1}}') } });
  invariants(dir);
  return true;
});

// ---------- streams ----------
check('streams', 'stream names follow the naming rule', () => {
  const dir = ready();
  for (const bad of ['', 'A', 'Orders', '-a', '_a', 'a.b', 'a b', 'x'.repeat(64), 'é']) wantErr(lb(dir, 'stream-create', bad), 'INVALID_NAME', JSON.stringify(bad));
  for (const good of ['a', '0', 'a_b-c', 'x'.repeat(63)]) want(lb(dir, 'stream-create', good), { ok: true, stream: good }, good);
  return wantErr(lb(dir, 'stream-create', 'a'), 'ALREADY_EXISTS');
});
check('streams', 'stream-list is sorted by code unit and counts stored events', () => {
  const dir = ready();
  for (const name of ['b', 'a_1', 'a-1', 'a']) lb(dir, 'stream-create', name);
  for (let i = 0; i < 3; i++) lb(dir, 'append', 'b', 't', '{}');
  lb(dir, 'compact', 'b', '2');
  return want(lb(dir, 'stream-list'), { ok: true, count: 4, streams: [
    { name: 'a', nextSeq: 1, count: 0, compactBefore: 1 }, { name: 'a-1', nextSeq: 1, count: 0, compactBefore: 1 },
    { name: 'a_1', nextSeq: 1, count: 0, compactBefore: 1 }, { name: 'b', nextSeq: 4, count: 2, compactBefore: 2 }] });
});
check('streams', 'head gives exactly its four fields', () => {
  const dir = withStream('s', 2);
  want(lb(dir, 'head', 's'), { ok: true, stream: 's', nextSeq: 3, count: 2, compactBefore: 1 });
  return wantErr(lb(dir, 'head', 'S'), 'STREAM_NOT_FOUND', 'malformed name');
});
check('streams', 'stream-delete checks events, then cursors, and --force removes everything', () => {
  const dir = withStream('s', 2);
  lb(dir, 'cursor-create', 'c1', 's'); lb(dir, 'cursor-create', 'c2', 's');
  lb(dir, 'stream-create', 'other'); lb(dir, 'cursor-create', 'keep', 'other');
  wantErr(lb(dir, 'stream-delete', 's'), 'HAS_EVENTS');
  wantErr(lb(dir, 'stream-delete', 'nope'), 'NOT_FOUND');
  wantErr(lb(dir, 'stream-delete', 'nope', '--force'), 'NOT_FOUND', 'with force');
  want(lb(dir, 'stream-delete', 's', '--force'), { ok: true, stream: 's', eventsDeleted: 2, cursorsDeleted: 2 });
  want(lb(dir, 'cursor-list'), { ok: true, count: 1, cursors: [{ name: 'keep', stream: 'other', pos: 0 }] });
  want(lb(dir, 'append', 'other', 't', '{}', '--id', 's-1'), { ok: true, stream: 'other', seq: 1, id: 's-1' }, 'freed id');
  lb(dir, 'stream-create', 'empty'); lb(dir, 'cursor-create', 'e1', 'empty');
  wantErr(lb(dir, 'stream-delete', 'empty'), 'HAS_CURSORS');
  lb(dir, 'cursor-delete', 'e1');
  want(lb(dir, 'stream-delete', 'empty'), { ok: true, stream: 'empty', eventsDeleted: 0, cursorsDeleted: 0 });
  invariants(dir);
  return true;
});
check('streams', 'a stream emptied by compaction can be deleted without --force', () => {
  const dir = withStream('s', 2);
  lb(dir, 'compact', 's', '3');
  return want(lb(dir, 'stream-delete', 's'), { ok: true, stream: 's', eventsDeleted: 0, cursorsDeleted: 0 });
});
check('streams', 'status sums stored and compacted events', () => {
  const dir = withStream('a', 4);
  lb(dir, 'stream-create', 'b'); lb(dir, 'append', 'b', 't', '{}');
  lb(dir, 'compact', 'a', '3'); lb(dir, 'cursor-create', 'c', 'a');
  return want(lb(dir, 'status'), { ok: true, streams: 2, cursors: 1, totalEvents: 3, compactedEvents: 2 });
});

// ---------- append ----------
check('append', 'append assigns seqs and default ids', () => {
  const dir = withStream('orders');
  want(lb(dir, 'append', 'orders', 'created', '{"total":5}'), { ok: true, stream: 'orders', seq: 1, id: 'orders-1' });
  want(lb(dir, 'append', 'orders', 'paid', '{}', '--id', 'pay.1'), { ok: true, stream: 'orders', seq: 2, id: 'pay.1' });
  want(lb(dir, 'append', 'orders', 'x', '{}'), { ok: true, stream: 'orders', seq: 3, id: 'orders-3' });
  want(lb(dir, 'get', 'orders', '1'), { ok: true, event: { seq: 1, type: 'created', id: 'orders-1', payload: { total: 5 } } });
  invariants(dir);
  return true;
});
check('append', 'append checks stream, type, payload, id, conflict, limit in that order', () => {
  const dir = withStream('s');
  lb(dir, 'append', 's', 't', '{}', '--id', 'used');
  wantErr(lb(dir, 'append', 'nope', 'BAD', 'x', '--id', 'bad id'), 'STREAM_NOT_FOUND');
  wantErr(lb(dir, 'append', 's', 'BAD', 'x', '--id', 'bad id'), 'INVALID_TYPE');
  wantErr(lb(dir, 'append', 's', 't', 'x', '--id', 'bad id'), 'INVALID_PAYLOAD');
  wantErr(lb(dir, 'append', 's', 't', '{}', '--id', 'bad id'), 'INVALID_ID');
  return wantErr(lb(dir, 'append', 's', 'u', '{}', '--id', 'used'), 'DUPLICATE_ID');
});
check('append', 'type, id and payload rules', () => {
  const dir = withStream('s');
  for (const bad of ['', 'A', 'a_b', '-a', 'x'.repeat(65), 'a.b']) wantErr(lb(dir, 'append', 's', bad, '{}'), 'INVALID_TYPE', JSON.stringify(bad));
  for (const bad of ['', 'a b', 'a/b', 'x'.repeat(129), 'é']) wantErr(lb(dir, 'append', 's', 't', '{}', '--id', bad), 'INVALID_ID', JSON.stringify(bad));
  for (const bad of ['[]', 'null', '"x"', '1', 'true', '{', '', '{"a":1}x']) wantErr(lb(dir, 'append', 's', 't', bad), 'INVALID_PAYLOAD', JSON.stringify(bad));
  expect(ok(lb(dir, 'append', 's', 'x'.repeat(64), '{"deep":{"list":[1,{"a":null}]}}', '--id', 'X'.repeat(128))), 'longest valid');
  return expect(readDb(dir).streams.s.nextSeq === 2, 'failed appends changed the store');
});
check('append', 'idempotent append uses deep equality', () => {
  const dir = withStream('s');
  lb(dir, 'stream-create', 'other');
  want(lb(dir, 'append', 's', 't', '{"a":1,"b":{"c":[1,2],"d":"x"}}', '--id', 'k'), { ok: true, stream: 's', seq: 1, id: 'k' });
  lb(dir, 'append', 's', 't', '{}');
  want(lb(dir, 'append', 's', 't', '{"b":{"d":"x","c":[1,2]},"a":1.0}', '--id', 'k'), { ok: true, stream: 's', seq: 1, id: 'k', idempotent: true });
  wantErr(lb(dir, 'append', 's', 't', '{"a":1,"b":{"c":[2,1],"d":"x"}}', '--id', 'k'), 'DUPLICATE_ID', 'array order');
  wantErr(lb(dir, 'append', 's', 'u', '{"a":1,"b":{"c":[1,2],"d":"x"}}', '--id', 'k'), 'DUPLICATE_ID', 'other type');
  wantErr(lb(dir, 'append', 'other', 't', '{"a":1,"b":{"c":[1,2],"d":"x"}}', '--id', 'k'), 'DUPLICATE_ID', 'other stream');
  wantErr(lb(dir, 'append', 's', 't', '{"a":1,"b":{"c":[1,2],"d":"x"},"e":null}', '--id', 'k'), 'DUPLICATE_ID', 'extra key');
  return want(lb(dir, 'head', 's'), { ok: true, stream: 's', nextSeq: 3, count: 2, compactBefore: 1 });
});
check('append', 'a taken default id is DUPLICATE_ID and changes nothing', () => {
  const dir = withStream('s');
  lb(dir, 'append', 's', 't', '{}', '--id', 's-2');
  wantErr(lb(dir, 'append', 's', 't', '{}'), 'DUPLICATE_ID');
  want(lb(dir, 'head', 's'), { ok: true, stream: 's', nextSeq: 2, count: 1, compactBefore: 1 });
  return want(lb(dir, 'append', 's', 't', '{}', '--id', 'z'), { ok: true, stream: 's', seq: 2, id: 'z' });
});
check('append', 'seqs are never reused after compaction', () => {
  const dir = withStream('s', 3);
  lb(dir, 'compact', 's', '4');
  want(lb(dir, 'append', 's', 't', '{}'), { ok: true, stream: 's', seq: 4, id: 's-4' });
  return want(lb(dir, 'append', 's', 't', '{}', '--id', 's-1'), { ok: true, stream: 's', seq: 5, id: 's-1' }, 'freed id');
});

// ---------- read and get ----------
check('read', 'read returns events, honours --from and --count, and reports hasMore', () => {
  const dir = withStream('s', 5);
  const all = lb(dir, 'read', 's').out;
  expect(all?.count === 5 && all.hasMore === false && all.events.map(e => e.seq).join() === '1,2,3,4,5', 'all: ' + JSON.stringify(all));
  expect(same(all.events[0], { seq: 1, type: 't', id: 's-1', payload: { n: 1 } }), 'event shape');
  want(lb(dir, 'read', 's', '--from', '2', '--count', '2'), { ok: true, stream: 's', count: 2, hasMore: true, events: [
    { seq: 2, type: 't', id: 's-2', payload: { n: 2 } }, { seq: 3, type: 't', id: 's-3', payload: { n: 3 } }] });
  expect(lb(dir, 'read', 's', '--from', '4', '--count', '2').out?.hasMore === false, 'hasMore at the end');
  want(lb(dir, 'read', 's', '--from', '6'), { ok: true, stream: 's', events: [], count: 0, hasMore: false });
  return want(lb(dir, 'read', 's', '--from', '999999'), { ok: true, stream: 's', events: [], count: 0, hasMore: false });
});
check('read', 'read errors and their order', () => {
  const dir = withStream('s', 5);
  lb(dir, 'compact', 's', '3');
  wantErr(lb(dir, 'read', 'nope', '--from', '0'), 'STREAM_NOT_FOUND');
  for (const bad of ['0', '-1', '01', '+1', '1.0', 'abc', ' 1', '1e3', '9007199254740992']) wantErr(lb(dir, 'read', 's', '--from', bad), 'INVALID_SEQ', bad);
  for (const bad of ['0', '-2', 'x', '02']) wantErr(lb(dir, 'read', 's', '--count', bad), 'INVALID_COUNT', bad);
  wantErr(lb(dir, 'read', 's', '--from', '0', '--count', '0'), 'INVALID_SEQ', 'seq before count');
  wantErr(lb(dir, 'read', 's', '--from', '1', '--count', '0'), 'INVALID_COUNT', 'count before compacted');
  wantErr(lb(dir, 'read', 's', '--from', '2'), 'COMPACTED');
  return expect(lb(dir, 'read', 's').out?.events?.[0]?.seq === 3, 'default from is compactBefore');
});
check('read', 'read returns at most 1000 events', () => {
  const dir = ready();
  writeDb(dir, bigStream(emptyDb(), 's', 1200));
  const r = lb(dir, 'read', 's');
  expect(r.out?.count === 1000 && r.out.events.length === 1000 && r.out.hasMore === true && r.out.events[999].seq === 1000, 'default cap');
  const big = lb(dir, 'read', 's', '--count', '5000');
  expect(big.out?.count === 1000 && big.out.hasMore === true, 'count above 1000');
  const rest = lb(dir, 'read', 's', '--from', '1001', '--count', '5000');
  return expect(rest.out?.count === 200 && rest.out.hasMore === false, 'last page');
});
check('read', 'get returns one event and checks seq, compaction and range', () => {
  const dir = withStream('s', 4);
  lb(dir, 'compact', 's', '3');
  want(lb(dir, 'get', 's', '3'), { ok: true, event: { seq: 3, type: 't', id: 's-3', payload: { n: 3 } } });
  wantErr(lb(dir, 'get', 'nope', '0'), 'STREAM_NOT_FOUND');
  for (const bad of ['0', '-1', '01', '+3', 'x', '3.0']) wantErr(lb(dir, 'get', 's', bad), 'INVALID_SEQ', bad);
  wantErr(lb(dir, 'get', 's', '2'), 'COMPACTED');
  wantErr(lb(dir, 'get', 's', '5'), 'NOT_FOUND');
  return wantErr(lb(dir, 'get', 's', '9007199254740991'), 'NOT_FOUND', 'max safe integer');
});

// ---------- compaction ----------
check('compact', 'compact removes events, frees ids and raises cursors', () => {
  const dir = withStream('s', 5);
  lb(dir, 'cursor-create', 'behind', 's'); lb(dir, 'cursor-create', 'ahead', 's');
  lb(dir, 'poll', 'ahead', '--count', '4');
  want(lb(dir, 'compact', 's', '3'), { ok: true, stream: 's', compactBefore: 3, removed: 2 });
  const cursors = lb(dir, 'cursor-list').out?.cursors;
  expect(same(cursors, [{ name: 'ahead', stream: 's', pos: 4 }, { name: 'behind', stream: 's', pos: 2 }]), JSON.stringify(cursors));
  want(lb(dir, 'append', 's', 't', '{}', '--id', 's-1'), { ok: true, stream: 's', seq: 6, id: 's-1' }, 'freed id');
  invariants(dir);
  return true;
});
check('compact', 'compact errors, the no-op and emptying the stream', () => {
  const dir = withStream('s', 3);
  wantErr(lb(dir, 'compact', 'nope', '0'), 'STREAM_NOT_FOUND');
  wantErr(lb(dir, 'compact', 's', '0'), 'INVALID_SEQ');
  wantErr(lb(dir, 'compact', 's', '5'), 'FUTURE_SEQ');
  want(lb(dir, 'compact', 's', '1'), { ok: true, stream: 's', compactBefore: 1, removed: 0 }, 'no-op');
  want(lb(dir, 'compact', 's', '2'), { ok: true, stream: 's', compactBefore: 2, removed: 1 });
  wantErr(lb(dir, 'compact', 's', '1'), 'ALREADY_COMPACTED');
  want(lb(dir, 'compact', 's', '2'), { ok: true, stream: 's', compactBefore: 2, removed: 0 }, 'no-op again');
  want(lb(dir, 'compact', 's', '4'), { ok: true, stream: 's', compactBefore: 4, removed: 2 }, 'empties');
  want(lb(dir, 'head', 's'), { ok: true, stream: 's', nextSeq: 4, count: 0, compactBefore: 4 });
  want(lb(dir, 'append', 's', 't', '{}'), { ok: true, stream: 's', seq: 4, id: 's-4' });
  invariants(dir);
  return true;
});

// ---------- cursors and poll ----------
check('cursors', 'cursor-create, cursor-list and cursor-delete', () => {
  const dir = withStream('s', 2);
  lb(dir, 'compact', 's', '2');
  want(lb(dir, 'cursor-create', 'b', 's'), { ok: true, cursor: 'b', stream: 's', pos: 0 });
  wantErr(lb(dir, 'cursor-create', 'Bad', 'nope'), 'INVALID_NAME');
  wantErr(lb(dir, 'cursor-create', 'b', 'nope'), 'ALREADY_EXISTS');
  wantErr(lb(dir, 'cursor-create', 'c', 'nope'), 'STREAM_NOT_FOUND');
  wantErr(lb(dir, 'cursor-create', 'c', 'S'), 'STREAM_NOT_FOUND', 'malformed stream name');
  lb(dir, 'cursor-create', 'a-1', 's'); lb(dir, 'cursor-create', 'a_1', 's');
  want(lb(dir, 'cursor-list'), { ok: true, count: 3, cursors: [{ name: 'a-1', stream: 's', pos: 0 }, { name: 'a_1', stream: 's', pos: 0 }, { name: 'b', stream: 's', pos: 0 }] });
  want(lb(dir, 'cursor-delete', 'b'), { ok: true, cursor: 'b' });
  wantErr(lb(dir, 'cursor-delete', 'b'), 'NOT_FOUND');
  return wantErr(lb(dir, 'cursor-delete', 'B'), 'NOT_FOUND', 'malformed');
});
check('cursors', 'poll delivers in order, persists the position and reports hasMore', () => {
  const dir = withStream('s', 25);
  lb(dir, 'cursor-create', 'c', 's');
  const first = lb(dir, 'poll', 'c').out;
  expect(first?.count === 10 && first.newPos === 10 && first.hasMore === true && first.stream === 's' && first.cursor === 'c' && first.events[0].seq === 1, 'default 10: ' + JSON.stringify(first)?.slice(0, 200));
  expect(same(first.events[0], { seq: 1, type: 't', id: 's-1', payload: { n: 1 } }), 'event shape');
  const second = lb(dir, 'poll', 'c', '--count', '20').out;
  expect(second?.count === 15 && second.newPos === 25 && second.hasMore === false, 'second');
  want(lb(dir, 'poll', 'c'), { ok: true, cursor: 'c', stream: 's', events: [], count: 0, newPos: 25, hasMore: false });
  return want(lb(dir, 'cursor-list'), { ok: true, count: 1, cursors: [{ name: 'c', stream: 's', pos: 25 }] });
});
check('cursors', 'poll after compaction starts from compactBefore', () => {
  const dir = withStream('s', 6);
  lb(dir, 'cursor-create', 'c', 's');
  const db = readDb(dir); db.streams.s.events = db.streams.s.events.filter(e => e.seq >= 4); db.streams.s.compactBefore = 4;
  for (const id of ['s-1', 's-2', 's-3']) delete db.eventIds[id];
  writeDb(dir, db); // cursor left at 0 by an external tool
  const r = lb(dir, 'poll', 'c', '--count', '2').out;
  expect(r?.events?.map(e => e.seq).join() === '4,5' && r.newPos === 5 && r.hasMore === true, JSON.stringify(r)?.slice(0, 200));
  lb(dir, 'compact', 's', '7');
  return want(lb(dir, 'poll', 'c'), { ok: true, cursor: 'c', stream: 's', events: [], count: 0, newPos: 6, hasMore: false });
});
check('cursors', 'poll errors and the count cap', () => {
  const dir = ready();
  writeDb(dir, bigStream(emptyDb(), 's', 1500));
  lb(dir, 'cursor-create', 'c', 's');
  wantErr(lb(dir, 'poll', 'nope', '--count', '0'), 'NOT_FOUND');
  for (const bad of ['0', 'x', '-5', '07']) wantErr(lb(dir, 'poll', 'c', '--count', bad), 'INVALID_COUNT', bad);
  const r = lb(dir, 'poll', 'c', '--count', '5000').out;
  return expect(r?.count === 1000 && r.newPos === 1000 && r.hasMore === true, 'cap: ' + JSON.stringify({ count: r?.count, newPos: r?.newPos }));
});

// ---------- limits ----------
check('limits', '100 streams at most', () => {
  const dir = ready(); const db = emptyDb();
  for (let i = 0; i < 100; i++) db.streams['s' + i] = { nextSeq: 1, compactBefore: 1, events: [] };
  writeDb(dir, db);
  wantErr(lb(dir, 'stream-create', 'one-more'), 'LIMIT_EXCEEDED');
  wantErr(lb(dir, 'stream-create', 's1'), 'ALREADY_EXISTS', 'exists before limit');
  lb(dir, 'stream-delete', 's1');
  return expect(ok(lb(dir, 'stream-create', 'one-more')), 'after delete');
});
check('limits', '10000 stored events per stream at most, idempotent appends still succeed', () => {
  const dir = ready();
  writeDb(dir, bigStream(emptyDb(), 's', 10000));
  wantErr(lb(dir, 'append', 's', 't', '{}', '--id', 'new'), 'LIMIT_EXCEEDED');
  wantErr(lb(dir, 'append', 's', 't', '{}', '--id', 's-5'), 'DUPLICATE_ID', 'conflict before limit');
  want(lb(dir, 'append', 's', 't', '{"n":5}', '--id', 's-5'), { ok: true, stream: 's', seq: 5, id: 's-5', idempotent: true });
  lb(dir, 'compact', 's', '2');
  return want(lb(dir, 'append', 's', 't', '{}'), { ok: true, stream: 's', seq: 10001, id: 's-10001' });
});
check('limits', '1000 cursors at most', () => {
  const dir = ready(); const db = emptyDb();
  db.streams.s = { nextSeq: 1, compactBefore: 1, events: [] };
  for (let i = 0; i < 1000; i++) db.cursors['c' + i] = { stream: 's', pos: 0 };
  writeDb(dir, db);
  wantErr(lb(dir, 'cursor-create', 'extra', 's'), 'LIMIT_EXCEEDED');
  return wantErr(lb(dir, 'cursor-create', 'extra', 'nope'), 'STREAM_NOT_FOUND', 'stream checked before limit');
});

// ---------- import ----------
check('import', 'import appends in file order and creates streams', () => {
  const dir = withStream('b', 1);
  const file = writeFile('i1.jsonl', jsonl([ev('b', 'b1'), ev('zz', 'z1', 'x', { k: [1] }), '', '   ', ev('a', 'a1'), { ...ev('b', 'b2'), seq: 99, extra: true }]).replace(/\n/g, '\r\n'));
  want(lb(dir, 'import', file), { ok: true, imported: 4, skipped: 0, streamsCreated: ['a', 'zz'] });
  want(lb(dir, 'read', 'b'), { ok: true, stream: 'b', count: 3, hasMore: false, events: [
    { seq: 1, type: 't', id: 'b-1', payload: { n: 1 } }, { seq: 2, type: 't', id: 'b1', payload: {} }, { seq: 3, type: 't', id: 'b2', payload: {} }] });
  want(lb(dir, 'get', 'zz', '1'), { ok: true, event: { seq: 1, type: 'x', id: 'z1', payload: { k: [1] } } });
  want(lb(dir, 'import', file), { ok: true, imported: 0, skipped: 4, streamsCreated: [] }, 'second run');
  invariants(dir);
  return true;
});
check('import', 'import paths are relative to the working directory', () => {
  const dir = ready();
  const folder = path.join(root, 'cwd-' + serial); fs.mkdirSync(folder);
  fs.writeFileSync(path.join(folder, 'rel.jsonl'), jsonl([ev('s', 'r1')]));
  return want(lb(dir, { cwd: folder }, 'import', 'rel.jsonl'), { ok: true, imported: 1, skipped: 0, streamsCreated: ['s'] });
});
check('import', 'an empty file imports nothing', () => want(lb(ready(), 'import', writeFile('empty.jsonl', '\n\n')), { ok: true, imported: 0, skipped: 0, streamsCreated: [] }));
check('import', 'each import error aborts the whole import', () => {
  const dir = withStream('s', 1);
  const before = fs.readFileSync(dbPath(dir), 'utf8');
  const cases = [
    ['INVALID_FILE', null], ['INVALID_FORMAT', '{bad json'], ['INVALID_FORMAT', '[1,2]'], ['INVALID_FORMAT', '"text"'],
    ['INVALID_FORMAT', JSON.stringify({ stream: 's', type: 't', id: 'x' })], ['INVALID_NAME', JSON.stringify(ev('Bad', 'x'))],
    ['INVALID_NAME', JSON.stringify({ stream: 5, type: 't', id: 'x', payload: {} })], ['INVALID_TYPE', JSON.stringify(ev('s', 'x', 'a_b'))],
    ['INVALID_PAYLOAD', JSON.stringify({ stream: 's', type: 't', id: 'x', payload: [] })], ['INVALID_PAYLOAD', JSON.stringify({ stream: 's', type: 't', id: 'x', payload: null })],
    ['INVALID_ID', JSON.stringify(ev('s', 'a b'))], ['INVALID_ID', JSON.stringify({ stream: 's', type: 't', id: 7, payload: {} })],
    ['DUPLICATE_ID', JSON.stringify(ev('s', 's-1', 't', { other: 1 }))], ['DUPLICATE_ID', JSON.stringify(ev('new', 's-1', 't', { n: 1 }))]
  ];
  for (const [code, badLine] of cases) {
    const file = badLine === null ? path.join(root, 'does-not-exist.jsonl') : writeFile(`bad-${serial++}.jsonl`, jsonl([ev('s', 'good1'), ev('fresh', 'good2'), badLine]));
    wantErr(lb(dir, 'import', file), code, badLine || 'missing file');
    expect(fs.readFileSync(dbPath(dir), 'utf8') === before, 'store changed by ' + code);
  }
  wantErr(lb(dir, 'import', root), 'INVALID_FILE', 'a directory');
  return true;
});
check('import', 'within a line the checks run name, type, payload, id; the first bad line wins', () => {
  const dir = withStream('s');
  wantErr(lb(dir, 'import', writeFile('o1.jsonl', JSON.stringify({ stream: 'B', type: 'B', id: 'b b', payload: 1 }))), 'INVALID_NAME');
  wantErr(lb(dir, 'import', writeFile('o2.jsonl', JSON.stringify({ stream: 's', type: 'B', id: 'b b', payload: 1 }))), 'INVALID_TYPE');
  wantErr(lb(dir, 'import', writeFile('o3.jsonl', JSON.stringify({ stream: 's', type: 't', id: 'b b', payload: 1 }))), 'INVALID_PAYLOAD');
  wantErr(lb(dir, 'import', writeFile('o4.jsonl', jsonl([ev('s', 'a'), ev('s', 'b', 'B_'), ev('S', 'c')]))), 'INVALID_TYPE', 'line order');
  const r = lb(dir, 'import', writeFile('o5.jsonl', jsonl([ev('s', 'a'), '', ev('s', 'b', 'B_')])));
  return expect(/\b3\b/.test(r.out?.error || ''), 'message should name line 3: ' + r.out?.error);
});
check('import', 'a repeated id in the file is DUPLICATE_ID even with identical data', () => {
  const dir = withStream('s', 1);
  wantErr(lb(dir, 'import', writeFile('d1.jsonl', jsonl([ev('s', 'x'), ev('s', 'x')]))), 'DUPLICATE_ID');
  wantErr(lb(dir, 'import', writeFile('d2.jsonl', jsonl([ev('s', 's-1', 't', { n: 1 }), ev('s', 's-1', 't', { n: 1 })]))), 'DUPLICATE_ID', 'stored and repeated');
  return want(lb(dir, 'status'), { ok: true, streams: 1, cursors: 0, totalEvents: 1, compactedEvents: 0 });
});
check('import', 'import limits are counted line by line, skipped lines do not count', () => {
  let dir = ready(); let db = emptyDb();
  for (let i = 0; i < 99; i++) db.streams['s' + i] = { nextSeq: 1, compactBefore: 1, events: [] };
  writeDb(dir, db);
  wantErr(lb(dir, 'import', writeFile('l1.jsonl', jsonl([ev('new1', 'a'), ev('s0', 'b'), ev('new2', 'c'), ev('BAD', 'd')]))), 'LIMIT_EXCEEDED', 'stream limit before later bad name');
  want(lb(dir, 'import', writeFile('l2.jsonl', jsonl([ev('new1', 'a'), ev('new1', 'b')]))), { ok: true, imported: 2, skipped: 0, streamsCreated: ['new1'] });
  dir = ready(); db = bigStream(emptyDb(), 's', 9999); writeDb(dir, db);
  wantErr(lb(dir, 'import', writeFile('l3.jsonl', jsonl([ev('s', 'a'), ev('s', 'b')]))), 'LIMIT_EXCEEDED', 'event limit');
  expect(readDb(dir).streams.s.nextSeq === 10000, 'partial import');
  want(lb(dir, 'import', writeFile('l4.jsonl', jsonl([ev('s', 's-1', 't', { n: 1 }), ev('s', 'a')]))), { ok: true, imported: 1, skipped: 1, streamsCreated: [] });
  return want(lb(dir, 'import', writeFile('l5.jsonl', jsonl([ev('s', 's-2', 't', { n: 2 })]))), { ok: true, imported: 0, skipped: 1, streamsCreated: [] }, 'skip at the limit');
});
check('import', 'import ids collide with later default ids', () => {
  const dir = withStream('s');
  lb(dir, 'import', writeFile('c1.jsonl', jsonl([ev('s', 's-2')])));
  return wantErr(lb(dir, 'append', 's', 't', '{}'), 'DUPLICATE_ID');
});

// ---------- locks ----------
check('locks', 'a lock left by a dead process is taken over', () => {
  const dir = withStream('s', 1);
  fs.writeFileSync(path.join(dir, 'lock'), `${deadPid()}\n`);
  want(lb(dir, 'append', 's', 't', '{}'), { ok: true, stream: 's', seq: 2, id: 's-2' });
  return expect(!fs.existsSync(path.join(dir, 'lock')), 'lock left behind');
});
check('locks', 'init works with a stale lock in a fresh directory', () => {
  const dir = fresh(); fs.mkdirSync(dir);
  fs.writeFileSync(path.join(dir, 'lock'), `${deadPid()}\n`);
  return want(lb(dir, 'init'), { ok: true, version: 1 }) && expect(same(readDb(dir), emptyDb()), 'store');
});
check('locks', 'a lock held by a live process gives LOCK_TIMEOUT after about 5 seconds', async () => {
  const dir = withStream('s', 1);
  const holder = cp.spawn(process.execPath, ['-e', 'setTimeout(() => {}, 30000)'], { windowsHide: true });
  try {
    fs.writeFileSync(path.join(dir, 'lock'), `${holder.pid}\n`);
    const before = fs.readFileSync(dbPath(dir));
    const r = lb(dir, 'append', 's', 't', '{}');
    wantErr(r, 'LOCK_TIMEOUT');
    expect(r.out.error === 'Could not acquire lock', 'message: ' + r.out.error);
    expect(r.ms >= 3500 && r.ms <= 12000, `took ${r.ms} ms`);
    expect(fs.readFileSync(dbPath(dir)).equals(before), 'store changed');
    expect(fs.readFileSync(path.join(dir, 'lock'), 'utf8') === `${holder.pid}\n`, 'live lock was removed');
    const read = lb(dir, 'status');
    return wantErr(read, 'LOCK_TIMEOUT', 'reads wait too');
  } finally { holder.kill(); }
});
check('locks', 'a lock without a PID is held while new and stale after 2 seconds', () => {
  const dir = withStream('s', 1);
  const lock = path.join(dir, 'lock');
  fs.writeFileSync(lock, '');
  const r = lb(dir, 'append', 's', 't', '{}');
  expect(ok(r), 'append after empty lock: ' + r.stdout);
  expect(r.ms >= 1500, `took over a fresh empty lock after ${r.ms} ms`);
  fs.writeFileSync(lock, 'garbage');
  const old = new Date(Date.now() - 60000); fs.utimesSync(lock, old, old);
  const quick = lb(dir, 'append', 's', 't', '{}');
  expect(ok(quick), 'old garbage lock');
  return expect(quick.ms < 1500, `old lock took ${quick.ms} ms`) && expect(!fs.existsSync(lock), 'lock left');
});

// ---------- concurrency ----------
check('concurrency', 'parallel appends from 8 processes lose nothing and never repeat a seq', async () => {
  const dir = withStream('s');
  const worker = async n => { const seqs = []; for (let i = 0; i < 20; i++) { const r = await lbAsync(dir, 'append', 's', 't', JSON.stringify({ w: n, i })); expect(ok(r), 'append failed: ' + r.stdout); seqs.push(r.out.seq); } return seqs; };
  const seqs = (await Promise.all(Array.from({ length: 8 }, (_, n) => worker(n)))).flat().sort((a, b) => a - b);
  expect(seqs.join() === Array.from({ length: 160 }, (_, i) => i + 1).join(), 'seqs: ' + seqs.slice(0, 20).join());
  invariants(dir);
  return want(lb(dir, 'head', 's'), { ok: true, stream: 's', nextSeq: 161, count: 160, compactBefore: 1 });
});
check('concurrency', 'parallel polls on one cursor deliver every event exactly once', async () => {
  const dir = ready();
  writeDb(dir, bigStream(emptyDb(), 's', 300));
  lb(dir, 'cursor-create', 'c', 's');
  const worker = async () => { const got = []; for (;;) { const r = await lbAsync(dir, 'poll', 'c', '--count', '7'); expect(ok(r), 'poll failed: ' + r.stdout); got.push(...r.out.events.map(e => e.seq)); if (!r.out.count && !r.out.hasMore) return got; } };
  const all = (await Promise.all(Array.from({ length: 6 }, worker))).flat().sort((a, b) => a - b);
  return expect(all.length === 300 && all.every((seq, i) => seq === i + 1), `delivered ${all.length}: duplicates or gaps`);
});
check('concurrency', 'only one of many parallel stream-creates succeeds', async () => {
  const dir = ready();
  const rs = await Promise.all(Array.from({ length: 8 }, () => lbAsync(dir, 'stream-create', 'race')));
  return expect(rs.filter(ok).length === 1 && rs.filter(r => err(r, 'ALREADY_EXISTS')).length === 7, rs.map(r => r.out?.code || 'ok').join());
});
check('concurrency', 'parallel processes meeting a stale lock still exclude each other', async () => {
  const dir = withStream('s');
  for (let round = 0; round < 3; round++) {
    fs.writeFileSync(path.join(dir, 'lock'), `${deadPid()}\n`);
    const rs = await Promise.all(Array.from({ length: 10 }, () => lbAsync(dir, 'append', 's', 't', '{}')));
    expect(rs.every(ok), 'append failed: ' + rs.find(r => !ok(r))?.stdout);
  }
  const seqs = lb(dir, 'read', 's').out.events.map(e => e.seq);
  invariants(dir);
  return expect(seqs.length === 30 && new Set(seqs).size === 30, `got ${seqs.length} events`);
});
check('concurrency', 'readers never see a partial import', async () => {
  const dir = ready();
  const file = writeFile('big-read.jsonl', jsonl(Array.from({ length: 3000 }, (_, i) => ev('s' + (i % 5), 'big' + i, 't', { i }))));
  const importing = lbAsync(dir, 'import', file);
  const seen = [];
  for (let i = 0; i < 12; i++) seen.push(lbAsync(dir, 'status').then(r => r.out?.totalEvents));
  const [imp, ...totals] = await Promise.all([importing, ...seen]);
  expect(ok(imp), 'import: ' + imp.stdout);
  return expect(totals.every(total => total === 0 || total === 3000), 'saw totals ' + totals.join());
});
check('concurrency', 'a mixed parallel workload keeps every invariant', async () => {
  const dir = withStream('s', 50);
  lb(dir, 'stream-create', 't'); lb(dir, 'cursor-create', 'c1', 's'); lb(dir, 'cursor-create', 'c2', 's');
  const jobs = [];
  for (let i = 0; i < 40; i++) jobs.push(lbAsync(dir, 'append', i % 2 ? 's' : 't', 'e', JSON.stringify({ i }), '--id', 'mix' + (i % 30)));
  for (let i = 0; i < 10; i++) jobs.push(lbAsync(dir, 'poll', i % 2 ? 'c1' : 'c2', '--count', '9'));
  for (const before of [5, 10, 20]) jobs.push(lbAsync(dir, 'compact', 's', String(before)));
  jobs.push(lbAsync(dir, 'import', writeFile('mix.jsonl', jsonl(Array.from({ length: 40 }, (_, i) => ev('u', 'imp' + i))))));
  const rs = await Promise.all(jobs);
  expect(rs.every(r => r.out && (r.code === 0 || r.code === 1)), 'a process crashed: ' + rs.find(r => !r.out)?.stdout);
  invariants(dir);
  return true;
});

// ---------- crash safety ----------
check('crash', 'killing an import at any moment leaves none or all of it', async () => {
  const lines = jsonl(Array.from({ length: 4000 }, (_, i) => ev('k' + (i % 7), 'kill' + i, 't', { i, pad: 'x'.repeat(40) })));
  const file = writeFile('kill.jsonl', lines);
  const timing = ready();
  const started = Date.now(); expect(ok(lb(timing, 'import', file)), 'import works'); const full = Date.now() - started;
  let partial = 0, outcomes = { none: 0, all: 0 };
  for (let k = 0; k < 24; k++) {
    const dir = withStream('base', 2);
    const child = cp.spawn(process.execPath, [program, dir, 'import', file], { cwd: root, windowsHide: true });
    const closed = new Promise(resolve => child.on('close', resolve));
    await sleep(Math.round(full * k / 20));
    child.kill('SIGKILL');
    await closed;
    const db = invariants(dir);
    const total = Object.keys(db.streams).filter(name => name !== 'base').reduce((sum, name) => sum + db.streams[name].events.length, 0);
    if (total === 0) outcomes.none++; else if (total === 4000) outcomes.all++; else partial++;
    const after = lb(dir, 'status');
    expect(ok(after), 'store unusable after kill: ' + after.stdout);
  }
  return expect(partial === 0, `${partial} partial imports (${JSON.stringify(outcomes)})`);
});
check('crash', 'killing appends at random moments never corrupts the store', async () => {
  const dir = withStream('s', 5);
  for (let round = 0; round < 6; round++) {
    const children = Array.from({ length: 6 }, (_, i) => cp.spawn(process.execPath, [program, dir, 'append', 's', 't', JSON.stringify({ round, i })], { cwd: root, windowsHide: true }));
    const closed = children.map(child => new Promise(resolve => child.on('close', resolve)));
    await sleep(40 + round * 25);
    for (const child of children) child.kill('SIGKILL');
    await Promise.all(closed);
    invariants(dir);
    expect(ok(lb(dir, 'append', 's', 't', '{}')), 'append after kill round ' + round);
  }
  return true;
});

// ---------- run ----------
(async () => {
  for (const { category, name, run } of checks) {
    if (only && !only.test(name) && !only.test(category)) continue;
    let pass = false, note = '';
    try { pass = (await run()) === true; if (!pass) note = 'returned ' + JSON.stringify(pass); } catch (error) { note = error.message; }
    results.push({ category, name, pass, ...(note ? { note: note.slice(0, 300) } : {}) });
    if (!process.env.LOGBOOK_GRADE_QUIET) process.stderr.write(`${pass ? 'PASS' : 'FAIL'} [${category}] ${name}${note ? ' — ' + note.slice(0, 160) : ''}\n`);
  }
  const byCategory = {};
  for (const r of results) { const c = byCategory[r.category] ||= { passed: 0, total: 0 }; c.total++; if (r.pass) c.passed++; }
  const summary = { workspace: source, passed: results.filter(r => r.pass).length, total: results.length, byCategory, results };
  if (outFile) fs.writeFileSync(outFile, JSON.stringify(summary, null, 2));
  process.stdout.write(JSON.stringify({ passed: summary.passed, total: summary.total, byCategory }) + '\n');
  try { fs.rmSync(root, { recursive: true, force: true }); } catch {}
})();
