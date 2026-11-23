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

test('collectDecisions handles malformed or null items, corrupt timestamps, and sorts breaking first', () => {
  const collected = collectDecisions({
    projects: [null, { id: 'p1', name: 'Project 1' }, undefined],
    execution: {
      megaTasks: [null, { id: 'm1', projectId: 'p1', title: 'Task 1' }],
      subTasks: [null, { megaTaskId: 'm1', status: 'PENDING' }],
      decisions: [
        null,
        { id: 'd-normal', projectId: 'p1', megaTaskId: 'm1', text: 'Normal?', breaks: false, at: 'invalid-date', answer: '' },
        { id: 'd-break', projectId: 'p1', megaTaskId: 'm1', text: 'Break?', breaks: true, at: null, answer: '' }
      ]
    },
    pipelineJobs: [null, { id: 'pipe1', projectId: 'p1', questions: [null, { id: 'q1', text: 'Q1?', breaks: false, at: undefined }] }]
  });

  assert.equal(collected.open.length, 3);
  assert.equal(collected.openCount, 3);
  // Breaking decision is first despite invalid dates
  assert.equal(collected.open[0].id, 'd-break');
  assert.equal(collected.open[0].breaks, true);
});

test('pipeline decisions can be answered and delegated, and reject changes once step started', () => {
  const pipelineJobs = [
    {
      id: 'pipe-intake',
      projectId: 'proj-x',
      status: 'intake',
      questions: [
        { id: 'q-a', text: 'Include unit tests?', suggestion: 'yes', breaks: false, answer: '' },
        { id: 'q-b', text: 'Target architecture?', suggestion: 'arm64', breaks: true, answer: '' }
      ]
    },
    {
      id: 'pipe-building',
      projectId: 'proj-x',
      status: 'building',
      questions: [
        { id: 'q-c', text: 'Use staging DB?', suggestion: 'staging', breaks: false, answer: 'staging' }
      ]
    }
  ];

  // Delegate q-a to lead
  const delRes = delegateDecision({}, 'proj-x', 'pipeline:pipe-intake:q-a', { pipelineJobs, leadLabel: 'Claude Sonnet' });
  assert.equal(delRes.ok, true);
  assert.equal(pipelineJobs[0].questions[0].answer, 'yes');
  assert.equal(pipelineJobs[0].questions[0].answeredBy, 'Decided by Claude Sonnet');

  // Answer q-b with 'accepted' -> adopts suggestion 'arm64'
  const ansRes = answerDecision({}, 'proj-x', 'pipeline:pipe-intake:q-b', 'accepted', { pipelineJobs });
  assert.equal(ansRes.ok, true);
  assert.equal(pipelineJobs[0].questions[1].answer, 'arm64');

  // Attempting to change an answered question in a building pipeline job is rejected
  const rejRes = answerDecision({}, 'proj-x', 'pipeline:pipe-building:q-c', 'prod', { pipelineJobs });
  assert.equal(rejRes.ok, false);
  assert.match(rejRes.reason, /already started/);
});

test('concurrent and error paths for answerDecision and delegateDecision', () => {
  const execution = {
    decisions: [
      { id: 'dec-concurrent', projectId: 'p1', megaTaskId: 'm1', text: 'Port number?', suggestion: '8080', breaks: false, answer: '' }
    ]
  };

  // Missing decision returns error
  assert.equal(answerDecision(execution, 'p1', 'nonexistent', 'val').ok, false);
  assert.equal(delegateDecision(execution, 'p1', 'nonexistent').ok, false);

  // Missing execution returns error
  assert.equal(answerDecision(null, 'p1', 'dec-concurrent', 'val').ok, false);
  assert.equal(delegateDecision(undefined, 'p1', 'dec-concurrent').ok, false);

  // Safe whitespace trimming and empty suggestion fallback
  const dNoSug = { id: 'no-sug', projectId: 'p1', megaTaskId: 'm1', text: 'Confirm?', suggestion: '', breaks: false };
  autoDecideDecision(dNoSug, { leadLabel: 'Lead' });
  assert.equal(dNoSug.answer, 'yes');

  // Trimming in answerDecision
  answerDecision(execution, 'p1', 'dec-concurrent', '   3000   ');
  assert.equal(execution.decisions[0].answer, '3000');
});
