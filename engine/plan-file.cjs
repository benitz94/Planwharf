'use strict';

// The work plan the crew reads from disk: .planwharf/plan.md in the project folder.
// Git ignores that folder through .git/info/exclude, so the project's .gitignore stays as it is.

const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { gitEnvironment } = require('./project-workspace.cjs');

const EXCLUDE_LINE = '/.planwharf/';
const EXCLUDE_MARKER = '# Planwharf managed work plan';
const RULES = 'No push or merge. Files outside the goal are not touched.';

function git(folder, args, timeout = 20000) {
  const result = spawnSync('git', args, {
    cwd: folder,
    encoding: 'utf8',
    env: gitEnvironment(),
    windowsHide: true,
    timeout,
    maxBuffer: 8 * 1024 * 1024
  });
  return { ok: result.status === 0, stdout: result.stdout || '', stderr: result.stderr || '', error: result.error };
}

function isGit(folder) {
  if (!folder || typeof folder !== 'string') return false;
  const result = git(folder, ['rev-parse', '--is-inside-work-tree']);
  return result.ok && result.stdout.trim() === 'true';
}

function dirtyLines(folder, allUntracked = false) {
  const result = git(folder, ['status', '--porcelain', ...(allUntracked ? ['--untracked-files=all'] : [])]);
  if (!result.ok) return [];
  return result.stdout.split('\n').map(line => line.trimEnd()).filter(Boolean);
}

function dirtyName(line) {
  const body = String(line).slice(3);
  return (body.split(' -> ').at(-1) || body).trim() || String(line).trim();
}

// Cache and editor junk are not the user's work: they must not block a start.
function isNoise(line) {
  const name = dirtyName(line).replace(/\\/g, '/');
  return /(^|\/)__pycache__(\/|$)/.test(name)
    || /(^|\/)\.(pytest_cache|mypy_cache|ruff_cache)(\/|$)/.test(name)
    || /\.(pyc|pyo)$/.test(name)
    || /(^|\/)\.DS_Store$/.test(name);
}

function dirtyMessage(lines) {
  const names = lines.map(dirtyName).filter(Boolean).slice(0, 8);
  const more = lines.length > names.length ? `, and ${lines.length - names.length} more` : '';
  return `Commit or stash the changes in the folder first: ${names.join(', ')}${more}, then press Let the crew do it again.`;
}

function ensureExclude(folder, keepWorkPlan = false) {
  const located = git(folder, ['rev-parse', '--git-path', 'info/exclude']);
  if (!located.ok || !located.stdout.trim()) return;
  const file = path.resolve(folder, located.stdout.trim());
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const current = fs.existsSync(file) ? fs.readFileSync(file, 'utf8') : '';
  if (keepWorkPlan) {
    const escaped = EXCLUDE_MARKER.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    const next = current.replace(new RegExp('^' + escaped + '\\r?\\n/\\.planwharf/\\r?\\n', 'gm'), '');
    if (next !== current) fs.writeFileSync(file, next);
    return;
  }
  if (current.split(/\r?\n/).some(line => line.trim() === EXCLUDE_LINE)) return;
  const next = current + (current.length && !current.endsWith('\n') ? '\n' : '') + EXCLUDE_MARKER + '\n' + EXCLUDE_LINE + '\n';
  fs.writeFileSync(file, next);
}

function shortGoal(goal) {
  const slug = String(goal || 'work').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40).replace(/-+$/g, '');
  return slug || 'work';
}

function namedBranch(text) {
  const source = String(text || '');
  const patterns = [
    /\bbranch\s+`([^`]+)`/i,
    /\bbranch\s+'([^']+)'/i,
    /\bbranch\s+"([^"]+)"/i,
    // A bare word is a name only when the text says so ("on branch develop", "branch named develop")
    // or when it looks like one (a slash, dash, dot, underscore or digit). "branch creation" is not a branch.
    /\bbranch\s+(?:named|called)\s+([A-Za-z0-9][A-Za-z0-9._/-]*)/i,
    /\b(?:on|to|onto|into|new|use|switch|checkout)\s+(?:the\s+|a\s+)?branch\s+([A-Za-z0-9][A-Za-z0-9._/-]*)/i,
    /\bbranch\s+((?=[A-Za-z0-9._/-]*(?:[0-9]|[._/-][A-Za-z0-9]))[A-Za-z0-9][A-Za-z0-9._/-]*)/i
  ];
  for (const pattern of patterns) {
    const match = source.match(pattern);
    if (!match) continue;
    const name = match[1].trim().replace(/[.,;:]+$/g, '');
    if (/^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(name) && !/^(from|the|a|an|on|name|named|called|is|that|this|and|or)$/i.test(name)) return name;
  }
  return null;
}

// "On fix/plan-file, ..." names a branch only when that branch already exists here or on the remote.
function leadingBranch(folder, text) {
  const match = String(text || '').match(/^\s*on\s+([A-Za-z0-9][A-Za-z0-9._-]*\/[A-Za-z0-9._/-]*[A-Za-z0-9_-])\s*[,:]/i);
  if (!match) return null;
  const name = match[1];
  if (branchExists(folder, name)) return name;
  if (git(folder, ['show-ref', '--verify', '--quiet', 'refs/remotes/origin/' + name]).ok) return name;
  return null;
}

function branchName(goal, requested) {
  if (requested && /^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(requested)) return requested;
  return namedBranch(goal) || ('planwharf/' + shortGoal(goal));
}

function shortSha(sha) { return String(sha || '').slice(0, 7); }

function remoteDefault(folder) {
  const symbolic = git(folder, ['symbolic-ref', '--short', 'refs/remotes/origin/HEAD']);
  if (symbolic.ok && symbolic.stdout.trim()) return symbolic.stdout.trim();
  const listed = git(folder, ['ls-remote', '--symref', 'origin', 'HEAD'], 30000);
  const ref = listed.ok && listed.stdout.match(/ref:\s+refs\/heads\/(\S+)\s+HEAD/);
  if (ref) return 'origin/' + ref[1];
  if (git(folder, ['rev-parse', '--verify', '--quiet', 'origin/main']).ok) return 'origin/main';
  if (git(folder, ['rev-parse', '--verify', '--quiet', 'origin/master']).ok) return 'origin/master';
  return null;
}

function planPath(folder) { return path.join(folder, '.planwharf', 'plan.md'); }

function read(folder) {
  try { return fs.readFileSync(planPath(folder), 'utf8'); } catch { return ''; }
}

function parse(text) {
  const source = String(text || '');
  const sections = {};
  let name = '';
  for (const line of source.split(/\r?\n/)) {
    const heading = line.match(/^##\s+(.+?)\s*$/);
    if (heading) { name = heading[1].trim().toLowerCase(); sections[name] = []; continue; }
    if (name) sections[name].push(line);
  }
  const body = key => (sections[key] || []).join('\n').trim();
  const steps = (sections.steps || []).filter(line => /^- \[[ xX]\] /.test(line)).map(line => {
    const done = /^- \[[xX]\]/.test(line);
    const commit = done ? (line.split(' — ').at(-1) || '').trim() : '';
    return { line, done, commit };
  });
  return { goal: body('goal'), base: body('base'), branch: body('branch'), rules: body('rules'), done: body('done'), steps };
}

function stepLabel(step, all) {
  const role = step.role || 'Build';
  const model = step.model || 'unassigned';
  const twins = (all || []).filter(other => (other.role || 'Build') === role && (other.model || 'unassigned') === model);
  const title = twins.length > 1 && step.title ? `: ${String(step.title).replace(/\s+/g, ' ').trim().slice(0, 80)}` : '';
  return `${role} (${model})${title}`;
}

// Section text on its own lines: one newline style, and no line that reads as a section heading,
// so a goal or summary that quotes "## Steps" cannot take over the plan's sections when it is read back.
function block(text) {
  return String(text || '').replace(/\r\n?/g, '\n').trim().replace(/^(#+\s)/gm, '\\$1');
}

function render(spec) {
  const steps = (spec.steps || []).map(step => {
    if (typeof step === 'string') return step;
    const box = step.done ? 'x' : ' ';
    const label = step.label || stepLabel(step, spec.steps);
    const tail = step.done ? ` — ${step.commit || 'not committed'}` : '';
    return `- [${box}] ${label}${tail}`;
  });
  return [
    '# Work plan',
    '',
    '## Goal',
    '',
    block(spec.goal),
    '',
    '## Base',
    '',
    block(spec.base),
    '',
    '## Branch',
    '',
    block(spec.branch),
    '',
    '## Steps',
    '',
    ...(steps.length ? steps : ['- [ ] Build (unassigned)']),
    '',
    '## Rules',
    '',
    RULES,
    '',
    '## Done',
    '',
    block(spec.done),
    '',
    ''
  ].join('\n');
}

function archive(folder, goal) {
  const file = planPath(folder);
  if (!fs.existsSync(file)) return null;
  const dir = path.join(folder, '.planwharf', 'done');
  fs.mkdirSync(dir, { recursive: true });
  const base = `${new Date().toISOString().slice(0, 10)}-${shortGoal(goal)}.md`;
  let dest = path.join(dir, base);
  let n = 2;
  while (fs.existsSync(dest)) {
    dest = path.join(dir, base.replace(/\.md$/, `-${n}.md`));
    n += 1;
  }
  fs.copyFileSync(file, dest);
  return dest;
}

function writePlan(folder, spec) {
  const previous = parse(read(folder));
  const goal = block(spec.goal);
  const same = previous.goal && previous.goal === goal;
  // A finished plan (its Done section is filled) was already archived by finish(); do not keep a second copy.
  if (previous.goal && !same && !previous.done) archive(folder, previous.goal);
  const steps = spec.steps && spec.steps.length
    ? spec.steps.map(step => {
      const label = step.label || stepLabel(step, spec.steps);
      const kept = same && previous.steps.find(old => old.done && old.line.includes(label));
      return kept ? { label, done: true, commit: kept.commit } : { ...step, label, done: Boolean(step.done), commit: step.commit || '' };
    })
    : (same ? previous.steps.map(step => ({ label: step.line.replace(/^- \[[ xX]\] /, '').replace(/ — .*$/, ''), done: step.done, commit: step.commit })) : []);
  fs.mkdirSync(path.dirname(planPath(folder)), { recursive: true });
  const text = render({
    goal,
    base: spec.base || previous.base,
    branch: spec.branch || previous.branch,
    steps,
    done: spec.done != null ? spec.done : (same ? previous.done : '')
  });
  fs.writeFileSync(planPath(folder), text);
  return text;
}

function branchExists(folder, name) {
  return git(folder, ['show-ref', '--verify', '--quiet', 'refs/heads/' + name]).ok;
}

function switchTo(folder, name, startPoint) {
  if (branchExists(folder, name)) {
    const current = git(folder, ['branch', '--show-current']).stdout.trim();
    if (current === name) return { ok: true };
    const switched = git(folder, ['switch', name]);
    return switched.ok ? { ok: true } : { ok: false, reason: `Could not switch to ${name}: ${(switched.stderr || switched.error?.message || 'git switch failed').split('\n')[0]}` };
  }
  const args = startPoint ? ['switch', '--no-track', '-c', name, startPoint] : ['switch', '-c', name];
  const made = git(folder, args);
  return made.ok ? { ok: true } : { ok: false, reason: `Could not create ${name}: ${(made.stderr || made.error?.message || 'git switch failed').split('\n')[0]}` };
}

function contains(folder, sha) {
  if (!sha) return true;
  return git(folder, ['merge-base', '--is-ancestor', sha, 'HEAD']).ok;
}

// Checks the folder, creates or reuses the work branch, and writes the plan file.
function prepare(folder, options = {}) {
  if (!isGit(folder)) {
    const goal = String(options.goal || '').trim();
    if (goal || (options.steps && options.steps.length)) {
      writePlan(folder, { goal, base: 'Not a git repository.', branch: '(none)', steps: options.steps || [], done: '' });
    }
    return { ok: true, git: false, chat: null };
  }
  // Check the existing ignore state first when enabling: a plan previously hidden by our
  // rule should not itself prevent switching the preference on.
  if (options.keepWorkPlan !== true) ensureExclude(folder, false);
  const goal = String(options.goal || '').trim();
  const asked = options.branch && /^[A-Za-z0-9][A-Za-z0-9._/-]*$/.test(options.branch) ? options.branch : null;
  const name = asked || namedBranch(goal) || leadingBranch(folder, goal) || branchName(goal, null);
  // Uncommitted changes block a new work, not the same work going on: on its own branch they are the
  // crew's unfinished changes, and the next step starts from them.
  let dirty = dirtyLines(folder, options.keepWorkPlan === true).filter(line => !isNoise(line));
  if (options.keepWorkPlan === true) {
    const previous = parse(read(folder));
    if (previous.goal && previous.goal === block(options.goal) && previous.branch === currentBranch(folder) && !previous.done)
      dirty = dirty.filter(line => line !== '?? .planwharf/plan.md');
  }
  if (dirty.length && currentBranch(folder) !== name) return { ok: false, reason: dirtyMessage(dirty) };
  if (options.keepWorkPlan === true) ensureExclude(folder, true);
  const fetched = git(folder, ['fetch', 'origin'], 30000);
  let base = '';
  let baseSha = '';
  let startPoint = null;
  let chat = null;
  if (!fetched.ok) {
    const head = git(folder, ['rev-parse', 'HEAD']);
    baseSha = head.ok ? head.stdout.trim() : '';
    const shown = shortSha(baseSha);
    base = `Offline: the remote could not be read, so this work starts on the current commit ${shown}.`;
    chat = `The remote could not be read, so the crew starts on the current commit ${shown}.`;
  } else {
    const remote = remoteDefault(folder);
    if (!remote) return { ok: false, reason: 'The remote has no default branch. Set origin/HEAD, then press Let the crew do it again.' };
    const sha = git(folder, ['rev-parse', remote]);
    if (!sha.ok) return { ok: false, reason: `Could not read ${remote}. Fetch the remote, then press Let the crew do it again.` };
    baseSha = sha.stdout.trim();
    base = `${remote} ${shortSha(baseSha)}`;
    startPoint = remote;
  }
  const existed = branchExists(folder, name);
  const switched = switchTo(folder, name, existed ? null : startPoint);
  if (!switched.ok) return switched;
  // A work that already has its branch goes on from where it started: main may have moved on since, and
  // that is fine. The branch only has to come from the same history; its base is where it left main.
  if (existed && startPoint) {
    const since = git(folder, ['merge-base', 'HEAD', startPoint]);
    if (!since.ok || !since.stdout.trim()) return { ok: false, reason: `Branch ${name} has no history in common with ${startPoint}. Delete the branch and press Let the crew do it again.` };
    baseSha = since.stdout.trim();
    base = `${startPoint} ${shortSha(baseSha)}`;
  }
  if (!contains(folder, baseSha)) {
    return { ok: false, reason: `Branch ${name} does not contain ${base}. Start again from that commit, or delete the branch and press Let the crew do it again.` };
  }
  const current = git(folder, ['branch', '--show-current']).stdout.trim() || name;
  writePlan(folder, { goal, base, branch: current, steps: options.steps || [] });
  return { ok: true, git: true, branch: current, base, offline: Boolean(chat), chat };
}

function commitNote(folder) {
  if (!isGit(folder)) return 'not committed';
  if (dirtyLines(folder).length) return 'not committed';
  const head = git(folder, ['rev-parse', 'HEAD']);
  if (!head.ok) return 'not committed';
  const full = head.stdout.trim();
  const recorded = (parse(read(folder)).base.match(/\b([0-9a-f]{7,40})\b/i) || [])[1] || '';
  if (recorded && (full.startsWith(recorded) || recorded.startsWith(full.slice(0, recorded.length)))) return 'not committed';
  const short = git(folder, ['rev-parse', '--short=7', 'HEAD']);
  return short.ok ? short.stdout.trim() : 'not committed';
}

function tickStep(folder, spec = {}) {
  const file = planPath(folder);
  if (!fs.existsSync(file)) return false;
  const lines = fs.readFileSync(file, 'utf8').split(/\r?\n/);
  const title = spec.title ? String(spec.title).replace(/\s+/g, ' ').trim().slice(0, 80) : '';
  const role = spec.role || '';
  const model = spec.model || '';
  let index = title ? lines.findIndex(line => line.startsWith('- [ ]') && line.includes(title)) : -1;
  if (index < 0 && role) index = lines.findIndex(line => line.startsWith('- [ ]') && line.includes(`${role} (${model}`));
  if (index < 0) index = lines.findIndex(line => line.startsWith('- [ ]'));
  if (index < 0) return false;
  const commit = spec.commit || 'not committed';
  lines[index] = lines[index].replace('- [ ]', '- [x]') + ` — ${commit}`;
  fs.writeFileSync(file, lines.join('\n'));
  return true;
}

function currentBranch(folder) {
  if (!isGit(folder)) return '';
  return git(folder, ['branch', '--show-current']).stdout.trim();
}

function pushed(folder, branch) {
  const name = branch || currentBranch(folder);
  if (!name || !isGit(folder)) return false;
  const remote = git(folder, ['rev-parse', 'origin/' + name]);
  const head = git(folder, ['rev-parse', 'HEAD']);
  return remote.ok && head.ok && remote.stdout.trim() === head.stdout.trim();
}

function finish(folder, spec = {}) {
  const file = planPath(folder);
  if (!fs.existsSync(file)) return false;
  const parsed = parse(read(folder));
  const branch = spec.branch || parsed.branch || currentBranch(folder) || '(none)';
  const sent = spec.pushed != null ? Boolean(spec.pushed) : pushed(folder, branch);
  const summary = String(spec.summary || '').trim();
  const done = [
    parsed.goal ? `Goal: ${parsed.goal}` : '',
    summary ? `Summary: ${summary}` : '',
    `Branch: ${branch}`,
    `Pushed: ${sent ? 'yes' : 'no'}`
  ].filter(Boolean).join('\n');
  writePlan(folder, { goal: parsed.goal, base: parsed.base, branch, steps: parsed.steps.map(step => ({
    label: step.line.replace(/^- \[[ xX]\] /, '').replace(/ — .*$/, ''),
    done: step.done,
    commit: step.commit
  })), done });
  archive(folder, parsed.goal);
  return true;
}

module.exports = { isNoise,
  EXCLUDE_LINE, RULES, isGit, dirtyMessage, namedBranch, branchName, shortGoal,
  prepare, read, parse, tickStep, finish, commitNote, currentBranch, pushed, planPath
};
