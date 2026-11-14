'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { GUARD_KEY, stripPushGuard, stripWorkerLaunchEnv } = require('./test-isolation.cjs');

test('the worker push guard is removed and other Git config stays, renumbered', () => {
  const env = {
    GIT_CONFIG_COUNT: '3',
    GIT_CONFIG_KEY_0: 'safe.directory',
    GIT_CONFIG_VALUE_0: 'D:/work',
    GIT_CONFIG_KEY_1: GUARD_KEY,
    GIT_CONFIG_VALUE_1: '',
    GIT_CONFIG_KEY_2: 'user.name',
    GIT_CONFIG_VALUE_2: 'Fixture'
  };
  stripPushGuard(env);
  assert.equal(env.GIT_CONFIG_COUNT, '2');
  assert.equal(env.GIT_CONFIG_KEY_0, 'safe.directory');
  assert.equal(env.GIT_CONFIG_VALUE_0, 'D:/work');
  assert.equal(env.GIT_CONFIG_KEY_1, 'user.name');
  assert.equal(env.GIT_CONFIG_VALUE_1, 'Fixture');
  assert.equal(env.GIT_CONFIG_KEY_2, undefined);
  assert.equal(env.GIT_CONFIG_VALUE_2, undefined);
});

test('a launch that only carries the push guard leaves no Git config count', () => {
  const env = { GIT_CONFIG_COUNT: '1', GIT_CONFIG_KEY_0: GUARD_KEY, GIT_CONFIG_VALUE_0: '', OTHER: 'kept' };
  stripPushGuard(env);
  assert.equal(env.GIT_CONFIG_COUNT, undefined);
  assert.equal(env.GIT_CONFIG_KEY_0, undefined);
  assert.equal(env.OTHER, 'kept');
});

test('desktop version from the worker launch is cleared without touching the plan path', () => {
  const env = { PLANWHARF_DESKTOP: '1', PLANWHARF_DESKTOP_VERSION: 'v0.2.57', PLANWHARF_PLAN: 'C:/plan.json', GIT_CONFIG_COUNT: 'nope' };
  stripWorkerLaunchEnv(env);
  assert.equal(env.PLANWHARF_DESKTOP, undefined);
  assert.equal(env.PLANWHARF_DESKTOP_VERSION, undefined);
  assert.equal(env.PLANWHARF_PLAN, 'C:/plan.json');
  assert.equal(env.GIT_CONFIG_COUNT, 'nope');
});

test('a count that is not a worker guard list is left untouched', () => {
  for (const count of ['-1', '1001', '', 'nope']) {
    const env = { GIT_CONFIG_COUNT: count, GIT_CONFIG_KEY_0: GUARD_KEY, GIT_CONFIG_VALUE_0: '' };
    stripPushGuard(env);
    assert.equal(env.GIT_CONFIG_COUNT, count);
    assert.equal(env.GIT_CONFIG_KEY_0, GUARD_KEY);
  }
  // parseInt reads 1.5 as one entry, so the empty guard is still removed.
  const truncated = { GIT_CONFIG_COUNT: '1.5', GIT_CONFIG_KEY_0: GUARD_KEY, GIT_CONFIG_VALUE_0: '' };
  stripPushGuard(truncated);
  assert.equal(truncated.GIT_CONFIG_COUNT, undefined);
  assert.equal(truncated.GIT_CONFIG_KEY_0, undefined);
});

test('a missing guard value is removed and a non-empty value stays', () => {
  const env = {
    GIT_CONFIG_COUNT: '3',
    GIT_CONFIG_KEY_0: GUARD_KEY,
    GIT_CONFIG_VALUE_0: 'https://example.test/',
    GIT_CONFIG_KEY_1: GUARD_KEY,
    GIT_CONFIG_KEY_2: GUARD_KEY,
    GIT_CONFIG_VALUE_2: ''
  };
  stripPushGuard(env);
  assert.equal(env.GIT_CONFIG_COUNT, '1');
  assert.equal(env.GIT_CONFIG_KEY_0, GUARD_KEY);
  assert.equal(env.GIT_CONFIG_VALUE_0, 'https://example.test/');
  assert.equal(env.GIT_CONFIG_KEY_1, undefined);
  assert.equal(env.GIT_CONFIG_VALUE_1, undefined);
  assert.equal(env.GIT_CONFIG_KEY_2, undefined);
});

test('a zero config count clears the count instead of leaving zero', () => {
  const env = { GIT_CONFIG_COUNT: '0', OTHER: 'kept' };
  stripPushGuard(env);
  assert.equal(env.GIT_CONFIG_COUNT, undefined);
  assert.equal(env.OTHER, 'kept');
});
