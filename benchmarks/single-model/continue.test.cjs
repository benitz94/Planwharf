'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path'),crypto=require('node:crypto');
const {remaining,loadFrozen}=require('./continue.cjs'),{schedule}=require('./run.cjs');
const checkpoint=require('./evidence/full-paused-2026-10-21/runs.json');
test('published twenty slots are excluded and all ten scheduler slots retain arm order',()=>{
 const before=JSON.stringify(checkpoint),slots=remaining(schedule(5),checkpoint);
 assert.equal(slots.length,10);assert.ok(slots.every(s=>s.taskId==='scheduler'));
 assert.deepEqual(slots.slice(0,4).map(s=>s.arm),['direct','planwharf','planwharf','direct']);
 assert.equal(new Set(slots.map(s=>s.pairId+'/'+s.arm)).size,10);assert.equal(JSON.stringify(checkpoint),before);
});
test('duplicate, unknown, or mismatched checkpoints are rejected before dispatch',()=>{
 for(const rows of [[...checkpoint,checkpoint[0]],[{...checkpoint[0],pairId:'unknown'}],[{...checkpoint[0],taskId:'scheduler'}],[{...checkpoint[0],scenario:'unexpected'}]])assert.throws(()=>remaining(schedule(5),rows));
});
test('frozen loader only exposes functions and rejects changed bytes',()=>{
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'continuation-loader-'));
 try{const source='function direct(){}; function planwharf(){}; module.exports={}; if(require.main===module)throw Error("CLI executed");';
 fs.writeFileSync(path.join(root,'run.cjs'),source);const hashes={'run.cjs':crypto.createHash('sha256').update(source).digest('hex')};
 assert.equal(typeof loadFrozen(root,hashes).trialFunctions.direct,'function');assert.equal(fs.readFileSync(path.join(root,'run.cjs'),'utf8'),source);
 fs.appendFileSync(path.join(root,'run.cjs'),' ');assert.throws(()=>loadFrozen(root,hashes),/Frozen source differs/);
 }finally{fs.rmSync(root,{recursive:true,force:true});}
});
