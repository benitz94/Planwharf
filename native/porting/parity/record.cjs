'use strict';
// Runs every case of cases.cjs through the JavaScript and writes the results to <module>.json next to
// this file. `node native/porting/parity/record.cjs` writes them; `--check` only compares and exits 1
// when a JavaScript change left a file out of date (CI runs the same check in engine/parity.test.cjs).
const fs = require('node:fs');
const path = require('node:path');
const cases = require('./cases.cjs');

// Functions that take a callback get one that accepts everything; JSON has no functions.
const CALL = {
  'model-policy': { resolveUsageFallback: (fn, args) => fn(...args, () => true) }
};

function record(moduleName) {
  const mod = require(path.join(__dirname, '..', '..', '..', 'engine', moduleName + '.cjs'));
  return cases[moduleName].map(([fn, args]) => {
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
    return !fs.existsSync(file) || fs.readFileSync(file, 'utf8') !== text(moduleName);
  });
}

if (require.main === module) {
  if (process.argv.includes('--check')) {
    const stale = check();
    if (stale.length) { console.error('Out of date, run node native/porting/parity/record.cjs: ' + stale.join(', ')); process.exit(1); }
  } else for (const moduleName of Object.keys(cases)) fs.writeFileSync(path.join(__dirname, moduleName + '.json'), text(moduleName));
}
module.exports = { record, text, check };
