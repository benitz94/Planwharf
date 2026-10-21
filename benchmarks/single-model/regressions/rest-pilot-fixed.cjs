// Repaired historical pilot output, used only as an offline regression fixture.
// Never copied into fresh live trial workspaces.
'use strict';

const http = require('node:http');
const fs = require('node:fs/promises');
const syncFs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const MAX_BODY = 16384;
const ID = /^[a-z][a-z0-9-]{0,31}$/;

const keys = (value, expected) => { const a = Object.keys(value).sort(), b = expected.slice().sort(); return a.length === b.length && a.every((x, i) => x === b[i]); };
const error = (message, status = 400) => Object.assign(new Error(message), { status });
function validItem(x) { return x && typeof x === 'object' && keys(x, ['id', 'title', 'done']) && typeof x.id === 'string' && ID.test(x.id) && typeof x.title === 'string' && x.title === x.title.trim() && x.title.length >= 1 && x.title.length <= 80 && typeof x.done === 'boolean'; }
function parse(text, file) {
  let x; try { x = JSON.parse(text); } catch { throw new Error(`Malformed store: ${file}`); }
  if (!x || typeof x !== 'object' || Array.isArray(x) || !keys(x, ['version', 'items']) || x.version !== 1 || !Array.isArray(x.items) || !x.items.every(validItem) || new Set(x.items.map(i => i.id)).size !== x.items.length) throw new Error(`Malformed store: ${file}`);
  return { version: 1, items: x.items.map(i => ({ ...i })) };
}
async function load(file) { try { return parse(await fs.readFile(file, 'utf8'), file); } catch (e) { if (e.code === 'ENOENT') return { version: 1, items: [] }; throw e; } }
async function replace(file, text) {
  const dir = path.dirname(file), tmp = path.join(dir, `.${path.basename(file)}.${process.pid}.${crypto.randomUUID()}.tmp`); let h;
  try { h = await fs.open(tmp, 'wx', 0o600); await h.writeFile(text); await h.sync(); await h.close(); h = undefined; await fs.rename(tmp, file); const d = await fs.open(dir, 'r'); try { await d.sync(); } catch (e) { if (!['EPERM', 'EINVAL', 'ENOTSUP'].includes(e.code)) throw e; } finally { await d.close(); } }
  catch (e) { if (h) await h.close().catch(() => {}); await fs.unlink(tmp).catch(() => {}); throw e; }
}

function createServer({ file, onStage } = {}) {
  if (typeof file !== 'string' || !file) throw new TypeError('file is required');
  if (onStage !== undefined && typeof onStage !== 'function') throw new TypeError('onStage must be a function');
  syncFs.mkdirSync(path.dirname(file), { recursive: true });
  let state = syncFs.existsSync(file) ? parse(syncFs.readFileSync(file, 'utf8'), file) : { version: 1, items: [] };
  let queue = Promise.resolve();
  const current = async () => state;
  const enqueue = fn => { const result = queue.then(fn, fn); queue = result.catch(() => {}); return result; };
  const send = (res, status, value, headers = {}) => { if (value === undefined) return res.writeHead(status, headers).end(); const data = JSON.stringify(value); res.writeHead(status, { 'Content-Type': 'application/json', ...headers, 'Content-Length': Buffer.byteLength(data) }).end(data); };
  const readBody = async req => { let n = 0; const chunks = []; for await (const chunk of req) { n += chunk.length; if (n > MAX_BODY) { req.resume(); throw error('Request body too large', 413); } chunks.push(chunk); } if (!n) throw error('Request body is required'); try { return JSON.parse(Buffer.concat(chunks).toString()); } catch { throw error('Malformed JSON'); } };
  const commit = async next => { if (onStage) await onStage('beforeCommit'); await replace(file, JSON.stringify(next) + '\n'); state = next; if (onStage) await onStage('afterCommit'); };
  const newItem = x => { if (!x || typeof x !== 'object' || Array.isArray(x) || !keys(x, Object.hasOwn(x, 'done') ? ['id', 'title', 'done'] : ['id', 'title'])) throw error('Body must contain only id, title, and optional done'); if (typeof x.id !== 'string' || !ID.test(x.id)) throw error('Invalid id'); if (typeof x.title !== 'string') throw error('Invalid title'); const title = x.title.trim(); if (title.length < 1 || title.length > 80) throw error('Invalid title'); if (x.done !== undefined && typeof x.done !== 'boolean') throw error('Invalid done'); return { id: x.id, title, done: x.done === undefined ? false : x.done }; };
  const patchItem = x => { if (!x || typeof x !== 'object' || Array.isArray(x) || !Object.keys(x).length || !Object.keys(x).every(k => k === 'title' || k === 'done')) throw error('Patch must contain a nonempty subset of title and done'); const p = {}; if (Object.hasOwn(x, 'title')) { if (typeof x.title !== 'string') throw error('Invalid title'); p.title = x.title.trim(); if (p.title.length < 1 || p.title.length > 80) throw error('Invalid title'); } if (Object.hasOwn(x, 'done')) { if (typeof x.done !== 'boolean') throw error('Invalid done'); p.done = x.done; } return p; };
  return http.createServer(async (req, res) => {
    try {
      const parts = new URL(req.url, 'http://127.0.0.1').pathname.split('/').filter(Boolean); const s = await current();
      if (parts[0] !== 'items' || parts.length > 2 || (parts[1] && !ID.test(parts[1]))) return send(res, 404, { error: 'Not found' });
      const id = parts[1];
      if (req.method === 'GET') { if (!id) return send(res, 200, s.items.slice().sort((a, b) => a.id.localeCompare(b.id))); const i = s.items.find(x => x.id === id); return send(res, i ? 200 : 404, i || { error: 'Not found' }); }
      if (req.method === 'POST' && !id) { const item = newItem(await readBody(req)); return await enqueue(async () => { const c = await current(); if (c.items.some(x => x.id === item.id)) throw error('Duplicate id', 409); await commit({ version: 1, items: [...c.items, item] }); send(res, 201, item, { Location: `/items/${item.id}` }); }); }
      if (id && req.method === 'DELETE') return await enqueue(async () => { const c = await current(), index = c.items.findIndex(x => x.id === id); if (index < 0) return send(res, 404, { error: 'Not found' }); const items = c.items.slice(); items.splice(index, 1); await commit({ version: 1, items }); send(res, 204); });
      if (id && req.method === 'PATCH') { const p = patchItem(await readBody(req)); return await enqueue(async () => { const c = await current(), index = c.items.findIndex(x => x.id === id); if (index < 0) return send(res, 404, { error: 'Not found' }); const items = c.items.slice(); items[index] = { ...items[index], ...p }; await commit({ version: 1, items }); send(res, 200, items[index]); }); }
      return send(res, 404, { error: 'Not found' });
    } catch (e) { if (!res.headersSent) send(res, e.status || 500, { error: e.status ? e.message : 'Internal server error' }); else res.destroy(); }
  });
}
module.exports = { createServer };
