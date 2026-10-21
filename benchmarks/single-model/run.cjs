'use strict';
const fs=require('node:fs'),path=require('node:path'),os=require('node:os'),crypto=require('node:crypto');
const {spawn}=require('node:child_process');
const tasks=require('./tasks.cjs'),R=require('./runtime.cjs'),A=require('./analysis.cjs');
const runner=require('../../engine/runner.cjs');
const usageLedger=require('./usage.cjs');
const RECOVERY='Continue the interrupted task from the existing files and the unchanged TASK.md. Complete all requirements, run your own tests and review your work. Do not open any visible windows or inspect other workspaces.';
const delay=ms=>new Promise(resolve=>setTimeout(resolve,ms));
function save(file,value){fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,JSON.stringify(value,null,2)+'\n');}
function schedule(pairs=1){return tasks.listTasks().flatMap((task,index)=>Array.from({length:pairs},(_,pair)=>({taskId:task.id,category:task.class,pairId:task.id+'-'+(pair+1),
  scenario:index===0?'baseline':'implementation-interruption',arms:(pair+index)%2?['planwharf','direct']:['direct','planwharf']})));}
function bounded(options){if(!Number.isInteger(options.pairs)||options.pairs<1||options.pairs>5)throw Error('Pairs must be 1..5 per task');if(!Number.isInteger(options.timeoutMs)||options.timeoutMs<1000||options.timeoutMs>900000)throw Error('Per-run timeout must be 1000..900000 ms');if(!Number.isInteger(options.maxCalls)||options.maxCalls<1||options.maxCalls>12)throw Error('Max calls must be 1..12');}
function completion(transport,grade){if(transport.timedOut)return'timed_out';if(transport.blocked)return'blocked';if(!transport.completed)return'failed';return grade.passed===grade.total?'accepted':'defective';}
function failureText(dir){try{return fs.readFileSync(path.join(dir,'stderr.log'),'utf8').slice(-12000);}catch{return'';}}
function environmentBlocked(text){return /Access is denied|unable to open database|failed to initialize|authentication|unauthori[sz]ed|insufficient_quota|quota exceeded|429|rate.limit|model.*(?:unavailable|not supported)|Could not find home/i.test(text);}
function receiptSession(id,profile){
  if(!/^[a-f0-9-]{36}$/i.test(id||''))return null;
  const root=path.join(profile,'sessions');let found=null;
  function visit(dir){let list;try{list=fs.readdirSync(dir,{withFileTypes:true});}catch{return;}for(const e of list){if(e.isSymbolicLink())continue;const p=path.join(dir,e.name);if(e.isDirectory())visit(p);else if(e.name.endsWith(id+'.jsonl'))found=p;}}
  visit(root);if(!found)return null;
  let last=null;for(const line of fs.readFileSync(found,'utf8').split('\n')){try{const e=JSON.parse(line);if(e.type==='event_msg'&&e.payload?.type==='token_count'&&e.payload.info?.total_token_usage)last=e.payload.info.total_token_usage;}catch{}}
  if(!last)return null;return{inputTokens:last.input_tokens??null,outputTokens:last.output_tokens??null,cachedInputTokens:last.cached_input_tokens??null,reasoningTokens:last.reasoning_output_tokens??null};
}
function meaningfulChanged(workspace,start){return R.sourceDigest(workspace)!==start;}
async function direct(options,workspace,privateDir,scenario){
  const start=R.sourceDigest(workspace),calls=[],started=performance.now();let fault=false;
  let result=await R.runCli({workspace,prompt:tasks.specification(options.taskId),model:options.model,artifactDir:path.join(privateDir,'call-1'),timeoutMs:options.timeoutMs,
    onTick:scenario==='implementation-interruption'?()=>{if(!fault&&meaningfulChanged(workspace,start)){fault=true;return true;}return false;}:undefined});
  calls.push(result);
  if(result.interrupted&&result.threadId&&options.maxCalls>1){const remaining=Math.floor(options.timeoutMs-(performance.now()-started));if(remaining>1000){result=await R.runCli({workspace,prompt:RECOVERY,model:options.model,artifactDir:path.join(privateDir,'call-2'),timeoutMs:remaining,resumeId:result.threadId});calls.push(result);}}
  const diagnostic=calls.map((c,i)=>[failureText(path.join(privateDir,'call-'+(i+1))),...c.failure,...c.diagnostics].join('\n')).join('\n');
  const accounted=usageLedger.allocate(calls);save(path.join(privateDir,'usage-ledger.json'),accounted.map(c=>({threadId:c.threadId,exitCode:c.exitCode,usage:c.usage,responseReceipt:c.responseReceipt,newResponseIds:c.newResponseIds,accounting:c.accounting})));
  return{completed:result.completed,timedOut:result.timedOut,blocked:!result.completed&&environmentBlocked(diagnostic),calls:accounted.map(c=>({usage:c.usage,wallMs:c.wallMs,exitCode:c.exitCode,threadId:c.threadId,newResponseIds:c.newResponseIds,accounting:c.accounting})),
    invocations:calls.length,usageScope:'all_calls',faultInjected:fault,recoveryAttempted:calls.length>1,restarts:calls.length-1,initialApprovals:0,scriptedRecoveryActions:calls.length-1,diagnostic:A.sanitizeEvidence(diagnostic),
    timing:{cliWallMs:calls.reduce((n,c)=>n+c.wallMs,0),modelMs:null,waitingMs:null,retryMs:null,processLaunchMs:null}};
}
async function startHost(options,privateDir){
  const planFile=path.join(privateDir,'notebook.json');const starter=structuredClone(require('../../fixtures/starter-notebook.json'));starter.projects=starter.projects.map(item=>({...item,tasks:[]}));save(planFile,starter);
  const config={model:options.model,maxCalls:options.maxCalls,planFile,attemptsDir:path.join(privateDir,'attempts'),callsFile:path.join(privateDir,'calls.json')};save(path.join(privateDir,'host-config.json'),config);
  const log=fs.openSync(path.join(privateDir,'host.log'),'a');const child=spawn(process.execPath,[path.join(__dirname,'host.cjs'),path.join(privateDir,'host-config.json')],{env:R.cliEnvironment(),windowsHide:true,stdio:['ignore',log,log,'ipc']});fs.closeSync(log);
  let base;try{base=await new Promise((resolve,reject)=>{const t=setTimeout(()=>reject(Error('Background host startup timed out')),Math.min(15000,options.timeoutMs));child.once('message',m=>{clearTimeout(t);resolve(m.base);});child.once('error',e=>{clearTimeout(t);reject(e);});child.once('exit',code=>{clearTimeout(t);reject(Error('Background host exited '+code));});});}
  catch(error){if(child.pid&&child.exitCode===null)runner.killWorker(child.pid);throw error;}
  return{base,child,config};
}
async function planwharf(options,workspace,privateDir,scenario){
  const started=performance.now(),host=await startHost(options,privateDir),events=[];let projectId,megaId,completed=false,timedOut=false,blocked=false,fault=false,recoveryAttempted=false,approvals=0,final=null,cleaning=false;
  const start=R.sourceDigest(workspace);
  async function api(route,data){const remaining=cleaning?3000:Math.floor(options.timeoutMs-(performance.now()-started));if(remaining<=0)throw Error('Benchmark deadline reached');const r=await fetch(host.base+route,{...(data===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(data)}),signal:AbortSignal.timeout(remaining)});const body=await r.json();events.push({at:new Date().toISOString(),route,status:r.status});if(!r.ok)throw Error(body.error||body.reason||'HTTP '+r.status);return body;}
  try{
    const current=await api('/api/plan');
    const created=await api('/api/projects',{version:current.version,project:{name:'Benchmark '+options.taskId,direction:tasks.specification(options.taskId),folder:workspace},createFolder:false});
    projectId=created.plan.projects.at(-1).project.id;
    await api('/api/orchestrator/lead',{id:'benchmark-model'});
    await api('/api/orchestrator/grill',{propose:false});
    await api('/api/focus',{projectId});
    const prepared=await api('/api/orchestrator/start',{version:created.version,projectId,timing:'discretion'});
    const row=prepared.snapshot?.projects.find(r=>r.id===projectId)||(await api('/api/orchestrator?readOnly=1')).projects.find(r=>r.id===projectId);
    if(prepared.phase!=='plan'||!row?.megaTaskId)throw Error('Normal Start did not produce an approvable MegaTask');
    megaId=row.megaTaskId;await api('/api/orchestrator/approve',{projectId,megaTaskId:megaId});approvals++;
    while(performance.now()-started<options.timeoutMs){
      final=(await api('/api/orchestrator')).projects.find(r=>r.id===projectId);
      if(scenario==='implementation-interruption'&&!fault&&final.workerAlive&&meaningfulChanged(workspace,start)){
        fault=true;await api('/api/orchestrator/stop',{projectId,megaTaskId:megaId});
        const execution=await api('/api/execution?projectId='+encodeURIComponent(projectId));const cp=execution.checkpoints.at(-1);
        if(!cp)throw Error('Interruption did not produce a checkpoint');
        await api('/api/orchestrator/resume',{projectId,megaTaskId:megaId,checkpointId:cp.id,confirmChanges:true});recoveryAttempted=true;
      }
      if(final.status==='complete'&&!final.workerAlive){completed=true;break;}
      if(!final.workerAlive&&(['failed','checkpointed'].includes(final.status)||final.status?.startsWith('blocked_'))){break;}
      await delay(700);
    }
    if(!completed&&performance.now()-started>=options.timeoutMs)timedOut=true;
  }catch(error){events.push({error:error.message});blocked=environmentBlocked(error.message);if(performance.now()-started>=options.timeoutMs)timedOut=true;}
  finally{cleaning=true;if(projectId&&(!completed||timedOut))try{await api('/api/orchestrator/stop',{projectId,megaTaskId:megaId});}catch{}save(path.join(privateDir,'http-events.json'),events);if(final)save(path.join(privateDir,'final-snapshot.json'),final);
    if(host.child.connected)host.child.send('close');await Promise.race([new Promise(r=>host.child.once('exit',r)),delay(3000)]);if(host.child.exitCode===null)runner.killWorker(host.child.pid);}
  let raw=[];try{raw=JSON.parse(fs.readFileSync(host.config.callsFile,'utf8'));}catch{}
  const profile=R.cliEnvironment().CODEX_HOME,calls=[];let diagnostic='';
  for(const event of raw.filter(e=>e.kind==='worker')){
    let log='';try{log=fs.readFileSync(event.logPath,'utf8');}catch{}diagnostic+=log+'\n';
    const sessionId=log.match(/session id:\s*([a-f0-9-]{36})/i)?.[1]||null;
    let exit=null;try{exit=JSON.parse(fs.readFileSync(event.exitPath,'utf8'));}catch{}
    calls.push({role:'worker',exactModel:event.exactModel,attemptId:event.attemptId,responseReceipt:usageLedger.readReceipt(sessionId,profile),usage:exit?.exitCode===0?receiptSession(sessionId,profile):null,exitCode:exit?.exitCode??null,sessionId,launchMs:event.launchMs});
  }
  for(const event of raw.filter(e=>e.kind==='lead-start')){const finish=raw.find(e=>e.kind==='lead-finish'&&e.id===event.id);diagnostic+='\n'+JSON.stringify({failure:finish?.failure,diagnostic:finish?.diagnostic,error:finish?.error});calls.push({role:'lead',exactModel:event.exactModel,usage:finish?.tokens?{inputTokens:finish.tokens.input,outputTokens:finish.tokens.output,cachedInputTokens:finish.tokens.cachedInput??null,reasoningTokens:null}:null,wallMs:finish?.wallMs??null});}
  const accounted=usageLedger.allocate(calls);save(path.join(privateDir,'usage-ledger.json'),accounted);
  if(!completed&&environmentBlocked(diagnostic))blocked=true;
  return{completed,timedOut,blocked,calls:accounted.map(({responseReceipt,...call})=>call),invocations:calls.length,usageScope:'all_calls',faultInjected:fault,recoveryAttempted,restarts:recoveryAttempted?1:0,initialApprovals:approvals,scriptedRecoveryActions:recoveryAttempted?2:0,
    diagnostic:A.sanitizeEvidence(diagnostic.slice(-16000)),httpEvents:A.sanitizeEvidence(events),lifecycle:final?{status:final.status,megaTaskId:final.megaTaskId,subTasks:final.subTasks?.map(s=>({type:s.type,status:s.status}))}:null,
    timing:{modelMs:null,waitingMs:null,retryMs:null,processLaunchMs:calls.length&&calls.every(c=>Number.isFinite(c.launchMs))?calls.reduce((n,c)=>n+c.launchMs,0):null}};
}
async function execute(options){
  bounded(options);const output=path.resolve(options.output);if(fs.existsSync(output))throw Error('Output directory must not already exist; evidence is append-only per study');fs.mkdirSync(output,{recursive:true});
  const list=schedule(options.pairs),studyId=crypto.randomUUID(),environment=await R.environment(options.model);
  if(!environment.modelInAuthenticatedCatalog||!environment.cli.authenticated)throw Error('Exact model/account access is not verified');
  const manifest={schema:1,studyId,startedAt:new Date().toISOString(),kind:options.pairs===5?'LIVE_FULL_STUDY':'LIVE_PILOT',protocolRevision:2,baseline:environment.baseline,model:options.model,options,environment,schedule:list,
    specificationHashes:tasks.listTasks(),sealedGraderHash:R.sha(fs.readFileSync(path.join(__dirname,'tasks.cjs'))),protocol:{accounting:'Native provider per-response receipts, including interrupted responses, deduplicated by response ID across resume. Unreported provider usage remains UNKNOWN.',acceptance:'All presealed deterministic checks pass AND transport completes successfully; incomplete outcomes retain resource use',timeBoundMs:options.timeoutMs,callBound:options.maxCalls,
      interventions:'One predeclared recovery on first source change for medium/complex, no ad hoc fixes or hidden grader feedback',windows:'No visible windows. Only server.cjs and noninteractive CLI are used.',humanEffort:'Scripted HTTP/CLI actions recorded; actual human-time savings UNKNOWN',fullStudy:'Target five pairs per task only after an accepted pilot and explicit --pairs 5 invocation; no automatic provider spending'}};
  save(path.join(output,'manifest-private.json'),manifest);save(path.join(output,'manifest.json'),A.sanitizeEvidence(manifest));
  const preflight=await require('./preflight.cjs').checkCapabilities({model:options.model,directory:path.join(output,'private','preflight'),timeoutMs:Math.min(options.timeoutMs,90000)});
  save(path.join(output,'preflight.json'),preflight);
  if(preflight.status!=='passed'){
    const analysis=A.analyze([]);save(path.join(output,'runs.json'),[]);save(path.join(output,'analysis.json'),analysis);
    fs.writeFileSync(path.join(output,'REPORT.txt'),A.renderReport(analysis,{environment:A.sanitizeEvidence(environment),preflight}));
    console.log(JSON.stringify({outcome:'PREFLIGHT_BLOCKED',reason:preflight.reason,taskTrialsSubmitted:0}));return{output,runs:[],analysis,preflight,blocked:true};
  }
  const runs=[];
  for(const pair of list){let initialCommit=null;for(const arm of pair.arms){
    if(R.sha(fs.readFileSync(path.join(__dirname,'tasks.cjs')))!==manifest.sealedGraderHash)throw Error('Sealed acceptance changed during study');
    const id=pair.pairId+'-'+arm+'-'+crypto.randomUUID().slice(0,8),dir=path.join(output,'private',id),workspace=path.join(dir,'workspace');fs.mkdirSync(dir,{recursive:true});
    const seed=tasks.setupTask(pair.taskId,workspace),commit=R.initializeGit(workspace);if(initialCommit&&commit!==initialCommit)throw Error('Paired starting Git histories differ');initialCommit=commit;
    const started=performance.now();let transport;
    try{transport=await(arm==='direct'?direct:planwharf)({...options,taskId:pair.taskId},workspace,dir,pair.scenario);}
    catch(error){transport={completed:false,timedOut:false,blocked:environmentBlocked(error.message),diagnostic:A.sanitizeEvidence(error.message),calls:null,invocations:null,usageScope:'all_calls',initialApprovals:null,restarts:null};}
    const gradeStarted=performance.now();let grade;
    try{grade=await tasks.evaluateTask(pair.taskId,workspace,{timeoutMs:10000,evidenceDir:path.join(dir,'acceptance')});}
    catch(error){grade={passed:0,total:tasks.listTasks().find(t=>t.id===pair.taskId).checkCount,outcome:'evaluator_error',error:A.sanitizeEvidence(error.message)};transport.completed=false;}
    const record={id,taskId:pair.taskId,category:pair.category,pairId:pair.pairId,arm,scenario:pair.scenario,model:options.model,startingCommit:commit,starterHash:seed.starterHash,
      outcome:completion(transport,grade),wallMs:performance.now()-started,evaluationMs:performance.now()-gradeStarted,...transport,interventions:null,requiredInitialSubmissions:1,
      acceptance:grade,artifactId:R.sourceDigest(workspace),quotaDecrement:null,billedCost:null};record.recovered=record.recoveryAttempted?record.outcome==='accepted':null;
    runs.push(A.sanitizeEvidence(record));save(path.join(output,'runs.json'),runs);console.log(JSON.stringify({id,arm,task:pair.taskId,outcome:record.outcome,checks:grade.passed+'/'+grade.total,wallMs:Math.round(record.wallMs),invocations:record.invocations}));
    const analysis=A.analyze(runs);save(path.join(output,'analysis.json'),analysis);fs.writeFileSync(path.join(output,'REPORT.txt'),A.renderReport(analysis,{environment:A.sanitizeEvidence(environment)}));
  }}
  return{output,runs,analysis:A.analyze(runs)};
}
async function offlineDemo(output){const root=path.resolve(output);if(fs.existsSync(root))throw Error('Demo output already exists');fs.mkdirSync(root,{recursive:true});const reports=[];
  for(const task of tasks.listTasks()){const workspace=path.join(root,task.id);tasks.setupTask(task.id,workspace);R.initializeGit(workspace);reports.push(await tasks.evaluateTask(task.id,workspace,{timeoutMs:10000,evidenceDir:path.join(root,task.id+'-acceptance')}));}
  save(path.join(root,'offline.json'),{mode:'OFFLINE_DEMONSTRATION_NOT_A_BENCHMARK',providerInvocations:0,expected:'Unimplemented starters must be rejected',reports});return root;}
module.exports={schedule,bounded,completion,execute,offlineDemo,receiptSession};
if(require.main===module){const args=process.argv.slice(2);const value=(name,fallback)=>args.includes(name)?args[args.indexOf(name)+1]:fallback;
  (async()=>{if(args.includes('--offline-demo')){console.log(await offlineDemo(value('--output','benchmark-offline-'+Date.now())));return;}
    if(!args.includes('--live')){console.log('Offline: node benchmarks/single-model/run.cjs --offline-demo --output <new-dir>\nOpt-in live: --live --model <exact-id> --pairs 1 --timeout-ms 480000 --max-calls 6 --output <new-private-dir>');return;}
    const result=await execute({model:value('--model',''),pairs:Number(value('--pairs','1')),timeoutMs:Number(value('--timeout-ms','480000')),maxCalls:Number(value('--max-calls','6')),output:value('--output','')});if(result.blocked)process.exitCode=2;
  })().catch(error=>{console.error(error.stack);process.exitCode=1;});}
