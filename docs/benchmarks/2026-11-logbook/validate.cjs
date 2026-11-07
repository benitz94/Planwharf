'use strict';
// Shows that grader.cjs is sound: the reference passes everything, an empty seed fails almost
// everything, and each deliberately broken copy of the reference is caught by at least one check.
// node validate.cjs [--only <mutant-name>]
const fs = require('node:fs'), os = require('node:os'), path = require('node:path'), cp = require('node:child_process');
const here = __dirname;
const only = process.argv.includes('--only') ? process.argv[process.argv.indexOf('--only') + 1] : null;
const reference = fs.readFileSync(path.join(here, 'reference', 'logbook.js'), 'utf8');
const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'logbook-validate-'));

// Each mutant replaces exact text in the reference; a replacement that does not apply is an error.
const MUTANTS = {
  'no lock at all': [['function acquire(dir) {', 'function acquire(dir) { return path.join(dir, "lock-unused");']],
  'stale locks are never taken over': [['if (lock && isStale(lock)) { takeOver(file, lock); continue; }', '']],
  'live locks are treated as stale': [["try { process.kill(pid, 0); return true; }", 'try { return false; }']],
  'empty locks are taken over at once': [['return Date.now() - lock.mtime >= EMPTY_LOCK_GRACE_MS;', 'return true;']],
  'stale takeover deletes then creates without a guard': [['function takeOver(file, seen) {', 'function takeOver(file, seen) { try { fs.unlinkSync(file); } catch {} return;']],
  'db.json written in place': [["const tmp = path.join(dir, 'db.json.tmp'), target", "const tmp = path.join(dir, 'db.json'), target"]],
  'import commits line by line': [['      imported++; changed = true;', '      imported++; changed = true; save(path.resolve(process.argv[2]), db);']],
  'reads rewrite db.json': [['if (changed && !READ_ONLY.has(command)) save(dir, db);', 'save(dir, db);']],
  'usage checked after the store': [['  const { datadir, command, positional, flags } = parseArgs(argv);', "  if (argv[0] && argv[1] !== 'init' && !fs.existsSync(path.join(path.resolve(argv[0]), 'db.json'))) fail('NOT_INITIALIZED', 'Store not initialized');\n  const { datadir, command, positional, flags } = parseArgs(argv);"]],
  'any flag accepted by any command': [['if (!Object.prototype.hasOwnProperty.call(accepted, name)) usage', "if (!['force', 'id', 'from', 'count'].includes(name)) usage"]],
  'repeated flags allowed': [["if (name in flags) usage(`Flag given twice: ${arg}`);", '']],
  'payload equality depends on key order': [['  return keys.every(key => has(b, key) && deepEqual(a[key], b[key]));', '  return JSON.stringify(a) === JSON.stringify(b);']],
  'plain objects for keys (__proto__ trap)': [['function toDict(value) { const dict = Object.create(null);', 'function toDict(value) { const dict = {};'], ['const has = (object, key) => Object.prototype.hasOwnProperty.call(object, key);', 'const has = (object, key) => key in object;']],
  'leading zeros accepted': [['const INT_RE = /^[1-9][0-9]*$/;', 'const INT_RE = /^[0-9]*[1-9][0-9]*$/;']],
  'no 1000 cap on read': [['const count = flags.count === undefined ? MAX_BATCH : Math.min(MAX_BATCH, parseInt1(', 'const count = flags.count === undefined ? Infinity : Math.min(Infinity, parseInt1(']],
  'compaction leaves cursors behind': [['if (cursor.stream === name) cursor.pos = Math.max(cursor.pos, before - 1);', '']],
  'compaction keeps ids': [['    removeIds(db, removed);\n    stream.events = stream.events.filter', '    stream.events = stream.events.filter']],
  'poll does not raise a cursor behind compaction': [['const start = Math.max(cursor.pos, stream.compactBefore - 1);', 'const start = cursor.pos;']],
  'stream-delete checks cursors before events': [["    if (!flags.force && stored(stream) > 0) fail('HAS_EVENTS', `Stream ${name} still stores events`);\n    if (!flags.force && cursors.length) fail('HAS_CURSORS', `Cursors point to stream ${name}`);", "    if (!flags.force && cursors.length) fail('HAS_CURSORS', `Cursors point to stream ${name}`);\n    if (!flags.force && stored(stream) > 0) fail('HAS_EVENTS', `Stream ${name} still stores events`);"]],
  'limit checked before the idempotent case': [["    let id = flags.id;", "    if (stored(stream) >= MAX_EVENTS) fail('LIMIT_EXCEEDED', 'full');\n    let id = flags.id;"]],
  'import limit counted on the total': [["      if (stored(stream) >= MAX_EVENTS) fail('LIMIT_EXCEEDED', `${where}: stream ${name} would store more than ${MAX_EVENTS} events`);", '']],
  'import allows identical repeated ids': [['      if (seenIds.has(id)) fail', '      if (false) fail']],
  'corrupt store treated as empty': [["let raw; try { raw = JSON.parse(text); } catch { fail('CORRUPT_STORE', 'db.json is not valid JSON'); }", 'let raw; try { raw = JSON.parse(text); } catch { raw = { version: 1, streams: {}, cursors: {}, eventIds: {} }; }']],
  'lock timeout of 1 second': [['LOCK_TIMEOUT_MS = 5000', 'LOCK_TIMEOUT_MS = 1000']],
  'lock left after an error': [['    return result;\n  } finally { release(lock); }', '    release(lock); return result;\n  } finally { }']],
  'two lines of output': [["process.stdout.write(JSON.stringify(output) + '\\n');", "process.stdout.write(JSON.stringify(output) + '\\n\\n');"]],
  'default id uses seq of the last event': [['id = `${name}-${stream.nextSeq}`', 'id = `${name}-${stream.nextSeq - 1}`']]
};

function workspace(name, files) {
  const dir = path.join(scratch, name.replace(/[^\w]+/g, '-'));
  fs.mkdirSync(dir, { recursive: true });
  for (const [file, text] of Object.entries(files)) fs.writeFileSync(path.join(dir, file), text);
  return dir;
}
function grade(dir) {
  const out = path.join(dir, '..', path.basename(dir) + '.json');
  cp.spawnSync(process.execPath, [path.join(here, 'grader.cjs'), dir, '--skip-suite', '--out', out], { encoding: 'utf8', timeout: 900000, env: { ...process.env, LOGBOOK_GRADE_QUIET: '1' }, windowsHide: true });
  return JSON.parse(fs.readFileSync(out, 'utf8'));
}

const report = { reference: null, seed: null, mutants: {} };
if (!only) {
  const ref = grade(workspace('reference', { 'logbook.js': reference, 'README.md': 'x' }));
  report.reference = { passed: ref.passed, total: ref.total, failed: ref.results.filter(r => !r.pass).map(r => r.name + ': ' + r.note) };
  const seed = grade(workspace('seed', { 'logbook.js': '' }));
  report.seed = { passed: seed.passed, total: seed.total, passedNames: seed.results.filter(r => r.pass).map(r => r.name) };
  console.log(`reference ${ref.passed}/${ref.total}; empty seed ${seed.passed}/${seed.total}`);
}
for (const [name, edits] of Object.entries(MUTANTS)) {
  if (only && name !== only) continue;
  let text = reference;
  for (const [from, to] of edits) { if (!text.includes(from)) throw new Error(`Mutant "${name}" does not apply: ${from.slice(0, 80)}`); text = text.replace(from, to); }
  const result = grade(workspace('mutant-' + name, { 'logbook.js': text, 'README.md': 'x' }));
  const caughtBy = result.results.filter(r => !r.pass).map(r => r.name);
  report.mutants[name] = { caught: caughtBy.length > 0, caughtBy };
  console.log(`${caughtBy.length ? 'caught' : 'MISSED'}  ${name}  (${caughtBy.length} checks: ${caughtBy.slice(0, 3).join('; ')})`);
}
fs.writeFileSync(path.join(here, only ? 'validate-one.json' : 'validate.json'), JSON.stringify(report, null, 2));
fs.rmSync(scratch, { recursive: true, force: true });
const missed = Object.entries(report.mutants).filter(([, m]) => !m.caught).map(([name]) => name);
const sound = (!report.reference || report.reference.passed === report.reference.total) && missed.length === 0;
console.log(sound ? 'grader is sound' : `NOT SOUND: missed ${missed.join(', ') || 'none'}; reference failures ${report.reference?.failed?.length}`);
process.exitCode = sound ? 0 : 1;
