'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const { spawn, spawnSync } = require('node:child_process');
const runtime = require('../../engine/provider-runtime.cjs');
const runner = require('../../engine/runner.cjs');
const usageLedger = require('./usage.cjs');
const sha = value => crypto.createHash('sha256').update(value).digest('hex');
function cliEnvironment() {
  // Configure the CLI's existing profile explicitly, without changing the user's profile.
  const profileDir = process.env.CODEX_HOME || path.join(os.homedir(), '.codex');
  return runtime.providerEnvironment('codex', { ...process.env, CODEX_HOME: profileDir });
}
function command(command, args, cwd) {
  const r = spawnSync(command, args, { cwd, windowsHide: true, encoding: 'utf8', timeout: 15000 });
  if (r.status !== 0) throw Error((r.stderr || r.error?.message || 'Command failed').slice(0, 1000));
  return r.stdout.trim();
}
function initializeGit(workspace) {
  command('git', ['init', '--initial-branch=main'], workspace);
  command('git', ['config', 'user.name', 'Benchmark Fixture'], workspace);
  command('git', ['config', 'user.email', 'benchmark@example.invalid'], workspace);
  command('git', ['config', 'core.autocrlf', 'false'], workspace);
  command('git', ['add', '.'], workspace);
  const r = spawnSync('git', ['commit', '-m', 'Identical benchmark starting point'], { cwd: workspace, windowsHide: true, encoding: 'utf8',
    env: { ...process.env, GIT_AUTHOR_DATE: '2000-01-01T00:00:00Z', GIT_COMMITTER_DATE: '2000-01-01T00:00:00Z' } });
  if (r.status !== 0) throw Error(r.stderr);
  return command('git', ['rev-parse', 'HEAD'], workspace);
}
function sourceDigest(directory) {
  const files = [];
  function walk(dir) { for (const item of fs.readdirSync(dir, { withFileTypes: true }).sort((a,b)=>a.name.localeCompare(b.name))) {
    if (['.git', 'node_modules'].includes(item.name)) continue;
    const p = path.join(dir, item.name);
    if (item.isSymbolicLink()) { files.push([path.relative(directory,p),'SYMLINK']); continue; }
    if (item.isDirectory()) walk(p); else files.push([path.relative(directory,p),sha(fs.readFileSync(p))]);
  } }
  walk(directory); return sha(JSON.stringify(files));
}
function usageFromEvents(events) {
  const turns = events.filter(e => e.type === 'turn.completed');
  if (!turns.length || events.some(e=>e.type==='turn.failed')) return null;
  const fields = { inputTokens:'input_tokens', outputTokens:'output_tokens', cachedInputTokens:'cached_input_tokens', reasoningTokens:'reasoning_output_tokens' };
  return Object.fromEntries(Object.entries(fields).map(([key, wire]) => [key, turns.every(e => Number.isSafeInteger(e.usage?.[wire]) && e.usage[wire]>=0) ? turns.reduce((n,e)=>n+e.usage[wire],0) : null]));
}
async function runCli({ workspace, prompt, model, artifactDir, timeoutMs, onStarted, onTick, ephemeral=false, resumeId }) {
  if (!Number.isSafeInteger(timeoutMs) || timeoutMs<=0 || timeoutMs>900000) throw Error('Bounded CLI timeout required (1..900000 ms)');
  if (!/^gpt-[a-zA-Z0-9._-]+$/.test(model)) throw Error('An exact GPT model identity is required');
  const executable = runtime.executable('codex'); if (!executable) throw Error('Codex executable unavailable');
  fs.mkdirSync(artifactDir,{recursive:true});
  const args = ['exec','--json','--sandbox','workspace-write','-c','approval_policy="never"','-c','sandbox_workspace_write.network_access=false','-c','sandbox_workspace_write.writable_roots=[]','-C',workspace,'-m',model];
  if(ephemeral)args.push('--ephemeral');
  if(resumeId)args.push('resume',resumeId);
  args.push('-');
  fs.writeFileSync(path.join(artifactDir,'command.json'),JSON.stringify({executable,args,promptHash:sha(prompt)},null,2));
  fs.writeFileSync(path.join(artifactDir,'prompt.txt'),prompt);
  const started = performance.now(), events=[], diagnostics=[]; let pending='',bytes=0,timedOut=false,interrupted=false;
  return new Promise(resolve=>{
    const stdout=fs.createWriteStream(path.join(artifactDir,'stdout.jsonl'));
    const stderr=fs.createWriteStream(path.join(artifactDir,'stderr.log'));
    const child=spawn(executable,args,{cwd:workspace,env:cliEnvironment(),windowsHide:true,stdio:['pipe','pipe','pipe']});
    const stop=()=>{ if(child.pid) return runner.killWorker(child.pid); return false; };
    let forced;
    const timer=setTimeout(()=>{timedOut=true;stop();forced=setTimeout(()=>{if(child.exitCode===null)child.kill('SIGKILL');},2000);},timeoutMs);
    const tick=onTick?setInterval(()=>{try{if(onTick({pid:child.pid,events,elapsedMs:performance.now()-started})){interrupted=true;stop();}}catch(e){diagnostics.push(e.message);}},300):null;
    child.once('spawn',()=>onStarted?.({pid:child.pid,stop}));
    child.stdout.on('data',chunk=>{bytes+=chunk.length;if(bytes>16*1024*1024){diagnostics.push('Output limit exceeded');stop();return;}stdout.write(chunk);pending+=chunk.toString();let i;while((i=pending.indexOf('\n'))>=0){const line=pending.slice(0,i);pending=pending.slice(i+1);try{events.push(JSON.parse(line));}catch{}}});
    child.stderr.on('data',chunk=>{bytes+=chunk.length;if(bytes<=16*1024*1024)stderr.write(chunk);else stop();});
    child.on('error',e=>diagnostics.push(e.message));
    child.once('close',(exitCode,signal)=>{clearTimeout(timer);if(forced)clearTimeout(forced);if(tick)clearInterval(tick);if(pending.trim())try{events.push(JSON.parse(pending));}catch{}stdout.end();stderr.end();
      const threadId=events.find(e=>e.type==='thread.started')?.thread_id||null;
      const responseReceipt=usageLedger.readReceipt(threadId,cliEnvironment().CODEX_HOME);
      if(responseReceipt)fs.writeFileSync(path.join(artifactDir,'usage-receipt.json'),JSON.stringify(responseReceipt,null,2)+'\n');
      resolve({responseReceipt,exitCode,signal,wallMs:performance.now()-started,timedOut,interrupted,diagnostics,threadId:events.find(e=>e.type==='thread.started')?.thread_id||null,
        usage:usageFromEvents(events),eventTypes:events.map(e=>e.type),completed:exitCode===0&&events.some(e=>e.type==='turn.completed')&&!events.some(e=>e.type==='turn.failed'||e.type==='error'),failure:events.filter(e=>e.type==='error'||e.type==='turn.failed').map(e=>e.message||e.error?.message||e.type),command:{executable,args},startedAt:new Date(Date.now()-(performance.now()-started)).toISOString()});
    });
    child.stdin.on('error',()=>{});child.stdin.end(prompt);
  });
}
async function environment(model) {
  const executable=runtime.executable('codex'), env=cliEnvironment();
  const status=spawnSync(executable,['login','status'],{env,windowsHide:true,encoding:'utf8',timeout:15000});
  const catalog=await require('../../engine/provider-adapters.cjs').adapterFor('codex').discover();
  let quota;try{quota=await require('../../engine/provider-adapters.cjs').adapterFor('codex').quota();}catch{}
  return {recordedAt:new Date().toISOString(),platform:process.platform,release:os.release(),arch:process.arch,cpu:os.cpus()[0]?.model||null,logicalCpus:os.cpus().length,memoryBytes:os.totalmem(),node:process.version,
    cli:{executable,version:runtime.cliVersion('codex'),authenticated:status.status===0&&/Logged in/.test(status.stderr+status.stdout),authStatus:status.stderr+status.stdout},
    requestedModel:model,modelInAuthenticatedCatalog:catalog.ok===true&&catalog.models.some(m=>m.cli===model),catalogVersion:catalog.cliVersion,providerReportedResolvedVersion:null,
    cliProfile:{source:process.env.CODEX_HOME?'Explicit CODEX_HOME override':'Default existing profile',configurationHash:fs.existsSync(path.join(env.CODEX_HOME,'config.toml'))?sha(fs.readFileSync(path.join(env.CODEX_HOME,'config.toml'))):null,sqliteOverride:process.env.CODEX_SQLITE_HOME||null},
    sandbox:'workspace-write',approvalPolicy:'never',toolNetworkAccess:false,inferenceEffort:'shared CLI/profile default; no arm-specific override',
    configurationDifferences:['Planwharf disables native worker subagents and requests a structured final report; direct CLI retains its ordinary self-planning/test capabilities.','Planwharf uses native persistence/dispatch and one-model role roster; no hand-written decomposition is injected.'],
    quotaSnapshot:quota||null,quotaAttribution:'Shared account activity cannot be attributed per trial; per-trial quota decrement UNKNOWN',billedCost:null,
    docs:'https://learn.chatgpt.com/docs/non-interactive-mode',baseline:command('git',['rev-parse','origin/main'],path.resolve(__dirname,'../..'))};
}
module.exports={sha,cliEnvironment,initializeGit,sourceDigest,runCli,usageFromEvents,environment,command};
