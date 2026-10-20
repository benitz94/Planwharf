'use strict';
// Opt-in audit controller. Run only against an isolated notebook created for this audit.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const stage=process.argv[2],out=path.resolve(process.argv[3]||'');
if(!process.argv[3])throw Error('Usage: node scripts/audit/dogfood.cjs STAGE DISPOSABLE_EVIDENCE_DIRECTORY');
const {url}=JSON.parse(fs.readFileSync(path.join(out,'server.json'))),model='audit-fixture:calculator';
const delay=ms=>new Promise(r=>setTimeout(r,ms));
function record(name,data){fs.appendFileSync(path.join(out,'audit-events.jsonl'),JSON.stringify({at:new Date().toISOString(),name,...data})+'\n');console.log(name,JSON.stringify(data));}
async function api(route,input,expected){const r=await fetch(url+route,input===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(input)});const data=await r.json();fs.appendFileSync(path.join(out,'audit-http.jsonl'),JSON.stringify({at:new Date().toISOString(),route,input,status:r.status,data})+'\n');if(expected!==undefined)assert.equal(r.status,expected,JSON.stringify({error:data.error,reason:data.reason}));else if(!r.ok)throw Error(route+': '+JSON.stringify({error:data.error,reason:data.reason}));return data;}
async function snapshot(){return api('/api/orchestrator');}
async function wait(project,predicate,limit=25000){const deadline=Date.now()+limit;let row;do{const s=await snapshot();row=s.projects.find(p=>p.id===project);if(predicate(row,s))return {row,s};await delay(150);}while(Date.now()<deadline);throw Error('Timed out waiting for '+project+': '+JSON.stringify(row));}
async function create(name,direction='Disposable controlled process audit only.'){const folder=path.join(out,name);fs.mkdirSync(folder,{recursive:true});const p=await api('/api/plan');const r=await api('/api/projects',{version:p.version,project:{name,direction,folder}},201);return r.plan.projects.at(-1).project;}
async function propose(project,scenario,type='IMPLEMENTATION',subs){const r=await api('/api/orchestrator/propose',{projectId:project,title:'Audit '+scenario,goal:'Explicit deterministic CLI audit: '+scenario,subTasks:subs||[{title:'Audit '+scenario,detail:'AUDIT_SCENARIO='+scenario,assignedModelId:model,type,dependsOn:[]}]},201);return r.snapshot.projects.find(p=>p.id===project);}
async function run(project,scenario,type='IMPLEMENTATION'){const proposed=await propose(project,scenario,type);await api('/api/orchestrator/approve',{projectId:project,megaTaskId:proposed.megaTaskId});const result=await wait(project,r=>['complete','failed','checkpointed'].includes(r.status));record(scenario,{project,status:result.row.status,megaTaskId:result.row.megaTaskId});return result;}
async function approve(project){const s=await snapshot(),r=s.projects.find(p=>p.id===project);await api('/api/orchestrator/approve',{projectId:project,megaTaskId:r.megaTaskId});return wait(project,r=>['complete','failed','checkpointed'].includes(r.status));}
async function main(){
  const app=await api('/api/app');assert.equal(path.resolve(app.planFile),path.join(out,'notebook.json'),'Refusing a non-audit notebook');
  const calculator=JSON.parse(fs.readFileSync(path.join(out,'calculator-project.json')));
  if(stage==='setup'){
    for(const role of ['orchestrator','worker','reviewer','supervisor'])await api('/api/models/roles/bulk',{role,enabled:false});
    await api('/api/providers/connections',{id:'audit-fixture',label:'AUDIT deterministic CLI (not AI)',adapter:'configured-cli',cliCommand:process.execPath,cliArgs:[path.join(__dirname,'dogfood-fixture.cjs'),'--model','{model}'],restrictedArgs:[],yoloArgs:[],outputProtocol:'codex-jsonl',modelIds:['calculator'],toolsEnabled:true});
    await api('/api/models/roles',{id:model,roles:{worker:true,reviewer:true,orchestrator:true,supervisor:false}});
    await api('/api/orchestrator/lead',{id:model});
    const skillFile=path.join(calculator.folder,'.planwharf/skills/audit-scope/SKILL.md');fs.mkdirSync(path.dirname(skillFile),{recursive:true});fs.writeFileSync(skillFile,'---\nname: audit-scope\ndescription: Keep this disposable audit isolated\n---\nAUDIT-SKILL-DELIVERED-20261020\nKeep all generated calculator files in the linked project. Report actual test outcomes.\n');
    const found=await api('/api/project-skills/discover',{projectId:calculator.id});const skill=found.catalog.find(s=>s.source==='project-local'&&s.name==='audit-scope');assert.ok(skill);
    await api('/api/project-skills/approve',{projectId:calculator.id,id:skill.id,enabled:true,contentHash:skill.contentHash});
    await api('/api/project-skills/settings',{projectId:calculator.id,settings:{deliveryEnabled:true,mode:'manual',selected:[skill.id]}});
    const request='Create a complete, locally executable calculator application. It must support addition, subtraction, multiplication, division, decimals, keyboard input, clear/reset, division-by-zero handling, automated tests, and a README explaining how to run it. Keep the application lightweight, usable and independent of paid external services.';
    const response=await api('/api/orchestrator/ask',{projectId:calculator.id,question:request});record('simulated-natural-request',{answer:response.answer});
    const s=await snapshot();record('after-natural-request',{projects:s.projects.map(p=>({id:p.id,status:p.status,megaTaskId:p.megaTaskId}))});
  }else if(stage==='build'){
    let s=await snapshot(),row=s.projects.find(p=>p.id===calculator.id);
    if(!row.megaTaskId){await api('/api/orchestrator/grill',{projectId:calculator.id,skip:true});const p=await api('/api/plan');await api('/api/orchestrator/start',{projectId:calculator.id,version:p.version,timing:'discretion'});}
    assert.equal(fs.existsSync(path.join(calculator.folder,'calculator.js')),false,'No calculator before approval');
    const done=await approve(calculator.id);record('calculator-build',{status:done.row.status,files:fs.readdirSync(calculator.folder)});
    assert.equal(done.row.status,'complete');
    const skills=await api('/api/project-skills?projectId='+calculator.id);const receipts=fs.readFileSync(path.join(calculator.folder,'fixture-receipts.jsonl'),'utf8').trim().split('\n').map(JSON.parse);
    record('skill-delivery',{received:receipts.some(r=>r.skillMarkerReceived),deliveries:skills.deliveries.map(d=>({role:d.role,submitted:d.submitted,loaded:d.loaded,followed:d.followed}))});
  }else if(stage==='review'||stage==='recover-review'){
    if(stage==='review'){
    const file=path.join(calculator.folder,'calculator.js'),pristine=fs.readFileSync(file,'utf8');assert.ok(pristine.includes("if(b===0)throw Error('Division by zero')"));
    fs.writeFileSync(file,pristine.replace("if(b===0)throw Error('Division by zero')","if(b===0)return 0"));record('deliberate-defect',{description:'Replaced division-by-zero exception with a wrong zero result in the disposable generated calculator.'});
    const rejected=await run(calculator.id,'review','AUDIT');assert.equal(rejected.row.status,'failed');
    }
    await api('/api/orchestrator/ask',{projectId:calculator.id,question:'Fix the calculator division-by-zero defect, run its tests, and wait for separate review. AUDIT_SCENARIO=correct'});
    const correction=await approve(calculator.id);record('correct',{status:correction.row.status});assert.equal(correction.row.status,'complete');
    const accepted=await run(calculator.id,'review','AUDIT');assert.equal(accepted.row.status,'complete');
  }else if(stage==='faults'){
    const results=[];
    for(const scenario of ['nonzero','malformed','vanish']){const p=await create('Audit-'+scenario);const r=await run(p.id,scenario);results.push({scenario,status:r.row.status});assert.notEqual(r.row.status,'complete');}
    const p=await create('Audit-hang');const row=await propose(p.id,'hang');await api('/api/orchestrator/approve',{projectId:p.id,megaTaskId:row.megaTaskId});const running=await wait(p.id,r=>r.dispatch?.some(d=>d.alive));
    const pids=running.row.dispatch.filter(d=>d.alive).map(d=>d.pid);await api('/api/orchestrator/stop',{projectId:p.id,megaTaskId:row.megaTaskId});const stopped=await wait(p.id,r=>r.status==='checkpointed');
    const alive=pids.filter(pid=>{try{process.kill(pid,0);return true;}catch{return false;}});record('hang-stop',{status:stopped.row.status,ownedPids:pids,stillAlive:alive});assert.equal(alive.length,0);
    fs.writeFileSync(path.join(out,'fault-summary.json'),JSON.stringify(results,null,2));
  }else if(stage==='concurrency'){
    const projects=[];for(let i=1;i<=3;i++)projects.push(await create('Audit-Concurrent-'+i));
    for(const project of projects){const r=await propose(project.id,'wait-for-resume');await api('/api/orchestrator/approve',{projectId:project.id,megaTaskId:r.megaTaskId});}
    const s=(await wait(projects[2].id,(_,s)=>projects.every(p=>s.projects.find(r=>r.id===p.id)?.dispatch?.some(d=>d.alive)))).s;
    fs.writeFileSync(path.join(out,'concurrent-projects.json'),JSON.stringify(projects,null,2));
    record('three-real-process-projects',{projects:projects.map(p=>({id:p.id,live:s.projects.find(r=>r.id===p.id).dispatch.filter(d=>d.alive).map(d=>({pid:d.pid,folder:d.folder}))}))});
  }else if(stage==='checkpoint'){
    const projects=JSON.parse(fs.readFileSync(path.join(out,'concurrent-projects.json')));
    for(const project of projects){const r=(await snapshot()).projects.find(p=>p.id===project.id);await api('/api/orchestrator/stop',{projectId:project.id,megaTaskId:r.megaTaskId});}
    const p=await create('Audit-Dependencies');const row=await propose(p.id,'dependencies','IMPLEMENTATION',[{title:'First receipt',detail:'AUDIT_SCENARIO=receipt',type:'IMPLEMENTATION',assignedModelId:model,dependsOn:[]},{title:'Wait for checkpoint',detail:'AUDIT_SCENARIO=wait-for-resume',type:'IMPLEMENTATION',assignedModelId:model,dependsOn:[0]}]);
    await api('/api/orchestrator/approve',{projectId:p.id,megaTaskId:row.megaTaskId});await wait(p.id,r=>r.subTasks?.[0]?.status==='COMPLETE'&&r.subTasks?.[1]?.status==='RUNNING');await api('/api/orchestrator/stop',{projectId:p.id,megaTaskId:row.megaTaskId});
    fs.writeFileSync(path.join(out,'resume-project.json'),JSON.stringify(p,null,2));record('checkpoint-before-restart',{project:p.id});
  }else if(stage==='resume'){
    const p=JSON.parse(fs.readFileSync(path.join(out,'resume-project.json'))),before=(await snapshot()).projects.find(r=>r.id===p.id);fs.writeFileSync(path.join(p.folder,'resume-allowed'),'Approved deterministic resumption.');
    const preview=await api('/api/execution?projectId='+p.id);fs.writeFileSync(path.join(out,'resume-before.json'),JSON.stringify(preview,null,2));
    const unconfirmed=await api('/api/orchestrator/resume',{projectId:p.id,megaTaskId:before.megaTaskId,checkpointId:before.checkpoint?.id},409);assert.equal(unconfirmed.requiresConfirmation,true);record('resume-confirmation-required',{reason:unconfirmed.reason});
    const response=await api('/api/orchestrator/resume',{projectId:p.id,megaTaskId:before.megaTaskId,checkpointId:before.checkpoint?.id,confirmChanges:true});record('resume-response',{ok:response.ok,reason:response.reason});
    const done=await wait(p.id,r=>['complete','failed'].includes(r.status));const receipts=fs.readFileSync(path.join(p.folder,'fixture-receipts.jsonl'),'utf8').trim().split('\n').map(JSON.parse);record('resume-after-restart',{status:done.row.status,attempts:receipts.map(r=>r.attemptId),subTasks:done.row.subTasks.map(s=>({title:s.title,status:s.status,attempts:s.attemptIds.length}))});
  }else if(stage==='advanced'||stage==='advanced-continue'){
    if(stage==='advanced'){
    const exported=await api('/api/project-export?projectId='+calculator.id),plan=await api('/api/plan');
    const imported=await api('/api/project-import',{version:plan.version,bundle:exported},201);const restored=imported.plan.projects.find(p=>p.project.id===imported.projectId);
    assert.notEqual(restored.project.id,calculator.id);assert.ok(!restored.project.folder);record('export-import',{original:calculator.id,copy:restored.project.id,folder:restored.project.folder,history:restored.tasks.length});
    let notebook=await api('/api/plan');await api('/api/place',{version:notebook.version,project:{id:restored.project.id,folder:calculator.folder}},201);record('explicit-relink',{copy:restored.project.id});
    const moving=await create('Audit-moved'),moved=path.join(out,'Audit-moved-away');assert.ok(path.resolve(moving.folder).startsWith(out+path.sep)&&path.resolve(moved).startsWith(out+path.sep));fs.renameSync(moving.folder,moved);
    const blocked=await api('/api/orchestrator/propose',{projectId:moving.id,title:'Moved-folder probe',goal:'No child may start in a missing folder',subTasks:[{title:'receipt',detail:'AUDIT_SCENARIO=receipt',assignedModelId:model,dependsOn:[]}]},201);record('moved-folder',{status:blocked.snapshot.projects.find(p=>p.id===moving.id).status,next:blocked.snapshot.projects.find(p=>p.id===moving.id).next,noReceipt:!fs.existsSync(path.join(moved,'fixture-receipts.jsonl'))});
    }
    let notebook=await api('/api/plan');const moving=notebook.plan.projects.find(p=>p.project.name==='Audit-moved').project,moved=path.join(out,'Audit-moved-away');await api('/api/place',{version:notebook.version,project:{id:moving.id,folder:moved}},201);
    await api('/api/orchestrator/ask',{projectId:moving.id,question:'Implement a harmless receipt only. AUDIT_SCENARIO=receipt'});assert.equal((await approve(moving.id)).row.status,'complete');record('moved-folder-recovery',{status:'complete'});
    await api('/api/providers/connections',{id:'audit-missing',label:'AUDIT missing executable',adapter:'configured-cli',cliCommand:path.join(out,'no-such-executable.exe'),cliArgs:['{model}'],restrictedArgs:[],yoloArgs:[],outputProtocol:'codex-jsonl',modelIds:['missing'],toolsEnabled:true});
    const missing=await create('Audit-missing');const rejected=await api('/api/orchestrator/propose',{projectId:missing.id,title:'Missing CLI',goal:'Must not start',subTasks:[{title:'missing',detail:'AUDIT_SCENARIO=receipt',assignedModelId:'audit-missing:missing',dependsOn:[]}]},201);record('missing-executable-proposal',{status:rejected.snapshot.projects.find(p=>p.id===missing.id).status,noReceipt:!fs.existsSync(path.join(missing.folder,'fixture-receipts.jsonl'))});
  }else if(stage==='containment'){
    const plan=await api('/api/plan'),missing=plan.plan.projects.find(p=>p.project.id==='audit-missing').project,row=(await snapshot()).projects.find(p=>p.id===missing.id);
    const response=await fetch(url+'/api/orchestrator/approve',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId:missing.id,megaTaskId:row.megaTaskId})});const approved=await response.json();const failed=await wait(missing.id,r=>r.status==='failed');record('missing-executable-launch',{httpStatus:response.status,status:failed.row.status,next:failed.row.next,noReceipt:!fs.existsSync(path.join(missing.folder,'fixture-receipts.jsonl'))});
    const foreign=await create('Audit-foreign-read');fs.writeFileSync(path.join(out,'foreign-canary.txt'),'AUDIT-FOREIGN-CANARY: disposable content, not private data.');await run(foreign.id,'foreign-read');record('configured-cli-foreign-read',JSON.parse(fs.readFileSync(path.join(foreign.folder,'foreign-observation.json'))));
    const metadata=await create('Audit-foreign-git'),dependency=JSON.parse(fs.readFileSync(path.join(out,'resume-project.json')));fs.writeFileSync(path.join(metadata.folder,'.git'),'gitdir: '+path.join(dependency.folder,'.git').replace(/\\/g,'/')+'\n');
    const r=await fetch(url+'/api/orchestrator/propose',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId:metadata.id,title:'Foreign metadata probe',goal:'No foreign metadata access',subTasks:[{title:'receipt',detail:'AUDIT_SCENARIO=receipt',assignedModelId:model,dependsOn:[]}]})});const data=await r.json();record('foreign-git-metadata',{httpStatus:r.status,error:data.error,status:data.snapshot?.projects.find(p=>p.id===metadata.id)?.status,noReceipt:!fs.existsSync(path.join(metadata.folder,'fixture-receipts.jsonl'))});
    if(r.ok){const approval=await fetch(url+'/api/orchestrator/approve',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({projectId:metadata.id,megaTaskId:data.snapshot.projects.find(p=>p.id===metadata.id).megaTaskId})});const denied=await approval.json();record('foreign-git-approval',{status:approval.status,error:denied.error});const completed=await wait(metadata.id,r=>['complete','failed'].includes(r.status));const inspection=await api('/api/repository?projectId='+metadata.id);record('foreign-git-boundary',{status:completed.row.status,workerLaunched:fs.existsSync(path.join(metadata.folder,'fixture-receipts.jsonl')),applicationGitInspectionAllowed:inspection.repository?.local?.git??inspection.local?.git??null});}
    const invalid=await api('/api/place',{version:(await api('/api/plan')).version,project:{id:missing.id,folder:path.join(out,'missing','..','nonexistent')}},400);record('invalid-relink',{error:invalid.error});
    const one=(await snapshot()).supervisor,two=(await snapshot()).supervisor;record('supervisor-observed',{name:one.name,modelId:one.modelId,projects:one.projects.map(p=>({id:p.projectId,status:p.status,active:p.active,alerts:p.alerts.map(a=>({id:a.id,kind:a.kind}))})),stableAlertIds:JSON.stringify(one.projects.map(p=>p.alerts.map(a=>a.id)))===JSON.stringify(two.projects.map(p=>p.alerts.map(a=>a.id)))});
  }else if(stage==='controls'){
    await api('/api/orchestrator/mode',{mode:'auto'});const p=await create('Audit-auto-approval'),proposed=await propose(p.id,'receipt');await delay(300);assert.equal(fs.existsSync(path.join(p.folder,'fixture-receipts.jsonl')),false);assert.equal((await snapshot()).projects.find(r=>r.id===p.id).status,'needs_approval');await approve(p.id);await api('/api/orchestrator/mode',{mode:'manual'});record('automatic-mode-approval-gate',{noExecutionBeforeApproval:true,status:'complete',restoredMode:'manual'});
    const file=path.join(calculator.folder,'.planwharf/skills/audit-scope/SKILL.md'),bytes=fs.readFileSync(file);fs.unlinkSync(file);
    try{const r=await propose(calculator.id,'receipt');await api('/api/orchestrator/approve',{projectId:calculator.id,megaTaskId:r.megaTaskId});const failed=await wait(calculator.id,r=>r.status==='failed'||r.status==='checkpointed');record('missing-selected-skill',{status:failed.row.status,next:failed.row.next});}finally{fs.writeFileSync(file,bytes);}
    const grill=await api('/api/orchestrator/ask',{projectId:calculator.id,question:'Grill me about the calculator requirements without starting any work.',grillKind:'me'});record('grill-with-manual-skill-selection',{answer:grill.answer});
  }else if(stage==='restart-concurrency'||stage==='stop-concurrency'){
    const projects=JSON.parse(fs.readFileSync(path.join(out,'concurrent-projects.json')));for(const p of projects){const row=(await snapshot()).projects.find(r=>r.id===p.id);if(stage==='restart-concurrency')await api('/api/orchestrator/resume',{projectId:p.id,megaTaskId:row.megaTaskId,checkpointId:row.checkpoint.id,confirmChanges:true});else if(row.dispatch.some(d=>d.alive))await api('/api/orchestrator/stop',{projectId:p.id,megaTaskId:row.megaTaskId});}record(stage,{projects:projects.map(p=>p.id)});
  }else if(stage==='deadline'){
    const p=await create('Audit-deadline','AUDIT_SCENARIO=hang. Controlled timeout test only.');await api('/api/orchestrator/grill',{projectId:p.id,skip:true});
    const planned=await api('/api/orchestrator/start',{projectId:p.id,version:(await api('/api/plan')).version,timing:'deadline',deadlineAt:new Date(Date.now()+4000).toISOString()});const row=planned.snapshot.projects.find(r=>r.id===p.id);
    await api('/api/orchestrator/approve',{projectId:p.id,megaTaskId:row.megaTaskId});const running=await wait(p.id,r=>r.dispatch?.some(d=>d.alive));const pids=running.row.dispatch.filter(d=>d.alive).map(d=>d.pid);const done=await wait(p.id,r=>r.status==='checkpointed',15000);
    record('deadline-checkpoint',{status:done.row.status,pids,stillAlive:pids.filter(pid=>{try{process.kill(pid,0);return true;}catch{return false;}})});
  }else if(stage==='quit'){
    const s=await snapshot();assert.ok(s.projects.every(r=>!r.dispatch?.some(d=>d.alive)),'Checkpoint audit workers before quitting');await api('/api/app/quit',{});record('normal-app-quit',{pid:app.pid});
  }else throw Error('Unknown stage '+stage);
}
main().catch(error=>{record('harness-error',{stage,message:error.message});process.exitCode=1;});
