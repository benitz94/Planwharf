'use strict';
// Provider-reported response receipts survive interrupted CLI turns. Never infer
// missing usage from elapsed time, exit status, or subscription quota.
const fs=require('node:fs'),path=require('node:path');
const fields={inputTokens:'input_tokens',outputTokens:'output_tokens',cachedInputTokens:'cached_input_tokens',reasoningTokens:'reasoning_output_tokens'};
const valid=n=>Number.isSafeInteger(n)&&n>=0;
function sum(records){if(!records.length)return null;return Object.fromEntries(Object.keys(fields).map(k=>[k,records.every(r=>valid(r.usage?.[k]))?records.reduce((n,r)=>n+r.usage[k],0):null]));}
function parseReceipt(events,sessionId){
  const records=[],seen=new Map();let conflict=false;
  for(const e of events){if(e.type!=='token_usage_record')continue;const p=e.payload;
    if(p?.session_id!==sessionId||p.thread_id!==sessionId||typeof p.response_id!=='string')continue;
    const usage=Object.fromEntries(Object.entries(fields).map(([k,w])=>[k,valid(p.usage?.[w])?p.usage[w]:null]));
    if(usage.inputTokens===null||usage.outputTokens===null){conflict=true;continue;}
    const previous=seen.get(p.response_id);if(previous){if(JSON.stringify(previous.usage)!==JSON.stringify(usage))conflict=true;continue;}
    const record={responseId:p.response_id,turnId:p.turn_id||null,at:e.timestamp||null,usage};seen.set(p.response_id,record);records.push(record);
  }
  return {sessionId,source:'native provider token_usage_record',records,responseCount:records.length,usage:conflict?null:sum(records),valid:!conflict&&records.length>0,
    unreportedProviderUsage:null,scope:'Provider-reported completed responses only. Unreported in-flight usage remains UNKNOWN; no estimate or billed-cost claim.'};
}
function readReceipt(sessionId,profile){
  if(!/^[a-f0-9-]{36}$/i.test(sessionId||''))return null;
  let found=null;const root=path.join(profile,'sessions');
  function walk(d){let items;try{items=fs.readdirSync(d,{withFileTypes:true});}catch{return;}for(const e of items){if(e.isSymbolicLink())continue;const p=path.join(d,e.name);if(e.isDirectory())walk(p);else if(e.name.endsWith(sessionId+'.jsonl'))found=p;}}
  walk(root);if(!found)return null;let raw;try{raw=fs.readFileSync(found,'utf8');}catch{return null;}
  const events=raw.split('\n').flatMap(line=>{try{return [JSON.parse(line)];}catch{return [];}});return parseReceipt(events,sessionId);
}
function allocate(calls){
  const seen=new Map(),sessions=new Set();
  return calls.map(call=>{
    const receipt=call.responseReceipt,id=call.sessionId||call.threadId;let usage=null,newRecords=[];
    if(receipt?.valid){for(const r of receipt.records){const prior=seen.get(r.responseId);if(prior){if(JSON.stringify(prior.usage)!==JSON.stringify(r.usage))return {...call,usage:null,accounting:'UNKNOWN: conflicting response receipts'};continue;}seen.set(r.responseId,r);newRecords.push(r);}usage=sum(newRecords);}
    else if(!receipt&&call.usage&&(!id||!sessions.has(id)))usage=call.usage;
    if(id)sessions.add(id);
    return {...call,usage,responseReceipt:receipt||null,newResponseIds:newRecords.map(r=>r.responseId),accounting:usage?'provider-reported response usage; duplicate response IDs counted once':'UNKNOWN: missing, invalid or unreported response usage'};
  });
}
module.exports={parseReceipt,readReceipt,allocate};
