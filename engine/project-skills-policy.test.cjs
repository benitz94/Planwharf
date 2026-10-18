'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Plan = require('../plan.js');
const { createServer } = require('../server.cjs');

test('activation requires project registration, download and documentation even with a global cache and saved flags', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-project-policy-'));
  const planFile = path.join(dir, 'notebook.json');
  const projects = ['alpha','beta'].map(id => {
    const item = Plan.example(); item.project = { id, name: id, folder: path.join(dir,id) }; item.tasks = [];
    fs.mkdirSync(item.project.folder); return item;
  });
  fs.writeFileSync(planFile, JSON.stringify({ schema: 2, projects }));
  const agents = path.join(projects[0].project.folder, '.agents');
  const skillFile = path.join(agents, 'skills', 'grilling', 'SKILL.md');
  fs.mkdirSync(path.dirname(skillFile), { recursive: true });
  const calls = [];
  const options = { planFile, models: [{ id: 'fixture-lead', provider: 'codex', cli: 'gpt-6-luna', kind: 'default', enabled: true, available: true }], refreshModels: false, fetchPlans: null,
    skills: { snapshot: () => ({ skillCount: 99, sha: 'global' }), prompt: () => { throw Error('Global cache must never enter a project prompt'); } },
    askModel: info => { calls.push(info); return { answer: 'Fixture response.' }; } };
  let server;
  const start = async () => { server = createServer(options); await new Promise(resolve => server.listen(0,'127.0.0.1',resolve)); };
  const request = async (route, input, expected = 200) => {
    const response = await fetch(`http://127.0.0.1:${server.address().port}${route}`, input === undefined ? {} : { method: 'POST', headers: { 'Content-Type':'application/json' }, body: JSON.stringify(input) });
    const data = await response.json(); assert.equal(response.status, expected, JSON.stringify(data)); return data;
  };
  const active = () => request('/api/skills?projectId=alpha');
  try {
    await start();
    assert.equal((await active()).enabled, false, 'Global cache does not register project skills');
    await request('/api/skills/settings', { projectId:'alpha', enabled:true }, 400);
    fs.writeFileSync(path.join(agents, 'matt-pocock-install.json'), JSON.stringify({ source:'https://github.com/mattpocock/skills', skills:[{ name:'grilling' }] }));
    assert.equal((await active()).projectSkills[0].downloaded, false);
    fs.writeFileSync(skillFile, 'ALPHA LOCAL SKILL');
    assert.equal((await active()).enabled, false, 'Undocumented local file stays inactive');
    await request('/api/skills/settings', { projectId:'alpha', skillId:'matt:grilling', enabled:true }, 400);
    fs.writeFileSync(path.join(agents,'SKILLS.md'), '[grilling](skills/grilling/SKILL.md)');
    assert.equal((await active()).enabled, true);
    await request('/api/orchestrator/lead', { id:'fixture-lead' });
    await request('/api/orchestrator/ask', { projectId:'alpha', question:'Explain the current state.' });
    assert.match(calls.at(-1).skillsPrompt, /ALPHA LOCAL SKILL/);
    await request('/api/orchestrator/ask', { projectId:'beta', question:'Explain the current state.' });
    assert.doesNotMatch(calls.at(-1).skillsPrompt, /ALPHA LOCAL SKILL/);
    await request('/api/skills/settings', { projectId:'alpha', skillId:'matt:grilling', enabled:false });
    await new Promise(resolve => server.close(resolve)); await start();
    assert.equal((await active()).enabled, false, 'Per-skill choice survives restart');
    await request('/api/skills/settings', { projectId:'alpha', skillId:'matt:grilling', enabled:true });
    fs.writeFileSync(path.join(agents,'SKILLS.md'), 'No documented skills.');
    assert.equal((await active()).projectSkills[0].active, false, 'Saved active flag cannot override missing documentation');
    await request('/api/orchestrator/lead', { id:'fixture-lead' });
    await request('/api/orchestrator/ask', { projectId:'alpha', question:'Explain the current state.' });
    assert.doesNotMatch(calls.at(-1).skillsPrompt, /ALPHA LOCAL SKILL/);
    fs.unlinkSync(skillFile);
    assert.equal((await active()).projectSkills[0].active, false);
    await request('/api/skills/settings', { projectId:'beta', skillId:'matt:grilling', enabled:true }, 400);
    assert.deepEqual((await request('/api/skills?projectId=beta')).projectSkills, []);
  } finally { if (server?.listening) await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive:true, force:true }); }
});
