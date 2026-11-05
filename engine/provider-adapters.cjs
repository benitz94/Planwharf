'use strict';

// Mechanics live here; catalog policy and user ordering live in configuration.
const adapters = {
  claude: { nativeModel: /^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$/, sourceProtectedResearch: false,
    account: () => require('./provider-accounts.cjs').unknownAccount(),
    quota: () => require('./claude-usage.cjs').readClaudeUsage(),
    discover: () => require('./claude.cjs').listClaude() },
  codex: { nativeModel: /^gpt-/, sourceProtectedResearch: true,
    account: provider => require('./provider-accounts.cjs').codexAccount(provider),
    quota: () => require('./planUsage.js').readChatGpt(require('node:os').homedir(), fetch),
    discover: async () => {
      const fs = require('node:fs'), path = require('node:path'), os = require('node:os');
      const home = process.env.CODEX_HOME || path.join(os.homedir(), '.codex');
      let auth; try { auth = JSON.parse(fs.readFileSync(path.join(home, 'auth.json'), 'utf8')); } catch { return { ok: false, reason: 'Codex authentication unavailable.' }; }
      const token = auth.tokens?.access_token;
      if (!token) return { ok: false, reason: 'Codex authentication unavailable.' };
      const response = await fetch('https://chatgpt.com/backend-api/codex/models?client_version=0.162.1', { headers: { Authorization: 'Bearer ' + token, 'ChatGPT-Account-Id': auth.tokens.account_id || '' }, signal: AbortSignal.timeout(10000) });
      if (!response.ok) return { ok: false, reason: 'Codex model catalog HTTP ' + response.status };
      const body = await response.json();
      if (!Array.isArray(body.models)) return { ok: false, reason: 'Codex returned an invalid model catalog.' };
      return { ok: true, models: body.models.map(model => ({ cli: model.slug || model.id, label: model.display_name || model.slug || model.id, capabilities: { canUseTools: true, canCode: true, canOrchestrate: true, canResearch: true } })) };
    } },
  grok: { nativeModel: /^grok-/, sourceProtectedResearch: false,
    account: provider => require('./provider-accounts.cjs').grokAccount(provider),
    quota: () => require('./planUsage.js').readGrok(require('node:os').homedir(), fetch, Date.now()),
    discover: () => require('./provider-models.cjs').listGrok() },
  antigravity: { nativeModel: /^(gemini-|claude-|gpt-oss-)/, sourceProtectedResearch: false,
    account: () => require('./provider-accounts.cjs').unknownAccount(),
    quota: () => require('./antigravity.cjs').readAntigravity(),
    discover: () => require('./antigravity.cjs').listAntigravity() },
};
for (const [id, adapter] of Object.entries(adapters)) {
  adapter.executable = () => require('./provider-runtime.cjs').executable(id);
  adapter.availability = () => ({ available: Boolean(adapter.executable()) });
  adapter.execute = (model, folder, prompt, lead, research) => require('./provider-runtime.cjs').launchSpec(model, folder, prompt, lead, research);
  adapter.environment = (inherited, platform) => require('./provider-runtime.cjs').providerEnvironment(id, inherited, platform);
  adapter.terminate = pid => require('./runner.cjs').killWorker(pid);
  adapter.resultIdentity = model => ({ provider: model.providerId || model.provider, model: model.cli, ...(model.accountKey ? { accountKey: model.accountKey } : {}) });
  adapter.capabilities = { canCode: true, canResearch: id !== 'claude', canUseTools: true, canInspectFilesystem: true, supportsStreaming: true };
}
function registerAdapter(id, adapter) {
  if (typeof id !== 'string' || !id || Object.hasOwn(adapters, id)) throw new Error('Adapter ID must be unique.');
  for (const method of ['executable', 'availability', 'execute', 'environment', 'terminate', 'quota', 'discover', 'resultIdentity']) if (typeof adapter[method] !== 'function') throw new Error('Adapter requires ' + method);
  if (!(adapter.nativeModel instanceof RegExp)) throw new Error('Adapter requires exact nativeModel validation.');
  adapters[id] = Object.freeze({ ...adapter });
}
function adapterFor(value) { const id = typeof value === 'string' ? value : value?.adapter || value?.provider; return Object.hasOwn(adapters, id) ? adapters[id] : undefined; }
require('./external-adapters.cjs').registerExternalAdapters(registerAdapter);
module.exports = { adapters, adapterFor, registerAdapter };
