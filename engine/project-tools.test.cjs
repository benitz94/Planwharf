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
  assert.match(toolRequestRule({ browser: true }), /off for your model: mcp, hooks, memory/);
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

test('saved legacy tools migrate; overrides survive restart and reset to the current default', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-model-tools-'));
  const file = path.join(dir, 'orchestrator.json');
  const plan = Plan.createProject(Plan.example(), { name: 'alpha', direction: 'Build alpha' });
  fs.writeFileSync(file, JSON.stringify({ orchestratorId: 'lead', projectTools: { alpha: { browser: true } } }));
  const seen = [];
  const open = () => createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: ['lead', 'other'].map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'premium' })),
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }),
    askModel: async info => { seen.push(info.model); return { answer: 'TOOL REQUEST: browser', actions: [] }; } });
  let orchestra = open();
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });
  await orchestra.ask('Check', 'alpha');
  assert.equal(seen.at(-1).tools.browser, true);
  assert.deepEqual(JSON.parse(fs.readFileSync(file)).projectTools.alpha, {
    default: { mcp: false, browser: true, hooks: false, memory: false }, models: {} });
  assert.equal((await orchestra.setProjectTools('alpha', { browser: false, memory: true }, 'lead')).ok, true);
  await orchestra.ask('Check', 'alpha');
  assert.equal(seen.at(-1).tools.browser, false);
  assert.equal(seen.at(-1).tools.memory, true);
  assert.equal(JSON.parse(fs.readFileSync(file)).asks[0].toolRequest, 'browser');
  await orchestra.setOrchestrator('other', 'alpha');
  await orchestra.ask('Check', 'alpha');
  assert.equal(seen.at(-1).id, 'other');
  assert.equal(seen.at(-1).tools.browser, true);
  assert.equal(seen.at(-1).tools.memory, false);
  await orchestra.shutdown();
  orchestra = open();
  const snap = await orchestra.snapshot('alpha');
  assert.equal(snap.projectTools.alpha.browser, true, 'legacy clients still read the default');
  assert.equal(snap.projectToolSettings.alpha.models.lead.memory, true);
  await orchestra.setProjectTools('alpha', { hooks: true });
  assert.equal((await orchestra.setProjectTools('alpha', null, 'lead')).ok, true);
  assert.deepEqual((await orchestra.snapshot('alpha')).projectToolSettings.alpha.models, {});
  await orchestra.setOrchestrator('lead', 'alpha');
  await orchestra.ask('Check', 'alpha');
  assert.equal(seen.at(-1).tools.hooks, true);
  for (const modelId of ['', null, 42, 'missing', '__proto__']) {
    assert.equal((await orchestra.setProjectTools('alpha', { mcp: true }, modelId)).ok, false);
  }
  assert.equal((await orchestra.setProjectTools('alpha', null)).ok, false);
  assert.equal((await orchestra.setProjectTools('alpha', { browser: 'yes' }, 'lead')).ok, false);
});

test('worker launches receive their own tools or the project default', async t => {
  for (const override of [false, true]) {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worker-tools-'));
    const folder = path.join(dir, 'project'); fs.mkdirSync(folder);
    const plan = { schema: 2, projects: [{ project: { id: 'demo', name: 'Demo', folder },
      revisions: [{ id: 1, direction: 'Build', open: [] }],
      tasks: [{ id: 't1', title: 'Build', detail: 'Build it', status: 'running', executionVersion: 2, revision_id: 1, source: 'chat' }] }] };
    const launches = []; let alive = true;
    const orchestra = createOrchestrator({ dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
      models: [{ id: 'worker', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true }], fetchPlans: null,
      inspectFolder: () => ({ git: false, dirty: [] }), spawnWorker: info => { launches.push(info); return { pid: 3000 }; },
      isProcessAlive: () => alive, killWorker: () => { alive = false; }, askModel: async () => ({ answer: '{"done":true}' }) });
    try {
      await orchestra.setProjectTools('demo', { browser: true });
      if (override) await orchestra.setProjectTools('demo', { browser: false, mcp: true }, 'worker');
      await orchestra.setMode('auto'); await orchestra.tick();
      await orchestra.setAutonomy('demo', true); await orchestra.autonomyTick();
      assert.equal(launches.length, 1);
      assert.deepEqual(launches[0].worker.tools, { mcp: override, browser: !override, hooks: false, memory: false });
    } finally { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); }
  }
});

test('tools API supports model overrides, reset, validation and the legacy default route', async () => {
  const { createServer } = require('../server.cjs');
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tools-api-'));
  const planFile = path.join(dir, 'notebook.json');
  const plan = Plan.example(); plan.tasks = []; plan.project = { ...plan.project, id: 'isolated' };
  fs.writeFileSync(planFile, JSON.stringify(plan));
  const server = createServer({ planFile, attemptsDir: path.join(dir, 'attempts'), refreshModels: false,
    fetchPlans: async () => [], models: [{ id: 'model-a', provider: 'codex', cli: 'fixture', enabled: true }],
    spawnWorker: () => { throw new Error('No worker may run'); } });
  try {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const post = (route, input) => fetch(`http://127.0.0.1:${server.address().port}${route}`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input) });
    assert.equal((await post('/api/project-tools', { projectId: 'isolated', tools: { browser: true } })).status, 200);
    const saved = await post('/api/projects/tools', { projectId: 'isolated', modelId: 'model-a', tools: { browser: false } });
    assert.equal(saved.status, 200);
    const body = await saved.json();
    assert.equal(body.tools.browser, false);
    assert.equal(body.snapshot.projectTools.isolated.browser, true);
    assert.equal(body.snapshot.projectToolSettings.isolated.models['model-a'].browser, false);
    for (const bad of [{ modelId: 'missing' }, { modelId: null }, { tools: { browser: 1 } }, { extra: true }]) {
      assert.equal((await post('/api/projects/tools', { projectId: 'isolated', tools: {}, ...bad })).status, 400);
    }
    const reset = await post('/api/projects/tools', { projectId: 'isolated', modelId: 'model-a', tools: null });
    assert.equal(reset.status, 200);
    assert.equal((await reset.json()).tools.browser, true);
  } finally { await new Promise(resolve => server.close(resolve)); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('resolved tools control Claude and Codex launch arguments for leads and workers', () => {
  const { launchSpec } = require('./provider-runtime.cjs');
  const before = { claude: process.env.PLANWHARF_CLAUDE, codex: process.env.PLANWHARF_CODEX };
  process.env.PLANWHARF_CLAUDE = process.execPath;
  process.env.PLANWHARF_CODEX = process.execPath;
  try {
    for (const lead of [false, true]) {
      for (const on of [false, true]) {
        const tools = { mcp: on, browser: on, hooks: on, memory: on };
        const claude = launchSpec({ id: 'claude-fixture', provider: 'claude', cli: 'claude-sonnet-4-6', alwaysApprove: true, tools }, process.cwd(), 'Fixture', lead);
        assert.equal(claude.args.includes('--chrome'), on);
        assert.equal(claude.args.includes('--strict-mcp-config'), !on);
        const settings = JSON.parse(claude.args[claude.args.indexOf('--settings') + 1]);
        assert.equal(settings.disableAllHooks, !on);
        assert.equal(settings.autoMemoryEnabled, on);
        const codex = launchSpec({ id: 'codex-fixture', provider: 'codex', cli: 'gpt-6.1-sol', tools }, process.cwd(), 'Fixture', lead);
        assert.equal(codex.args.includes('mcp_servers={}'), !on);
      }
    }
  } finally {
    for (const [provider, value] of Object.entries(before)) {
      const key = 'PLANWHARF_' + provider.toUpperCase();
      if (value === undefined) delete process.env[key]; else process.env[key] = value;
    }
  }
});

test('multi-project tool isolation and archiving preserve model overrides independently', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-multi-tools-'));
  let plan = Plan.example();
  plan = Plan.createProject(plan, { name: 'alpha', direction: 'Build alpha' });
  plan = Plan.createProject(plan, { name: 'beta', direction: 'Build beta' });
  const open = () => createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: ['lead', 'worker'].map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'premium' })),
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }), askModel: async () => ({ answer: 'OK', actions: [] }) });
  let orchestra = open();
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });

  await orchestra.setProjectTools('alpha', { browser: true });
  await orchestra.setProjectTools('alpha', { browser: false, memory: true }, 'lead');
  await orchestra.setProjectTools('beta', { hooks: true });

  const snap = await orchestra.snapshot();
  assert.equal(snap.projectToolSettings.alpha.default.browser, true);
  assert.equal(snap.projectToolSettings.alpha.models.lead.memory, true);
  assert.equal(snap.projectToolSettings.alpha.models.lead.browser, false);
  assert.equal(snap.projectToolSettings.beta.default.hooks, true);
  assert.equal(snap.projectToolSettings.beta.default.browser, false);
  assert.deepEqual(snap.projectToolSettings.beta.models, {});

  const archiveResult = await orchestra.archiveProject('alpha', 'test-archive', { keepTeam: true });
  assert.equal(archiveResult.ok, true);
  const snapAfterKeep = await orchestra.snapshot();
  assert.equal(snapAfterKeep.projectToolSettings.alpha.default.browser, true);
  assert.equal(snapAfterKeep.projectToolSettings.alpha.models.lead.memory, true);

  await orchestra.archiveProject('beta', 'test-archive', { keepTeam: false });
  const snapAfterDelete = await orchestra.snapshot();
  assert.equal(snapAfterDelete.projectToolSettings.beta, undefined);

  await orchestra.shutdown();
  orchestra = open();
  const snapRestart = await orchestra.snapshot();
  assert.equal(snapRestart.projectToolSettings.alpha.default.browser, true);
  assert.equal(snapRestart.projectToolSettings.alpha.models.lead.memory, true);
});

test('malformed saved project tools recover cleanly to valid defaults and models', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-corrupt-tools-'));
  const file = path.join(dir, 'orchestrator.json');
  const plan = Plan.createProject(Plan.example(), { name: 'gamma', direction: 'Build gamma' });
  fs.writeFileSync(file, JSON.stringify({
    orchestratorId: 'lead',
    projectTools: {
      gamma: {
        default: null,
        models: {
          lead: 'invalid',
          worker: { browser: 1, memory: true, invalidProp: true },
          corruptArray: []
        }
      },
      corruptString: 'bogus',
      corruptNull: null
    }
  }));
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: ['lead', 'worker'].map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true })),
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }), askModel: async () => ({ answer: 'OK', actions: [] }) });
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });

  const snap = await orchestra.snapshot('gamma');
  assert.deepEqual(snap.projectToolSettings.gamma.default, { mcp: false, browser: false, hooks: false, memory: false });
  assert.deepEqual(snap.projectToolSettings.gamma.models.worker, { mcp: false, browser: false, hooks: false, memory: true });
  assert.equal(snap.projectToolSettings.gamma.models.lead, undefined);
  assert.equal(snap.projectToolSettings.gamma.models.corruptArray, undefined);

  const set = await orchestra.setProjectTools('gamma', { mcp: true }, 'lead');
  assert.equal(set.ok, true);
  assert.equal(set.tools.mcp, true);
});

test('concurrent updates to default tools and model overrides preserve both changes', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-concurrency-tools-'));
  const plan = Plan.createProject(Plan.example(), { name: 'delta', direction: 'Build delta' });
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: ['lead', 'worker'].map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true })),
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }), askModel: async () => ({ answer: 'OK', actions: [] }) });
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });

  await Promise.all([
    orchestra.setProjectTools('delta', { browser: true }),
    orchestra.setProjectTools('delta', { memory: true }, 'worker')
  ]);

  const snap = await orchestra.snapshot('delta');
  assert.equal(snap.projectToolSettings.delta.default.browser, true);
  assert.equal(snap.projectToolSettings.delta.models.worker.memory, true);
  await orchestra.setProjectTools('delta', {}, 'lead');
  const snapLead = await orchestra.snapshot('delta');
  assert.equal(snapLead.projectToolSettings.delta.models.lead.browser, true);
  assert.equal(snapLead.projectToolSettings.delta.models.lead.memory, false);
});

test('an override stays complete when the default changes, and a bad save changes nothing', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tools-isolate-'));
  const file = path.join(dir, 'orchestrator.json');
  const plan = Plan.createProject(Plan.example(), { name: 'alpha', direction: 'Build alpha' });
  const seen = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: ['lead', 'other'].map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'premium' })),
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }),
    askModel: async info => { seen.push(info.model); return { answer: 'Need the browser.\nTOOL REQUEST: browser', actions: [] }; } });
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });
  assert.equal((await orchestra.setOrchestrator('lead')).ok, true);
  assert.equal((await orchestra.setProjectTools('alpha', { browser: true, hooks: true })).ok, true);
  assert.equal((await orchestra.setProjectTools('alpha', { browser: false }, 'lead')).ok, true);
  let settings = (await orchestra.snapshot('alpha')).projectToolSettings.alpha;
  assert.deepEqual(settings.models.lead, { mcp: false, browser: false, hooks: true, memory: false });
  assert.equal((await orchestra.setProjectTools('alpha', { memory: true })).ok, true);
  settings = (await orchestra.snapshot('alpha')).projectToolSettings.alpha;
  assert.equal(settings.default.browser, true);
  assert.equal(settings.default.hooks, true);
  assert.equal(settings.default.memory, true);
  assert.deepEqual(settings.models.lead, { mcp: false, browser: false, hooks: true, memory: false });
  assert.equal((await orchestra.snapshot('alpha')).projectTools.alpha.memory, true, 'the flat snapshot stays the default');
  await orchestra.ask('Check the page', 'alpha');
  assert.equal(seen.at(-1).tools.browser, false);
  assert.equal(seen.at(-1).tools.hooks, true);
  assert.equal(JSON.parse(fs.readFileSync(file, 'utf8')).asks[0].toolRequest, 'browser');
  await orchestra.setProjectTools('alpha', { browser: true }, 'lead');
  await orchestra.ask('Check the page again', 'alpha');
  assert.equal(seen.at(-1).tools.browser, true);
  assert.equal(JSON.parse(fs.readFileSync(file, 'utf8')).asks[0].toolRequest, undefined);
  const saved = JSON.parse(fs.readFileSync(file, 'utf8')).projectTools.alpha;
  for (const [tools, modelId] of [[{ browser: 'yes' }, 'lead'], [null, undefined], [[], 'lead'], [{ mcp: true }, 'missing']]) {
    assert.equal((await orchestra.setProjectTools('alpha', tools, modelId)).ok, false);
  }
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')).projectTools.alpha, saved);
  assert.equal((await orchestra.setProjectTools('alpha', null, 'other')).ok, true);
  assert.equal((await orchestra.snapshot('alpha')).projectToolSettings.alpha.models.other, undefined);
  assert.equal((await orchestra.snapshot('alpha')).projectToolSettings.alpha.models.lead.browser, true);
});

test('legacy tools are rewritten on shutdown, and partial or corrupt saves do not inherit', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tools-rewrite-'));
  const file = path.join(dir, 'orchestrator.json');
  const plan = Plan.createProject(Plan.example(), { name: 'alpha', direction: 'Build alpha' });
  fs.writeFileSync(file, JSON.stringify({
    orchestratorId: 'lead',
    projectTools: {
      alpha: { browser: true, models: [{ mcp: true }] },
      beta: { default: { browser: 'true', hooks: true }, models: { lead: { memory: 'yes', mcp: true }, drop: null, list: [] } },
      gone: 'nope'
    }
  }));
  const open = () => createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: [{ id: 'lead', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'premium' }],
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }), askModel: async () => ({ answer: 'OK', actions: [] }) });
  let orchestra = open();
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });
  const first = await orchestra.snapshot();
  assert.deepEqual(first.projectToolSettings.alpha.default, { mcp: false, browser: true, hooks: false, memory: false });
  assert.deepEqual(first.projectToolSettings.alpha.models, {});
  assert.equal(first.projectToolSettings.beta.default.browser, false);
  assert.equal(first.projectToolSettings.beta.default.hooks, true);
  assert.deepEqual(first.projectToolSettings.beta.models.lead, { mcp: true, browser: false, hooks: false, memory: false });
  assert.equal(first.projectToolSettings.beta.models.drop, undefined);
  assert.equal(first.projectToolSettings.beta.models.list, undefined);
  await orchestra.shutdown();
  const rewritten = JSON.parse(fs.readFileSync(file, 'utf8')).projectTools;
  assert.deepEqual(rewritten.alpha, { default: { mcp: false, browser: true, hooks: false, memory: false }, models: {} });
  assert.equal(rewritten.beta.models.lead.mcp, true);
  assert.equal(rewritten.beta.models.drop, undefined);
  orchestra = open();
  assert.equal((await orchestra.snapshot()).projectToolSettings.alpha.default.browser, true);
});

test('each project gives the same lead its own tools', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tools-leads-'));
  let plan = Plan.createProject(Plan.example(), { name: 'alpha', direction: 'Build alpha' });
  plan = Plan.createProject(plan, { name: 'beta', direction: 'Build beta' });
  const seen = [];
  const orchestra = createOrchestrator({ dir, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models: [{ id: 'lead', provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true, kind: 'premium' }],
    fetchPlans: async () => [], inspectFolder: () => ({ git: false }),
    askModel: async info => { seen.push({ projectId: info.projectId, tools: info.model.tools }); return { answer: 'OK', actions: [] }; } });
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });
  assert.equal((await orchestra.setOrchestrator('lead')).ok, true);
  await orchestra.setProjectTools('alpha', { browser: true });
  await orchestra.setProjectTools('alpha', { browser: false, memory: true }, 'lead');
  await orchestra.setProjectTools('beta', { hooks: true });
  await orchestra.ask('Check alpha', 'alpha');
  await orchestra.ask('Check beta', 'beta');
  assert.equal(seen.at(-2).projectId, 'alpha');
  assert.deepEqual(seen.at(-2).tools, { mcp: false, browser: false, hooks: false, memory: true });
  assert.equal(seen.at(-1).projectId, 'beta');
  assert.deepEqual(seen.at(-1).tools, { mcp: false, browser: false, hooks: true, memory: false });
});

test('a launched worker gets its own tools when another model differs', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-worker-own-tools-'));
  const folder = path.join(dir, 'project'); fs.mkdirSync(folder);
  const plan = { schema: 2, projects: [{ project: { id: 'demo', name: 'Demo', folder },
    revisions: [{ id: 1, direction: 'Build', open: [] }],
    tasks: [{ id: 't1', title: 'Build', detail: 'Build it', status: 'running', executionVersion: 2, revision_id: 1, source: 'chat' }] }] };
  const launches = []; let alive = true;
  const models = ['builder', 'other'].map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true }));
  const orchestra = createOrchestrator({ dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }),
    models, fetchPlans: null, inspectFolder: () => ({ git: false, dirty: [] }),
    spawnWorker: info => { launches.push(info); return { pid: 3000 + launches.length }; },
    isProcessAlive: () => alive, killWorker: () => { alive = false; }, askModel: async () => ({ answer: '{"done":true}' }) });
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });
  await orchestra.setProjectTools('demo', { browser: true, hooks: true });
  await orchestra.setProjectTools('demo', { mcp: true, browser: false }, 'builder');
  await orchestra.setProjectTools('demo', { memory: true, browser: false, hooks: false }, 'other');
  await orchestra.setMode('auto'); await orchestra.tick();
  await orchestra.setAutonomy('demo', true); await orchestra.autonomyTick();
  assert.ok(launches.length >= 1);
  const expected = {
    builder: { mcp: true, browser: false, hooks: true, memory: false },
    other: { mcp: false, browser: false, hooks: false, memory: true }
  };
  for (const info of launches) assert.deepEqual(info.worker.tools, expected[info.worker.id]);
});

test('overlapping tool saves keep every model, including a slow tick', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-tools-race-'));
  const plan = Plan.createProject(Plan.example(), { name: 'delta', direction: 'Build delta' });
  const ids = ['lead', 'a', 'b', 'c', 'd'];
  const orchestra = createOrchestrator({ dir, readPlan: async () => { await new Promise(resolve => setTimeout(resolve, 20)); return plan; },
    attempts: createAttemptEngine({ dir }),
    models: ids.map(id => ({ id, provider: 'codex', cli: 'gpt-6-luna', enabled: true, available: true })),
    fetchPlans: async () => { await new Promise(resolve => setTimeout(resolve, 20)); return []; },
    inspectFolder: () => ({ git: false }), askModel: async () => ({ answer: 'OK', actions: [] }) });
  t.after(async () => { await orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); });
  const writes = await Promise.all([
    orchestra.setProjectTools('delta', { browser: true, hooks: true }),
    ...ids.filter(id => id !== 'lead').map((id, index) => orchestra.setProjectTools('delta', { memory: index % 2 === 0, mcp: index % 2 === 1 }, id)),
    orchestra.setProjectTools('delta', null, 'a')
  ]);
  assert.ok(writes.every(result => result.ok));
  const settings = (await orchestra.snapshot('delta')).projectToolSettings.delta;
  assert.equal(settings.default.browser, true);
  assert.equal(settings.default.hooks, true);
  assert.equal(settings.models.a, undefined);
  for (const [index, id] of ['b', 'c', 'd'].entries()) {
    assert.equal(settings.models[id].memory, index % 2 === 1);
    assert.equal(settings.models[id].mcp, index % 2 === 0);
    assert.equal(settings.models[id].browser, true, 'the override copied the default that was saved');
  }
});

test('the lead prompt and launch follow that model\'s tools', () => {
  const { askPrompt, askCliArgs } = require('./runner.cjs');
  const tools = { mcp: false, browser: true, hooks: false, memory: true };
  const prompt = askPrompt({ question: 'Check the page', allowActions: true, model: { id: 'lead', label: 'Lead', tools } });
  assert.match(prompt, /Project tools that are off for your model: mcp, hooks \(/);
  const previous = process.env.PLANWHARF_CLAUDE;
  process.env.PLANWHARF_CLAUDE = process.execPath;
  try {
    const spec = askCliArgs({ question: 'Check the page', folder: os.tmpdir(), model: { id: 'lead', provider: 'claude', cli: 'claude-sonnet-4-6', alwaysApprove: true, tools } });
    assert.equal(spec.args.includes('--chrome'), true);
    assert.equal(spec.args.includes('--strict-mcp-config'), true);
    const settings = JSON.parse(spec.args[spec.args.indexOf('--settings') + 1]);
    assert.equal(settings.autoMemoryEnabled, true);
    assert.equal(settings.disableAllHooks, true);
  } finally {
    if (previous === undefined) delete process.env.PLANWHARF_CLAUDE; else process.env.PLANWHARF_CLAUDE = previous;
  }
});
