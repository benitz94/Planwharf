'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { listTasks, setupTask, specification, evaluateTask } = require('./tasks.cjs');

// Oracle fixtures validate the grader, never starter files or model prompts.
// These deliberately compact fixtures are test infrastructure, not task output.
const CONFIG = String.raw`
'use strict';
exports.parseConfig = text => {
 if(typeof text!=='string')throw Error('text');
 const input=JSON.parse(text);
 if(!input||Array.isArray(input)||typeof input!=='object'||Object.keys(input).some(k=>!['port','host','retries','logLevel'].includes(k)))throw Error('object');
 const out={port:3000,host:'127.0.0.1',retries:2,logLevel:'info',...input};
 if(!Number.isInteger(out.port)||out.port<1||out.port>65535||!Number.isInteger(out.retries)||out.retries<0||out.retries>10||typeof out.host!=='string'||!out.host.length||out.host.length>253||/\s/.test(out.host)||!['debug','info','warn','error'].includes(out.logLevel))throw Error('invalid');
 return out;
};
`;

const API = String.raw`
'use strict';
const fs=require('node:fs'),path=require('node:path'),http=require('node:http');
exports.createServer=({file,onStage=()=>{}})=>{
 fs.mkdirSync(path.dirname(file),{recursive:true});
 let state=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):{version:1,items:[]};
 if(state.version!==1||!Array.isArray(state.items))throw Error('bad store');
 let serial=Promise.resolve();
 const validId=id=>typeof id==='string'&&/^[a-z][a-z0-9-]{0,31}$/.test(id);
 const validate=(body,patch=false)=>{
  if(!body||typeof body!=='object'||Array.isArray(body)||!Object.keys(body).length||Object.keys(body).some(k=>!(patch?['title','done']:['id','title','done']).includes(k)))throw Error('body');
  if(!patch&&(!validId(body.id)||!Object.hasOwn(body,'title')))throw Error('id/title');
  if(Object.hasOwn(body,'title')&&(typeof body.title!=='string'||!body.title.trim()||body.title.trim().length>80))throw Error('title');
  if(Object.hasOwn(body,'done')&&typeof body.done!=='boolean')throw Error('done');
 };
 return http.createServer(async(req,res)=>{
  const send=(status,value)=>{res.statusCode=status;if(value!==undefined){res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));}else res.end();};
  try{
   const chunks=[];let length=0;for await(const chunk of req){length+=chunk.length;if(length<=17000)chunks.push(chunk);}
   if(length>16384)return send(413,{error:'large'});
   const route=new URL(req.url,'http://localhost').pathname,match=route.match(/^\/items\/([^/]+)$/),id=match?.[1];
   if(req.method==='GET'){await serial;if(route==='/items')return send(200,[...state.items].sort((a,b)=>a.id.localeCompare(b.id)));if(match){const item=state.items.find(i=>i.id===id);return item?send(200,item):send(404,{error:'missing'});}return send(404,{error:'route'});}
   if(!(route==='/items'&&req.method==='POST'||match&&['PATCH','DELETE'].includes(req.method)))return send(404,{error:'route'});
   let body;if(req.method!=='DELETE'){try{body=JSON.parse(Buffer.concat(chunks).toString());validate(body,req.method==='PATCH');}catch{return send(400,{error:'invalid'});}}
   const operation=async()=>{
    const next=structuredClone(state);let response,status;
    if(req.method==='POST'){if(next.items.some(i=>i.id===body.id))return send(409,{error:'duplicate'});response={id:body.id,title:body.title.trim(),done:body.done??false};next.items.push(response);status=201;}
    else{const index=next.items.findIndex(i=>i.id===id);if(index<0)return send(404,{error:'missing'});if(req.method==='DELETE'){next.items.splice(index,1);status=204;}else{response={...next.items[index],...body};if(body.title!==undefined)response.title=body.title.trim();next.items[index]=response;status=200;}}
    next.items.sort((a,b)=>a.id.localeCompare(b.id));fs.writeFileSync(file+'.tmp',JSON.stringify(next));await onStage('beforeCommit');fs.renameSync(file+'.tmp',file);state=next;await onStage('afterCommit');if(status===201)res.setHeader('Location','/items/'+response.id);send(status,response);
   };
   serial=serial.then(operation);await serial;
  }catch{if(!res.headersSent)send(500,{error:'internal'});}
 });
};
`;

const SCHEDULER = String.raw`
'use strict';
const fs=require('node:fs'),path=require('node:path');
exports.createScheduler=({file,concurrency,execute,onStage=()=>{}})=>{
 if(!Number.isInteger(concurrency)||concurrency<1||concurrency>8||typeof execute!=='function')throw Error('options');
 fs.mkdirSync(path.dirname(file),{recursive:true});
 let state=fs.existsSync(file)?JSON.parse(fs.readFileSync(file,'utf8')):{version:1,jobs:[]};
 if(state.version!==1||!Array.isArray(state.jobs))throw Error('bad checkpoint');
 const copy=x=>structuredClone(x),persist=()=>{state.jobs.sort((a,b)=>a.id.localeCompare(b.id));fs.writeFileSync(file+'.tmp',JSON.stringify(state));fs.renameSync(file+'.tmp',file);};
 for(const job of state.jobs)if(job.status==='running')job.status='pending';
 let draining;
 const snapshot=()=>copy(state);
 const enqueue=async batch=>{
  if(!Array.isArray(batch))throw Error('batch');
  const next=copy(state),ids=new Set(next.jobs.map(j=>j.id));
  for(const item of batch){if(!item||typeof item!=='object'||Array.isArray(item)||Object.keys(item).some(k=>!['id','dependsOn','payload'].includes(k))||typeof item.id!=='string'||!(/^[a-z][a-z0-9-]{0,31}$/).test(item.id)||ids.has(item.id))throw Error('id');ids.add(item.id);next.jobs.push({id:item.id,dependsOn:item.dependsOn===undefined?[]:copy(item.dependsOn),payload:item.payload===undefined?null:copy(item.payload),status:'pending',attempt:0});}
  for(const job of next.jobs){if(!Array.isArray(job.dependsOn)||new Set(job.dependsOn).size!==job.dependsOn.length||job.dependsOn.some(dep=>typeof dep!=='string'||!ids.has(dep)||dep===job.id))throw Error('dependencies');}
  const visiting=new Set(),visited=new Set(),map=new Map(next.jobs.map(j=>[j.id,j]));
  const visit=id=>{if(visiting.has(id))throw Error('cycle');if(visited.has(id))return;visiting.add(id);for(const dep of map.get(id).dependsOn)visit(dep);visiting.delete(id);visited.add(id);};
  for(const id of ids)visit(id);state=next;persist();return snapshot();
 };
 const run=()=>{
  if(draining)return draining;
  draining=(async()=>{
   const active=new Map();
   while(true){
    let changed=true;while(changed){changed=false;for(const job of state.jobs)if(job.status==='pending'&&job.dependsOn.some(id=>['failed','blocked'].includes(state.jobs.find(j=>j.id===id).status))){job.status='blocked';persist();changed=true;}}
    const ready=state.jobs.filter(j=>j.status==='pending'&&j.dependsOn.every(id=>state.jobs.find(d=>d.id===id).status==='completed'));
    while(active.size<concurrency&&ready.length){
     const job=ready.shift();job.status='running';job.attempt++;persist();
     const work=(async()=>{await onStage('claimed',job.id);try{job.result=await execute({id:job.id,payload:copy(job.payload),attempt:job.attempt});job.status='completed';}catch(error){job.status='failed';job.error=String(error.message||error);}persist();if(job.status==='completed')await onStage('completed',job.id);})().finally(()=>active.delete(job.id));active.set(job.id,work);
    }
    if(!active.size)break;await Promise.race(active.values());
   }
   return snapshot();
  })().finally(()=>{draining=undefined;});return draining;
 };
 return{enqueue,run,snapshot};
};
`;

function temporary(t) { const root = fs.mkdtempSync(path.join(os.tmpdir(), 'planwharf-task-grader-test-')); t.after(() => fs.rmSync(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 })); return root; }
function output(root, id, source) {
  const workspace = path.join(root, id); setupTask(id, workspace);
  fs.writeFileSync(path.join(workspace, listTasks().find(item => item.id === id).module), source);
  fs.writeFileSync(path.join(workspace, 'README.md'), '# Fixture\n\nThis oracle documents its public CommonJS API and validates inputs. Run node --test test.cjs for self-tests. Persistence uses atomic checkpoints; side effects are at-least-once after interrupted execution.');
  fs.writeFileSync(path.join(workspace, 'test.cjs'), "const test=require('node:test');const assert=require('node:assert/strict');test('module exports exist',()=>assert.ok(Object.keys(require('./" + listTasks().find(item => item.id === id).module + "')).length));\n");
  return workspace;
}

test('specifications and deterministic starter bytes are identical across arms', t => {
  const root = temporary(t), tasks = listTasks(); assert.equal(tasks.length, 3);
  for (const item of tasks) {
    const a = setupTask(item.id, path.join(root, item.id + '-a')), b = setupTask(item.id, path.join(root, item.id + '-b'));
    assert.equal(a.starterHash, b.starterHash); assert.equal(a.prompt, specification(item.id)); assert.equal(a.prompt, b.prompt);
    for (const name of a.files) assert.deepEqual(fs.readFileSync(path.join(a.workspace, name)), fs.readFileSync(path.join(b.workspace, name)));
    assert.equal(fs.existsSync(path.join(a.workspace, 'tasks.cjs')), false, 'Private grader is absent from model workspace');
    assert.throws(() => setupTask(item.id, a.workspace), /empty/);
  }
  assert.throws(() => specification('missing'), /Unknown/);
});

test('known-correct config oracle is independently accepted; coercion mutant fails', async t => {
  const root = temporary(t), workspace = output(root, 'config', CONFIG);
  const good = await evaluateTask('config', workspace, { evidenceDir: path.join(root, 'good') });
  assert.equal(good.passed, good.total, JSON.stringify(good.checks)); assert.equal(good.outcome, 'independently-accepted');
  fs.writeFileSync(path.join(workspace, 'config.cjs'), CONFIG.replace('const out={port:', 'if(typeof input.port===\'string\')input.port=Number(input.port);const out={port:'));
  const bad = await evaluateTask('config', workspace, { evidenceDir: path.join(root, 'bad') });
  assert.equal(bad.checks.find(check => check.name === 'invalid-values').passed, false); assert.equal(bad.outcome, 'completed-with-defects');
});

test('known-correct REST oracle passes real process restart and commit-stage crashes', async t => {
  const root = temporary(t), workspace = output(root, 'rest-api', API);
  const result = await evaluateTask('rest-api', workspace, { evidenceDir: path.join(root, 'good') });
  assert.equal(result.passed, result.total, JSON.stringify(result.checks));
  fs.writeFileSync(path.join(workspace, 'api.cjs'), API.replace("await onStage('beforeCommit');", ''));
  const mutant = await evaluateTask('rest-api', workspace, { evidenceDir: path.join(root, 'mutant') });
  assert.equal(mutant.checks.find(check => check.name === 'before-commit-crash').passed, false, 'Missing stage notification is a defect, not simulated recovery success');
});

test('known-correct scheduler oracle passes graph, durable claims, crash and recovery invariants', async t => {
  const root = temporary(t), workspace = output(root, 'scheduler', SCHEDULER);
  const result = await evaluateTask('scheduler', workspace, { evidenceDir: path.join(root, 'good') });
  assert.equal(result.passed, result.total, JSON.stringify(result.checks));
  fs.writeFileSync(path.join(workspace, 'scheduler.cjs'), SCHEDULER.replace("if(job.status==='running')job.status='pending';", "if(job.status==='running')job.status='completed';"));
  const mutant = await evaluateTask('scheduler', workspace, { evidenceDir: path.join(root, 'mutant') });
  assert.equal(mutant.checks.find(check => check.name === 'claimed-crash').passed, false, 'Falsely completed interrupted work fails acceptance');
});

test('empty starters, missing exports and wrong partial reports never count as accepted', async t => {
  const root = temporary(t);
  for (const { id, module: filename } of listTasks()) {
    const workspace = path.join(root, id); setupTask(id, workspace);
    const report = await evaluateTask(id, workspace, { evidenceDir: path.join(root, id + '-evidence') });
    assert.equal(report.passed, 0); assert.equal(report.outcome, 'failed'); assert.ok(report.checks.every(check => !check.passed));
    fs.writeFileSync(path.join(workspace, filename), id === 'scheduler' ? 'exports.createScheduler=()=>({enqueue:async()=>{},run:async()=>({version:1,jobs:[]}),snapshot:()=>({version:1,jobs:[]})});' : id === 'config' ? 'exports.parseConfig=()=>({port:3000,host:"127.0.0.1",retries:2,logLevel:"info"});' : 'exports.createServer=()=>({});');
    const partial = await evaluateTask(id, workspace, { evidenceDir: path.join(root, id + '-partial') });
    assert.ok(partial.passed < partial.total); assert.notEqual(partial.outcome, 'independently-accepted');
  }
});

test('grader rejects in-workspace evidence and invalid execution bounds', async t => {
  const root = temporary(t), workspace = path.join(root, 'config'); setupTask('config', workspace);
  await assert.rejects(evaluateTask('config', workspace, { evidenceDir: path.join(workspace, 'evidence') }), /outside/);
  await assert.rejects(evaluateTask('config', workspace, { timeoutMs: 0 }), /timeoutMs/);
});


test('REST acceptance permits unsorted durable items while HTTP lists stay sorted', async t => {
  const root = temporary(t);
  const source = API.replace("next.items.sort((a,b)=>a.id.localeCompare(b.id));", '');
  assert.notEqual(source, API, 'Fixture removes only non-contract disk sorting');
  const workspace = output(root, 'rest-api', source);
  const report = await evaluateTask('rest-api', workspace, { evidenceDir: path.join(root, 'unsorted-store') });
  assert.equal(report.passed, report.total, JSON.stringify(report.checks));
});


test('repaired historical REST fixture handles duplicate rejections and validates corrupt stores synchronously', async t => {
  const root = temporary(t);
  const source = fs.readFileSync(path.join(__dirname, 'regressions/rest-pilot-fixed.cjs'), 'utf8');
  const workspace = output(root, 'rest-api', source);
  const report = await evaluateTask('rest-api', workspace, { evidenceDir: path.join(root, 'repaired-pilot') });
  assert.equal(report.passed, report.total, JSON.stringify(report.checks));
});
