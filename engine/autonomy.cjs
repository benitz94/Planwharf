'use strict';
// Project autonomy policy. The owner's setting grants authority; model text only
// proposes the next objective and is never parsed into any other action.

const LIMITS = Object.freeze({ maxMegaTasks: 20, maxNudgesPerHour: 3, maxObjectiveChars: 4000 });
const HOUR_MS = 3600000;
const WORK_ENDED = new Set(['worker failure', 'blocked worker']);

function createSettings(sessionId, at) {
  return { enabled: true, sessionId, enabledAt: at, started: false, approved: [], evaluated: [], nudgedKeys: [], nudgeTimes: [], assumptions: [], stopReason: null };
}

// Rewrites every push URL to a helper that does not exist. A deterrent for
// accidental pushes, not a sandbox: a process can override its own Git config.
function pushGuardEnvironment(env = process.env) {
  const index = Number.parseInt(env.GIT_CONFIG_COUNT, 10) || 0;
  return {
    GIT_CONFIG_COUNT: String(index + 1),
    [`GIT_CONFIG_KEY_${index}`]: 'url.planwharf-push-blocked://.pushInsteadOf',
    [`GIT_CONFIG_VALUE_${index}`]: ''
  };
}

function nextStep({ mode, sessionId, settings, row, alerts = [], now = Date.now() }) {
  const none = reason => ({ type: 'none', reason });
  if (!settings?.enabled) return none('off');
  if (mode !== 'auto') return none('manual-mode');
  if (settings.sessionId !== sessionId) return none('other-session');
  if (settings.stopReason) return none('stopped');
  if (!row) return none('no-project');
  // A checkpoint saved because a worker failed or stopped blocked is not the user's pause: the lead hears about it.
  const workEnded = row.status === 'checkpointed' && WORK_ENDED.has(row.checkpoint?.reason);
  if ((row.status === 'checkpointed' && !workEnded) || row.status === 'paused' || (row.megaTask?.pauseRequested && !workEnded)) return none('user-paused');
  // Work that ended blocked or failed goes back to the lead at once, with its results: the lead names the
  // rest of the work and the crew carries on. It never waits for an alert to notice it.
  if (workEnded && row.megaTaskId && !(settings.continued || []).includes(row.megaTaskId)) return { type: 'continue', megaTaskId: row.megaTaskId };
  if (row.status === 'needs_approval' && row.megaTaskId) {
    if (settings.approved.includes(row.megaTaskId)) return none('already-approved');
    if (settings.approved.length >= LIMITS.maxMegaTasks) return { type: 'limit', reason: 'MEGATASK_LIMIT' };
    return { type: 'approve', megaTaskId: row.megaTaskId };
  }
  // Work finished before this setting was turned on is history, not this session's work: start anew.
  if (!settings.started && (!row.megaTaskId || row.status === 'complete' || String(row.megaTask?.status || '').toUpperCase() === 'CANCELLED')) return { type: 'start' };
  if (row.status === 'complete' && row.megaTaskId && !settings.evaluated.includes(row.megaTaskId)) return { type: 'evaluate', megaTaskId: row.megaTaskId };
  const alert = alerts.find(item => item.active !== false && !item.ignored && !settings.nudgedKeys.includes(item.key));
  if (!alert) return none('idle');
  if (settings.nudgeTimes.filter(at => now - Date.parse(at) < HOUR_MS).length >= LIMITS.maxNudgesPerHour) return none('nudge-limit');
  return { type: 'nudge', alert };
}

const { shortLine, clip, sentences, SUMMARY_MAX, SPLIT_LINE_ASK, SUMMARY_ASK } = require('./plain-lines.cjs');

function leadInstructions() {
  return [
    'Reply with one JSON object and nothing else: {"done":true|false,"next":"objective or null","assumptions":["..."],"summary":"one or two plain sentences"}',
    'Set done to true only if the goal is met by the recorded results. Otherwise put the single next objective in next.',
    SUMMARY_ASK,
    'Decide open questions yourself and list what you assumed. Work stays inside the project folder: never push, merge to a shared branch, or publish a release.'
  ].join('\n');
}

function checksText(checks) {
  if (checks === undefined) return [];
  if (!checks) return ['Planwharf found no recognised test entry point (package.json test script or test files) in the project folder.'];
  const verdict = checks.timedOut ? 'TIMED OUT' : checks.exitCode === 0 ? 'PASSED' : `FAILED (exit ${checks.exitCode})`;
  return [`Planwharf ran \`${checks.label}\` in the project folder just now: ${verdict} in ${checks.seconds} s. Output tail:`, '```', checks.output || '(no output)', '```',
    'This run is fresh evidence: do not run the tests again. Do not edit files. Inspect the project folder (files, git log, git diff) when the results leave a doubt.'];
}

// True when the evidence is clear enough to end without asking the lead.
function cleanFinish(results, checks) {
  const list = Array.isArray(results) ? results : [];
  if (list.length < 2 || !checks || checks.timedOut || checks.exitCode !== 0) return false;
  return list.every(result => result.status === 'COMPLETE' && !(Array.isArray(result.blockers) && result.blockers.length));
}

function resultLines(results) {
  return (results || []).slice(-6).map(result => {
    const files = Array.isArray(result.filesChanged) && result.filesChanged.length ? ` Files changed: ${result.filesChanged.slice(0, 20).join(', ')}.` : '';
    const blockers = Array.isArray(result.blockers) && result.blockers.length ? ` Blockers: ${result.blockers.slice(0, 3).map(item => String(item).slice(0, 300)).join(' | ')}` : '';
    return `- ${result.status}: ${result.summary || 'no summary'}${files}${blockers}`;
  });
}
function evaluationPrompt({ name, goal, results, checks }) {
  const lines = resultLines(results);
  return [`Autonomous review of project ${name}. The current MegaTask finished.`, `Goal: ${goal || 'not recorded'}`, 'Recorded results:', ...(lines.length ? lines : ['- none recorded']), ...checksText(checks),
    'A worker that did part of the goal is not the end of it: if the goal is not met, put all the remaining work in next, so the crew continues.', leadInstructions()].join('\n');
}

// Work that stopped part way (a step blocked, failed or reported partial) goes back to the lead with its
// results. The lead names the rest of the work; Planwharf prepares it and the lead splits it again.
function continuePrompt({ name, goal, results, reason }) {
  const lines = resultLines(results);
  return [`Autonomous review of project ${name}. The current MegaTask stopped before the goal was met (${reason || 'a step did not finish'}).`, `Goal: ${goal || 'not recorded'}`, 'Recorded results:', ...(lines.length ? lines : ['- none recorded']),
    'Inspect the project folder (files, git log, git diff) to see what is already done. Do not edit files.',
    'If steps stopped on tests or checks outside their own files, give those failures to one part that may change those files (or to the part that wires the work together), instead of repeating the same parts.',
    'Put in next the complete remaining work, naming what is already done so the crew does not redo it. Planwharf prepares it and you split it into parts again, so a step that was too big becomes several smaller ones. If a blocker needs the owner (an approval, a key, a decision), say so in next.',
    leadInstructions()].join('\n');
}

// Text comes from backend-detected facts, so no model runs to raise a nudge.
function nudgePrompt({ name, goal, alert }) {
  return [`Supervisor alert for project ${name}: ${alert.title}. ${alert.message}`, `Goal: ${goal || 'not recorded'}`, 'If active workers are still making progress, set next to null. If the work is stuck, name the objective that recovers it.', leadInstructions()].join('\n');
}

// Every balanced {...} block in the text, so prose, code fences or braces around the answer do not hide it.
function jsonObjects(text) {
  const found = [];
  for (let start = text.indexOf('{'); start >= 0; start = text.indexOf('{', start + 1)) {
    let depth = 0, quoted = false, escaped = false;
    for (let index = start; index < text.length; index += 1) {
      const char = text[index];
      if (quoted) { if (escaped) escaped = false; else if (char === '\\') escaped = true; else if (char === '"') quoted = false; continue; }
      if (char === '"') quoted = true;
      else if (char === '{') depth += 1;
      else if (char === '}' && --depth === 0) { try { found.push(JSON.parse(text.slice(start, index + 1))); } catch { /* not JSON */ } break; }
    }
  }
  return found;
}

function parseDecision(text) {
  if (typeof text !== 'string') return null;
  const parsed = jsonObjects(text).filter(item => item && typeof item.done === 'boolean').at(-1);
  if (!parsed) return null;
  const next = typeof parsed.next === 'string' && parsed.next.trim() ? parsed.next.trim().slice(0, LIMITS.maxObjectiveChars) : null;
  if (!parsed.done && parsed.next != null && !next) return null;
  const assumptions = Array.isArray(parsed.assumptions) ? parsed.assumptions.filter(item => typeof item === 'string').map(item => item.slice(0, 500)).slice(0, 10) : [];
  const summary = typeof parsed.summary === 'string' && parsed.summary.trim() ? clip(sentences(parsed.summary, 2), SUMMARY_MAX) : '';
  return { done: parsed.done, next: parsed.done ? null : next, assumptions, ...(summary ? { summary } : {}) };
}

// Second try when the lead's reply could not be read: the same question, asking for the JSON object alone.
function strictDecisionPrompt(question) {
  return `${question}\n\nYour previous reply could not be read. Reply with only the JSON object, with no other text before or after it.`;
}

// Before approval the lead picks who does each slice, instead of the catalog order.
// Each slice lists only the models whose roles allow it (Worker builds, Verifier checks).
function assignmentPrompt({ name, goal, slices, candidates, said }) {
  // The crew a lead proposed in the chat is its own plan: it comes with the choice, so the lead does not
  // choose again from nothing. The end of the answer is kept, where the crew is usually proposed.
  const earlier = typeof said === 'string' && said.trim() ? said.trim() : '';
  const quoted = earlier.length > 2500 ? '…' + earlier.slice(-2499) : earlier;
  return [`Autonomous planning for project ${name}. Goal: ${goal || 'not recorded'}`,
    ...(quoted ? [`You told the user in the chat, before this work started:\n<<<\n${quoted}\n>>>`,
      'If you proposed a crew there, keep that crew unless one of its models is not allowed below; if you choose differently, say why in "why".'] : []),
    'The MegaTask has these slices, run in order. Each lists the models allowed to take it:',
    ...slices.map((slice, index) => `${index + 1}. ${slice}\n   Allowed: ${candidates[index].map(worker => `${worker.id} (${worker.label}, provider ${worker.provider})`).join('; ')}`),
    'Choose one allowed model per slice. Pick the model most likely to do that slice correctly. For each later slice prefer a different provider than the slice before it, so the work is checked independently.',
    'You may inspect the project to judge the slices; do not change anything.',
    'Reply with one JSON object and nothing else: {"assign":["worker id for slice 1", "..."],"why":"one sentence"}.'].join('\n');
}

function parseAssignment(text, count, allowed) {
  const allowedAt = index => Array.isArray(allowed[0]) ? allowed[index] : allowed;
  if (typeof text !== 'string') return null;
  const start = text.indexOf('{'), end = text.lastIndexOf('}');
  if (start < 0 || end < start) return null;
  let parsed; try { parsed = JSON.parse(text.slice(start, end + 1)); } catch { return null; }
  const ids = Array.isArray(parsed?.assign) ? parsed.assign : null;
  if (!ids || ids.length !== count || !ids.every((id, index) => typeof id === 'string' && allowedAt(index).includes(id))) return null;
  return { assign: ids, why: typeof parsed.why === 'string' ? parsed.why.slice(0, 300) : '' };
}

// The lead splits the work into parts that can run at the same time, each limited to the files it may
// change, and picks a model for each by its weight. Planwharf checks every rule; a reply that breaks one
// is refused and the work keeps its usual plan (build, then verify).
const MAX_PARTS = 10;
// A big job may have more parts than run at once: the rest start as earlier ones finish.
const MAX_SPLIT_PARTS = 30;
const SIZES = ['light', 'normal', 'heavy'];
function splitPrompt({ name, goal, builders, verifiers, files, said, documents }) {
  const earlier = typeof said === 'string' && said.trim() ? said.trim() : '';
  const quoted = earlier.length > 2500 ? '…' + earlier.slice(-2499) : earlier;
  const describe = model => `${model.id} (${model.label}, provider ${model.provider}, ${model.tier}${Number.isFinite(model.left) ? `, ${model.left}% quota left` : ', quota not reported'})`;
  const listed = (files || []).slice(0, 400);
  return [`Autonomous planning for project ${name}. Goal: ${goal || 'not recorded'}`,
    ...(quoted ? [`You told the user in the chat, before this work started:\n<<<\n${quoted}\n>>>`] : []),
    `Your crew can work in parallel: up to ${MAX_PARTS} workers at the same time, all in the same project folder. You may give up to ${MAX_SPLIT_PARTS} parts: the rest start as earlier ones finish.`,
    'Size the work by what the goal and the documents it names ask for, not by the length of the goal line: a goal that points to a sheet or plan listing several modules, screens or files is a large job. Read those documents before you decide.',
    'Split the work into parts where that saves time or keeps each part small enough for one worker to finish: a worker given too much stops part way. Parts that run at the same time must touch different files: two workers on one file overwrite each other. Only a truly small job (one change in a few files) stays one part.',
    'For each part give: title; detail (complete instructions, the worker sees nothing else); files (the files or folders it may change, relative to the project folder; nothing outside them); dependsOn (indexes of earlier parts that must finish first); size (light, normal or heavy); model (one id allowed to build).',
    SPLIT_LINE_ASK,
    'Files that every part would touch (project files such as .csproj or package.json, shared configuration) belong to one last integration part that depends on the others. A part may also change the files of the parts it depends on, once they are finished.',
    'Each model has a weight: light, default or premium. Planwharf enforces it: a light part goes to a light model, a normal part to a default or light model, a heavy part to any model; a part given to a heavier model is moved to a lighter one when there is one. Strong models working at the same time drain their company\'s shared quota fast: keep premium models for heavy parts.',
    'Spread the parts over different models and companies: each company has its own quota, and many parts on one company drain it and leave the others idle. Models of the same company are different models too. Planwharf moves a part to a less busy model of the same weight when one model or company has more than its share.',
    'Also name "verify": the model that checks the whole work at the end, allowed to verify, ideally from a company that built none of the parts.',
    'Models allowed to build: ' + builders.map(describe).join('; '),
    'Models allowed to verify: ' + verifiers.map(describe).join('; '),
    ...(listed.length ? [`Project files (${listed.length}${(files || []).length > listed.length ? ' of ' + files.length : ''}):\n${listed.join('\n')}`] : []),
    ...(documents || []).map(doc => `Document the goal names, ${doc.path}${doc.clipped ? ' (start only; read the rest in the project folder)' : ''}:\n<<<\n${doc.text}\n>>>`),
    'You may read any project file to size the work (the project folder is open to you); do not change anything.',
    'Reply with one JSON object and nothing else: {"parts":[{"title":"","detail":"","files":["relative/path"],"dependsOn":[],"size":"normal","model":"id"}],"verify":"id","why":"one sentence"}'].join('\n');
}

// The weight a part may use: a light part a light model, a normal part a default or light model, a heavy
// part any model. A part given to a heavier model moves to a lighter one when there is one, so the strong
// models (and their company's quota) are kept for the heavy work.
const FITS = { light: ['light', 'default', 'premium'], normal: ['default', 'light', 'premium'], heavy: ['premium', 'default', 'light'] };
const ALLOWED = { light: ['light'], normal: ['default', 'light'], heavy: ['premium', 'default', 'light'] };
function fitWeights(parts, builders) {
  const byId = new Map((builders || []).map(model => [model.id, model]));
  const load = new Map();
  const moved = [];
  const out = (parts || []).map(part => {
    const size = SIZES.includes(part.size) ? part.size : 'normal';
    const current = byId.get(part.model);
    let chosen = part.model;
    if (!current || !ALLOWED[size].includes(current.tier)) {
      // The lightest weight that fits and has a model; among those, the least busy, then the most quota.
      for (const tier of FITS[size]) {
        const options = (builders || []).filter(model => model.tier === tier);
        if (!options.length) continue;
        if (current && FITS[size].indexOf(tier) >= FITS[size].indexOf(current.tier)) break;
        options.sort((a, b) => ((load.get(a.id) || 0) - (load.get(b.id) || 0)) || ((Number.isFinite(a.left) ? 0 : 1) - (Number.isFinite(b.left) ? 0 : 1)) || ((b.left ?? 0) - (a.left ?? 0)));
        chosen = options[0].id;
        break;
      }
    }
    load.set(chosen, (load.get(chosen) || 0) + 1);
    if (chosen !== part.model) moved.push({ title: part.title, size, from: part.model, to: chosen });
    return chosen === part.model ? part : { ...part, model: chosen };
  });
  return { parts: out, moved };
}

// After the weights, no model and no company takes more than its share of the parts while another
// model of an allowed weight has room: a part goes to the least busy company, then the least busy model.
// The lead's choice stays when it is within its share.
function spreadParts(parts, builders) {
  const list = (builders || []).filter(model => model && model.id);
  const companyOf = model => model.provider || model.id;
  const modelLoad = new Map(), companyLoad = new Map();
  const moved = [];
  const total = (parts || []).length;
  const out = (parts || []).map(part => {
    const size = SIZES.includes(part.size) ? part.size : 'normal';
    const allowed = list.filter(model => ALLOWED[size].includes(model.tier));
    const current = allowed.find(model => model.id === part.model);
    let chosen = part.model;
    if (allowed.length > 1) {
      const companies = new Set(allowed.map(companyOf));
      const modelShare = Math.ceil(total / allowed.length), companyShare = Math.ceil(total / companies.size);
      const fits = model => (modelLoad.get(model.id) || 0) < modelShare && (companyLoad.get(companyOf(model)) || 0) < companyShare;
      if (!current || !fits(current)) {
        const ranked = [...allowed].sort((a, b) => ((companyLoad.get(companyOf(a)) || 0) - (companyLoad.get(companyOf(b)) || 0))
          || ((modelLoad.get(a.id) || 0) - (modelLoad.get(b.id) || 0)) || ((b.left ?? 50) - (a.left ?? 50)));
        chosen = (ranked.find(fits) || ranked[0]).id;
      }
    }
    const model = list.find(item => item.id === chosen);
    modelLoad.set(chosen, (modelLoad.get(chosen) || 0) + 1);
    if (model) companyLoad.set(companyOf(model), (companyLoad.get(companyOf(model)) || 0) + 1);
    if (chosen !== part.model) moved.push({ title: part.title, size, from: part.model, to: chosen, spread: true });
    return chosen === part.model ? part : { ...part, model: chosen };
  });
  return { parts: out, moved };
}

const normalPath = file => file.replace(/\\/g, '/').replace(/\/+$/, '');
// Two scopes overlap when they share a file, or one is a folder holding the other.
const overlaps = (a, b) => a.some(x => b.some(y => x === y || y.startsWith(x + '/') || x.startsWith(y + '/')));

function parseSplit(text, { builders, verifiers }) {
  if (typeof text !== 'string') return null;
  const parsed = jsonObjects(text).filter(item => item && Array.isArray(item.parts)).at(-1);
  if (!parsed || !parsed.parts.length || parsed.parts.length > MAX_SPLIT_PARTS || !verifiers.length) return null;
  // A model the lead may not use, or an unknown size, does not throw the split away: Planwharf picks the
  // model by the part's weight (an empty model here) and reads an unknown size as normal.
  const verify = verifiers.includes(parsed.verify) ? parsed.verify : verifiers[0];
  const parts = [];
  for (const [index, part] of parsed.parts.entries()) {
    if (!part || typeof part.title !== 'string' || !part.title.trim() || typeof part.detail !== 'string' || !part.detail.trim()) return null;
    const size = SIZES.includes(part.size) ? part.size : 'normal';
    const model = builders.includes(part.model) ? part.model : '';
    if (!Array.isArray(part.files) || !part.files.length || !part.files.every(file => typeof file === 'string' && file.trim())) return null;
    const files = part.files.map(file => normalPath(file.trim()));
    // A scope stays inside the project folder: no absolute path, no way out.
    if (files.some(file => !file || /^([a-zA-Z]:|\/)/.test(file) || file.split('/').includes('..'))) return null;
    const dependsOn = part.dependsOn === undefined ? [] : part.dependsOn;
    if (!Array.isArray(dependsOn) || !dependsOn.every(parent => Number.isInteger(parent) && parent >= 0 && parent < index)) return null;
    const line = typeof part.line === 'string' && part.line.trim() ? shortLine(part.line) : '';
    parts.push({ title: part.title.trim().slice(0, 200), detail: part.detail.trim().slice(0, 12000), files, dependsOn: [...new Set(dependsOn)], size, model, ...(line ? { line } : {}) });
  }
  // Parts that would change the same files are put in order, earlier first: they never run together.
  const before = index => { const seen = new Set(), queue = [...parts[index].dependsOn]; while (queue.length) { const next = queue.pop(); if (!seen.has(next)) { seen.add(next); queue.push(...parts[next].dependsOn); } } return seen; };
  for (let later = 1; later < parts.length; later += 1)
    for (let earlier = 0; earlier < later; earlier += 1)
      if (overlaps(parts[earlier].files, parts[later].files) && !before(later).has(earlier)) parts[later].dependsOn = [...parts[later].dependsOn, earlier].sort((a, b) => a - b);
  return { parts, verify, why: typeof parsed.why === 'string' ? parsed.why.slice(0, 300) : '' };
}

// The project documents a goal points to, for the lead to size the work by: a path written in the goal
// (`docs/plan.md` or its file name), or a numbered sheet or job (`sheet 42`, `job 42`) that matches
// exactly one numbered Markdown file. Only files in the project's own list are returned.
const DOC_LIMIT = 12000, DOCS_LIMIT = 30000;
function namedDocuments(goal, files) {
  const text = String(goal || '');
  const list = (files || []).map(file => String(file).replace(/\\/g, '/'));
  const found = [];
  const add = file => { if (file && !found.includes(file) && found.length < 4) found.push(file); };
  for (const file of list) {
    const base = file.split('/').pop();
    if (text.includes(file) || (/\.(md|txt)$/i.test(base) && base.length > 6 && text.includes(base))) add(file);
  }
  for (const match of text.matchAll(/\b(?:sheet|job|foglio|lavoro)\s+(\d{1,3})\b/gi)) {
    const pattern = new RegExp(`(^|/)0*${match[1]}-[^/]*\\.md$`, 'i');
    const sheets = list.filter(file => pattern.test(file));
    if (sheets.length === 1) add(sheets[0]);
  }
  return found;
}
function readDocuments(folder, names, read) {
  let room = DOCS_LIMIT;
  const out = [];
  for (const name of names) {
    if (room <= 0) break;
    let text;
    try { text = read(require('node:path').join(folder, name)); } catch { continue; }
    if (typeof text !== 'string' || !text.trim()) continue;
    const limit = Math.min(DOC_LIMIT, room);
    out.push({ path: name, text: text.slice(0, limit), clipped: text.length > limit });
    room -= Math.min(text.length, limit);
  }
  return out;
}

module.exports = { spreadParts, MAX_SPLIT_PARTS, namedDocuments, readDocuments, continuePrompt, fitWeights, MAX_PARTS, splitPrompt, parseSplit, LIMITS, strictDecisionPrompt, cleanFinish, createSettings, pushGuardEnvironment, nextStep, evaluationPrompt, nudgePrompt, parseDecision, assignmentPrompt, parseAssignment };
