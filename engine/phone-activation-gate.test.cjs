'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  DEFAULT_OLLAMA_MODEL,
  assessPairedActivation,
  isolatedPhoneSettings,
  backupNames,
  samePath,
  writeIsolatedNotebook,
  copyBackup,
  liveExchange,
  reportMarkdown
} = require('./phone-activation.cjs');

test('paired verification stays off while the installed listener owns the bot', () => {
  const installed = 'C:\\Users\\benit\\Downloads\\Planwharf-native-win-x64-v0.2.85\\Planwharf.exe';
  const blocked = assessPairedActivation({
    listeners: [{ running: true, pollsTelegram: true, path: installed, pid: 13540 }],
    thisBuildPath: 'D:\\benit\\Desktop\\Projects\\Planwharf-engine\\validation\\phone-activation-native\\Planwharf.exe',
    tokenPresent: true,
    isolatedHasToken: false
  });
  assert.equal(blocked.allowed, false);
  assert.match(blocked.blocker, /already polling Telegram/);
  assert.match(blocked.blocker, /getUpdates/);
  assert.equal(blocked.installedListeners[0].pid, 13540);
  assert.equal(blocked.model, DEFAULT_OLLAMA_MODEL);
  const asObject = assessPairedActivation({
    listeners: { running: true, pollsTelegram: true, path: installed, pid: 13540 },
    tokenPresent: true
  });
  assert.equal(asObject.allowed, false);
  assert.equal(asObject.installedListeners.length, 1);

  const clear = assessPairedActivation({
    listeners: [{ running: false, pollsTelegram: true, path: installed, pid: 1 }],
    tokenPresent: true,
    isolatedHasToken: false,
    model: 'qwen2.5:3b'
  });
  assert.equal(clear.allowed, true);
  assert.equal(clear.blocker, '');
});

test('a token inside the isolated notebook and a missing token both block live polling', () => {
  const withToken = assessPairedActivation({ tokenPresent: true, isolatedHasToken: true, listeners: [] });
  assert.equal(withToken.allowed, false);
  assert.match(withToken.blocker, /isolated validation notebook contains a bot token/);

  const noToken = assessPairedActivation({ tokenPresent: false, isolatedHasToken: false, listeners: [] });
  assert.equal(noToken.allowed, false);
  assert.match(noToken.blocker, /No bot token/);

  const ownProcess = assessPairedActivation({
    listeners: [{ running: true, pollsTelegram: true, path: 'D:/build/Planwharf.exe', pid: 9 }],
    thisBuildPath: 'D:\\build\\Planwharf.exe',
    tokenPresent: true
  });
  assert.equal(ownProcess.allowed, true);
  assert.equal(samePath('D:/build/Planwharf.exe', 'D:\\build\\Planwharf.exe'), true);
});

test('the isolated notebook defaults to qwen2.5:3b and keeps the bot token out', () => {
  const settings = isolatedPhoneSettings();
  assert.equal(settings.ollama.enabled, true);
  assert.equal(settings.ollama.model, 'qwen2.5:3b');
  assert.equal(settings.ollama.baseUrl, 'http://127.0.0.1:11434');
  assert.equal(settings.telegram.enabled, false);
  assert.equal(settings.telegram.pairedChatId, null);
  assert.equal(Object.hasOwn(settings, 'telegramToken'), false);
  assert.deepEqual(backupNames(), ['phone-settings.json', 'telegram-bot.token', 'plan.json']);
});

test('the isolated notebook and backup stay recoverable and do not poll when blocked', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-activation-'));
  const from = path.join(root, 'from');
  const backup = path.join(root, 'backup');
  const notebook = path.join(root, 'notebook');
  fs.mkdirSync(from, { recursive: true });
  fs.writeFileSync(path.join(from, 'plan.json'), '{"schema":2,"projects":[]}\n');
  fs.writeFileSync(path.join(from, 'phone-settings.json'), '{"telegram":{"enabled":true}}\n');
  fs.writeFileSync(path.join(from, 'telegram-bot.token'), '123456:secret\n');
  const starter = path.join(root, 'starter.json');
  fs.writeFileSync(starter, '{"schema":2,"projects":[{"project":{"id":"isolated","name":"Isolated"}}]}\n');
  try {
    const copied = copyBackup(from, backup);
    assert.equal(copied.files.length, 3);
    assert.equal(fs.readFileSync(path.join(backup, 'plan.json'), 'utf8'), fs.readFileSync(path.join(from, 'plan.json'), 'utf8'));
    const manifest = JSON.parse(fs.readFileSync(path.join(backup, 'manifest.json'), 'utf8'));
    assert.equal(manifest.files.length, 3);
    assert.equal(manifest.files.some(file => String(file.sha256).length === 64), true);

    const made = writeIsolatedNotebook(notebook, starter);
    const settings = JSON.parse(fs.readFileSync(path.join(made.attempts, 'phone-settings.json'), 'utf8'));
    assert.equal(settings.ollama.model, 'qwen2.5:3b');
    assert.equal(fs.existsSync(path.join(made.attempts, 'telegram-bot.token')), false);
    fs.writeFileSync(path.join(made.attempts, 'telegram-bot.token'), 'should-be-removed');
    writeIsolatedNotebook(notebook, starter);
    assert.equal(fs.existsSync(path.join(made.attempts, 'telegram-bot.token')), false);

    let called = 0;
    const blocked = await liveExchange({ allowed: false, token: '123456:secret', fetchImpl: async () => { called += 1; return { ok: true, json: async () => ({ ok: true, result: [] }) }; } });
    assert.equal(blocked.ran, false);
    assert.equal(called, 0);
    const open = await liveExchange({
      allowed: true,
      token: '123456:secret',
      fetchImpl: async url => {
        called += 1;
        assert.match(url, /bot123456:secret\/getUpdates/);
        return { ok: true, json: async () => ({ ok: true, result: [{ update_id: 1 }] }) };
      }
    });
    assert.equal(open.ran, true);
    assert.equal(open.updates, 1);
    const text = reportMarkdown({ assessment: { allowed: false, model: 'qwen2.5:3b', blocker: 'listener' }, probe: { installed: true }, backup: copied, exchange: blocked, build: 'native' });
    assert.match(text, /qwen2\.5:3b/);
    assert.match(text, /does not call Telegram getUpdates/);
    assert.equal(text.includes('secret'), false);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
