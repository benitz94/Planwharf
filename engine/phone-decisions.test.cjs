'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createPhoneNotifier, savePhoneSettings } = require('./phone.cjs');

function makeTempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'phone-decisions-'));
}

test('phone /decisions lists open decisions with the two choices', async () => {
  const dir = makeTempDir();
  const sent = [];

  savePhoneSettings(dir, {
    telegram: {
      enabled: true,
      pairedChatId: '12345',
      pairedUsername: 'captain'
    },
    language: 'en'
  }, 'bot-token-123');

  const openDecisions = [
    {
      id: 'dec-1',
      projectId: 'proj-alpha',
      projectName: 'Alpha Ship',
      workTitle: 'Install sails',
      text: 'Canvas or nylon sails?',
      suggestion: 'Canvas',
      breaks: false
    },
    {
      id: 'dec-2',
      projectId: 'proj-alpha',
      projectName: 'Alpha Ship',
      workTitle: 'Anchor system',
      text: 'Iron or stone anchor?',
      suggestion: 'Iron',
      breaks: true
    }
  ];

  let answered = null;
  let delegated = null;

  const fakeFetch = async (url, opts) => {
    if (url.includes('/sendMessage')) {
      const payload = JSON.parse(opts.body);
      sent.push(payload);
      return { ok: true, status: 200, json: async () => ({ ok: true, result: { message_id: sent.length } }) };
    }
    if (url.includes('/answerCallbackQuery')) {
      return { ok: true, status: 200, json: async () => ({ ok: true }) };
    }
    return { ok: true, status: 200, json: async () => ({ ok: true, result: [] }) };
  };

  const notifier = createPhoneNotifier({
    storeDir: dir,
    getDecisions: async () => ({ open: openDecisions, answered: [] }),
    onAnswerDecision: async (projectId, decisionId, ans) => {
      answered = { projectId, decisionId, ans };
    },
    onDelegateDecision: async (projectId, decisionId) => {
      delegated = { projectId, decisionId };
    },
    fetchImpl: fakeFetch
  });

  // Test /decisions
  await notifier.processTelegramMessage({
    chat: { id: 12345 },
    text: '/decisions'
  });

  assert.equal(sent.length, 2);
  // Verify first decision message
  assert.ok(sent[0].text.includes('Canvas or nylon sails?'));
  assert.ok(sent[0].reply_markup.inline_keyboard[0][0].text.includes('Accept: Canvas'));
  assert.ok(sent[0].reply_markup.inline_keyboard[0][1].text.includes('Let the lead decide'));
  assert.equal(sent[0].reply_markup.inline_keyboard[0][0].callback_data, 'decide_suggest:proj-alpha:dec-1');
  assert.equal(sent[0].reply_markup.inline_keyboard[0][1].callback_data, 'decide_lead:proj-alpha:dec-1');

  // Verify breaking decision highlights warning
  assert.ok(sent[1].text.includes('Could break something'));
  assert.ok(sent[1].reply_markup.inline_keyboard[0][0].text.includes('Accept: Iron'));

  // Test callback: Accept suggestion
  await notifier.processTelegramMessage({
    callback_query: {
      id: 'cb-1',
      data: 'decide_suggest:proj-alpha:dec-1',
      message: { chat: { id: 12345 }, message_id: 1 }
    }
  });

  assert.deepEqual(answered, { projectId: 'proj-alpha', decisionId: 'dec-1', ans: 'accepted' });

  // Test callback: Let the lead decide
  await notifier.processTelegramMessage({
    callback_query: {
      id: 'cb-2',
      data: 'decide_lead:proj-alpha:dec-2',
      message: { chat: { id: 12345 }, message_id: 2 }
    }
  });

  assert.deepEqual(delegated, { projectId: 'proj-alpha', decisionId: 'dec-2' });
});

test('phone /decisions when none are open replies in English and Italian', async () => {
  const dir = makeTempDir();
  const sent = [];

  savePhoneSettings(dir, {
    telegram: { enabled: true, pairedChatId: '12345' },
    language: 'it'
  }, 'token');

  const fakeFetch = async (url, opts) => {
    if (url.includes('/sendMessage')) {
      sent.push(JSON.parse(opts.body));
    }
    return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
  };

  const notifier = createPhoneNotifier({
    storeDir: dir,
    getDecisions: async () => ({ open: [], answered: [] }),
    fetchImpl: fakeFetch
  });

  await notifier.processTelegramMessage({
    chat: { id: 12345 },
    text: '/decisioni'
  });

  assert.equal(sent.length, 1);
  assert.equal(sent[0].text, 'Nessuna decisione in attesa.');
});

test('alert with decisionId carries the two choices and break highlight', async () => {
  const dir = makeTempDir();
  const sent = [];

  savePhoneSettings(dir, {
    telegram: { enabled: true, pairedChatId: '12345' },
    language: 'en'
  }, 'token');

  const fakeFetch = async (url, opts) => {
    if (url.includes('/sendMessage')) {
      sent.push(JSON.parse(opts.body));
    }
    return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
  };

  const notifier = createPhoneNotifier({
    storeDir: dir,
    fetchImpl: fakeFetch
  });

  await notifier.sendAlert({
    type: 'needs_owner',
    projectId: 'p1',
    projectName: 'Demo',
    goalTitle: 'Upgrade system',
    detail: 'Decision needed before merge',
    decisionId: 'd-99',
    suggestion: 'Proceed',
    breaks: true
  });

  assert.equal(sent.length, 1);
  assert.ok(sent[0].reply_markup?.inline_keyboard);
  assert.equal(sent[0].reply_markup.inline_keyboard[0][0].text, '✅ Accept: Proceed');
  assert.equal(sent[0].reply_markup.inline_keyboard[0][1].text, '🤖 Let the lead decide');
  assert.ok(sent[0].text.includes('Could break something'));
});

test('checkTransitions detects waiting_on_you and attaches decisionId and break warning', async () => {
  const dir = makeTempDir();
  const sent = [];

  savePhoneSettings(dir, {
    telegram: { enabled: true, pairedChatId: '12345' },
    language: 'it'
  }, 'token');

  const fakeFetch = async (url, opts) => {
    if (url.includes('/sendMessage')) {
      sent.push(JSON.parse(opts.body));
    }
    return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
  };

  let snapStatus = 'watching';
  const openDecs = [
    {
      id: 'dec-hot',
      projectId: 'proj-1',
      text: 'Ripristinare il backup del database?',
      suggestion: 'sì',
      breaks: true
    }
  ];

  const notifier = createPhoneNotifier({
    storeDir: dir,
    getSnapshot: async () => ({
      projects: [
        {
          id: 'proj-1',
          name: 'Core DB',
          status: snapStatus,
          goal: 'Migrazione dati',
          openDecisions: snapStatus === 'waiting_on_you' ? openDecs : []
        }
      ]
    }),
    fetchImpl: fakeFetch
  });

  // First check establishes baseline
  await notifier.checkTransitions();
  assert.equal(sent.length, 0);

  // Transition to waiting_on_you
  snapStatus = 'waiting_on_you';
  await notifier.checkTransitions();

  assert.equal(sent.length, 1);
  assert.ok(sent[0].text.includes('Potrebbe rompere qualcosa'));
  assert.ok(sent[0].text.includes('Ripristinare il backup'));
  assert.ok(sent[0].reply_markup?.inline_keyboard);
  assert.equal(sent[0].reply_markup.inline_keyboard[0][0].callback_data, 'decide_suggest:proj-1:dec-hot');
  assert.equal(sent[0].reply_markup.inline_keyboard[0][1].callback_data, 'decide_lead:proj-1:dec-hot');
});

test('callback query error handling when step already started (English and Italian)', async () => {
  const dir = makeTempDir();
  const sent = [];

  savePhoneSettings(dir, {
    telegram: { enabled: true, pairedChatId: '12345' },
    language: 'it'
  }, 'token');

  const fakeFetch = async (url, opts) => {
    if (url.includes('/sendMessage')) {
      sent.push(JSON.parse(opts.body));
    }
    return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
  };

  const notifier = createPhoneNotifier({
    storeDir: dir,
    onAnswerDecision: async () => ({
      ok: false,
      reason: 'The step that needs this decision has already started.'
    }),
    onDelegateDecision: async () => ({
      ok: false,
      reason: 'The step that needs this decision has already started.'
    }),
    fetchImpl: fakeFetch
  });

  // Callback query answering in Italian with started step
  await notifier.processTelegramMessage({
    callback_query: {
      id: 'cb-err-1',
      data: 'decide_suggest:p1:d1',
      message: { chat: { id: 12345 }, message_id: 10 }
    }
  });

  assert.equal(sent.length, 1);
  assert.ok(sent[0].text.includes('già iniziato'));

  // Switch to English and test delegation error
  savePhoneSettings(dir, {
    telegram: { enabled: true, pairedChatId: '12345' },
    language: 'en'
  }, 'token');

  await notifier.processTelegramMessage({
    callback_query: {
      id: 'cb-err-2',
      data: 'decide_lead:p1:d1',
      message: { chat: { id: 12345 }, message_id: 11 }
    }
  });

  assert.equal(sent.length, 2);
  assert.ok(sent[1].text.includes('already started'));

  // Malformed callback data handled safely
  await notifier.processTelegramMessage({
    callback_query: {
      id: 'cb-malformed',
      data: 'decide_suggest:',
      message: { chat: { id: 12345 }, message_id: 12 }
    }
  });
  // No extra sent message, no throw
  assert.equal(sent.length, 2);
});
