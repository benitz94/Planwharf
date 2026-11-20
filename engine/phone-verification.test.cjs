'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const {
  fitTelegram,
  telegramReadable,
  normalizeLanguage,
  languageOrDefault,
  isDetailsRequest,
  say
} = require('./phone-voice.cjs');

const {
  projectFacts,
  buildReplyMessages,
  withCaptain,
  isClearMemory,
  isBareConfirmation
} = require('./phone-ollama.cjs');

const {
  createPhoneNotifier,
  savePhoneSettings,
  formatStatusMessage,
  formatProgressBar,
  matchProject
} = require('./phone.cjs');

test('fitTelegram cuts overlong HTML text without breaking entities, tags, or Telegram limits', () => {
  // 1. Nested HTML tags cut safely and all open tags closed in reverse order
  const nested = '<b><i><code>' + 'Line of text in harbor code\n'.repeat(250) + '</code></i></b>';
  const fittedNested = fitTelegram(nested);
  assert.ok(fittedNested.length <= 4096, `Length ${fittedNested.length} exceeds 4096`);
  assert.ok(fittedNested.endsWith('</code></i></b>\n…'), 'Tags closed in reverse order followed by note');
  const readNested = telegramReadable(fittedNested);
  assert.equal(readNested.ok, true, `Telegram readable reasons: ${readNested.reasons.join(', ')}`);

  // 2. Overlong text cutting right across an entity
  const entityBoundary = 'A'.repeat(4090) + '&amp;&amp;&amp;&amp;';
  const fittedEntity = fitTelegram(entityBoundary);
  assert.ok(fittedEntity.length <= 4096);
  const readEntity = telegramReadable(fittedEntity);
  assert.equal(readEntity.ok, true, `Entity check reasons: ${readEntity.reasons.join(', ')}`);

  // 3. Exact boundary cases: 4096 chars stays unchanged, 4097 chars is safely truncated
  const exact4096 = 'A'.repeat(4096);
  assert.equal(fitTelegram(exact4096).length, 4096);
  assert.equal(fitTelegram(exact4096), exact4096);

  const exact4097 = 'B'.repeat(4097);
  const fitted4097 = fitTelegram(exact4097);
  assert.ok(fitted4097.length <= 4096);
  assert.ok(fitted4097.endsWith('\n…'));
  assert.equal(telegramReadable(fitted4097).ok, true);
});

test('projectFacts and Ollama prompt preserve step 0 and sanitize paths/tokens', () => {
  const projects = [{
    id: 'dock-zero',
    name: 'Pier Zero',
    status: 'watching',
    step: { step: 0, total: 4 },
    timeLeft: { minutes: 15 },
    goal: 'Start work in C:\\hidden\\blueprint.js with token=secret456'
  }];

  const facts = projectFacts(projects, value => String(value || '').replace(/C:\\[^\s]+/, '[path]').replace(/token=\w+/, '[redacted]'));
  assert.equal(facts.length, 1);
  assert.equal(facts[0].step, '0/4', 'Step 0 of 4 must not be dropped as falsy');
  assert.equal(facts[0].minutes, 15);
  assert.equal(facts[0].goal.includes('blueprint.js'), false);
  assert.equal(facts[0].goal.includes('secret456'), false);

  const messages = buildReplyMessages({
    question: 'How is the work starting?',
    projects,
    language: 'it',
    sanitize: value => String(value || '')
  });
  assert.ok(messages.some(m => m.role === 'system' && m.content.includes('step 0/4')));
  assert.ok(messages.some(m => m.role === 'system' && m.content.includes('Answer in Italian')));
});

test('withCaptain normalizes greeting to the user language', () => {
  assert.equal(withCaptain('Hello harbor', 'en'), '🏴‍☠️ Ahoy, Captain!\n\nHello harbor');
  assert.equal(withCaptain('Buongiorno porto', 'it'), '🏴‍☠️ Ahoy, Capitano!\n\nBuongiorno porto');

  // When model generates English greeting for Italian conversation, it normalizes to Capitano
  assert.equal(withCaptain('🏴‍☠️ Ahoy, Captain!\n\nTutto bene', 'it'), '🏴‍☠️ Ahoy, Capitano!\n\nTutto bene');

  // When model generates Italian greeting for English conversation, it normalizes to Captain
  assert.equal(withCaptain('🏴‍☠️ Ahoy, Capitano!\n\nAll clear', 'en'), '🏴‍☠️ Ahoy, Captain!\n\nAll clear');

  // Empty body gives just the greeting
  assert.equal(withCaptain('', 'it'), '🏴‍☠️ Ahoy, Capitano!');
  assert.equal(withCaptain('', 'en'), '🏴‍☠️ Ahoy, Captain!');
});

test('normalizeLanguage and languageOrDefault handle locale codes and aliases', () => {
  assert.equal(normalizeLanguage('it-IT'), 'it');
  assert.equal(normalizeLanguage('it_CH'), 'it');
  assert.equal(normalizeLanguage('en-US'), 'en');
  assert.equal(normalizeLanguage('en_GB'), 'en');
  assert.equal(normalizeLanguage('Italiano'), 'it');
  assert.equal(normalizeLanguage('ENGLISH'), 'en');
  assert.equal(normalizeLanguage('es-ES'), null);
  assert.equal(normalizeLanguage(null), null);
  assert.equal(languageOrDefault('it-IT'), 'it');
  assert.equal(languageOrDefault('fr-FR'), 'en');
});

test('isClearMemory and isDetailsRequest recognize Italian and English natural phrases', () => {
  // Clear memory phrases
  assert.equal(isClearMemory('/clear'), true);
  assert.equal(isClearMemory('/forget@PlanwharfBot'), true);
  assert.equal(isClearMemory('clear memory'), true);
  assert.equal(isClearMemory('forget this chat'), true);
  assert.equal(isClearMemory('cancella memoria'), true);
  assert.equal(isClearMemory('pulisci memoria'), true);
  assert.equal(isClearMemory('svuota memoria'), true);
  assert.equal(isClearMemory('dimentica questa chat'), true);
  assert.equal(isClearMemory('status'), false);

  // Details request phrases
  assert.equal(isDetailsRequest('/details'), true);
  assert.equal(isDetailsRequest('/details@Bot'), true);
  assert.equal(isDetailsRequest('details'), true);
  assert.equal(isDetailsRequest('show details'), true);
  assert.equal(isDetailsRequest('technical details'), true);
  assert.equal(isDetailsRequest('show technical details'), true);
  assert.equal(isDetailsRequest('technical note'), true);
  assert.equal(isDetailsRequest('dettagli'), true);
  assert.equal(isDetailsRequest('dettagli tecnici'), true);
  assert.equal(isDetailsRequest('mostra dettagli'), true);
  assert.equal(isDetailsRequest('mostra i dettagli'), true);
  assert.equal(isDetailsRequest('nota tecnica'), true);
  assert.equal(isDetailsRequest('/status'), false);
});

test('isBareConfirmation handles punctuation and language variations', () => {
  assert.equal(isBareConfirmation('yes'), true);
  assert.equal(isBareConfirmation('YES'), true);
  assert.equal(isBareConfirmation('yes!'), true);
  assert.equal(isBareConfirmation('yes.'), true);
  assert.equal(isBareConfirmation('si'), true);
  assert.equal(isBareConfirmation('si!'), true);
  assert.equal(isBareConfirmation('sì'), true);
  assert.equal(isBareConfirmation('SÌ!'), true);
  assert.equal(isBareConfirmation('yes please'), false);
  assert.equal(isBareConfirmation('no'), false);
});

test('formatStatusMessage maps blocked_no_folder to Blocked (no usage) in both languages', () => {
  const p1 = [{ id: 'p1', name: 'Dock 1', status: 'blocked_no_worker' }];
  const p2 = [{ id: 'p2', name: 'Dock 2', status: 'blocked_no_folder' }];

  const statusEn1 = formatStatusMessage(p1, 'en');
  const statusEn2 = formatStatusMessage(p2, 'en');
  assert.ok(statusEn1.includes('Blocked (no usage)'));
  assert.ok(statusEn2.includes('Blocked (no usage)'));

  const statusIt1 = formatStatusMessage(p1, 'it');
  const statusIt2 = formatStatusMessage(p2, 'it');
  assert.ok(statusIt1.includes('Bloccato (senza utilizzo)'));
  assert.ok(statusIt2.includes('Bloccato (senza utilizzo)'));
});

test('Italian commands (/stato, /riprendi) and localized keyboard buttons work seamlessly', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-verif-it-'));
  const sent = [];
  let resumed = null;
  let stopped = null;

  try {
    savePhoneSettings(dir, {
      telegram: { enabled: true, pairedChatId: '100' },
      language: 'it'
    }, 'isolated-test-token');

    const projects = [
      { id: 'dock-a', name: 'Molo Levante', status: 'watching', step: { step: 1, total: 3 }, timeLeft: { minutes: 5 } },
      { id: 'dock-b', name: 'Molo Ponente', status: 'checkpointed' }
    ];

    const notifier = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects }),
      onStopProject: async id => { stopped = id; },
      onResumeProject: async id => { resumed = id; },
      fetchImpl: async (url, options = {}) => {
        const body = options.body ? JSON.parse(options.body) : {};
        sent.push({ url: String(url), body });
        return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
      },
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    // 1. /stato works as /status in Italian and renders Italian inline keyboard
    await notifier.processTelegramMessage({ chat: { id: 100 }, text: '/stato' });
    const lastMsg = sent.at(-1);
    assert.ok(lastMsg.body.text.includes('Stato del porto'));
    assert.ok(lastMsg.body.text.includes('Al lavoro'));
    assert.ok(lastMsg.body.text.includes('Fermo'));
    const keyboard = lastMsg.body.reply_markup?.inline_keyboard;
    assert.ok(keyboard);
    assert.equal(keyboard[0][0].text, '⏹ Ferma Molo Levante');
    assert.equal(keyboard[1][0].text, '▶️ Riprendi Molo Ponente');
    assert.equal(keyboard[2][0].text, '🔄 Aggiorna');

    // 2. /riprendi works as /resume
    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 100 }, text: '/riprendi Levante' });
    assert.equal(resumed, 'dock-a');
    assert.ok(sent.at(-1).body.text.includes('è ripartito'));

    // 3. Inline callback stop resolves actual project name and prompts with Italian buttons
    sent.length = 0;
    // Simulate tapping "⏹ Ferma Molo Levante" button (callback_data: "stop:dock-a")
    await notifier.pollUpdates(); // empty poll
    // Deliver callback via pollUpdates
    const callbackFetch = async (url, options = {}) => {
      if (String(url).includes('getUpdates')) {
        return {
          ok: true,
          status: 200,
          json: async () => ({
            ok: true,
            result: [{
              update_id: 10,
              callback_query: {
                id: 'cb-1',
                data: 'stop:dock-a',
                message: { chat: { id: 100 } }
              }
            }]
          })
        };
      }
      const body = options.body ? JSON.parse(options.body) : {};
      sent.push({ url: String(url), body });
      return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
    };

    const notifierCb = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects }),
      onStopProject: async id => { stopped = id; },
      fetchImpl: callbackFetch,
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    await notifierCb.pollUpdates();
    const confirmPrompt = sent.find(s => s.body.reply_markup?.inline_keyboard);
    assert.ok(confirmPrompt, 'Confirmation prompt must be sent');
    assert.ok(confirmPrompt.body.text.includes('Conferma la fermata'));
    const inlineButtons = confirmPrompt.body.reply_markup.inline_keyboard[0];
    assert.equal(inlineButtons[0].text, '✅ Sì, ferma');
    assert.equal(inlineButtons[1].text, '❌ Annulla');

    // Confirm with "sì!"
    sent.length = 0;
    await notifierCb.processTelegramMessage({ chat: { id: 100 }, text: 'sì!' });
    assert.equal(stopped, 'dock-a');
    assert.ok(sent.at(-1).body.text.includes('L\'equipaggio di "Molo Levante" è stato fermato.'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('concurrent telegram messages, callbacks and memory clearing do not throw or corrupt state', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-verif-concurrent-'));
  const sent = [];

  try {
    savePhoneSettings(dir, {
      telegram: { enabled: true, pairedChatId: '200' },
      ollama: { enabled: true, model: 'qwen2.5:3b' },
      language: 'en'
    }, 'concurrency-token');

    const projects = [{ id: 'proj-c', name: 'Cargo', status: 'watching', step: { step: 1, total: 2 } }];

    const notifier = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects }),
      fetchImpl: async (url, options = {}) => {
        const body = options.body ? JSON.parse(options.body) : {};
        sent.push({ url: String(url), body });
        if (String(url).includes('/api/chat')) {
          return { ok: true, status: 200, json: async () => ({ message: { content: 'Cargo is on step 1.' } }) };
        }
        return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
      },
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    // Fire 6 simultaneous messages across multiple command types
    await Promise.all([
      notifier.processTelegramMessage({ chat: { id: 200 }, text: '/status' }),
      notifier.processTelegramMessage({ chat: { id: 200 }, text: 'How is the cargo?' }),
      notifier.processTelegramMessage({ chat: { id: 200 }, text: '/details' }),
      notifier.processTelegramMessage({ chat: { id: 200 }, text: 'cancella memoria' }),
      notifier.processTelegramMessage({ chat: { id: 200 }, text: '/help' }),
      notifier.processTelegramMessage({ chat: { id: 999 }, text: '/status' }) // Foreign chat, ignored
    ]);

    // Foreign chat 999 should not produce any sent messages
    assert.equal(sent.some(s => s.body.chat_id === '999'), false);

    // Messages sent to 200 should all be valid and telegram readable
    const to200 = sent.filter(s => s.body.chat_id === '200');
    assert.ok(to200.length >= 4);
    for (const msg of to200) {
      const readable = telegramReadable(msg.body.text);
      assert.equal(readable.ok, true, `Text readable: ${readable.reasons.join(',')}`);
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('Italian commands /ferma, /aiuto, /guida, /dettagli, and /cancella work with nautical voice', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-it-cmds-'));
  const sent = [];
  let stopped = null;

  try {
    savePhoneSettings(dir, {
      telegram: { enabled: true, pairedChatId: '300' },
      language: 'it'
    }, 'it-cmds-token');

    const projects = [{ id: 'dock-gamma', name: 'Molo Gamma', status: 'watching' }];

    const notifier = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects }),
      onStopProject: async id => { stopped = id; },
      fetchImpl: async (url, options = {}) => {
        const body = options.body ? JSON.parse(options.body) : {};
        sent.push({ url: String(url), body });
        return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
      },
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    // 1. /ferma prompts confirmation
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/ferma Gamma' });
    assert.ok(sent.at(-1).body.text.includes('Conferma la fermata'));
    assert.equal(stopped, null);

    // Confirm with "si"
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: 'si' });
    assert.equal(stopped, 'dock-gamma');
    assert.ok(sent.at(-1).body.text.includes('L\'equipaggio di "Molo Gamma" è stato fermato.'));

    // 2. /aiuto and /guida
    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/aiuto' });
    assert.ok(sent.at(-1).body.text.includes('Guida del porto'));

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/guida@PlanwharfBot' });
    assert.ok(sent.at(-1).body.text.includes('Guida del porto'));

    // 3. /dettagli and /dettagli@Bot
    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/dettagli' });
    assert.ok(sent.at(-1).body.text.includes('Dettagli tecnici'));

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/dettagli@Bot' });
    assert.ok(sent.at(-1).body.text.includes('Dettagli tecnici'));

    // 4. /cancella and /dimentica
    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/cancella@Bot' });
    assert.ok(sent.at(-1).body.text.includes('Giornale di bordo pulito'));

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 300 }, text: '/dimentica' });
    assert.ok(sent.at(-1).body.text.includes('Giornale di bordo pulito'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('stop confirmation expiration prevents delayed execution and returns unrecognized', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-expire-'));
  const sent = [];
  let stopped = null;

  try {
    savePhoneSettings(dir, {
      telegram: { enabled: true, pairedChatId: '400' },
      language: 'en'
    }, 'expire-token');

    const projects = [{ id: 'p-exp', name: 'Expiring', status: 'watching' }];

    const notifier = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects }),
      onStopProject: async id => { stopped = id; },
      confirmWindowMs: 20, // 20ms window for testing
      fetchImpl: async (url, options = {}) => {
        const body = options.body ? JSON.parse(options.body) : {};
        sent.push({ url: String(url), body });
        return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
      },
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    await notifier.processTelegramMessage({ chat: { id: 400 }, text: '/stop Expiring' });
    assert.ok(sent.at(-1).body.text.includes('Confirm Stop Order'));
    assert.equal(stopped, null);

    // Wait 35ms for confirmation window to expire
    await new Promise(resolve => setTimeout(resolve, 35));

    // Late "yes" should be treated as unrecognized and NOT stop the project
    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 400 }, text: 'yes' });
    assert.equal(stopped, null);
    assert.ok(sent.at(-1).body.text.includes('Unrecognized Command'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('negative Telegram group/channel chat IDs pair and receive notifications properly', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'phone-negchat-'));
  const sent = [];

  try {
    savePhoneSettings(dir, {
      telegram: { enabled: true, pairedChatId: '-1009876543210' },
      language: 'it'
    }, 'group-token');

    const notifier = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects: [{ id: 'group-p', name: 'Flotta', status: 'watching' }] }),
      fetchImpl: async (url, options = {}) => {
        const body = options.body ? JSON.parse(options.body) : {};
        sent.push({ url: String(url), body });
        return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
      },
      apiBaseUrl: 'http://fake.telegram.invalid'
    });

    await notifier.processTelegramMessage({ chat: { id: -1009876543210 }, text: '/stato' });
    assert.equal(sent.length, 1);
    assert.equal(sent[0].body.chat_id, '-1009876543210');
    assert.ok(sent[0].body.text.includes('Stato del porto'));
    assert.ok(sent[0].body.text.includes('Flotta'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('fitTelegram cuts surrogate pairs without orphaned high surrogates and preserves valid UTF-8', () => {
  const emojiStr = 'A'.repeat(4093) + '\uD83D\uDE80' + 'BB';
  const fitted = fitTelegram(emojiStr);
  assert.ok(fitted.length <= 4096);
  assert.equal(/[\uD800-\uDBFF]$/.test(fitted.replace(/\n…$/, '')), false, 'Must not end in orphaned surrogate');
  const check = telegramReadable(fitted);
  assert.equal(check.ok, true, `Readable: ${check.reasons.join(',')}`);
});

test('withCaptain cleans redundant prefixes without duplicating greetings', () => {
  assert.equal(withCaptain('Captain: Everything is ready', 'en'), '🏴‍☠️ Ahoy, Captain!\n\nEverything is ready');
  assert.equal(withCaptain('Capitano: Tutto pronto', 'it'), '🏴‍☠️ Ahoy, Capitano!\n\nTutto pronto');
  assert.equal(withCaptain('⚓Captain Tutto pronto', 'it'), '🏴‍☠️ Ahoy, Capitano!\n\nTutto pronto');
  assert.equal(withCaptain('🏴‍☠️ Ahoy, Capitano!: Avanti', 'en'), '🏴‍☠️ Ahoy, Captain!\n\nAvanti');
});

test('formatStatusMessage maps idle to Idle and localizes to A riposo with white circle emoji', () => {
  const projects = [{ id: 'p-idle', name: 'Barca in secca', status: 'idle' }];
  const statusIt = formatStatusMessage(projects, 'it');
  assert.ok(statusIt.includes('⚪ <b>Stato:</b> A riposo'), `Output was: ${statusIt}`);
  const statusEn = formatStatusMessage(projects, 'en');
  assert.ok(statusEn.includes('⚪ <b>Status:</b> Idle'), `Output was: ${statusEn}`);
});

test('formatAlertMessage and formatTelegramAlertMessage format updated type cleanly', () => {
  const { formatAlertMessage, formatTelegramAlertMessage } = require('./phone.cjs');
  const plainEn = formatAlertMessage({ type: 'updated', projectName: 'Dock', goalTitle: 'Update goal', language: 'en' });
  assert.ok(plainEn.includes('Status: Updated •'));
  assert.ok(plainEn.includes('The project changed.'));

  const plainIt = formatAlertMessage({ type: 'updated', projectName: 'Dock', goalTitle: 'Obiettivo', language: 'it' });
  assert.ok(plainIt.includes('Stato: Aggiornato •'));
  assert.ok(plainIt.includes('Il progetto è cambiato.'));

  const tgIt = formatTelegramAlertMessage({ type: 'updated', projectName: 'Dock', goalTitle: 'Obiettivo', language: 'it' });
  assert.ok(tgIt.includes('🧭 <b>Stato</b>\nAggiornato •'));
  assert.ok(tgIt.includes('Il progetto è cambiato.'));
});
