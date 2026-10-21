'use strict';
// Append-only continuation. Frozen trial bodies and task probes are never edited.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),Module=require('node:module');
const sha=value=>crypto.createHash('sha256').update(value).digest('hex');
const read=file=>JSON.parse(fs.readFileSync(file,'utf8'));
const save=(file,value)=>{fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,JSON.stringify(value,null,2)+'\n');};
function remaining(schedule,completed){
  const slots=schedule.flatMap(pair=>pair.arms.map(arm=>({...pair,arm})));
  const expected=new Map(slots.map(s=>[s.pairId+'/'+s.arm,s])),seen=new Set();
  for(const row of completed){const key=row.pairId+'/'+row.arm,slot=expected.get(key);
    if(!slot||seen.has(key)||slot.taskId!==row.taskId||slot.scenario!==row.scenario)throw Error('Invalid or duplicate completed slot: '+key);
    seen.add(key);
  }
  return slots.filter(s=>!seen.has(s.pairId+'/'+s.arm));
}
function verifyFrozen(root,hashes){for(const [file,hash] of Object.entries(hashes))if(sha(fs.readFileSync(path.join(root,file)))!==hash)throw Error('Frozen source differs: '+file);}
function loadFrozen(root,hashes){
  verifyFrozen(root,hashes);const filename=path.join(root,'run.cjs'),loaded=new Module(filename,module);
  loaded.filename=filename;loaded.paths=Module._nodeModulePaths(root);
  // Only expose lexical functions; the original CLI guard stays false.
  loaded._compile(fs.readFileSync(filename,'utf8')+'\nmodule.exports.trialFunctions={direct,planwharf};\n',filename);
  return loaded.exports;
}
async function continueStudy({checkpoint,frozenRoot,output,environment,preflight}){
  checkpoint=path.resolve(checkpoint);frozenRoot=path.resolve(frozenRoot);output=path.resolve(output);
  if(fs.existsSync(output))throw Error('Continuation destination must be new');
  const homeFile=path.join(checkpoint,'runs.json'),homeBytes=fs.readFileSync(homeFile),completed=JSON.parse(homeBytes);
  const original=read(path.join(checkpoint,'manifest.json')),freeze=read(path.join(checkpoint,'protocol-freeze.json'));
  const F=loadFrozen(frozenRoot,freeze.hashes),R=require(path.join(frozenRoot,'runtime.cjs')),A=require(path.join(frozenRoot,'analysis.cjs')),tasks=require(path.join(frozenRoot,'tasks.cjs'));
  const options={...original.options,output};F.bounded(options);
  if(preflight.status!=='passed'||!environment.cli.authenticated||!environment.modelInAuthenticatedCatalog||environment.cli.version!==original.environment.cli.version||environment.requestedModel!==original.model)throw Error('Environment/capability gate failed');
  if(JSON.stringify(tasks.listTasks())!==JSON.stringify(original.specificationHashes))throw Error('Task specifications differ');
  const slots=remaining(F.schedule(options.pairs),completed);
  if(completed.length!==20||slots.length!==10||slots.some(s=>s.taskId!=='scheduler'))throw Error('Expected exactly twenty completed and ten remaining scheduler slots');
  const replacement={pairId:'scheduler-1',arm:'direct',previousId:'scheduler-1-direct-b382c893',reason:'Operator-aborted on home machine; fresh replacement on office machine, not a native session resume',reportedUsage:{inputTokens:43641,outputTokens:5033,cachedInputTokens:38528,reasoningTokens:1706},additionalInFlightUsage:null,includeInPairedComparison:false};
  const manifest={schema:1,studyId:crypto.randomUUID(),kind:'LIVE_OFFICE_CONTINUATION',protocolRevision:2,startedAt:new Date().toISOString(),model:original.model,options,environment,baseline:original.baseline,schedule:slots,sourceFreeze:freeze,checkpoint:{studyId:original.studyId,completed:completed.length,runsSha256:sha(homeBytes)},cohort:'office-2026-10-21',comparability:{samePhysicalMachine:false,sameProfile:false,sameAccountContinuity:'UNKNOWN',resolvedModelSnapshot:'UNKNOWN',poolHomeOfficeTiming:false,pairedComparisons:'Office scheduler pairs 2..5 only; pair 1 operator replacement reported separately'},operatorAborted:replacement,protocol:original.protocol,specificationHashes:original.specificationHashes};
  save(path.join(output,'manifest-private.json'),manifest);save(path.join(output,'manifest.json'),A.sanitizeEvidence(manifest));save(path.join(output,'preflight.json'),preflight);
  const runs=[],commits=new Map();
  for(const slot of slots){
    verifyFrozen(frozenRoot,freeze.hashes);if(!fs.readFileSync(homeFile).equals(homeBytes))throw Error('Original checkpoint mutated');
    const {pairId,arm,taskId,category,scenario}=slot,id=pairId+'-'+arm+'-'+crypto.randomUUID().slice(0,8),dir=path.join(output,'private',id),workspace=path.join(dir,'workspace');fs.mkdirSync(dir,{recursive:true});
    const seed=tasks.setupTask(taskId,workspace),commit=R.initializeGit(workspace);if(commits.has(pairId)&&commits.get(pairId)!==commit)throw Error('Paired starting histories differ');commits.set(pairId,commit);
    save(path.join(output,'progress.json'),{completed:runs.length,total:slots.length,current:id,startedAt:new Date().toISOString()});console.log(JSON.stringify({starting:id,completed:runs.length}));
    const started=performance.now();let transport;
    try{transport=await F.trialFunctions[arm]({...options,taskId},workspace,dir,scenario);}
    catch(error){transport={completed:false,timedOut:false,blocked:false,diagnostic:A.sanitizeEvidence(error.message),calls:null,invocations:null,usageScope:'all_calls',initialApprovals:null,restarts:null};}
    const gradeStarted=performance.now();let grade;
    try{grade=await tasks.evaluateTask(taskId,workspace,{timeoutMs:10000,evidenceDir:path.join(dir,'acceptance')});}
    catch(error){grade={passed:0,total:tasks.listTasks().find(t=>t.id===taskId).checkCount,outcome:'evaluator_error',error:A.sanitizeEvidence(error.message)};transport.completed=false;}
    const record={id,taskId,category,pairId,arm,scenario,model:options.model,startingCommit:commit,starterHash:seed.starterHash,outcome:F.completion(transport,grade),wallMs:performance.now()-started,evaluationMs:performance.now()-gradeStarted,...transport,interventions:null,requiredInitialSubmissions:1,acceptance:grade,artifactId:R.sourceDigest(workspace),quotaDecrement:null,billedCost:null,cohort:manifest.cohort,operatorReplacement:pairId==='scheduler-1'&&arm==='direct',pairedComparisonEligible:pairId!=='scheduler-1'};
    record.recovered=record.recoveryAttempted?record.outcome==='accepted':null;
    runs.push(A.sanitizeEvidence(record));save(path.join(output,'runs.json'),runs);console.log(JSON.stringify({id,outcome:record.outcome,checks:grade.passed+'/'+grade.total,invocations:record.invocations,wallMs:Math.round(record.wallMs)}));
  }
  verifyFrozen(frozenRoot,freeze.hashes);if(!fs.readFileSync(homeFile).equals(homeBytes))throw Error('Original checkpoint mutated');
  save(path.join(output,'progress.json'),{completed:runs.length,total:slots.length,current:null,finishedAt:new Date().toISOString()});return {output,runs};
}
module.exports={remaining,verifyFrozen,loadFrozen,continueStudy};
if(require.main===module){const config=read(process.argv[2]);continueStudy(config).catch(error=>{console.error(error.stack);process.exitCode=1;});}
