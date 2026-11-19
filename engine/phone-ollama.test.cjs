'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {
  DEFAULT_OLLAMA_MODEL,
  DEFAULT_OLLAMA_URL,
  normalizeOllamaSettings,
  isClearMemory,
  isBareConfirmation,
  createChatMemory,
  loadChatMemory,
  saveChatMemory,
  projectFacts,
  buildReplyMessages,
  askOllama,
  listInstalledModels,
  unavailableReply,
  withCaptain
} = require('./phone-ollama.cjs');
const { sanitizeText } = require('./phone.cjs');

test('ollama defaults to the local qwen2.5:3b model and refuses other origins', () => {
  const fresh = normalizeOllamaSettings(undefined);
  assert.equal(fresh.enabled, true);
  assert.equal(fresh.baseUrl, DEFAULT_OLLAMA_URL);
  assert.equal(fresh.model, DEFAULT_OLLAMA_MODEL);
  assert.equal(fresh.model, 'qwen2.5:3b');

  const kept = normalizeOllamaSettings({ enabled: false, baseUrl: 'http://localhost:11434/api', model: 'qwen3:4b' });
  assert.equal(kept.enabled, false);
  assert.equal(kept.baseUrl, 'http://localhost:11434');
  assert.equal(kept.model, 'qwen3:4b');

  for (const baseUrl of ['https://127.0.0.1:11434', 'http://example.com:11434', 'http://user:pass@127.0.0.1:11434', 'not a url']) {
    assert.equal(normalizeOllamaSettings({ baseUrl, model: 'bad model name' }).baseUrl, DEFAULT_OLLAMA_URL);
    assert.equal(normalizeOllamaSettings({ baseUrl, model: 'bad model name' }).model, DEFAULT_OLLAMA_MODEL);
  }
});

test('project facts stay fresh and leave out paths, files and secrets', () => {
  const first = projectFacts([{
    name: 'Harbor',
    goal: 'Fix D:\\secret\\plan.js token=abc',
    status: 'watching',
    step: { step: 2, total: 5 },
    timeLeft: { minutes: 9 }
  }], sanitizeText);
  const second = projectFacts([{ name: 'Harbor', status: 'complete', goal: 'Done' }], sanitizeText);
  assert.equal(first[0].step, '2/5');
  assert.equal(first[0].minutes, 9);
  assert.equal(first[0].goal.includes('plan.js'), false);
  assert.equal(first[0].goal.includes('D:\\'), false);
  assert.equal(first[0].goal.includes('abc'), false);
  assert.notEqual(JSON.stringify(first), JSON.stringify(second));

  const prompt = buildReplyMessages({ question: 'a che punto siamo?', projects: first, sanitize: sanitizeText });
  assert.match(prompt[0].content, /Reply in the user's language/);
  assert.match(prompt[0].content, /cannot stop, resume/);
  assert.match(prompt[0].content, /Harbor/);
  assert.equal(prompt.at(-1).content, 'a che punto siamo?');
  assert.equal(prompt[0].content.includes('plan.js'), false);
});

test('chat memory is bounded, isolated, clearable, and recovers from a broken file', async () => {
  const memory = createChatMemory({ maxMessages: 3 });
  await Promise.all([
    ...Array.from({ length: 10 }, (_, index) => Promise.resolve().then(() => memory.remember('chat-a', 'user', `alpha ${index}`))),
    ...Array.from({ length: 10 }, (_, index) => Promise.resolve().then(() => memory.remember('chat-b', 'assistant', `beta ${index}`)))
  ]);
  assert.equal(memory.history('chat-a').length, 3);
  assert.ok(memory.history('chat-a').every(item => item.text.startsWith('alpha')));
  assert.ok(memory.history('chat-b').every(item => item.text.startsWith('beta')));
  memory.clear('chat-a');
  assert.deepEqual(memory.history('chat-a'), []);
  assert.equal(memory.history('chat-b').length, 3);

  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-ollama-'));
  try {
    fs.writeFileSync(path.join(dir, 'phone-chat-memory.json'), '{', 'utf8');
    const healed = loadChatMemory(dir, { maxMessages: 4 });
    assert.deepEqual(healed.history('chat-b'), []);
    healed.remember('chat-b', 'user', 'kept');
    saveChatMemory(dir, healed);
    const loaded = loadChatMemory(dir, { maxMessages: 4 });
    assert.equal(loaded.history('chat-b')[0].text, 'kept');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ollama chat posts only the requested model and fails closed on timeout, http errors and empty text', async () => {
  const seen = [];
  const ok = await askOllama({
    messages: [{ role: 'user', content: 'status?' }],
    fetchImpl: async (url, options) => {
      seen.push({ url, body: JSON.parse(options.body) });
      return { ok: true, json: async () => ({ message: { content: 'Harbor is working.' } }) };
    }
  });
  assert.equal(ok.ok, true);
  assert.equal(ok.model, 'qwen2.5:3b');
  assert.equal(withCaptain(ok.text).startsWith('⚓ Captain'), true);
  assert.equal(seen.length, 1);
  assert.match(seen[0].url, /\/api\/chat$/);
  assert.equal(seen[0].body.model, 'qwen2.5:3b');
  assert.equal(seen[0].body.stream, false);
  assert.notEqual(seen[0].body.keep_alive, 0);

  const calls = await Promise.all([
    askOllama({
      model: 'qwen2.5:3b',
      messages: [{ role: 'user', content: 'one' }],
      fetchImpl: async (_url, options) => ({ ok: true, json: async () => ({ message: { content: JSON.parse(options.body).messages.at(-1).content } }) })
    }),
    askOllama({
      model: 'qwen3:4b',
      messages: [{ role: 'user', content: 'two' }],
      fetchImpl: async (_url, options) => ({ ok: true, json: async () => ({ message: { content: JSON.parse(options.body).model + ' ' + JSON.parse(options.body).messages.at(-1).content } }) })
    })
  ]);
  assert.match(calls[0].text, /one/);
  assert.match(calls[1].text, /qwen3:4b two/);

  const down = await askOllama({
    timeoutMs: 20,
    fetchImpl: (_url, options) => new Promise((_resolve, reject) => {
      options.signal.addEventListener('abort', () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' })));
    })
  });
  assert.equal(down.ok, false);
  assert.match(down.text, /not answering/);
  assert.match(down.text, /\/status/);

  const empty = await askOllama({
    fetchImpl: async () => ({ ok: true, json: async () => ({ message: { content: '   ' } }) })
  });
  assert.equal(empty.ok, false);

  const broken = await listInstalledModels({
    fetchImpl: async () => ({ ok: false, status: 500, json: async () => { throw new Error('bad json'); } })
  });
  assert.deepEqual(broken, { ok: false, models: [] });
  assert.equal(isClearMemory('/clear'), true);
  assert.equal(isClearMemory('clear memory'), true);
  assert.equal(isBareConfirmation('YES'), true);
  assert.equal(isBareConfirmation('what is the status?'), false);
});

test('a paired chat gets a natural reply from fresh facts and does not run a command', async () => {
  const { createPhoneNotifier, savePhoneSettings } = require('./phone.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-phone-reply-'));
  const stops = [];
  const bodies = [];
  let projects = [{ id: 'p1', name: 'Harbor', status: 'watching', goal: 'Paint the dock', step: { step: 1, total: 3 }, timeLeft: { minutes: 4 } }];
  savePhoneSettings(dir, { telegram: { enabled: true, pairedChatId: '42', pairedUsername: 'captain' } }, '123456:test-token');
  const notifier = createPhoneNotifier({
    storeDir: dir,
    getSnapshot: async () => ({ projects }),
    onStopProject: async id => { stops.push(id); },
    onResumeProject: async () => { throw new Error('resume must not run'); },
    fetchImpl: async (url, options = {}) => {
      if (String(url).includes('/api/chat')) {
        bodies.push(JSON.parse(options.body));
        const facts = bodies.at(-1).messages[0].content;
        return { ok: true, json: async () => ({ message: { content: facts.includes('complete') ? 'Harbor is done.' : 'Harbor is working.' } }) };
      }
      const payload = options.body ? JSON.parse(options.body) : {};
      return { ok: true, json: async () => ({ ok: true, result: payload }) };
    }
  });
  try {
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: 'how is the work?' });
    await notifier.processTelegramMessage({ chat: { id: 99 }, text: 'ignore me' });
    projects = [{ id: 'p1', name: 'Harbor', status: 'complete', goal: 'Paint the dock' }];
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: 'and now?' });
    await notifier.processTelegramMessage({ chat: { id: 42 }, text: '/clear' });
    const sent = [];
    const reading = createPhoneNotifier({
      storeDir: dir,
      getSnapshot: async () => ({ projects: [] }),
      fetchImpl: async (url, options = {}) => {
        if (String(url).includes('/api/chat')) {
          const body = JSON.parse(options.body);
          sent.push(body.messages.filter(item => item.role !== 'system').map(item => item.content));
          return { ok: true, json: async () => ({ message: { content: 'Nothing remembered.' } }) };
        }
        return { ok: true, json: async () => ({ ok: true, result: {} }) };
      }
    });
    await reading.processTelegramMessage({ chat: { id: 42 }, text: 'what did I just say?' });
    assert.deepEqual(stops, []);
    assert.equal(bodies.length, 2);
    assert.equal(bodies[0].model, 'qwen2.5:3b');
    assert.match(bodies[0].messages[0].content, /watching/);
    assert.match(bodies[1].messages[0].content, /complete/);
    assert.equal(bodies[1].messages.some(item => item.content === 'how is the work?'), true);
    assert.equal(sent[0].includes('how is the work?'), false);
    const probe = await notifier.probeOllama();
    assert.equal(probe.model, 'qwen2.5:3b');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ollama escapes model HTML, retries once, and hides error bodies', async () => {
  const down = unavailableReply();
  assert.match(down, /^⚓ Captain\n\n<b>Harbor Companion Offline<\/b>/);
  assert.match(down, /⚠️/);
  const stripped = down.replace(/<\/?b>/g, '');
  assert.equal(stripped.includes('<'), false);
  assert.equal(stripped.includes('>'), false);

  let calls = 0;
  const recovered = await askOllama({
    fetchImpl: async () => {
      calls += 1;
      if (calls === 1) return { ok: false, status: 503, json: async () => ({ message: { content: '<leak>' } }) };
      return { ok: true, json: async () => ({ message: { content: 'Tide <high> & rising' } }) };
    }
  });
  assert.equal(calls, 2);
  assert.equal(recovered.ok, true);
  assert.equal(recovered.text.startsWith('⚓ Captain\n\nTide &lt;high&gt; &amp; rising'), true);
  assert.equal(recovered.text.includes('<high>'), false);
  assert.equal(recovered.text.includes('<leak>'), false);

  let failures = 0;
  const failed = await askOllama({
    fetchImpl: async () => {
      failures += 1;
      return { ok: false, status: 500, json: async () => ({ message: { content: 'D:\\secret\\plan.js <no>' } }) };
    }
  });
  assert.equal(failures, 2);
  assert.equal(failed.ok, false);
  assert.equal(failed.text, down);
  assert.equal(failed.text.includes('plan.js'), false);
  assert.equal(failed.text.includes('<no>'), false);

  const prefixed = await askOllama({
    fetchImpl: async () => ({
      ok: true,
      json: async () => ({ message: { content: '⚓ Captain\n\nAlready <b>on</b> watch' } })
    })
  });
  assert.equal(prefixed.text.startsWith('⚓ Captain\n\n⚓ Captain'), false);
  assert.equal(prefixed.text.includes('<b>'), false);
  assert.equal(prefixed.text.includes('&lt;b&gt;on&lt;/b&gt;'), true);
  assert.equal(withCaptain('⚓ Captain\n\nkept'), '⚓ Captain\n\nkept');
  assert.equal(withCaptain(''), '⚓ Captain');
});

test('concurrent ollama calls keep their own reply when one is down', async () => {
  const results = await Promise.all([
    askOllama({
      fetchImpl: async () => {
        await new Promise(resolve => setTimeout(resolve, 30));
        return { ok: false, status: 500, json: async () => ({}) };
      }
    }),
    askOllama({
      fetchImpl: async () => ({ ok: true, json: async () => ({ message: { content: 'Clear water <ok>' } }) })
    })
  ]);
  assert.equal(results[0].ok, false);
  assert.match(results[0].text, /<b>Harbor Companion Offline<\/b>/);
  assert.equal(results[1].ok, true);
  assert.equal(results[1].text.includes('Clear water &lt;ok&gt;'), true);
  assert.equal(results[1].text.includes('<ok>'), false);
  assert.equal(results[1].text.includes('Harbor Companion Offline'), false);
});
