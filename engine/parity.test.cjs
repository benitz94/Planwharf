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
  if (!fs.existsSync(requiredPath)) {
    errors.push(`Missing required configuration file: ${requiredPath}`);
    return errors;
  }
  if (!fs.existsSync(parityTestsCsPath)) {
    errors.push(`Missing ParityTests.cs file: ${parityTestsCsPath}`);
    return errors;
  }
  let required;
  try {
    required = JSON.parse(fs.readFileSync(requiredPath, 'utf8'));
  } catch (err) {
    errors.push(`Invalid JSON in required file ${requiredPath}: ${err.message}`);
    return errors;
  }
  if (!required || typeof required !== 'object' || Array.isArray(required)) {
    errors.push(`Invalid format in required file: expected an object mapping module names to specs`);
    return errors;
  }

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

    // 4. Missing required configuration file
    const noReqErrors = checkRequired(path.join(tmp, 'nonexistent-required.json'), fakeParityDir, fakeCsPath);
    assert.match(noReqErrors.join('\n'), /Missing required configuration file/);

    // 5. Missing ParityTests.cs file
    const noCsErrors = checkRequired(fakeRequiredPath, fakeParityDir, path.join(tmp, 'Nonexistent.cs'));
    assert.match(noCsErrors.join('\n'), /Missing ParityTests\.cs file/);

    // 6. Corrupted JSON in required configuration file
    fs.writeFileSync(fakeRequiredPath, '{ invalid json');
    const corruptReqErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(corruptReqErrors.join('\n'), /Invalid JSON in required file/);

    // 7. Non-object required configuration file
    fs.writeFileSync(fakeRequiredPath, JSON.stringify(['not', 'an', 'object']));
    const nonObjReqErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(nonObjReqErrors.join('\n'), /Invalid format in required file/);

    // 8. Corrupted JSON in parity module file
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'corrupt-mod': { minCases: 1 } }));
    fs.writeFileSync(path.join(tmp, 'corrupt-mod.json'), '{ broken-json');
    const corruptModErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(corruptModErrors.join('\n'), /Invalid JSON in parity file: corrupt-mod\.json/);

    // 9. Non-array content in parity module file
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'obj-mod': { minCases: 2 } }));
    fs.writeFileSync(path.join(tmp, 'obj-mod.json'), JSON.stringify({ not: 'an array' }));
    const nonArrayErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(nonArrayErrors.join('\n'), /Module obj-mod has only 0 parity cases, requires at least 2/);

    // 10. Spec as plain number
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'num-spec': 3 }));
    fs.writeFileSync(path.join(tmp, 'num-spec.json'), JSON.stringify([{ fn: 'fn1', args: [] }]));
    fs.writeFileSync(fakeCsPath, '["num-spec/fn1"]');
    const numSpecErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(numSpecErrors.join('\n'), /Module num-spec has only 1 parity cases, requires at least 3/);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

module.exports = { checkRequired };
