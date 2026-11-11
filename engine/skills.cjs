'use strict';
// Settings > Skills and instructions: what each installed CLI reads from the user's own folders.
// Planwharf only lists these files and opens them for the user; it never writes their content.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

function places(home = os.homedir()) {
  return [
    { cli: 'claude', label: 'Claude Code', home: path.join(home, '.claude'), skills: path.join(home, '.claude', 'skills'), instructions: path.join(home, '.claude', 'CLAUDE.md'), settings: path.join(home, '.claude', 'settings.json') },
    { cli: 'codex', label: 'Codex', home: path.join(home, '.codex'), skills: path.join(home, '.codex', 'skills'), instructions: path.join(home, '.codex', 'AGENTS.md'), settings: path.join(home, '.codex', 'config.toml') },
    { cli: 'grok', label: 'Grok', home: path.join(home, '.grok'), skills: null, instructions: null, settings: path.join(home, '.grok', 'config.toml') }
  ];
}

// The name and first line of the description from a SKILL.md front matter.
function readSkill(file) {
  let text = '';
  try { text = fs.readFileSync(file, 'utf8').slice(0, 8000); } catch { return null; }
  const front = /^---\r?\n([\s\S]*?)\r?\n---/.exec(text)?.[1] || '';
  const field = name => {
    const match = new RegExp('^' + name + ':\\s*(.*)$', 'm').exec(front);
    return match ? match[1].trim().replace(/^["']|["']$/g, '') : '';
  };
  return { name: field('name') || path.basename(path.dirname(file)), description: field('description').slice(0, 300) };
}

function listSkills(dir) {
  if (!dir) return [];
  const found = new Map();
  const walk = (folder, depth, source) => {
    let entries = [];
    try { entries = fs.readdirSync(folder, { withFileTypes: true }); } catch { return; }
    if (entries.some(entry => entry.isFile() && entry.name === 'SKILL.md')) {
      const skill = readSkill(path.join(folder, 'SKILL.md'));
      if (skill && !found.has(skill.name)) found.set(skill.name, { ...skill, source });
      return;
    }
    if (depth === 0) return;
    for (const entry of entries) {
      if (!entry.isDirectory() || entry.name === '.trash') continue;
      const next = depth === 3 && entry.name === 'synced' ? 'Claude app' : depth === 3 && entry.name === '.system' ? 'built in' : source;
      walk(path.join(folder, entry.name), depth - 1, next);
    }
  };
  walk(dir, 3, 'yours');
  return [...found.values()].sort((a, b) => a.name.localeCompare(b.name));
}

function skillsOverview(home) {
  return places(home).filter(place => fs.existsSync(place.home)).map(place => ({
    cli: place.cli, label: place.label,
    skillsFolder: place.skills, skills: listSkills(place.skills),
    instructions: place.instructions, instructionsExist: Boolean(place.instructions && fs.existsSync(place.instructions)),
    settings: place.settings, settingsExist: fs.existsSync(place.settings)
  }));
}

// Only the fixed places above can be opened. A missing skills folder or instructions file is created
// empty when the user asks to open it, so there is somewhere to write; existing files are never changed.
function pathToOpen(cli, what, home) {
  const place = places(home).find(item => item.cli === cli);
  if (!place || !['skills', 'instructions', 'settings'].includes(what) || !place[what]) return null;
  if (what === 'skills') fs.mkdirSync(place.skills, { recursive: true });
  if (what === 'instructions' && !fs.existsSync(place.instructions)) fs.writeFileSync(place.instructions, '', { flag: 'wx' });
  if (what === 'settings' && !fs.existsSync(place.settings)) return null;
  return { file: place[what], folder: what === 'skills' };
}

module.exports = { places, listSkills, readSkill, skillsOverview, pathToOpen };
