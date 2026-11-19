'use strict';
// The recorded results that the C# port is compared with must match what the JavaScript gives today:
// a JavaScript change without running native/porting/parity/record.cjs again fails here.
const test = require('node:test');
const assert = require('node:assert/strict');
const { check } = require('../native/porting/parity/record.cjs');

test('the recorded JavaScript results for the C# comparison are up to date', () => {
  assert.deepEqual(check(), [], 'run: node native/porting/parity/record.cjs, then make the C# port give the same results');
});
