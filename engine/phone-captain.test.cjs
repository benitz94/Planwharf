'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  savePhoneSettings,
  createPhoneNotifier,
  formatStatusMessage,
  formatTelegramAlertMessage,
  formatAlertMessage
} = require('./phone.cjs');
const { telegramReadable, messageKeys, SUPPORTED_LANGUAGES, say } = require('./phone-voice.cjs');
const { buildReplyMessages } = require('./phone-ollama.cjs');

function tempDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'phone-captain-'));
}

function pair(dir, extra = {}) {
  savePhoneSettings(dir, {
    telegram: { enabled: true, pairedChatId: '42' },
    ollama: { enabled: true, model: 'qwen2.5:3b' },
    ...extra
  }, 'isolated-bot-token');
}

function open(dir, overrides = {}) {
  const sent = [];
  const notifier = createPhoneNotifier({
    storeDir: dir,
    getSnapshot: overrides.getSnapshot || (async () => ({ projects: overrides.projects || [] })),
    onStopProject: overrides.onStopProject,
    onResumeProject: overrides.onResumeProject,
    fetchImpl: async (url, options = {}) => {
      const body = options.body ? JSON.parse(options.body) : null;
      sent.push({ url: String(url), body });
      if (String(url).includes('/api/chat')) {
        const facts = JSON.stringify(body.messages || []);
        const content = facts.includes('complete') ? 'Il molo è pronto.' : 'Il molo è al passo 1.';
        return { ok: true, status: 200, json: async () => ({ message: { content } }) };
      }
      return { ok: true, status: 200, json: async () => ({ ok: true, result: {} }) };
    },
    apiBaseUrl: 'http://fake.telegram.invalid',
    ntfyServerUrl: 'http://fake.ntfy.invalid'
  });
  return { notifier, sent };
}

function texts(sent) {
  return sent.map(item => item.body && item.body.text).filter(Boolean);
}

test('isolated Italian and English messages stay readable and commands still act', async () => {
  const dir = tempDir();
  let stopped = null;
  let resumed = null;
  const projects = [
    { id: 'p1', name: 'Molo <Nord>', status: 'watching', step: { step: 1, total: 4 }, timeLeft: { minutes: 6 }, goal: 'Paint D:\\secret\\hull.js' }
  ];
  try {
    pair(dir);
    const { notifier, sent } = open(dir, {
      projects,
      onStopProject: async id => {
        stopped = id;
        if (stopped === 'p1' && resumed == null) throw new Error('refused at C:\\secret\\crew.lock token=abc');
      },
      onResumeProject: async id => { resumed = id; }
    });

    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/language it' });
    assert.equal(notifier.getSettings().language, 'it');
    assert.match(texts(sent).at(-1), /Italiano/);
    assert.equal(telegramReadable(texts(sent).at(-1)).ok, true);

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/status' });
    const status = texts(sent).at(-1);
    assert.match(status, /Stato del porto/);
    assert.match(status, /Al lavoro/);
    assert.match(status, /passo 1 di 4/);
    assert.match(status, /Molo &lt;Nord&gt;/);
    assert.equal(status.includes('<Nord>'), false);
    assert.equal(status.includes('hull.js'), false);
    assert.equal(status.includes('D:\\secret'), false);
    assert.equal(telegramReadable(status).ok, true);
    assert.ok(status.length <= 4096);

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/stop Molo' });
    assert.equal(stopped, null);
    assert.match(texts(sent).at(-1), /sì/);
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: 'sì' });
    assert.equal(stopped, 'p1');
    assert.match(texts(sent).join('\n'), /non è riuscito a fermare/);
    assert.equal(texts(sent).join('\n').includes('C:\\secret'), false);
    assert.equal(texts(sent).join('\n').includes('crew.lock'), false);
    assert.equal(texts(sent).join('\n').includes('abc'), false);

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: 'dettagli tecnici' });
    const details = texts(sent).at(-1);
    assert.match(details, /stop-failed/);
    assert.match(details, /\[path\]/);
    assert.match(details, /\[redacted\]/);
    assert.equal(details.includes('C:\\secret'), false);
    assert.equal(details.includes('token=abc'), false);
    assert.equal(telegramReadable(details).ok, true);

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/resume Molo' });
    assert.equal(resumed, 'p1');
    assert.match(texts(sent).at(-1), /è ripartito/);

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: 'a che punto siamo?' });
    const chat = sent.find(item => item.url.includes('/api/chat'));
    assert.match(chat.body.messages[0].content, /Answer in Italian/);
    assert.match(chat.body.messages[0].content, /Reply in the user's language/);
    assert.match(chat.body.messages[0].content, /cannot stop, resume/);
    assert.equal(chat.body.messages[0].content.includes('hull.js'), false);
    const reply = texts(sent).at(-1);
    assert.match(reply, /^🏴‍☠️ Ahoy, Capitano!\n\nIl molo è al passo 1\.$/);
    assert.equal(telegramReadable(reply).ok, true);

    const again = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects: [] }),
      fetchImpl: async () => ({ ok: true, status: 200, json: async () => ({ ok: true, result: {} }) }),
      apiBaseUrl: 'http://fake.telegram.invalid'
    });
    assert.equal(again.getSettings().language, 'it');

    for (const text of texts(sent)) {
      const check = telegramReadable(text);
      assert.equal(check.ok, true, check.reasons.join(','));
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('English commands keep their actions when the language is rejected or the project list fails', async () => {
  const dir = tempDir();
  let stops = 0;
  try {
    pair(dir, { language: 'en' });
    const { notifier, sent } = open(dir, {
      projects: [{ id: 'p1', name: 'Alpha', status: 'watching' }],
      onStopProject: async () => { stops += 1; }
    });
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/language fr' });
    assert.match(texts(sent).at(-1), /English and Italian/);
    assert.equal(notifier.getSettings().language, 'en');

    sent.length = 0;
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/stop Alpha' });
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: 'yes' });
    assert.equal(stops, 1);
    assert.match(texts(sent).join('\n'), /has been stopped/);

    const broken = open(dir, {
      getSnapshot: async () => { throw new Error('cannot read C:\\secret\\plan.json'); }
    });
    await broken.notifier.processTelegramMessage({ chat: { id: 42 }, text: '/status' });
    assert.match(texts(broken.sent).at(-1), /could not read the projects/i);
    await broken.notifier.processTelegramMessage({ chat: { id: 42 }, text: '/details' });
    const note = texts(broken.sent).at(-1);
    assert.match(note, /projects-unreadable/);
    assert.equal(note.includes('C:\\secret'), false);
    assert.equal(note.includes('plan.json'), false);
    assert.equal(telegramReadable(note).ok, true);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('rendered harbor messages cover both languages with isolated project data', () => {
  const project = {
    id: 'p',
    name: 'Pier <A> & B',
    status: 'watching',
    step: { step: 2, total: 5 },
    timeLeft: { minutes: '3 <i>hr</i>' },
    goal: 'Hold'
  };
  for (const language of SUPPORTED_LANGUAGES) {
    const status = formatStatusMessage([project], language);
    const alert = formatTelegramAlertMessage({
      type: 'failed',
      projectName: project.name,
      goalTitle: 'See C:\\secret\\note.js <tag>',
      step: project.step,
      language
    });
    const plain = formatAlertMessage({
      type: 'failed',
      projectName: project.name,
      goalTitle: project.goal,
      detail: 'token=abc ./hidden.js',
      language
    });
    assert.equal(telegramReadable(status).ok, true, language);
    assert.equal(telegramReadable(alert).ok, true, language);
    assert.equal(plain.includes('<b>'), false);
    assert.equal(plain.includes('⚓'), false);
    assert.equal(plain.includes('note.js'), false);
    assert.equal(plain.includes('abc'), false);
    assert.equal(alert.includes('<tag>'), false);
    assert.equal(status.includes('<A>'), false);
    assert.ok(messageKeys().length >= 20);
    assert.ok(say(language, 'help').length > 0);
  }
  assert.match(formatStatusMessage([project], 'en'), /<b>Status:<\/b> Working/);
  assert.match(formatStatusMessage([project], 'it'), /<b>Stato:<\/b> Al lavoro/);
  assert.match(formatTelegramAlertMessage({ type: 'done', projectName: 'Dock', goalTitle: 'Done', language: 'en' }), /AHOY, CAPTAIN!/);
  assert.match(formatTelegramAlertMessage({ type: 'done', projectName: 'Dock', goalTitle: 'Fatto', durationMinutes: 5, language: 'it' }), /AHOY, CAPITANO!/);

  const prompt = buildReplyMessages({
    question: 'how is the pier?',
    projects: [project],
    language: 'it',
    sanitize: value => String(value || '')
  });
  assert.match(prompt[0].content, /cannot stop, resume/);
  assert.match(prompt[0].content, /Answer in Italian/);
});

test('every alert names the lead, the builders and the verifier; a model that took over shows with an arrow', () => {
  const { crewOfProject, formatTelegramAlertMessage } = require('./phone.cjs');
  const models = [{ id: 'grok', label: 'Grok Build' }, { id: 'opus', label: 'Opus 5.5' }, { id: 'gem', label: 'Gemini 3.8' }, { id: 'luna', label: 'GPT-5.6 Luna' }];
  const project = { crew: [{ role: 'Orchestrator', name: 'Grok 4.7' }], subTasks: [
    { id: 'a', title: 'Port quota', assignedModelId: 'grok', status: 'COMPLETE' },
    { id: 'b', title: 'Port skills', assignedModelId: 'luna', status: 'RUNNING' },
    { id: 'c', title: 'Verify the work independently against the job', assignedModelId: 'gem', status: 'PENDING' }] };
  const crew = crewOfProject(project, models, { __lead: 'Fable 5.2', a: 'Grok Build', b: 'Grok Build', c: 'Opus 5.5' });
  const text = formatTelegramAlertMessage({ type: 'switched', projectName: 'Ship', goalTitle: 'Goal', crew });
  assert.ok(text.includes('👑 <b>Lead</b> Fable 5.2 → Grok 4.7'));
  assert.ok(text.includes('🔨 <b>Build</b> Grok Build ✅ · Grok Build → GPT-5.6 Luna ⚙️'));
  assert.ok(text.includes('🔍 <b>Verify</b> Opus 5.5 → Gemini 3.8'));
  // No change since the last message: no arrow.
  const same = formatTelegramAlertMessage({ type: 'updated', projectName: 'Ship', goalTitle: 'Goal', crew: crewOfProject(project, models, { __lead: 'Grok 4.7', a: 'Grok Build', b: 'GPT-5.6 Luna', c: 'Gemini 3.8' }) });
  assert.ok(!same.includes('→'));
  // Compact: one line each.
  assert.ok(text.split('\n').length <= 8, text);
});
