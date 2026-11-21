'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const chat = require('./chat-mode.cjs');
const { askPrompt } = require('./runner.cjs');
const { LIMITS, createSettings, nextStep } = require('./autonomy.cjs');
const { formatStatusMessage } = require('./phone.cjs');

test('Plan is the default and Pipeline is not a visible mode', () => {
  assert.equal(chat.DEFAULT, 'plan');
  assert.equal(chat.normalize(undefined), null);
  assert.equal(chat.normalize('plan'), 'plan');
  assert.equal(chat.normalize(' Talk '), 'talk');
  assert.equal(chat.normalize('AUTO'), 'auto');
  assert.equal(chat.normalize('pipeline'), null);
  assert.equal(chat.normalize('manual'), null);
  assert.deepEqual(chat.VISIBLE, ['talk', 'plan', 'auto']);
  assert.ok(chat.RESERVED.includes('pipeline'));
});

test('Talk and Plan are saved; Auto and Pipeline come back as Plan', () => {
  assert.equal(chat.persisted('talk'), 'talk');
  assert.equal(chat.persisted('plan'), 'plan');
  assert.equal(chat.persisted('auto'), 'plan');
  assert.equal(chat.persisted('pipeline'), 'plan');
  assert.equal(chat.persisted('nope'), 'plan');
  assert.equal(chat.persisted(undefined), 'plan');
});

test('Talk forbids a crew goal and drops one the lead still wrote', () => {
  const prompt = askPrompt({ question: 'What if the list was a map?', chatMode: 'talk', model: { label: 'Lead' }, state: {} });
  assert.match(prompt, /Do not suggest a goal for the crew/);
  assert.match(prompt, /Do not write a line that starts with "Suggested next goal:"/);
  assert.doesNotMatch(prompt, /end with one line that starts exactly with "Suggested next goal:"/);
  const kept = askPrompt({ question: 'Build the map', model: { label: 'Lead' }, state: {} });
  assert.match(kept, /Suggested next goal:/);
  const answer = 'A map could work.\n\nSuggested next goal: Replace the list with a map.';
  assert.equal(chat.suggestedGoal(answer), 'Replace the list with a map.');
  assert.equal(chat.suggestedGoal(chat.withoutSuggestion(answer)), null);
  assert.match(chat.withoutSuggestion(answer), /A map could work/);
});

test('orders select the same positions as the switch, and Pipeline is refused', () => {
  assert.deepEqual(chat.order('mode talk'), { action: 'mode', mode: 'talk', chatMode: 'talk' });
  assert.deepEqual(chat.order('modalità chat'), { action: 'mode', mode: 'talk', chatMode: 'talk' });
  assert.deepEqual(chat.order('modalita chat'), { action: 'mode', mode: 'talk', chatMode: 'talk' });
  assert.deepEqual(chat.order('mode plan'), { action: 'mode', mode: 'plan', chatMode: 'plan' });
  assert.deepEqual(chat.order('mode auto'), { action: 'mode', mode: 'auto', chatMode: 'auto' });
  assert.deepEqual(chat.order('Switch to automatic'), { action: 'mode', mode: 'auto', chatMode: 'auto' });
  assert.deepEqual(chat.order('Switch to manual'), { action: 'mode', mode: 'manual', chatMode: 'plan' });
  assert.deepEqual(chat.order('mode manual'), { action: 'mode', mode: 'manual', chatMode: 'plan' });
  assert.equal(chat.order('mode pipeline').action, 'reject');
  assert.equal(chat.order('set to pipeline').mode, 'pipeline');
  assert.equal(chat.order('How should the list be organised?'), null);
  assert.deepEqual(chat.order('Tell me about automatic mode').chatMode, 'auto');
  assert.equal(chat.label('talk'), 'Talk');
  assert.equal(chat.label('plan'), 'Plan');
  assert.equal(chat.label('auto'), 'Auto');
});

test('work in flight is the current goal, not a finished one', () => {
  assert.equal(chat.workInFlight(null), false);
  assert.equal(chat.workInFlight({ status: 'idle' }), false);
  assert.equal(chat.workInFlight({ status: 'complete', megaTask: { status: 'COMPLETE' } }), false);
  assert.equal(chat.workInFlight({ status: 'watching', canStop: true }), true);
  assert.equal(chat.workInFlight({ status: 'needs_approval', megaTask: { status: 'READY_FOR_APPROVAL' } }), true);
  assert.equal(chat.workInFlight({ status: 'idle', dispatch: [{ alive: true }] }), true);
});

function project(id, name) {
  return { project: { id, name, folder: '' }, revisions: [{ id: 1, direction: 'Ship ' + name, open: [] }], tasks: [] };
}

function openOrchestra(dir, plan, extra = {}) {
  const { createOrchestrator } = require('./orchestrator.js');
  const { createAttemptEngine } = require('./attempts.js');
  const launches = [], killed = [], asks = [], prepared = [];
  const live = new Set();
  const orchestra = createOrchestrator({
    dir, phase2: true, readPlan: async () => plan, attempts: createAttemptEngine({ dir }), fetchPlans: null,
    models: [{ id: 'lead', label: 'Lead', provider: 'codex', cli: 'gpt-6-luna', kind: 'default', enabled: true, available: true, roles: { orchestrator: true, worker: true } }],
    inspectFolder: () => ({ git: false, dirty: [] }),
    spawnWorker: info => { const pid = 4100 + launches.length; launches.push(info); live.add(pid); return { pid }; },
    isProcessAlive: pid => live.has(pid),
    killWorker: pid => { killed.push(pid); live.delete(pid); },
    askModel: async info => { asks.push(info); return { answer: extra.answer ? extra.answer(info) : 'Noted.\n\nSuggested next goal: Ship the fixture.' }; },
    prepareTask: async (projectId, question) => { prepared.push({ projectId, question }); return { ok: true, task: { id: 't-new', title: question } }; },
    ...extra.hooks
  });
  return { orchestra, launches, killed, live, asks, prepared };
}

test('each project defaults to Plan, and Talk and Plan are saved apart from Auto', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-mode-'));
  const folderA = path.join(dir, 'a'), folderB = path.join(dir, 'b');
  fs.mkdirSync(folderA); fs.mkdirSync(folderB);
  const plan = { schema: 2, projects: [project('alpha', 'Alpha'), project('beta', 'Beta')] };
  plan.projects[0].project.folder = folderA;
  plan.projects[1].project.folder = folderB;
  const first = openOrchestra(dir, plan);
  try {
    const initial = await first.orchestra.tick();
    assert.equal(initial.projects.find(row => row.id === 'alpha').chatMode, 'plan');
    assert.equal(initial.projects.find(row => row.id === 'beta').chatMode, 'plan');
    assert.equal(initial.projects[0].offerStart, true);
    await first.orchestra.setChatMode('alpha', 'talk');
    await first.orchestra.setChatMode('beta', 'plan');
    await first.orchestra.setChatMode('alpha', 'auto');
    const saved = await first.orchestra.snapshot();
    assert.equal(saved.projects.find(row => row.id === 'alpha').chatMode, 'auto');
    assert.equal(saved.projects.find(row => row.id === 'beta').chatMode, 'plan');
    assert.equal(saved.mode, 'auto');
    assert.equal(saved.autonomy.alpha.enabled, true);
    const refused = await first.orchestra.setChatMode('alpha', 'pipeline');
    assert.equal(refused.ok, false);
    assert.equal((await first.orchestra.snapshot()).projects.find(row => row.id === 'alpha').chatMode, 'auto');
    await first.orchestra.shutdown();
    const second = openOrchestra(dir, plan);
    const again = await second.orchestra.tick();
    assert.equal(again.projects.find(row => row.id === 'alpha').chatMode, 'plan', 'Auto does not survive a restart');
    assert.equal(again.projects.find(row => row.id === 'beta').chatMode, 'plan');
    assert.equal(again.autonomy.alpha?.enabled, false);
    await second.orchestra.setChatMode('beta', 'talk');
    await second.orchestra.shutdown();
    const third = openOrchestra(dir, plan);
    const kept = await third.orchestra.tick();
    assert.equal(kept.projects.find(row => row.id === 'beta').chatMode, 'talk');
    assert.equal(kept.projects.find(row => row.id === 'alpha').chatMode, 'plan');
    await third.orchestra.shutdown();
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});

test('Talk drops a crew goal and both start controls; Plan keeps them', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-talk-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const plan = {
    schema: 2,
    projects: [
      {
        schema: 1,
        project: { id: 'demo', name: 'Demo', folder },
        revisions: [{ id: 1, mode: 'manual', title: '', createdAt: new Date().toISOString(), direction: 'Ship it', state: '', closed: [], open: [] }],
        tasks: []
      }
    ]
  };
  const box = openOrchestra(dir, plan);
  try {
    await box.orchestra.setOrchestrator('lead');
    await box.orchestra.setChatMode('demo', 'talk');
    const asked = await box.orchestra.ask('What if this was a map?', 'demo');
    assert.equal(asked.ok, true);
    assert.equal(box.asks.at(-1).chatMode, 'talk');
    assert.match(box.asks.at(-1).question, /map/);
    assert.equal(chat.suggestedGoal(asked.answer), null);
    assert.doesNotMatch(asked.answer, /Suggested next goal:/);
    const row = asked.snapshot.projects.find(item => item.id === 'demo');
    assert.equal(row.chatMode, 'talk');
    assert.equal(row.crewGoal, null);
    assert.equal(row.offerUseGoal, false);
    assert.equal(row.offerStart, false);
    await box.orchestra.ask('implement the map', 'demo');
    assert.equal(box.prepared.length, 0, 'Talk does not turn a message into a task');
    await box.orchestra.setChatMode('demo', 'plan');
    const planned = await box.orchestra.snapshot();
    const planRow = planned.projects.find(item => item.id === 'demo');
    assert.equal(planRow.chatMode, 'plan');
    assert.equal(planRow.offerStart, true);
    assert.equal(planRow.crewGoal, null, 'the Talk reply no longer carries a goal');
  } finally { await box.orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('Auto starts a proposal, stays inside autonomy limits, and ends after the current goal', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-auto-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const plan = { schema: 2, projects: [{ project: { id: 'demo', name: 'Demo', folder }, revisions: [{ id: 1, direction: 'Ship it', open: [] }], tasks: [{ id: 't1', title: 'Build', detail: 'Build it', status: 'running', executionVersion: 2, revision_id: 1, source: 'chat' }] }] };
  const box = openOrchestra(dir, plan, { answer: () => '{"done":true}' });
  try {
    const capped = { ...createSettings('s', 't'), started: true, approved: Array.from({ length: LIMITS.maxMegaTasks }, (_, i) => 'old' + i) };
    assert.equal(nextStep({ mode: 'auto', sessionId: 's', settings: capped, row: { status: 'needs_approval', megaTaskId: 'new' } }).reason, 'MEGATASK_LIMIT');
    const started = await box.orchestra.setChatMode('demo', 'auto');
    assert.equal(started.ok, true);
    assert.equal(started.snapshot.mode, 'auto');
    assert.equal(started.snapshot.autonomy.demo.enabled, true);
    await box.orchestra.autonomyTick();
    assert.equal(box.launches.length, 1, 'Auto approves and starts the proposal');
    assert.equal(box.killed.length, 0);
    const running = await box.orchestra.snapshot();
    assert.equal(running.projects[0].canStop, true);
    await box.orchestra.setChatMode('demo', 'plan');
    assert.equal(box.killed.length, 0, 'moving the switch does not stop the crew');
    assert.equal((await box.orchestra.snapshot()).mode, 'manual', 'Plan does not keep automatic approval');
    assert.equal((await box.orchestra.snapshot()).projects[0].canStop, true);
    const before = box.launches.length;
    await box.orchestra.autonomyTick();
    assert.equal(box.launches.length, before, 'Auto does not start another goal after the switch');
    await box.orchestra.stop('demo');
    assert.ok(box.killed.length > 0, 'Stop the crew still stops at once');
    assert.equal((await box.orchestra.snapshot()).projects[0].canStop, false);
  } finally { await box.orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('chat orders stay on the same position as the switch', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-orders-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const plan = {
    schema: 2,
    projects: [
      {
        schema: 1,
        project: { id: 'demo', name: 'Demo', folder },
        revisions: [{ id: 1, mode: 'manual', title: '', createdAt: new Date().toISOString(), direction: 'Ship it', state: '', closed: [], open: [] }],
        tasks: []
      }
    ]
  };
  const box = openOrchestra(dir, plan);
  try {
    const talk = await box.orchestra.ask('modalità chat', 'demo');
    assert.match(talk.answer, /Talk/);
    assert.equal(talk.snapshot.projects[0].chatMode, 'talk');
    const planMode = await box.orchestra.ask('mode plan', 'demo');
    assert.match(planMode.answer, /Plan/);
    assert.equal(planMode.snapshot.projects[0].chatMode, 'plan');
    const manual = await box.orchestra.ask('mode manual', 'demo');
    assert.match(manual.answer, /Plan/);
    assert.equal(manual.snapshot.mode, 'manual');
    assert.equal(manual.snapshot.projects[0].chatMode, 'plan');
    const auto = await box.orchestra.ask('mode auto', 'demo');
    assert.match(auto.answer, /Auto/);
    assert.equal(auto.snapshot.projects[0].chatMode, 'auto');
    assert.equal(auto.snapshot.mode, 'auto');
    const hidden = await box.orchestra.ask('mode pipeline', 'demo');
    assert.match(hidden.answer, /Pipeline is not available/);
    assert.equal(hidden.snapshot.projects[0].chatMode, 'auto');
    assert.equal(hidden.snapshot.projects.some(row => row.chatMode === 'pipeline'), false);
  } finally { await box.orchestra.shutdown(); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('phone /status names the mode of each project', () => {
  const projects = [
    { id: 'a', name: 'Alpha', status: 'idle', chatMode: 'talk' },
    { id: 'b', name: 'Beta', status: 'watching', chatMode: 'auto' },
    { id: 'c', name: 'Gamma', status: 'idle' }
  ];
  const english = formatStatusMessage(projects, 'en');
  assert.match(english, /Alpha[\s\S]*<b>Mode:<\/b> Talk/);
  assert.match(english, /Beta[\s\S]*<b>Mode:<\/b> Auto/);
  assert.match(english, /Gamma[\s\S]*<b>Mode:<\/b> Plan/);
  const italian = formatStatusMessage(projects, 'it');
  assert.match(italian, /Alpha[\s\S]*<b>Modalità:<\/b> Chat/);
  assert.match(italian, /Beta[\s\S]*<b>Modalità:<\/b> Auto/);
  assert.match(italian, /Gamma[\s\S]*<b>Modalità:<\/b> Piano/);
});

test('multi-project Auto mode: switching one project to Plan leaves other project in Auto', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-multi-'));
  const folderA = path.join(dir, 'a'), folderB = path.join(dir, 'b');
  fs.mkdirSync(folderA); fs.mkdirSync(folderB);
  const plan = {
    schema: 2,
    projects: [
      { project: { id: 'alpha', name: 'Alpha', folder: folderA }, revisions: [{ id: 1, direction: 'Ship Alpha', open: [] }], tasks: [{ id: 'ta1', title: 'Build A', status: 'running', executionVersion: 2, revision_id: 1, source: 'chat' }] },
      { project: { id: 'beta', name: 'Beta', folder: folderB }, revisions: [{ id: 2, direction: 'Ship Beta', open: [] }], tasks: [{ id: 'tb1', title: 'Build B', status: 'running', executionVersion: 2, revision_id: 2, source: 'chat' }] }
    ]
  };
  const box = openOrchestra(dir, plan, { answer: () => '{"done":true}' });
  try {
    await box.orchestra.setChatMode('alpha', 'auto');
    await box.orchestra.setChatMode('beta', 'auto');
    let snap = await box.orchestra.snapshot();
    assert.equal(snap.mode, 'auto');
    assert.equal(snap.autonomy.alpha.enabled, true);
    assert.equal(snap.autonomy.beta.enabled, true);

    await box.orchestra.setChatMode('alpha', 'plan');
    snap = await box.orchestra.snapshot();
    assert.equal(snap.projects.find(p => p.id === 'alpha').chatMode, 'plan');
    assert.equal(snap.projects.find(p => p.id === 'beta').chatMode, 'auto');
    assert.equal(snap.mode, 'auto', 'global auto mode stays active because beta is in auto');
    assert.equal(snap.autonomy.beta.enabled, true, 'beta autonomy remains enabled');

    await box.orchestra.setChatMode('beta', 'plan');
    snap = await box.orchestra.snapshot();
    assert.equal(snap.projects.find(p => p.id === 'beta').chatMode, 'plan');
    assert.equal(snap.mode, 'manual', 'global mode becomes manual when all projects leave auto');
  } finally {
    await box.orchestra.shutdown();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('order parser handles punctuation, Italian variants and non-order phrases', () => {
  assert.deepEqual(chat.order('mode talk!'), { action: 'mode', mode: 'talk', chatMode: 'talk' });
  assert.deepEqual(chat.order('mode plan?'), { action: 'mode', mode: 'plan', chatMode: 'plan' });
  assert.deepEqual(chat.order('mode auto...'), { action: 'mode', mode: 'auto', chatMode: 'auto' });
  assert.deepEqual(chat.order('modalità chat.'), { action: 'mode', mode: 'talk', chatMode: 'talk' });

  assert.deepEqual(chat.order('modalità piano'), { action: 'mode', mode: 'plan', chatMode: 'plan' });
  assert.deepEqual(chat.order('modalita piano'), { action: 'mode', mode: 'plan', chatMode: 'plan' });
  assert.deepEqual(chat.order('passa a chat'), { action: 'mode', mode: 'talk', chatMode: 'talk' });
  assert.deepEqual(chat.order('passa a piano'), { action: 'mode', mode: 'plan', chatMode: 'plan' });
  assert.deepEqual(chat.order('passa ad auto'), { action: 'mode', mode: 'auto', chatMode: 'auto' });
  assert.deepEqual(chat.order('modalità manuale'), { action: 'mode', mode: 'manual', chatMode: 'plan' });
  assert.deepEqual(chat.order('modalita manuale'), { action: 'mode', mode: 'manual', chatMode: 'plan' });
  assert.deepEqual(chat.order('passa a manuale'), { action: 'mode', mode: 'manual', chatMode: 'plan' });

  assert.equal(chat.order('modalità pipeline').action, 'reject');
  assert.equal(chat.order('modalita pipeline').action, 'reject');
  assert.equal(chat.order('passa a pipeline').action, 'reject');

  assert.equal(chat.order(''), null);
  assert.equal(chat.order('   '), null);
  assert.equal(chat.order(null), null);
  assert.equal(chat.order(undefined), null);

  assert.equal(chat.suggestedGoal('Suggested next goal:   \n'), null);
  assert.equal(chat.suggestedGoal(null), null);

  assert.equal(chat.order('How should we plan the database?'), null);
  assert.equal(chat.order('Can you talk about the architecture?'), null);
});

test('state file recovers from corrupt or reserved chatModes on startup', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-chat-corrupt-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const plan = {
    schema: 2,
    projects: [
      {
        schema: 1,
        project: { id: 'demo', name: 'Demo', folder },
        revisions: [{ id: 1, mode: 'manual', title: '', createdAt: new Date().toISOString(), direction: 'Ship it', state: '', closed: [], open: [] }],
        tasks: []
      }
    ]
  };
  const statePath = path.join(dir, 'state.json');
  fs.writeFileSync(statePath, JSON.stringify({
    chatModes: { demo: 'pipeline', other: 'invalid', num: 42 },
    autonomy: {}
  }));
  const box = openOrchestra(dir, plan);
  try {
    const snap = await box.orchestra.tick();
    assert.equal(snap.projects.find(p => p.id === 'demo').chatMode, 'plan', 'pipeline recovers to plan');
    const setRes = await box.orchestra.setChatMode('demo', 'pipeline');
    assert.equal(setRes.ok, false);
  } finally {
    await box.orchestra.shutdown();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('server /api/orchestrator/chat-mode endpoint validates inputs and changes mode', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-server-mode-'));
  const folder = path.join(dir, 'project');
  fs.mkdirSync(folder);
  const planPath = path.join(dir, 'plan.json');
  const plan = {
    schema: 2,
    projects: [
      {
        schema: 1,
        project: { id: 'demo', name: 'Demo', folder },
        revisions: [{ id: 1, mode: 'manual', title: '', createdAt: new Date().toISOString(), direction: 'Ship it', state: '', closed: [], open: [] }],
        tasks: []
      }
    ]
  };
  fs.writeFileSync(planPath, JSON.stringify(plan));
  const { createServer } = require('../server.cjs');
  const server = createServer({ dir, planFile: planPath, phase2: true });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  const baseUrl = `http://127.0.0.1:${port}`;
  try {
    const nonJson = await fetch(`${baseUrl}/api/orchestrator/chat-mode`, {
      method: 'POST',
      headers: { 'content-type': 'text/plain' },
      body: 'hello'
    });
    assert.equal(nonJson.status, 415);

    const extra = await fetch(`${baseUrl}/api/orchestrator/chat-mode`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ projectId: 'demo', mode: 'talk', extra: true })
    });
    assert.equal(extra.status, 400);

    const badMode = await fetch(`${baseUrl}/api/orchestrator/chat-mode`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ projectId: 'demo', mode: 'pipeline' })
    });
    assert.equal(badMode.status, 400);

    const badProj = await fetch(`${baseUrl}/api/orchestrator/chat-mode`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ projectId: 'nonexistent', mode: 'talk' })
    });
    assert.equal(badProj.status, 400);

    const good = await fetch(`${baseUrl}/api/orchestrator/chat-mode`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ projectId: 'demo', mode: 'talk' })
    });
    assert.equal(good.status, 200);
    const snap = await good.json();
    assert.equal(snap.projects.find(p => p.id === 'demo').chatMode, 'talk');
  } finally {
    server.close();
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
