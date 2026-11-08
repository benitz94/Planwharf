'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const Plan = require('../plan.js');
const { claudeToolArgs, isolatedArgs } = require('./claude.cjs');
const { toolRequestRule, TOOL_REQUEST } = require('./runner.cjs');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

test('tools off give Claude the same isolated launch as before; each checkbox turns on one thing', () => {
  assert.deepEqual(claudeToolArgs(), isolatedArgs);
  assert.deepEqual(claudeToolArgs({ mcp: false, browser: false, hooks: false, memory: false }), isolatedArgs);
  const all = claudeToolArgs({ mcp: true, browser: true, hooks: true, memory: true });
  assert.ok(!all.includes('--strict-mcp-config') && all.includes('--chrome') && !all.includes('--no-chrome'));
  assert.deepEqual(JSON.parse(all[all.indexOf('--settings') + 1]), { disableAllHooks: false, autoMemoryEnabled: true });
  const browserOnly = claudeToolArgs({ browser: true });
  assert.ok(browserOnly.includes('--strict-mcp-config') && browserOnly.includes('--chrome'));
});

test('the lead is told which tools are off and asks with one exact line', () => {
  assert.match(toolRequestRule({ browser: true }), /off for the team: mcp, hooks, memory/);
  assert.match(toolRequestRule({}), /Never ask for subagents/);
  assert.equal('I need to try the page.\nTOOL REQUEST: browser'.match(TOOL_REQUEST)[1], 'browser');
  assert.equal('TOOL REQUEST: subagents'.match(TOOL_REQUEST), null);
});

test('project tools reach lead and workers, the lead request becomes a button, and only valid names are saved', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tools-'));
  let plan = Plan.example();
  plan = Plan.createProject(plan, { name: 'alpha', direction: 'Build alpha' });
  fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({ orchestratorId: 'fixture', asks: [] }));
  const seen = [];
  const orchestra = createOrchestrator({ dir, models: [{ id: 'fixture', label: 'Fixture', provider: 'codex', cli: 'gpt-6-luna', kind: 'premium', enabled: true, available: true, limit: 200 }],
    readPlan: async () => plan, attempts: createAttemptEngine({ dir }), fetchPlans: async () => [],
    inspectFolder: () => ({ git: false, names: [], fileCount: 0 }), spawnWorker: () => { throw Error('no workers'); },
    askModel: async info => { seen.push(info.model.tools); return { answer: 'Trying the page needs a browser.\nTOOL REQUEST: browser', actions: [] }; } });
  assert.equal((await orchestra.setProjectTools('alpha', { subagents: true })).ok, false, 'subagents are not a tool');
  assert.equal((await orchestra.setProjectTools('alpha', { mcp: 'yes' })).ok, false);
  await orchestra.ask('Please check the page.', 'alpha');
  assert.deepEqual(seen.at(-1), { mcp: false, browser: false, hooks: false, memory: false }, 'all off by default');
  const saved = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8')).asks[0];
  assert.equal(saved.toolRequest, 'browser');
  assert.doesNotMatch(saved.answer, /TOOL REQUEST/, 'the request line is not shown');
  assert.equal((await orchestra.setProjectTools('alpha', { browser: true })).ok, true);
  await orchestra.ask('Please check the page again.', 'alpha');
  assert.equal(seen.at(-1).browser, true, 'the lead gets the tool once the user turns it on');
  const again = JSON.parse(fs.readFileSync(path.join(dir, 'orchestrator.json'), 'utf8')).asks[0];
  assert.equal(again.toolRequest, undefined, 'no button for a tool that is already on');
  await orchestra.shutdown();
});
