'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const Plan = require('../plan.js');
const { createOrchestrator } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

// Two projects talk to the lead at the same time: each gets its own answer, and stopping one
// leaves the other running (before 2026-11-08 any new lead call cut off the one in flight).
test('two projects ask the lead at once; Stop in one leaves the other', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-parallel-'));
  const plan = { schema: 2, projects: ['alpha', 'beta'].map(id => { const item = Plan.example(); item.project = { id, name: id }; item.tasks = []; return item; }) };
  fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({ orchestratorId: 'fixture', asks: [] }));
  const waiting = new Map(), entered = new Map(), aborted = [];
  const orchestra = createOrchestrator({ dir, models: [{ id: 'fixture', label: 'Fixture', provider: 'codex', cli: 'gpt-6-luna', kind: 'premium', enabled: true, available: true, limit: 200 }],
    readPlan: async () => plan, attempts: createAttemptEngine({ dir }), fetchPlans: async () => [],
    inspectFolder: () => ({ git: false, names: [], fileCount: 0 }),
    spawnWorker: () => { throw Error('no workers in this test'); },
    askModel: async info => {
      info.onProgress?.({ answer: 'thinking about ' + info.projectId });
      await new Promise(resolve => { waiting.set(info.projectId, resolve); entered.get(info.projectId)?.(); });
      return { answer: 'Answer for ' + info.projectId + '.', actions: [] };
    },
    abortAsk: key => { aborted.push(key); waiting.get(String(key).replace(/^chat:/, ''))?.(); }
  });
  const reached = id => new Promise(resolve => entered.set(id, resolve));
  const alphaIn = reached('alpha'), betaIn = reached('beta');
  const alpha = orchestra.ask('Please explain alpha.', 'alpha');
  const beta = orchestra.ask('Please explain beta.', 'beta');
  await Promise.all([alphaIn, betaIn]);
  const live = await orchestra.snapshot();
  assert.deepEqual(Object.keys(live.thinkingByProject).sort(), ['alpha', 'beta'], 'both replies are in flight together');
  await orchestra.abortAsk('alpha');
  assert.deepEqual(aborted, ['chat:alpha'], 'Stop reaches only the alpha call');
  assert.equal((await alpha).cancelled, true);
  waiting.get('beta')();
  const answered = await beta;
  assert.notEqual(answered.cancelled, true);
  assert.match(answered.answer || JSON.stringify(answered.asks?.[0] || {}), /Answer for beta/);
  await orchestra.shutdown();
});
