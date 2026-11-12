'use strict';
// The lead is never left without quota: at the switch threshold, or at zero, Planwharf names the next
// lead in the ranking, for real, and says so in the chat.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createOrchestrator, leadRanking } = require('./orchestrator.js');
const { createAttemptEngine } = require('./attempts.js');

// As in a real team: only the chosen lead has the lead role; the others build and verify.
const CLI = { fable: 'claude-fable-5-2', opus: 'claude-opus-5-5', sonnet: 'claude-sonnet-5-5', haiku: 'claude-haiku-5-5', sol: 'gpt-6.1-sol', astra: 'gpt-6-astra', luna: 'gpt-6-luna', grok: 'grok-4.7' };
const model = (id, label, provider, plan, extra = {}) => ({ id, label, provider, adapter: provider, cli: CLI[id], plan, enabled: true, available: true, orchestratorEligible: true,
  roles: { orchestrator: id === 'fable', worker: true, verifier: true, reviewer: false, supervisor: false },
  capabilities: { canOrchestrate: true, canCode: true, canUseTools: true, canInspectFilesystem: true }, ...extra });

test('the ranking puts stronger leads first, then more quota; unknown quota comes after known', () => {
  const plans = [{ id: 'claude', readable: true, leftPct: 60 }, { id: 'chatgpt', readable: true, leftPct: 90 }, { id: 'grok', readable: false }];
  const list = [
    model('haiku', 'Haiku 5.5', 'claude', 'claude'),
    model('grok', 'Grok 4.7', 'grok', 'grok', { kind: 'premium' }),
    model('luna', 'GPT-6 Luna', 'codex', 'chatgpt', { kind: 'local' }),
    model('sol', 'GPT-6.1 Sol', 'codex', 'chatgpt', { kind: 'default' }),
    model('opus', 'Opus 5.5', 'claude', 'claude'),
    model('astra', 'GPT-6 Astra', 'codex', 'chatgpt', { kind: 'premium' }),
    model('sonnet', 'Sonnet 5.5', 'claude', 'claude')
  ];
  // Premium: Astra (90%) and Opus (60%) with known quota, then Grok 4.7 (unknown); then default; then light.
  assert.deepEqual(leadRanking(list, plans).map(item => item.id), ['astra', 'opus', 'grok', 'sol', 'sonnet', 'luna', 'haiku']);
});

test('a lead at the threshold hands over to the next in the ranking, and the chat says so', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-lead-switch-'));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const projectId = plan.projects ? plan.projects[0].project.id : plan.project.id;
  let plans = [{ id: 'claude', readable: true, leftPct: 60 }, { id: 'chatgpt', readable: true, leftPct: 70 }];
  const asked = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => plans, plansTtl: 0,
    models: [model('fable', 'Fable 5.2', 'claude', 'claude'), model('sol', 'GPT-6.1 Sol', 'codex', 'chatgpt', { kind: 'default' }), model('astra', 'GPT-6 Astra', 'codex', 'chatgpt', { kind: 'premium' })],
    askModel: async info => { asked.push(info.model.id); return { answer: 'ok' }; } });
  try {
    await orchestra.setOrchestrator('fable');
    await orchestra.tick();
    await orchestra.ask('Plan it.', projectId);
    assert.deepEqual(asked, ['fable'], 'with quota, the chosen lead answers');
    plans = [{ id: 'claude', readable: true, leftPct: 15 }, { id: 'chatgpt', readable: true, leftPct: 70 }];
    await orchestra.tick();
    await orchestra.ask('Go on.', projectId);
    assert.equal(asked.at(-1), 'astra', 'the strongest lead with quota takes over');
    const view = await orchestra.snapshot();
    assert.equal(view.orchestratorId, 'astra', 'the switch is real, not hidden in one call');
    assert.ok(view.execution.timeline.some(event => event.type === 'LEAD_SWITCHED' && event.summary === 'Planwharf switched the lead: Fable 5.2 → GPT-6 Astra. Why: quota of Fable 5.2 is at 15% (switch at 20%).'),
      JSON.stringify(view.execution.timeline.map(event => event.summary)));
    // Every lead low and none better: the lead keeps working rather than stop.
    plans = [{ id: 'claude', readable: true, leftPct: 5 }, { id: 'chatgpt', readable: true, leftPct: 10 }];
    await orchestra.tick();
    await orchestra.ask('Still there?', projectId);
    assert.equal(asked.at(-1), 'astra');
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
