'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createPhoneNotifier, loadPhoneSettings, savePhoneSettings } = require('./phone.cjs');

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phone-activation-'));
}

function projects() {
  return [{ id: 'harbor', name: 'Harbor', status: 'watching', goal: 'Paint the dock', step: { step: 2, total: 4 }, timeLeft: { minutes: 6 } }];
}

function pair(dir) {
  savePhoneSettings(dir, {
    ntfy: { enabled: true, topic: 'dock-alerts', serverUrl: 'http://127.0.0.1:9' },
    telegram: { enabled: true, pairedChatId: '42', pairedUsername: 'captain', startCode: 'pw-activation', updateOffset: 3 },
    ollama: { enabled: true, baseUrl: 'http://127.0.0.1:11434', model: 'qwen2.5:3b' }
  }, '123456:activation-token');
}

test('the telegram listener records a natural reply and a cold start keeps settings, projects and pairing', async () => {
  const dir = tempDir();
  const notebook = projects();
  const updates = [{ update_id: 7, message: { chat: { id: 42, username: 'captain' }, text: 'how is Harbor?' } }];
  const sent = [];
  const chats = [];
  const stops = [];
  const resumes = [];
  let tags = [];
  const fetchImpl = async (url, options = {}) => {
    const target = String(url);
    if (target.includes('getUpdates')) {
      const offset = Number(new URL(target).searchParams.get('offset') || 0);
      const pending = updates.filter(item => item.update_id >= offset);
      return { ok: true, status: 200, json: async () => ({ ok: true, result: pending }) };
    }
    if (target.includes('/api/chat')) {
      const body = JSON.parse(options.body);
      chats.push(body);
      const facts = body.messages[0].content;
      const answer = facts.includes('complete') ? 'Harbor is done.' : 'Harbor is on step 2 of 4.';
      return { ok: true, status: 200, json: async () => ({ message: { content: answer } }) };
    }
    if (target.includes('/api/tags')) {
      return { ok: true, status: 200, json: async () => ({ models: tags.map(name => ({ name })) }) };
    }
    const payload = options.body ? JSON.parse(options.body) : {};
    sent.push({ url: target, payload });
    return { ok: true, status: 200, json: async () => ({ ok: true, result: payload }) };
  };
  const opened = {
    storeDir: dir,
    getSnapshot: async () => ({ projects: notebook.map(item => ({ ...item })) }),
    onStopProject: async id => { stops.push(id); },
    onResumeProject: async id => { resumes.push(id); },
    fetchImpl,
    apiBaseUrl: 'http://fake.telegram.invalid'
  };
  try {
    pair(dir);
    const first = createPhoneNotifier(opened);
    await first.pollUpdates();
    assert.equal(stops.length, 0);
    assert.equal(resumes.length, 0);
    assert.equal(chats.length, 1);
    assert.equal(chats[0].model, 'qwen2.5:3b');
    assert.match(chats[0].messages[0].content, /Harbor: watching/);
    assert.match(chats[0].messages[0].content, /step 2\/4/);
    assert.equal(sent.length, 1);
    assert.match(sent[0].url, /\/sendMessage$/);
    assert.equal(sent[0].payload.chat_id, '42');
    assert.match(sent[0].payload.text, /⚓ Captain/);
    assert.match(sent[0].payload.text, /step 2 of 4/);
    assert.match(chats[0].messages[0].content, /Paint the dock/);
    assert.equal(sent[0].payload.text.includes('[path]'), false);
    assert.equal(loadPhoneSettings(dir).telegram.updateOffset, 8);

    notebook[0] = { ...notebook[0], status: 'complete', goal: 'Paint the dock' };
    const cold = createPhoneNotifier(opened);
    const restored = cold.getSettings();
    assert.equal(restored.telegram.paired, true);
    assert.equal(restored.telegram.pairedChatId, '42');
    assert.equal(restored.telegram.pairedUsername, 'captain');
    assert.equal(restored.telegram.startCode, 'pw-activation');
    assert.equal(restored.telegram.tokenConfigured, true);
    assert.equal(restored.telegram.tokenPreview.includes('123456:activation-token'), false);
    assert.equal(restored.ntfy.enabled, true);
    assert.equal(restored.ntfy.topic, 'dock-alerts');
    assert.equal(restored.ollama.enabled, true);
    assert.equal(restored.ollama.model, 'qwen2.5:3b');
    assert.equal(restored.ollama.baseUrl, 'http://127.0.0.1:11434');
    await cold.pollUpdates();
    assert.equal(chats.length, 1);
    assert.equal(sent.length, 1);

    updates.push({ update_id: 8, message: { chat: { id: 42 }, text: 'and now?' } });
    await cold.pollUpdates();
    assert.equal(chats.length, 2);
    assert.match(chats[1].messages[0].content, /complete/);
    assert.equal(chats[1].messages.some(item => item.content === 'how is Harbor?'), true);
    assert.match(sent.at(-1).payload.text, /Harbor is done/);
    assert.deepEqual(stops, []);
    assert.deepEqual(resumes, []);
    assert.equal(JSON.stringify(notebook[0].goal), '"Paint the dock"');

    tags = ['qwen2.5:3b'];
    const probe = await cold.probeOllama();
    assert.equal(probe.ok, true);
    assert.equal(probe.installed, true);
    assert.equal(probe.model, 'qwen2.5:3b');

    const saved = cold.updateSettings({ ollama: { enabled: true, model: 'qwen3:4b' }, telegram: { enabled: true } });
    assert.equal(saved.telegram.pairedChatId, '42');
    assert.equal(saved.telegram.startCode, 'pw-activation');
    assert.equal(saved.ntfy.topic, 'dock-alerts');
    assert.equal(saved.ollama.model, 'qwen3:4b');
    const disk = loadPhoneSettings(dir);
    assert.equal(disk.telegramToken, '123456:activation-token');
    assert.equal(disk.telegram.pairedChatId, '42');
    assert.equal(disk.telegram.updateOffset, 9);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('a cold server keeps phone settings and notebook projects while the listener fails closed', async () => {
  const { createServer } = require('../server.cjs');
  const root = tempDir();
  const planFile = path.join(root, 'plan.json');
  const attemptsDir = path.join(root, 'attempts');
  fs.copyFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), planFile);
  const before = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  pair(attemptsDir);
  const sent = [];
  const updates = [{ update_id: 4, message: { chat: { id: 42 }, text: 'what is happening?' } }];
  const fetchImpl = async (url, options = {}) => {
    const target = String(url);
    if (target.includes('getUpdates')) {
      const offset = Number(new URL(target).searchParams.get('offset') || 0);
      return { ok: true, status: 200, json: async () => ({ ok: true, result: updates.filter(item => item.update_id >= offset) }) };
    }
    if (target.includes('/api/chat')) {
      return { ok: false, status: 503, json: async () => ({ error: 'down' }) };
    }
    const payload = options.body ? JSON.parse(options.body) : {};
    sent.push(payload);
    return { ok: true, status: 200, json: async () => ({ ok: true, result: payload }) };
  };
  function open() {
    return createServer({ planFile, attemptsDir, refreshModels: false, monitorIntervalMs: 600000 });
  }
  const first = open();
  await new Promise(resolve => first.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${first.address().port}`;
  try {
    const initial = await (await fetch(`${base}/api/settings/phone`)).json();
    assert.equal(initial.telegram.pairedChatId, '42');
    assert.equal(initial.telegram.startCode, 'pw-activation');
    assert.equal(initial.ntfy.topic, 'dock-alerts');
    assert.equal(initial.ollama.model, 'qwen2.5:3b');
    assert.equal(initial.ollama.enabled, true);
    assert.equal(initial.telegramToken, undefined);
    const app = await (await fetch(`${base}/api/app`)).json();
    assert.equal(path.resolve(app.planFile), path.resolve(planFile));
    const saved = await (await fetch(`${base}/api/settings/phone`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ollama: { enabled: true, model: 'qwen3:4b', baseUrl: 'http://example.com' }, telegram: { enabled: true } })
    })).json();
    assert.equal(saved.ollama.model, 'qwen3:4b');
    assert.equal(saved.ollama.baseUrl, 'http://127.0.0.1:11434');
    assert.equal(saved.telegram.pairedChatId, '42');
    assert.equal(saved.ntfy.topic, 'dock-alerts');
    const probed = await (await fetch(`${base}/api/settings/phone/ollama`, { method: 'POST' })).json();
    assert.equal(typeof probed.ok, 'boolean');
    assert.equal(probed.model, 'qwen3:4b');
    assert.equal(JSON.stringify(JSON.parse(fs.readFileSync(planFile, 'utf8')).projects), JSON.stringify(before.projects));

    const listening = createPhoneNotifier({
      storeDir: attemptsDir,
      getSnapshot: async () => ({ projects: projects() }),
      onStopProject: async () => { throw new Error('stop must not run'); },
      onResumeProject: async () => { throw new Error('resume must not run'); },
      fetchImpl,
      apiBaseUrl: 'http://fake.telegram.invalid'
    });
    await listening.pollUpdates();
    assert.equal(sent.length, 1);
    assert.match(sent[0].text, /not answering/);
    assert.match(sent[0].text, /\/status/);
    assert.equal(loadPhoneSettings(attemptsDir).telegram.pairedChatId, '42');
  } finally {
    await new Promise(resolve => first.close(resolve));
  }

  const cold = open();
  await new Promise(resolve => cold.listen(0, '127.0.0.1', resolve));
  const again = `http://127.0.0.1:${cold.address().port}`;
  try {
    const restored = await (await fetch(`${again}/api/settings/phone`)).json();
    assert.equal(restored.telegram.paired, true);
    assert.equal(restored.telegram.pairedChatId, '42');
    assert.equal(restored.telegram.pairedUsername, 'captain');
    assert.equal(restored.ollama.model, 'qwen3:4b');
    assert.equal(restored.ntfy.enabled, true);
    assert.equal(loadPhoneSettings(attemptsDir).telegramToken, '123456:activation-token');
    assert.equal(JSON.stringify(JSON.parse(fs.readFileSync(planFile, 'utf8')).projects), JSON.stringify(before.projects));
  } finally {
    await new Promise(resolve => cold.close(resolve));
    fs.rmSync(root, { recursive: true, force: true });
  }
});
