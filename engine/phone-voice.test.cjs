'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
  SUPPORTED_LANGUAGES,
  normalizeLanguage,
  languageOrDefault,
  say,
  messageKeys,
  telegramReadable,
  telegramAlertLabel,
  plainAlert,
  stateLabel,
  fitTelegram,
  isFragment,
  isDetailsRequest,
  parseLanguageCommand,
  companionOffline
} = require('./phone-voice.cjs');

const SAMPLE = {
  name: 'Harbor',
  names: 'Harbor, Dock',
  current: '1',
  total: '4',
  note: 'The stop order was refused.',
  code: 'stop-failed',
  languageName: 'English',
  reasonLine: 'The local helper on this computer did not answer.'
};

test('language codes accept English and Italian names and reject anything else', () => {
  assert.deepEqual(SUPPORTED_LANGUAGES, ['en', 'it']);
  assert.equal(normalizeLanguage('Italiano'), 'it');
  assert.equal(normalizeLanguage('ENGLISH'), 'en');
  assert.equal(normalizeLanguage('fr'), null);
  assert.equal(languageOrDefault('nope'), 'en');
});

test('every catalog message is readable in every supported language', () => {
  const keys = messageKeys();
  assert.ok(keys.includes('pairing'));
  assert.ok(keys.includes('couldNotReadProjects'));
  for (const key of keys) {
    for (const language of SUPPORTED_LANGUAGES) {
      const text = say(language, key, SAMPLE);
      const check = telegramReadable(text);
      assert.equal(check.ok, true, `${language} ${key} ${check.reasons.join(',')}`);
      if (!isFragment(key)) assert.ok(text.startsWith('⚓'), `${language} ${key} should greet the captain`);
      assert.equal(text.includes('<script'), false);
    }
    assert.notEqual(say('en', key, SAMPLE), say('it', key, SAMPLE), `${key} needs both languages`);
  }
});

test('English command phrases stay recognizable and Italian does not leak raw brackets', () => {
  assert.match(say('en', 'stopConfirm', SAMPLE), /Stop the crew for "Harbor"\? Reply "yes" to confirm\./);
  assert.match(say('en', 'couldNotReadProjects'), /could not read the projects/i);
  assert.match(say('en', 'couldNotCompleteStop'), /could not complete/i);
  assert.match(say('it', 'stopConfirm', SAMPLE), /sì/);
  assert.equal(say('it', 'pairing').includes('<progetto>'), false);
  assert.equal(telegramAlertLabel('en', 'done'), 'Done ✅');
  assert.equal(stateLabel('en', 'Working'), 'Working');
  assert.equal(stateLabel('it', 'Working'), 'Al lavoro');
  assert.match(plainAlert('en', 'failed'), /did not succeed/);
  assert.match(plainAlert('it', 'needs_owner'), /aspetta/);
});

test('details and language requests are recognized without changing other commands', () => {
  assert.equal(isDetailsRequest('/details@PlanwharfBot'), true);
  assert.equal(isDetailsRequest('Dettagli tecnici'), true);
  assert.equal(isDetailsRequest('/status'), false);
  assert.deepEqual(parseLanguageCommand('/lingua it'), { action: 'set', language: 'it' });
  assert.deepEqual(parseLanguageCommand('/language@Bot'), { action: 'show' });
  assert.equal(parseLanguageCommand('/language fr').action, 'unknown');
  assert.equal(parseLanguageCommand('/stop Alpha'), null);
  const offline = companionOffline('en', 'timeout');
  assert.match(offline, /<b>Harbor Companion Offline<\/b>/);
  assert.match(offline, /not answering/);
  assert.match(offline, /\/status/);
  assert.match(offline, /too long/);
  assert.equal(telegramReadable(companionOffline('it', 'HTTP 503')).ok, true);
  assert.match(say('en', 'unrecognized'), /Unrecognized command/);
  assert.match(say('en', 'memoryCleared'), /chat memory is clear/);
  assert.match(say('en', 'crewResumed', SAMPLE), /has resumed/);
  assert.match(say('en', 'stopSpecify', SAMPLE), /specify a project/i);
  assert.match(say('en', 'statusEmpty'), /<b>Harbor Status<\/b>/);
  assert.match(say('en', 'statusEmpty'), /No projects in port/);
});

test('overlong text is cut to a Telegram message without splitting the last kept line', () => {
  const long = `${'Harbor line\n'.repeat(500)}END`;
  const fitted = fitTelegram(long);
  assert.ok(fitted.length <= 4096);
  assert.ok(fitted.endsWith('\n…'));
  assert.equal(fitted.includes('END'), false);
  const check = telegramReadable(fitted);
  assert.equal(check.ok, true, check.reasons.join(','));
});
