'use strict';
// Reference implementation of task/TASK.md. Node.js >= 18, no dependencies.
const fs = require('fs');
const path = require('path');

const NAME_RE = /^[a-z0-9][a-z0-9_-]{0,62}$/;
const TYPE_RE = /^[a-z0-9][a-z0-9-]{0,63}$/;
const ID_RE = /^[a-zA-Z0-9._-]{1,128}$/;
const INT_RE = /^[1-9][0-9]*$/;
const MAX_STREAMS = 100, MAX_EVENTS = 10000, MAX_CURSORS = 1000, MAX_BATCH = 1000;
const LOCK_TIMEOUT_MS = 5000, EMPTY_LOCK_GRACE_MS = 2000, TAKEOVER_GRACE_MS = 2000;

class Fail extends Error { constructor(code, message, exit = 1) { super(message); this.code = code; this.exit = exit; } }
const fail = (code, message) => { throw new Fail(code, message); };

// Commands: positional count and accepted flags (true = takes a value).
const COMMANDS = {
  init: [0, {}], 'stream-create': [1, {}], 'stream-delete': [1, { force: false }], 'stream-list': [0, {}],
  append: [3, { id: true }], read: [1, { from: true, count: true }], get: [2, {}], head: [1, {}],
  compact: [2, {}], 'cursor-create': [2, {}], 'cursor-delete': [1, {}], 'cursor-list': [0, {}],
  poll: [1, { count: true }], import: [1, {}], status: [0, {}]
};
const READ_ONLY = new Set(['stream-list', 'read', 'get', 'head', 'cursor-list', 'status']);

function parseArgs(argv) {
  const usage = message => { throw new Fail('USAGE', message, 2); };
  if (argv.length < 1) usage('Missing <datadir>');
  if (argv.length < 2) usage('Missing command');
  const [datadir, command, ...rest] = argv;
  if (!Object.prototype.hasOwnProperty.call(COMMANDS, command)) usage(`Unknown command: ${command}`);
  const [count, accepted] = COMMANDS[command];
  const flags = Object.create(null), positional = [];
  for (let i = 0; i < rest.length; i++) {
    const arg = rest[i];
    if (!arg.startsWith('--')) { positional.push(arg); continue; }
    const name = arg.slice(2);
    if (!Object.prototype.hasOwnProperty.call(accepted, name)) usage(`Unknown flag for ${command}: ${arg}`);
    if (name in flags) usage(`Flag given twice: ${arg}`);
    if (accepted[name]) { if (i + 1 >= rest.length) usage(`Flag ${arg} needs a value`); flags[name] = rest[++i]; }
    else flags[name] = true;
  }
  if (positional.length !== count) usage(`${command} takes ${count} argument${count === 1 ? '' : 's'}, got ${positional.length}`);
  return { datadir, command, positional, flags };
}

// ---------- helpers ----------
const has = (object, key) => Object.prototype.hasOwnProperty.call(object, key);
const sleep = ms => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
function toDict(value) { const dict = Object.create(null); for (const key of Object.keys(value)) dict[key] = value[key]; return dict; }
function parseInt1(text, code, what) {
  if (typeof text !== 'string' || !INT_RE.test(text)) fail(code, `${what} must be an integer >= 1`);
  const value = Number(text);
  if (!Number.isSafeInteger(value)) fail(code, `${what} is too large`);
  return value;
}
function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== 'object') return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((item, i) => deepEqual(item, b[i]));
  const keys = Object.keys(a);
  if (keys.length !== Object.keys(b).length) return false;
  return keys.every(key => has(b, key) && deepEqual(a[key], b[key]));
}
function parsePayload(text) {
  let value; try { value = JSON.parse(text); } catch { fail('INVALID_PAYLOAD', 'Payload is not valid JSON'); }
  if (!isObject(value)) fail('INVALID_PAYLOAD', 'Payload must be a JSON object');
  return value;
}
const eventOut = event => ({ seq: event.seq, type: event.type, id: event.id, payload: event.payload });
const stored = stream => stream.nextSeq - stream.compactBefore;

// ---------- lock ----------
function pidAlive(pid) {
  if (pid === process.pid) return false; // We do not hold it yet, so a lock naming us is left over.
  try { process.kill(pid, 0); return true; } catch (error) { return error.code === 'EPERM'; }
}
// null means "look again": the file is gone, or on Windows it is being deleted (EPERM/EACCES/EBUSY).
function readLock(file) {
  try { return { text: fs.readFileSync(file, 'utf8'), mtime: fs.statSync(file).mtimeMs }; }
  catch (error) { if (['ENOENT', 'EPERM', 'EACCES', 'EBUSY'].includes(error.code)) return null; throw error; }
}
function isStale(lock) {
  const match = /^(\d+)\n$/.exec(lock.text);
  if (match) return !pidAlive(Number(match[1]));
  return Date.now() - lock.mtime >= EMPTY_LOCK_GRACE_MS;
}
// Only one process at a time may remove a stale lock; it re-checks the lock while holding lock.takeover.
function takeOver(file, seen) {
  const guard = file + '.takeover';
  let fd;
  try { fd = fs.openSync(guard, 'wx'); }
  catch (error) {
    if (error.code !== 'EEXIST') throw error;
    const other = readLock(guard);
    // The takeover step lasts microseconds; an old guard belongs to a process that died inside it.
    if (other && Date.now() - other.mtime >= TAKEOVER_GRACE_MS) { try { fs.unlinkSync(guard); } catch {} }
    return;
  }
  try {
    fs.writeSync(fd, `${process.pid}\n`); fs.closeSync(fd);
    const now = readLock(file);
    if (now && now.text === seen.text && now.mtime === seen.mtime && isStale(now)) fs.unlinkSync(file);
  } finally { try { fs.unlinkSync(guard); } catch {} }
}
function acquire(dir) {
  const file = path.join(dir, 'lock');
  const deadline = Date.now() + LOCK_TIMEOUT_MS;
  for (;;) {
    try {
      const fd = fs.openSync(file, 'wx');
      try { fs.writeSync(fd, `${process.pid}\n`); } finally { fs.closeSync(fd); }
      return file;
    } catch (error) {
      if (!['EEXIST', 'EPERM', 'EACCES', 'EBUSY'].includes(error.code)) throw error;
    }
    const lock = readLock(file);
    if (lock && isStale(lock)) { takeOver(file, lock); continue; }
    if (Date.now() >= deadline) fail('LOCK_TIMEOUT', 'Could not acquire lock');
    sleep(5 + Math.floor(Math.random() * 20));
  }
}
function release(file) {
  try { if (fs.readFileSync(file, 'utf8') === `${process.pid}\n`) fs.unlinkSync(file); } catch {}
}

// ---------- store ----------
function load(dir) {
  let text;
  try { text = fs.readFileSync(path.join(dir, 'db.json'), 'utf8'); }
  catch (error) { if (error.code === 'ENOENT') fail('NOT_INITIALIZED', 'Store not initialized'); throw error; }
  let raw; try { raw = JSON.parse(text); } catch { fail('CORRUPT_STORE', 'db.json is not valid JSON'); }
  if (!isObject(raw) || raw.version !== 1 || !isObject(raw.streams) || !isObject(raw.cursors) || !isObject(raw.eventIds)) fail('CORRUPT_STORE', 'db.json does not have the store structure');
  return { version: 1, streams: toDict(raw.streams), cursors: toDict(raw.cursors), eventIds: toDict(raw.eventIds) };
}
function save(dir, db) {
  const tmp = path.join(dir, 'db.json.tmp'), target = path.join(dir, 'db.json');
  const fd = fs.openSync(tmp, 'w');
  try { fs.writeSync(fd, JSON.stringify(db)); fs.fsyncSync(fd); } finally { fs.closeSync(fd); }
  const until = Date.now() + 1000;
  for (;;) {
    try { fs.renameSync(tmp, target); return; }
    catch (error) { if (!['EPERM', 'EBUSY', 'EACCES'].includes(error.code) || Date.now() >= until) throw error; sleep(20); }
  }
}
const emptyStore = () => ({ version: 1, streams: {}, cursors: {}, eventIds: {} });

// ---------- commands ----------
function streamOf(db, name, code = 'STREAM_NOT_FOUND') {
  if (!has(db.streams, name)) fail(code, `Stream not found: ${name}`);
  return db.streams[name];
}
function newStream() { return { nextSeq: 1, compactBefore: 1, events: [] }; }
function removeIds(db, events) { for (const event of events) if (has(db.eventIds, event.id)) delete db.eventIds[event.id]; }

const run = {
  'stream-create'(db, [name]) {
    if (!NAME_RE.test(name)) fail('INVALID_NAME', `Invalid stream name: ${name}`);
    if (has(db.streams, name)) fail('ALREADY_EXISTS', `Stream already exists: ${name}`);
    if (Object.keys(db.streams).length >= MAX_STREAMS) fail('LIMIT_EXCEEDED', `At most ${MAX_STREAMS} streams`);
    db.streams[name] = newStream();
    return { result: { stream: name }, changed: true };
  },
  'stream-delete'(db, [name], flags) {
    const stream = streamOf(db, name, 'NOT_FOUND');
    const cursors = Object.keys(db.cursors).filter(key => db.cursors[key].stream === name);
    if (!flags.force && stored(stream) > 0) fail('HAS_EVENTS', `Stream ${name} still stores events`);
    if (!flags.force && cursors.length) fail('HAS_CURSORS', `Cursors point to stream ${name}`);
    removeIds(db, stream.events);
    for (const key of cursors) delete db.cursors[key];
    delete db.streams[name];
    return { result: { stream: name, eventsDeleted: stream.events.length, cursorsDeleted: cursors.length }, changed: true };
  },
  'stream-list'(db) {
    const streams = Object.keys(db.streams).sort().map(name => { const s = db.streams[name]; return { name, nextSeq: s.nextSeq, count: stored(s), compactBefore: s.compactBefore }; });
    return { result: { streams, count: streams.length } };
  },
  append(db, [name, type, payloadText], flags) {
    const stream = streamOf(db, name);
    if (!TYPE_RE.test(type)) fail('INVALID_TYPE', `Invalid event type: ${type}`);
    const payload = parsePayload(payloadText);
    let id = flags.id;
    if (id !== undefined && !ID_RE.test(id)) fail('INVALID_ID', `Invalid event id: ${id}`);
    if (id !== undefined && has(db.eventIds, id)) {
      const at = db.eventIds[id];
      const existing = at.stream === name ? stream.events.find(event => event.seq === at.seq) : null;
      if (existing && existing.type === type && deepEqual(existing.payload, payload)) return { result: { stream: name, seq: existing.seq, id, idempotent: true } };
      fail('DUPLICATE_ID', `Event id already used: ${id}`);
    }
    if (id === undefined) { id = `${name}-${stream.nextSeq}`; if (has(db.eventIds, id)) fail('DUPLICATE_ID', `Event id already used: ${id}`); }
    if (stored(stream) >= MAX_EVENTS) fail('LIMIT_EXCEEDED', `Stream ${name} already stores ${MAX_EVENTS} events`);
    const seq = stream.nextSeq++;
    stream.events.push({ seq, type, id, payload });
    db.eventIds[id] = { stream: name, seq };
    return { result: { stream: name, seq, id }, changed: true };
  },
  read(db, [name], flags) {
    const stream = streamOf(db, name);
    const from = flags.from === undefined ? stream.compactBefore : parseInt1(flags.from, 'INVALID_SEQ', '--from');
    const count = flags.count === undefined ? MAX_BATCH : Math.min(MAX_BATCH, parseInt1(flags.count, 'INVALID_COUNT', '--count'));
    if (from < stream.compactBefore) fail('COMPACTED', `Events before ${stream.compactBefore} were compacted`);
    const matching = stream.events.filter(event => event.seq >= from);
    const events = matching.slice(0, count).map(eventOut);
    return { result: { stream: name, events, count: events.length, hasMore: matching.length > events.length } };
  },
  get(db, [name, seqText]) {
    const stream = streamOf(db, name);
    const seq = parseInt1(seqText, 'INVALID_SEQ', 'seq');
    if (seq < stream.compactBefore) fail('COMPACTED', `Event ${seq} was compacted`);
    const event = seq < stream.nextSeq ? stream.events.find(item => item.seq === seq) : null;
    if (!event) fail('NOT_FOUND', `No event ${seq} in ${name}`);
    return { result: { event: eventOut(event) } };
  },
  head(db, [name]) {
    const stream = streamOf(db, name);
    return { result: { stream: name, nextSeq: stream.nextSeq, count: stored(stream), compactBefore: stream.compactBefore } };
  },
  compact(db, [name, beforeText]) {
    const stream = streamOf(db, name);
    const before = parseInt1(beforeText, 'INVALID_SEQ', 'before-seq');
    if (before < stream.compactBefore) fail('ALREADY_COMPACTED', `Already compacted before ${stream.compactBefore}`);
    if (before > stream.nextSeq) fail('FUTURE_SEQ', `before-seq is beyond nextSeq ${stream.nextSeq}`);
    const removed = stream.events.filter(event => event.seq < before);
    if (before === stream.compactBefore) return { result: { stream: name, compactBefore: before, removed: 0 } };
    removeIds(db, removed);
    stream.events = stream.events.filter(event => event.seq >= before);
    stream.compactBefore = before;
    for (const key of Object.keys(db.cursors)) { const cursor = db.cursors[key]; if (cursor.stream === name) cursor.pos = Math.max(cursor.pos, before - 1); }
    return { result: { stream: name, compactBefore: before, removed: removed.length }, changed: true };
  },
  'cursor-create'(db, [name, streamName]) {
    if (!NAME_RE.test(name)) fail('INVALID_NAME', `Invalid cursor name: ${name}`);
    if (has(db.cursors, name)) fail('ALREADY_EXISTS', `Cursor already exists: ${name}`);
    streamOf(db, streamName);
    if (Object.keys(db.cursors).length >= MAX_CURSORS) fail('LIMIT_EXCEEDED', `At most ${MAX_CURSORS} cursors`);
    db.cursors[name] = { stream: streamName, pos: 0 };
    return { result: { cursor: name, stream: streamName, pos: 0 }, changed: true };
  },
  'cursor-delete'(db, [name]) {
    if (!has(db.cursors, name)) fail('NOT_FOUND', `Cursor not found: ${name}`);
    delete db.cursors[name];
    return { result: { cursor: name }, changed: true };
  },
  'cursor-list'(db) {
    const cursors = Object.keys(db.cursors).sort().map(name => ({ name, stream: db.cursors[name].stream, pos: db.cursors[name].pos }));
    return { result: { cursors, count: cursors.length } };
  },
  poll(db, [name], flags) {
    if (!has(db.cursors, name)) fail('NOT_FOUND', `Cursor not found: ${name}`);
    const count = flags.count === undefined ? 10 : Math.min(MAX_BATCH, parseInt1(flags.count, 'INVALID_COUNT', '--count'));
    const cursor = db.cursors[name];
    const stream = streamOf(db, cursor.stream);
    const start = Math.max(cursor.pos, stream.compactBefore - 1);
    const events = stream.events.filter(event => event.seq > start && event.seq <= start + count).map(eventOut);
    const newPos = events.length ? events[events.length - 1].seq : start;
    const hasMore = stream.events.some(event => event.seq > newPos);
    const changed = newPos !== cursor.pos;
    cursor.pos = newPos;
    return { result: { cursor: name, stream: cursor.stream, events, count: events.length, newPos, hasMore }, changed };
  },
  import(db, [file]) {
    let text;
    try { text = fs.readFileSync(path.resolve(process.cwd(), file), 'utf8'); } catch (error) { fail('INVALID_FILE', `Cannot read ${file}: ${error.code || error.message}`); }
    const seenIds = new Set(), created = [];
    let imported = 0, skipped = 0, changed = false;
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].replace(/\r$/, '');
      if (!line.trim()) continue;
      const where = `line ${i + 1}`;
      let item; try { item = JSON.parse(line); } catch { fail('INVALID_FORMAT', `${where}: not valid JSON`); }
      if (!isObject(item) || !['stream', 'type', 'id', 'payload'].every(key => has(item, key))) fail('INVALID_FORMAT', `${where}: needs stream, type, id and payload`);
      const { stream: name, type, id, payload } = item;
      if (typeof name !== 'string' || !NAME_RE.test(name)) fail('INVALID_NAME', `${where}: invalid stream name`);
      if (typeof type !== 'string' || !TYPE_RE.test(type)) fail('INVALID_TYPE', `${where}: invalid event type`);
      if (!isObject(payload)) fail('INVALID_PAYLOAD', `${where}: payload must be an object`);
      if (typeof id !== 'string' || !ID_RE.test(id)) fail('INVALID_ID', `${where}: invalid event id`);
      if (seenIds.has(id)) fail('DUPLICATE_ID', `${where}: id ${id} repeated in the file`);
      seenIds.add(id);
      if (has(db.eventIds, id)) {
        const at = db.eventIds[id];
        const existing = at.stream === name && has(db.streams, name) ? db.streams[name].events.find(event => event.seq === at.seq) : null;
        if (existing && existing.type === type && deepEqual(existing.payload, payload)) { skipped++; continue; }
        fail('DUPLICATE_ID', `${where}: id ${id} already stored with other data`);
      }
      if (!has(db.streams, name)) {
        if (Object.keys(db.streams).length >= MAX_STREAMS) fail('LIMIT_EXCEEDED', `${where}: more than ${MAX_STREAMS} streams`);
        db.streams[name] = newStream(); created.push(name);
      }
      const stream = db.streams[name];
      if (stored(stream) >= MAX_EVENTS) fail('LIMIT_EXCEEDED', `${where}: stream ${name} would store more than ${MAX_EVENTS} events`);
      const seq = stream.nextSeq++;
      stream.events.push({ seq, type, id, payload });
      db.eventIds[id] = { stream: name, seq };
      imported++; changed = true;
    }
    return { result: { imported, skipped, streamsCreated: created.sort() }, changed };
  },
  status(db) {
    const streams = Object.keys(db.streams).map(name => db.streams[name]);
    return { result: { streams: streams.length, cursors: Object.keys(db.cursors).length, totalEvents: streams.reduce((sum, s) => sum + stored(s), 0), compactedEvents: streams.reduce((sum, s) => sum + s.compactBefore - 1, 0) } };
  }
};

function main(argv) {
  const { datadir, command, positional, flags } = parseArgs(argv);
  const dir = path.resolve(datadir);
  if (command === 'init') {
    fs.mkdirSync(dir, { recursive: true });
    const lock = acquire(dir);
    try {
      if (fs.existsSync(path.join(dir, 'db.json'))) fail('ALREADY_INITIALIZED', 'Store already initialized');
      save(dir, emptyStore());
      return { version: 1 };
    } finally { release(lock); }
  }
  if (!fs.existsSync(path.join(dir, 'db.json'))) fail('NOT_INITIALIZED', 'Store not initialized');
  const lock = acquire(dir);
  try {
    const db = load(dir);
    const { result, changed } = run[command](db, positional, flags);
    if (changed && !READ_ONLY.has(command)) save(dir, db);
    return result;
  } finally { release(lock); }
}

let output, exit = 0;
try { output = { ok: true, ...main(process.argv.slice(2)) }; }
catch (error) {
  if (error instanceof Fail) { output = { ok: false, error: error.message, code: error.code }; exit = error.exit; }
  else { output = { ok: false, error: String(error && error.message || error), code: 'IO_ERROR' }; exit = 1; }
}
process.stdout.write(JSON.stringify(output) + '\n');
process.exitCode = exit;
