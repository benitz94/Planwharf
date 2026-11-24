'use strict';
// Every name a module takes from another one (const { a, b } = require('./x') or require('./x').a) is
// exported there. A missing one is undefined until the line that calls it runs: on 2026-11-24 every
// lead reply with a summary stopped the crew on "sentences is not a function".
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('every name taken from another module is exported by it', () => {
  const root = path.join(__dirname, '..');
  const files = ['engine', '.'].flatMap(dir => fs.readdirSync(path.join(root, dir)).filter(name => /\.c?js$/.test(name) && !/\.test\./.test(name)).map(name => path.join(root, dir, name)));
  const missing = [];
  const load = (from, request) => { try { return require(path.resolve(path.dirname(from), request)); } catch { return null; } };
  for (const file of files) {
    const source = fs.readFileSync(file, 'utf8');
    for (const match of source.matchAll(/const\s*\{([^}]+)\}\s*=\s*require\((['"])(\.[^'"]+)\2\)/g)) {
      const mod = load(file, match[3]);
      if (!mod) continue;
      for (const name of match[1].split(',').map(item => item.trim().split(':')[0].trim()).filter(Boolean)) if (!(name in mod)) missing.push(`${path.relative(root, file)}: ${name} from ${match[3]}`);
    }
    for (const match of source.matchAll(/require\((['"])(\.[^'"]+)\1\)\.(\w+)/g)) {
      const mod = load(file, match[2]);
      if (mod && !(match[3] in mod)) missing.push(`${path.relative(root, file)}: ${match[3]} from ${match[2]}`);
    }
  }
  assert.deepEqual(missing, []);
});
