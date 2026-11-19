'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const {
  sanitizeText,
  sanitizeGoalTitle,
  formatAlertMessage,
  formatTelegramAlertMessage,
  formatProgressBar,
  escapeHtml,
  maskToken,
  loadPhoneSettings,
  savePhoneSettings,
  sendNtfyAlert,
  sendTelegramMessage,
  pollTelegramUpdates,
  formatStatusMessage,
  matchProject,
  createPhoneNotifier
} = require('./phone.cjs');

test('sanitizeText strips absolute paths, relative paths, code files, and secrets', () => {
  const dirty = 'Running in C:\\Projects\\Planwharf-engine\\src\\main.cjs with token=secret123 on /home/user/code/index.js';
  const clean = sanitizeText(dirty);
  assert.ok(!clean.includes('C:\\Projects'));
  assert.ok(!clean.includes('/home/user'));
  assert.ok(!clean.includes('main.cjs'));
  assert.ok(!clean.includes('index.js'));
  assert.ok(!clean.includes('secret123'));
});

test('sanitizeGoalTitle extracts first line, removes paths, and truncates', () => {
  const longGoal = 'Build feature in D:\\repo\\plan.js\nSecond line with private prompt\nThird line';
  const title = sanitizeGoalTitle(longGoal);
  assert.ok(!title.includes('\n'));
  assert.ok(!title.includes('D:\\repo'));
  assert.ok(!title.includes('plan.js'));
  assert.ok(!title.includes('private prompt'));
});

test('formatAlertMessage formats done, failed, and needs_owner without leaking private data', () => {
  const doneMsg = formatAlertMessage({
    type: 'done',
    projectName: 'Planwharf Core',
    goalTitle: 'Improve indexing in C:\\data\\store.json',
    durationMinutes: 45
  });
  assert.ok(doneMsg.includes('Planwharf: Planwharf Core'));
  assert.ok(doneMsg.includes('Status: Done'));
  assert.ok(doneMsg.includes('Duration: 45m'));
  assert.ok(!doneMsg.includes('C:\\data'));
  assert.ok(!doneMsg.includes('store.json'));

  const failedMsg = formatAlertMessage({
    type: 'failed',
    projectName: 'Demo Project',
    goalTitle: 'Unit tests failed with error in ./test/foo.cjs',
    timeLeft: null
  });
  assert.ok(failedMsg.includes('Status: Failed'));
  assert.ok(!failedMsg.includes('./test'));
  assert.ok(!failedMsg.includes('foo.cjs'));
  const { alertForStatus, alertForStep } = require('./phone.cjs');
  assert.equal(alertForStatus('watching', 'checkpointed'), 'stopped');
  assert.equal(alertForStatus('checkpointed', 'watching'), 'resumed');
  assert.equal(alertForStatus('idle', 'watching'), 'started');
  assert.equal(alertForStatus('watching', 'failed'), 'failed');
  assert.equal(alertForStep({ role: 'Build', model: 'Sol', attempt: 'a' }, { role: 'Verify', model: 'Grok', attempt: 'b' }), 'verify');
  assert.equal(alertForStep({ role: 'Build', model: 'Sol', attempt: 'a' }, { role: 'Build', model: 'Gemini', attempt: 'b' }), 'switched');
  assert.equal(alertForStep({ role: 'Build', model: 'Sol', attempt: 'a' }, { role: 'Build', model: 'Sol', attempt: 'c' }), 'restarted');


  const needsOwnerMsg = formatAlertMessage({
    type: 'needs_owner',
    projectName: 'Demo Project',
    goalTitle: 'Stop and ask owner',
    timeLeft: { minutes: 15, longerThanUsual: true, sure: 'rough' }
  });
  assert.ok(needsOwnerMsg.includes('Status: Needs your attention'));
  assert.ok(needsOwnerMsg.includes('About 15m left (longer than usual) · rough'));
});

test('maskToken hides token details while leaving enough to recognize', () => {
  assert.equal(maskToken(null), null);
  assert.equal(maskToken(''), null);
  assert.equal(maskToken('short'), '***');
  assert.equal(maskToken('123456789:ABCDEF_secret_token_123'), '1234..._123');
});

test('loadPhoneSettings and savePhoneSettings persist in storeDir with 0o600 permissions', () => {
  const os = require('node:os');
  const path = require('node:path');
  const fs = require('node:fs');
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-test-'));

  try {
    const initial = loadPhoneSettings(tempDir);
    assert.equal(initial.ntfy.enabled, false);
    assert.equal(initial.ntfy.topic, '');
    assert.equal(initial.telegram.enabled, false);
    assert.ok(initial.telegram.startCode.startsWith('pw-'));
    assert.equal(initial.telegramToken, null);

    const saved = savePhoneSettings(tempDir, {
      ntfy: { enabled: true, topic: 'my-private-topic' },
      telegram: { enabled: true }
    }, '123456:my-bot-token');

    assert.equal(saved.ntfy.enabled, true);
    assert.equal(saved.ntfy.topic, 'my-private-topic');
    assert.equal(saved.telegram.enabled, true);
    assert.equal(saved.telegramToken, '123456:my-bot-token');

    // Check token file exists and is never in a project folder
    const tokenFile = path.join(tempDir, 'telegram-bot.token');
    assert.ok(fs.existsSync(tokenFile));
    assert.equal(fs.readFileSync(tokenFile, 'utf8').trim(), '123456:my-bot-token');

    // Unpair and clear token
    const unpair = savePhoneSettings(tempDir, {
      telegram: { unpair: true }
    }, '');
    assert.equal(unpair.telegram.pairedChatId, null);
    assert.equal(unpair.telegramToken, null);
    assert.ok(!fs.existsSync(tokenFile));
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('HTTP isolation: sendNtfyAlert posts to fake server and handles errors', async () => {
  const http = require('node:http');
  const received = [];

  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      received.push({
        method: req.method,
        url: req.url,
        headers: req.headers,
        body
      });
      if (req.url.includes('error-topic')) {
        res.writeHead(500, { 'Content-Type': 'text/plain' });
        res.end('Server error');
      } else {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ id: 'msg-123' }));
      }
    });
  });

  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const baseUrl = `http://127.0.0.1:${port}`;

  try {
    const resSuccess = await sendNtfyAlert({
      topic: 'test-topic',
      serverUrl: baseUrl,
      message: 'Planwharf: Test Project\nStatus: Done',
      title: 'Planwharf Alert',
      priority: 'high',
      tags: ['tada']
    });
    assert.equal(resSuccess.ok, true);
    assert.equal(received.length, 1);
    assert.equal(received[0].url, '/test-topic');
    assert.equal(received[0].headers['title'], 'Planwharf Alert');
    assert.equal(received[0].headers['priority'], 'high');
    assert.equal(received[0].headers['tags'], 'tada');
    assert.equal(received[0].body, 'Planwharf: Test Project\nStatus: Done');

    const resFail = await sendNtfyAlert({
      topic: 'error-topic',
      serverUrl: baseUrl,
      message: 'Should fail'
    });
    assert.equal(resFail.ok, false);
    assert.equal(resFail.status, 500);
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});

test('HTTP isolation: sendTelegramMessage and pollTelegramUpdates with fake Telegram API', async () => {
  const http = require('node:http');
  const requests = [];

  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      requests.push({ method: req.method, url: req.url, body });
      if (req.url.endsWith('/sendMessage')) {
        const parsed = JSON.parse(body);
        if (parsed.chat_id === 'unauthorized') {
          res.writeHead(403, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ ok: false, description: 'Forbidden: bot was blocked by the user' }));
        } else {
          res.writeHead(200, { 'Content-Type': 'application/json' });
          res.end(JSON.stringify({ ok: true, result: { message_id: 101, text: parsed.text } }));
        }
      } else if (req.url.includes('/getUpdates')) {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({
          ok: true,
          result: [
            { update_id: 1, message: { chat: { id: 12345, username: 'testuser' }, text: '/status' } }
          ]
        }));
      } else {
        res.writeHead(404);
        res.end();
      }
    });
  });

  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const apiBaseUrl = `http://127.0.0.1:${port}`;

  try {
    const msgRes = await sendTelegramMessage({
      token: 'fake-token-123',
      chatId: '98765',
      text: 'Hello from Planwharf',
      apiBaseUrl
    });
    assert.equal(msgRes.ok, true);
    assert.equal(requests[0].url, '/botfake-token-123/sendMessage');
    const sent = JSON.parse(requests[0].body);
    assert.equal(sent.chat_id, '98765');
    assert.equal(sent.text, 'Hello from Planwharf');
    assert.equal(sent.parse_mode, 'HTML');

    const failRes = await sendTelegramMessage({
      token: 'fake-token-123',
      chatId: 'unauthorized',
      text: 'Hi',
      apiBaseUrl
    });
    assert.equal(failRes.ok, false);
    assert.ok(failRes.reason.includes('Forbidden'));

    const updatesRes = await pollTelegramUpdates({
      token: 'fake-token-123',
      offset: 0,
      apiBaseUrl
    });
    assert.equal(updatesRes.ok, true);
    assert.equal(updatesRes.updates.length, 1);
    assert.equal(updatesRes.updates[0].message.text, '/status');
  } finally {
    await new Promise(resolve => server.close(resolve));
  }
});

test('formatStatusMessage lists projects without exposing files, paths or prompts', () => {
  const projects = [
    {
      id: 'proj-1',
      name: 'Harbor App in C:\\Source\\Secret',
      status: 'watching',
      step: { step: 2, total: 4 },
      timeLeft: { minutes: 25, endsAt: '2026-11-17T18:00:00Z' }
    },
    {
      id: 'proj-2',
      name: 'Website /var/www/site/index.html',
      status: 'complete'
    }
  ];

  const statusText = formatStatusMessage(projects);
  assert.ok(statusText.startsWith('⚓ Captain'));
  assert.ok(statusText.includes('<b>Harbor Status</b>'));
  assert.ok(statusText.includes('<b>Harbor App in [path]</b>'));
  assert.ok(statusText.includes('🟢 <b>Status:</b> Working · ~25m left'));
  assert.ok(statusText.includes('■■□□ 2/4'));
  assert.ok(statusText.includes('<b>Website [path]</b>'));
  assert.ok(statusText.includes('✅ <b>Status:</b> Done'));
  assert.ok(!statusText.includes('C:\\Source\\Secret'));
  assert.ok(!statusText.includes('/var/www/site/index.html'));
});

test('Telegram bot lifecycle: pairing with start code, ignoring foreign chats, and commands', async () => {
  const os = require('node:os');
  const path = require('node:path');
  const fs = require('node:fs');
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-bot-test-'));

  const sentMessages = [];
  const fakeFetch = async (url, options = {}) => {
    if (url.includes('/sendMessage')) {
      const body = JSON.parse(options.body);
      sentMessages.push({ url, ...body });
      return {
        ok: true,
        status: 200,
        json: async () => ({ ok: true, result: { message_id: 1, text: body.text } })
      };
    }
    return { ok: false, status: 404, json: async () => ({ ok: false }) };
  };

  let stoppedProject = null;
  let resumedProject = null;

  const mockSnapshot = {
    projects: [
      {
        id: 'p1',
        name: 'Alpha Project',
        status: 'watching',
        step: { step: 1, total: 3 },
        timeLeft: { minutes: 12 }
      },
      {
        id: 'p2',
        name: 'Beta Project',
        status: 'checkpointed'
      }
    ]
  };

  try {
    const notifier = createPhoneNotifier({
      storeDir: tempDir,
      getSnapshot: async () => mockSnapshot,
      onStopProject: async id => { stoppedProject = id; },
      onResumeProject: async id => { resumedProject = id; },
      fetchImpl: fakeFetch,
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    // Configure telegram with token
    notifier.updateSettings({
      telegram: { enabled: true }
    }, 'test-bot-token');

    const publicSettings = notifier.getSettings();
    assert.equal(publicSettings.telegram.enabled, true);
    assert.equal(publicSettings.telegram.tokenConfigured, true);
    assert.equal(publicSettings.telegram.paired, false);
    const startCode = publicSettings.telegram.startCode;
    assert.ok(startCode.startsWith('pw-'));

    // 1. Foreign chat sends random message before pairing -> ignored!
    await notifier.processTelegramMessage({
      chat: { id: 99999 },
      text: 'Hello bot'
    });
    assert.equal(sentMessages.length, 0);

    // 2. Chat 12345 sends wrong start code -> ignored!
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: 'pw-999999'
    });
    assert.equal(sentMessages.length, 0);

    // 3. Chat 12345 sends the correct start code -> PAIRS!
    await notifier.processTelegramMessage({
      chat: { id: 12345, username: 'authorized_user' },
      text: `/start ${startCode}`
    });
    assert.equal(sentMessages.length, 1);
    assert.equal(sentMessages[0].chat_id, '12345');
    assert.ok(sentMessages[0].text.includes('Planwharf connected'));

    const pairedSettings = notifier.getSettings();
    assert.equal(pairedSettings.telegram.paired, true);
    assert.equal(pairedSettings.telegram.pairedChatId, '12345');
    assert.equal(pairedSettings.telegram.pairedUsername, 'authorized_user');

    // 4. A message from ANOTHER chat 88888 is ignored!
    sentMessages.length = 0;
    await notifier.processTelegramMessage({
      chat: { id: 88888, username: 'attacker' },
      text: '/status'
    });
    assert.equal(sentMessages.length, 0);

    // 5. Paired chat asks for /status -> replies with projects status
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: '/status'
    });
    assert.equal(sentMessages.length, 1);
    assert.equal(sentMessages[0].parse_mode, 'HTML');
    assert.ok(sentMessages[0].text.includes('<b>Alpha Project</b>'));
    assert.ok(sentMessages[0].text.includes('🟢 <b>Status:</b> Working · ~12m left'));
    assert.ok(sentMessages[0].text.includes('■□□ 1/3'));
    assert.ok(sentMessages[0].text.includes('<b>Beta Project</b>'));
    assert.ok(sentMessages[0].text.includes('⏹ <b>Status:</b> Stopped'));

    // 6. /stop command asks for "yes" confirmation!
    sentMessages.length = 0;
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: '/stop Alpha'
    });
    assert.equal(sentMessages.length, 1);
    assert.ok(sentMessages[0].text.includes('Stop the crew for "Alpha Project"? Reply "yes" to confirm.'));
    assert.equal(stoppedProject, null);

    // 6a. Replying "no" cancels stop
    sentMessages.length = 0;
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: 'no'
    });
    assert.equal(sentMessages.length, 1);
    assert.ok(sentMessages[0].text.includes('Stop cancelled.'));
    assert.equal(stoppedProject, null);

    // 6b. Asking /stop again and confirming "yes" executes stop
    sentMessages.length = 0;
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: '/stop Alpha'
    });
    assert.equal(sentMessages.length, 1);
    assert.ok(sentMessages[0].text.includes('Reply "yes" to confirm'));

    sentMessages.length = 0;
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: 'yes'
    });
    assert.equal(stoppedProject, 'p1');
    assert.equal(sentMessages.length, 1);
    assert.ok(sentMessages[0].text.includes('The crew for "Alpha Project" has been stopped.'));

    // 7. /resume command resumes project
    sentMessages.length = 0;
    await notifier.processTelegramMessage({
      chat: { id: 12345 },
      text: '/resume Beta'
    });
    assert.equal(resumedProject, 'p2');
    assert.equal(sentMessages.length, 1);
    assert.ok(sentMessages[0].text.includes('The crew for "Beta Project" has resumed.'));
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('checkTransitions detects work ending, failing, or needing owner and alerts both channels', async () => {
  const os = require('node:os');
  const path = require('node:path');
  const fs = require('node:fs');
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-transitions-test-'));

  const alertsSent = [];
  const fakeFetch = async (url, options = {}) => {
    alertsSent.push({ url, options });
    return {
      ok: true,
      status: 200,
      json: async () => ({ ok: true, result: {} })
    };
  };

  let currentSnapshot = {
    projects: [
      { id: 'p1', name: 'Engine Project', status: 'watching', goal: 'Build feature', timeLeft: { minutes: 10 } }
    ]
  };

  try {
    const notifier = createPhoneNotifier({
      storeDir: tempDir,
      getSnapshot: async () => currentSnapshot,
      fetchImpl: fakeFetch,
      apiBaseUrl: 'http://fake.telegram.invalid',
      ntfyServerUrl: 'http://fake.ntfy.invalid'
    });

    notifier.updateSettings({
      ntfy: { enabled: true, topic: 'engine-alerts', serverUrl: 'http://fake.ntfy.invalid' },
      telegram: { enabled: true }
    }, 'secret-bot-token');
    savePhoneSettings(tempDir, { telegram: { pairedChatId: '12345' } });

    // First call establishes baseline
    await notifier.checkTransitions();
    assert.equal(alertsSent.length, 0);

    // Transition 1: watching -> complete
    currentSnapshot = {
      projects: [
        { id: 'p1', name: 'Engine Project', status: 'complete', goal: 'Build feature' }
      ]
    };
    await notifier.checkTransitions();

    assert.equal(alertsSent.length, 2); // 1 ntfy + 1 telegram
    const ntfyAlert = alertsSent.find(a => a.url.includes('engine-alerts'));
    const tgAlert = alertsSent.find(a => a.url.includes('/sendMessage'));
    assert.ok(ntfyAlert);
    assert.ok(ntfyAlert.options.body.includes('Status: Done'));
    assert.ok(tgAlert);
    const tgBody = JSON.parse(tgAlert.options.body);
    assert.equal(tgBody.chat_id, '12345');
    assert.equal(tgBody.parse_mode, 'HTML');
    assert.ok(tgBody.text.includes('<b>Status:</b> Done ✅'));
    assert.ok(tgBody.text.includes('⚓ Captain'));
    assert.ok(tgBody.text.includes('<b>Harbor Alert: Engine Project</b>'));
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('server.cjs HTTP endpoints for phone settings and test alert', async () => {
  const os = require('node:os');
  const path = require('node:path');
  const fs = require('node:fs');
  const { createServer } = require('../server.cjs');

  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-server-test-'));
  const planFile = path.join(tempDir, 'plan.json');
  fs.copyFileSync(path.join(__dirname, '..', 'fixtures', 'starter-notebook.json'), planFile);

  const server = createServer({
    planFile,
    attemptsDir: path.join(tempDir, '.attempts'),
    refreshModels: false
  });

  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const baseUrl = `http://127.0.0.1:${port}`;

  try {
    // 1. GET /api/settings/phone
    const getRes = await fetch(`${baseUrl}/api/settings/phone`);
    assert.equal(getRes.status, 200);
    const initialSettings = await getRes.json();
    assert.equal(initialSettings.ntfy.enabled, false);
    assert.equal(initialSettings.telegram.enabled, false);
    assert.equal(initialSettings.telegram.tokenConfigured, false);
    assert.ok(initialSettings.telegram.startCode.startsWith('pw-'));

    // 2. POST /api/settings/phone (saving ntfy & telegram token)
    const postRes = await fetch(`${baseUrl}/api/settings/phone`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ntfy: { enabled: true, topic: 'my-topic' },
        telegram: { enabled: true },
        telegramToken: '123456789:ABCDEF_token_xyz'
      })
    });
    assert.equal(postRes.status, 200);
    const updatedSettings = await postRes.json();
    assert.equal(updatedSettings.ntfy.enabled, true);
    assert.equal(updatedSettings.ntfy.topic, 'my-topic');
    assert.equal(updatedSettings.telegram.enabled, true);
    assert.equal(updatedSettings.telegram.tokenConfigured, true);
    // Raw token is NEVER returned! Only preview!
    assert.equal(updatedSettings.telegram.tokenPreview, '1234..._xyz');
    assert.equal(updatedSettings.telegramToken, undefined);

    // 3. POST /api/settings/phone/test
    const testRes = await fetch(`${baseUrl}/api/settings/phone/test`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({})
    });
    assert.equal(testRes.status, 200);
    const testResult = await testRes.json();
    assert.equal(testResult.ok, true);
  } finally {
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('formatTelegramAlertMessage and formatProgressBar render harbor companion voice with HTML, headings, and progress bar', () => {
  // 1. Escaping dynamic text (including project names with < and &)
  const escapedAlert = formatTelegramAlertMessage({
    type: 'started',
    projectName: 'Dock <North> & South',
    goalTitle: 'Inspect <engine> & pipes\nsecond line',
    step: { step: 2, total: 5 }
  });
  assert.ok(escapedAlert.startsWith('⚓ Captain'));
  assert.ok(escapedAlert.includes('<b>Harbor Alert: Dock &lt;North&gt; &amp; South</b>'));
  assert.ok(escapedAlert.includes('<b>Goal:</b> Inspect &lt;engine&gt; &amp; pipes'));
  assert.ok(escapedAlert.includes('<b>Status:</b> Started ▶️'));
  assert.ok(escapedAlert.includes('■■□□□ 2/5'));
  assert.ok(!escapedAlert.includes('<North>'));
  assert.ok(!escapedAlert.includes('<engine>'));

  // 2. Alert without step count omits progress bar
  const alertNoStep = formatTelegramAlertMessage({
    type: 'done',
    projectName: 'Simple Project',
    goalTitle: 'Finished task',
    durationMinutes: 30
  });
  assert.ok(alertNoStep.includes('<b>Status:</b> Done ✅'));
  assert.ok(alertNoStep.includes('<b>Duration:</b> 30m'));
  assert.ok(!alertNoStep.includes('■'));
  assert.ok(!alertNoStep.includes('□'));

  // 3. formatProgressBar helper behavior
  assert.equal(formatProgressBar(2, 4), '■■□□ 2/4');
  assert.equal(formatProgressBar({ step: 1, total: 3 }), '■□□ 1/3');
  assert.equal(formatProgressBar(null), '');
  assert.equal(formatProgressBar(undefined), '');
  assert.equal(formatProgressBar(0, 0), '');

  // 4. Status message escaping dynamic text
  const statusEscaped = formatStatusMessage([
    { id: 'p1', name: 'Crew <Alpha> & Beta', status: 'watching', step: { step: 3, total: 3 } }
  ]);
  assert.ok(statusEscaped.includes('<b>Crew &lt;Alpha&gt; &amp; Beta</b>'));
  assert.ok(statusEscaped.includes('■■■ 3/3'));
  assert.ok(!statusEscaped.includes('<Alpha>'));
});

test('harbor alerts and status cover step zero, hostile HTML, and progress edges', () => {
  assert.equal(formatProgressBar(0, 5), '□□□□□ 0/5');
  assert.equal(formatProgressBar({ step: 0, total: 4 }), '□□□□ 0/4');
  assert.equal(formatProgressBar('0/5'), '□□□□□ 0/5');
  assert.equal(formatProgressBar('3/12'), '■■■□□□□□□□ 3/12');
  assert.equal(formatProgressBar(15, 10), '■■■■■■■■■■ 15/10');
  assert.equal(formatProgressBar(-1, 5), '');
  assert.equal(formatProgressBar(1, -3), '');
  assert.equal(formatProgressBar('nope'), '');
  assert.equal(formatProgressBar(Number.NaN, 4), '');
  assert.equal(formatProgressBar(1, 0), '');
  assert.equal(formatProgressBar(0.2, 0.4), '');

  const fractional = formatProgressBar(2.4, 5);
  const blocks = (fractional.match(/■/g) || []).length + (fractional.match(/□/g) || []).length;
  assert.equal(blocks, 5);
  assert.match(fractional, /2\.4\/5$/);

  const zeroStep = formatStatusMessage([
    { id: 'p', name: 'Dock', status: 'watching', step: { step: 0, total: 4 }, timeLeft: { minutes: 8 } }
  ]);
  assert.ok(zeroStep.includes('□□□□ 0/4'));
  assert.ok(zeroStep.includes('🟢 <b>Status:</b> Working · ~8m left'));

  const noStep = formatStatusMessage([{ id: 'p', name: 'Dock', status: 'idle' }]);
  assert.ok(!noStep.includes('■'));
  assert.ok(!noStep.includes('□'));

  const hostile = formatStatusMessage([
    { id: 'p', name: 'North <dock> & pier', status: 'stuck <hard>', timeLeft: { minutes: '5 <b>now</b> & soon' } },
    { id: 'q', name: 'Quiet', status: 'watching', timeLeft: { minutes: '2 <i>hr</i>' } }
  ]);
  assert.ok(hostile.includes('<b>North &lt;dock&gt; &amp; pier</b>'));
  assert.ok(hostile.includes('• <b>Status:</b> stuck &lt;hard&gt;'));
  assert.ok(hostile.includes('· ~2 &lt;i&gt;hr&lt;/i&gt;m left'));
  assert.ok(!hostile.includes('<dock>'));
  assert.ok(!hostile.includes('<hard>'));
  assert.ok(!hostile.includes('<b>now</b>'));
  assert.ok(!hostile.includes('<i>hr</i>'));

  const alert = formatTelegramAlertMessage({
    type: 'needs_owner',
    projectName: 'A & B',
    goalTitle: 'Hold <fast>\nsecond line',
    timeLeft: { minutes: 0, longerThanUsual: true, sure: 'rough <guess> & co' },
    detail: 'See C:\\secret\\note.js and <tag> & more',
    step: { step: 0, total: 4 },
    durationMinutes: -1
  });
  assert.ok(alert.startsWith('⚓ Captain'));
  assert.ok(alert.includes('<b>Harbor Alert: A &amp; B</b>'));
  assert.ok(alert.includes('<b>Goal:</b> Hold &lt;fast&gt;'));
  assert.ok(alert.includes('<b>Status:</b> Needs your attention 🔔'));
  assert.ok(alert.includes('<b>Estimate:</b> About 0m left (longer than usual) · rough &lt;guess&gt; &amp; co'));
  assert.ok(alert.includes('&lt;tag&gt; &amp; more'));
  assert.ok(alert.includes('□□□□ 0/4'));
  assert.ok(!alert.includes('Duration'));
  assert.ok(!alert.includes('<fast>'));
  assert.ok(!alert.includes('<guess>'));
  assert.ok(!alert.includes('<tag>'));
  assert.ok(!alert.includes('note.js'));
  assert.ok(!alert.includes('C:\\secret'));

  const plain = formatAlertMessage({
    type: 'needs_owner',
    projectName: 'A & B',
    goalTitle: 'Hold',
    detail: 'See the note',
    step: { step: 1, total: 2 }
  });
  assert.match(plain, /^Planwharf: /);
  assert.ok(!plain.includes('<b>'));
  assert.ok(!plain.includes('⚓'));
  assert.ok(!plain.includes('■'));
  assert.ok(!plain.includes('□'));

  const unknown = formatTelegramAlertMessage({ type: 'mystery', projectName: 'P <x>', goalTitle: 'G & H' });
  assert.ok(unknown.includes('<b>Harbor Alert: P &lt;x&gt;</b>'));
  assert.ok(unknown.includes('<b>Status:</b> Updated •'));
  assert.ok(!unknown.includes('■'));
  assert.ok(!unknown.includes('<x>'));
});
