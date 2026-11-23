'use strict';
// The recorded results that the C# port is compared with must match what the JavaScript gives today:
// a JavaScript change without running native/porting/parity/record.cjs again fails here.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const test = require('node:test');
const assert = require('node:assert/strict');
const { check } = require('../native/porting/parity/record.cjs');

function checkRequired(requiredPath, parityDir, parityTestsCsPath) {
  const errors = [];
  const required = JSON.parse(fs.readFileSync(requiredPath, 'utf8'));
  const parityTestsSource = fs.readFileSync(parityTestsCsPath, 'utf8');

  for (const [moduleName, spec] of Object.entries(required)) {
    const minCases = typeof spec === 'number' ? spec : (spec?.minCases ?? 1);
    const jsonPath = path.join(parityDir, moduleName + '.json');
    if (!fs.existsSync(jsonPath)) {
      errors.push(`Missing parity file for required module: ${moduleName} (${moduleName}.json)`);
      continue;
    }

    let cases;
    try {
      cases = JSON.parse(fs.readFileSync(jsonPath, 'utf8'));
    } catch (err) {
      errors.push(`Invalid JSON in parity file: ${moduleName}.json: ${err.message}`);
      continue;
    }

    if (!Array.isArray(cases) || cases.length < minCases) {
      errors.push(`Module ${moduleName} has only ${Array.isArray(cases) ? cases.length : 0} parity cases, requires at least ${minCases}`);
    }

    if (Array.isArray(cases)) {
      const fns = new Set(cases.map(c => c && c.fn).filter(Boolean));
      for (const fn of fns) {
        const key = `"${moduleName}/${fn}"`;
        if (!parityTestsSource.includes(key)) {
          errors.push(`Function ${moduleName}/${fn} in ${moduleName}.json is not wired in ParityTests.cs`);
        }
      }
    }
  }

  return errors;
}

test('the recorded JavaScript results for the C# comparison are up to date', () => {
  assert.deepEqual(check(), [], 'run: node native/porting/parity/record.cjs, then make the C# port give the same results');
});

test('every required port has comparison cases and C# wiring', () => {
  const parityDir = path.resolve(__dirname, '../native/porting/parity');
  const requiredPath = path.join(parityDir, 'required.json');
  const parityTestsCsPath = path.resolve(__dirname, '../native/Planwharf.Engine.Tests/ParityTests.cs');

  const errors = checkRequired(requiredPath, parityDir, parityTestsCsPath);
  assert.deepEqual(errors, [], 'Every required module must have parity JSON cases and wiring in ParityTests.cs');
});

test('the parity guard detects missing files, too few cases, and unwired ports', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-parity-guard-'));
  try {
    const fakeRequiredPath = path.join(tmp, 'required.json');
    const fakeParityDir = tmp;
    const fakeCsPath = path.join(tmp, 'ParityTests.cs');

    // 1. Missing parity file
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'missing-mod': { minCases: 5 } }));
    fs.writeFileSync(fakeCsPath, '["missing-mod/fn"]');
    const missingErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(missingErrors.join('\n'), /Missing parity file for required module: missing-mod/);

    // 2. Too few cases
    fs.writeFileSync(path.join(tmp, 'short-mod.json'), JSON.stringify([{ fn: 'testFn', args: [] }]));
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'short-mod': { minCases: 5 } }));
    fs.writeFileSync(fakeCsPath, '["short-mod/testFn"]');
    const shortErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(shortErrors.join('\n'), /Module short-mod has only 1 parity cases, requires at least 5/);

    // 3. Unwired port in ParityTests.cs
    fs.writeFileSync(path.join(tmp, 'unwired-mod.json'), JSON.stringify([
      { fn: 'unwiredFn', args: [] },
      { fn: 'unwiredFn', args: [1] }
    ]));
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'unwired-mod': { minCases: 1 } }));
    fs.writeFileSync(fakeCsPath, '["other-mod/fn"]');
    const unwiredErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(unwiredErrors.join('\n'), /Function unwired-mod\/unwiredFn in unwired-mod\.json is not wired in ParityTests\.cs/);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

module.exports = { checkRequired };
