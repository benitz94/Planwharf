'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),os=require('node:os'),vm=require('node:vm');
test('background shutdown retires completed native metadata PIDs and terminates only pending owned attempts',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'pw-benchmark-host-'));
  try{
    const configPath=path.join(root,'config.json');fs.writeFileSync(configPath,JSON.stringify({model:'gpt-fixture',maxCalls:3,callsFile:path.join(root,'calls.json')}));
    const listeners={},killed=[];let options,pid=100;
    const fakeRunner={spawnWorker:()=>({pid:++pid,logPath:path.join(root,'log')}),isProcessAlive:()=>true,killWorker:id=>killed.push(id),abortAsk:()=>{}};
    const server={listen:()=>{},closeAllConnections:()=>{},close:callback=>callback()};
    const processFixture={argv:['node','host',configPath],on:(event,fn)=>listeners[event]=fn,exit:()=>{}};
    const imports=name=>name==='node:fs'?fs:name==='node:path'?path:name==='../../server.cjs'?{createServer:info=>(options=info,server)}:name==='../../engine/runner.cjs'?fakeRunner:(()=>{throw Error('Unexpected import '+name);})();
    vm.runInNewContext(fs.readFileSync(path.join(__dirname,'host.cjs'),'utf8'),{require:imports,process:processFixture,performance,setInterval:()=>({unref(){}}),clearInterval:()=>{}});
    const worker={cli:'gpt-fixture',provider:'codex'};
    const finished=path.join(root,'finished.json'),pending=path.join(root,'pending.json');
    options.spawnWorker({worker,attemptId:'finished',exitPath:finished});
    options.spawnWorker({worker,attemptId:'pending',exitPath:pending});
    fs.writeFileSync(finished,JSON.stringify({attemptId:'finished',exitCode:0}));
    // PID 101 now looks alive but might have been reused; its matching exit retires it.
    listeners.message('close');assert.deepEqual(killed,[102]);
    assert.throws(()=>options.spawnWorker({worker:{cli:'gpt-other',provider:'codex'}}),/substitution/);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
