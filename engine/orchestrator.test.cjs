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
    readPlan: async () => plan,
    attempts,
    sentinel,
    models,
    dir,
    inspectFolder: inspectFolder || (() => ({ folder: plan.projects[0].project.folder, fileCount: 3, names: ['README.md', 'src', '.git'], git: true, head: 'ref: refs/heads/main' })),
    ...(extra || {})
  });
  return { dir, attempts, orchestra };
}

(async () => {
  assert.equal(jobKind({ title: 'Continue: latest repo changes' }), 'inspect');
  assert.match(jobFromFacts({ direction: 'Ship the core.' }, { git: true, lastSubject: 'Add README' }).title, /Continue: Add README/);
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

  const approved = await ready.orchestra.approve('demo');
  assert.equal(approved.ok, true);
  assert.equal(approved.snapshot.projects[0].status, 'watching');
  assert.equal(approved.snapshot.projects[0].modelId, 'gemini');
  assert.match(approved.snapshot.projects[0].waitingFor, /COMPLETE|Assigned|output/);
  assert.ok(approved.snapshot.projects[0].workerLabel);
  assert.equal(approved.snapshot.projects[0].step, 'watch');
  assert.ok(approved.snapshot.projects[0].crew.some(person => person.role === 'Worker' && person.name !== 'None yet'));
  assert.equal(ready.attempts.getAttempts().length, 1);
  assert.equal(ready.attempts.getAttempts()[0].modelId, 'gemini');
  const again = await ready.orchestra.tick();
  assert.equal(again.projects[0].status, 'watching');
  assert.equal(ready.attempts.getAttempts().length, 1);

  const grokOnly = make(notebook({ folder: DEMO, open: [] }));
  const grokOrchestra = createOrchestrator({
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

  const autoRun = make(notebook({ folder: DEMO, open: [] }));
  snap = await autoRun.orchestra.setMode('auto');
  assert.equal(snap.mode, 'auto');
  assert.equal(snap.projects[0].status, 'watching');
  assert.equal(autoRun.attempts.getAttempts().length, 1);
  assert.ok(snap.models.some(item => item.id === 'gemini' && item.used > 0));

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
  assert.match(prepared.job.title, /Continue/);
  assert.match(prepared.job.detail, /Goal: Ship the core/);
  startPlan.projects[0].tasks.push({
    id: 'job-auto',
    projectId: 'demo',
    revision_id: prepared.job.revision_id,
    title: prepared.job.title,
    detail: prepared.job.detail,
    status: 'running'
  });
  snap = await startBox.orchestra.tick();
  assert.equal(snap.mode, 'auto');
  assert.equal(snap.projects[0].status, 'needs_approval');
  assert.equal(startBox.attempts.getAttempts().length, 0);
  assert.match(snap.projects[0].plan.join('\n'), /Proceed with the plan/);
  const startedRun = await startBox.orchestra.approve('demo');
  assert.equal(startedRun.ok, true);
  assert.equal(startedRun.snapshot.projects[0].status, 'watching');
  assert.equal(startedRun.snapshot.projects[0].modelId, 'gemini');
  assert.equal(startBox.attempts.getAttempts().length, 1);

  const spawnDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-spawn-'));
  const spawnAttempts = createAttemptEngine({ dir: spawnDir });
  const spawned = [];
  const spawnPlan = notebook({ folder: DEMO, open: [] });
  const spawnOrch = createOrchestrator({
    readPlan: async () => spawnPlan,
    attempts: spawnAttempts,
    sentinel: createSentinel(spawnAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: spawnDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: info => {
      spawned.push(info);
      return { pid: 4242, logPath: path.join(spawnDir, 'logs', 'attempt.log') };
    },
    isProcessAlive: pid => pid === 4242
  });
  const spawnApproved = await spawnOrch.approve('demo');
  assert.equal(spawnApproved.ok, true);
  assert.equal(spawnApproved.snapshot.projects[0].status, 'watching');
  assert.equal(spawnApproved.snapshot.projects[0].workerPid, 4242);
  assert.equal(spawnApproved.snapshot.projects[0].workerAlive, true);
  assert.equal(spawned.length, 1);
  assert.equal(spawned[0].folder, DEMO);
  assert.equal(spawnOrch.pidFor(spawnAttempts.getAttempts()[0]), 4242);

  const splitDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-split-'));
  const splitAttempts = createAttemptEngine({ dir: splitDir });
  const splitPlan = notebook({ folder: DEMO, open: [] });
  splitPlan.projects[0].tasks[0].title = 'Implement the parser';
  splitPlan.projects[0].tasks[0].detail = 'Write code.';
  const splitCalls = [];
  let splitPid = 800;
  const splitOrch = createOrchestrator({
    readPlan: async () => splitPlan,
    attempts: splitAttempts,
    sentinel: createSentinel(splitAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: splitDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: info => {
      splitCalls.push(info);
      return { pid: splitPid++, logPath: path.join(splitDir, 'logs', `${info.attemptId}.log`) };
    },
    isProcessAlive: () => true
  });
  const splitApproved = await splitOrch.approve('demo');
  assert.equal(splitApproved.ok, true);
  assert.equal(splitApproved.snapshot.projects[0].status, 'watching');
  assert.ok(splitApproved.snapshot.projects[0].dispatch.length >= 2);
  assert.equal(splitCalls.length, splitApproved.snapshot.projects[0].dispatch.length);
  assert.match(splitApproved.snapshot.projects[0].orchestratorDoing, /Monitoring/);
  assert.ok(splitApproved.snapshot.projects[0].crew.every(person => person.name !== 'Planwharf'));

  let failClock = '2026-10-09T17:00:00.000Z';
  const failDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-fail-'));
  fs.mkdirSync(path.join(failDir, 'logs'), { recursive: true });
  const failAttempts = createAttemptEngine({ dir: failDir });
  const failPlan = notebook({ folder: DEMO, open: [] });
  const killed = [];
  let nextPid = 500;
  const failOrch = createOrchestrator({
    readPlan: async () => failPlan,
    attempts: failAttempts,
    sentinel: createSentinel(failAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: failDir,
    now: () => failClock,
    stallMs: 1000,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: info => {
      const logPath = path.join(failDir, 'logs', `${info.attemptId}.log`);
      fs.writeFileSync(logPath, 'started\n');
      return { pid: nextPid++, logPath };
    },
    isProcessAlive: () => true,
    killWorker: pid => { killed.push(pid); }
  });
  const failApproved = await failOrch.approve('demo');
  assert.equal(failApproved.snapshot.projects[0].modelId, 'gemini');
  assert.equal(failApproved.snapshot.projects[0].workerPid, 500);
  assert.match(failApproved.snapshot.projects[0].workerTail || failApproved.snapshot.projects[0].workerActivity, /started|Assigned/);
  failClock = '2026-10-09T17:00:03.000Z';
  snap = await failOrch.tick();
  assert.equal(snap.projects[0].status, 'watching');
  assert.equal(snap.projects[0].modelId, 'grok');
  assert.equal(snap.projects[0].workerPid, 501);
  assert.deepEqual(snap.projects[0].tried, ['gemini']);
  assert.equal(killed[0], 500);
  assert.ok(snap.projects[0].log.some(line => /Switched from Gemini to Grok/.test(line.text)));

  const deadDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-dead-'));
  const deadAttempts = createAttemptEngine({ dir: deadDir });
  const deadPlan = notebook({ folder: DEMO, open: [] });
  let deadPid = 700;
  const deadOrch = createOrchestrator({
    readPlan: async () => deadPlan,
    attempts: deadAttempts,
    sentinel: createSentinel(deadAttempts, { isAlive: () => false }),
    models,
    dir: deadDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: deadPid++, logPath: path.join(deadDir, 'logs', 'w.log') }),
    isProcessAlive: () => false,
    killWorker: () => {}
  });
  const deadApproved = await deadOrch.approve('demo');
  assert.equal(deadApproved.snapshot.projects[0].status, 'watching');
  assert.equal(deadApproved.snapshot.projects[0].modelId, 'grok');
  assert.deepEqual(deadApproved.snapshot.projects[0].tried, ['gemini']);

  const noneDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-none-'));
  const noneAttempts = createAttemptEngine({ dir: noneDir });
  const nonePlan = notebook({ folder: DEMO, open: [] });
  const noneOrch = createOrchestrator({
    readPlan: async () => nonePlan,
    attempts: noneAttempts,
    sentinel: createSentinel(noneAttempts, { isAlive: () => false }),
    models: [{ id: 'gemini', label: 'Gemini', enabled: true }],
    dir: noneDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: 900, logPath: path.join(noneDir, 'logs', 'w.log') }),
    isProcessAlive: () => false,
    killWorker: () => {}
  });
  const noneApproved = await noneOrch.approve('demo');
  assert.equal(noneApproved.snapshot.projects[0].status, 'checkpointed');
  assert.equal(noneApproved.snapshot.projects[0].canResume, true);
  assert.ok(noneApproved.snapshot.projects[0].checkpoint);
  assert.equal(noneApproved.snapshot.projects[0].checkpoint.reason, 'no verdict');
  assert.equal(fs.existsSync(noneApproved.snapshot.projects[0].checkpoint.path), true);
  assert.match(noneApproved.snapshot.projects[0].doing, /Checkpoint saved/);
  assert.match(noneApproved.snapshot.projects[0].waitingFor, /Resume from checkpoint/);

  const badDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-bad-'));
  const badAttempts = createAttemptEngine({ dir: badDir });
  const badPlan = notebook({ folder: DEMO, open: [] });
  const badOrch = createOrchestrator({
    readPlan: async () => badPlan,
    attempts: badAttempts,
    sentinel: createSentinel(badAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models: [{ id: 'gemini', label: 'Gemini', enabled: true }],
    dir: badDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: 1100, logPath: path.join(badDir, 'logs', 'w.log') }),
    isProcessAlive: () => true,
    killWorker: () => {}
  });
  const badApproved = await badOrch.approve('demo');
  assert.equal(badApproved.snapshot.projects[0].status, 'watching');
  const badResult = badApproved.snapshot.projects[0].resultPath;
  assert.ok(badResult);
  fs.writeFileSync(badResult, 'STATUS: FAILED\nREASON: tests exploded\n');
  snap = await badOrch.tick();
  assert.equal(snap.projects[0].status, 'checkpointed');
  assert.equal(snap.projects[0].canResume, true);
  assert.equal(snap.projects[0].checkpoint.reason, 'worker failed');
  assert.equal(fs.existsSync(snap.projects[0].checkpoint.path), true);
  assert.match(snap.projects[0].doing, /Checkpoint saved/);

  const stalePlan = notebook({ folder: DEMO, open: [], tasks: [] });
  const staleDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-stale-'));
  const staleAttempts = createAttemptEngine({ dir: staleDir });
  staleAttempts.claim({ taskId: 'task-1', projectId: 'demo', modelId: 'gemini', candidates: models });
  const staleOrch = createOrchestrator({
    readPlan: async () => stalePlan,
    attempts: staleAttempts,
    sentinel: createSentinel(staleAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: staleDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' })
  });
  snap = await staleOrch.tick();
  assert.equal(snap.projects[0].status, 'idle');
  assert.equal(staleAttempts.getAttempts().every(item => item.status !== 'active'), true);

  const completeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-'));
  const completeAttempts = createAttemptEngine({ dir: completeDir });
  const completeSentinel = createSentinel(completeAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') });
  const completePlan = notebook({ folder: DEMO, open: [] });
  const completeOrch = createOrchestrator({
    readPlan: async () => completePlan,
    attempts: completeAttempts,
    sentinel: completeSentinel,
    models,
    dir: completeDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' })
  });
  await completeOrch.approve('demo');
  const attempt = completeAttempts.getAttempts()[0];
  fs.writeFileSync(attempt.resultPath, 'STATUS: COMPLETE\n');
  snap = await completeOrch.tick();
  assert.equal(snap.projects[0].status, 'complete');

  const { createServer: realCreateServer } = require('../server.cjs');
  // HTTP lifecycle scenarios use one stable worker; production roster is tested above.
  const createServer = options => realCreateServer({ models: full.map(item => ({ ...item, enabled: item.id === 'grok-4-6' })), fetchPlans: async () => [], ...options });
  const httpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-http-'));
  const httpPlan = path.join(httpDir, 'plan.json');
  const httpFolder = path.join(httpDir, 'repo');
  fs.mkdirSync(httpFolder);
  fs.writeFileSync(path.join(httpFolder, 'README.md'), 'demo\n');
  fs.writeFileSync(httpPlan, JSON.stringify(notebook({ folder: httpFolder, open: [] })) + '\n');
  const httpServer = createServer({ phase2: false,planFile: httpPlan, attemptsDir: path.join(httpDir, 'attempts') });
  await new Promise(resolve => httpServer.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${httpServer.address().port}`;
  try {
    const listed = await (await fetch(base + '/api/orchestrator')).json();
    assert.equal(listed.mode, 'manual');
    assert.equal(listed.projects[0].status, 'needs_approval');
    assert.ok(listed.models.some(item => item.id === 'luna' && !item.enabled && item.available === true && item.plan === 'ChatGPT'));
    assert.ok(listed.models.some(item => item.id === 'gpt-5-mini' && !item.enabled && item.available === false));
    assert.ok(listed.models.filter(item => item.plan === 'ChatGPT').length > 1);
    assert.ok(listed.models.filter(item => item.plan === 'Grok').length > 1);
    assert.ok(listed.models.some(item => item.id === 'grok' && item.plan === 'Grok'));
    assert.ok(listed.models.filter(item => item.plan === 'Gemini').every(item => item.enabled === false));
    const run = await fetch(base + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo' }) });
    assert.equal(run.status, 200);
    const after = await run.json();
    assert.equal(after.projects[0].status, 'watching');
    assert.equal(after.projects[0].modelId, 'grok-4-6');
    assert.ok(after.models.some(item => item.id === 'grok-4-6' && item.used >= 1 && item.remaining < item.limit));
    const autoHttp = createServer({ phase2: false,planFile: httpPlan, attemptsDir: path.join(httpDir, 'attempts-auto') });
    await new Promise(resolve => autoHttp.listen(0, '127.0.0.1', resolve));
    const autoBase = `http://127.0.0.1:${autoHttp.address().port}`;
    try {
      const switched = await fetch(autoBase + '/api/orchestrator/mode', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: 'auto' }) });
      assert.equal(switched.status, 200);
      const autoBody = await switched.json();
      assert.equal(autoBody.mode, 'auto');
      assert.equal(autoBody.projects[0].status, 'needs_approval', 'Tasks present at startup require fresh authorization');
      const authorized = await fetch(autoBase + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: autoBody.projects[0].id }) });
      assert.equal(authorized.status, 200);
      assert.equal((await authorized.json()).projects[0].status, 'watching');
      const toggled = await fetch(autoBase + '/api/orchestrator/model', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: 'luna', enabled: false }) });
      assert.equal(toggled.status, 200);
      const offBody = await toggled.json();
      assert.equal(offBody.models.find(item => item.id === 'luna').enabled, false);
    } finally {
      await new Promise(resolve => autoHttp.close(resolve));
    }
  } finally {
    await new Promise(resolve => httpServer.close(resolve));
  }

  const gitHttpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-start-'));
  const gitRepo = path.join(gitHttpDir, 'repo');
  fs.mkdirSync(gitRepo);
  fs.writeFileSync(path.join(gitRepo, 'README.md'), 'demo\n');
  function git(args) {
    const result = spawnSync('git', ['-C', gitRepo, ...args], { encoding: 'utf8', windowsHide: true, timeout: 10000 });
    assert.equal(result.status, 0, String(result.stderr || (result.error && result.error.message) || ''));
    return result.stdout;
  }
  git(['init']);
  git(['config', 'user.email', 'planwharf@example.com']);
  git(['config', 'user.name', 'Planwharf']);
  git(['add', '.']);
  git(['commit', '-m', 'Add README']);
  const startPlanFile = path.join(gitHttpDir, 'plan.json');
  fs.writeFileSync(startPlanFile, JSON.stringify(notebook({ folder: gitRepo, open: [], tasks: [] })) + '\n');
  const startServer = createServer({ phase2: false,planFile: startPlanFile, attemptsDir: path.join(gitHttpDir, 'attempts') });
  await new Promise(resolve => startServer.listen(0, '127.0.0.1', resolve));
  const startBase = `http://127.0.0.1:${startServer.address().port}`;
  try {
    const switched = await fetch(startBase + '/api/orchestrator/mode', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: 'auto' }) });
    assert.equal(switched.status, 200);
    const idleBody = await switched.json();
    assert.equal(idleBody.mode, 'auto');
    assert.equal(idleBody.projects[0].status, 'idle');
    assert.equal(idleBody.projects[0].canStart, true);
    const grillOff = await fetch(startBase + '/api/orchestrator/grill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ propose: false }) });
    assert.equal(grillOff.status, 200);
    const planBody = await (await fetch(startBase + '/api/plan')).json();
    const started = await fetch(startBase + '/api/orchestrator/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: planBody.version, projectId: 'demo' }) });
    assert.equal(started.status, 201);
    const startedBody = await started.json();
    assert.equal(startedBody.snapshot.mode, 'auto');
    assert.equal(startedBody.snapshot.projects[0].status, 'needs_approval');
    assert.match(startedBody.snapshot.projects[0].plan.join('\n'), /Latest git/);
    assert.match(startedBody.snapshot.projects[0].plan.join('\n'), /Add README/);
    assert.match(startedBody.plan.projects[0].tasks[0].title, /Add README/);
    assert.match(startedBody.plan.projects[0].tasks[0].detail, /proceed with the notebook plan/);
    assert.equal(startedBody.snapshot.projects[0].canStart, true);
    const restarted = await fetch(startBase + '/api/orchestrator/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: startedBody.version, projectId: 'demo' }) });
    assert.equal(restarted.status, 201);
    const restartedBody = await restarted.json();
    assert.equal(restartedBody.snapshot.projects[0].status, 'needs_approval');
    assert.equal(restartedBody.plan.projects[0].tasks.filter(task => task.status === 'running').length, 1);
    assert.equal(restartedBody.snapshot.projects[0].canStart, true);
    const approvedStart = await fetch(startBase + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo' }) });
    assert.equal(approvedStart.status, 200);
    const approvedBody = await approvedStart.json();
    assert.equal(approvedBody.projects[0].status, 'watching');
    assert.equal(approvedBody.projects[0].modelId, 'grok-4-6');
  } finally {
    await new Promise(resolve => startServer.close(resolve));
  }

  const chatDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-chat-'));
  const chatRepo = path.join(chatDir, 'repo');
  fs.mkdirSync(chatRepo);
  fs.writeFileSync(path.join(chatRepo, 'README.md'), 'demo\n');
  const chatPlanFile = path.join(chatDir, 'plan.json');
  fs.writeFileSync(chatPlanFile, JSON.stringify(notebook({ folder: chatRepo, open: [], tasks: [] })) + '\n');
  const chatServer = createServer({ phase2: false,planFile: chatPlanFile, attemptsDir: path.join(chatDir, 'attempts') });
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
  const abortServer = createServer({ phase2: false,
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

  const grillDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-grill-'));
  const grillRepo = path.join(grillDir, 'repo');
  fs.mkdirSync(grillRepo);
  fs.writeFileSync(path.join(grillRepo, 'README.md'), '# Demo\n');
  const grillPlanFile = path.join(grillDir, 'plan.json');
  fs.writeFileSync(grillPlanFile, JSON.stringify(notebook({ folder: grillRepo, open: [], tasks: [], fresh: true })) + '\n');
  const grillServer = createServer({ phase2: false,planFile: grillPlanFile, attemptsDir: path.join(grillDir, 'attempts') });
  await new Promise(resolve => grillServer.listen(0, '127.0.0.1', resolve));
  const grillBase = `http://127.0.0.1:${grillServer.address().port}`;
  try {
    const proposed = await (await fetch(grillBase + '/api/orchestrator')).json();
    assert.equal(proposed.grillPropose, true);
    assert.equal(proposed.grillKind, 'me');
    const kindDocs = await fetch(grillBase + '/api/orchestrator/grill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ propose: true, kind: 'docs' }) });
    assert.equal(kindDocs.status, 200);
    assert.equal((await kindDocs.json()).grillKind, 'docs');
    assert.equal(proposed.projects[0].status, 'idle');
    assert.equal(proposed.projects[0].canStart, true);
    const planBody = await (await fetch(grillBase + '/api/plan')).json();
    const docsStart = await fetch(grillBase + '/api/grill-start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: planBody.version, projectId: 'demo', kind: 'docs' }) });
    assert.equal(docsStart.status, 201);
    const docsBody = await docsStart.json();
    assert.equal(docsBody.plan.projects[0].revisions.at(-1).title, 'Grill with the docs');
    assert.match(docsBody.plan.projects[0].revisions.at(-1).open.join('\n'), /README\.md/);
    const afterDocs = await (await fetch(grillBase + '/api/orchestrator')).json();
    assert.equal(afterDocs.projects[0].status, 'waiting_on_you');
    const off = await fetch(grillBase + '/api/orchestrator/grill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ propose: false }) });
    assert.equal(off.status, 200);
    assert.equal((await off.json()).grillPropose, false);
  } finally {
    await new Promise(resolve => grillServer.close(resolve));
  }

  const skipDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-skip-'));
  const skipRepo = path.join(skipDir, 'repo');
  fs.mkdirSync(skipRepo);
  const skipPlanFile = path.join(skipDir, 'plan.json');
  fs.writeFileSync(skipPlanFile, JSON.stringify(notebook({ folder: skipRepo, open: [], tasks: [], fresh: true })) + '\n');
  const skipServer = createServer({ phase2: false,planFile: skipPlanFile, attemptsDir: path.join(skipDir, 'attempts') });
  await new Promise(resolve => skipServer.listen(0, '127.0.0.1', resolve));
  const skipBase = `http://127.0.0.1:${skipServer.address().port}`;
  try {
    const beforeSkip = await (await fetch(skipBase + '/api/orchestrator')).json();
    assert.equal(beforeSkip.projects[0].status, 'idle');
    const skipPlanBody = await (await fetch(skipBase + '/api/plan')).json();
    const skipStart = await fetch(skipBase + '/api/orchestrator/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: skipPlanBody.version, projectId: 'demo' }) });
    assert.equal(skipStart.status, 200);
    const skipStarted = await skipStart.json();
    assert.equal(skipStarted.snapshot.projects[0].status, 'waiting_on_you');
    assert.ok(skipStarted.plan.projects[0].revisions.at(-1).open.some(question => /Recommended:/i.test(question)));
    const skippedHttp = await fetch(skipBase + '/api/orchestrator/grill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo', skip: true }) });
    assert.equal(skippedHttp.status, 200);
    const skippedBody = await skippedHttp.json();
    const skippedSnap = skippedBody.snapshot || skippedBody;
    assert.equal(skippedSnap.projects[0].status, 'idle');
    assert.equal(skippedSnap.projects[0].grillSkipped, true);
  } finally {
    await new Promise(resolve => skipServer.close(resolve));
  }

  const openSkipDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-openskip-'));
  const openSkipRepo = path.join(openSkipDir, 'repo');
  fs.mkdirSync(openSkipRepo);
  const openSkipPlanFile = path.join(openSkipDir, 'plan.json');
  fs.writeFileSync(openSkipPlanFile, JSON.stringify(notebook({ folder: openSkipRepo, open: ['What is in scope?'], tasks: [] })) + '\n');
  const openSkipServer = createServer({ phase2: false,planFile: openSkipPlanFile, attemptsDir: path.join(openSkipDir, 'attempts') });
  await new Promise(resolve => openSkipServer.listen(0, '127.0.0.1', resolve));
  const openSkipBase = `http://127.0.0.1:${openSkipServer.address().port}`;
  try {
    const openSkipPlan = await (await fetch(openSkipBase + '/api/plan')).json();
    const openSkipHttp = await fetch(openSkipBase + '/api/orchestrator/grill', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo', skip: true, version: openSkipPlan.version }) });
    assert.equal(openSkipHttp.status, 200);
    const openSkipBody = await openSkipHttp.json();
    assert.equal(openSkipBody.plan.projects[0].revisions.at(-1).open.length, 0);
    assert.equal(openSkipBody.snapshot.projects[0].status, 'idle');
    assert.equal(openSkipBody.snapshot.projects[0].grillSkipped, true);
  } finally {
    await new Promise(resolve => openSkipServer.close(resolve));
  }

  const pipelineDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-pipe-'));
  const pipelineRepo = path.join(pipelineDir, 'repo');
  fs.mkdirSync(pipelineRepo);
  fs.writeFileSync(path.join(pipelineRepo, 'README.md'), '# Demo\n');
  const pipelinePlanFile = path.join(pipelineDir, 'plan.json');
  fs.writeFileSync(pipelinePlanFile, JSON.stringify(notebook({ folder: pipelineRepo, open: [], tasks: [], fresh: true })) + '\n');
  const pipelineServer = createServer({ phase2: false,planFile: pipelinePlanFile, attemptsDir: path.join(pipelineDir, 'attempts') });
  await new Promise(resolve => pipelineServer.listen(0, '127.0.0.1', resolve));
  const pipelineBase = `http://127.0.0.1:${pipelineServer.address().port}`;
  try {
    const planBody = await (await fetch(pipelineBase + '/api/plan')).json();
    const started = await fetch(pipelineBase + '/api/orchestrator/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ version: planBody.version, projectId: 'demo' }) });
    assert.equal(started.status, 200);
    const startedBody = await started.json();
    assert.equal(startedBody.phase, 'grill');
    assert.equal(startedBody.plan.projects[0].tasks.length, 0);
    assert.equal(startedBody.snapshot.projects[0].status, 'waiting_on_you');
    assert.ok(startedBody.plan.projects[0].revisions.at(-1).open.some(question => /Recommended:/i.test(question)));
    assert.ok(startedBody.plan.projects[0].revisions.at(-1).open.some(question => /Ship the core/i.test(question)));
    assert.equal(startedBody.snapshot.projects[0].pendingAutoStart, true);
    assert.equal(startedBody.snapshot.projects[0].canStart, true);
    assert.equal(startedBody.snapshot.projects[0].step, 'grill');
    assert.ok(startedBody.snapshot.projects[0].crew.some(person => person.role === 'Orchestrator' && person.name === 'Orchestrator'));
    assert.ok(startedBody.snapshot.projects[0].doing);
  } finally {
    await new Promise(resolve => pipelineServer.close(resolve));
  }

  let clock = '2026-10-09T16:00:00.000Z';
  const duePlan = notebook({ folder: DEMO, open: [] });
  duePlan.projects[0].tasks[0].timing = 'deadline';
  duePlan.projects[0].tasks[0].deadlineAt = '2026-10-09T17:00:00.000Z';
  const dueDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-due-'));
  const dueAttempts = createAttemptEngine({ dir: dueDir });
  const dueOrch = createOrchestrator({
    readPlan: async () => duePlan,
    attempts: dueAttempts,
    sentinel: createSentinel(dueAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: dueDir,
    now: () => clock,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' })
  });
  const dueApproved = await dueOrch.approve('demo');
  assert.equal(dueApproved.ok, true);
  assert.equal(dueApproved.snapshot.projects[0].status, 'watching');
  clock = '2026-10-09T17:00:01.000Z';
  snap = await dueOrch.tick();
  assert.equal(snap.projects[0].status, 'checkpointed');
  assert.equal(snap.projects[0].canResume, true);
  assert.ok(snap.projects[0].checkpoint && snap.projects[0].checkpoint.path);
  assert.equal(fs.existsSync(snap.projects[0].checkpoint.path), true);
  assert.match(snap.projects[0].doing, /Checkpoint saved/);
  assert.equal(dueAttempts.getAttempts().every(item => item.status !== 'active'), true);
  const resumed = await dueOrch.resume('demo');
  assert.equal(resumed.ok, true);
  assert.equal(resumed.snapshot.projects[0].status, 'watching');
  assert.equal(dueAttempts.getAttempts().filter(item => item.status === 'active').length, 1);

  const stopDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-stop-'));
  const stopAttempts = createAttemptEngine({ dir: stopDir });
  const stopPlan = notebook({ folder: DEMO, open: [] });
  const stopKilled = [];
  let stopPid = 777;
  const stopOrch = createOrchestrator({
    readPlan: async () => stopPlan,
    attempts: stopAttempts,
    sentinel: createSentinel(stopAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: stopDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: stopPid++, logPath: path.join(stopDir, 'logs', 'attempt.log') }),
    isProcessAlive: pid => !stopKilled.includes(pid),
    killWorker: pid => { stopKilled.push(pid); }
  });
  const stopApproved = await stopOrch.approve('demo');
  assert.equal(stopApproved.ok, true);
  assert.equal(stopApproved.snapshot.projects[0].status, 'watching');
  assert.equal(stopApproved.snapshot.projects[0].canStop, true);
  const stopResult = await stopOrch.stop('demo');
  assert.equal(stopResult.ok, true);
  assert.equal(stopResult.snapshot.projects[0].status, 'checkpointed');
  assert.equal(stopResult.snapshot.projects[0].checkpoint.reason, 'stop');
  assert.equal(stopResult.snapshot.projects[0].canResume, true);
  assert.match(stopResult.snapshot.projects[0].doing, /Checkpoint saved/);
  assert.match(stopResult.snapshot.projects[0].waitingFor, /Resume from checkpoint/);
  assert.equal(stopResult.snapshot.projects[0].canStop, false);
  assert.equal(stopKilled.includes(777), true);
  assert.equal(stopAttempts.getAttempts().every(item => item.status !== 'active'), true);
  const stopAgain = await stopOrch.stop('demo');
  assert.equal(stopAgain.ok, false);
  const waitPlan = notebook({ folder: DEMO, open: ['What is the smallest slice?'] });
  const waitOrch = createOrchestrator({
    readPlan: async () => waitPlan,
    attempts: createAttemptEngine({ dir: fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-wait-')) }),
    models,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' })
  });
  const waitSnap = await waitOrch.tick();
  assert.equal(waitSnap.projects[0].status, 'waiting_on_you');
  assert.equal(waitSnap.projects[0].canStop, false);
  const waitStop = await waitOrch.stop('demo');
  assert.equal(waitStop.ok, false);
  const resumedStop = await stopOrch.resume('demo');
  assert.equal(resumedStop.ok, true);
  assert.equal(resumedStop.snapshot.projects[0].status, 'watching');
  assert.equal(resumedStop.snapshot.projects[0].canStop, true);

  const stopHttpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-stop-http-'));
  const stopHttpRepo = path.join(stopHttpDir, 'repo');
  fs.mkdirSync(stopHttpRepo);
  fs.writeFileSync(path.join(stopHttpRepo, 'README.md'), '# Demo\n');
  const stopHttpPlanFile = path.join(stopHttpDir, 'plan.json');
  fs.writeFileSync(stopHttpPlanFile, JSON.stringify(notebook({ folder: stopHttpRepo, open: [] })) + '\n');
  const httpKilled = [];
  let httpPid = 9001;
  const openedWindows = [];
  const stopHttpServer = createServer({ phase2: false,
    planFile: stopHttpPlanFile,
    attemptsDir: path.join(stopHttpDir, 'attempts'),
    inspectFolder: () => ({ folder: stopHttpRepo, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: httpPid++, logPath: path.join(stopHttpDir, 'logs', 'attempt.log') }),
    isProcessAlive: pid => !httpKilled.includes(pid),
    killWorker: pid => { httpKilled.push(pid); },
    openWatchWindow: url => { openedWindows.push(url); return { pid: 1 }; }
  });
  await new Promise(resolve => stopHttpServer.listen(0, '127.0.0.1', resolve));
  const stopHttpBase = `http://127.0.0.1:${stopHttpServer.address().port}`;
  try {
    const approvedHttp = await fetch(stopHttpBase + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo' }) });
    assert.equal(approvedHttp.status, 200);
    const approvedHttpBody = await approvedHttp.json();
    assert.equal(approvedHttpBody.projects[0].status, 'watching');
    const stoppedHttp = await fetch(stopHttpBase + '/api/orchestrator/stop', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo' }) });
    assert.equal(stoppedHttp.status, 200);
    const stoppedHttpBody = await stoppedHttp.json();
    assert.equal(stoppedHttpBody.projects[0].status, 'checkpointed');
    assert.equal(stoppedHttpBody.projects[0].checkpoint.reason, 'stop');
    assert.equal(httpKilled.includes(9001), true);
    const windowHttp = await fetch(stopHttpBase + '/api/orchestrator/window', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({}) });
    assert.equal(windowHttp.status, 200);
    const windowBody = await windowHttp.json();
    assert.equal(windowBody.ok, true);
    assert.equal(windowBody.opened, true);
    assert.match(windowBody.url, /:\d+\/$/);
    assert.equal(openedWindows[0], windowBody.url);
  } finally {
    await new Promise(resolve => stopHttpServer.close(resolve));
  }

  const keepDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-keep-'));
  const keepAttempts = createAttemptEngine({ dir: keepDir });
  const keepPlan = notebook({ folder: DEMO, open: [] });
  let keepPid = 4400;
  const keepOrch = createOrchestrator({
    readPlan: async () => keepPlan,
    attempts: keepAttempts,
    sentinel: createSentinel(keepAttempts, { isAlive: attempt => Boolean(attempt && attempt.status === 'active') }),
    models,
    dir: keepDir,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: keepPid++, logPath: path.join(keepDir, 'logs', 'attempt.log') }),
    isProcessAlive: () => false,
    killWorker: () => {}
  });
  const keepApproved = await keepOrch.approve('demo');
  assert.equal(keepApproved.snapshot.projects[0].status, 'watching');
  const keepStopped = await keepOrch.stop('demo');
  assert.equal(keepStopped.snapshot.projects[0].status, 'checkpointed');
  const keepFile = keepStopped.snapshot.projects[0].checkpoint.path;
  assert.equal(fs.existsSync(keepFile), true);
  let keepClock = '2027-10-10T12:00:00.000Z';
  const keepRevived = createOrchestrator({
    readPlan: async () => keepPlan,
    attempts: createAttemptEngine({ dir: keepDir }),
    models,
    dir: keepDir,
    now: () => keepClock,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: keepPid++, logPath: path.join(keepDir, 'logs', 'attempt.log') }),
    isProcessAlive: () => true,
    killWorker: () => {}
  });
  const keepSnap = await keepRevived.tick();
  assert.equal(keepSnap.projects[0].status, 'checkpointed');
  assert.equal(keepSnap.projects[0].canResume, true);
  assert.equal(keepSnap.projects[0].checkpoint.reason, 'stop');
  assert.equal(keepSnap.projects[0].checkpoint.path, keepFile);
  assert.match(keepSnap.projects[0].waitingFor, /Resume from checkpoint/);
  const keepState = JSON.parse(fs.readFileSync(path.join(keepDir, 'orchestrator.json'), 'utf8'));
  keepState.projects.demo.checkpoint = null;
  keepState.projects.demo.status = 'idle';
  fs.writeFileSync(path.join(keepDir, 'orchestrator.json'), JSON.stringify(keepState) + '\n');
  keepClock = '2028-06-01T00:00:00.000Z';
  const keepFromFile = createOrchestrator({
    readPlan: async () => keepPlan,
    attempts: createAttemptEngine({ dir: keepDir }),
    models: [{ id: 'gemini', label: 'Gemini', enabled: false }, { id: 'grok', label: 'Grok', enabled: false }],
    dir: keepDir,
    now: () => keepClock,
    inspectFolder: () => ({ folder: DEMO, fileCount: 1, names: ['README.md'], git: false, head: '' })
  });
  const keepFileSnap = await keepFromFile.tick();
  assert.equal(keepFileSnap.projects[0].status, 'checkpointed');
  assert.equal(keepFileSnap.projects[0].canResume, true);
  assert.equal(keepFileSnap.projects[0].checkpoint.reason, 'stop');
  const keepResumed = await keepRevived.resume('demo');
  assert.equal(keepResumed.ok, true);
  assert.equal(keepResumed.snapshot.projects[0].status, 'watching');

  const keepHttpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-orch-keep-http-'));
  const keepHttpRepo = path.join(keepHttpDir, 'repo');
  fs.mkdirSync(keepHttpRepo);
  fs.writeFileSync(path.join(keepHttpRepo, 'README.md'), '# Demo\n');
  const keepHttpPlan = path.join(keepHttpDir, 'plan.json');
  fs.writeFileSync(keepHttpPlan, JSON.stringify(notebook({ folder: keepHttpRepo, open: [] })) + '\n');
  const keepAttemptsDir = path.join(keepHttpDir, 'attempts');
  let keepHttpPid = 9100;
  const keepHttpOpts = {
    phase2: false,
    // This checkpoint persistence fixture does not exercise downloadable Skills.
   
   
    planFile: keepHttpPlan,
    models,
    attemptsDir: keepAttemptsDir,
    inspectFolder: () => ({ folder: keepHttpRepo, fileCount: 1, names: ['README.md'], git: false, head: '' }),
    spawnWorker: () => ({ pid: keepHttpPid++, logPath: path.join(keepHttpDir, 'logs', 'attempt.log') }),
    isProcessAlive: () => false,
    killWorker: () => {}
  };
  const keepHttpServer = createServer(keepHttpOpts);
  await new Promise(resolve => keepHttpServer.listen(0, '127.0.0.1', resolve));
  const keepHttpBase = `http://127.0.0.1:${keepHttpServer.address().port}`;
  try {
    const approvedKeep = await fetch(keepHttpBase + '/api/orchestrator/approve', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo' }) });
    assert.equal(approvedKeep.status, 200, await approvedKeep.clone().text());
    const stoppedKeepResponse = await fetch(keepHttpBase + '/api/orchestrator/stop', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId: 'demo' }) });
    assert.equal(stoppedKeepResponse.status, 200, await stoppedKeepResponse.clone().text());
    const stoppedKeep = await stoppedKeepResponse.json();
    assert.equal(stoppedKeep.projects[0].status, 'checkpointed');
  } finally {
    await new Promise(resolve => keepHttpServer.close(resolve));
  }
  const keepHttpServer2 = createServer(keepHttpOpts);
  await new Promise(resolve => keepHttpServer2.listen(0, '127.0.0.1', resolve));
  const keepHttpBase2 = `http://127.0.0.1:${keepHttpServer2.address().port}`;
  try {
    const afterRelaunchResponse = await fetch(keepHttpBase2 + '/api/orchestrator');
    assert.equal(afterRelaunchResponse.status, 200, await afterRelaunchResponse.clone().text());
    const afterRelaunch = await afterRelaunchResponse.json();
    assert.equal(afterRelaunch.projects[0].status, 'checkpointed');
    assert.equal(afterRelaunch.projects[0].canResume, true);
    assert.equal(afterRelaunch.projects[0].checkpoint.reason, 'stop');
  } finally {
    await new Promise(resolve => keepHttpServer2.close(resolve));
  }

  console.log('OK: orchestrator gates, grill proposal, Auto start holds Approve, Automatic picks a plan worker, usage counts, COMPLETE watched, deadline checkpoint resumes, Stop checkpoint, monitor window.');
})().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
