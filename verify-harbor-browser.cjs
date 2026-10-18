'use strict';
// Real browser + real isolated backend. Only worker transport/liveness is mocked.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');
const { spawn } = require('node:child_process');
const { createServer } = require('./server.cjs');
const { findBrowser } = require('./planwharf.cjs');
const out = process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'harbor-v2-' + Date.now());
fs.mkdirSync(out, { recursive: true });
const personalHash = () => crypto.createHash('sha256').update(fs.readFileSync(path.join(__dirname, 'plan.json'))).digest('hex');
const before = personalHash(), observations = [], errors = [], networkErrors = [], requests = [];
const seed = require('./fixtures/starter-notebook.json').projects[0];
const plan = { schema: 2, projects: ['harbor-alpha', 'harbor-beta'].map(id => ({ ...structuredClone(seed), project: { id, name: id, folder: path.join(out, id) }, tasks: [] })) };
for (const item of plan.projects) fs.mkdirSync(item.project.folder);
const planFile = path.join(out, 'notebook.json'); fs.writeFileSync(planFile, JSON.stringify(plan));
const launches = [], live = new Set(); let pid = 86000, aiCalls = 0;
const models = [{ id: 'harbor-fixture', label: 'Harbor fixture worker', provider: 'codex', cli: 'gpt-6-luna', kind: 'default', enabled: true, available: true }];
const server = createServer({ planFile, attemptsDir: path.join(out, 'attempts'), models, fetchPlans: null, skillsEnabled: false, refreshModels: false, monitorIntervalMs: 60000,
  inspectFolder: folder => ({ folder, git: false, names: [], fileCount: 0, dirty: [] }),
  spawnWorker: info => { const id = ++pid; launches.push({ ...info, pid: id }); live.add(id); return { pid: id }; },
  isProcessAlive: id => live.has(id), killWorker: id => live.delete(id), askModel: info => { assert.equal(info.purpose, 'supervisor'); assert.equal(info.allowActions, false); aiCalls++; return { answer: 'Fixture Reviewer advice: inspect the evidence before deciding.', provider: 'codex', model: 'gpt-6-luna' }; }
});
let unavailable = false;
// Production /orchestrator.html still redirects to the unified view. This extra
// test-only route also exercises the retained HTML, without changing app routing.
const proxy = http.createServer((req, res) => {
  if (req.url === '/standalone-orchestrator.html') { res.writeHead(200, { 'Content-Type': 'text/html' }); res.end(fs.readFileSync(path.join(__dirname, 'orchestrator.html'))); return; }
  if (unavailable && req.url === '/api/orchestrator') { res.writeHead(503, { 'Content-Type': 'application/json' }); res.end('{"error":"Isolated outage"}'); return; }
  const headers = { ...req.headers, host: `127.0.0.1:${server.address().port}` }; if (headers.origin) headers.origin = 'http://' + headers.host;
  const upstream = http.request({ hostname: '127.0.0.1', port: server.address().port, path: req.url, method: req.method, headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
  upstream.on('error', e => { res.writeHead(502); res.end(e.message); }); req.pipe(upstream);
});
let browser, ws, cdp, evaluate, base, acceptStopConfirmation = false;
const pause = ms => new Promise(r => setTimeout(r, ms));
async function until(check, label, timeout = 16000) { const end = Date.now() + timeout; while (Date.now() < end) { if (await check()) return; await pause(80); } throw Error('Timed out: ' + label); }
async function request(route, data, status = 200) {
  const r = await fetch(base + route, data === undefined ? {} : { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data) }); const result = await r.json(); assert.equal(r.status, status, JSON.stringify(result)); return result;
}
async function click(selector) {
  const box = await evaluate(`(() => {const e=document.querySelector(${JSON.stringify(selector)});e.scrollIntoView({block:'center'});const r=e.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()`);
  await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', ...box, button: 'left', clickCount: 1 }); await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', ...box, button: 'left', clickCount: 1 });
}
async function shot(name) { await evaluate('window.scrollTo(0,0)'); const image = await cdp('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false }); fs.writeFileSync(path.join(out, name + '.png'), Buffer.from(image.data, 'base64')); }
async function state(expected) {
  await until(() => evaluate(`document.getElementById('harbor-background')?.dataset.state === ${JSON.stringify(expected)}`), expected);
  const id = await evaluate("document.getElementById('harbor-background').dataset.project");
  const row = (await request('/api/orchestrator?readOnly=1')).projects.find(r => r.id === id);
  observations.push({ expected, projectId: id, backendStatus: row?.status, live: row?.workerAlive, pauseRequested: row?.megaTask?.pauseRequested });
}
async function select(id, standalone = false) {
  await evaluate(`(() => {const e=document.getElementById('${standalone ? 'orch-project' : 'project-select'}');e.value=${JSON.stringify(id)};e.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  await until(() => evaluate(`document.getElementById('harbor-background').dataset.project===${JSON.stringify(id)}`), 'project selection');
}
async function settings(next) {
  await click('#harbor-settings-button');
  assert.equal(await evaluate("document.getElementById('harbor-dialog').open"), true);
  await evaluate(`(() => {const f=document.getElementById('harbor-settings-form');for(const [k,v] of Object.entries(${JSON.stringify(next)})){if(typeof v==='boolean')f.elements[k].checked=v;else f.elements[k].value=v;}f.elements.flag.dispatchEvent(new Event('change'));})()`);
  const preview = await evaluate("document.querySelector('[data-flag-preview] svg').getBoundingClientRect().toJSON()"); assert.ok(preview.width > 50 && preview.height > 30);
  await click('#harbor-settings-form button[type=submit]'); assert.equal(await evaluate("document.getElementById('harbor-dialog').open"), false);
}
async function supervisorSettings(next) {
  await evaluate("document.querySelector('.supervisor-preferences').open=true");
  for(const [key,value] of Object.entries(next)) {
    if(key==='name'){await click('#supervisor-settings-form [name=name]');await evaluate("document.querySelector('#supervisor-settings-form [name=name]').select()");await cdp('Input.insertText',{text:value});}
    else await evaluate(`(() => {const e=document.querySelector('#supervisor-settings-form [name=${key}]');e.value=${JSON.stringify(value)};e.dispatchEvent(new Event('change',{bubbles:true}));})()`);
  }
  await click('#supervisor-settings-form button[type=submit]');
  await until(async()=>{const d=(await request('/api/orchestrator?readOnly=1')).supervisor;return Object.entries(next).every(([k,v])=>d[k]===(k==='name'?(v.trim()||'Supervisor'):v));},'Supervisor settings saved');
  await until(()=>evaluate("document.getElementById('supervisor-error').hidden"),'Supervisor settings no error');
}
async function askSupervisor(question,answer) {
  await click('#supervisor-question');await cdp('Input.insertText',{text:question});await click('#supervisor-ask-form button');
  await until(()=>evaluate(`document.getElementById('supervisor-thread').textContent.includes(${JSON.stringify(answer)})`),'Supervisor answer');
}
async function viewport(width, height, scale = 1) { await cdp('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: scale, mobile: width < 600 }); await pause(100); }
async function contrast() {
  const values = await evaluate(`(() => {
    const ctx=document.createElement('canvas').getContext('2d');
    const rgb=color=>{ctx.clearRect(0,0,1,1);ctx.fillStyle=color;ctx.fillRect(0,0,1,1);return [...ctx.getImageData(0,0,1,1).data];};
    const scene=getComputedStyle(document.getElementById('harbor-background'));
    const colors=['--harbor-sky','--harbor-cabin','--harbor-line','--harbor-hull','--harbor-pier','--harbor-rope'].map(key=>rgb(scene.getPropertyValue(key)));
    // Include the brightest local flag artwork as a conservative backdrop.
    colors.push(rgb('#f5e8ba'));
    const panels=[...document.querySelectorAll('.home-goal,.orch-chat,.orch-board')].filter(e=>e.checkVisibility()).map(e=>{
      const css=getComputedStyle(e);return {selector:e.className,surface:rgb(css.backgroundColor),text:['--ink','--muted','--soft'].map(key=>({key,rgb:rgb(css.getPropertyValue(key))}))};
    });return {colors,panels,theme:document.documentElement.dataset.theme};
  })()`);
  const lum = rgb => rgb.slice(0,3).map(c=>c/255).map(c=>c<=.04045?c/12.92:((c+.055)/1.055)**2.4).reduce((sum,c,i)=>sum+c*[.2126,.7152,.0722][i],0);
  for(const panel of values.panels)for(const text of panel.text){
    const ratios=values.colors.map(behind=>{const alpha=panel.surface[3]/255,bg=behind.map((c,i)=>i<3?panel.surface[i]*alpha+c*(1-alpha):255);const a=lum(bg),b=lum(text.rgb);return (Math.max(a,b)+.05)/(Math.min(a,b)+.05);});
    const minimum=Math.min(...ratios);assert.ok(minimum>=4.5,values.theme+' '+panel.selector+' '+text.key+' contrast '+minimum);observations.push({contrast:{theme:values.theme,panel:panel.selector,text:text.key,minimum}});
  }
}
async function geometry() {
  const result = await evaluate(`(() => {const bg=document.getElementById('harbor-background');const r=bg.getBoundingClientRect();const ship=document.querySelector('.harbor-ship').getBoundingClientRect();const pier=document.querySelector('.harbor-pier').getBoundingClientRect();return {bg:r.toJSON(),ship:ship.toJSON(),pier:pier.toJSON(),w:document.documentElement.clientWidth,h:innerHeight,position:getComputedStyle(bg).position,pointer:getComputedStyle(bg).pointerEvents,inert:bg.inert,hidden:bg.getAttribute('aria-hidden'),overflow:document.documentElement.scrollWidth>innerWidth,focusable:bg.querySelectorAll('button,input,select,a,[tabindex]').length};})()`);
  assert.equal(result.position, 'fixed'); assert.equal(result.pointer, 'none'); assert.equal(result.inert, true); assert.equal(result.hidden, 'true'); assert.equal(result.focusable, 0);
  assert.equal(result.bg.x, 0); assert.equal(result.bg.y, 0); assert.equal(result.bg.width, result.w); assert.equal(result.bg.height, result.h);
  assert.equal(result.overflow, false); assert.ok(result.ship.right > 0 && result.ship.left < result.w && result.ship.bottom > 0 && result.ship.top < result.h, 'Vessel intersects viewport');
  assert.ok(result.pier.right > 0 && result.pier.left < result.w, 'Pier intersects viewport');
  observations.push({ geometry: result });
}
function finish(info, status = 'COMPLETE', keepAlive = false) {
  fs.writeFileSync(info.structuredResultPath, JSON.stringify({ resultId: info.resultId, attemptId: info.attemptId, subTaskId: info.subTaskId, megaTaskId: info.megaTaskId, status, summary: 'Isolated Harbor fixture', filesChanged: [], tests: [], blockers: [], nextAction: 'Review fixture', createdAt: new Date().toISOString() }));
  fs.writeFileSync(info.exitPath, JSON.stringify({ attemptId: info.attemptId, exitCode: status === 'FAILED' ? 1 : 0, provider: info.worker.provider, model: info.worker.cli }));
  fs.writeFileSync(info.resultPath, `STATUS: ${status}\nEXIT: ${status === 'FAILED' ? 1 : 0}\n`); if (!keepAlive) live.delete(info.pid);
}
async function propose(count = 2) { const result = await request('/api/orchestrator/propose', { projectId: 'harbor-alpha', title: 'Harbor background validation', goal: 'Isolated backend lifecycle', subTasks: Array.from({ length: count }, (_, i) => ({ title: 'Slice ' + i, detail: 'Local fixture', assignedModelId: 'harbor-fixture', type: i ? 'AUDIT' : 'IMPLEMENTATION', dependsOn: i ? [i - 1] : [] })) }, 201); return result.snapshot.projects.find(r => r.id === 'harbor-alpha').megaTaskId; }
async function supervisorActions() {
  // Real contextual alert actions on independent parallel work in a second project.
  await select('harbor-beta');
  const parallel=await request('/api/orchestrator/propose',{projectId:'harbor-beta',title:'Supervisor action validation',goal:'Isolated manual actions',subTasks:[{title:'Build fixture',detail:'Fixture',type:'IMPLEMENTATION',assignedModelId:'harbor-fixture',dependsOn:[]},{title:'Review fixture',detail:'Fixture',type:'AUDIT',assignedModelId:'harbor-fixture',dependsOn:[]}]},201);
  const betaMega=parallel.snapshot.projects.find(p=>p.id==='harbor-beta').megaTaskId;
  await request('/api/orchestrator/approve',{projectId:'harbor-beta',megaTaskId:betaMega});await state('running');
  const betaLaunches=launches.filter(l=>l.megaTaskId===betaMega);assert.equal(betaLaunches.length,2,'Independent Worker and Reviewer actually launch');
  finish(betaLaunches[0],'FAILED');await state('running');await click('#supervisor-navigation');
  await until(()=>evaluate("!!document.querySelector('[data-supervisor-action=pause]')"),'contextual pause available');
  assert.equal(await evaluate("document.querySelector('#supervisor-reviewers .badge').textContent"),'running');
  await click('[data-supervisor-action=pause]');await state('pausing');await shot('supervisor-pausing');
  acceptStopConfirmation=true;await click('[data-supervisor-action=stop]');acceptStopConfirmation=false;await state('checkpoint');
  const betaCheckpoint=(await request('/api/execution?projectId=harbor-beta')).checkpoints.at(-1);assert.ok(betaCheckpoint.id);assert.equal(live.has(betaLaunches[1].pid),false,'Contextual Stop reaches actual worker transport');
  observations.push({supervisorActions:{pause:true,stopCheckpoint:betaCheckpoint.id,workerReviewerBreakdown:true}});
  }
async function launchBrowser() {
  const profile = path.join(out, 'edge-profile');
  fs.rmSync(path.join(profile, 'DevToolsActivePort'), {force:true});
  browser = spawn(findBrowser(), ['--headless=new', '--disable-gpu', '--disable-extensions', '--no-first-run', '--no-default-browser-check', '--remote-debugging-port=0', '--user-data-dir=' + profile, 'about:blank', ...(process.env.PLANWHARF_TEST_NO_SANDBOX === '1' ? ['--no-sandbox'] : [])], { windowsHide: true, stdio: 'ignore' }); browser.on('error', e => errors.push(e.message));
  let port; await until(() => {try {port=Number(fs.readFileSync(path.join(profile,'DevToolsActivePort'),'utf8').split('\n')[0]);return port;}catch{}}, 'Edge opens');
  const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); ws = new WebSocket(pages.find(p => p.type === 'page').webSocketDebuggerUrl);
  await new Promise((r,j) => {ws.addEventListener('open',r,{once:true});ws.addEventListener('error',j,{once:true});});
  let sequence=0;const pending=new Map();
  ws.addEventListener('message', e => {const m=JSON.parse(e.data);if(m.method==='Page.javascriptDialogOpening'&&acceptStopConfirmation)void cdp('Page.handleJavaScriptDialog',{accept:true});if(m.method==='Runtime.exceptionThrown')errors.push(m.params.exceptionDetails);if(m.method==='Runtime.consoleAPICalled'&&m.params.type==='error')errors.push(m.params.args);if(m.method==='Network.responseReceived'&&m.params.response.status>=400&&!m.params.response.url.endsWith('/favicon.ico')&&m.params.response.status!==503)networkErrors.push(m.params.response);if(m.method==='Network.requestWillBeSent')requests.push(m.params);if(pending.has(m.id)){const p=pending.get(m.id);pending.delete(m.id);clearTimeout(p.timer);m.error?p.reject(Error(JSON.stringify(m.error))):p.resolve(m.result);}});
  cdp=(method,params={})=>new Promise((resolve,reject)=>{const id=++sequence;const timer=setTimeout(()=>{pending.delete(id);reject(Error('CDP timeout '+method));},15000);pending.set(id,{resolve,reject,timer});ws.send(JSON.stringify({id,method,params}));});
  evaluate=async expression=>{const r=await cdp('Runtime.evaluate',{expression,returnByValue:true,awaitPromise:true});if(r.exceptionDetails)throw Error(JSON.stringify(r.exceptionDetails));return r.result.value;};
  await cdp('Runtime.enable');await cdp('Page.enable');await cdp('Network.enable');
}
(async () => {
  await new Promise(r => server.listen(0, '127.0.0.1', r)); await new Promise(r => proxy.listen(0, '127.0.0.1', r)); base = `http://127.0.0.1:${proxy.address().port}`;
  for (const asset of ['/harbor-ui.js', '/harbor.css', '/supervisor-ui.js', '/supervisor.css', '/assets/harbor/port-dusk.png', '/assets/harbor/vessel.png', '/assets/harbor/vessel-pirate.png', '/assets/harbor/captains-caribbean.png']) assert.equal((await fetch(base + asset)).status, 200, asset + ' actually served');
  await launchBrowser();await viewport(1600,1000);
  await cdp('Page.navigate',{url:base});await until(()=>evaluate("document.getElementById('project-select')?.options.length===2 && document.querySelector('.harbor-flag svg')"),'app loads');
  if(process.env.PLANWHARF_SUPERVISOR_ACTIONS_ONLY==='1'){await supervisorActions();assert.equal(aiCalls,0);assert.deepEqual(errors,[]);assert.deepEqual(networkErrors,[]);observations.push({result:'PASS',scope:'Supervisor contextual actions'});console.log('PASS Supervisor contextual browser actions:',out);return;}
  await select('harbor-alpha');await state('idle');await until(()=>evaluate("document.getElementById('home-progress').textContent && !document.getElementById('home-progress').textContent.startsWith('Loading') && document.getElementById('home-error').hidden"),'home data rendered');await geometry();await contrast();await shot('home-dark-idle');
  const layout=()=>evaluate("[...document.querySelectorAll('.rail,.topbar,#project-view,.home-goal')].map(e=>{const r=e.getBoundingClientRect();return [r.x,r.y,r.width,r.height]})");
  const on=await layout();await settings({enabled:false});assert.equal(await evaluate("document.getElementById('harbor-background').checkVisibility()"),false);assert.equal(await evaluate("getComputedStyle(document.querySelector('.fx')).display !== 'none'"),true);assert.deepEqual(await layout(),on,'Harbor on/off never shifts workspace geometry');
  await cdp('Page.reload');await until(()=>evaluate("document.getElementById('harbor-background')?.hidden===true"),'disabled persists');await settings({enabled:true,animations:'off'});
  await click('#harbor-settings-button');assert.equal(await evaluate("document.querySelector('#harbor-settings-form [name=supervisorName]')===null"),true);await click('[data-harbor-cancel]');
  await click('#supervisor-navigation');await supervisorSettings({name:'   '});assert.equal(await evaluate("document.getElementById('supervisor-title').textContent"),'Supervisor');
  await supervisorSettings({name:'Captain <Nemo> & Co'});await cdp('Page.reload');await until(()=>evaluate("document.getElementById('supervisor-title')?.textContent==='Captain <Nemo> & Co'"),'Supervisor name reload');
  assert.equal(await evaluate("document.getElementById('supervisor-title').querySelector('script')===null"),true);
  await askSupervisor('What is happening?','Live evidence');assert.equal(aiCalls,0);
  await supervisorSettings({modelId:'harbor-fixture'});await askSupervisor('What do you suggest?','Fixture Reviewer advice');assert.equal(aiCalls,1);
  await supervisorSettings({modelId:''});await shot('supervisor-idle');await click('[data-return-project]');
  for(const flag of ['classic','tech','exploration','merchant','pirate']){await settings({flag});assert.equal(await evaluate("document.getElementById('harbor-background').dataset.flag"),flag);assert.ok(await evaluate("document.querySelector('.harbor-flag svg').getBoundingClientRect().width>20"));}
  await settings({flag:'random',effects:false});const random=await evaluate("document.getElementById('harbor-background').dataset.flag");await cdp('Page.reload');await until(()=>evaluate("document.querySelector('.harbor-flag svg')"),'random reload');assert.equal(await evaluate("document.getElementById('harbor-background').dataset.flag"),random);assert.equal(await evaluate("getComputedStyle(document.querySelector('.harbor-atmosphere')).display"),'none');
  const noMotion=()=>evaluate("[...document.querySelectorAll('#harbor-background *')].every(e=>getComputedStyle(e).animationName==='none' && parseFloat(getComputedStyle(e).transitionDuration)===0)");assert.equal(await noMotion(),true);
  await settings({animations:'reduced'});assert.equal(await noMotion(),true);await settings({animations:'full',effects:true});await cdp('Emulation.setEmulatedMedia',{features:[{name:'prefers-reduced-motion',value:'reduce'}]});assert.equal(await noMotion(),true);await cdp('Emulation.setEmulatedMedia',{features:[]});
  const backgroundTab=await cdp('Target.createTarget',{url:'about:blank',background:false});
  await until(()=>evaluate("document.hidden && document.getElementById('harbor-background').dataset.suspended==='true'"),'hidden window pauses Harbor');
  assert.equal(await evaluate("getComputedStyle(document.querySelector('.harbor-ship')).animationPlayState"),'paused');
  await cdp('Page.bringToFront');await until(()=>evaluate("!document.hidden && document.getElementById('harbor-background').dataset.suspended==='false'"),'visible window resumes Harbor');
  await cdp('Target.closeTarget',{targetId:backgroundTab.targetId});observations.push({visibilitySuspension:true});
  await click('#theme-toggle');await contrast();await shot('home-light-idle');assert.equal(await evaluate("document.documentElement.dataset.theme"),'light');
  // Real text input, note submission, existing dialog and project details controls.
  await click('#home-note-text');await cdp('Input.insertText',{text:'Harbor input must remain usable.'});await click('#home-note-add');await until(()=>evaluate("document.getElementById('home-notes').textContent.includes('Harbor input must remain usable.')"),'note saved through UI');
  await click('#new-revision');assert.equal(await evaluate("document.getElementById('revision-dialog').open"),true);await click('#cancel-dialog');
  await click('#project-details > summary');assert.equal(await evaluate("document.getElementById('project-details').open"),true);
  const hit=await evaluate("(() => {const e=document.getElementById('new-revision'),r=e.getBoundingClientRect();return document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.closest('button')?.id;})()");assert.equal(hit,'new-revision');
  const megaTaskId=await propose();await state('preparing');await request('/api/orchestrator/approve',{projectId:'harbor-alpha',megaTaskId});await state('running');
  const start=await evaluate("getComputedStyle(document.querySelector('.harbor-route')).transform");await pause(700);const moved=await evaluate("getComputedStyle(document.querySelector('.harbor-route')).transform");assert.notEqual(start,moved,'Vessel actually departs instead of only changing a label');
  await shot('home-light-running');await click('#watch-window');assert.equal(await evaluate("!document.getElementById('orchestrator-view').hidden"),true);await geometry();await contrast();await shot('orchestrator-light-running');await click('#theme-toggle');await contrast();await shot('orchestrator-dark-running');
  const backgroundRequests=requests.filter(r=>r.request.url.includes('/api/')&&r.initiator.stack?.callFrames?.some(f=>f.url.endsWith('/harbor-ui.js')));assert.equal(backgroundRequests.length,0,'Harbor reuses existing polling, no new API requests');
  await select('harbor-beta');await state('idle');await select('harbor-alpha');await state('running');
  assert.deepEqual(errors,[],'No JavaScript errors before the intentional outage');
  unavailable=true;await state('neutral');unavailable=false;await state('running');
  // Expected outage console entries are assessed separately from unexpected errors.
  assert.deepEqual(errors,[],'Expected HTTP outage must not cause JavaScript exceptions');observations.push({expectedOutage:true});
  await request('/api/orchestrator/pause',{projectId:'harbor-alpha',megaTaskId});await state('pausing');finish(launches.at(-1));await state('checkpoint');
  const cp=(await request('/api/execution?projectId=harbor-alpha')).checkpoints.at(-1);await request('/api/orchestrator/resume',{projectId:'harbor-alpha',megaTaskId,checkpointId:cp.id});await state('resuming');await state('running');
  await request('/api/orchestrator/stop',{projectId:'harbor-alpha',megaTaskId});await state('checkpoint');const stopped=(await request('/api/execution?projectId=harbor-alpha')).checkpoints.at(-1);await request('/api/orchestrator/resume',{projectId:'harbor-alpha',megaTaskId,checkpointId:stopped.id});await state('resuming');await state('running');
  finish(launches.at(-1),'COMPLETE',true);await pause(4500);assert.equal(await evaluate("document.getElementById('harbor-background').dataset.state"),'running','Result text/file cannot complete a live worker');live.delete(launches.at(-1).pid);await state('completed');await shot('orchestrator-completed');
  const failed=await propose(1);await state('preparing');await request('/api/orchestrator/approve',{projectId:'harbor-alpha',megaTaskId:failed});await state('running');finish(launches.at(-1),'FAILED');await state('failed');
  await until(()=>evaluate("!document.getElementById('supervisor-notice').hidden"),'real-time anomaly notice');await click('[data-open-supervisor]');
  await until(()=>evaluate("document.querySelector('[data-supervisor-action=inspect]')"),'Supervisor failure alert');await shot('supervisor-failure');
  await click('[data-supervisor-action=inspect]');assert.equal(await evaluate("document.getElementById('supervisor-evidence').open"),true);await click('#supervisor-evidence button');
  const failures=(await request('/api/orchestrator?readOnly=1')).projects.find(p=>p.id==='harbor-alpha').status;
  for(let i=0;i<8 && await evaluate("!!document.querySelector('[data-supervisor-action=ignore]')");i++){const id=await evaluate("document.querySelector('[data-supervisor-action=ignore]').dataset.alert");await click('[data-supervisor-action=ignore]');await until(()=>evaluate(`!document.querySelector('[data-supervisor-action=ignore][data-alert="${id}"]')`),'specific alert ignored');}assert.equal(await evaluate("!!document.querySelector('[data-supervisor-action=ignore]')"),false);
  assert.equal((await request('/api/orchestrator?readOnly=1')).projects.find(p=>p.id==='harbor-alpha').status,failures,'Ignore cannot alter execution');
  await cdp('Page.reload');await until(()=>evaluate("document.getElementById('supervisor-summary')?.textContent.includes('harbor-alpha')"),'Supervisor reload');
  assert.equal(await evaluate("document.querySelector('[data-supervisor-action=ignore]')===null"),true);await click('#supervisor-show-ignored');assert.equal(await evaluate("!!document.querySelector('.supervisor-alert.is-ignored')"),true);
  await select('harbor-beta');await until(()=>evaluate("document.getElementById('supervisor-thread').textContent.includes('Ask about progress')"),'project-scoped chat');assert.equal(await evaluate("document.querySelector('.supervisor-alert')===null"),true);await select('harbor-alpha');await click('[data-return-orchestrator]');
  await supervisorActions();
  await click('[data-return-orchestrator]');await select('harbor-alpha');await state('failed');
  for(const [w,h] of [[1280,720],[900,900],[390,844]]){await viewport(w,h);await geometry();await shot('orchestrator-'+w);}
  await viewport(800,600,2);await geometry(); // Narrow CSS viewport at 2x density.
  await cdp('Emulation.setPageScaleFactor',{pageScaleFactor:1.5});assert.equal(await evaluate("document.documentElement.scrollWidth<=innerWidth"),true);await cdp('Emulation.setPageScaleFactor',{pageScaleFactor:1});
  await cdp('Page.navigate',{url:base+'/orchestrator.html'});await until(()=>evaluate("location.hash==='#orchestrator' && document.getElementById('harbor-background')"),'legacy Orchestrator link');
  await cdp('Page.navigate',{url:base+'/standalone-orchestrator.html'});await until(()=>evaluate("document.getElementById('orch-project')?.options.length===3"),'retained standalone HTML');await select('harbor-alpha',true);await state('failed');await geometry();await shot('standalone-mobile');
  await viewport(390,844);await cdp('Page.navigate',{url:base});await until(()=>evaluate("document.getElementById('home-now')?.textContent && !document.getElementById('home-now').textContent.startsWith('Loading')"),'mobile home content');await geometry();await shot('home-mobile');
  await settings({flag:'merchant',animations:'off',enabled:true});await click('#supervisor-navigation');await supervisorSettings({name:'Persistent Captain'});await click('[data-return-project]');
  await cdp('Browser.close');await until(()=>browser.exitCode!==null,'browser closes');ws.close();
  await launchBrowser();await viewport(1280,900);await cdp('Page.navigate',{url:base});await until(()=>evaluate("document.querySelector('.harbor-flag svg')"),'app restarts');
  await click('#harbor-settings-button');assert.equal(await evaluate("document.getElementById('supervisor-title').textContent"),'Persistent Captain');assert.equal(await evaluate("document.querySelector('[name=flag]').value"),'merchant');await click('[data-harbor-cancel]');await shot('browser-restarted');observations.push({restartPersistence:true});
  assert.equal(aiCalls,1);assert.deepEqual(errors,[]);assert.deepEqual(networkErrors,[]);observations.push({result:'PASS',aiCalls});console.log('PASS Harbor full-background browser acceptance:',out);
})().catch(async e=>{console.error(e);process.exitCode=1;observations.push({result:'FAIL',error:e.stack});if(evaluate){try{observations.push({dom:await evaluate("({url:location.href,state:document.getElementById('harbor-background')?.dataset,dialogs:[...document.querySelectorAll('dialog[open]')].map(e=>e.id)})")});await shot('failure');}catch{}}}).finally(async()=>{
  ws?.close();browser?.kill();if(proxy.listening)await new Promise(r=>proxy.close(r));if(server.listening)await new Promise(r=>server.close(r));
  fs.writeFileSync(path.join(out,'results.json'),JSON.stringify({observations,errors,networkErrors,personalNotebookUnchanged:personalHash()===before},null,2));assert.equal(personalHash(),before,'Personal notebook unchanged');
});
