'use strict';
const assert = require('node:assert/strict');
const { technicalEvidence, redact, LIMITS } = require('./activity-evidence.cjs');
const { assignmentSession, pagedProjectActivitySessions } = require('./activity-sessions.cjs');
const codex = { adapter: 'codex' };
const row = {type:'item.completed',timestamp:'2026-10-16T10:00:00Z',item:{type:'command_execution',command:'node --test\n# original command',aggregated_output:'TAP version 13\nok 1 - fixture\n# pass 1\n',exit_code:0,CodeContent:'NEVER-COPY-CODE'}};
const technical=technicalEvidence(JSON.stringify(row),codex);
assert.equal(technical.entries[0].command,row.item.command);
assert.equal(technical.entries[0].output,row.item.aggregated_output);
assert.equal(technical.entries[0].exitCode,0);
assert.equal(technical.entries[0].sourceTimestamp,row.timestamp);
assert.equal(technical.entries[0].provenance,'observed');
assert.equal('validated' in technical.entries[0],false);
assert.doesNotMatch(JSON.stringify(technical),/NEVER-COPY-CODE|Running tests/,'Technical text is source whitelist, not friendly descriptions');
assert.equal(technicalEvidence(JSON.stringify(row),{adapter:'antigravity'}).entries.length,0);
assert.equal(technicalEvidence(JSON.stringify(row),{adapter:'grok'}).entries.length,0);
assert.equal(technicalEvidence(JSON.stringify(row),{adapter:'claude'}).entries.length,0);
const claudeTool = { type: 'assistant', message: { content: [{ type: 'tool_use', name: 'Bash', input: { command: 'node --test --token SECRET-FLAG', content: 'PRIVATE-SOURCE' } }] } };
const claudeEvidence = technicalEvidence(JSON.stringify(claudeTool), { adapter: 'claude' });
assert.equal(claudeEvidence.entries[0].tool, 'Bash'); assert.match(claudeEvidence.entries[0].command, /node --test/);
assert.equal(claudeEvidence.entries[0].provenance, 'observed');
assert.doesNotMatch(JSON.stringify(claudeEvidence), /SECRET-FLAG|PRIVATE-SOURCE|Adapter identity is unknown/);
claudeTool.message.content[0].name = 'Write';
const claudeWrite = technicalEvidence(JSON.stringify(claudeTool), { adapter: 'claude' });
assert.equal(claudeWrite.entries[0].tool, 'Write'); assert.equal(claudeWrite.entries[0].command, undefined);
assert.doesNotMatch(JSON.stringify(claudeWrite), /PRIVATE-SOURCE|node --test/);
assert.equal(technicalEvidence(JSON.stringify(row),{}).entries[0].provenance,'unknown');
const agy={event:'step_update',step_update:{step_type:'tool',state:'DONE',tool_name:'run_command',tool_info:{parameters:{CommandLine:'npm test --token SECRET-FLAG',CodeContent:'PRIVATE-SOURCE'},result:{output:'UNVERIFIED-RESULT'}}}};
const a=technicalEvidence(JSON.stringify(agy),{adapter:'antigravity'});
assert.equal(a.entries[0].state,'DONE');assert.match(a.entries[0].command,/npm test/);
assert.doesNotMatch(JSON.stringify(a),/SECRET-FLAG|PRIVATE-SOURCE|UNVERIFIED-RESULT/);
assert.equal(technicalEvidence(JSON.stringify(agy),codex).entries.length,0);
const writer=structuredClone(agy);writer.step_update.tool_name='write_to_file';writer.step_update.tool_info.parameters={CommandLine:'DO-NOT-TREAT-AS-COMMAND',CodeContent:'SOURCE-CODE',patch:'PATCH-CONTENT'};
assert.doesNotMatch(JSON.stringify(technicalEvidence(JSON.stringify(writer),{adapter:'antigravity'})),/DO-NOT|SOURCE-CODE|PATCH-CONTENT/);
const sensitive='api_key=VALUE-1\npassword="VALUE 2"\nAuthorization: Bearer VALUE-3\nhttps://user:VALUE-4@example.invalid?token=VALUE-5\n-----BEGIN PRIVATE KEY-----\nVALUE-6\n-----END PRIVATE KEY-----\n<script>unsafe</script>\x1b[31m\x1b]0;secret title\x07';
const sanitized=redact(sensitive);
assert.doesNotMatch(sanitized,/VALUE|<script>|\x1b|secret title/);assert.match(sanitized,/\n/);
const nested=JSON.stringify({nested:JSON.stringify({api_key:'NESTED-SECRET',password:'NESTED-PASSWORD'})});
assert.doesNotMatch(redact(nested),/NESTED-/);
assert.doesNotMatch(technicalEvidence('-----BEGIN OPENSSH PRIVATE KEY-----\nRAW-KEY-LINE\n-----END OPENSSH PRIVATE KEY-----\n',codex).entries[0].output,/RAW-KEY-LINE/);
const plain=technicalEvidence('compiler diagnostic line 1\nline 2\n5 passed\n',codex);
assert.equal(plain.entries[0].output,'compiler diagnostic line 1\nline 2\n5 passed\n');
assert.equal(plain.entries[0].provenance,'reported');assert.equal(plain.entries[0].protocol,'plain');
for(const line of ['{"item":',JSON.stringify({type:'unknown',CodeContent:'DO-NOT-DUMP',secret:'NO-DUMP'}),JSON.stringify({type:'item.completed',item:{type:'file_change',changes:[{path:'private.js',diff:'SOURCE-DIFF'}]}})]){
 const limited=technicalEvidence(line,codex);assert.equal(limited.entries.length,0);assert.doesNotMatch(JSON.stringify(limited),/DO-NOT-DUMP|NO-DUMP|SOURCE-DIFF/);assert.ok(limited.warnings.length);
}
const many=technicalEvidence(Array.from({length:100},(_,i)=>JSON.stringify({...row,item:{...row.item,command:'echo '+i}})).join('\n'),codex);
assert.ok(many.entries.length<=LIMITS.entries);assert.ok(Buffer.byteLength(JSON.stringify(many.entries))<=LIMITS.totalBytes+2);assert.equal(many.truncated,true);
const huge=technicalEvidence(JSON.stringify({...row,item:{...row.item,aggregated_output:'diagnostic\n'.repeat(800)}}),codex);
assert.equal(huge.truncated,true);assert.match(huge.entries[0].output,/excerpt truncated/);
assert.equal(technicalEvidence('x'.repeat(LIMITS.tailBytes*2),codex).truncated,true);

// More than one snapshot page: every stored assignment remains reachable and
// logs are read only for the selected page, never to enumerate counts.
const results=Array.from({length:60},(_,i)=>({resultId:'R-a'+i,attemptId:'a'+i,megaTaskId:'m',subTaskId:'s',provider:'codex',cli:'model',assignedModelId:'configured',status:'COMPLETE',exitCode:0,summary:'fixture',filesChanged:[],tests:[],blockers:[],nextAction:'',createdAt:'2026-10-16T10:00:00Z'}));
const execution={megaTasks:[{id:'m',projectId:'p'},{id:'foreign',projectId:'q'}],subTasks:[{id:'s',megaTaskId:'m',type:'IMPLEMENTATION',resultId:'R-a59',status:'COMPLETE'}],results};
const parts=results.map((result,i)=>({...result,modelId:'configured',logPath:'private-log-'+i}));
const history=[...parts.map(part=>({id:'p',dispatch:[part]})),{id:'q',dispatch:[{...parts[0],logPath:'FOREIGN-LOG'}]}];
let reads=[];
const page=(offset,limit=24)=>pagedProjectActivitySessions({projectId:'p',row:{id:'p',dispatch:[parts[59]]},history,execution,offset,limit,readTail:file=>{reads.push(file);return file==='private-log-59'?JSON.stringify(row):'exact source '+file;}});
const seen=new Set();
for(const offset of [0,24,48]){
 reads=[];const p=page(offset);assert.equal(p.total,60);assert.equal(p.offset,offset);assert.equal(p.limit,24);assert.equal(reads.length,p.sessions.length);
 for(const session of p.sessions){assert.equal(session.projectId,'p');assert.ok(!seen.has(session.attemptId));seen.add(session.attemptId);assert.ok(session.events.length<=50);assert.ok(session.technicalEvidence);}
 assert.ok(!reads.includes('FOREIGN-LOG'));assert.doesNotMatch(JSON.stringify(p),/"logPath"|FOREIGN-LOG/);
}
assert.equal(seen.size,60);
reads=[];assert.equal(page(1000).sessions.length,0);assert.equal(reads.length,0);
assert.equal(page(-1,999).offset,0);assert.equal(page(-1,999).limit,24);
assert.equal(page(NaN,NaN).offset,0);assert.equal(page(0,0).limit,1);
const exact=page(0).sessions[0];assert.equal(exact.attemptId,'a59');assert.equal(exact.technicalEvidence.entries[0].command,row.item.command);
const noMix=assignmentSession({projectId:'p',part:{attemptId:'own'},row:{workerTail:'OTHER-ASSIGNMENT-OUTPUT'},tail:'EXACT-ASSIGNMENT-OUTPUT'});
assert.match(noMix.technicalEvidence.entries[0].output,/EXACT-ASSIGNMENT/);assert.doesNotMatch(JSON.stringify(noMix.technicalEvidence),/OTHER-ASSIGNMENT/);
const wrong=pagedProjectActivitySessions({projectId:'p',row:{id:'q',dispatch:[parts[59]]},execution:{},history:[],readTail:()=>{throw Error('Must not read foreign row');}});assert.equal(wrong.total,0);
const injected=structuredClone(execution);injected.results=[{...results[0],logPath:'OTHER-PROJECT-SECRET-LOG'}];
let injectedReads=0;
const resultOnly=pagedProjectActivitySessions({projectId:'p',execution:injected,readTail:()=>{injectedReads++;return 'FOREIGN-SECRET';}});
assert.equal(injectedReads,0,'Worker result-only extra fields never authorize any source log read');
assert.equal(resultOnly.total,1);assert.deepEqual(resultOnly.sessions[0].technicalEvidence.entries,[]);
assert.doesNotMatch(JSON.stringify(resultOnly),/OTHER-PROJECT-SECRET-LOG|FOREIGN-SECRET/);
const trusted=pagedProjectActivitySessions({projectId:'p',history:[{id:'p',dispatch:[parts[0]]}],execution:injected,readTail:file=>{assert.equal(file,parts[0].logPath);return 'trusted exact assignment output';}});
assert.match(trusted.sessions[0].technicalEvidence.entries[0].output,/trusted exact assignment output/);
console.log('PASS: source-whitelisted technical excerpts/provenance/redaction/newlines/limits and exact assignment mapping; 60 retained assignments reachable in bounded project-scoped pages with selected-page-only log reads. Fixtures only.');
