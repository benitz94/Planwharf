'use strict';
const fs = require('node:fs');
const path = require('node:path');

function apiConfig(provider) {
  const config = provider?.providerConfig || provider || {};
  let url; try { url = new URL(config.baseUrl); } catch { throw new Error('API_CONFIGURATION_INVALID: baseUrl is required.'); }
  if (url.username || url.password || url.search || url.hash || (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)))) throw new Error('API_CONFIGURATION_INVALID: use HTTPS, or HTTP on loopback, without credentials or query parameters.');
  if (config.authEnv !== undefined && !/^[A-Za-z_][A-Za-z0-9_]*$/.test(config.authEnv)) throw new Error('API_CONFIGURATION_INVALID: authEnv must name an environment variable.');
  const protocol = config.apiProtocol || 'openai';
  if (!['openai', 'anthropic'].includes(protocol)) throw new Error('API_CONFIGURATION_INVALID: apiProtocol must be openai or anthropic.');
  for (const [key, max] of [['maxOutputTokens', 65536], ['maxTurns', 100], ['requestTimeoutMs', 300000]]) if (config[key] !== undefined && (!Number.isInteger(config[key]) || config[key] < 1 || config[key] > max)) throw new Error('API_CONFIGURATION_INVALID: invalid ' + key);
  return { baseUrl: url.href.replace(/\/+$/, ''), authEnv: config.authEnv, apiProtocol: protocol, maxOutputTokens: config.maxOutputTokens || 4096, maxTurns: config.maxTurns || 30, requestTimeoutMs: config.requestTimeoutMs || 90000 };
}
function apiHeaders(config, env = process.env) {
  const key = config.authEnv ? env[config.authEnv] : null;
  if (config.authEnv && !key) throw new Error('PROVIDER_AUTHENTICATION_FAILED: API key environment variable is not set: ' + config.authEnv);
  return { 'Content-Type': 'application/json', ...(config.apiProtocol === 'anthropic' ? { 'anthropic-version': '2023-06-01', ...(key ? { 'x-api-key': key } : {}) } : key ? { Authorization: 'Bearer ' + key } : {}) };
}
function configuredCommand(provider) {
  const config = provider?.providerConfig || provider || {};
  const command = config.cliCommand;
  if (typeof command !== 'string' || !command || /[\r\n\0]/.test(command)) return null;
  if (path.isAbsolute(command)) return fs.existsSync(command) && fs.statSync(command).isFile() ? command : null;
  // Bare executable names use PATH; shell wrappers and shell command strings are not executed.
  if (!/^[\w.-]+$/.test(command)) return null;
  for (const dir of (process.env.PATH || '').split(path.delimiter)) for (const ext of process.platform === 'win32' && !/\.exe$/i.test(command) ? ['.exe'] : ['']) {
    const file = path.join(dir, command + ext); if (fs.existsSync(file) && fs.statSync(file).isFile()) return file;
  }
  return null;
}
function cliSpec(model, folder, prompt, lead, research) {
  if (research) throw new Error('Source-protected research is not supported by configured-cli.');
  const config = model.providerConfig || {};
  const command = configuredCommand(model);
  if (!command) throw new Error('Configured CLI executable not found.');
  const base = config.cliArgs;
  const yolo = model.policy?.alwaysApprove ?? model.alwaysApprove ?? false;
  const permissions = yolo ? config.yoloArgs : config.restrictedArgs;
  if (!Array.isArray(base) || !base.some(arg => typeof arg === 'string' && arg.includes('{model}'))) throw new Error('CLI_CONFIGURATION_INVALID: cliArgs must explicitly contain {model}.');
  if (!Array.isArray(permissions)) throw new Error('PERMISSION_MODE_UNSUPPORTED: configure the CLI arguments for the selected YOLO mode explicitly.');
  const args = [...base, ...permissions].map(arg => {
    if (typeof arg !== 'string' || /[\0\r\n]/.test(arg)) throw new Error('CLI_CONFIGURATION_INVALID: arguments must be single-line strings.');
    return arg.replaceAll('{model}', model.cli).replaceAll('{cwd}', folder);
  });
  if (!['codex-jsonl', 'messages-jsonl', 'antigravity-jsonl'].includes(config.outputProtocol)) throw new Error('CLI_CONFIGURATION_INVALID: declare a supported structured outputProtocol.');
  return { command, args, cwd: folder, provider: model.providerId || model.provider, adapter: 'configured-cli', model: model.cli,
    stdin: config.outputProtocol === 'antigravity-jsonl' ? JSON.stringify({ event: 'user', message: { content: prompt } }) + '\n' : prompt, outputProtocol: config.outputProtocol };
}
function registerExternalAdapters(register) {
  const common = { nativeModel: /\S+/, sourceProtectedResearch: false, environment: inherited => ({ ...inherited }), terminate: pid => require('./runner.cjs').killWorker(pid),
    resultIdentity: model => ({ provider: model.providerId || model.provider, model: model.cli }),
    quota: async () => ({ readable: false, exhausted: false, leftPct: null, reason: 'This adapter has no verified provider balance/quota endpoint. Observed API tokens are recorded separately.' }),
    capabilities: { canCode: true, canResearch: false, canUseTools: true, canInspectFilesystem: true, supportsStreaming: false } };
  register('api', { ...common,
    executable: () => process.execPath,
    availability: provider => { try { apiHeaders(apiConfig(provider)); return { available: true }; } catch (error) { return { available: false, reason: error.message }; } },
    execute: (model, folder, prompt, lead, research) => {
      if (research) throw new Error('Source-protected research is not supported by the API adapter.');
      if (!lead && !model.apiResultOutputPath) throw new Error('API workers require the structured MegaTask result contract.');
      const config = apiConfig(model); apiHeaders(config);
      return { command: process.execPath, args: [path.join(__dirname, 'api-agent.cjs')], cwd: folder, provider: model.providerId || model.provider, adapter: 'api', model: model.cli,
        stdin: JSON.stringify({ config, model: model.cli, prompt, lead, advisory: model.advisory === true, alwaysApprove: model.policy?.alwaysApprove ?? model.alwaysApprove ?? false, resultOutputPath: model.apiResultOutputPath, resultIdentity: model.apiResultIdentity }) };
    },
    discover: async provider => {
      const config = apiConfig(provider);
      const response = await fetch(config.baseUrl + '/models', { headers: apiHeaders(config), redirect: 'error', signal: AbortSignal.timeout(10000) });
      if (!response.ok) return { ok: false, reason: 'API model catalog HTTP ' + response.status + '; configured model IDs are preserved.' };
      const body = await response.json();
      if (!Array.isArray(body.data)) return { ok: false, reason: 'API model catalog format changed; expected data array.' };
      return { ok: true, models: body.data.filter(row => typeof row.id === 'string').map(row => ({ cli: row.id, label: row.display_name || row.id, capabilities: { canCode: false, canUseTools: false, canOrchestrate: false }, orchestratorEligible: false, workerEligible: false })) };
    } });
  register('configured-cli', { ...common, executable: provider => configuredCommand(provider), availability: provider => ({ available: Boolean(configuredCommand(provider)) }), execute: cliSpec,
    discover: async () => ({ ok: false, reason: 'Configured CLI uses explicit model IDs; no enumeration command has been verified.' }) });
}
module.exports = { apiConfig, apiHeaders, configuredCommand, cliSpec, registerExternalAdapters };
