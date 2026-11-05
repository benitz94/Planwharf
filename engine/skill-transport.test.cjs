'use strict';
const test = require('node:test'), assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), cp = require('node:child_process'), http = require('node:http');
const { askOrchestrator, promptFor, parseArgs } = require('./runner.cjs');
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
function fixture(t) { const root = path.resolve(__dirname, '../work'); fs.mkdirSync(root, { recursive: true }); const dir = fs.mkdtempSync(path.join(root, 'skill-transport-')); t.after(() => fs.rmSync(dir, { recursive: true, force: true })); return dir; }
function delivery(dir, prompt, provider = 'fixture-cli', model = 'exact-fixture-model') { return { recordId: 'record-fixture', receiptPath: path.join(dir, 'private', 'receipt.json'), projectId: 'project-fixture', attemptId: 'attempt-fixture', provider, model, skills: [{ id: 'project-local/fixture', contentHash: hash('fixture skill') }], assignedPromptHash: hash(prompt) }; }
function cliModel(dir, script) { const file = path.join(dir, 'fixture.cjs'); fs.writeFileSync(file, script); return { id: 'fixture', provider: 'fixture-cli', adapter: 'configured-cli', cli: 'exact-fixture-model', providerConfig: { cliCommand: process.execPath, cliArgs: [file, '{model}'], yoloArgs: [], restrictedArgs: [], outputProtocol: 'codex-jsonl' } }; }
const receivingScript = `let text='';process.stdin.setEncoding('utf8');process.stdin.on('data',s=>text+=s);process.stdin.on('end',()=>{require('node:fs').writeFileSync('received.txt',text);console.log(JSON.stringify({type:'item.completed',item:{type:'agent_message',text:'Fixture accepted local input.'}}));});`;
test('real configured CLI receives dedicated skill once and records only stdin transport', async t => {
 const dir = fixture(t), prompt = 'UNIQUE-SKILL-PROCEDURE', d = delivery(dir, prompt), model = cliModel(dir, receivingScript);
 const result = await askOrchestrator({ folder: dir, projectId: d.projectId, attemptId: d.attemptId, model, question: 'Inspect fixture only.', skillsPrompt: prompt, skillDelivery: d }); assert.equal(result.failure, undefined);
 const received = fs.readFileSync(path.join(dir, 'received.txt'), 'utf8'); assert.equal(received.split(prompt).length - 1, 1);
 const row = JSON.parse(fs.readFileSync(d.receiptPath, 'utf8')); assert.equal(row.submitted, true); assert.equal(row.transportHash, hash(received)); assert.equal(row.assignedPromptHash, hash(prompt)); assert.equal(row.provider, 'fixture-cli'); assert.equal(row.model, 'exact-fixture-model');
 assert.equal(row.transport, 'cli-stdin-written-consumption-unverified'); assert.equal(row.loaded, 'unverified'); assert.equal(row.followed, 'unverified'); assert.equal(row.verified, false); assert.equal(JSON.stringify(row).includes(prompt), false);
});
test('worker job carries dedicated instructions once and records exact attempt identity', t => {
 const dir = fixture(t), prompt = 'UNIQUE-WORKER-PROCEDURE', d = delivery(dir, prompt), model = cliModel(dir, receivingScript);
 const job = { linkedFolder: dir, folder: dir, result: path.join(dir, 'result.txt'), projectId: d.projectId, attempt: d.attemptId, title: 'Fixture only', detail: 'Inspect isolated fixture.', skillsPrompt: prompt, skillDelivery: d, model };
 const input = path.join(dir, 'job.json'); fs.writeFileSync(input, JSON.stringify(job));
 assert.equal(parseArgs(['node', 'runner', '--job-file', input]).skillsPrompt, prompt);
 assert.equal(promptFor(job).split(prompt).length - 1, 1);
 const result = cp.spawnSync(process.execPath, [path.join(__dirname, 'runner.cjs'), '--job-file', input], { encoding: 'utf8', timeout: 10000, windowsHide: true }); assert.equal(result.status, 0, result.stderr);
 const received = fs.readFileSync(path.join(dir, 'received.txt'), 'utf8'); assert.equal(received.split(prompt).length - 1, 1);
 const row = JSON.parse(fs.readFileSync(d.receiptPath, 'utf8')); assert.equal(row.attemptId, d.attemptId); assert.equal(row.transportHash, hash(received)); assert.equal(row.verified, false);
});
test('identity/hash mismatch prevents fixture launch and receipt creation', async t => {
 const dir = fixture(t), prompt = 'FIXTURE-INSTRUCTIONS', d = delivery(dir, prompt), model = cliModel(dir, receivingScript);
 for (const change of [{ provider: 'other' }, { model: 'other' }, { projectId: 'other' }, { attemptId: 'other' }, { promptHash: hash('wrong') }]) {
  const result = await askOrchestrator({ folder: dir, projectId: d.projectId, attemptId: d.attemptId, model, question: 'Fixture only', skillsPrompt: prompt, skillDelivery: { ...d, ...change } }); assert.equal(result.failure, 'launch');
 }
 assert.equal(fs.existsSync(path.join(dir, 'received.txt')), false); assert.equal(fs.existsSync(d.receiptPath), false);
});
test('early pipe failure does not record successful prompt transport', async t => {
 const dir = fixture(t), prompt = 'X'.repeat(2 * 1024 * 1024), d = delivery(dir, prompt), model = cliModel(dir, 'process.exit(1);');
 const result = await askOrchestrator({ folder: dir, projectId: d.projectId, attemptId: d.attemptId, model, question: 'Fixture only', skillsPrompt: prompt, skillDelivery: d }); assert.ok(result.failure); assert.equal(fs.existsSync(d.receiptPath), false);
});
test('loopback API receives prompt while receipt clearly records local adapter stdin only', async t => {
 const dir = fixture(t), prompt = 'UNIQUE-API-PROCEDURE'; let observed;
 const server = http.createServer(async (req, res) => { let text = ''; for await (const chunk of req) text += chunk; observed = JSON.parse(text); res.writeHead(200, { 'Content-Type': 'application/json' }); res.end(JSON.stringify({ choices: [{ message: { role: 'assistant', content: 'Offline fixture response.' } }] })); });
 t.after(() => new Promise(resolve => server.close(resolve))); await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
 const d = delivery(dir, prompt, 'fixture-api', 'exact-api-model');
 const model = { id: 'api-fixture', provider: 'fixture-api', adapter: 'api', cli: 'exact-api-model', providerConfig: { baseUrl: 'http://127.0.0.1:' + server.address().port, apiProtocol: 'openai' } };
 const result = await askOrchestrator({ purpose: 'supervisor', folder: dir, projectId: d.projectId, attemptId: d.attemptId, model, question: 'Fixture evidence only', skillsPrompt: prompt, skillDelivery: d }); assert.equal(result.failure, undefined);
 assert.equal(observed.messages[0].content.split(prompt).length - 1, 1);
 const row = JSON.parse(fs.readFileSync(d.receiptPath, 'utf8')); assert.equal(row.transport, 'api-adapter-stdin-written-http-unverified'); assert.equal(row.submitted, true); assert.equal(row.verified, false); assert.equal(row.provider, 'fixture-api');
});
