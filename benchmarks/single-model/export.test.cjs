'use strict';
const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {exportStudy}=require('./export.cjs');
test('publication excludes source workspaces and symlinks, redacts parsed and raw receipts, and preserves outcomes',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'pw-bench-export-'));
  try{
    const source=path.join(root,'private'),destination=path.join(root,'public');fs.mkdirSync(path.join(source,'private','trial','workspace'),{recursive:true});
    fs.writeFileSync(path.join(source,'manifest-private.json'),JSON.stringify({environment:{authStatus:'C:\\\\Users\\\\Somebody\\\\.codex\\\\tmp'},secret:'credential'}));
    fs.writeFileSync(path.join(source,'runs.json'),JSON.stringify([{id:'a',taskId:'config',pairId:'1',arm:'direct',outcome:'blocked',wallMs:12}]));
    fs.writeFileSync(path.join(source,'private','trial','workspace','secret.txt'),'must not publish workspace');
    fs.writeFileSync(path.join(source,'private','trial','stderr.log'),'Bearer secretValue C:\\Users\\Somebody\\profile');
    fs.writeFileSync(path.join(source,'private','trial','stdout.jsonl'),JSON.stringify({type:'event',text:'C:\\Users\\Somebody\\profile',nested:JSON.stringify({url:'https://example.com/file?sig=private'})})+'\n');
    fs.writeFileSync(path.join(source,'private','trial','receipt.json'),JSON.stringify({access_token:'private',inputTokens:20}));
    const acceptance=path.join(source,'private','trial','acceptance','corrupt-store-fixture');
    fs.mkdirSync(acceptance,{recursive:true});
    fs.writeFileSync(path.join(acceptance,'store.json'),'{intentionally corrupt');
    fs.writeFileSync(path.join(acceptance,'probe.json'),JSON.stringify({passed:true,stdout:'corruption rejected'}));
    assert.throws(()=>exportStudy(source,path.join(source,'inside')),/outside/);
    exportStudy(source,destination);
    assert.equal(fs.existsSync(path.join(destination,'receipts','trial','workspace')),false);
    const exportedAcceptance=path.join(destination,'receipts','trial','acceptance','corrupt-store-fixture');
    assert.equal(fs.existsSync(path.join(exportedAcceptance,'store.json')),false);
    assert.equal(JSON.parse(fs.readFileSync(path.join(exportedAcceptance,'probe.json'))).passed,true);
    assert.doesNotMatch(fs.readFileSync(path.join(destination,'receipts','trial','stderr.log'),'utf8'),/secretValue|Somebody/);
    assert.deepEqual(JSON.parse(fs.readFileSync(path.join(destination,'receipts','trial','receipt.json'))),{access_token:'[REDACTED]',inputTokens:20});
    const event=JSON.parse(fs.readFileSync(path.join(destination,'receipts','trial','stdout.jsonl'),'utf8').trim());
    assert.equal(event.type,'event');assert.ok(!JSON.stringify(event).includes('Somebody'));
    assert.equal(JSON.parse(event.nested).url,'https://example.com/file?[REDACTED QUERY]');
    assert.equal(JSON.parse(fs.readFileSync(path.join(destination,'runs.json')))[0].outcome,'blocked');
    assert.throws(()=>exportStudy(source,destination),/must be new/);
  }finally{fs.rmSync(root,{recursive:true,force:true});}
});
