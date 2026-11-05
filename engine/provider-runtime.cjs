'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { modelRouteError } = require('./model-routing.cjs');
let codexCertificateBundle;
const storeShellAccess = new Map();

function providerEnvironment(provider, inherited = process.env, platform = process.platform) {
  const env = { ...inherited };
  if (provider !== 'codex' || platform !== 'win32') return env;
  const pathKey = Object.keys(env).find(key => key.toUpperCase() === 'PATH');
  if (pathKey) env[pathKey] = env[pathKey].split(';').filter(directory => {
    if (!/WindowsApps/i.test(directory)) return true;
    const shell = path.join(directory, 'pwsh.exe');
    // Store execution aliases are reparse points: existsSync follows the denied
    // target and returns false even though Codex can discover the alias.
    try { fs.lstatSync(shell); } catch { return true; }
    if (!storeShellAccess.has(shell)) {
      const checked = require('node:child_process').spawnSync(shell, ['-NoProfile', '-NonInteractive', '-Command', 'exit 0'], { windowsHide: true, stdio: 'ignore', timeout: 2000 });
      storeShellAccess.set(shell, checked.status === 0);
    }
    // An installed Store shell may be discoverable but denied to this account.
    // Keep all other paths, and let Codex discover the accessible system shell.
    return storeShellAccess.get(shell);
  }).join(';');
  if (env.CODEX_CA_CERTIFICATE || env.SSL_CERT_FILE) return env;
  // Windows native certificate discovery can fail in restricted desktop sessions.
  // Supply Node's verified trust roots through Codex's supported CA setting;
  // never disable verification or replace an explicitly configured CA bundle.
  if (!codexCertificateBundle) {
    const tls = require('node:tls');
    const roots = typeof tls.getCACertificates === 'function' ? tls.getCACertificates('default') : tls.rootCertificates;
    if (!roots.length) throw new Error('No trusted certificates available for Codex HTTPS. Configure CODEX_CA_CERTIFICATE.');
    const certificateDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-codex-ca-'));
    const file = path.join(certificateDir, 'trusted-roots.pem');
    fs.writeFileSync(file, roots.join('\n') + '\n', { encoding: 'utf8', flag: 'wx', mode: 0o600 });
    codexCertificateBundle = file;
    process.once('exit', () => {
      try { fs.unlinkSync(file); fs.rmdirSync(certificateDir); } catch { /* A public CA bundle can remain if the process is forcibly terminated. */ }
    });
  }
  env.CODEX_CA_CERTIFICATE = codexCertificateBundle;
  return env;
}

function executable(provider) {
  const names = { grok: 'grok', codex: 'codex', antigravity: 'agy', claude: 'claude' };
  const name = names[provider];
  if (!name) return null;
  const override = process.env['PLANWHARF_' + provider.toUpperCase()];
  if (provider === 'claude' && override) {
    try { return fs.statSync(override).isFile() ? override : null; } catch { return null; }
  }
  if (override && fs.existsSync(override)) return override;
  const pinned = provider === 'grok' ? path.join(os.homedir(), '.grok', 'bin', 'grok.exe')
    : provider === 'antigravity' ? path.join(os.homedir(), '.gemini', 'bin', 'agy.exe')
    : provider === 'claude' ? path.join(os.homedir(), '.local', 'bin', process.platform === 'win32' ? 'claude.exe' : 'claude') : '';
  if (pinned && fs.existsSync(pinned)) return pinned;
  for (const dir of (process.env.PATH || '').split(path.delimiter)) {
    if (provider === 'codex' && process.platform === 'win32') {
      // Resolve npm's actual binary before PATH proxies (.cmd wrappers reject multiline prompts).
      const arch = process.arch === 'arm64' ? 'arm64' : 'x64';
      const triple = process.arch === 'arm64' ? 'aarch64-pc-windows-msvc' : 'x86_64-pc-windows-msvc';
      const packageRoot = path.join(dir, 'node_modules', '@openai', 'codex');
      const candidates = [
        path.join(packageRoot, 'node_modules', '@openai', 'codex-win32-' + arch, 'vendor', triple, 'bin', 'codex.exe'),
        path.join(dir, 'node_modules', '@openai', 'codex-win32-' + arch, 'vendor', triple, 'bin', 'codex.exe'),
        path.join(packageRoot, 'vendor', triple, 'bin', 'codex.exe')
      ];
      for (const file of candidates) if (fs.existsSync(file) && fs.statSync(file).isFile()) return file;
    }
    // npm's extensionless launcher is a shell script, not a Windows executable.
    for (const ext of process.platform === 'win32' ? ['.exe'] : ['']) {
      const file = path.join(dir, name + ext);
      if (fs.existsSync(file) && fs.statSync(file).isFile()) return file;
    }
  }
  return null;
}

function launchSpec(model, folder, prompt, lead = false, research = false) {
  const routeError = modelRouteError(model);
  if (routeError) throw new Error(routeError);
  const provider = model.providerId || model.provider;
  const adapter = model.adapter || model.provider;
  if (model.accountKey) {
    const current = require('./provider-adapters.cjs').adapterFor(model)?.account?.({ id: provider, adapter });
    if (!current || current.key !== model.accountKey) throw new Error('CLI account changed before launch. Refresh models and review the current account roles.');
  }
  if (research && !require('./provider-adapters.cjs').adapterFor(model)?.sourceProtectedResearch) throw new Error('Research source protection is not supported by the requested adapter: ' + adapter);
  const cli = model.cli;
  const alwaysApprove = model.policy?.alwaysApprove ?? model.alwaysApprove ?? true;
  if (typeof alwaysApprove !== 'boolean') throw new Error('Invalid alwaysApprove model policy.');
  if (!alwaysApprove && adapter === 'antigravity') throw new Error('PERMISSION_MODE_UNSUPPORTED: Antigravity has no verified unattended approval bridge. Enable Always approve / YOLO explicitly or select another adapter.');
  const command = executable(adapter);
  if (!command) throw new Error(provider + ' executable not found (native executable required)');
  let args;
  let stdin;
  let promptFile;
  const final = require('./structured-final.cjs');
  const finalSchema = !lead && model.apiResultOutputPath && model.apiResultIdentity && final.supportsFinalResult({ adapter }) ? JSON.stringify(final.resultSchema(model.apiResultIdentity)) : null;
  if (adapter === 'claude') {
    args = ['-p', '--model', cli, '--output-format', 'stream-json', '--verbose', '--include-partial-messages',
      ...require('./claude.cjs').isolatedArgs, '--disallowedTools', 'Agent,Task', '--permission-mode', alwaysApprove ? 'bypassPermissions' : 'dontAsk'];
    if (finalSchema) args.push('--json-schema', finalSchema);
    stdin = prompt;
  } else if (adapter === 'grok') {
    args = ['--cwd', folder, '-m', cli, '--no-subagents', '--max-turns', lead ? '8' : '50',
      '--permission-mode', alwaysApprove ? 'auto' : 'dontAsk', '--output-format', lead ? 'streaming-messages-json' : 'plain'];
    if (alwaysApprove) args.push('--always-approve');
    if (lead) args.push('--include-partial-messages');
    // Native --prompt-file preserves the complete prompt without OS argv limits.
    const inputDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-grok-prompt-'));
    promptFile = path.join(inputDir, 'prompt.txt');
    fs.writeFileSync(promptFile, prompt, { encoding: 'utf8', mode: 0o600, flag: 'wx' });
    args.push('--prompt-file', promptFile);
  } else if (adapter === 'antigravity') {
    args = ['--model', cli, '--mode', 'accept-edits', '--dangerously-skip-permissions', '--output-format', 'stream-json', '--print-timeout', '30m'];
    // Antigravity's documented stream input uses user events, not -p or '-'.
    args.push('--input-format', 'stream-json');
    stdin = JSON.stringify({ event: 'user', message: { content: prompt } }) + '\n';
  } else {
    args = ['exec', '--skip-git-repo-check', '-C', folder, '-m', cli];
    if (research) args.push('--sandbox', 'workspace-write', '-c', 'approval_policy="never"', '-c', 'sandbox_workspace_write.network_access=true', '-c', 'sandbox_workspace_write.writable_roots=[]');
    else if (alwaysApprove) args.push('--dangerously-bypass-approvals-and-sandbox');
    else args.push('--sandbox', 'workspace-write', '-c', 'approval_policy="never"', '-c', 'sandbox_workspace_write.network_access=false', '-c', 'sandbox_workspace_write.writable_roots=[]');
    if (lead) args.push('--json');
    // Keep conversation text off the OS command line (Windows has a 32K limit).
    args.push('-');
    stdin = prompt;
  }
  return { command, args, cwd: folder, provider, adapter, model: cli, ...(stdin !== undefined ? { stdin } : {}), ...(promptFile ? { promptFile } : {}) };
}

module.exports = { executable, launchSpec, providerEnvironment };
