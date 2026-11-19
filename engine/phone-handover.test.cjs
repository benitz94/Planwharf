'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const activation = require('./phone-activation.cjs');
const handover = require('./phone-handover.cjs');
const { runHandover } = handover;
const handoverPlan = activation.handoverPlan || handover.handoverPlan;
const exchangeEvidence = activation.exchangeEvidence || handover.exchangeEvidence;

const installed = {
  address: 'http://127.0.0.1:4173',
  pid: 13540,
  path: 'C:\\Users\\benit\\Downloads\\Planwharf-native-win-x64-v0.2.85\\Planwharf.exe'
};
const build = 'D:\\benit\\Desktop\\Projects\\Planwharf-engine\\validation\\phone-activation-native\\Planwharf.exe';

function readyHandover(overrides = {}) {
  const calls = [];
  const input = {
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => { calls.push('pause'); return { ok: true }; },
    pollOnce: async () => { calls.push('poll'); return { updates: [] }; },
    replyTo: async () => { throw new Error('must not reply'); },
    startBuild: async () => { calls.push('start'); return { address: 'http://127.0.0.1:4174', model: 'qwen2.5:3b', paired: true }; },
    sendAlert: async () => { calls.push('alert'); return { messageId: 9, text: '⚓ Captain\nStatus: Done ✅' }; },
    stopBuild: async () => { calls.push('stop'); },
    resumePolling: async () => { calls.push('resume'); return { ok: true }; },
    ...overrides
  };
  return { calls, input };
}

test('handover stays blocked until the backup, token and a different build are present', () => {
  const missing = handoverPlan({ installed, updatedBuild: build, backupOk: false, tokenPresent: true });
  assert.equal(missing.allowed, false);
  assert.match(missing.blocker, /backup/);
  const same = handoverPlan({ installed: { ...installed, path: build }, updatedBuild: build, backupOk: true, tokenPresent: true });
  assert.equal(same.allowed, false);
  assert.match(same.blocker, /already polling/);
  const ready = handoverPlan({ installed, updatedBuild: build, backupOk: true, tokenPresent: true });
  assert.equal(ready.allowed, true);
  assert.equal(ready.pause.address, installed.address);
  assert.equal(ready.restore, 're-enable telegram polling on the installed listener');
});

test('exchange evidence keeps the message text and drops a bot token', () => {
  const evidence = exchangeEvidence({
    outbound: { messageId: 9, text: '⚓ Captain\nStatus: Done ✅ token 123456:ABCDEF_secret' },
    inbound: { updateId: 4, text: 'how is Harbor?' },
    reply: { messageId: 10, text: '⚓ Captain\nHarbor is on step 2 of 4.' }
  });
  assert.equal(evidence.outbound.messageId, 9);
  assert.match(evidence.outbound.text, /Done/);
  assert.equal(evidence.outbound.text.includes('ABCDEF_secret'), false);
  assert.equal(evidence.inbound.text, 'how is Harbor?');
  assert.match(evidence.reply.text, /step 2 of 4/);
});

test('a paused listener is restored when the updated build sends the alert and a natural reply', async () => {
  const calls = [];
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => { calls.push('pause'); return { ok: true }; },
    pollOnce: async () => ({ updates: [{ update_id: 4, message: { text: 'how is Harbor?' } }] }),
    replyTo: async message => { calls.push(message.text); return { messageId: 10, text: '⚓ Captain\nHarbor is on step 2 of 4.' }; },
    startBuild: async () => { calls.push('start'); return { address: 'http://127.0.0.1:4174', model: 'qwen2.5:3b', paired: true }; },
    sendAlert: async () => ({ messageId: 9, text: '⚓ Captain\nPlanwharf: Planwharf Test\nStatus: Done ✅' }),
    stopBuild: async () => { calls.push('stop'); },
    resumePolling: async () => { calls.push('resume'); return { ok: true }; }
  });
  assert.equal(result.ok, true);
  assert.equal(result.restored, true);
  assert.deepEqual(calls, ['pause', 'how is Harbor?', 'start', 'stop', 'resume']);
  assert.equal(result.started.model, 'qwen2.5:3b');
  assert.match(result.evidence.reply.text, /Harbor/);
  assert.equal(JSON.stringify(result).includes('123456:secret'), false);
});

test('a failed pause does not start the updated build', async () => {
  let started = false;
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => ({ ok: false }),
    pollOnce: async () => { throw new Error('must not poll'); },
    replyTo: async () => { throw new Error('must not reply'); },
    startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174' }; },
    sendAlert: async () => ({ text: 'nope' }),
    stopBuild: async () => {},
    resumePolling: async () => ({ ok: true })
  });
  assert.equal(result.ok, false);
  assert.equal(started, false);
});

test('a poll that is still blocked restores the installed listener', async () => {
  let started = false;
  let resumed = false;
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => ({ ok: true }),
    pollOnce: async () => ({ blocked: true, updates: [] }),
    replyTo: async () => { throw new Error('must not reply'); },
    startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174' }; },
    sendAlert: async () => ({ text: 'nope' }),
    stopBuild: async () => {},
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  assert.equal(result.ok, false);
  assert.equal(result.restored, true);
  assert.equal(started, false);
  assert.equal(resumed, true);
  assert.match(result.blocker, /still polling/);
});

test('a handover poll error stops handover, skips build and idle reply, and restores polling', async () => {
  let started = false;
  let idleAsked = false;
  let replied = false;
  let resumed = false;
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => ({ ok: true }),
    pollOnce: async () => ({ updates: [], error: 'Telegram poll error: 401 Unauthorized' }),
    askWhenIdle: async () => { idleAsked = true; return { messageId: 10, text: 'idle' }; },
    replyTo: async () => { replied = true; return { messageId: 10, text: 'reply' }; },
    startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174' }; },
    sendAlert: async () => ({ text: 'alert' }),
    stopBuild: async () => {},
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  assert.equal(result.ok, false);
  assert.equal(result.restored, true);
  assert.equal(started, false);
  assert.equal(idleAsked, false);
  assert.equal(replied, false);
  assert.equal(resumed, true);
  assert.equal(result.blocker, 'Telegram poll error: 401 Unauthorized');
  assert.equal(result.evidence.reply, null);
});

test('the installed listener is polling again when the updated build does not start', async () => {
  let resumed = false;
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => ({ ok: true }),
    pollOnce: async () => ({ updates: [] }),
    replyTo: async () => { throw new Error('no inbound'); },
    startBuild: async () => null,
    sendAlert: async () => { throw new Error('no build'); },
    stopBuild: async () => {},
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  assert.equal(result.ok, false);
  assert.equal(result.restored, true);
  assert.equal(resumed, true);
});

test('a thrown step after pause still restores polling and stops a build that started', async () => {
  const polled = readyHandover({
    pollOnce: async () => { throw new Error('poll dropped'); },
    startBuild: async () => { throw new Error('must not start'); }
  });
  const pollResult = await runHandover(polled.input);
  assert.equal(pollResult.ok, false);
  assert.equal(pollResult.restored, true);
  assert.match(pollResult.blocker, /poll dropped/);
  assert.deepEqual(polled.calls, ['pause', 'resume']);

  const replied = readyHandover({
    pollOnce: async () => ({ updates: [{ update_id: 8, message: { text: 'status?' } }] }),
    replyTo: async () => { throw new Error('reply dropped'); }
  });
  const replyResult = await runHandover(replied.input);
  assert.equal(replyResult.restored, true);
  assert.equal(replyResult.evidence.inbound.text, 'status?');
  assert.equal(replied.calls.includes('start'), false);
  assert.equal(replied.calls.at(-1), 'resume');

  let stopped = false;
  const alerted = readyHandover({
    sendAlert: async () => { throw new Error('alert dropped'); },
    stopBuild: async () => { stopped = true; }
  });
  const alertResult = await runHandover(alerted.input);
  assert.equal(alertResult.ok, false);
  assert.equal(alertResult.restored, true);
  assert.equal(stopped, true);
  assert.match(alertResult.blocker, /alert dropped/);

  let stoppedAfterStart = false;
  const started = readyHandover({
    startBuild: async () => { throw new Error('build missing'); },
    stopBuild: async () => { stoppedAfterStart = true; }
  });
  const startResult = await runHandover(started.input);
  assert.equal(startResult.restored, true);
  assert.equal(stoppedAfterStart, false);
  assert.match(startResult.blocker, /build missing/);
  assert.equal(started.calls.at(-1), 'resume');
});

test('stopping the updated build cannot skip restoring the installed listener', async () => {
  const { calls, input } = readyHandover({
    stopBuild: async () => { calls.push('stop'); throw new Error('kill failed'); }
  });
  const result = await runHandover(input);
  assert.equal(result.restored, true);
  assert.equal(result.ok, true);
  assert.deepEqual(calls, ['pause', 'poll', 'start', 'alert', 'stop', 'resume']);
});

test('an empty alert is kept as a failed exchange and polling still returns', async () => {
  let resumed = false;
  const { input } = readyHandover({
    sendAlert: async () => ({ messageId: 11, text: '' }),
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  const result = await runHandover(input);
  assert.equal(result.ok, false);
  assert.equal(result.restored, true);
  assert.equal(resumed, true);
  assert.equal(result.evidence.outbound.text, '');
});

test('no pending message uses the idle question and records that reply', async () => {
  const calls = [];
  const result = await runHandover({
    ...readyHandover().input,
    pollOnce: async () => ({ updates: [{ update_id: 1, message: { text: '' } }, { update_id: 2 }] }),
    askWhenIdle: async () => { calls.push('idle'); return { messageId: 12, text: '⚓ Captain\nAll systems are idle.' }; },
    replyTo: async () => { throw new Error('blank text is not a question'); }
  });
  assert.equal(result.ok, true);
  assert.deepEqual(calls, ['idle']);
  assert.match(result.evidence.reply.text, /All systems are idle/);
  assert.equal(result.evidence.inbound, null);
});

test('a pause that throws does not resume or start, and the next handover can run', async () => {
  let started = false;
  let resumed = false;
  const failed = await runHandover({
    ...readyHandover().input,
    pausePolling: async () => { throw new Error('settings down'); },
    startBuild: async () => { started = true; },
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  assert.equal(failed.ok, false);
  assert.equal(failed.restored, false);
  assert.match(failed.blocker, /settings down/);
  assert.equal(started, false);
  assert.equal(resumed, false);
  const next = await runHandover(readyHandover().input);
  assert.equal(next.ok, true);
});

test('a second handover waits out the first instead of pausing the listener twice', async () => {
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let secondPaused = false;
  const firstInput = readyHandover({
    pollOnce: async () => { await gate; return { updates: [] }; }
  }).input;
  const first = runHandover(firstInput);
  await new Promise(resolve => setImmediate(resolve));
  const second = await runHandover({
    ...readyHandover().input,
    pausePolling: async () => { secondPaused = true; return { ok: true }; }
  });
  assert.equal(second.ok, false);
  assert.equal(second.restored, false);
  assert.equal(secondPaused, false);
  assert.match(second.blocker, /already running/);
  release();
  const finished = await first;
  assert.equal(finished.ok, true);
  assert.equal(finished.restored, true);
  const after = await runHandover(readyHandover().input);
  assert.equal(after.ok, true);
});

test('resume failure is reported after the alert and the build has stopped', async () => {
  let stopped = false;
  const { input } = readyHandover({
    stopBuild: async () => { stopped = true; },
    resumePolling: async () => ({ ok: false })
  });
  const result = await runHandover(input);
  assert.equal(stopped, true);
  assert.equal(result.ok, false);
  assert.equal(result.restored, false);
  assert.match(result.evidence.outbound.text, /Done/);
});

test('a thrown resume is a failed restore after the build stops', async () => {
  let stopped = false;
  const result = await runHandover({
    ...readyHandover().input,
    stopBuild: async () => { stopped = true; },
    resumePolling: async () => { throw new Error('listener down'); }
  });
  assert.equal(stopped, true);
  assert.equal(result.ok, false);
  assert.equal(result.restored, false);
  assert.match(result.evidence.outbound.text, /Done/);
});

test('a missing token never pauses the installed listener', async () => {
  let paused = false;
  const result = await runHandover({
    ...readyHandover().input,
    token: '',
    pausePolling: async () => { paused = true; return { ok: true }; }
  });
  assert.equal(result.ok, false);
  assert.equal(result.plan.allowed, false);
  assert.equal(paused, false);
});

test('a 409 conflict retries and stops only if the other poller never clears, not as an immediate stop', async () => {
  const { createLivePoller } = require('./phone-handover-live.cjs');
  let pollAttempts = 0;
  const waits = [];
  const poller = createLivePoller({
    stored: { telegramToken: '123456:secret', telegram: { updateOffset: 0 } },
    pollTelegramUpdatesImpl: async () => {
      pollAttempts += 1;
      return { ok: false, updates: [], reason: 'HTTP 409 Conflict: terminated by other getUpdates request' };
    },
    waitImpl: async ms => { waits.push(ms); }
  });

  const polled = await poller.pollOnce();
  assert.equal(pollAttempts, 8);
  assert.equal(waits.length, 8);
  assert.equal(polled.blocked, true);
  assert.match(polled.error, /409/);

  let started = false;
  let idleAsked = false;
  let resumed = false;
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => ({ ok: true }),
    pollOnce: async () => polled,
    askWhenIdle: async () => { idleAsked = true; return { messageId: 10, text: 'idle' }; },
    startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174' }; },
    stopBuild: async () => {},
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  assert.equal(result.ok, false);
  assert.equal(result.restored, true);
  assert.equal(started, false);
  assert.equal(idleAsked, false);
  assert.equal(resumed, true);
  assert.match(result.blocker, /still polling/);
});

test('a 409 conflict retries and recovers when the other poller clears', async () => {
  const { createLivePoller } = require('./phone-handover-live.cjs');
  let pollAttempts = 0;
  const waits = [];
  const poller = createLivePoller({
    stored: { telegramToken: '123456:secret', telegram: { updateOffset: 0 } },
    pollTelegramUpdatesImpl: async () => {
      pollAttempts += 1;
      if (pollAttempts < 3) {
        return { ok: false, updates: [], reason: 'HTTP 409 Conflict' };
      }
      return { ok: true, updates: [] };
    },
    waitImpl: async ms => { waits.push(ms); }
  });

  const polled = await poller.pollOnce();
  assert.equal(pollAttempts, 3);
  assert.equal(waits.length, 2);
  assert.equal(polled.ok, true);
  assert.deepEqual(polled.updates, []);
});

test('a non-409 poll error stops immediately without retrying', async () => {
  const { createLivePoller } = require('./phone-handover-live.cjs');
  let pollAttempts = 0;
  const poller = createLivePoller({
    stored: { telegramToken: '123456:secret', telegram: { updateOffset: 0 } },
    pollTelegramUpdatesImpl: async () => {
      pollAttempts += 1;
      return { ok: false, updates: [], reason: 'HTTP 401 Unauthorized' };
    },
    waitImpl: async () => { throw new Error('must not wait or retry on 401'); }
  });

  const polled = await poller.pollOnce();
  assert.equal(pollAttempts, 1);
  assert.deepEqual(polled, { updates: [], error: 'HTTP 401 Unauthorized' });
});

test('a clean poll with no waiting message and no error follows the existing path that sends the idle reply', async () => {
  let idleAsked = false;
  let started = false;
  let resumed = false;
  const result = await runHandover({
    installed,
    updatedBuild: build,
    backupOk: true,
    token: '123456:secret',
    pausePolling: async () => ({ ok: true }),
    pollOnce: async () => ({ ok: true, updates: [] }),
    askWhenIdle: async () => { idleAsked = true; return { messageId: 10, text: 'All systems running smoothly.' }; },
    replyTo: async () => { throw new Error('no message to reply to'); },
    startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174', model: 'qwen2.5:3b', paired: true }; },
    sendAlert: async () => ({ messageId: 9, text: 'Status: Done' }),
    stopBuild: async () => {},
    resumePolling: async () => { resumed = true; return { ok: true }; }
  });
  assert.equal(result.ok, true);
  assert.equal(result.restored, true);
  assert.equal(idleAsked, true);
  assert.equal(started, true);
  assert.equal(resumed, true);
  assert.match(result.evidence.reply.text, /All systems running smoothly/);
});

test('an error object, a failed poll result, and a waiting message still stop without a build or idle reply', async () => {
  const shapes = [
    { updates: [{ update_id: 4, message: { text: 'status?' } }], error: { message: 'socket down' } },
    { ok: false, updates: [], reason: { message: 'disk full' } },
    { ok: false, updates: [], reason: '' },
    { updates: [], error: new Error('poll exploded') },
    { updates: [], error: 503 },
    { updates: [], error: {} },
    { updates: [], error: ' ' }
  ];
  const blockers = ['socket down', 'disk full', 'Handover poll failed.', 'poll exploded', '503', 'Handover poll failed.', ' '];
  for (let index = 0; index < shapes.length; index += 1) {
    let started = false;
    let idleAsked = false;
    let replied = false;
    let alerted = false;
    let resumed = null;
    const result = await runHandover({
      ...readyHandover().input,
      pollOnce: async () => shapes[index],
      askWhenIdle: async () => { idleAsked = true; return { messageId: 1, text: 'idle' }; },
      replyTo: async () => { replied = true; return { messageId: 2, text: 'reply' }; },
      startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174' }; },
      sendAlert: async () => { alerted = true; return { text: 'alert' }; },
      resumePolling: async listener => { resumed = listener; return { ok: true }; }
    });
    assert.equal(result.ok, false, blockers[index]);
    assert.equal(result.restored, true, blockers[index]);
    assert.equal(result.blocker, blockers[index]);
    assert.equal(started, false);
    assert.equal(idleAsked, false);
    assert.equal(replied, false);
    assert.equal(alerted, false);
    assert.equal(resumed, installed);
    assert.equal(result.evidence.reply, null);
    assert.equal(result.evidence.outbound, null);
  }
});

test('a poll error restores the installed listener even when resume throws, then the next handover can run', async () => {
  let started = false;
  let resumeAttempts = 0;
  const failed = await runHandover({
    ...readyHandover().input,
    pollOnce: async () => ({ updates: [], error: 'network down' }),
    startBuild: async () => { started = true; return { address: 'http://127.0.0.1:4174' }; },
    resumePolling: async () => { resumeAttempts += 1; throw new Error('listener down'); }
  });
  assert.equal(started, false);
  assert.equal(resumeAttempts, 1);
  assert.equal(failed.ok, false);
  assert.equal(failed.restored, false);
  assert.equal(failed.blocker, 'network down');
  const next = await runHandover(readyHandover().input);
  assert.equal(next.ok, true);
  assert.equal(next.restored, true);
});

test('a poll error with no resume function does not start the build and leaves polling unrestored', async () => {
  const { calls, input } = readyHandover({
    pollOnce: async () => ({ updates: [], error: 'network down' }),
    startBuild: async () => { calls.push('start'); return { address: 'http://127.0.0.1:4174' }; }
  });
  delete input.resumePolling;
  const result = await runHandover(input);
  assert.equal(result.ok, false);
  assert.equal(result.restored, false);
  assert.equal(result.blocker, 'network down');
  assert.deepEqual(calls, ['pause']);
});

test('a second handover does not pause while the first poll is failing', async () => {
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let firstStarted = false;
  let firstIdle = false;
  let secondPaused = false;
  const first = runHandover({
    ...readyHandover().input,
    pollOnce: async () => { await gate; return { updates: [], error: { message: 'network down' } }; },
    askWhenIdle: async () => { firstIdle = true; return { text: 'idle' }; },
    startBuild: async () => { firstStarted = true; return { address: 'http://127.0.0.1:4174' }; }
  });
  await new Promise(resolve => setImmediate(resolve));
  const second = await runHandover({
    ...readyHandover().input,
    pausePolling: async () => { secondPaused = true; return { ok: true }; }
  });
  assert.equal(second.ok, false);
  assert.equal(second.restored, false);
  assert.equal(secondPaused, false);
  assert.match(second.blocker, /already running/);
  release();
  const finished = await first;
  assert.equal(finished.ok, false);
  assert.equal(finished.restored, true);
  assert.equal(finished.blocker, 'network down');
  assert.equal(firstStarted, false);
  assert.equal(firstIdle, false);
  const after = await runHandover(readyHandover().input);
  assert.equal(after.ok, true);
});

test('live poll errors stop immediately, keep 409 retries, and recover without moving the ack', async () => {
  const { createLivePoller } = require('./phone-handover-live.cjs');
  const stored = { telegramToken: '123456:ABCDEF_secret', telegram: { updateOffset: 4 } };

  const thrown = createLivePoller({
    stored,
    pollTelegramUpdatesImpl: async () => { throw new Error('fail 123456:ABCDEF_secret'); },
    waitImpl: async () => { throw new Error('must not wait'); }
  });
  assert.deepEqual(await thrown.pollOnce(), { updates: [], error: 'fail [redacted]' });
  assert.equal(thrown.getPendingAck(), 0);

  let attempts = 0;
  const changed = createLivePoller({
    stored,
    pollTelegramUpdatesImpl: async () => {
      attempts += 1;
      if (attempts === 1) return { ok: false, reason: { message: 'HTTP 409 Conflict' } };
      return { ok: false, error: { message: 'HTTP 500 from 123456:ABCDEF_secret' } };
    },
    waitImpl: async () => {}
  });
  assert.deepEqual(await changed.pollOnce(), { updates: [], error: 'HTTP 500 from [redacted]' });
  assert.equal(attempts, 2);
  assert.equal(changed.getPendingAck(), 0);

  let conflicts = 0;
  const objectConflict = createLivePoller({
    stored,
    pollTelegramUpdatesImpl: async () => {
      conflicts += 1;
      return { ok: false, error: { message: 'Conflict: terminated by other getUpdates request' } };
    },
    waitImpl: async () => {}
  });
  const blocked = await objectConflict.pollOnce();
  assert.equal(conflicts, 8);
  assert.equal(blocked.blocked, true);
  assert.match(blocked.error, /Conflict/);
  assert.equal(objectConflict.getPendingAck(), 0);

  let reads = 0;
  const recovered = createLivePoller({
    stored,
    pollTelegramUpdatesImpl: async () => {
      reads += 1;
      if (reads < 8) return { ok: false, reason: 'HTTP 409 Conflict' };
      return { ok: true, updates: [{ update_id: 11 }, { update_id: 7 }] };
    },
    waitImpl: async () => {}
  });
  const polled = await recovered.pollOnce();
  assert.equal(reads, 8);
  assert.equal(polled.ok, true);
  assert.equal(recovered.getPendingAck(), 12);

  const empty = createLivePoller({
    stored,
    pollTelegramUpdatesImpl: async () => null,
    waitImpl: async () => { throw new Error('must not retry a null poll'); }
  });
  assert.deepEqual(await empty.pollOnce(), { updates: [], error: 'Telegram poll failed.' });
  assert.equal(empty.getPendingAck(), 0);

  let thrownConflict = 0;
  const thrownDuringRetry = createLivePoller({
    stored,
    pollTelegramUpdatesImpl: async () => {
      thrownConflict += 1;
      if (thrownConflict === 1) return { ok: false, reason: 409 };
      throw new Error('HTTP 409 Conflict dropped');
    },
    waitImpl: async () => {}
  });
  assert.deepEqual(await thrownDuringRetry.pollOnce(), { updates: [], error: 'HTTP 409 Conflict dropped' });
  assert.equal(thrownConflict, 2);
  assert.equal(thrownDuringRetry.getPendingAck(), 0);
});
