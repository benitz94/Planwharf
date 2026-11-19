'use strict';

const fs = require('node:fs');
const path = require('node:path');
const {
  DEFAULT_OLLAMA_MODEL,
  assessPairedActivation,
  writeIsolatedNotebook,
  copyBackup,
  liveExchange,
  reportMarkdown
} = require('./phone-activation.cjs');
const { listInstalledModels, askOllama } = require('./phone-ollama.cjs');

function arg(name) {
  const index = process.argv.indexOf(name);
  return index >= 0 ? process.argv[index + 1] : '';
}

async function main() {
  const from = arg('--from');
  const attempts = arg('--attempts') || from;
  const backupTo = arg('--backup-to');
  const notebook = arg('--notebook');
  const starter = arg('--starter');
  const report = arg('--report');
  const listenersFile = arg('--listeners');
  const build = arg('--build');
  const listeners = listenersFile && fs.existsSync(listenersFile) ? JSON.parse(fs.readFileSync(listenersFile, 'utf8')) : [];
  const backup = from && backupTo ? copyBackup(from, backupTo, attempts) : { files: [] };
  const made = notebook && starter ? writeIsolatedNotebook(notebook, starter) : null;
  const tokenPath = attempts ? path.join(attempts, 'telegram-bot.token') : '';
  const token = tokenPath && fs.existsSync(tokenPath) ? fs.readFileSync(tokenPath, 'utf8').trim() : '';
  const assessment = assessPairedActivation({
    listeners,
    thisBuildPath: build,
    tokenPresent: Boolean(token),
    isolatedHasToken: Boolean(made && fs.existsSync(path.join(made.attempts, 'telegram-bot.token'))),
    model: DEFAULT_OLLAMA_MODEL
  });
  const listed = await listInstalledModels({ timeoutMs: 4000 });
  const probe = { ok: listed.ok, models: listed.models, installed: listed.models.includes(DEFAULT_OLLAMA_MODEL) };
  let local = null;
  if (probe.installed) {
    local = await askOllama({
      model: DEFAULT_OLLAMA_MODEL,
      timeoutMs: 60000,
      messages: [
        { role: 'system', content: 'Reply with exactly one word: ready' },
        { role: 'user', content: 'ready?' }
      ]
    });
  }
  const exchange = arg('--live') === 'yes'
    ? await liveExchange({ allowed: assessment.allowed, token, fetchImpl: globalThis.fetch })
    : { ran: false, reason: 'not requested' };
  const markdown = reportMarkdown({ assessment, probe, backup, exchange, build });
  const localLine = local ? `Local model reply ok: ${Boolean(local.ok)}\nLocal model: ${local.model}\n` : 'Local model reply: not run\n';
  const replyLine = local?.ok ? `Local reply:\n${local.text}\n` : '';
  if (report) {
    fs.mkdirSync(path.dirname(path.resolve(report)), { recursive: true });
    fs.writeFileSync(report, markdown + localLine + replyLine);
    const jsonPath = report.replace(/\.md$/i, '.json');
    fs.writeFileSync(jsonPath, JSON.stringify({
      allowed: assessment.allowed,
      blocker: assessment.blocker,
      model: assessment.model,
      installedListeners: assessment.installedListeners,
      probe,
      exchange: { ran: exchange.ran, ok: exchange.ok || false, updates: exchange.updates || 0, reason: exchange.reason || '' },
      local: local ? { ok: local.ok, model: local.model, reason: local.reason || '', text: local.ok ? local.text : '' } : null,
      backup: (backup.files || []).map(file => ({ name: file.name, sha256: file.sha256, bytes: file.bytes })),
      notebook: made ? made.root : null,
      build: build || ''
    }, null, 2) + '\n');
  }
  process.stdout.write(`${assessment.allowed ? 'ALLOWED' : 'BLOCKED'}\n${assessment.blocker || 'no blocker'}\n`);
}

main().catch(error => {
  process.stderr.write(`${error && error.message ? error.message : error}\n`);
  process.exitCode = 1;
});
