'use strict';
const assert = require('node:assert/strict');
const { noVerdictPlan } = require('./execution.cjs');

assert.equal(noVerdictPlan(0), 'retry-same', 'the first miss is one more try on the same model');
assert.equal(noVerdictPlan(1), 'switch', 'the second miss goes to the next eligible model');
assert.equal(noVerdictPlan(2), 'stop', 'two models in all, then the owner decides');
assert.equal(noVerdictPlan(3), 'stop');
console.log('PASS no verdict retry plan');
