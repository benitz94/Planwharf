'use strict';
const test=require('node:test'),assert=require('node:assert/strict');
const {assess}=require('./preflight.cjs');
const edit={type:'item.completed',item:{type:'file_change',status:'completed'}};
const command={type:'item.completed',item:{type:'command_execution',command:'node -e check',status:'completed',exit_code:0}};
const good=()=>({transport:{completed:true},events:[edit,command],verification:{nonce:'fixture',passed:true},nonce:'fixture',diagnostic:''});
test('capability gate requires observed native editing, successful Node execution and matching independent receipt',()=>{
  assert.equal(assess(good()).status,'passed');
  for(const partial of [{events:[]},{events:[edit]},{events:[command]},{verification:null},{verification:{nonce:'wrong',passed:true}},{transport:{completed:false}}])assert.equal(assess({...good(),...partial}).status,'blocked');
  assert.equal(assess({...good(),events:[edit,{...command,item:{...command.item,exit_code:-1,status:'failed'}}]}).status,'blocked');
});
test('successful model narration never masks read-only policy or Windows provisioning failure',()=>{
  for(const [diagnostic,reason] of [['Failed to create unified exec process: open sandbox provisioning pipe','Windows sandbox provisioning pipe unavailable'],['patch rejected: writing is blocked by read-only sandbox','Effective CLI filesystem policy is read-only']]){
    const result=assess({...good(),events:[],verification:null,diagnostic});assert.equal(result.status,'blocked');assert.equal(result.reason,reason);
  }
  assert.equal(assess({...good(),transport:{completed:false,timedOut:true}}).reason,'Capability check timed out');
});
