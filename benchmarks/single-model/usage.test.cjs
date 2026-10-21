'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const {parseReceipt,allocate}=require('./usage.cjs');
const id='12345678-1234-1234-1234-123456789abc';
const event=(response,input,output)=>({type:'token_usage_record',payload:{session_id:id,thread_id:id,response_id:response,usage:{input_tokens:input,output_tokens:output,cached_input_tokens:0,reasoning_output_tokens:0}}});
test('interrupted response receipts are retained and cumulative resume receipts are deduplicated',()=>{
 const first=parseReceipt([event('a',10,2)],id),resumed=parseReceipt([event('a',10,2),event('b',30,4)],id);
 const calls=allocate([{exitCode:1,threadId:id,responseReceipt:first},{exitCode:0,threadId:id,responseReceipt:resumed}]);
 assert.deepEqual(calls.map(c=>c.usage.inputTokens),[10,30]);assert.deepEqual(calls.map(c=>c.usage.outputTokens),[2,4]);
 assert.deepEqual(calls.map(c=>c.newResponseIds),[['a'],['b']]);assert.equal(calls[0].responseReceipt.unreportedProviderUsage,null);
});
test('missing receipts, conflicting duplicates and mismatched sessions never become zero usage',()=>{
 assert.equal(parseReceipt([],id).valid,false);
 assert.equal(parseReceipt([event('a',1,1),event('a',2,1)],id).valid,false);
 assert.equal(parseReceipt([event('a',1,1)],'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa').valid,false);
 assert.equal(allocate([{responseReceipt:parseReceipt([],id),usage:{inputTokens:99}}])[0].usage,null);
 const duplicate=parseReceipt([event('a',1,1),event('a',1,1)],id);assert.equal(duplicate.responseCount,1);
});
test('separate Planwharf recovery sessions contribute both interrupted and completed usage',()=>{
 const other='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';let e=event('b',20,3);e.payload.session_id=other;e.payload.thread_id=other;
 const calls=allocate([{exitCode:null,sessionId:id,responseReceipt:parseReceipt([event('a',10,2)],id)},{exitCode:0,sessionId:other,responseReceipt:parseReceipt([e],other)}]);
 assert.equal(calls.reduce((n,c)=>n+c.usage.inputTokens,0),30);assert.equal(calls.reduce((n,c)=>n+c.usage.outputTokens,0),5);
});
