'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { projectSkillsPrompt } = require('./project-skills.cjs');

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-project-skills-'));
try {
  const project = path.join(dir, 'project');
  const skill = path.join(project, '.agents', 'skills', 'security-review');
  fs.mkdirSync(skill, { recursive: true });
  assert.equal(projectSkillsPrompt(project), '', 'Projects without ECC do not inherit another project catalog');
  const manifest = path.join(project, '.agents', 'ecc-install.json');
  const valid = { source: 'https://github.com/affaan-m/ECC', skills: [{ name: 'security-review' }] };
  fs.writeFileSync(manifest, JSON.stringify(valid));
  assert.equal(projectSkillsPrompt(project), '', 'Missing downloaded guidance cannot be active');
  fs.writeFileSync(path.join(skill, 'SKILL.md'), '---\nname: security-review\n---\nFixture security controls.');
  assert.equal(projectSkillsPrompt(project), '', 'Downloaded skills without project documentation stay inactive');
  fs.writeFileSync(path.join(project, '.agents', 'SKILLS.md'), '[security-review](skills/security-review/SKILL.md)');
  assert.match(projectSkillsPrompt(project), /Fixture security controls/);
  assert.match(projectSkillsPrompt(project), /assigned lead, worker, research or reviewer role/);
  fs.writeFileSync(manifest, JSON.stringify({ ...valid, skills: [{ name: '../../outside' }] }));
  assert.throws(() => projectSkillsPrompt(project), /Invalid or duplicate/);
  fs.writeFileSync(manifest, JSON.stringify({ ...valid, skills: [...valid.skills, ...valid.skills] }));
  assert.throws(() => projectSkillsPrompt(project), /Invalid or duplicate/);
  fs.writeFileSync(manifest, JSON.stringify(valid));
  fs.writeFileSync(path.join(skill, 'SKILL.md'), 'x'.repeat(64 * 1024 + 1));
  assert.throws(() => projectSkillsPrompt(project), /size limit/);
  const outside = path.join(dir, 'outside');
  fs.mkdirSync(outside);
  fs.writeFileSync(path.join(outside, 'SKILL.md'), 'Must not enter the prompt');
  fs.symlinkSync(outside, path.join(project, '.agents', 'skills', 'escape'), process.platform === 'win32' ? 'junction' : 'dir');
  fs.writeFileSync(manifest, JSON.stringify({ ...valid, skills: [{ name: 'escape' }] }));
  assert.throws(() => projectSkillsPrompt(project), /escapes/, 'A skill link cannot read outside the project');
  console.log('OK: project ECC instructions, missing-file errors, invalid names, duplicates, size limits and symlink containment.');
} finally {
  const resolved = path.resolve(dir);
  assert.equal(path.dirname(resolved), path.resolve(os.tmpdir()));
  assert.ok(path.basename(resolved).startsWith('pw-project-skills-'));
  fs.rmSync(resolved, { recursive: true, force: true });
}
