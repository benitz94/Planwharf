'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const { askOrchestrator, askPrompt, promptFor } = require('./runner.cjs');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-all-provider-transport-'));
const saved = {};
const promptDirs = new Set();
const hash = text => crypto.createHash('sha256').update(text, 'utf8').digest('hex');

// A real native fixture accepts the providers' actual arguments on Windows.
// It reads their documented input channels and emits matching result events.
// No model, credentials, personal notebook, or provider process is involved.
const nativeSource = `
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
class FakeProvider {
  static int Main(string[] args) {
    Console.InputEncoding = new UTF8Encoding(false);
    Console.OutputEncoding = new UTF8Encoding(false);
    if (File.Exists("early-exit")) { Console.Error.Write("isolated early exit"); return 7; }
    var json = new JavaScriptSerializer(); json.MaxJsonLength = int.MaxValue;
    if (args.Length == 2 && args[0] == "auth" && args[1] == "status") { Console.WriteLine(json.Serialize(new { loggedIn = true })); return 0; }
    if (File.Exists("cli-change")) { Console.Error.Write("unknown flag --removed"); return 2; }
    if (File.Exists("protocol-change")) { Console.WriteLine(json.Serialize(new { future_protocol = true })); return 0; }
    int file = Array.IndexOf(args, "--prompt-file");
    int stream = Array.IndexOf(args, "--input-format");
    bool claude = Array.IndexOf(args, "-p") >= 0;
    string input = "", prompt = "";
    if (file >= 0) prompt = File.ReadAllText(args[file + 1], Encoding.UTF8);
    else {
      input = Console.In.ReadToEnd();
      if (stream >= 0) {
        var row = json.Deserialize<Dictionary<string, object>>(input);
        if (row.ContainsKey("type") && (string)row["type"] == "control_request") {
          Console.WriteLine(json.Serialize(new { type = "control_response", response = new { subtype = "success", request_id = (string)row["request_id"], response = new { models = new[] { new { value = "sonnet", resolvedModel = "claude-sonnet-fixture", displayName = "Fixture Sonnet" } } } } })); return 0;
        }
        if ((string)row["event"] != "user") return 8;
        prompt = (string)((Dictionary<string, object>)row["message"])["content"];
      } else prompt = input;
    }
    File.WriteAllText("received.json", json.Serialize(new { args = args, prompt = prompt, input = input }), new UTF8Encoding(false));
    if (claude) {
      string model = args[Array.IndexOf(args, "--model") + 1];
      Console.WriteLine(json.Serialize(new { type = "system", subtype = "init", model = File.Exists("identity-change") ? "claude-wrong-model" : model }));
      if (File.Exists("result-error")) { Console.WriteLine(json.Serialize(new { type = "result", subtype = "error_max_turns", is_error = true, result = "isolated failed turn" })); return 0; }
    }
    if (stream >= 0 && File.Exists("result-error")) { Console.WriteLine(json.Serialize(new { @event = "result", result = new { status = "ERROR", response = "", error = "isolated failed turn" } })); return 0; }
    string text;
    using (var sha = SHA256.Create()) text = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(prompt))).Replace("-", "").ToLowerInvariant();
    if (claude) Console.WriteLine(json.Serialize(new { type = "result", subtype = "success", is_error = false, result = text, duration_ms = 1 }));
    else if (stream >= 0) Console.WriteLine(json.Serialize(new { @event = "result", result = new { status = "SUCCESS", response = text, duration_seconds = 0.001 } }));
    else if (file >= 0) Console.WriteLine(json.Serialize(new { type = "result", result = text, duration_ms = 1 }));
    else Console.WriteLine(json.Serialize(new { type = "item.completed", item = new { type = "agent_message", text = text } }));
    return 0;
  }
}
`;
const scriptSource = `#!/usr/bin/env node
const fs = require('node:fs'), crypto = require('node:crypto');
const args = process.argv.slice(2);
if (args[0] === 'auth' && args[1] === 'status') { console.log(JSON.stringify({loggedIn:true})); process.exit(0); }
if (fs.existsSync('early-exit')) { process.stderr.write('isolated early exit'); process.exit(7); }
if (fs.existsSync('cli-change')) { process.stderr.write('unknown flag --removed'); process.exit(2); }
if (fs.existsSync('protocol-change')) { console.log(JSON.stringify({future_protocol:true})); process.exit(0); }
const file = args.indexOf('--prompt-file'), stream = args.indexOf('--input-format');
const claude = args.includes('-p');
const input = file < 0 ? fs.readFileSync(0, 'utf8') : '';
if(stream >= 0 && JSON.parse(input).type === 'control_request') {const row=JSON.parse(input);console.log(JSON.stringify({type:'control_response',response:{subtype:'success',request_id:row.request_id,response:{models:[{value:'sonnet',resolvedModel:'claude-sonnet-fixture',displayName:'Fixture Sonnet'}]}}}));process.exit(0);}
const prompt = file >= 0 ? fs.readFileSync(args[file + 1], 'utf8') : stream >= 0 ? JSON.parse(input).message.content : input;
fs.writeFileSync('received.json', JSON.stringify({args,prompt,input}));
if (claude) {
 console.log(JSON.stringify({type:'system',subtype:'init',model:fs.existsSync('identity-change')?'claude-wrong-model':args[args.indexOf('--model')+1]}));
 if(fs.existsSync('result-error')) {console.log(JSON.stringify({type:'result',subtype:'error_max_turns',is_error:true,result:'isolated failed turn'}));process.exit(0);}
}
if (stream >= 0 && fs.existsSync('result-error')) { console.log(JSON.stringify({event:'result',result:{status:'ERROR',response:'',error:'isolated failed turn'}})); process.exit(0); }
const text = crypto.createHash('sha256').update(prompt, 'utf8').digest('hex');
console.log(JSON.stringify(claude ? {type:'result',subtype:'success',is_error:false,result:text} : stream >= 0 ? {event:'result',result:{status:'SUCCESS',response:text}} : file >= 0 ? {type:'result',result:text} : {type:'item.completed',item:{type:'agent_message',text}}));
`;

function receipt(folder) {
  const value = JSON.parse(fs.readFileSync(path.join(folder, 'received.json'), 'utf8'));
  const file = value.args.indexOf('--prompt-file');
  if (file >= 0) {
    const promptFile = value.args[file + 1];
    assert.ok(!path.resolve(promptFile).startsWith(path.resolve(folder) + path.sep), 'Prompt artifacts stay outside linked source');
    promptDirs.add(path.dirname(promptFile));
  }
  assert.ok(value.args.every(arg => arg.length < 32767));
  if (!value.args.includes('--no-session-persistence')) assert.ok(!value.args.includes('-p'));
  return value;
}

async function main() {
  let executable;
  if (process.platform === 'win32') {
    const compiler = path.join(process.env.WINDIR || 'C:/Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
    assert.ok(fs.existsSync(compiler), '.NET Framework compiler is needed for the native Windows fixture');
    const source = path.join(dir, 'FakeProvider.cs');
    executable = path.join(dir, 'FakeProvider.exe');
    fs.writeFileSync(source, nativeSource);
    const built = spawnSync(compiler, ['/nologo', '/r:System.Web.Extensions.dll', '/out:' + executable, source], { encoding: 'utf8', timeout: 10000 });
    assert.equal(built.status, 0, built.stdout + built.stderr);
  } else {
    executable = path.join(dir, 'fake-provider');
    fs.writeFileSync(executable, scriptSource, { mode: 0o700 });
  }
  const catalog = await require('./claude.cjs').listClaude({ command: executable });
  assert.equal(catalog.ok, true); assert.deepEqual(catalog.models.map(model => model.cli), ['claude-sonnet-fixture']);
  for (const [provider, cli] of [['codex', 'gpt-6-astra'], ['antigravity', 'gemini-3.1-pro-high'], ['grok', 'grok-4.6'], ['claude', 'claude-sonnet-fixture']]) {
    const env = 'PLANWHARF_' + provider.toUpperCase(); saved[env] = process.env[env]; process.env[env] = executable;
    const folder = path.join(dir, provider); fs.mkdirSync(folder);
    const model = { provider, cli, label: 'Isolated ' + provider, alwaysApprove: true };
    const detail = 'Full discussion \u00e8 \u65e5\u672c\u8a9e \u{1f3ce}\u{fe0f}, quotes " and newlines.\n'.repeat(4000);
    const info = { model, folder, question: 'Follow the full long conversation.', state: { history: [{ user: detail, orchestrator: 'Prior answer.' }] } };
    const expected = askPrompt(info); assert.ok(expected.length > 32767);
    const lead = await askOrchestrator(info);
    assert.equal(lead.answer, hash(expected), provider + ' receives the entire lead prompt');
    const received = receipt(folder); assert.equal(received.prompt, expected);
    if (provider === 'antigravity') {
      assert.equal(received.input.split('\n').filter(Boolean).length, 1, 'AGY gets exactly one complete JSON user event');
      assert.equal(JSON.parse(received.input).event, 'user');
    }
    const opts = { linkedFolder: folder, folder, result: path.join(folder, 'result.txt'), title: 'Isolated implementation', detail, worker: 'Fixture', attempt: 'fixture', model };
    fs.writeFileSync(path.join(folder, 'job.json'), JSON.stringify(opts));
    const bootstrap = path.join(folder, 'bootstrap.cjs');
    fs.writeFileSync(bootstrap, `const opts=require('./job.json');const args=['node','runner','--linked-folder',opts.linkedFolder];for(const key of ['folder','result','title','detail','worker','attempt'])args.push('--'+key,opts[key]);args.push('--model-json',JSON.stringify(opts.model));require(${JSON.stringify(path.join(__dirname, 'runner.cjs'))}).run(args);`);
    const worker = spawnSync(process.execPath, [bootstrap], { encoding: 'utf8', timeout: 10000, env: process.env });
    assert.equal(worker.error, undefined); assert.equal(worker.status, 0, provider + ': ' + worker.stderr);
    assert.match(fs.readFileSync(opts.result, 'utf8'), /STATUS: COMPLETE/);
    assert.equal(receipt(folder).prompt, promptFor(opts), provider + ' receives the entire worker prompt');
    for (const [marker, category] of [['cli-change', 'PROVIDER_CLI_INCOMPATIBLE'], ['protocol-change', 'PROVIDER_PROTOCOL_CHANGED']]) {
      fs.writeFileSync(path.join(folder, marker), 'fixture');
      const changed = await askOrchestrator({ model, folder, question: 'Isolated interface drift probe.' });
      assert.equal(changed.diagnostic.code, category, provider + ': drift diagnostic');
      assert.ok(changed.failure);
      fs.rmSync(path.join(folder, marker));
    }
    if (provider === 'claude') {
      fs.writeFileSync(path.join(folder, 'identity-change'), 'fixture');
      const changed = await askOrchestrator({ model, folder, question: 'Isolated identity mismatch.' });
      assert.equal(changed.diagnostic.code, 'PROVIDER_MODEL_IDENTITY_CHANGED');
      const changedWorker = spawnSync(process.execPath, [bootstrap], { encoding: 'utf8', timeout: 10000 });
      assert.notEqual(changedWorker.status, 0);
      assert.match(fs.readFileSync(opts.result, 'utf8'), /STATUS: FAILED/);
      fs.rmSync(path.join(folder, 'identity-change'));
    }
    if (provider === 'antigravity' || provider === 'claude') {
      fs.writeFileSync(path.join(folder, 'early-exit'), 'fixture');
      const early = await askOrchestrator({ model, folder, question: 'x'.repeat(4 * 1024 * 1024) });
      assert.match(early.answer, /CLI failed/);
      fs.rmSync(path.join(folder, 'early-exit'));
      fs.writeFileSync(path.join(folder, 'result-error'), 'fixture');
      const failedTurn = await askOrchestrator({ model, folder, question: 'Isolated failed turn.' });
      assert.match(failedTurn.answer, /CLI failed.*isolated failed turn/);
      const failedWorker = spawnSync(process.execPath, [bootstrap], { encoding: 'utf8', timeout: 10000, env: process.env });
      assert.notEqual(failedWorker.status, 0, 'A zero-exit provider error turn must fail the worker result');
      assert.match(fs.readFileSync(opts.result, 'utf8'), /STATUS: FAILED/);
    }
  }
  console.log('OK: native provider long Unicode lead/worker transports, Claude model discovery and identity, interface changes and zero-exit errors; early exits are handled.');
}
main().catch(error => { console.error(error); process.exitCode = 1; }).finally(() => {
  for (const [env, value] of Object.entries(saved)) { if (value === undefined) delete process.env[env]; else process.env[env] = value; }
  for (const inputDir of promptDirs) fs.rmSync(inputDir, { recursive: true, force: true });
  fs.rmSync(dir, { recursive: true, force: true });
});
