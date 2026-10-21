'use strict';
// Independent post-run metering audit. Reads numeric native receipts only.
const fs=require('node:fs'),path=require('node:path');
const keys=['inputTokens','outputTokens','cachedInputTokens','reasoningTokens'];
const wire={inputTokens:'input_tokens',outputTokens:'output_tokens',cachedInputTokens:'cached_input_tokens',reasoningTokens:'reasoning_output_tokens'};
function auditLedger(calls,native){
 const seen=new Set(),issues=[],rows=[];
 for(const [index,call] of calls.entries()){
  const receipt=call.responseReceipt;
  if(!receipt?.valid){rows.push({call:index,availability:'UNKNOWN',reason:'No valid provider response receipt'});continue;}
  const records=receipt.records,latest=records.at(-1),source=native.get(latest.responseId);
  const cumulative=source?.thread_token_usage;
  const totals=Object.fromEntries(keys.map(k=>[k,records.every(r=>Number.isSafeInteger(r.usage?.[k]))?records.reduce((n,r)=>n+r.usage[k],0):null]));
  const fresh=records.filter(r=>!seen.has(r.responseId));for(const r of records)seen.add(r.responseId);
  const delta=Object.fromEntries(keys.map(k=>[k,fresh.every(r=>Number.isSafeInteger(r.usage?.[k]))?fresh.reduce((n,r)=>n+r.usage[k],0):null]));
  const errors=[];
  for(const k of keys){if(totals[k]!==null&&totals[k]!==cumulative?.[wire[k]])errors.push(k+': snapshot differs from native cumulative receipt');if(call.usage?.[k]!==delta[k])errors.push(k+': allocated usage differs from unique responses');}
  for(const r of records){const raw=native.get(r.responseId);if(!raw||keys.some(k=>r.usage[k]!== (Number.isSafeInteger(raw.usage?.[wire[k]])?raw.usage[wire[k]]:null)))errors.push('Response receipt mismatch: '+r.responseId);}
  issues.push(...errors.map(error=>({call:index,error})));rows.push({call:index,sessionId:receipt.sessionId,responseCount:records.length,newResponses:fresh.length,availability:errors.length?'INVALID':'MEASURED',cumulative,allocated:call.usage,errors});
 }
 return {valid:issues.length===0,knownCalls:rows.filter(r=>r.availability==='MEASURED').length,unknownCalls:rows.filter(r=>r.availability==='UNKNOWN').length,issues,rows};
}
function auditStudy(study,profile){
 const native=new Map();const ids=new Set();const runs=JSON.parse(fs.readFileSync(path.join(study,'runs.json')));
 const ledgers=runs.map(run=>{const calls=JSON.parse(fs.readFileSync(path.join(study,'private',run.id,'usage-ledger.json')));for(const c of calls)if(c.responseReceipt?.sessionId)ids.add(c.responseReceipt.sessionId);return {id:run.id,calls,expectedCalls:run.calls,invocations:run.invocations};});
 function walk(dir){for(const e of fs.readdirSync(dir,{withFileTypes:true})){if(e.isSymbolicLink())continue;const p=path.join(dir,e.name);if(e.isDirectory())walk(p);else if([...ids].some(id=>e.name.endsWith(id+'.jsonl'))){for(const line of fs.readFileSync(p,'utf8').split('\n'))try{const e=JSON.parse(line);if(e.type==='token_usage_record'&&ids.has(e.payload?.session_id))native.set(e.payload.response_id,e.payload);}catch{}}}}
 walk(path.join(profile,'sessions'));const trials=ledgers.map(l=>{const result=auditLedger(l.calls,native);
   if(l.calls.length!==l.invocations||l.expectedCalls.length!==l.calls.length)result.issues.push({error:'Trial launch count differs from retained ledger'});
   for(let i=0;i<l.calls.length;i++)if(JSON.stringify(l.calls[i].usage)!==JSON.stringify(l.expectedCalls[i]?.usage))result.issues.push({call:i,error:'Trial summary usage differs from retained ledger'});
   result.valid=result.issues.length===0;return {id:l.id,...result};});
 return {scope:'Independent native cumulative/unique-response audit. Unreported provider usage and billed cost remain UNKNOWN.',valid:trials.every(r=>r.valid),trialsChecked:trials.length,knownCalls:trials.reduce((n,r)=>n+r.knownCalls,0),unknownCalls:trials.reduce((n,r)=>n+r.unknownCalls,0),trials};
}
module.exports={auditLedger,auditStudy};
if(require.main===module){const [study,profile]=process.argv.slice(2);if(!study||!profile)throw Error('Usage: node audit.cjs <private-study> <private-profile>');const result=auditStudy(path.resolve(study),path.resolve(profile));fs.writeFileSync(path.join(study,'usage-audit.json'),JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify({valid:result.valid,trialsChecked:result.trialsChecked,knownCalls:result.knownCalls,unknownCalls:result.unknownCalls}));if(!result.valid)process.exitCode=1;}
