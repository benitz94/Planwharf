'use strict';
// Background-only observational host. The application and provider adapters are unchanged.
const fs = require('node:fs'), path = require('node:path');
const { createServer } = require('../../server.cjs');
const runner = require('../../engine/runner.cjs');
const config = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const events = [], workers = new Map(); let count = 0;
function retireWorkers(){for(const [pid,info] of workers){let finished=false;try{finished=JSON.parse(fs.readFileSync(info.exitPath,'utf8')).attemptId===info.attemptId;}catch{}if(finished||!runner.isProcessAlive(pid))workers.delete(pid);}}
const retirement=setInterval(retireWorkers,500);retirement.unref();
function record(event) { events.push({ at: new Date().toISOString(), ...event }); fs.writeFileSync(config.callsFile, JSON.stringify(events, null, 2)); }
function check(model) { if (model.cli !== config.model || (model.providerId || model.provider) !== 'codex') throw Error('Benchmark forbids model/provider substitution'); if (++count > config.maxCalls) throw Error('Benchmark invocation bound reached'); }
const deniedWindow = () => { throw Error('Visible windows are prohibited by the benchmark'); };
const model = { id:'benchmark-model', label:'Single exact benchmark model', provider:'codex', cli:config.model, kind:'default', plan:'ChatGPT', enabled:true, available:true,
  roles:{orchestrator:true,worker:true,reviewer:true,supervisor:false}, policy:{alwaysApprove:false,minRemainingPercent:null,warningRemainingPercent:null,fallbackModelId:null} };
const server = createServer({ planFile:config.planFile, attemptsDir:config.attemptsDir, models:[model], skillsEnabled:false, syncSkills:false, refreshModels:false,
  monitorIntervalMs:1000, fetchPlans:async()=>[], openFolder:deniedWindow, openUrl:deniedWindow, openWatchWindow:deniedWindow, openPlanWindow:deniedWindow,
  spawnWorker(info) { check(info.worker); const started=performance.now(); const child=runner.spawnWorker(info); if(child.pid)workers.set(child.pid,{attemptId:info.attemptId,exitPath:info.exitPath});
    record({kind:'worker',exactModel:info.worker.cli,attemptId:info.attemptId,megaTaskId:info.megaTaskId,projectId:info.projectId,pid:child.pid,logPath:child.logPath,
      resultPath:info.resultPath,structuredResultPath:info.structuredResultPath,exitPath:info.exitPath,launchMs:performance.now()-started});return child; },
  async askModel(info) { check(info.model);const started=performance.now(); const id='lead-'+count;record({kind:'lead-start',id,exactModel:info.model.cli});
    try { const result=await runner.askOrchestrator(info); record({kind:'lead-finish',id,wallMs:performance.now()-started,tokens:result?.tokens||null,failure:result?.failure||null,diagnostic:result?.diagnostic||null});return result; }
    catch(error){record({kind:'lead-finish',id,wallMs:performance.now()-started,error:error.message});throw error;} },
  isProcessAlive:runner.isProcessAlive, killWorker:runner.killWorker, abortAsk:runner.abortAsk });
server.listen(0,'127.0.0.1',()=>process.send?.({base:'http://127.0.0.1:'+server.address().port}));
function close(){clearInterval(retirement);runner.abortAsk();retireWorkers();for(const pid of workers.keys())if(runner.isProcessAlive(pid))runner.killWorker(pid);server.closeAllConnections?.();server.close(()=>process.exit(0));}
process.on('message',message=>{if(message==='close')close();});
process.on('disconnect',close);
