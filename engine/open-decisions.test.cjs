'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { openDecisionsOf } = require('./orchestrator.js');

const execution = {
  megaTasks: [{ id: 'old', status: 'COMPLETE' }, { id: 'new', status: 'READY_FOR_APPROVAL' }, { id: 'gone', status: 'CANCELLED' }],
  decisions: [
    { id: 'a', projectId: 'p', megaTaskId: 'old', text: 'Old question', answer: '' },
    { id: 'b', projectId: 'p', megaTaskId: 'new', text: 'New question', answer: '' },
    { id: 'c', projectId: 'p', megaTaskId: 'new', text: 'Answered', answer: 'yes' },
    { id: 'd', projectId: 'q', megaTaskId: 'new', text: 'Other project', answer: '' },
    { id: 'e', projectId: 'p', megaTaskId: 'gone', text: 'Dropped work', answer: '' },
  ],
};

test('a new proposal never shows the open decision of an earlier work', () => {
  assert.deepStrictEqual(openDecisionsOf('p', { megaTaskId: 'new' }, execution).map(d => d.id), ['b']);
});

test('the work that raised a decision still asks it', () => {
  assert.deepStrictEqual(openDecisionsOf('p', { megaTaskId: 'old' }, execution).map(d => d.id), ['a']);
});

test('no work, or dropped work, asks nothing', () => {
  assert.deepStrictEqual(openDecisionsOf('p', { megaTaskId: null }, execution), []);
  assert.deepStrictEqual(openDecisionsOf('p', { megaTaskId: 'gone' }, execution), []);
});
