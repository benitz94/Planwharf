'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const {
  isClearMemory,
  normalizeOllamaSettings,
  DEFAULT_OLLAMA_MODEL,
  DEFAULT_OLLAMA_URL
} = require('./phone-ollama.cjs');
const { samePath } = require('./phone-activation.cjs');

test('isClearMemory handles bot suffix, case variance, and rejects non-clear commands', () => {
  assert.equal(isClearMemory('/clear'), true);
  assert.equal(isClearMemory('/clear@PlanwharfBot'), true);
  assert.equal(isClearMemory('/FORGET@captain_bot'), true);
  assert.equal(isClearMemory('  /clear@SomeBot  '), true);
  assert.equal(isClearMemory('clear memory'), true);
  assert.equal(isClearMemory('forget this chat'), true);

  assert.equal(isClearMemory('/clearance'), false);
  assert.equal(isClearMemory('/clear something'), false);
  assert.equal(isClearMemory('/forgetful'), false);
  assert.equal(isClearMemory(''), false);
  assert.equal(isClearMemory(null), false);
  assert.equal(isClearMemory(undefined), false);
});

test('normalizeOllamaSettings validates model patterns and loopback URL variations', () => {
  const custom = normalizeOllamaSettings({
    model: 'hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M',
    baseUrl: 'http://127.0.0.1:11434/api/extra'
  });
  assert.equal(custom.model, 'hf.co/bartowski/L3-8B-Stheno-v3.2-GGUF:Q4_K_M');
  assert.equal(custom.baseUrl, 'http://127.0.0.1:11434');
  assert.equal(custom.enabled, true);

  const ipv6 = normalizeOllamaSettings({ baseUrl: 'http://[::1]:11434' });
  assert.equal(ipv6.baseUrl, 'http://[::1]:11434');

  const localhost = normalizeOllamaSettings({ baseUrl: 'http://localhost:11434/' });
  assert.equal(localhost.baseUrl, 'http://localhost:11434');

  const invalidModel = normalizeOllamaSettings({ model: '   ' });
  assert.equal(invalidModel.model, DEFAULT_OLLAMA_MODEL);

  const maliciousModel = normalizeOllamaSettings({ model: 'bad$model`name' });
  assert.equal(maliciousModel.model, DEFAULT_OLLAMA_MODEL);

  const external = normalizeOllamaSettings({ baseUrl: 'http://192.168.1.50:11434' });
  assert.equal(external.baseUrl, DEFAULT_OLLAMA_URL);

  const credentials = normalizeOllamaSettings({ baseUrl: 'http://admin:pass@127.0.0.1:11434' });
  assert.equal(credentials.baseUrl, DEFAULT_OLLAMA_URL);
});

test('samePath normalizes relative, absolute, whitespace, and slash differences', () => {
  const abs = path.resolve('validation/phone-activation-native/Planwharf.exe');
  const rel = 'validation/phone-activation-native/Planwharf.exe';
  const withWhitespace = `  ${rel}  `;
  const windowsRel = rel.replace(/\//g, '\\');

  assert.equal(samePath(rel, abs), true);
  assert.equal(samePath(withWhitespace, abs), true);
  assert.equal(samePath(windowsRel, abs), true);
  assert.equal(samePath(rel, windowsRel), true);
  assert.equal(samePath('', abs), false);
  assert.equal(samePath(null, abs), false);
  assert.equal(samePath(undefined, abs), false);
  assert.equal(samePath('D:/another/path/Planwharf.exe', abs), false);
});

const fs = require('node:fs');
const os = require('node:os');
const {
  copyBackup,
  liveExchange,
  assessPairedActivation
} = require('./phone-activation.cjs');

test('copyBackup recovers files located in .attempts-plan fallback', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-backup-verify-'));
  try {
    const from = path.join(tmp, 'notebook');
    const attempts = path.join(from, '.attempts-plan');
    const to = path.join(tmp, 'backup');
    fs.mkdirSync(attempts, { recursive: true });

    fs.writeFileSync(path.join(from, 'plan.json'), '{"schema":2}\n');
    fs.writeFileSync(path.join(attempts, 'phone-settings.json'), '{"telegram":{"enabled":false}}\n');
    fs.writeFileSync(path.join(attempts, 'telegram-bot.token'), '999999:backup-token\n');

    // Call copyBackup without explicit attemptsDir
    const result = copyBackup(from, to);
    assert.equal(result.files.length, 3);
    assert.ok(fs.existsSync(path.join(to, 'plan.json')));
    assert.ok(fs.existsSync(path.join(to, 'phone-settings.json')));
    assert.ok(fs.existsSync(path.join(to, 'telegram-bot.token')));
    assert.ok(fs.existsSync(path.join(to, 'manifest.json')));

    const manifest = JSON.parse(fs.readFileSync(path.join(to, 'manifest.json'), 'utf8'));
    assert.equal(manifest.files.length, 3);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('liveExchange recovers from network errors and exceptions without throwing', async () => {
  const failingFetch = async () => {
    throw new Error('ETIMEDOUT: Connection timed out');
  };
  const result = await liveExchange({
    allowed: true,
    token: 'fake-token',
    fetchImpl: failingFetch
  });
  assert.equal(result.ran, true);
  assert.equal(result.ok, false);
  assert.equal(result.updates, 0);
  assert.match(result.reason, /ETIMEDOUT/);

  // When not allowed, does not invoke fetch
  let called = false;
  const blockedResult = await liveExchange({
    allowed: false,
    token: 'fake-token',
    fetchImpl: async () => { called = true; }
  });
  assert.equal(blockedResult.ran, false);
  assert.equal(blockedResult.reason, 'blocked');
  assert.equal(called, false);
});

test('assessPairedActivation handles mixed listeners and relative build path', () => {
  const thisBuild = 'validation/phone-activation-native/Planwharf.exe';
  const installedApp = 'C:\\Program Files\\Planwharf\\Planwharf.exe';

  // Mixed listeners list: one stopped, one not polling, one active external listener
  const mixed = assessPairedActivation({
    listeners: [
      { running: false, pollsTelegram: true, path: installedApp, pid: 101 },
      { running: true, pollsTelegram: false, path: installedApp, pid: 102 },
      { running: true, pollsTelegram: true, path: installedApp, pid: 103 }
    ],
    thisBuildPath: thisBuild,
    tokenPresent: true
  });
  assert.equal(mixed.allowed, false);
  assert.equal(mixed.installedListeners.length, 1);
  assert.equal(mixed.installedListeners[0].pid, 103);

  // Own build matching with relative vs absolute
  const ownBuildOnly = assessPairedActivation({
    listeners: [
      { running: true, pollsTelegram: true, path: path.resolve(thisBuild), pid: 200 }
    ],
    thisBuildPath: thisBuild,
    tokenPresent: true
  });
  assert.equal(ownBuildOnly.allowed, true);
  assert.equal(ownBuildOnly.installedListeners.length, 0);
});

const {
  createChatMemory,
  saveChatMemory,
  loadChatMemory
} = require('./phone-ollama.cjs');
const { createPhoneNotifier, savePhoneSettings } = require('./phone.cjs');

test('concurrent saveChatMemory calls do not collide or corrupt store', async () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-memory-concurrency-'));
  try {
    const memory = createChatMemory({ maxMessages: 5 });
    // Write 20 parallel updates
    await Promise.all(
      Array.from({ length: 20 }, (_, i) => {
        memory.remember('chat-concurrent', 'user', `msg-${i}`);
        return Promise.resolve().then(() => saveChatMemory(tmp, memory));
      })
    );
    const loaded = loadChatMemory(tmp);
    const history = loaded.history('chat-concurrent');
    assert.equal(history.length, 5);
    // Last message should be within the range
    assert.match(history.at(-1).text, /msg-\d+/);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('telegram bot handles /clear@BotName in live notifier without leaking memory', async () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-bot-clear-'));
  try {
    savePhoneSettings(tmp, {
      telegram: { enabled: true, pairedChatId: '55', pairedUsername: 'tester' },
      ollama: { enabled: true, model: 'qwen2.5:3b' }
    }, '123456:clear-test-token');

    const sent = [];
    let chatCalls = 0;
    const notifier = createPhoneNotifier({
      storeDir: tmp,
      getSnapshot: async () => ({ projects: [] }),
      fetchImpl: async (url, options = {}) => {
        const u = String(url);
        if (u.includes('/api/chat')) {
          chatCalls++;
          return { ok: true, json: async () => ({ message: { content: 'I remember you.' } }) };
        }
        if (options.body) {
          sent.push(JSON.parse(options.body));
        }
        return { ok: true, json: async () => ({ ok: true, result: {} }) };
      }
    });

    // 1. Send normal message -> triggers Ollama
    await notifier.processTelegramMessage({ chat: { id: 55 }, text: 'remember this secret fact' });
    assert.equal(chatCalls, 1);

    // 2. Send /clear@PlanwharfBot -> should clear memory and NOT call Ollama
    await notifier.processTelegramMessage({ chat: { id: 55 }, text: '/clear@PlanwharfBot' });
    assert.equal(chatCalls, 1);
    assert.match(sent.at(-1).text, /⚓ Captain/);
    assert.match(sent.at(-1).text, /chat memory is clear/);

    // 3. Verify memory is empty on disk
    const reloaded = loadChatMemory(tmp);
    assert.deepEqual(reloaded.history('55'), []);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

test('probeOllama safely handles unreachable Ollama and reports installed status', async () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-probe-edge-'));
  try {
    savePhoneSettings(tmp, {
      ollama: { enabled: true, baseUrl: 'http://127.0.0.1:11434', model: 'qwen2.5:3b' }
    });

    // Unreachable Ollama (fetch rejects)
    const downNotifier = createPhoneNotifier({
      storeDir: tmp,
      fetchImpl: async () => { throw new Error('ECONNREFUSED'); }
    });
    const downProbe = await downNotifier.probeOllama();
    assert.equal(downProbe.ok, false);
    assert.equal(downProbe.installed, false);
    assert.deepEqual(downProbe.models, []);
    assert.equal(downProbe.model, 'qwen2.5:3b');

    // Reachable Ollama with custom models
    const upNotifier = createPhoneNotifier({
      storeDir: tmp,
      fetchImpl: async () => ({
        ok: true,
        json: async () => ({ models: [{ name: 'qwen2.5:3b' }, { name: 'llama3:8b' }] })
      })
    });
    const upProbe = await upNotifier.probeOllama();
    assert.equal(upProbe.ok, true);
    assert.equal(upProbe.installed, true);
    assert.deepEqual(upProbe.models, ['qwen2.5:3b', 'llama3:8b']);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});
