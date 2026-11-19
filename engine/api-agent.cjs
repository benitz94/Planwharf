'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { apiHeaders } = require('./external-adapters.cjs');

const schema = (properties, required) => ({ type: 'object', properties, required, additionalProperties: false });
const definitions = [
  { name: 'list_files', description: 'List entries in a workspace directory.', parameters: schema({ path: { type: 'string' } }, ['path']) },
  { name: 'read_file', description: 'Read a UTF-8 workspace file, at most 64 KiB.', parameters: schema({ path: { type: 'string' } }, ['path']) },
  { name: 'write_file', description: 'Write a UTF-8 workspace file. Not allowed for a coordinator or outside the workspace.', parameters: schema({ path: { type: 'string' }, content: { type: 'string' } }, ['path', 'content']) },
  { name: 'write_result', description: 'Submit the required structured Planwharf result JSON to its assigned output file. Include the exact identity fields from the prompt.', parameters: schema({ content: { type: 'string' } }, ['content']) },
  { name: 'run_command', description: 'Run a native executable with an argument array in the workspace. Available only to workers with YOLO enabled. No shell command string.', parameters: schema({ command: { type: 'string' }, args: { type: 'array', items: { type: 'string' } } }, ['command', 'args']) }
];
function workspacePath(root, value, writing = false) {
  if (typeof value !== 'string' || value.includes('\0')) throw new Error('Invalid workspace path.');
  const base = fs.realpathSync(root), target = path.resolve(base, value);
  const within = resolved => { const relative = path.relative(base, resolved); return relative === '' || !relative.startsWith('..' + path.sep) && relative !== '..' && !path.isAbsolute(relative); };
  if (!within(target)) throw new Error('Path is outside the selected workspace.');
  const relative = path.relative(base, target);
  if (writing && relative.split(/[\\/]/).some(part => ['.git', '.codex', '.agents'].includes(part.toLowerCase()))) throw new Error('Writing repository or agent control files is not allowed.');
  // Resolve every existing parent to reject symlinks/junctions escaping the workspace.
  let existing = target;
  while (true) {
    try { fs.lstatSync(existing); break; }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
    const parent = path.dirname(existing); if (parent === existing) throw new Error('No existing workspace parent.'); existing = parent;
  }
  if (!within(fs.realpathSync(existing))) throw new Error('Workspace path resolves outside the selected workspace.');
  return target;
}
async function executeTool(call, { folder, lead, alwaysApprove, resultOutputPath, resultIdentity }) {
  const input = call.input;
  if (!input || typeof input !== 'object' || Array.isArray(input)) throw new Error('Invalid tool arguments.');
  if (call.name === 'list_files') return fs.readdirSync(workspacePath(folder, input.path), { withFileTypes: true }).slice(0, 1000).map(entry => ({ name: entry.name, directory: entry.isDirectory() }));
  if (call.name === 'read_file') {
    const file = workspacePath(folder, input.path); if (fs.statSync(file).size > 65536) throw new Error('File exceeds the 64 KiB tool limit.');
    return fs.readFileSync(file, 'utf8');
  }
  if (call.name === 'write_file') {
    if (lead) throw new Error('Coordinator cannot write project files.');
    if (typeof input.content !== 'string' || Buffer.byteLength(input.content) > 1024 * 1024) throw new Error('Invalid write content or write exceeds 1 MiB.');
    const file = workspacePath(folder, input.path, true); fs.mkdirSync(path.dirname(file), { recursive: true });
    workspacePath(folder, input.path, true); fs.writeFileSync(file, input.content, 'utf8'); return { written: input.path };
  }
  if (call.name === 'write_result') {
    if (lead || !resultOutputPath || !resultIdentity) throw new Error('No structured worker result is assigned to this request.');
    if (typeof input.content !== 'string' || Buffer.byteLength(input.content) > 1024 * 1024) throw new Error('Invalid structured result content.');
    const result = JSON.parse(input.content);
    if (!require('./worker-result.cjs').validateResult(result, resultIdentity)) throw new Error('Structured result failed identity or schema validation.');
    fs.writeFileSync(resultOutputPath, JSON.stringify(result, null, 2), 'utf8'); return { recorded: true, status: result.status };
  }
  if (call.name === 'run_command') {
    if (lead || !alwaysApprove) throw new Error('PERMISSION_DENIED: commands require a worker with YOLO enabled.');
    if (typeof input.command !== 'string' || !input.command || /[\r\n\0]/.test(input.command) || !Array.isArray(input.args) || input.args.some(arg => typeof arg !== 'string' || arg.includes('\0'))) throw new Error('Command requires an executable and string arguments.');
    return new Promise(resolve => execFile(input.command, input.args, { cwd: folder, windowsHide: true, timeout: 60000, maxBuffer: 65536, encoding: 'utf8' }, (error, stdout, stderr) => resolve({ exitCode: error ? typeof error.code === 'number' ? error.code : null : 0, stdout: String(stdout).slice(-24000), stderr: String(stderr).slice(-8000) })));
  }
  throw new Error('Unknown API tool: ' + call.name);
}
async function responseJson(response) {
  if (!response.ok) {
    // Never copy response bodies: providers can echo prompts or credentials in errors.
    await response.body?.cancel(); throw new Error('API HTTP ' + response.status);
  }
  const chunks = []; let size = 0;
  for await (const chunk of response.body) { size += chunk.length; if (size > 4 * 1024 * 1024) throw new Error('PROVIDER_PROTOCOL_CHANGED: API response exceeds 4 MiB.'); chunks.push(Buffer.from(chunk)); }
  try { return JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { throw new Error('PROVIDER_PROTOCOL_CHANGED: API returned invalid JSON.'); }
}
async function runAgent(job, { folder = process.cwd(), request = fetch, emit = () => {}, env = process.env } = {}) {
  const { config, model, prompt, lead = false, alwaysApprove = false } = job;
  if (typeof model !== 'string' || !model || typeof prompt !== 'string' || typeof alwaysApprove !== 'boolean') throw new Error('Invalid API job.');
  const headers = apiHeaders(config, env);
  const allowed = job.advisory ? [] : definitions.filter(tool => !lead || !['write_file', 'write_result', 'run_command'].includes(tool.name)).filter(tool => alwaysApprove || tool.name !== 'run_command').filter(tool => tool.name !== 'write_result' || job.resultOutputPath);
  const anthropic = config.apiProtocol === 'anthropic';
  const messages = [{ role: 'user', content: prompt }];
  const tokens = { input: 0, output: 0, cachedInput: 0 }; let observedUsage = false;
  for (let turn = 0; turn < config.maxTurns; turn++) {
    const body = { model, messages, max_tokens: config.maxOutputTokens, stream: false,
      tools: anthropic ? allowed.map(tool => ({ name: tool.name, description: tool.description, input_schema: tool.parameters })) : allowed.map(tool => ({ type: 'function', function: tool })) };
    if (job.advisory) delete body.tools;
    const response = await request(config.baseUrl + (anthropic ? '/messages' : '/chat/completions'), { method: 'POST', headers, body: JSON.stringify(body), redirect: 'error', signal: AbortSignal.timeout(config.requestTimeoutMs) });
    const data = await responseJson(response);
    if (data.model && data.model !== model) throw new Error('PROVIDER_MODEL_IDENTITY_CHANGED: API returned a different model ID. Review the configured exact model ID; no substitution was accepted.');
    const usage = data.usage;
    const inputTokens = anthropic ? usage?.input_tokens : usage?.prompt_tokens;
    const outputTokens = anthropic ? usage?.output_tokens : usage?.completion_tokens;
    if (Number.isSafeInteger(inputTokens) && inputTokens >= 0 && Number.isSafeInteger(outputTokens) && outputTokens >= 0) {
      observedUsage = true; tokens.input += inputTokens; tokens.output += outputTokens;
      const cached = anthropic ? usage.cache_read_input_tokens : usage.prompt_tokens_details?.cached_tokens;
      if (Number.isSafeInteger(cached) && cached >= 0) tokens.cachedInput += cached;
      emit({ type: 'turn.completed', usage: { input_tokens: tokens.input, output_tokens: tokens.output, cached_input_tokens: tokens.cachedInput } });
    }
    let content, calls, assistant;
    if (anthropic) {
      if (!Array.isArray(data.content)) throw new Error('PROVIDER_PROTOCOL_CHANGED: Anthropic content array missing.');
      if (data.stop_reason === 'max_tokens') throw new Error('API_OUTPUT_LIMIT: response was truncated.');
      content = data.content.filter(block => block.type === 'text').map(block => block.text).join('\n');
      calls = data.content.filter(block => block.type === 'tool_use').map(block => ({ id: block.id, name: block.name, input: block.input }));
      assistant = { role: 'assistant', content: data.content };
    } else {
      const choice = data.choices?.[0];
      if (!choice?.message || choice.message.role !== 'assistant') throw new Error('PROVIDER_PROTOCOL_CHANGED: assistant message missing.');
      if (choice.finish_reason === 'length' || choice.finish_reason === 'content_filter') throw new Error('API_OUTPUT_LIMIT: response was truncated or filtered.');
      assistant = choice.message; content = assistant.content;
      if (assistant.tool_calls !== undefined && !Array.isArray(assistant.tool_calls)) throw new Error('PROVIDER_PROTOCOL_CHANGED: tool_calls must be an array.');
      calls = (assistant.tool_calls || []).map(call => { let input; try { input = JSON.parse(call.function.arguments); } catch { throw new Error('PROVIDER_PROTOCOL_CHANGED: invalid tool arguments JSON.'); } return { id: call.id, name: call.function.name, input }; });
    }
    messages.push(assistant);
    if (job.advisory && calls.length) throw new Error('PERMISSION_DENIED: Supervisor responses cannot execute tools.');
    if (!calls.length) {
      if (typeof content !== 'string' || !content.trim()) throw new Error('PROVIDER_PROTOCOL_CHANGED: no recognized response.');
      emit({ type: 'item.completed', item: { type: 'agent_message', text: content } });
      return { answer: content, tokens: observedUsage ? tokens : null };
    }
    if (calls.length > 32 || calls.some(call => typeof call.id !== 'string' || !call.id || typeof call.name !== 'string')) throw new Error('PROVIDER_PROTOCOL_CHANGED: invalid or excessive tool calls.');
    const results = [];
    for (const call of calls) {
      emit({ type: 'item.started', item: { type: 'command_execution', command: call.name } });
      let result;
      try { if (!allowed.some(tool => tool.name === call.name)) throw new Error('PERMISSION_DENIED: tool is not available in this permission mode.'); result = await executeTool(call, { folder, lead, alwaysApprove, resultOutputPath: job.resultOutputPath, resultIdentity: job.resultIdentity }); }
      catch (error) { result = { error: error.message }; }
      const text = JSON.stringify(result);
      if (anthropic) results.push({ type: 'tool_result', tool_use_id: call.id, content: text });
      else messages.push({ role: 'tool', tool_call_id: call.id, content: text });
    }
    if (anthropic) messages.push({ role: 'user', content: results });
  }
  throw new Error('API_TURN_LIMIT: configured maximum tool rounds reached; completion was not verified.');
}
if (require.main === module) {
  const emit = event => process.stdout.write(JSON.stringify(event) + '\n');
  (async () => {
    let input = ''; for await (const chunk of process.stdin) { input += chunk; if (Buffer.byteLength(input) > 16 * 1024 * 1024) throw new Error('API prompt exceeds transport limit.'); }
    await runAgent(JSON.parse(input), { emit });
  })().catch(error => { process.stderr.write(error.message + '\n'); emit({ type: 'turn.failed', error: { message: error.message } }); process.exitCode = 1; });
}
module.exports = { runAgent, executeTool, workspacePath, responseJson };
