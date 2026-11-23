'use strict';

const test = require('node:test');
const assert = require('node:assert');
const {
  collectDecisions,
  delegateDecision,
  answerDecision,
  isAutoDecide,
  autoDecideDecision,
  isStepStarted
} = require('./decisions-inbox.cjs');

test('decisions from two projects and a pipeline job appear in one list', () => {
  const projects = [
    { id: 'proj-1', name: 'Alpha Web' },
    { id: 'proj-2', name: 'Beta Mobile' }
  ];

  const execution = {
    megaTasks: [
      { id: 'mega-1', projectId: 'proj-1', title: 'Setup Auth' },
      { id: 'mega-2', projectId: 'proj-2', title: 'Add Payments' }
    ],
    subTasks: [
      { id: 'sub-1', megaTaskId: 'mega-1', status: 'PENDING' }
    ],
    decisions: [
      { id: 'dec-1', projectId: 'proj-1', megaTaskId: 'mega-1', text: 'Use OAuth or Passwordless?', suggestion: 'OAuth', breaks: false, at: '2026-11-23T10:00:00Z', answer: '' },
      { id: 'dec-2', projectId: 'proj-2', megaTaskId: 'mega-2', text: 'Stripe or PayPal?', suggestion: 'Stripe', breaks: true, at: '2026-11-23T10:05:00Z', answer: '' }
    ]
  };

  const pipelineJobs = [
    {
      id: 'pipe-1',
      projectId: 'proj-1',
      title: 'Database Migration',
      questions: [
        { id: 'q-1', text: 'Preserve legacy users table?', suggestion: 'yes', breaks: false, answer: '' }
      ]
    }
  ];

  const collected = collectDecisions({ projects, execution, pipelineJobs });

  assert.equal(collected.open.length, 3);
  assert.equal(collected.openCount, 3);

  // The breaking decision must be listed first!
  assert.equal(collected.open[0].id, 'dec-2');
  assert.equal(collected.open[0].breaks, true);
  assert.equal(collected.open[0].projectName, 'Beta Mobile');

  // Verify all 3 are present
  const ids = collected.open.map(d => d.id);
  assert.ok(ids.includes('dec-1'));
  assert.ok(ids.includes('dec-2'));
  assert.ok(ids.includes('pipeline:pipe-1:q-1'));
});

test('Auto answers with the lead choice and reason and work goes on', () => {
  const decision = {
    id: 'dec-auto',
    projectId: 'proj-1',
    megaTaskId: 'mega-1',
    text: 'Compress images on upload?',
    suggestion: 'Compress with WebP',
    breaks: false
  };

  autoDecideDecision(decision, { leadLabel: 'Claude Sonnet', now: () => '2026-11-23T11:00:00Z' });

  assert.equal(decision.answer, 'Compress with WebP');
  assert.equal(decision.answeredBy, 'Decided by Claude Sonnet');
  assert.match(decision.autoReason, /Compress with WebP/);
  assert.equal(decision.answeredAt, '2026-11-23T11:00:00Z');
});

test('a breaking decision under Auto is answered and highlighted', () => {
  const projects = [{ id: 'p1', name: 'Core Engine' }];
  const execution = {
    megaTasks: [{ id: 'm1', projectId: 'p1', title: 'Engine upgrade' }],
    subTasks: [],
    decisions: [
      {
        id: 'dec-break',
        projectId: 'p1',
        megaTaskId: 'm1',
        text: 'Drop deprecated v1 API?',
        suggestion: 'drop',
        breaks: true,
        at: '2026-11-23T09:00:00Z'
      },
      {
        id: 'dec-normal',
        projectId: 'p1',
        megaTaskId: 'm1',
        text: 'Enable debug logging?',
        suggestion: 'no',
        breaks: false,
        at: '2026-11-23T10:00:00Z'
      }
    ]
  };

  // Auto decide both
  autoDecideDecision(execution.decisions[0], { leadLabel: 'Gemini 3.8 Flash' });
  autoDecideDecision(execution.decisions[1], { leadLabel: 'Gemini 3.8 Flash' });

  const collected = collectDecisions({ projects, execution });
  assert.equal(collected.open.length, 0);
  assert.equal(collected.answered.length, 2);

  // In answered list, breaking decision is answered, highlighted and listed first
  assert.equal(collected.answered[0].id, 'dec-break');
  assert.equal(collected.answered[0].breaks, true);
  assert.equal(collected.answered[0].answer, 'drop');
  assert.equal(collected.answered[0].answeredBy, 'Decided by Gemini 3.8 Flash');
});

test('Let the lead decide on one decision delegates only that one', () => {
  const execution = {
    decisions: [
      { id: 'd-1', projectId: 'p1', megaTaskId: 'm1', text: 'Cache TTL in seconds?', suggestion: '3600', breaks: false, answer: '' },
      { id: 'd-2', projectId: 'p1', megaTaskId: 'm1', text: 'Retry count?', suggestion: '3', breaks: false, answer: '' }
    ]
  };

  const res = delegateDecision(execution, 'p1', 'd-1', { leadLabel: 'Grok 4.7' });
  assert.equal(res.ok, true);
  assert.equal(execution.decisions[0].answer, '3600');
  assert.equal(execution.decisions[0].answeredBy, 'Decided by Grok 4.7');
  assert.equal(execution.decisions[1].answer, '');
});

test('changing an answer before the step starts and rejecting after it starts', () => {
  const subTasks = [
    { id: 'sub-pending', megaTaskId: 'm-pending', status: 'PENDING' },
    { id: 'sub-running', megaTaskId: 'm-running', status: 'RUNNING' }
  ];

  const execution = {
    decisions: [
      { id: 'd-pend', projectId: 'p1', megaTaskId: 'm-pending', text: 'Color theme?', answer: 'blue', answeredBy: 'Decided by Lead' },
      { id: 'd-run', projectId: 'p1', megaTaskId: 'm-running', text: 'Storage backend?', answer: 's3', answeredBy: 'Decided by Lead' }
    ]
  };

  // Changing before step starts: OK
  const changeRes = answerDecision(execution, 'p1', 'd-pend', 'green', { who: 'owner', subTasks });
  assert.equal(changeRes.ok, true);
  assert.equal(execution.decisions[0].answer, 'green');
  assert.equal(execution.decisions[0].answeredBy, 'owner');

  // Changing after step starts: Rejected
  const rejectRes = answerDecision(execution, 'p1', 'd-run', 'gcs', { who: 'owner', subTasks });
  assert.equal(rejectRes.ok, false);
  assert.match(rejectRes.reason, /already started/);
  assert.equal(execution.decisions[1].answer, 's3');
});

test('isAutoDecide checks project settings, fallback to global default', () => {
  assert.equal(isAutoDecide('p1', { autoDecide: { p1: true } }), true);
  assert.equal(isAutoDecide('p1', { autoDecide: { p1: false } }), false);
  assert.equal(isAutoDecide('p1', { defaultAutoDecide: true }), true);
  assert.equal(isAutoDecide('p1', { defaultAutoDecide: false }), false);
  assert.equal(isAutoDecide('p1', {}, { autoDecide: true }), true);
  assert.equal(isAutoDecide('p1', {}, { autoDecide: false }), false);
});
