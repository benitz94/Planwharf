'use strict';
const fs=require('node:fs'),path=require('node:path');
const out=path.resolve(process.argv[3]),{url}=JSON.parse(fs.readFileSync(path.join(out,'server.json')));
const {chromium}=require(process.env.CODEX_PRIMARY_RUNTIME_NODE_MODULES?path.join(process.env.CODEX_PRIMARY_RUNTIME_NODE_MODULES,'playwright'):'playwright');
const prompt='Create a complete, locally executable calculator application. It must support addition, subtraction, multiplication, division, decimals, keyboard input, clear/reset, division-by-zero handling, automated tests, and a README explaining how to run it. Keep the application lightweight, usable and independent of paid external services.';
async function req(route,input){const started=Date.now();const response=await fetch(url+route,input===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(input)});const value=await response.json();fs.appendFileSync(path.join(out,'chronology.jsonl'),JSON.stringify({at:new Date().toISOString(),route,status:response.status,elapsedMs:Date.now()-started,input,value})+'\n');if(!response.ok)throw Error(route+': '+JSON.stringify(value));return value;}
async function browser(){const context=await chromium.launchPersistentContext(path.join(out,'browser-profile'),{executablePath:process.env.PLANWHARF_TEST_BROWSER,headless:true,viewport:{width:1440,height:1000}});const page=context.pages()[0]||await context.newPage();page.on('pageerror',e=>fs.appendFileSync(path.join(out,'browser-errors.txt'),e.stack+'\n'));page.on('response',async r=>{if(r.status()>=400)fs.appendFileSync(path.join(out,'http-errors.jsonl'),JSON.stringify({at:new Date().toISOString(),url:r.url(),status:r.status(),body:await r.text().catch(()=>'<unavailable>')})+'\n');});await page.goto(url);await page.waitForFunction(()=>document.body.dataset.orchReady==='1');return {context,page};}
(async()=>{const stage=process.argv[2];const app=await req('/api/app');if(path.resolve(app.planFile)!==path.join(out,'notebook.json'))throw Error('Refusing a non-audit notebook');if(stage==='init'){
  for(const role of ['orchestrator','worker','reviewer','supervisor'])await req('/api/models/roles/bulk',{role,enabled:false});
  await req('/api/models/roles',{id:'gpt-6-luna',roles:{orchestrator:true,worker:true,reviewer:true,supervisor:false}});
  await req('/api/providers/policy',{id:'codex',policy:{alwaysApprove:false,minRemainingPercent:20,warningRemainingPercent:30,fallbackProviderId:null}});
  await req('/api/orchestrator/lead',{id:'gpt-6-luna'});
  const {context,page}=await browser();try{
    await page.screenshot({path:path.join(out,'01-startup.png')});
    console.log((await page.locator('body').innerText()).slice(0,1400));
    console.log(await page.locator('#project-dialog input,#project-dialog textarea').evaluateAll(es=>es.map(e=>({name:e.name,id:e.id,type:e.type}))));
  }finally{await context.close();}
}else if(stage==='select-default'){
  await req('/api/models/roles',{id:'gpt-6-luna',roles:{orchestrator:false,worker:false,reviewer:false,supervisor:false}});
  await req('/api/models/roles',{id:'gpt-6.1-sol',roles:{orchestrator:true,worker:true,reviewer:true,supervisor:false}});
  await req('/api/orchestrator/lead',{id:'gpt-6.1-sol'});console.log('Explicitly selected installed CLI default gpt-6.1-sol; previous rejected model disabled.');
}else if(stage==='create'){
  const folder=path.join(out,'Planwharf-Dummy-Calculator');fs.mkdirSync(folder,{recursive:true});
  const {context,page}=await browser();try{
    await page.getByRole('button',{name:'Add project',exact:true}).first().click();
    await page.locator('#project-form [name=name]').fill('Planwharf-Dummy-Calculator');
    await page.locator('#project-form [name=direction]').fill(prompt);
    await page.locator('#project-form [name=folder]').fill(folder);
    await page.locator('#project-form button[type=submit]').click();
    await page.waitForFunction(()=>!document.querySelector('#project-dialog').open);
    const {plan}=await req('/api/plan');const project=plan.projects.find(p=>p.project.name==='Planwharf-Dummy-Calculator');
    fs.writeFileSync(path.join(out,'calculator-project.json'),JSON.stringify(project.project,null,2));
    await page.screenshot({path:path.join(out,'02-project-created.png')});
    console.log(JSON.stringify(project.project));
  }finally{await context.close();}
}else if(stage==='ask'){
  const project=JSON.parse(fs.readFileSync(path.join(out,'calculator-project.json')));const {context,page}=await browser();try{
    await page.locator('#project-select').selectOption(project.id);
    await page.getByRole('button',{name:'Workspace',exact:true}).click();
    await page.locator('#dashboard-orch-ask-input').fill(prompt+' Use only this linked project folder. Do not install dependencies, call external services, or spawn subagents. Propose at most two implementation/review steps and wait for approval.');
    const answered=page.waitForResponse(r=>r.url().endsWith('/api/orchestrator/ask')&&r.request().method()==='POST',{timeout:200000});
    await page.screenshot({path:path.join(out,'03-send-covered-by-soundtrack.png')});
    try{await page.locator('#dashboard-orch-ask-submit').click({timeout:2500});}catch(error){
      fs.writeFileSync(path.join(out,'send-overlap.json'),JSON.stringify({error:error.message,geometry:await page.locator('#dashboard-orch-ask-submit').evaluate(e=>{const r=e.getBoundingClientRect();return {rect:r.toJSON(),hit:document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)?.outerHTML};})},null,2));
      await page.locator('#dashboard-orch-ask-submit').focus();await page.keyboard.press('Enter');
    }
    const response=await answered;fs.writeFileSync(path.join(out,'natural-language-response.json'),JSON.stringify(await response.json(),null,2));
    await page.screenshot({path:path.join(out,'03-natural-language-answer.png')});
    fs.writeFileSync(path.join(out,'natural-language-ui.txt'),await page.locator('body').innerText());
    console.log((await page.locator('#dashboard-orch-ask-thread').innerText()).slice(-4500));
  }finally{await context.close();}
}else if(stage==='snapshot'){const s=await req('/api/orchestrator?readOnly=1');console.log(JSON.stringify({projects:s.projects?.map(p=>({id:p.id,name:p.name,status:p.status,next:p.next,doing:p.doing,megaTaskId:p.megaTaskId,dispatch:p.dispatch?.map(d=>({attemptId:d.attemptId,alive:d.alive,activity:d.activity}))})),usage:s.invocations},null,2));}
})().catch(e=>{console.error(e);process.exitCode=1;});
