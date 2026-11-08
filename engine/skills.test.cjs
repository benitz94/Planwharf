'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const skills = require('./skills.cjs');

test('skills are listed per program; only the fixed places open; existing files are never changed', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-home-'));
  fs.mkdirSync(path.join(home, '.claude', 'skills', 'mine'), { recursive: true });
  fs.writeFileSync(path.join(home, '.claude', 'skills', 'mine', 'SKILL.md'), '---\nname: my-skill\ndescription: Does a thing\n---\nbody');
  fs.mkdirSync(path.join(home, '.claude', 'skills', '.trash', 'old', 'gone'), { recursive: true });
  fs.writeFileSync(path.join(home, '.claude', 'skills', '.trash', 'old', 'gone', 'SKILL.md'), '---\nname: gone\n---');
  fs.mkdirSync(path.join(home, '.codex', 'skills', '.system', 'tool'), { recursive: true });
  fs.writeFileSync(path.join(home, '.codex', 'skills', '.system', 'tool', 'SKILL.md'), '---\nname: tool\ndescription: "Built in"\n---');
  fs.writeFileSync(path.join(home, '.codex', 'AGENTS.md'), 'my own rules');
  const overview = skills.skillsOverview(home);
  assert.deepEqual(overview.map(item => item.cli), ['claude', 'codex'], 'only installed programs');
  assert.deepEqual(overview[0].skills, [{ name: 'my-skill', description: 'Does a thing', source: 'yours' }], 'deleted skills are not listed');
  assert.deepEqual(overview[1].skills, [{ name: 'tool', description: 'Built in', source: 'built in' }]);
  assert.equal(skills.pathToOpen('claude', '../outside', home), null);
  assert.equal(skills.pathToOpen('someone', 'skills', home), null);
  skills.pathToOpen('codex', 'instructions', home);
  assert.equal(fs.readFileSync(path.join(home, '.codex', 'AGENTS.md'), 'utf8'), 'my own rules', 'an existing file is left as it is');
  assert.equal(skills.pathToOpen('claude', 'instructions', home).folder, false);
  assert.equal(fs.readFileSync(path.join(home, '.claude', 'CLAUDE.md'), 'utf8'), '', 'a missing file is created empty on request');
});
