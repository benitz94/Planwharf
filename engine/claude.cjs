'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, execFile } = require('node:child_process');

// Execution and discovery delegate authentication to Claude Code; credentials never enter prompts.
const isolatedArgs = ['--no-session-persistence', '--setting-sources', 'user', '--settings', '{"disableAllHooks":true,"autoMemoryEnabled":false}', '--strict-mcp-config', '--mcp-config', '{"mcpServers":{}}', '--no-chrome'];
function parseModels(rows) {
  if (!Array.isArray(rows)) return { ok: false, reason: 'Claude Code returned no model catalog.' };
  const models = [];
  for (const row of rows) {
    const cli = row?.resolvedModel || row?.value;
    if (typeof cli !== 'string' || cli.length > 512 || !/^claude-[a-zA-Z0-9][a-zA-Z0-9._-]*$/.test(cli)) continue;
    const previous = models.find(model => model.cli === cli);
    if (previous) { if (row.value !== 'default' && typeof row.displayName === 'string') previous.label = row.displayName; continue; }
    models.push({ cli, label: row.displayName || cli, capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true, canOrchestrate: true, canResearch: false } });
  }
  return models.length ? { ok: true, models } : { ok: false, reason: 'Claude Code returned no exact model IDs. Aliases were not guessed.' };
}
async function listClaude({ command = require('./providers.cjs').executable('claude'), run = execFile } = {}) {
  if (!command) return { ok: false, reason: 'Claude Code executable not found. Install Claude Code and run claude auth login.' };
  const authenticated = await new Promise(resolve => run(command, ['auth', 'status'], { windowsHide: true, timeout: 10000, maxBuffer: 65536 }, (error, stdout) => {
    try { resolve(!error && JSON.parse(stdout).loggedIn === true); } catch { resolve(false); }
  }));
  if (!authenticated) return { ok: false, reason: 'Claude Code authentication unavailable. Run claude auth login, then refresh models.' };
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-claude-models-'));
  try {
    return await new Promise(resolve => {
      const child = spawn(command, ['-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose', ...isolatedArgs], { cwd: dir, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
      child.stdout.setEncoding('utf8');
      let output = '', result, overflow = false;
      const timer = setTimeout(() => { child.kill(); }, 20000);
      child.stderr.resume();
      child.stdout.on('data', chunk => {
        if (overflow) return;
        output += chunk;
        if (Buffer.byteLength(output) > 1024 * 1024) { overflow = true; child.kill(); }
      });
      child.on('error', () => { clearTimeout(timer); resolve({ ok: false, reason: 'Claude Code model discovery could not start.' }); });
      child.on('close', code => {
        clearTimeout(timer);
        if (!overflow && code === 0) for (const line of output.split(/\r?\n/)) {
          try { const row = JSON.parse(line); if (row.type === 'control_response' && row.response?.request_id === 'planwharf-models' && row.response.subtype === 'success') result = parseModels(row.response.response?.models); } catch {}
        }
        resolve(result || { ok: false, reason: 'Claude Code model discovery failed or its control protocol changed. No substitute was selected.' });
      });
      child.stdin.on('error', () => child.kill());
      // Initialization enumerates models without sending a user turn or invoking inference.
      child.stdin.end(JSON.stringify({ type: 'control_request', request_id: 'planwharf-models', request: { subtype: 'initialize' } }) + '\n');
    });
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
}
module.exports = { isolatedArgs, parseModels, listClaude };
