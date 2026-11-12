'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createSentinel } = require('./sentinel.js');
const { spawnSync } = require('node:child_process');
const { createOrchestrator, pickWorker, pickWorkers, jobKind, jobFromFacts, lastActivity, interpretActivity, sliceCount, answerQuestion, parseOrder, canOrchestrate, usageOf, planExhausted, ORCH_NAME, parseGrillQuestions } = require('./orchestrator.js');
const { parseGrokCredits, parseChatGptUsage } = require('./planUsage.js');
// A real folder: a linked project folder must exist on this machine, whatever the platform.
const DEMO = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orchestrator-demo-'));

const models = [
  { id: 'gemini', label: 'Gemini', enabled: true },
  { id: 'grok', label: 'Grok', enabled: true },
  { id: 'luna', label: 'Luna', enabled: false }
];

function notebook(overrides) {
  const folder = overrides && overrides.folder;
  const open = overrides && 'open' in overrides ? overrides.open : [];
  const tasks = overrides && 'tasks' in overrides ? overrides.tasks : [{ id: 'job-1', projectId: 'demo', revision_id: 1, title: 'Check the repo', detail: 'Inspect.', status: 'running' }];
  const fresh = Boolean(overrides && overrides.fresh);
  const r1 = { id: 1, mode: 'manual', title: 'R1', createdAt: '2026-10-09T10:00:00Z', direction: 'Ship the core.', state: 'Ready.', closed: [], open: fresh ? open : [] };
  const revisions = fresh
    ? [r1]
    : [r1, { id: 2, mode: 'grill', title: 'Grill me from R1', createdAt: '2026-10-09T10:05:00Z', direction: 'Ship the core.', state: 'Ready.', closed: [], open }];
  return {
    schema: 2,
    projects: [{
      schema: 1,
      project: { id: 'demo', name: 'Demo', ...(folder ? { folder } : {}) },
      revisions,
      tasks
    }]
  };
}

function make(plan, inspectFolder, extra) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-'));
  const attempts = createAttemptEngine({ dir });
  const sentinel = createSentinel(attempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') });
  const orchestra = createOrchestrator({
    phase2: true,
    readPlan: async () => plan,
    attempts,
    sentinel,
    models,
    dir,
    phase2: true,
    inspectFolder: inspectFolder || (() => ({ folder: plan.projects[0].project.folder, fileCount: 3, names: ['README.md', 'src', '.git'], git: true, head: 'ref: refs/heads/main' })),
    ...(extra || {})
  });
  return { dir, attempts, orchestra };
}

(async () => {
  assert.equal(jobKind({ title: 'Continue: latest repo changes' }), 'inspect');
  // The work is named after the goal; only without a goal does it continue from the repo.
  assert.equal(jobFromFacts({ direction: 'Ship the core.' }, { git: true, lastSubject: 'Add README' }).title, 'Ship the core.');
  assert.match(jobFromFacts({ direction: '' }, { git: true, lastSubject: 'Add README' }).title, /Continue: Add README/);
  assert.equal(jobFromFacts({ direction: 'Build a local calculator with tests.' }, { git: false }).title, 'Build a local calculator with tests.');
  assert.equal(pickWorker(models, { job: { title: 'inspect' }, skip: ['gemini'] }).id, 'grok');
  assert.equal(lastActivity('hello\nWriting calculator.py'), 'Writing calculator.py');
  assert.match(interpretActivity('Writing calculator.py\n', { fileCount: 3 }, true), /Writing project files/);
  assert.equal(sliceCount({ title: 'Implement the parser', detail: 'Write code.' }, {}, 3), 2);
  assert.equal(sliceCount({ title: 'Check the repo' }, {}, 3), 1);
  assert.equal(ORCH_NAME, 'Orchestrator');
  assert.deepEqual(parseGrillQuestions('["Is this the calculator slice? Recommended: yes."]'), ['Is this the calculator slice? Recommended: yes.']);
  assert.equal(canOrchestrate({ enabled: true, kind: 'premium' }), true);
  assert.equal(canOrchestrate({ enabled: true, kind: 'default' }), true);
  assert.equal(canOrchestrate({ enabled: true, kind: 'local' }), false);
  const asked = answerQuestion('What is Luna doing?', {
    projects: [{
      id: 'demo',
      name: 'Demo',
      label: 'Watching',
      orchestratorDoing: 'Monitoring 2 workers (1 alive).',
      dispatch: [{ label: 'GPT-5.6 Luna', modelId: 'luna', slice: 'Inspect', activity: 'Reading docs.', pid: 9, alive: true }],
      monitoring: { workers: 1, alive: 1, folderFiles: 4 }
    }]
  });
  assert.match(asked.answer, /Luna/);
  assert.match(asked.answer, /Inspect/);
  assert.deepEqual(usageOf({ used: 10, limit: 50 }), { used: 10, limit: 50, remaining: 40, pct: 20, leftPct: 80 });
  const grokParsed = parseGrokCredits({ config: { creditUsagePercent: 11, currentPeriod: { type: 'USAGE_PERIOD_TYPE_WEEKLY', end: '2026-10-13T19:49:33Z' } } });
  assert.equal(grokParsed.pct, 11);
  assert.equal(grokParsed.leftPct, 89);
  assert.equal(grokParsed.exhausted, false);
  const chatParsed = parseChatGptUsage({ plan_type: 'plus', rate_limit: { allowed: true, limit_reached: false, primary_window: { used_percent: 0 }, secondary_window: { used_percent: 7 } } });
  assert.equal(chatParsed.pct, 7);
  assert.equal(chatParsed.windows[1].pct, 7);
  const exhaustedChat = parseChatGptUsage({ rate_limit: { allowed: false, limit_reached: true, primary_window: { used_percent: 100 } } });
  assert.equal(exhaustedChat.exhausted, true);
  assert.equal(planExhausted([{ id: 'ChatGPT', readable: true, exhausted: true }], 'ChatGPT'), true);
  const quotaAsk = answerQuestion('How much quota is left?', {
    plans: [
      { id: 'ChatGPT', label: 'ChatGPT', readable: true, exhausted: false, pct: 7, leftPct: 93, windows: [{ name: '5-hour', pct: 0, leftPct: 100 }, { name: 'weekly', pct: 7, leftPct: 93 }] },
      { id: 'Grok', label: 'Grok', readable: true, exhausted: false, pct: 11, leftPct: 89, windows: [{ name: 'weekly', pct: 11, leftPct: 89 }] },
      { id: 'Gemini', label: 'Gemini', readable: false, reason: 'No Gemini login on this PC' }
    ],
    projects: [{ id: 'demo', name: 'Demo', label: 'Idle' }]
  });
  assert.match(quotaAsk.answer, /ChatGPT: 5-hour 0% used/);
  assert.match(quotaAsk.answer, /weekly 7% used/);
  assert.match(quotaAsk.answer, /Grok: weekly 11% used/);
  assert.match(quotaAsk.answer, /Gemini: No Gemini login/);
  assert.equal(parseOrder('What is Luna doing?', { models: [{ id: 'luna', label: 'Luna' }], projects: [] }), null);
  const offOrder = parseOrder('Turn off Luna', { models: [{ id: 'luna', label: 'Luna' }], projects: [{ id: 'demo', name: 'Demo' }] });
  assert.equal(offOrder.action, 'model');
  assert.equal(offOrder.id, 'luna');
  assert.equal(offOrder.enabled, false);
  assert.equal(parseOrder('Stop Demo', { models: [], projects: [{ id: 'demo', name: 'Demo' }] }).action, 'stop');
  const leadOrder = parseOrder('Use Grok as orchestrator', { models: [{ id: 'grok', label: 'Grok' }], projects: [] });
  assert.equal(leadOrder.action, 'lead');
  assert.equal(leadOrder.id, 'grok');
  assert.equal(parseOrder('Start Demo', { models: [], projects: [{ id: 'demo', name: 'Demo' }] }).action, 'start');
  // The clarification interview (Grill) is gone: its chat commands are ordinary messages now.
  assert.equal(parseOrder('Skip grill on Demo', { models: [], projects: [{ id: 'demo', name: 'Demo' }] }), null);
  assert.equal(parseOrder('Switch to automatic', { models: [], projects: [{ id: 'demo', name: 'Demo' }] }).action, 'mode');
  assert.equal(parseOrder('Switch to automatic', { models: [], projects: [] }).mode, 'auto');
  assert.equal(parseOrder('Switch to manual', { models: [], projects: [] }).mode, 'manual');
  assert.equal(parseOrder('Grill off', { models: [], projects: [] }), null);
  assert.equal(parseOrder('How far did we go with Demo?', { models: [], projects: [{ id: 'demo', name: 'Demo', status: 'idle' }] }), null);
  assert.equal(parseOrder('Implement the parser on Demo', { models: [], projects: [{ id: 'demo', name: 'Demo', status: 'idle' }] }), null);
  assert.equal(parseOrder('Smista i worker su Demo', { models: [], projects: [{ id: 'demo', name: 'Demo', status: 'needs_approval' }] }), null);
  assert.equal(parseOrder('Continue Demo', { models: [], projects: [{ id: 'demo', name: 'Demo', status: 'watching' }] }), null);
  assert.equal(parseOrder('Implement the parser on Demo', { models: [], projects: [{ id: 'demo', name: 'Demo', status: 'checkpointed' }] }), null);
  assert.equal(pickWorker(models, { job: { title: 'Check the repo', detail: 'Inspect.' } }).id, 'gemini');
  const configuredFull = require("./provider-catalog.cjs").loadCatalog(require("../models.json")).models;
  assert.ok(configuredFull.filter(model => model.adapter === 'antigravity').every(model => model.available === false), 'Configured Antigravity IDs require fresh CLI verification');
  // Quota routing uses simulated verified access; this fixture never invokes a provider.
  const full = configuredFull.map(model => model.adapter === 'antigravity' ? { ...model, available: true, unavailableReason: null, availabilityVerified: true } : model);
  assert.equal(pickWorker(full, { job: { title: 'Check the repo' }, plans: [{ id: 'ChatGPT', readable: true, exhausted: true }] }).plan, 'Gemini');
  assert.equal(pickWorker(full, { job: { title: 'Check the repo' }, plans: [{ id: 'ChatGPT', readable: true, exhausted: true }] }).id, 'gemini-flash');
  assert.equal(pickWorker(full, { job: { title: 'Check the repo' } }).id, 'luna');
  const planLive = make(notebook({ folder: DEMO }), null, {
    models: full,
    fetchPlans: async () => [
      { id: 'ChatGPT', label: 'ChatGPT', readable: true, exhausted: true, pct: 100, leftPct: 0, windows: [{ name: 'weekly', pct: 100, leftPct: 0 }] },
      { id: 'Grok', label: 'Grok', readable: true, exhausted: false, pct: 11, leftPct: 89, windows: [{ name: 'weekly', pct: 11, leftPct: 89 }] },
      { id: 'Gemini', label: 'Gemini', readable: false, reason: 'No Gemini login on this PC' }
    ]
  });
  const planSnap = await planLive.orchestra.tick();
  assert.equal(planSnap.plans.find(item => item.id === 'ChatGPT').exhausted, true);
  assert.equal(planSnap.fleet.find(item => item.id === 'luna').exhausted, true);
  assert.equal(planSnap.fleet.find(item => item.id === 'grok-fast').exhausted, false);
  assert.equal(pickWorker(full, { job: { title: 'Implement the parser', detail: 'Write code.' } }).id, 'luna');
  assert.equal(pickWorker(full, { job: { title: 'Hard escalation', detail: 'complex premium fix' } }).id, 'luna');
  assert.equal(pickWorker(full, { job: { title: 'Hard escalation', detail: 'complex premium fix' }, skip: ['luna'] }).id, 'gpt-5.5');
  assert.equal(pickWorker(full, { job: { title: 'Hard escalation', detail: 'complex' }, usage: { 'gpt-6-astra': { used: 20 } } }).id, 'luna');
  assert.ok(full.some(item => item.id === 'gpt-6-astra' && item.kind === 'premium' && item.available === true));
  assert.equal(canOrchestrate(full.find(item => item.id === 'gpt-6-astra')), true);
  const crew = pickWorkers(full, { job: { title: 'Implement the parser', detail: 'Write code.' }, facts: { dirty: ['a', 'b', 'c', 'd'] } });
  assert.equal(crew.length, 2);
  assert.equal(crew[0].id, 'luna');
  assert.ok(full.filter(item => item.plan === 'ChatGPT').length > 1);
  assert.ok(full.filter(item => item.plan === 'Grok').length > 1);
  assert.ok(full.filter(item => item.plan === 'Gemini').every(item => item.enabled === true));

  const questions = make(notebook({ folder: DEMO, open: ['What is in scope?'] }));
  let snap = await questions.orchestra.tick();
  // Open questions no longer hold the project back: the lead decides them.
  assert.notEqual(snap.projects[0].status, 'waiting_on_you');
  assert.equal(questions.attempts.getAttempts().length, 0);
  const askedLive = await questions.orchestra.ask('Are you monitoring the worker?');
  assert.equal(askedLive.ok, true);
  assert.match(askedLive.answer, /orchestrator|Grill|waiting|Demo/i);
  const askedNext = await questions.orchestra.ask('What is next?');
  assert.match(askedNext.answer, /Next:|waiting for/i);
  assert.notEqual(askedLive.answer, askedNext.answer);
  const cheapLead = await questions.orchestra.setOrchestrator('luna');
  assert.equal(cheapLead.ok, false);
  const setLead = await questions.orchestra.setOrchestrator('grok');
  assert.equal(setLead.ok, true);
  const leadSnap = await questions.orchestra.tick();
  assert.equal(leadSnap.orchestratorId, 'grok');
  assert.ok(leadSnap.projects[0].crew.some(person => person.role === 'Orchestrator' && person.name === 'Grok'));
  const talkDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-ask-'));
  const talkAttempts = createAttemptEngine({ dir: talkDir });
  const talkPlan = notebook({ folder: DEMO, open: ['What is in scope?'] });
  let lastAskState = null;
  let lastAskInfo = null;
  const talkOrch = createOrchestrator({
    phase2: true,
    readPlan: async () => talkPlan,
    attempts: talkAttempts,
    sentinel: createSentinel(talkAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: talkDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    askModel: async (info) => {
      lastAskInfo = info;
      lastAskState = info.state;
      return 'MODEL:' + info.question;
    }
  });
  await talkOrch.setOrchestrator('gemini');
  const spokenA = await talkOrch.ask('What is Luna doing?');
  const spokenB = await talkOrch.ask('Why two workers?');
  assert.equal(spokenA.answer, 'MODEL:What is Luna doing?');
  assert.equal(spokenB.answer, 'MODEL:Why two workers?');
  assert.notEqual(spokenA.answer, spokenB.answer);
  assert.ok(Array.isArray(lastAskState.history));
  assert.equal(lastAskState.history[0].user, 'What is Luna doing?');
  assert.equal(lastAskInfo.folder, DEMO);
  assert.equal(lastAskState.focus.folder, DEMO);

  const muteDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-mute-'));
  const muteAttempts = createAttemptEngine({ dir: muteDir });
  const mutePlan = notebook({ folder: DEMO, open: [], tasks: [] });
  const muteOrch = createOrchestrator({
    phase2: true,
    readPlan: async () => mutePlan,
    attempts: muteAttempts,
    sentinel: createSentinel(muteAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: muteDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    askModel: async () => 'SHOULD-NOT-RUN'
  });
  const noLeadAsk = await muteOrch.ask('How far did we go with Demo?');
  assert.match(noLeadAsk.answer, /Pick an orchestrator/i);
  assert.doesNotMatch(noLeadAsk.answer, /SHOULD-NOT-RUN/);
  await muteOrch.setOrchestrator('gemini');
  const silentOrch = createOrchestrator({
    phase2: true,
    readPlan: async () => mutePlan,
    attempts: muteAttempts,
    sentinel: createSentinel(muteAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: muteDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 21, names: ['README.md'], git: true, head: '' }),
    askModel: async () => ''
  });
  await silentOrch.setOrchestrator('gemini');
  const silentAsk = await silentOrch.ask('How far did we go with Demo?');
  assert.match(silentAsk.answer, /no answer|CLI/i);
  assert.doesNotMatch(silentAsk.answer, /Evidence complete|Speaking as/i);

  const thinkDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-think-'));
  const thinkAttempts = createAttemptEngine({ dir: thinkDir });
  const thinkPlan = notebook({ folder: DEMO, open: [], tasks: [] });
  let sawProgress = false;
  const thinkOrch = createOrchestrator({
    phase2: true,
    readPlan: async () => thinkPlan,
    attempts: thinkAttempts,
    sentinel: createSentinel(thinkAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: thinkDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: true, head: '' }),
    askModel: async ({ onProgress }) => {
      if (typeof onProgress === 'function') {
        onProgress({ thinking: 'I will inspect git.', answer: '', ms: 20 });
        sawProgress = true;
      }
      const live = await thinkOrch.tick();
      assert.equal(live.thinking.thinking, 'I will inspect git.');
      assert.ok(Number.isFinite(live.thinking.ms));
      return { answer: 'RaceEngineer is a local prototype.', thinking: 'I will inspect git.', ms: 42100 };
    }
  });
  await thinkOrch.setOrchestrator('gemini');
  const thought = await thinkOrch.ask('How far did we go with Demo?');
  assert.equal(sawProgress, true);
  assert.equal(thought.answer, 'RaceEngineer is a local prototype.');
  assert.equal(thought.thinking, 'I will inspect git.');
  assert.equal(thought.ms, 42100);
  assert.equal(thought.asks[0].ms, 42100);
  const talkMs = await talkOrch.ask('Turn off Grok');
  assert.equal(talkMs.order, 'model');
  assert.ok(Number.isFinite(talkMs.ms));

  let abortHits = 0;
  let resolveHang;
  const hangDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-abort-'));
  const hangAttempts = createAttemptEngine({ dir: hangDir });
  const hangPlan = notebook({ folder: DEMO, open: [], tasks: [] });
  const hangOrch = createOrchestrator({
    phase2: true,
    readPlan: async () => hangPlan,
    attempts: hangAttempts,
    sentinel: createSentinel(hangAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: hangDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: true, head: '' }),
    askModel: () => new Promise(resolve => { resolveHang = resolve; }),
    abortAsk: () => {
      abortHits += 1;
      if (typeof resolveHang === 'function') resolveHang({ answer: 'late', thinking: '', ms: 1 });
    }
  });
  await hangOrch.setOrchestrator('gemini');
  const pendingAsk = hangOrch.ask('How far did we go with Demo?');
  let liveAsk = null;
  for (let i = 0; i < 50; i++) {
    liveAsk = await hangOrch.tick();
    if (liveAsk.asking) break;
    await new Promise(resolve => setTimeout(resolve, 10));
  }
  assert.equal(liveAsk.asking, true);
  const abortedAsk = await hangOrch.abortAsk();
  assert.equal(abortHits, 1);
  assert.equal(abortedAsk.aborted, true);
  assert.equal(abortedAsk.asks[0].answer, 'Stopped.');
  assert.equal(abortedAsk.asks[0].order, 'abort');
  const finishedAsk = await pendingAsk;
  assert.equal(finishedAsk.cancelled, true);
  assert.equal((await hangOrch.tick()).asking, false);
  const afterStop = await hangOrch.ask('Switch to manual');
  assert.equal(afterStop.order, 'mode');
  assert.equal(afterStop.cancelled, undefined);

  let dispatchedModel = false;
  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-dispatch-'));
  const workAttempts = createAttemptEngine({ dir: workDir });
  const workPlan = notebook({ folder: DEMO, open: [], tasks: [] });
  const workOrch = createOrchestrator({
    phase2: true,
    readPlan: async () => workPlan,
    attempts: workAttempts,
    sentinel: createSentinel(workAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: workDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: true, head: '', lastSubject: 'Add README' }),
    askModel: async () => {
      dispatchedModel = true;
      return 'SHOULD-NOT-IMPLEMENT';
    }
  });
  await workOrch.setGrillPropose(false);
  await workOrch.setOrchestrator('gemini');
  const dispatched = await workOrch.ask('Start Demo');
  assert.equal(dispatchedModel, false);
  assert.equal(dispatched.order, 'start');
  assert.match(dispatched.answer, /dispatch|assign workers/i);
  assert.doesNotMatch(dispatched.answer, /SHOULD-NOT-IMPLEMENT/);

  const idle = make(notebook({ folder: DEMO, open: [], tasks: [] }));
  snap = await idle.orchestra.tick();
  assert.ok(Array.isArray(snap.fleet));
  assert.ok(Array.isArray(snap.plans));
  assert.equal(typeof snap.fleet[0].exhausted, 'boolean');
  assert.ok(Array.isArray(snap.interventions));
  assert.equal(typeof snap.projects[0].analysis, 'string');
  const switched = await idle.orchestra.ask('Turn off Gemini');
  assert.equal(switched.ok, true);
  assert.equal(switched.order, 'model');
  assert.match(switched.answer, /off/i);
  const autoChat = await idle.orchestra.ask('Switch to automatic');
  assert.equal(autoChat.order, 'mode');
  assert.equal(autoChat.snapshot.mode, 'auto');
  assert.equal(parseOrder('Grill me', { models: [], projects: [] }), null);
  assert.equal(switched.snapshot.models.find(item => item.id === 'gemini').enabled, false);
  assert.equal(snap.projects[0].status, 'idle');
  assert.equal(snap.projects[0].waitingFor, 'Auto start');
  // The next step of an idle project is written for the chat view (it no longer names "Auto start").
  assert.match(snap.projects[0].next, /Living Plan|let the crew do it/i);
  assert.equal(snap.projects[0].canStart, true);

  const fresh = make(notebook({ folder: DEMO, open: [], tasks: [], fresh: true }));
  snap = await fresh.orchestra.tick();
  // Without the interview a fresh project goes straight to planning.
  assert.equal(snap.grillPropose, false);
  assert.equal(snap.projects[0].status, 'idle');
  assert.equal(snap.projects[0].canStart, true);
  const proposed = await fresh.orchestra.startAutoTask('demo');
  assert.equal(proposed.ok, true);
  assert.equal(proposed.phase, 'plan');

  const noFolder = make(notebook({ open: [] }));
  snap = await noFolder.orchestra.tick();
  assert.equal(snap.projects[0].status, 'blocked_no_folder');

  const ready = make(notebook({ folder: DEMO, open: [] }));
  snap = await ready.orchestra.tick();
  assert.equal(snap.projects[0].status, 'needs_approval');
  assert.equal(snap.projects[0].canStart, true);
  assert.match(snap.projects[0].doing, /Approve/);
  assert.equal(snap.projects[0].waitingFor, 'Your Approve run');
  assert.match(snap.projects[0].next, /Approve/);
  assert.equal(snap.projects[0].step, 'approve');
  assert.ok(snap.projects[0].crew.some(person => person.role === 'Worker' && person.name !== 'None yet'));
  assert.equal(ready.attempts.getAttempts().length, 0);

  // Work saved by the old (pre-Phase 2) path cannot be approved: it is started again with Let the crew do it.
  const approved = await ready.orchestra.approve('demo');
  assert.equal(approved.ok, false);
  assert.match(approved.reason, /older version of Planwharf/);
  assert.equal(ready.attempts.getAttempts().length, 0, 'nothing was dispatched');

  const grokOnly = make(notebook({ folder: DEMO, open: [] }));
  const grokOrchestra = createOrchestrator({
    phase2: true,
    readPlan: async () => notebook({ folder: DEMO, open: [] }),
    attempts: grokOnly.attempts,
    sentinel: createSentinel(grokOnly.attempts, { isAlive: () => true }),
    models: [{ id: 'grok', label: 'Grok', enabled: true }],
    dir: grokOnly.dir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['a'], git: false, head: '' })
  });
  snap = await grokOrchestra.tick();
  assert.equal(snap.projects[0].status, 'needs_approval');
  assert.equal(snap.projects[0].doing.includes('Grok') || snap.mode === 'manual', true);

  const startPlan = notebook({ folder: DEMO, open: [], tasks: [] });
  const startBox = make(startPlan);
  await startBox.orchestra.setMode('auto');
  await startBox.orchestra.setGrillPropose(false);
  // The clarification interview is off by default since the chat view; these checks turn it on to keep its engine path covered.
  await questions.orchestra.setGrillPropose(true, 'me');
  const questionsStart = await questions.orchestra.startAutoTask('demo');
  assert.equal(questionsStart.ok, true);
  assert.equal(questionsStart.phase, 'grill');
  assert.equal(questionsStart.clearJob, true);
  const openIdle = make(notebook({ folder: DEMO, open: ['What is in scope?'], tasks: [] }));
  await openIdle.orchestra.setGrillPropose(true, 'me');
  const grilledOpen = await openIdle.orchestra.startAutoTask('demo');
  assert.equal(grilledOpen.ok, true);
  assert.equal(grilledOpen.phase, 'grill');
  assert.equal(grilledOpen.job, null);
  await ready.orchestra.setGrillPropose(true, 'me');
  const readyRestart = await ready.orchestra.startAutoTask('demo');
  assert.equal(readyRestart.ok, true);
  assert.equal(readyRestart.clearJob, true);
  assert.equal(readyRestart.phase, 'grill');
  const noFolderIdle = make(notebook({ open: [], tasks: [] }));
  assert.equal((await noFolderIdle.orchestra.startAutoTask('demo')).reason, 'no folder');
  const missing = await startBox.orchestra.startAutoTask('nope');
  assert.equal(missing.ok, false);
  assert.equal(missing.reason, 'project not found');
  const prepared = await startBox.orchestra.startAutoTask('demo');
  assert.equal(prepared.ok, true);
  assert.equal(prepared.job.title, 'Ship the core.', 'the work is named after the goal');
  assert.match(prepared.job.detail, /Goal: Ship the core/);

  // The chat over HTTP: send, stop a reply, the interview commands as ordinary messages, the pipeline.
  const { createServer: realCreateServer } = require('../server.cjs');
  // HTTP scenarios use one stable worker; the production roster is tested above.
  const createServer = options => realCreateServer({ models: full.map(item => ({ ...item, enabled: item.id === 'grok-4-6' })), fetchPlans: async () => [], ...options });
  const chatDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-chat-'));
  const chatRepo = path.join(chatDir, 'repo');
  fs.mkdirSync(chatRepo);
  fs.writeFileSync(path.join(chatRepo, 'README.md'), 'demo\n');
  const chatPlanFile = path.join(chatDir, 'plan.json');
  fs.writeFileSync(chatPlanFile, JSON.stringify(notebook({ folder: chatRepo, open: [], tasks: [] })) + '\n');
  const chatServer = createServer({ planFile: chatPlanFile, attemptsDir: path.join(chatDir, 'attempts') });
  await new Promise(resolve => chatServer.listen(0, '127.0.0.1', resolve));
  const chatBase = `http://127.0.0.1:${chatServer.address().port}`;
  try {
    const grillOffChat = await fetch(chatBase + '/api/orchestrator/grill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ propose: false }) });
    assert.equal(grillOffChat.status, 200);
    const chatMode = await fetch(chatBase + '/api/orchestrator/ask', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question: 'Switch to automatic' }) });
    assert.equal(chatMode.status, 200);
    const chatModeBody = await chatMode.json();
    assert.equal(chatModeBody.order, 'mode');
    assert.equal(chatModeBody.snapshot.mode, 'auto');
    const chatStart = await fetch(chatBase + '/api/orchestrator/ask', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question: 'Start Demo' }) });
    assert.equal(chatStart.status, 200);
    const chatStartBody = await chatStart.json();
    assert.equal(chatStartBody.order, 'start');
    assert.equal(chatStartBody.snapshot.projects[0].status, 'needs_approval');
    assert.match(chatStartBody.answer, /Plan ready/i);
    const chatPlan = await (await fetch(chatBase + '/api/plan')).json();
    assert.equal(chatPlan.plan.projects[0].tasks.filter(task => task.status === 'running').length, 1);
    assert.equal(chatStartBody.asks.length, 2);
  } finally {
    await new Promise(resolve => chatServer.close(resolve));
  }

  const abortDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-askcancel-'));
  const abortRepo = path.join(abortDir, 'repo');
  fs.mkdirSync(abortRepo);
  fs.writeFileSync(path.join(abortRepo, 'README.md'), 'demo\n');
  const abortPlanFile = path.join(abortDir, 'plan.json');
  fs.writeFileSync(abortPlanFile, JSON.stringify(notebook({ folder: abortRepo, open: [], tasks: [] })) + '\n');
  let httpAbort = 0;
  let resolveHttpAsk;
  const abortServer = createServer({
    planFile: abortPlanFile,
    attemptsDir: path.join(abortDir, 'attempts'),
    askModel: () => new Promise(resolve => { resolveHttpAsk = resolve; }),
    abortAsk: () => {
      httpAbort += 1;
      if (typeof resolveHttpAsk === 'function') resolveHttpAsk({ answer: 'late', thinking: '', ms: 1 });
    }
  });
  await new Promise(resolve => abortServer.listen(0, '127.0.0.1', resolve));
  const abortBase = `http://127.0.0.1:${abortServer.address().port}`;
  try {
    const leadSet = await fetch(abortBase + '/api/orchestrator/lead', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: 'grok-4-6' }) });
    assert.equal(leadSet.status, 200);
    const hanging = fetch(abortBase + '/api/orchestrator/ask', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question: 'How far did we go with Demo?' }) });
    let asking = false;
    for (let i = 0; i < 50; i++) {
      const snap = await (await fetch(abortBase + '/api/orchestrator')).json();
      if (snap.asking) {
        asking = true;
        break;
      }
      await new Promise(resolve => setTimeout(resolve, 20));
    }
    assert.equal(asking, true);
    const cancelled = await fetch(abortBase + '/api/orchestrator/ask/cancel', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
    assert.equal(cancelled.status, 200);
    const cancelledBody = await cancelled.json();
    assert.equal(cancelledBody.asks[0].answer, 'Stopped.');
    assert.equal(httpAbort, 1);
    const hangingBody = await (await hanging).json();
    assert.equal(hangingBody.cancelled, true);
  } finally {
    await new Promise(resolve => abortServer.close(resolve));
  }

  console.log('OK: orchestrator helpers, the lead in the chat, idle and ready projects, auto start, chat over HTTP (Phase 2).');
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
