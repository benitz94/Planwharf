'use strict';
// Explicitly opt-in: create an isolated audit notebook and run the stock application.
const fs=require('node:fs'),path=require('node:path'),{execFileSync}=require('node:child_process');
if(!process.argv[2])throw Error('Usage: node scripts/audit/start.cjs NEW_DISPOSABLE_DIRECTORY');
const root=path.resolve(__dirname,'../..'),out=path.resolve(process.argv[2]),file=path.join(out,'notebook.json');
fs.mkdirSync(out,{recursive:true});
if(!fs.existsSync(file)){const Plan=require(path.join(root,'plan.js')),seed=Plan.example();seed.project={id:'audit-control',name:'Audit Control',folder:path.join(out,'control')};fs.mkdirSync(seed.project.folder,{recursive:true});seed.tasks=[];seed.revisions.at(-1).open=[];seed.revisions.at(-1).direction='Disposable audit control project.';seed.revisions.at(-1).state='Audit setup only.';fs.writeFileSync(file,JSON.stringify({schema:2,projects:[seed]},null,2));}
if(!JSON.parse(fs.readFileSync(file)).projects.every(p=>p.project.id.startsWith('audit-')||p.project.id.startsWith('planwharf-dummy-calculator')))throw Error('Refusing a notebook containing unrelated projects.');
fs.writeFileSync(path.join(out,'environment.json'),JSON.stringify({node:process.version,platform:process.platform,architecture:process.arch,baseline:execFileSync('git',['rev-parse','HEAD'],{cwd:root,encoding:'utf8'}).trim(),startedAt:new Date().toISOString()},null,2));
require(path.join(root,'planwharf.cjs')).main(['node','planwharf.cjs','--plan',file,'--attempts-dir',path.join(out,'attempts'),'--port','0','--no-window']).then(({url})=>{fs.writeFileSync(path.join(out,'server.json'),JSON.stringify({url,pid:process.pid}));console.log('AUDIT_READY '+url);}).catch(error=>{console.error(error.message);process.exitCode=1;});
