'use strict';
const test=require('node:test'),assert=require('node:assert/strict');const {parseReceipt,allocate}=require('./usage.cjs');const {auditLedger}=require('./audit.cjs');
const id='12345678-1234-1234-1234-123456789abc';
const raw=(response,n)=>({type:'token_usage_record',payload:{session_id:id,thread_id:id,response_id:response,usage:{input_tokens:n,output_tokens:1,cached_input_tokens:0,reasoning_output_tokens:0}}});
test('independent metering audit rejects duplicate resume allocation and missing native cumulative responses',()=>{
 const one=raw('a',10),two=raw('b',20);one.payload.thread_token_usage={input_tokens:10,output_tokens:1,cached_input_tokens:0,reasoning_output_tokens:0};two.payload.thread_token_usage={input_tokens:30,output_tokens:2,cached_input_tokens:0,reasoning_output_tokens:0};
 const calls=allocate([{responseReceipt:parseReceipt([one],id)},{responseReceipt:parseReceipt([one,two],id)}]);const native=new Map([['a',one.payload],['b',two.payload]]);
 assert.equal(auditLedger(calls,native).valid,true);
 const duplicate=structuredClone(calls);duplicate[1].usage.inputTokens=30;assert.equal(auditLedger(duplicate,native).valid,false);
 const missing=structuredClone(calls);missing[1].responseReceipt.records.shift();assert.equal(auditLedger(missing,native).valid,false);
 assert.equal(auditLedger([{usage:null}],native).unknownCalls,1);
});


test('trial summary cannot disagree with its retained numeric ledger', t=>{
 const fs=require('node:fs'),path=require('node:path'),os=require('node:os');const {auditStudy}=require('./audit.cjs');
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'pw-audit-'));
 t.after(()=>{assert.ok(path.resolve(root).startsWith(path.resolve(os.tmpdir())+path.sep+'pw-audit-'));fs.rmSync(root,{recursive:true,force:true});});
 const study=path.join(root,'study'),profile=path.join(root,'profile');fs.mkdirSync(path.join(study,'private','trial'),{recursive:true});fs.mkdirSync(path.join(profile,'sessions'),{recursive:true});
 const e=raw('a',10);e.payload.thread_token_usage={input_tokens:10,output_tokens:1,cached_input_tokens:0,reasoning_output_tokens:0};const calls=allocate([{responseReceipt:parseReceipt([e],id)}]);
 fs.writeFileSync(path.join(profile,'sessions','rollout-'+id+'.jsonl'),JSON.stringify(e)+'\n');fs.writeFileSync(path.join(study,'private','trial','usage-ledger.json'),JSON.stringify(calls));
 const run={id:'trial',invocations:1,calls:[{usage:calls[0].usage}]};fs.writeFileSync(path.join(study,'runs.json'),JSON.stringify([run]));assert.equal(auditStudy(study,profile).valid,true);
 run.calls[0].usage={...run.calls[0].usage,inputTokens:999};fs.writeFileSync(path.join(study,'runs.json'),JSON.stringify([run]));assert.equal(auditStudy(study,profile).valid,false);
});
