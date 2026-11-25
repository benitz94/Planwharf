'use strict';
// The recorded results that the C# port is compared with must match what the JavaScript gives today:
// a JavaScript change without running native/porting/parity/record.cjs again fails here.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const test = require('node:test');
const assert = require('node:assert/strict');
const { check } = require('../native/porting/parity/record.cjs');

function checkRequired(requiredPath, parityDir, parityTestsCsPath, scenariosDir = path.join(parityDir, 'scenarios')) {
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

  // Independently enforce nonzero execution and project-home scenario coverage and six runner scenario floors
  const runnerFloors = required.runner?.scenarioFloors;
  const expectedRunnerScenarios = ['provider-models', 'provider-adapters', 'api-agent', 'worktrees', 'pr-check', 'phone-activation-check'];
  for (const scen of expectedRunnerScenarios) {
    if (!runnerFloors || typeof runnerFloors[scen] !== 'number' || runnerFloors[scen] <= 0) {
      errors.push(`Runner must preserve scenario floor for: ${scen}`);
    }
  }

  const execFloors = required.execution?.scenarioFloors;
  if (!execFloors || typeof execFloors.execution !== 'number' || execFloors.execution <= 0) {
    errors.push(`Execution must independently enforce nonzero scenario coverage (execution)`);
  }

  const homeFloors = required['project-home']?.scenarioFloors;
  if (!homeFloors || typeof homeFloors['project-home'] !== 'number' || homeFloors['project-home'] <= 0) {
    errors.push(`Project-home must independently enforce nonzero scenario coverage (project-home)`);
  }

  for (const [moduleName, spec] of Object.entries(required)) {
    // 1. Scenario floors check
    const scenarioFloors = spec?.scenarioFloors;
    if (scenarioFloors) {
      if (typeof scenarioFloors !== 'object' || Array.isArray(scenarioFloors)) {
        errors.push(`Invalid scenarioFloors format for module ${moduleName}`);
      } else {
        for (const [scenName, minCount] of Object.entries(scenarioFloors)) {
          if (typeof minCount !== 'number' || minCount <= 0) {
            errors.push(`Scenario floor for ${scenName} under ${moduleName} must be positive`);
            continue;
          }
          const scenPath = path.join(scenariosDir, scenName + '.json');
          if (!fs.existsSync(scenPath)) {
            errors.push(`Missing scenario fixture for ${moduleName}: ${scenName}.json`);
            continue;
          }
          let scenData;
          try {
            scenData = JSON.parse(fs.readFileSync(scenPath, 'utf8'));
          } catch (err) {
            errors.push(`Invalid JSON in scenario fixture ${scenName}.json: ${err.message}`);
            continue;
          }
          const actualCount = Array.isArray(scenData?.scenarios) ? scenData.scenarios.length : 0;
          if (actualCount < minCount) {
            errors.push(`Scenario module ${scenName} has only ${actualCount} scenarios, requires at least ${minCount}`);
          }
        }
      }
    }

    // 2. Scenario-only modules do not have pure parity .json files or ParityTests.Ports wiring
    if (spec?.scenarioOnly === true) {
      if (!scenarioFloors || Object.keys(scenarioFloors).length === 0) {
        errors.push(`Scenario-only module ${moduleName} must declare scenarioFloors`);
      }
      continue;
    }

    // 3. Pure or mixed module checks
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

    // 11. Scenario-only module without scenarioFloors
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'scen-only-broken': { scenarioOnly: true } }));
    const noScenFloorsErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath);
    assert.match(noScenFloorsErrors.join('\n'), /Scenario-only module scen-only-broken must declare scenarioFloors/);

    // 12. Missing scenario fixture
    const fakeScenariosDir = path.join(tmp, 'scenarios');
    fs.mkdirSync(fakeScenariosDir, { recursive: true });
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'scen-mod': { scenarioOnly: true, scenarioFloors: { 'missing-scen': 5 } } }));
    const missingScenErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.match(missingScenErrors.join('\n'), /Missing scenario fixture for scen-mod: missing-scen\.json/);

    // 13. Too few scenarios in fixture
    fs.writeFileSync(path.join(fakeScenariosDir, 'short-scen.json'), JSON.stringify({ scenarios: [{ name: 's1' }] }));
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'scen-mod': { scenarioOnly: true, scenarioFloors: { 'short-scen': 5 } } }));
    const shortScenErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.match(shortScenErrors.join('\n'), /Scenario module short-scen has only 1 scenarios, requires at least 5/);

    // 14. Corrupted scenario fixture JSON
    fs.writeFileSync(path.join(fakeScenariosDir, 'corrupt-scen.json'), '{ broken-json');
    fs.writeFileSync(fakeRequiredPath, JSON.stringify({ 'scen-mod': { scenarioOnly: true, scenarioFloors: { 'corrupt-scen': 1 } } }));
    const corruptScenErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.match(corruptScenErrors.join('\n'), /Invalid JSON in scenario fixture corrupt-scen\.json/);

    // 15. Valid scenario-only module passes without pure file or C# wiring
    fs.writeFileSync(path.join(fakeScenariosDir, 'valid-scen.json'), JSON.stringify({ scenarios: [{ name: 's1' }, { name: 's2' }] }));
    const validRequired = {
      runner: { scenarioFloors: { 'provider-models': 1, 'provider-adapters': 1, 'api-agent': 1, worktrees: 1, 'pr-check': 1, 'phone-activation-check': 1 } },
      execution: { scenarioFloors: { execution: 1 } },
      'project-home': { scenarioOnly: true, scenarioFloors: { 'project-home': 1 } },
      'valid-only': { scenarioOnly: true, scenarioFloors: { 'valid-scen': 2 } }
    };
    for (const name of ['provider-models', 'provider-adapters', 'api-agent', 'worktrees', 'pr-check', 'phone-activation-check', 'execution', 'project-home']) {
      fs.writeFileSync(path.join(fakeScenariosDir, name + '.json'), JSON.stringify({ scenarios: [{ name: 's1' }] }));
    }
    fs.writeFileSync(path.join(fakeParityDir, 'runner.json'), JSON.stringify([{ fn: 'fnR', args: [] }]));
    fs.writeFileSync(path.join(fakeParityDir, 'execution.json'), JSON.stringify([{ fn: 'fnE', args: [] }]));
    fs.writeFileSync(fakeCsPath, '["runner/fnR", "execution/fnE"]');
    fs.writeFileSync(fakeRequiredPath, JSON.stringify(validRequired));
    const validErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.deepEqual(validErrors, []);

    // 16. Omission of execution scenario coverage fails
    const missingExecReq = structuredClone(validRequired);
    delete missingExecReq.execution;
    fs.writeFileSync(fakeRequiredPath, JSON.stringify(missingExecReq));
    const missExecErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.match(missExecErrors.join('\n'), /Execution must independently enforce nonzero scenario coverage/);

    // 17. Omission of project-home scenario coverage fails
    const missingHomeReq = structuredClone(validRequired);
    delete missingHomeReq['project-home'];
    fs.writeFileSync(fakeRequiredPath, JSON.stringify(missingHomeReq));
    const missHomeErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.match(missHomeErrors.join('\n'), /Project-home must independently enforce nonzero scenario coverage/);

    // 18. Omission of runner scenario floor fails
    const missingRunnerReq = structuredClone(validRequired);
    delete missingRunnerReq.runner.scenarioFloors['pr-check'];
    fs.writeFileSync(fakeRequiredPath, JSON.stringify(missingRunnerReq));
    const missRunnerErrors = checkRequired(fakeRequiredPath, fakeParityDir, fakeCsPath, fakeScenariosDir);
    assert.match(missRunnerErrors.join('\n'), /Runner must preserve scenario floor for: pr-check/);
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});

module.exports = { checkRequired };

test('phase 5 scenario fixtures are actually wired into C# replay', () => {
  const source = fs.readFileSync(path.resolve(__dirname, '../native/Planwharf.Engine.Tests/ScenarioParityTests.cs'), 'utf8');
  for (const module of ['execution', 'project-home']) {
    assert.ok(source.includes(`GetScenarios("${module}")`), `${module} requires an executable scenario theory`);
  }
});

test('phase 5 floors reject removed, undersized and corrupt fixtures and recover after restoration', () => {
  const source = path.resolve(__dirname, '../native/porting/parity');
  const tmp = fs.mkdtempSync(path.join(__dirname, '.parity-verifier-'));
  try {
    const manifest = JSON.parse(fs.readFileSync(path.join(source, 'required.json'), 'utf8'));
    assert.ok(manifest.execution.minCases >= 20);
    assert.ok(manifest.execution.scenarioFloors.execution >= 11);
    assert.ok(manifest['project-home'].scenarioOnly);
    assert.ok(manifest['project-home'].scenarioFloors['project-home'] >= 2);
    fs.mkdirSync(path.join(tmp, 'scenarios'));
    for (const file of fs.readdirSync(source).filter(file => file.endsWith('.json'))) {
      fs.copyFileSync(path.join(source, file), path.join(tmp, file));
    }
    for (const file of fs.readdirSync(path.join(source, 'scenarios'))) {
      if (file.endsWith('.json')) fs.copyFileSync(path.join(source, 'scenarios', file), path.join(tmp, 'scenarios', file));
    }
    const cs = path.resolve(__dirname, '../native/Planwharf.Engine.Tests/ParityTests.cs');
    const checkCopy = () => checkRequired(path.join(tmp, 'required.json'), tmp, cs);
    assert.deepEqual(checkCopy(), []);
    for (const [relative, floor, isScenario] of [
      ['execution.json', manifest.execution.minCases, false],
      ['scenarios/execution.json', manifest.execution.scenarioFloors.execution, true],
      ['scenarios/project-home.json', manifest['project-home'].scenarioFloors['project-home'], true]
    ]) {
      const file = path.join(tmp, relative), original = fs.readFileSync(file, 'utf8');
      fs.unlinkSync(file);
      assert.match(checkCopy().join('\n'), /Missing (parity file|scenario fixture)/);
      const data = JSON.parse(original);
      if (isScenario) data.scenarios = data.scenarios.slice(0, floor - 1);
      fs.writeFileSync(file, JSON.stringify(isScenario ? data : data.slice(0, floor - 1)));
      assert.ok(checkCopy().some(error => error.includes(`requires at least ${floor}`)), relative);
      fs.writeFileSync(file, '{broken');
      assert.match(checkCopy().join('\n'), /Invalid JSON/);
      fs.writeFileSync(file, original);
      assert.deepEqual(checkCopy(), [], `${relative} restoration must recover without stale failures`);
    }
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
});
