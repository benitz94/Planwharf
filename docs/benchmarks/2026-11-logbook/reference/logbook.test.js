'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs'), os = require('os'), path = require('path'), cp = require('child_process');

const program = path.join(__dirname, 'logbook.js');
const dirs = [];
const fresh = () => { const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'logbook-test-')); dirs.push(dir); return path.join(dir, 'data'); };
test.after(() => { for (const dir of dirs) fs.rmSync(dir, { recursive: true, force: true }); });
const lb = (dir, ...args) => {
  const r = cp.spawnSync(process.execPath, [program, dir, ...args], { encoding: 'utf8' });
  return { code: r.status, out: JSON.parse(r.stdout) };
};
const ready = () => { const dir = fresh(); lb(dir, 'init'); return dir; };

test('init, append, read', () => {
  const dir = ready();
  assert.deepStrictEqual(lb(dir, 'stream-create', 'orders').out, { ok: true, stream: 'orders' });
  assert.deepStrictEqual(lb(dir, 'append', 'orders', 'created', '{"a":1}').out, { ok: true, stream: 'orders', seq: 1, id: 'orders-1' });
  const read = lb(dir, 'read', 'orders').out;
  assert.strictEqual(read.count, 1);
  assert.deepStrictEqual(read.events[0].payload, { a: 1 });
});

test('usage errors come first', () => {
  const r = lb(fresh(), 'append', 'x');
  assert.strictEqual(r.code, 2);
  assert.strictEqual(r.out.code, 'USAGE');
  assert.strictEqual(lb(fresh(), 'status').out.code, 'NOT_INITIALIZED');
});

test('idempotent append and duplicate ids', () => {
  const dir = ready();
  lb(dir, 'stream-create', 's');
  lb(dir, 'append', 's', 't', '{"a":1,"b":2}', '--id', 'x');
  assert.strictEqual(lb(dir, 'append', 's', 't', '{"b":2,"a":1}', '--id', 'x').out.idempotent, true);
  assert.strictEqual(lb(dir, 'append', 's', 't', '{"a":2}', '--id', 'x').out.code, 'DUPLICATE_ID');
});

test('compaction raises cursors and poll moves them', () => {
  const dir = ready();
  lb(dir, 'stream-create', 's');
  for (let i = 0; i < 5; i++) lb(dir, 'append', 's', 't', '{}');
  lb(dir, 'cursor-create', 'c', 's');
  assert.strictEqual(lb(dir, 'compact', 's', '3').out.removed, 2);
  const poll = lb(dir, 'poll', 'c', '--count', '2').out;
  assert.deepStrictEqual(poll.events.map(event => event.seq), [3, 4]);
  assert.strictEqual(poll.newPos, 4);
  assert.strictEqual(lb(dir, 'get', 's', '1').out.code, 'COMPACTED');
});

test('import is all or nothing', () => {
  const dir = ready();
  const file = path.join(path.dirname(dir), 'in.jsonl');
  fs.writeFileSync(file, '{"stream":"a","type":"t","id":"1","payload":{}}\n{"stream":"a","type":"t","id":"1","payload":{}}\n');
  assert.strictEqual(lb(dir, 'import', file).out.code, 'DUPLICATE_ID');
  assert.strictEqual(lb(dir, 'status').out.totalEvents, 0);
});

test('concurrent appends lose nothing', async () => {
  const dir = ready();
  lb(dir, 'stream-create', 's');
  const one = () => new Promise(resolve => cp.execFile(process.execPath, [program, dir, 'append', 's', 't', '{}'], (error, stdout) => resolve(stdout)));
  const outputs = await Promise.all(Array.from({ length: 20 }, one));
  const seqs = outputs.map(stdout => { const out = JSON.parse(stdout); assert.strictEqual(out.ok, true, stdout); return out.seq; });
  assert.deepStrictEqual(seqs.sort((a, b) => a - b), Array.from({ length: 20 }, (_, i) => i + 1));
});
