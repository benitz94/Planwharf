'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),os=require('node:os'),crypto=require('node:crypto');
const {validate}=require('./finalize-office.cjs'),{remaining}=require('./continue.cjs'),{schedule}=require('./run.cjs');
test('final publication requires the ten unique pending slots, unchanged checkpoint and finished runner',()=>{
 const checkpoint=path.join(__dirname,'evidence/full-paused-2026-10-21'),bytes=fs.readFileSync(path.join(checkpoint,'runs.json')),root=fs.mkdtempSync(path.join(os.tmpdir(),'office-publication-'));
 try{const manifest={checkpoint:{runsSha256:crypto.createHash('sha256').update(bytes).digest('hex')},options:{output:root},cohort:'office-test'};
 const office=remaining(schedule(5),JSON.parse(bytes)).map(s=>({...s,cohort:'office-test'}));
 fs.writeFileSync(path.join(root,'progress.json'),JSON.stringify({completed:10,current:null,finishedAt:'2026-10-21T00:00:00Z'}));
 assert.equal(validate(checkpoint,manifest,office).length,20);
 assert.throws(()=>validate(checkpoint,manifest,office.slice(1)),/incomplete/);
 assert.throws(()=>validate(checkpoint,manifest,[...office.slice(1),office[1]]),/duplicate/);
 assert.throws(()=>validate(checkpoint,{...manifest,checkpoint:{runsSha256:'wrong'}},office),/bytes changed/);
 fs.writeFileSync(path.join(root,'progress.json'),JSON.stringify({completed:9,current:office[9].pairId}));
 assert.throws(()=>validate(checkpoint,manifest,office),/did not finish/);
 }finally{fs.rmSync(root,{recursive:true,force:true});}
});
