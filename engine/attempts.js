(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.PlanwharfAttempts = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const clone = value => JSON.parse(JSON.stringify(value));

  function isId(value) {
    return typeof value === 'string' && value.trim() !== '';
  }

  function emptyStore() {
    return { nextId: 1, attempts: [] };
  }

  function createNodeIo(dir) {
    const fs = require('node:fs');
    const path = require('node:path');
    const crypto = require('node:crypto');
    fs.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, 'attempts.json');
    const lock = path.join(dir, 'attempts.lock');

    function acquire() {
      const started = Date.now();
      while (true) {
        try {
          return fs.openSync(lock, 'wx');
        } catch (error) {
          if (error.code !== 'EEXIST') throw error;
          if (Date.now() - started > 5000) throw new Error('attempt store locked');
          Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 20);
        }
      }
    }

    function withLock(fn) {
      const fd = acquire();
      try {
        return fn();
      } finally {
        fs.closeSync(fd);
        try { fs.unlinkSync(lock); } catch (_) {}
      }
    }

    function readUnlocked() {
      try {
        const raw = fs.readFileSync(file, 'utf8');
        const parsed = JSON.parse(raw);
        if (!parsed || typeof parsed !== 'object' || !Array.isArray(parsed.attempts)) return emptyStore();
        const nextId = Number.isInteger(parsed.nextId) && parsed.nextId > 0 ? parsed.nextId : 1;
        return { nextId, attempts: parsed.attempts };
      } catch (error) {
        if (error.code === 'ENOENT') return emptyStore();
        return emptyStore();
      }
    }

    function writeUnlocked(store) {
      const temporary = file + '.' + crypto.randomUUID() + '.tmp';
      fs.writeFileSync(temporary, JSON.stringify(store) + '\n', { encoding: 'utf8', flag: 'wx' });
      require('./atomic-rename.cjs').renameWithRetry(temporary, file);
    }

    return {
      withLock,
      read: readUnlocked,
      write: writeUnlocked
    };
  }

  function createAttemptEngine(options) {
    const opts = options && typeof options === 'object' ? options : {};
    let io = opts.io || null;
    if (!io) {
      if (typeof opts.dir !== 'string' || opts.dir.trim() === '') {
        throw new Error('attempt store directory required');
      }
      if (typeof require !== 'function') {
        throw new Error('attempt file store is unavailable');
      }
      io = createNodeIo(opts.dir);
    }

    function transact(mutator) {
      return io.withLock(() => {
        const store = io.read();
        const result = mutator(store);
        if (result && result.write) io.write(store);
        return result && result.value !== undefined ? result.value : result;
      });
    }

    function getAttempts() {
      return transact(store => ({ write: false, value: clone(store.attempts) }));
    }

    function getActiveAttempt(query) {
      if (!query || typeof query !== 'object') return null;
      return transact(store => {
        const found = store.attempts.find(item => {
          if (item.status !== 'active') return false;
          if (isId(query.taskId) && item.taskId !== query.taskId) return false;
          if (isId(query.projectId) && item.projectId !== query.projectId) return false;
          return isId(query.taskId) || isId(query.projectId);
        });
        return { write: false, value: found ? clone(found) : null };
      });
    }

    function claim(input) {
      if (!input || typeof input !== 'object' || Array.isArray(input)) {
        return { ok: false, reason: 'invalid data' };
      }
      const taskId = input.taskId;
      const projectId = input.projectId;
      const modelId = input.modelId;
      const candidates = input.candidates;
      if (!isId(taskId) || !isId(projectId) || !isId(modelId) || !Array.isArray(candidates)) {
        return { ok: false, reason: 'invalid data' };
      }

      let matched = null;
      let enabledCount = 0;
      for (let i = 0; i < candidates.length; i++) {
        const candidate = candidates[i];
        if (!candidate || typeof candidate !== 'object') continue;
        if (candidate.enabled === true) enabledCount += 1;
        if (candidate.id === modelId && matched === null) matched = candidate;
      }
      if (enabledCount === 0) return { ok: false, reason: 'no model enabled' };
      if (!matched) return { ok: false, reason: 'model not present' };
      if (matched.enabled !== true) return { ok: false, reason: 'model not eligible' };

      return transact(store => {
        for (let i = 0; i < store.attempts.length; i++) {
          const item = store.attempts[i];
          if (item.status !== 'active') continue;
          if (item.taskId === taskId && item.projectId === projectId) {
            return { write: false, value: { ok: false, reason: 'task already assigned' } };
          }
        }
        for (let i = 0; i < store.attempts.length; i++) {
          const item = store.attempts[i];
          if (item.status !== 'active') continue;
          if (item.projectId === projectId  && !(isId(input.megaTaskId) && item.megaTaskId === input.megaTaskId && isId(input.subTaskId) && item.subTaskId !== input.subTaskId)) {
            return { write: false, value: { ok: false, reason: 'project already assigned' } };
          }
        }
        const attempt = {
          id: 'attempt-' + store.nextId,
          taskId,
          projectId,
          modelId,
          status: 'active'
        };
        if (isId(input.megaTaskId) && isId(input.subTaskId)) {
          attempt.megaTaskId = input.megaTaskId;
          attempt.subTaskId = input.subTaskId;
          attempt.research = input.research === true;
        }
        store.nextId += 1;
        store.attempts.push(attempt);
        return { write: true, value: { ok: true, attempt: clone(attempt) } };
      });
    }

    function closeAttempt(input) {
      if (!input || !isId(input.attemptId)) return { ok: false, reason: 'invalid data' };
      return transact(store => {
        const found = store.attempts.find(item => item.id === input.attemptId);
        if (!found) return { write: false, value: { ok: false, reason: 'attempt not found' } };
        if (found.status !== 'active') return { write: false, value: { ok: false, reason: 'attempt not active', attempt: clone(found) } };
        found.status = 'closed';
        found.closedReason = typeof input.reason === 'string' && input.reason.trim() !== '' ? input.reason.trim() : 'closed';
        return { write: true, value: { ok: true, attempt: clone(found) } };
      });
    }

    function attachResult(input) {
      if (!input || !isId(input.attemptId) || typeof input.resultPath !== 'string' || input.resultPath.trim() === '') {
        return { ok: false, reason: 'invalid data' };
      }
      return transact(store => {
        const found = store.attempts.find(item => item.id === input.attemptId);
        if (!found) return { write: false, value: { ok: false, reason: 'attempt not found' } };
        found.resultPath = input.resultPath;
        if (typeof input.status === 'string') found.resultStatus = input.status;
        if (input.exitCode !== undefined) found.exitCode = input.exitCode;
        return { write: true, value: { ok: true, attempt: clone(found) } };
      });
    }

    function getResult(attemptId) {
      if (!isId(attemptId)) return null;
      return transact(store => {
        const found = store.attempts.find(item => item.id === attemptId);
        return { write: false, value: found ? clone(found) : null };
      });
    }

    return { claim, getAttempts, getActiveAttempt, closeAttempt, attachResult, getResult };
  }

  function openAttemptEngine(options) {
    const opts = options && typeof options === 'object' ? options : {};
    if (opts.dir || opts.io) {
      return Promise.resolve(createAttemptEngine(opts));
    }
    if (typeof navigator === 'undefined' || !navigator.storage || typeof navigator.storage.getDirectory !== 'function') {
      return Promise.reject(new Error('attempt file store is unavailable'));
    }
    return navigator.storage.getDirectory().then(root => root.getFileHandle('planwharf-attempts.json', { create: true })).then(handle => handle.getFile().then(file => file.text().then(text => {
      let cache;
      try {
        const parsed = JSON.parse(text);
        cache = parsed && typeof parsed === 'object' && Array.isArray(parsed.attempts)
          ? { nextId: Number.isInteger(parsed.nextId) && parsed.nextId > 0 ? parsed.nextId : 1, attempts: parsed.attempts }
          : emptyStore();
      } catch (_) {
        cache = emptyStore();
      }
      let chain = Promise.resolve();
      const io = {
        withLock(fn) { return fn(); },
        read() { return clone(cache); },
        write(store) {
          cache = clone(store);
          chain = chain.then(async () => {
            const writable = await handle.createWritable();
            await writable.write(JSON.stringify(cache) + '\n');
            await writable.close();
          }).catch(() => {});
        }
      };
      return createAttemptEngine({ io });
    })));
  }

  return { createAttemptEngine, openAttemptEngine };
});
