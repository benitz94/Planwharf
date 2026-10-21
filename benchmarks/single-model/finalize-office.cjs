'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const A=require('./analysis.cjs'),{remaining,verifyFrozen}=require('./continue.cjs'),{schedule}=require('./run.cjs');
const read=file=>JSON.parse(fs.readFileSync(file,'utf8'));
const write=(root,name,value)=>fs.writeFileSync(path.join(root,name),typeof value==='string'?value:JSON.stringify(value,null,2)+'\n');
function validate(checkpoint,manifest,office){
 const bytes=fs.readFileSync(path.join(checkpoint,'runs.json')),home=JSON.parse(bytes);
 if(crypto.createHash('sha256').update(bytes).digest('hex')!==manifest.checkpoint.runsSha256)throw Error('Checkpoint bytes changed');
 const pending=remaining(schedule(5),home),allowed=new Set(pending.map(s=>s.pairId+'/'+s.arm));
 if(home.length!==20||office.length!==10||remaining(schedule(5),[...home,...office]).length||office.some(r=>!allowed.has(r.pairId+'/'+r.arm)||r.cohort!==manifest.cohort))throw Error('Continuation is incomplete or contains an unexpected slot');
 const progressFile=path.join(manifest.options.output,'progress.json'),progress=read(progressFile);
 if(progress.completed!==10||progress.current!==null||!progress.finishedAt)throw Error('Runner did not finish');
 return home;
}
function finalize({checkpoint,study,profile,frozenRoot,destination}){
 const manifest=read(path.join(study,'manifest-private.json')),office=read(path.join(study,'runs.json'));
 const home=validate(checkpoint,manifest,office);verifyFrozen(frozenRoot,manifest.sourceFreeze.hashes);
 const audit=require('./audit.cjs').auditStudy(study,profile);if(!audit.valid)throw Error('Native usage audit failed');
 require('./export.cjs').exportStudy(study,destination);
 const clean=office.filter(r=>r.pairedComparisonEligible),analysis=A.analyze(clean);
 write(destination,'usage-audit.json',A.sanitizeEvidence(audit));
 write(destination,'combined-runs.json',[...home,...office]);
 write(destination,'cohorts.json',{home:{trialIds:home.map(r=>r.id),manifest:'../full-paused-2026-10-21/manifest.json',rawEvidenceAvailableHere:false},office:{trialIds:office.map(r=>r.id),manifest:'manifest.json',rawEvidenceAvailableHere:true},comparability:manifest.comparability});
 write(destination,'analysis.json',{scope:'Office scheduler pairs 2..5 only; excludes BOTH arms of operator-replacement pair 1. No home/office pooling.',analysis});
 write(destination,'REPORT.txt','OFFICE SCHEDULER PAIRS 2..5 ONLY\nAll ten office observations and their usage are retained in runs.json and usage-audit.json. Pair 1 is separately reported in RESULTS.md.\n\n'+A.renderReport(analysis,{environment:A.sanitizeEvidence(manifest.environment)}));
 // Keep each home category separate as well as keeping the machine cohorts separate.
 write(destination,'home-category-analysis.json',Object.fromEntries(['config','rest-api'].map(task=>[task,A.analyze(home.filter(r=>r.taskId===task))])));
 const counts=rows=>Object.fromEntries(['direct','planwharf'].map(arm=>{const list=rows.filter(r=>r.arm===arm);return[arm,{trials:list.length,outcomes:Object.fromEntries(['accepted','defective','failed','blocked','timed_out'].map(o=>[o,list.filter(r=>r.outcome===o).length]))}];}));
 write(destination,'outcomes.json',{home:counts(home),office:counts(office),operatorAborted:manifest.operatorAborted});
 const lines=['# Completed trial slots, with separate machine cohorts','',
 'All 30 logical slots now have an outcome: the original 20 home rows were preserved byte-for-byte in their checkpoint and were not rerun; ten scheduler slots ran on the office machine. This is not a single-machine 30-trial timing comparison.','',
 '| Cohort / task | Direct accepted | Planwharf accepted |','| --- | ---: | ---: |'];
 for(const [cohort,task,rows] of [['Home','config',home],['Home','rest-api',home],['Office','scheduler',office]]){
  const group=rows.filter(r=>r.taskId===task),c=counts(group);lines.push(`| ${cohort} / ${task} | ${c.direct.outcomes.accepted}/${c.direct.trials} | ${c.planwharf.outcomes.accepted}/${c.planwharf.trials} |`);
 }
 lines.push('','Every defective, blocked, failed or timed-out result remains in runs.json; acceptance requires completed transport and all sealed checks. Generated implementations were not hand-repaired, and private grader feedback was not supplied to the model.','',
 '## Environment and continuity','',
 `Office: ${manifest.environment.cpu}; ${manifest.environment.logicalCpus} logical CPUs; ${manifest.environment.node}; Windows ${manifest.environment.release}. The native CLI ${manifest.environment.cli.version}, requested model ${manifest.model}, source tree and all seven frozen raw source hashes were verified. A separate capability call demonstrated native editing and Node execution before any trial. Its usage is calibration, outside trial totals.`,
 '', 'Home used a Xeon E3-1505M v5, eight logical CPUs, Node v24.19.0 and Windows 10.0.19045. Office uses an isolated authenticated profile with notification hooks disabled. Profile/hardware differ; exact account continuity and immutable provider-resolved model snapshot are UNKNOWN. Both office arms use the same office profile and executable. No overall time/token savings or monetary claim is made.','',
 '## Operator-aborted observation and replacement','',
 `The original ${manifest.operatorAborted.previousId} remains operator-aborted, with reported input 43,641, output 5,033, cached input 38,528 and reasoning 1,706 tokens. Additional in-flight consumption is UNKNOWN. Its workspace/native session was unavailable on this machine. scheduler-1/direct is a declared fresh replacement, not a resumed native session. Both arms of scheduler-1 are excluded from comparative resource analysis; their outcomes, elapsed time and measured usage remain published.`,
 '', 'The office report analyzes only scheduler pairs 2..5. At most four comparable pairs remain, below the five-pair bootstrap threshold. Home results are analyzed by task separately. Costs, attributable quota decrement and measured human effort remain UNKNOWN. Cached input and reasoning are subsets, never added again to input + output.','',
 '## Evidence coverage','',
 `The independent office native audit checked ${audit.trialsChecked} trials, ${audit.knownCalls} measured launches and ${audit.unknownCalls} launches with unavailable receipts. Unique response allocation and cumulative native totals were checked, including interrupted responses. Unreported consumption remains UNKNOWN.`,
 '', 'Office sanitized call logs, native numeric receipts and acceptance reports are published here; workspaces and the authenticated profile are excluded. The existing home numeric audit is carried forward unchanged in the original checkpoint. Full home raw logs and the old aborted workspace were not available here and have not been independently reaudited or newly uploaded. Earlier pilots remain separate.','',
 'Execution used hidden child processes and the background server, with visible-window callbacks disabled. No desktop launcher or browser was opened. Production engine behavior and the frozen trial functions were unchanged. The continuation loader only exposes the frozen lexical trial functions in memory; the original files remain intact.','');
 write(destination,'RESULTS.md',lines.join('\n'));return {destination,audit,office:counts(office)};
}
module.exports={validate,finalize};
if(require.main===module){try{const result=finalize(read(process.argv[2]));console.log(JSON.stringify({destination:result.destination,valid:result.audit.valid,office:result.office}));}catch(error){console.error(error.stack);process.exitCode=1;}}
