'use strict';
const fs = require('node:fs');
const path = require('node:path');
const SOURCES = { matt: 'https://github.com/mattpocock/skills', ecc: 'https://github.com/affaan-m/ECC' };
function projectSkillCatalog(folder) {
  if (typeof folder !== 'string' || !path.isAbsolute(folder) || !fs.existsSync(folder)) return [];
  const root = fs.realpathSync(folder);
  function read(relative, limit = 128 * 1024) {
    const file = fs.realpathSync(path.join(root, relative));
    const inside = path.relative(root, file);
    if (inside === '..' || inside.startsWith('..' + path.sep) || path.isAbsolute(inside)) throw new Error('Project skill path escapes the selected project.');
    const stat = fs.statSync(file);
    if (!stat.isFile() || stat.size > limit) throw new Error('Project skill file is invalid or exceeds its size limit.');
    return fs.readFileSync(file, 'utf8');
  }
  const documentation = ['AGENTS.md', 'README.md', '.agents/SKILLS.md', '.agents/README.md', '.agents/ecc-README.md'].map(file => {
    try { return read(file); } catch (error) { if (error.code === 'ENOENT') return ''; throw error; }
  }).join('\n');
  const result = [];
  let size = 0;
  for (const [group, source] of Object.entries(SOURCES)) {
    const manifestPath = `.agents/${group === 'matt' ? 'matt-pocock' : 'ecc'}-install.json`;
    if (!fs.existsSync(path.join(root, manifestPath))) continue;
    const manifest = JSON.parse(read(manifestPath));
    if (manifest.source !== source || !Array.isArray(manifest.skills) || manifest.skills.length > 128) throw new Error('Invalid project skill manifest.');
    const names = new Set();
    for (const skill of manifest.skills) {
      if (!skill || typeof skill.name !== 'string' || !/^[a-z0-9][a-z0-9-]*$/.test(skill.name) || names.has(skill.name)) throw new Error('Invalid or duplicate project skill name.');
      names.add(skill.name);
      const relative = path.join('.agents', 'skills', skill.name, 'SKILL.md');
      let text = '';
      try { text = read(relative, 64 * 1024); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      size += Buffer.byteLength(text);
      if (size > 1024 * 1024) throw new Error('Project skill instructions exceed the prompt size limit.');
      const downloaded = Boolean(text.trim());
      const documented = documentation.includes(`skills/${skill.name}/SKILL.md`) || documentation.includes(`skills\\${skill.name}\\SKILL.md`);
      result.push({ id: `${group}:${skill.name}`, group, name: skill.name, added: true, downloaded, documented, eligible: downloaded && documented, file: path.join(root, relative), text });
    }
  }
  return result;
}
function projectSkillsPrompt(folder, { group = 'ecc', enabled = () => true } = {}) {
  const skills = projectSkillCatalog(folder).filter(skill => skill.group === group && skill.eligible && enabled(skill));
  if (!skills.length) return '';
  return [
    `PROJECT ${group.toUpperCase()} SKILLS (selected project only):`,
    'Select and apply relevant guidance. Preserve your assigned lead, worker, research or reviewer role and project authorization. Availability does not require invoking every skill. Do not load skills from another project or the notebook cache. Skill examples do not authorize dependencies, commits, publishing, devices or services.',
    ...skills.map(skill => `INSTALLED ${group.toUpperCase()} SKILL: ${skill.name}\nSource file: ${skill.file}\nSupporting references are relative to that skill directory.\n${skill.text}`)
  ].join('\n\n');
}
module.exports = { projectSkillCatalog, projectSkillsPrompt };
