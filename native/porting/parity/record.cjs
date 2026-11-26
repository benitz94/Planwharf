'use strict';
// Runs every case of cases.cjs through the JavaScript and writes the results to <module>.json next to
// this file. `node native/porting/parity/record.cjs` writes them; `--check` only compares and exits 1
// when a JavaScript change left a file out of date (CI runs the same check in engine/parity.test.cjs).
const fs = require('node:fs');
const { Module } = require('node:module');
const path = require('node:path');
const cases = require('./cases.cjs');

const PATH_MAP = {
  plan: path.join(__dirname, '..', '..', '..', 'plan.js'),
  quota: path.join(__dirname, '..', '..', '..', 'quota.js'),
  models: path.join(__dirname, '..', '..', '..', 'models.js'),
  planUsage: path.join(__dirname, '..', '..', '..', 'engine', 'planUsage.js'),
  'orchestrator-order-parsing': path.join(__dirname, '..', '..', '..', 'engine', 'orchestrator.js')
};

function loadOrchestratorLeadQuestions() {
  const orchPath = path.join(__dirname, '..', '..', '..', 'engine', 'orchestrator.js');
  const autonomyMod = require(path.join(__dirname, '..', '..', '..', 'engine', 'autonomy.cjs'));
  const mod = new Module(orchPath);
  mod.filename = orchPath;
  mod.paths = Module._nodeModulePaths(path.dirname(orchPath));
  // Test-only in-memory seam exposing top-level helpers from unchanged engine/orchestrator.js.
  const source = fs.readFileSync(orchPath, 'utf8')
    + '\n;module.exports = { ...module.exports, leadEnvelope, isDocName, briefRow };\n';
  mod._compile(source, orchPath);
  return {
    ...mod.exports,
    evaluationPrompt: autonomyMod.evaluationPrompt,
    nudgePrompt: autonomyMod.nudgePrompt,
    continuePrompt: autonomyMod.continuePrompt,
    splitPrompt: autonomyMod.splitPrompt,
    assignmentPrompt: autonomyMod.assignmentPrompt,
    strictDecisionPrompt: autonomyMod.strictDecisionPrompt
  };
}

function loadOrchestratorState() {
  const orchPath = path.join(__dirname, '..', '..', '..', 'engine', 'orchestrator.js');
  const mod = new Module(orchPath);
  mod.filename = orchPath;
  mod.paths = Module._nodeModulePaths(path.dirname(orchPath));
  // Test-only seam: expose the unchanged module's private default constructor without
  // changing engine/orchestrator.js or reimplementing its behavior here.
  const source = fs.readFileSync(orchPath, 'utf8')
    + '\n;module.exports = { ...module.exports, emptyEntry };\n';
  mod._compile(source, orchPath);
  return mod.exports;
}

const LOADER_MAP = {
  'orchestrator-lead-questions': loadOrchestratorLeadQuestions,
  'orchestrator-state': loadOrchestratorState
};

// Functions that take a callback get one that accepts everything; JSON has no functions.
const CALL = {
  'model-policy': { resolveUsageFallback: (fn, args) => fn(...args, () => true) },
  execution: { splitSummary: (fn, args) => fn(args[0], id => id, args[1] || '') }
};

function record(moduleName) {
  const loader = LOADER_MAP[moduleName];
  const modPath = PATH_MAP[moduleName] || path.join(__dirname, '..', '..', '..', 'engine', moduleName + '.cjs');
  const mod = loader ? loader() : require(modPath);
  return cases[moduleName].map(([fn, args]) => {
    if (typeof mod[fn] !== 'function') {
      throw new Error(`Missing exported function for ${moduleName}/${fn}`);
    }
    let out;
    try {
      const call = CALL[moduleName]?.[fn];
      const value = call ? call(mod[fn], structuredClone(args)) : mod[fn](...structuredClone(args));
      out = { result: value === undefined ? null : JSON.parse(JSON.stringify(value)) };
    } catch (error) { out = { error: error.message }; }
    return { fn, args, ...out };
  });
}

function text(moduleName) { return JSON.stringify(record(moduleName), null, 2) + '\n'; }

function check() {
  return Object.keys(cases).filter(moduleName => {
    const file = path.join(__dirname, moduleName + '.json');
    // A Windows checkout may store the recorded file with CRLF. That is not a JavaScript change.
    const saved = fs.existsSync(file) ? fs.readFileSync(file, 'utf8').replace(/\r\n/g, '\n') : null;
    return saved !== text(moduleName);
  });
}

if (require.main === module) {
  if (process.argv.includes('--check')) {
    const stale = check();
    if (stale.length) { console.error('Out of date, run node native/porting/parity/record.cjs: ' + stale.join(', ')); process.exit(1); }
  } else for (const moduleName of Object.keys(cases)) fs.writeFileSync(path.join(__dirname, moduleName + '.json'), text(moduleName));
}
module.exports = { record, text, check };
