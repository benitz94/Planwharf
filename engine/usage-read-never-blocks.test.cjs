'use strict';
// A usage read that fails is not proof that a company's login is gone: the AI program renews its own
// login when it runs. On 2026-11-24 Grok's usage service refused a saved token, Planwharf wrote "Grok login
// expired", marked the plan "not signed in" and kept every Grok model out of the crew, while grok worked.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { readGrok, readChatGpt, planExhausted, unreadPlan } = require('./planUsage.js');
const { notePlanRefusal, providerError } = require('./plan-status.cjs');

const refused = async () => ({ status: 401, ok: false, headers: { get: () => null }, json: async () => ({}), text: async () => '' });

test('a refused token at Grok\'s usage service does not keep Grok out of the crew', async t => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-grok-'));
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  fs.mkdirSync(path.join(home, '.grok'));
  fs.writeFileSync(path.join(home, '.grok', 'auth.json'), JSON.stringify({ key: 'stale-key', expires_at: '2020-01-01T00:00:00Z' }));
  const plan = await readGrok(home, refused, Date.now());
  assert.equal(plan.readable, false);
  assert.equal(plan.planStatus, 'unknown');
  assert.match(plan.reason, /Grok usage could not be read: the usage service refused the saved login\..*Grok models are not stopped for this\./);
  assert.doesNotMatch(plan.reason, /login expired/);
  assert.equal(planExhausted([plan], 'Grok'), false, 'Grok models stay available');
});

test('the same for ChatGPT, and for every read that fails on Planwharf\'s side', async t => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-codex-'));
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  fs.mkdirSync(path.join(home, '.codex'));
  fs.writeFileSync(path.join(home, '.codex', 'auth.json'), JSON.stringify({ tokens: { access_token: 'stale', account_id: 'a' } }));
  const plan = await readChatGpt(home, refused);
  assert.equal(plan.planStatus, 'unknown');
  assert.equal(planExhausted([plan], 'ChatGPT'), false);
  for (const reason of ['Grok login expired.', 'Sign in with grok CLI to read weekly usage.', 'Not readable']) assert.equal(planExhausted([unreadPlan('Grok', reason)], 'Grok'), false, reason);
});

test('the company\'s own answer, or a real refusal of its program, still marks the login', () => {
  const answer = providerError('grok', 'login expired for this account', 'Grok');
  assert.equal(planExhausted([{ ...unreadPlan('Grok', answer.reason), planStatus: answer.planStatus }], 'Grok'), true);
  const noted = notePlanRefusal([unreadPlan('Grok', 'x')], { id: 'grok-4.7', adapter: 'grok', quotaPoolId: 'Grok' }, 'Error: not logged in');
  assert.equal(noted.status, 'not signed in');
  assert.equal(planExhausted(noted.plans, 'Grok'), true);
});
