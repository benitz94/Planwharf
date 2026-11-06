'use strict';
// Opt-in, allowlisted metadata only. Never read notebooks, logs, environment dumps or projects.
const os = require('node:os');
const fs = require('node:fs');
const crypto = require('node:crypto');
const states = ['idle', 'running', 'paused', 'failed', 'completed', 'unknown'];
const codes = ['PROVIDER_EXECUTION_FAILED', 'PROVIDER_CLI_INCOMPATIBLE', 'PROVIDER_PROTOCOL_CHANGED', 'PROVIDER_AUTHENTICATION_FAILED', 'PROVIDER_USAGE_LIMIT', 'PROVIDER_MODEL_UNAVAILABLE', 'PROVIDER_CLI_MISSING', 'PROVIDER_CONNECTION_FAILED', 'unknown'];
const select = (value, allowed) => allowed.includes(value) ? value : 'unknown';
const version = value => typeof value === 'string' && /^v?\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(value) ? value : 'unknown';
function generate(input = {}) {
  return { schema: 1, timestamp: new Date().toISOString(), appVersion: version(input.appVersion), nodeVersion: version(process.version), os: { platform: select(os.platform(), ['win32', 'linux', 'darwin']), architecture: select(os.arch(), ['x64', 'arm64', 'ia32']) },
    state: select(input.state, states), errorCategory: select(input.errorCategory, codes),
    browser: { kind: select(input.browser?.kind, ['edge', 'chrome']), available: input.browser?.available === true },
    adapters: ['codex', 'grok', 'gemini'].map(id => ({ id, available: input.adapters?.[id]?.available === true, version: version(input.adapters?.[id]?.version) })),
    tests: ['claims', 'attempts', 'browser', 'release'].map(id => ({ id, result: select(input.tests?.[id], ['pass', 'fail', 'unavailable', 'not-run']) })),
    configuration: {},
    excluded: ['prompts', 'conversations', 'source', 'credentials', 'paths', 'raw logs', 'environment'] };
}
function preview(input) {
  const text = JSON.stringify(generate(input), null, 2) + '\n';
  return { text, confirmation: crypto.createHash('sha256').update(text).digest('hex') };
}
function exportPreview(prepared, confirmation, destination) {
  const digest = crypto.createHash('sha256').update(prepared.text).digest('hex');
  if (confirmation !== digest || prepared.confirmation !== digest) throw Error('Confirm the exact reviewed preview before exporting');
  // Validate the entire allowlisted shape again, including generated metadata.
  const parsed = JSON.parse(prepared.text);
  const rebuilt = generate(parsed);
  rebuilt.timestamp = parsed.timestamp;
  rebuilt.nodeVersion = parsed.nodeVersion;
  rebuilt.os = parsed.os;
  // Strict checks for generated fields that are not input metadata.
  if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(parsed.timestamp) || version(parsed.nodeVersion) !== parsed.nodeVersion || !['win32','linux','darwin','unknown'].includes(parsed.os?.platform) || !['x64','arm64','ia32','unknown'].includes(parsed.os?.architecture)) throw Error('Invalid diagnostic metadata');
  rebuilt.adapters = ['codex','grok','gemini'].map(id => { const item = parsed.adapters?.find(x => x.id === id); return { id, available: item?.available === true, version: version(item?.version) }; });
  rebuilt.tests = ['claims','attempts','browser','release'].map(id => ({ id, result: select(parsed.tests?.find(x => x.id === id)?.result, ['pass','fail','unavailable','not-run']) }));
  if (JSON.stringify(rebuilt) !== JSON.stringify(parsed)) throw Error('Diagnostic export contains non-allowlisted data');
  fs.writeFileSync(destination, prepared.text, { flag: 'wx', mode: 0o600 });
}
if (require.main === module) {
  const readline = require('node:readline/promises');
  (async () => { const prepared = preview({}); console.log(prepared.text); if (!process.stdin.isTTY || !process.stdout.isTTY) throw Error('Interactive preview confirmation required; nothing exported'); const prompt = readline.createInterface({ input: process.stdin, output: process.stdout }); try { const answer = await prompt.question('Export this exact report to planwharf-support.json? Type EXPORT: '); if (answer === 'EXPORT') exportPreview(prepared, prepared.confirmation, 'planwharf-support.json'); } finally { prompt.close(); } })().catch(error => { console.error(error.message); process.exitCode = 1; });
}
module.exports = { generate, preview, exportPreview };
