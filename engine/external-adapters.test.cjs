'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { apiConfig } = require('./external-adapters.cjs');
const { runAgent, executeTool, workspacePath } = require('./api-agent.cjs');
const { askOrchestrator } = require('./runner.cjs');
const { launchSpec } = require('./providers.cjs');
const { loadCatalog } = require('./provider-catalog.cjs');
test('API protocols run tool rounds, enforce permissions, preserve identity and contain invalid responses', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-api-tools-'));
  const requests = [];
  const server = http.createServer((req, res) => {
    let text = ''; req.on('data', chunk => text += chunk); req.on('end', () => {
      const body = JSON.parse(text); requests.push({ url: req.url, body });
      res.setHeader('Content-Type', 'application/json');
      if (body.model === 'rate-limit') { res.writeHead(429); res.end(JSON.stringify({ secret: 'DO_NOT_COPY_RESPONSE_BODY' })); return; }
      if (body.model === 'invalid') { res.end('{}'); return; }
      if (body.model === 'identity') { res.end(JSON.stringify({ model: 'a-different-model' })); return; }
      const anthropic = req.url.endsWith('/messages');
      assert.equal(anthropic ? req.headers['x-api-key'] : req.headers.authorization, anthropic ? 'fixture-key' : 'Bearer fixture-key');
      const toolResult = body.messages.some(row => row.role === 'tool' || row.role === 'user' && Array.isArray(row.content));
      if (!toolResult) {
        const call = { id: 'call-1', name: 'write_file', input: { path: 'output.txt', content: 'Unicode fixture: è 日本語' } };
        res.end(JSON.stringify(anthropic ? { model: body.model, content: [{ type: 'tool_use', ...call }], stop_reason: 'tool_use', usage: { input_tokens: 10, output_tokens: 4 } }
          : { model: body.model, choices: [{ message: { role: 'assistant', content: null, tool_calls: [{ id: call.id, type: 'function', function: { name: call.name, arguments: JSON.stringify(call.input) } }] }, finish_reason: 'tool_calls' }], usage: { prompt_tokens: 10, completion_tokens: 4 } }));
      } else res.end(JSON.stringify(anthropic ? { model: body.model, content: [{ type: 'text', text: 'Finished fixture' }], stop_reason: 'end_turn', usage: { input_tokens: 20, output_tokens: 5 } }
        : { model: body.model, choices: [{ message: { role: 'assistant', content: 'Finished fixture' }, finish_reason: 'stop' }], usage: { prompt_tokens: 20, completion_tokens: 5 } }));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const baseUrl = 'http://127.0.0.1:' + server.address().port + '/v1';
  try {
    for (const apiProtocol of ['openai', 'anthropic']) {
      const events = [];
      const config = apiConfig({ baseUrl, authEnv: 'FIXTURE_KEY', apiProtocol });
      const result = await runAgent({ config, model: 'exact-fixture', prompt: 'Local fixture', alwaysApprove: false }, { folder, env: { FIXTURE_KEY: 'fixture-key' }, emit: row => events.push(row) });
      assert.equal(result.answer, 'Finished fixture'); assert.deepEqual(result.tokens, { input: 30, output: 9, cachedInput: 0 });
      assert.equal(fs.readFileSync(path.join(folder, 'output.txt'), 'utf8'), 'Unicode fixture: è 日本語');
      assert.ok(!requests.at(-1).body.tools.some(tool => (tool.name || tool.function?.name) === 'run_command'));
      assert.ok(events.some(row => row.type === 'turn.completed'));
    }
    fs.unlinkSync(path.join(folder, 'output.txt'));
    const config = apiConfig({ baseUrl, authEnv: 'FIXTURE_KEY' });
    await runAgent({ config, model: 'exact-fixture', prompt: 'Read-only lead fixture', lead: true, alwaysApprove: true }, { folder, env: { FIXTURE_KEY: 'fixture-key' } });
    assert.ok(!fs.existsSync(path.join(folder, 'output.txt')), 'Lead writes remain denied even if the API requests them');
    for (const model of ['rate-limit', 'invalid', 'identity']) await assert.rejects(runAgent({ config, model, prompt: 'Fixture' }, { folder, env: { FIXTURE_KEY: 'fixture-key' } }), error => !error.message.includes('DO_NOT_COPY_RESPONSE_BODY'));
    await assert.rejects(executeTool({ name: 'run_command', input: { command: process.execPath, args: ['-e', 'process.exit(0)'] } }, { folder, alwaysApprove: false }), /PERMISSION_DENIED/);
    assert.throws(() => workspacePath(folder, '../escape'), /outside/);
    assert.throws(() => workspacePath(folder, '.git/config', true), /control files/);
    assert.throws(() => apiConfig({ baseUrl: 'https://user:secret@example.invalid/v1' }), /API_CONFIGURATION_INVALID/);
    assert.throws(() => apiConfig({ baseUrl: 'http://remote.example/v1' }), /API_CONFIGURATION_INVALID/);
  } finally { await new Promise(resolve => server.close(resolve)); fs.rmSync(folder, { recursive: true, force: true }); }
});
test('API lead and structured worker run through real child processes without secrets in argv or evidence', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-api-process-'));
  const project = path.join(folder, 'source'); fs.mkdirSync(project);
  const output = path.join(folder, 'result.json');
  const identity = { resultId: 'R-fixture', attemptId: 'fixture', subTaskId: 'sub-fixture', megaTaskId: 'mega-fixture' };
  const result = { ...identity, status: 'COMPLETE', summary: 'Isolated fixture output', filesChanged: ['created.txt'], tests: [], blockers: [], nextAction: '', createdAt: new Date().toISOString() };
  const server = http.createServer((req, res) => {
    let text = ''; req.on('data', chunk => text += chunk); req.on('end', () => {
      const body = JSON.parse(text); res.setHeader('Content-Type', 'application/json');
      const done = body.messages.some(row => row.role === 'tool');
      const calls = [{ name: 'write_file', input: { path: 'created.txt', content: 'Fixture only' } }, { name: 'write_result', input: { content: JSON.stringify(result) } }];
      res.end(JSON.stringify({ model: body.model, choices: [{ message: body.model === 'exact-worker' && !done ? { role: 'assistant', content: null, tool_calls: calls.map((call,n) => ({ id: 't-' + n, type: 'function', function: { name: call.name, arguments: JSON.stringify(call.input) } })) } : { role: 'assistant', content: 'Fixture answer' }, finish_reason: 'stop' }], usage: { prompt_tokens: 11, completion_tokens: 7 } }));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const saved = process.env.PLANWHARF_FIXTURE_API_KEY; process.env.PLANWHARF_FIXTURE_API_KEY = 'secret-fixture';
  try {
    const config = loadCatalog({ schema: 3, providers: [{ id: 'deepseek-fixture', adapter: 'api', baseUrl: 'http://127.0.0.1:' + server.address().port + '/v1', authEnv: 'PLANWHARF_FIXTURE_API_KEY', models: [{ id: 'api-lead', cli: 'exact-lead', enabled: true }] }] });
    const lead = config.models[0];
    const answer = await askOrchestrator({ model: lead, folder: project, question: 'Isolated API transport' });
    assert.equal(answer.answer, 'Fixture answer'); assert.deepEqual(answer.tokens, { input: 11, output: 7, cachedInput: 0 });
    const model = { ...lead, cli: 'exact-worker', policy: { alwaysApprove: false } };
    const spec = launchSpec(lead, project, 'Fixture', true); assert.ok(!spec.args.join(' ').includes('secret-fixture')); assert.ok(!spec.stdin.includes('secret-fixture'));
    const jobFile = path.join(folder, 'job.json');
    fs.writeFileSync(jobFile, JSON.stringify({ linkedFolder: project, folder: project, result: path.join(folder, 'result.txt'), title: 'Fixture', detail: 'Fixture', attempt: 'fixture', model,
      structuredResultPath: output, exitPath: path.join(folder, 'exit.json'), megaTaskId: identity.megaTaskId, subTaskId: identity.subTaskId, resultId: identity.resultId }));
    const receipt = await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, [path.join(__dirname, 'runner.cjs'), '--job-file', jobFile], { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] }); let stdout = '', stderr = '';
      child.stdout.on('data', c => stdout += c); child.stderr.on('data', c => stderr += c); child.on('error', reject); child.on('close', code => resolve({ code, stdout, stderr }));
    });
    assert.equal(receipt.code, 0, receipt.stderr); assert.ok(!receipt.stdout.includes('secret-fixture')); assert.deepEqual(JSON.parse(fs.readFileSync(output, 'utf8')), result);
    assert.equal(fs.readFileSync(path.join(project, 'created.txt'), 'utf8'), 'Fixture only');
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(folder, 'result.txt.usage.json'), 'utf8')), { input: 22, output: 14, cachedInput: 0 });
  } finally { if (saved === undefined) delete process.env.PLANWHARF_FIXTURE_API_KEY; else process.env.PLANWHARF_FIXTURE_API_KEY = saved; await new Promise(resolve => server.close(resolve)); fs.rmSync(folder, { recursive: true, force: true }); }
});
test('configured CLI uses declared command, exact ID, prompt transport and independent permission arguments', async () => {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-configured-cli-'));
  try {
    const fixture = path.join(folder, 'fixture.cjs');
    fs.writeFileSync(fixture, "const fs=require('fs');let prompt='';process.stdin.on('data',c=>prompt+=c);process.stdin.on('end',()=>{fs.writeFileSync('received.json',JSON.stringify({args:process.argv.slice(2),prompt}));console.log(JSON.stringify({type:'item.completed',item:{type:'agent_message',text:'Configured CLI fixture'}}));});");
    const model = { id: 'kimi-cli-fixture', provider: 'kimi-cli', adapter: 'configured-cli', cli: 'exact-kimi-fixture', providerConfig: { cliCommand: process.execPath, cliArgs: [fixture, '--model', '{model}', '--cwd', '{cwd}'], yoloArgs: ['--fixture-yolo'], restrictedArgs: ['--fixture-restricted'], outputProtocol: 'codex-jsonl' } };
    for (const alwaysApprove of [false, true]) {
      const reply = await askOrchestrator({ model: { ...model, policy: { alwaysApprove } }, folder, question: 'Unicode è 日本語' }); assert.equal(reply.answer, 'Configured CLI fixture');
      const received = JSON.parse(fs.readFileSync(path.join(folder, 'received.json'), 'utf8')); assert.ok(received.args.includes('exact-kimi-fixture')); assert.ok(received.args.includes(alwaysApprove ? '--fixture-yolo' : '--fixture-restricted')); assert.ok(received.prompt.includes('Unicode è 日本語'));
    }
    assert.throws(() => launchSpec({ ...model, providerConfig: { ...model.providerConfig, restrictedArgs: undefined }, policy: { alwaysApprove: false } }, folder, 'Fixture', true), /PERMISSION_MODE_UNSUPPORTED/);
  } finally { fs.rmSync(folder, { recursive: true, force: true }); }
});
