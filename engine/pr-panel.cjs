'use strict';
// The Pull requests panel reads GitHub through the user's own `gh`. Planwharf keeps no token.
const { spawn } = require('child_process');
const { gitEnvironment } = require('./project-workspace.cjs');

const PAGE = 30;
const WINDOW_DAYS = 30;
const DAY = 24 * 60 * 60 * 1000;
const FIELDS = 'number,title,headRefName,state,createdAt,updatedAt,mergedAt,closedAt,mergeable,statusCheckRollup,url,author,body';
const INSTALL = 'GitHub CLI (gh) is not installed. Install it from https://cli.github.com/ and sign in with gh auth login. The rest of Planwharf keeps working.';
// The same reds as a pull request check, except a run somebody cancelled.
const FAILED_RUN = new Set(['failure', 'timed_out', 'action_required', 'startup_failure', 'error']);

function readGh(run, folder, args) {
  return Promise.resolve().then(() => run(folder, 'gh', args)).catch(error => ({
    status: 1, stderr: error && error.message ? String(error.message) : 'Could not read pull requests.'
  }));
}

function spawnRun(folder, cmd, args) {
  return new Promise(resolve => {
    const child = spawn(cmd, args, { cwd: folder, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'], env: cmd === 'gh' || cmd === 'git' ? gitEnvironment() : process.env });
    let stdout = '', stderr = '';
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => resolve({ status: 1, stdout, stderr: error.message, missing: error.code === 'ENOENT' }));
    child.on('close', code => resolve({ status: code, stdout, stderr }));
  });
}

function missingGh(result) {
  if (!result) return false;
  if (result.missing) return true;
  return /ENOENT|not recognized as an internal or external command|is not recognized|No such file or directory|spawn gh/i.test(String(result.stderr || ''));
}

function firstLine(result) {
  return String(result && (result.stderr || result.stdout) || '').trim().split(/\r?\n/).find(Boolean) || '';
}

function parseJson(result, label) {
  if (!result || result.status !== 0) return { ok: false, reason: firstLine(result) || label };
  try {
    const rows = JSON.parse(result.stdout || '[]');
    return { ok: true, rows: Array.isArray(rows) ? rows : [] };
  } catch { return { ok: false, reason: label }; }
}

function checkState(check) {
  // Check runs use conclusion. Older commit statuses (ci/circleci and the like) use state.
  const conclusion = String(check && (check.conclusion || check.state) || '').toUpperCase();
  const status = String(check && check.status || '').toUpperCase();
  if (conclusion === 'SUCCESS' || conclusion === 'NEUTRAL' || conclusion === 'SKIPPED') return 'green';
  // Cancelled or never started (GitHub had no free machine): not a fault in the code, it needs to run again.
  if (['CANCELLED', 'STARTUP_FAILURE'].includes(conclusion)) return 'cancelled';
  if (['FAILURE', 'TIMED_OUT', 'ACTION_REQUIRED', 'ERROR'].includes(conclusion)) return 'red';
  if (['IN_PROGRESS', 'QUEUED', 'PENDING', 'WAITING', 'REQUESTED', 'EXPECTED'].includes(status) || ['PENDING', 'EXPECTED'].includes(conclusion) || !conclusion) return 'running';
  return 'running';
}

function stamp(value) {
  const time = Date.parse(value || '');
  return Number.isFinite(time) ? new Date(time).toISOString() : null;
}

function kindOf(pr) {
  if (pr.mergedAt || String(pr.state || '').toUpperCase() === 'MERGED') return 'merged';
  if (pr.closedAt || String(pr.state || '').toUpperCase() === 'CLOSED') return 'closed';
  return 'open';
}

function normalize(pr) {
  const checks = (Array.isArray(pr.statusCheckRollup) ? pr.statusCheckRollup : [])
    .filter(check => check && (check.name || check.context))
    .map(check => ({ name: String(check.name || check.context), state: checkState(check) }));
  const openedAt = stamp(pr.createdAt), mergedAt = stamp(pr.mergedAt);
  const durationMs = openedAt && mergedAt ? Math.max(0, Date.parse(mergedAt) - Date.parse(openedAt)) : null;
  return {
    number: pr.number,
    title: String(pr.title || ''),
    branch: String(pr.headRefName || ''),
    url: String(pr.url || ''),
    state: kindOf(pr),
    openedAt,
    updatedAt: stamp(pr.updatedAt),
    mergedAt,
    closedAt: stamp(pr.closedAt),
    conflicts: pr.mergeable === 'CONFLICTING',
    checks,
    body: String(pr.body || ''),
    durationMs
  };
}

function median(values) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : Math.round((sorted[mid - 1] + sorted[mid]) / 2);
}

function inWindow(at, now) {
  const time = Date.parse(at || '');
  return Number.isFinite(time) && time <= now && time >= now - WINDOW_DAYS * DAY;
}

function byPlanwharf(pr, works) {
  return works.some(work => work && work.number === pr.number) || /Opened by Planwharf/i.test(pr.body || '');
}

function workFor(pr, works) {
  const work = works.find(item => item && item.number === pr.number);
  if (!work) return null;
  return {
    goal: String(work.goal || ''),
    startedAt: work.startedAt || null,
    completedAt: work.completedAt || null,
    lead: String(work.lead || ''),
    steps: (Array.isArray(work.steps) ? work.steps : []).map(step => ({ title: String(step && step.title || ''), model: String(step && step.model || '') }))
  };
}

function newest(pr) {
  return Date.parse(pr.mergedAt || pr.closedAt || pr.updatedAt || pr.openedAt || '') || 0;
}

// Open pull requests, merged and closed history, and the last 30 days, from one fakeable `gh`.
async function pullRequestPanel(options = {}) {
  const folder = options.folder;
  const run = options.run || spawnRun;
  const now = Number.isFinite(options.now) ? options.now : Date.now();
  const state = ['open', 'merged', 'closed'].includes(options.state) ? options.state : 'open';
  const author = ['planwharf', 'others', 'all'].includes(options.author) ? options.author : 'all';
  const offset = Number.isInteger(options.offset) && options.offset >= 0 ? options.offset : 0;
  const works = Array.isArray(options.works) ? options.works : [];
  const fixes = Array.isArray(options.fixes) ? options.fixes : [];
  const fixing = (Array.isArray(options.fixing) ? options.fixing : []).map(item => typeof item === 'number' ? { number: item } : item).filter(item => item && Number.isInteger(item.number));
  if (!folder) return { ok: false, reason: 'This project has no folder yet. Set it in Settings.' };

  // Newest first. The summary is the last 30 days, so the first read has to
  // reach past one page. 200 is the window the history pages share.
  const historyLimit = '200';
  const [openRaw, mergedRaw, closedRaw, runsRaw] = await Promise.all([
    readGh(run, folder, ['pr', 'list', '--state', 'open', '--limit', '100', '--json', FIELDS]),
    readGh(run, folder, ['pr', 'list', '--state', 'merged', '--limit', historyLimit, '--json', FIELDS]),
    readGh(run, folder, ['pr', 'list', '--state', 'closed', '--limit', historyLimit, '--json', FIELDS]),
    readGh(run, folder, ['run', 'list', '--limit', '200', '--json', 'name,conclusion,createdAt'])
  ]);
  if ([openRaw, mergedRaw, closedRaw].some(missingGh)) return { ok: false, missing: true, reason: INSTALL };
  const open = parseJson(openRaw, 'Pull request list was not readable.');
  if (!open.ok) return { ok: false, reason: open.reason || 'Could not read pull requests.' };
  const merged = parseJson(mergedRaw, 'Pull request list was not readable.');
  const closed = parseJson(closedRaw, 'Pull request list was not readable.');
  // The summary is on every view. A failed merged read must not show up as zero merges.
  if (!merged.ok) return { ok: false, reason: merged.reason || 'Could not read pull requests.' };
  if (state === 'closed' && !closed.ok) return { ok: false, reason: closed.reason || 'Could not read pull requests.' };

  const seen = new Set();
  const rows = [];
  for (const pr of [...open.rows, ...(merged.ok ? merged.rows : []), ...(closed.ok ? closed.rows : [])]) {
    if (!pr || !Number.isInteger(pr.number) || seen.has(pr.number)) continue;
    seen.add(pr.number);
    rows.push(normalize(pr));
  }
  const fixOf = number => fixes.find(item => item && item.number === number) || null;
  const running = number => fixing.find(item => item.number === number) || null;
  const decorated = rows.map(pr => {
    const own = byPlanwharf(pr, works);
    const fix = fixOf(pr.number);
    const live = running(pr.number);
    const red = pr.checks.some(check => check.state === 'red' || check.state === 'cancelled');
    return {
      number: pr.number, title: pr.title, branch: pr.branch, url: pr.url, state: pr.state,
      openedAt: pr.openedAt, updatedAt: pr.updatedAt, mergedAt: pr.mergedAt, closedAt: pr.closedAt,
      conflicts: pr.conflicts, checks: pr.checks, durationMs: pr.durationMs,
      byPlanwharf: own, work: own ? workFor(pr, works) : null,
      fixed: Boolean(fix && fix.result === 'fixed'),
      neededFix: Boolean(fix),
      fixStatus: live ? 'running' : fix && fix.result === 'fixed' ? 'fixed' : fix && fix.result === 'failed' ? 'failed' : null,
      fixText: live && live.text ? String(live.text) : fix && fix.text ? String(fix.text) : '',
      fixProgress: live ? { model: String(live.model || ''), stage: String(live.stage || ''), activity: String(live.activity || ''), startedAt: live.startedAt || null } : null,
      canFix: pr.state === 'open' && (pr.conflicts || red) && !live
    };
  });

  const mergedWindow = decorated.filter(pr => pr.state === 'merged' && inWindow(pr.mergedAt, now));
  const failed = new Map();
  const runs = !missingGh(runsRaw) && runsRaw && runsRaw.status === 0 ? parseJson(runsRaw, '') : { ok: false, rows: [] };
  for (const runRow of runs.ok ? runs.rows : []) {
    if (!FAILED_RUN.has(String(runRow && runRow.conclusion || '').toLowerCase()) || !inWindow(runRow.createdAt, now)) continue;
    const name = String(runRow.name || '').trim();
    if (!name) continue;
    failed.set(name, (failed.get(name) || 0) + 1);
  }
  const summary = {
    days: WINDOW_DAYS,
    merged: mergedWindow.length,
    medianMergeMs: median(mergedWindow.map(pr => pr.durationMs).filter(value => Number.isFinite(value))),
    neededFix: mergedWindow.filter(pr => pr.neededFix).length,
    failedChecks: [...failed.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0])).slice(0, 5).map(([name, count]) => ({ name, count }))
  };
  const wanted = decorated.filter(pr => pr.state === state && (author === 'all' || (author === 'planwharf' ? pr.byPlanwharf : !pr.byPlanwharf)));
  wanted.sort((a, b) => newest(b) - newest(a) || b.number - a.number);
  return {
    ok: true, state, author, offset, page: PAGE, summary,
    items: wanted.slice(offset, offset + PAGE),
    more: wanted.length > offset + PAGE,
    fixing: fixing.map(item => ({ number: item.number, startedAt: item.startedAt || null, text: item.text || '' }))
  };
}

module.exports = { pullRequestPanel, PAGE, INSTALL, spawnRun, checkState };
