'use strict';
// A Planwharf worker inherits a Git push guard and the desktop app's version.
// Those belong to the worker launch, not to a fixture repo or a portable install
// record the tests build themselves. Other Git config entries stay.

const GUARD_KEY = 'url.planwharf-push-blocked://.pushInsteadOf';

function stripPushGuard(env = process.env) {
  const count = Number.parseInt(env.GIT_CONFIG_COUNT, 10);
  if (!Number.isInteger(count) || count < 0 || count > 1000) return env;
  const kept = [];
  for (let index = 0; index < count; index += 1) {
    const key = env['GIT_CONFIG_KEY_' + index];
    const value = env['GIT_CONFIG_VALUE_' + index];
    if (key === GUARD_KEY && (value === '' || value === undefined)) continue;
    kept.push([key, value]);
  }
  for (let index = 0; index < count; index += 1) {
    delete env['GIT_CONFIG_KEY_' + index];
    delete env['GIT_CONFIG_VALUE_' + index];
  }
  if (!kept.length) delete env.GIT_CONFIG_COUNT;
  else {
    env.GIT_CONFIG_COUNT = String(kept.length);
    kept.forEach(([key, value], index) => {
      if (key !== undefined) env['GIT_CONFIG_KEY_' + index] = key;
      if (value !== undefined) env['GIT_CONFIG_VALUE_' + index] = value;
    });
  }
  return env;
}

function stripWorkerLaunchEnv(env = process.env) {
  stripPushGuard(env);
  delete env.PLANWHARF_DESKTOP;
  delete env.PLANWHARF_DESKTOP_VERSION;
  return env;
}

module.exports = { GUARD_KEY, stripPushGuard, stripWorkerLaunchEnv };
