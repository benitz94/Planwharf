'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const R=require('./runtime.cjs');
const {sanitizeEvidence}=require('./analysis.cjs');
function assess({transport,events,verification,nonce,diagnostic}){
  const edited=events.some(e=>e.type==='item.completed'&&e.item?.type==='file_change'&&e.item.status==='completed');
  const executed=events.some(e=>e.type==='item.completed'&&e.item?.type==='command_execution'&&e.item.status==='completed'&&e.item.exit_code===0&&/\bnode(?:\.exe)?\b/i.test(e.item.command||''));
  const verified=verification?.nonce===nonce&&verification?.passed===true;
  return {status:transport.completed&&edited&&executed&&verified?'passed':'blocked',edited,executed,verified,
    reason:transport.timedOut?'Capability check timed out':/open sandbox provisioning pipe/i.test(diagnostic)?'Windows sandbox provisioning pipe unavailable':/read-only sandbox|filesystem is read.only/i.test(diagnostic)?'Effective CLI filesystem policy is read-only':!transport.completed?'CLI transport did not complete':!edited?'Native file editing was not demonstrated':!executed?'Successful native Node execution was not demonstrated':!verified?'Independent capability receipt did not match':'Capability check passed'};
}
async function checkCapabilities({model,directory,timeoutMs}){
  const workspace=path.join(directory,'workspace'),call=path.join(directory,'call');fs.mkdirSync(workspace,{recursive:true});
  const nonce=crypto.randomUUID();fs.writeFileSync(path.join(workspace,'README.md'),'Disposable native CLI capability check. Not a benchmark task.\n');R.initializeGit(workspace);
  const prompt=`This is a capability check, not a benchmark task. Use apply_patch to create capability.cjs exporting the string ${JSON.stringify(nonce)}. Then run Node to require that file, assert the string equals ${JSON.stringify(nonce)}, and write verification.json with {"nonce":${JSON.stringify(nonce)},"passed":true}. Only Node should create verification.json. Do not claim success unless tools succeed. No visible windows, browser, external network or files outside this workspace. If a tool is blocked, report it and stop; do not retry around the restriction.`;
  const transport=await R.runCli({workspace,prompt,model,artifactDir:call,timeoutMs});
  const read=name=>{try{return fs.readFileSync(path.join(call,name),'utf8');}catch{return'';}};
  const events=read('stdout.jsonl').split('\n').flatMap(line=>{try{return[JSON.parse(line)];}catch{return[];}});
  let verification=null;try{verification=JSON.parse(fs.readFileSync(path.join(workspace,'verification.json'),'utf8'));}catch{}
  const diagnostic=read('stderr.log')+'\n'+events.filter(e=>e.type==='item.completed').map(e=>e.item?.aggregated_output||e.item?.text||'').join('\n');
  const result={...assess({transport,events,verification,nonce,diagnostic}),model,usage:transport.usage,invocations:1,wallMs:transport.wallMs,exitCode:transport.exitCode,diagnostic:sanitizeEvidence(diagnostic),
    scope:'Calibration only. No task trials are submitted unless native editing and Node execution both work. Provider usage here is separate from arm totals.'};
  fs.writeFileSync(path.join(directory,'result.json'),JSON.stringify(result,null,2)+'\n');return result;
}
module.exports={assess,checkCapabilities};
