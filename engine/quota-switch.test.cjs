'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { quotaLeft, lowQuota, quotaSwitches, quotaWindows, AUTO_SWITCH_DEFAULT } = require('./model-policy.cjs');

const plans = [
  { id: 'chatgpt', readable: true, leftPct: 12 },
  { id: 'claude', readable: true, leftPct: 60 },
  { id: 'gemini', readable: true, leftPct: 98 },
  { id: 'grok', readable: false }
];
const model = (id, plan, extra = {}) => ({ id, label: id, plan, enabled: true, available: true, ...extra });
const models = [model('terra', 'chatgpt'), model('sonnet', 'claude'), model('gemini-pro', 'gemini'), model('grok', 'grok')];

test('the default threshold is 35% left, on the 5-hour window and on the weekly one', () => {
  assert.equal(AUTO_SWITCH_DEFAULT.thresholdPercent, 35);
  assert.equal(AUTO_SWITCH_DEFAULT.fiveHourPercent, 35);
  assert.equal(AUTO_SWITCH_DEFAULT.weeklyPercent, 35);
  assert.equal(AUTO_SWITCH_DEFAULT.enabled, true);
});

test('quota is low at or under the threshold, or when exhausted; unread quota is not low', () => {
  assert.equal(quotaLeft(models[0], plans), 12);
  assert.equal(lowQuota(models[0], plans, 20), true);
  assert.equal(lowQuota(models[1], plans, 20), false);
  assert.equal(lowQuota(model('x', 'chatgpt'), [{ id: 'chatgpt', readable: true, leftPct: 50, exhausted: true }], 20), true);
  assert.equal(lowQuota(models[3], plans, 20), false);
});

test('a working step on a low model moves to the eligible model with the most quota left', () => {
  const running = [{ subTaskId: 's1', modelId: 'terra' }, { subTaskId: 's2', modelId: 'sonnet' }];
  const switches = quotaSwitches({ running, models, plans, thresholdPercent: 20, eligible: () => true });
  assert.deepEqual(switches, [{ subTaskId: 's1', fromModelId: 'terra', toModelId: 'gemini-pro', leftPercent: 12 }]);
});

test('no switch without an eligible model above the threshold, and none when switching is off', () => {
  const running = [{ subTaskId: 's1', modelId: 'terra' }];
  assert.deepEqual(quotaSwitches({ running, models, plans, thresholdPercent: 20, eligible: candidate => candidate.id === 'terra' }), []);
  assert.deepEqual(quotaSwitches({ running, models, plans, thresholdPercent: 20, enabled: false, eligible: () => true }), []);
  // Grok's quota is not reported (Grok Business bills in dollars): it can still take over, so the work goes on.
  assert.deepEqual(quotaSwitches({ running, models: [models[0], models[3]], plans, thresholdPercent: 20, eligible: () => true }).map(change => change.toModelId), ['grok']);
  // A model whose quota is read and above its limit comes before one whose quota is not reported.
  assert.equal(quotaSwitches({ running, models: [models[0], models[3], models[1]], plans, thresholdPercent: 20, eligible: () => true })[0].toModelId, 'sonnet');
});

test('either window under its limit makes a model low; each model can have its own limit, or none', () => {
  const windowed = [{ id: 'claude', readable: true, leftPct: 30, windows: [{ name: '5-hour', leftPct: 80 }, { name: 'weekly', leftPct: 30 }] }];
  const claude = model('opus', 'claude');
  // The weekly window is under 35% even though the 5-hour one has plenty left.
  assert.equal(lowQuota(claude, windowed, 35), true);
  assert.equal(lowQuota(claude, windowed, { fiveHourPercent: 35, weeklyPercent: 25 }), false);
  assert.equal(lowQuota(claude, windowed, { fiveHourPercent: 85, weeklyPercent: 10 }), true, 'the 5-hour window under its own limit');
  // No limit: only a quota really exhausted stops the model.
  assert.equal(lowQuota(claude, windowed, { none: true }), false);
  assert.equal(lowQuota(claude, [{ ...windowed[0], exhausted: true }], { none: true }), true);
  // A limit chosen per model, as a function of the model.
  assert.equal(lowQuota(claude, windowed, model => model.id === 'opus' ? { none: true } : 35), false);
  // The two windows by name, whatever the provider calls them.
  assert.deepEqual(quotaWindows(claude, [{ id: 'claude', readable: true, windows: [{ name: 'five_hour', leftPct: 61 }, { name: 'seven_day', leftPct: 44 }] }]), { fiveHour: 61, weekly: 44 });
  assert.deepEqual(quotaWindows(claude, [{ id: 'claude', readable: false }]), { fiveHour: null, weekly: null });
});

test('the switch keeps the check independent: it prefers a company the other steps do not use', () => {
  // Build runs on terra (ChatGPT, low); Verify uses a Gemini model. With room in both, Claude is
  // preferred to Gemini, though Gemini has more quota, so build and check stay with different companies.
  const withProviders = [model('terra', 'chatgpt', { provider: 'codex' }), model('sonnet', 'claude', { provider: 'claude' }), model('gemini-pro', 'gemini', { provider: 'antigravity' })];
  const running = [{ subTaskId: 's1', modelId: 'terra', avoidProviders: ['antigravity'] }];
  assert.equal(quotaSwitches({ running, models: withProviders, plans, thresholdPercent: 20, eligible: () => true })[0].toModelId, 'sonnet');
  // Only the other company's model has room: the step still moves rather than stop.
  assert.equal(quotaSwitches({ running, models: [withProviders[0], withProviders[2]], plans, thresholdPercent: 20, eligible: () => true })[0].toModelId, 'gemini-pro');
});

test('the general limit and each model\'s own limit are saved, shown on the model, and survive a restart', async () => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-quota-limits-'));
  const plan = require('../plan.js').example(); plan.tasks = [];
  const roster = [{ id: 'opus', label: 'Opus 5.5', provider: 'claude', adapter: 'claude', cli: 'claude-opus-5-5', plan: 'claude', enabled: true, available: true, cost: 1, limit: 100, capabilities: { canCode: true } },
    { id: 'grok', label: 'Grok 4.7', provider: 'grok', adapter: 'grok', cli: 'grok-4.7', plan: 'grok', enabled: true, available: true, cost: 1, limit: 100, capabilities: { canCode: true } }];
  const windows = [{ id: 'claude', readable: true, leftPct: 30, windows: [{ name: '5-hour', leftPct: 70 }, { name: 'weekly', leftPct: 30 }] }, { id: 'grok', readable: false }];
  const make = () => createOrchestrator({ dir, readPlan: async () => plan, models: roster, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }), fetchPlans: async () => windows, plansTtl: 0 });
  try {
    let orchestra = make();
    assert.deepEqual(orchestra.autoSwitch(), { enabled: true, fiveHourPercent: 35, weeklyPercent: 35, thresholdPercent: 35 });
    let opus = (await orchestra.tick()).models.find(model => model.id === 'opus');
    assert.deepEqual(opus.quotaWindows, { fiveHour: 70, weekly: 30 });
    assert.equal(opus.lowQuota, true, 'the weekly window is under 35%');
    assert.deepEqual(orchestra.setAutoSwitch({ weeklyPercent: 25 }).autoSwitch, { enabled: true, fiveHourPercent: 35, weeklyPercent: 25, thresholdPercent: 35 });
    assert.equal((await orchestra.tick()).models.find(model => model.id === 'opus').lowQuota, false);
    // Opus gets its own limit; Grok gets none.
    assert.equal(orchestra.setQuotaLimit('opus', { weeklyPercent: 40 }).ok, true);
    assert.equal(orchestra.setQuotaLimit('grok', { none: true }).ok, true);
    assert.equal(orchestra.setQuotaLimit('opus', { weeklyPercent: 95 }).ok, false, 'over 90% is refused');
    assert.equal(orchestra.setQuotaLimit('nobody', { none: true }).ok, false);
    orchestra.shutdown?.();
    orchestra = make();
    const view = await orchestra.tick();
    opus = view.models.find(model => model.id === 'opus');
    assert.deepEqual({ ...opus.quotaLimit }, { none: false, fiveHourPercent: 35, weeklyPercent: 40, custom: true });
    assert.equal(opus.lowQuota, true, 'its own weekly limit of 40% applies');
    assert.equal(view.models.find(model => model.id === 'grok').quotaLimit.none, true);
    assert.equal(orchestra.setQuotaLimit('opus', { reset: true }).ok, true);
    assert.equal((await orchestra.tick()).models.find(model => model.id === 'opus').quotaLimit.custom, false);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('an old saved single threshold applies to both windows; the former 20% default becomes 35%', () => {
  const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  for (const [saved, expected] of [[20, 35], [30, 30]]) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-quota-legacy-'));
    fs.writeFileSync(path.join(dir, 'orchestrator.json'), JSON.stringify({ autoSwitch: { enabled: true, thresholdPercent: saved } }));
    try {
      const orchestra = createOrchestrator({ dir, readPlan: async () => require('../plan.js').example(), models: [], attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }) });
      assert.deepEqual([orchestra.autoSwitch().fiveHourPercent, orchestra.autoSwitch().weeklyPercent], [expected, expected], `saved ${saved}%`);
    } finally { fs.rmSync(dir, { recursive: true, force: true }); }
  }
});
