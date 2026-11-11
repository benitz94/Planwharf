'use strict';
// Chat-centred Planwharf view. The user is the captain: they set the goal and the team,
// the lead coordinates and the crew works. Everything here uses the existing backend API.
(() => {
  const $ = id => document.getElementById(id);
  const escape = value => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[ch]));
  const store = {
    get(key, fallback) { try { const value = localStorage.getItem('planwharf_chat_' + key); return value == null ? fallback : JSON.parse(value); } catch { return fallback; } },
    set(key, value) { try { localStorage.setItem('planwharf_chat_' + key, JSON.stringify(value)); } catch {} }
  };
  const state = {
    orchestra: null, plan: null, projectId: store.get('project', null), panel: store.get('panel', null),
    pending: {}, repo: {}, sessions: store.get('sessions', {}), threadSig: '', sideSig: '', confirm: null, loaded: false,
    updates: null, windowTitle: null, armed: null
  };

  // ---------- backend ----------
  async function getJson(url) {
    const response = await fetch(url, { cache: 'no-store' });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || body.reason || `Request failed (${response.status}).`);
    return body;
  }
  async function post(url, data) {
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(data ?? {}) });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || body.reason || `Request failed (${response.status}).`);
    return body;
  }
  async function refresh({ tick = true } = {}) {
    try {
      const [orchestra, plan] = await Promise.all([getJson((tick ? '/api/orchestrator' : '/api/orchestrator?readOnly=1') + (state.projectId ? (tick ? '?' : '&') + 'team=' + encodeURIComponent(state.projectId) : '')), getJson('/api/plan')]);
      state.orchestra = orchestra; state.plan = plan; state.loaded = true;
      const projects = orchestra.projects || [];
      if (!projects.some(row => row.id === state.projectId)) state.projectId = projects[0]?.id || null;
      render();
    } catch (error) {
      if (!state.loaded) { $('thread').innerHTML = `<div class="card"><h2>Planwharf is not reachable</h2><p>${escape(error.message)}</p><p class="muted">Start Planwharf again, then reload this page.</p></div>`; }
    }
  }

  // ---------- derived facts ----------
  const project = () => (state.orchestra?.projects || []).find(row => row.id === state.projectId) || null;
  const models = () => state.orchestra?.models || [];
  const lead = () => models().find(model => model.id === state.orchestra?.orchestratorId) || null;
  const autonomyOf = id => state.orchestra?.autonomy?.[id] || null;
  const quotaLeft = model => {
    const pool = (state.orchestra?.plans || []).find(plan => plan.id === (model.quotaPoolId || model.plan));
    return pool && pool.readable && Number.isFinite(pool.leftPct) ? pool.leftPct : null;
  };
  const builders = () => models().filter(model => model.roles?.worker && model.enabled && model.available !== false);
  const verifiers = () => models().filter(model => model.roles?.verifier && model.enabled && model.available !== false);
  const ROLE_KEYS = ['orchestrator', 'worker', 'verifier', 'reviewer', 'supervisor'];
  const onBench = model => !ROLE_KEYS.some(role => model.roles?.[role]);
  const NO_ROLES = Object.fromEntries(ROLE_KEYS.map(role => [role, false]));
  // One colour per provider, so the same company is recognisable everywhere.
  const PROVIDER_COLOURS = { codex: '#10a37f', openai: '#10a37f', claude: '#d97757', anthropic: '#d97757', grok: '#6b7280', xai: '#6b7280', antigravity: '#4285f4', gemini: '#4285f4', google: '#4285f4', api: '#8b5cf6' };
  const providerOf = id => models().find(model => model.id === id)?.provider || '';
  const providerDot = provider => `<span class="prov" style="background:${PROVIDER_COLOURS[String(provider).toLowerCase()] || '#9ca3af'}" title="${escape(provider || 'unknown provider')}" aria-hidden="true"></span>`;
  // Small line icons (24px grid, drawn with the current text colour).
  const ICONS = {
    plus: '<path d="M12 5v14M5 12h14"/>',
    team: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20c.8-3.6 3.3-5.5 6.5-5.5s5.7 1.9 6.5 5.5M16 4.6a3.5 3.5 0 0 1 0 6.8M18 14.8c2 .7 3.2 2.4 3.6 5.2"/>',
    progress: '<path d="M5 20v-6M12 20V5M19 20v-10"/>',
    usage: '<path d="M3.5 18a9 9 0 1 1 17 0"/><path d="M12 14l4-4"/>',
    file: '<path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/><path d="M14 3v5h5"/>',
    settings: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
    folder: '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/>',
    build: '<path d="M8 8l-4 4 4 4M16 8l4 4-4 4M13.5 5l-3 14"/>',
    verify: '<path d="M12 3l8 3v6c0 4.5-3.4 8-8 9-4.6-1-8-4.5-8-9V6z"/><path d="M8.5 12l2.5 2.5 4.5-5"/>',
    review: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
    watch: '<path d="M6 16v-5a6 6 0 0 1 12 0v5l2 2H4z"/><path d="M10 21h4"/>',
    lead: '<circle cx="12" cy="12" r="8.5"/><path d="M12 4.5v2M12 17.5v2M4.5 12h2M17.5 12h2"/><path d="M15.2 8.8 13 13l-4.2 2.2L11 11z"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    alert: '<path d="M12 3.5l9.5 17h-19z"/><path d="M12 10v4.5M12 17.5v.5"/>',
    play: '<path d="M7 5v14l12-7z"/>',
    stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
    pencil: '<path d="M4 20h4L19 9l-4-4L4 16z"/>',
    clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
    moon: '<path d="M20 14.5A8 8 0 1 1 9.5 4a6.5 6.5 0 0 0 10.5 10.5z"/>',
    box: '<path d="M4 8l8-4 8 4v8l-8 4-8-4z"/><path d="M4 8l8 4 8-4M12 12v8"/>',
    refresh: '<path d="M20 11a8 8 0 0 0-14.6-4.5L4 8M4 4v4h4M4 13a8 8 0 0 0 14.6 4.5L20 16M20 20v-4h-4"/>',
    trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3"/>',
    power: '<path d="M12 3v9"/><path d="M6.4 6.4a8 8 0 1 0 11.2 0"/>',
    plug: '<path d="M9 3v5M15 3v5M6 8h12v3a6 6 0 0 1-12 0zM12 17v4"/>',
    chip: '<rect x="7" y="7" width="10" height="10" rx="1.5"/><path d="M10 3v4M14 3v4M10 17v4M14 17v4M3 10h4M3 14h4M17 10h4M17 14h4"/>',
    rocket: '<path d="M9 15l-3-3c1.5-4 5-9 14-9 0 9-5 12.5-9 14z"/><path d="M5.5 14.5c-1 1-1.5 3.5-1.5 5.5 2 0 4.5-.5 5.5-1.5"/><circle cx="15" cy="9" r="1.5"/>',
    send: '<path d="M4 12l16-8-6 16-2.5-6.5z"/><path d="M11.5 13.5L20 4"/>',
    pause: '<path d="M9 5v14M15 5v14"/>',
    research: '<circle cx="11" cy="11" r="6.5"/><path d="M16 16l4.5 4.5"/>',
    copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V6a2 2 0 0 1 2-2h9"/>'
  };
  const ic = (name, cls = '') => `<svg class="i ${cls}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">${ICONS[name] || ''}</svg>`;
  const tile = (name, tone) => `<span class="tile ${tone}" aria-hidden="true">${ic(name)}</span>`;
  const head = (name, tone, text) => `<h3 class="head">${tile(name, tone)}<span>${text}</span></h3>`;
  // Each role has its own colour and icon, used the same way everywhere.
  const ROLE_LOOK = { Build: ['build', 'r-build'], Verify: ['verify', 'r-verify'], Review: ['review', 'r-review'], Research: ['research', 'r-review'], Watch: ['watch', 'r-watch'], Lead: ['lead', 'r-lead'] };
  const VERIFY_PREFIX = 'Verify the work independently';
  const duration = ms => !Number.isFinite(ms) || ms < 0 ? '' : ms < 60000 ? Math.max(1, Math.round(ms / 1000)) + ' s' : ms < 3600000 ? Math.round(ms / 60000) + ' min' : (ms / 3600000).toFixed(1) + ' h';
  const STOP_REASONS = {
    GOAL_REACHED: 'Goal reached', MEGATASK_LIMIT: 'Stopped: step limit reached', NO_LEAD_AVAILABLE: 'Stopped: no lead available',
    START_UNAVAILABLE: 'Stopped: could not start', TIME_LIMIT: 'Stopped: time limit reached', NOTHING_STARTED: 'Stopped: no work started', START_FAILED: 'Stopped: could not start'
  };
  function projectStatus(row) {
    if (!row) return { text: '', kind: '' };
    const auto = autonomyOf(row.id);
    if (auto?.enabled && auto.stopReason === 'GOAL_REACHED') return { text: 'Goal reached', kind: 'done' };
    if (auto?.enabled && auto.stopReason) return { text: STOP_REASONS[auto.stopReason] || 'Stopped: ' + auto.stopReason.split(':')[0].replace(/_/g, ' ').toLowerCase(), kind: 'attention' };
    if (auto?.enabled) return { text: 'Crew working', kind: 'running' };
    const status = String(row.status || 'idle');
    if (/running|watching|dispatch/.test(status)) return { text: 'Working', kind: 'running' };
    if (/blocked|failed|needs|relink|checkpoint/.test(status)) return { text: row.label || 'Needs you', kind: 'attention' };
    if (/complete/.test(status)) return { text: 'Done', kind: 'done' };
    return { text: 'Ready', kind: '' };
  }
  // Who is working right now on a project: a live crew process, or the lead answering in this window.
  function workingNow(row) {
    if (!row) return null;
    const part = (row.dispatch || []).find(item => item && item.alive);
    if (part) return { who: modelLabel(part.modelId) || part.label || row.workerLabel || 'The crew', since: Date.parse(part.startedAt || '') };
    if (state.pending[row.id]) return { who: lead()?.label || 'The lead', since: state.pending[row.id].since };
    return null;
  }
  const workingText = work => `${work.who} is working${Number.isFinite(work.since) ? ' · ' + (duration(Date.now() - work.since) || '1 s') : ''}`;
  const sessionFor = id => { if (!state.sessions[id]) { state.sessions[id] = 'chat-' + Math.random().toString(36).slice(2, 10); store.set('sessions', state.sessions); } return state.sessions[id]; };
  const modelLabel = id => models().find(model => model.id === id)?.label || null;

  // ---------- rendering ----------
  function render() {
    renderProjects();
    renderHeader();
    renderThread();
    renderActions();
    renderSide();
  }
  // A dot on a project you are not looking at, when something happened there since you last opened it:
  // a step finished, failed or blocked, the goal was reached, autonomy stopped, or the lead answered.
  const NEWS_EVENTS = /SUBTASK_(COMPLETED|FAILED|BLOCKED)|MEGATASK_(PROPOSED|COMPLETED|FAILED|BLOCKED|STOPPED)|GOAL|AUTONOMY/;
  function lastNews(row) {
    let latest = 0;
    for (const event of state.orchestra?.execution?.timeline || []) if (event.projectId === row.id && NEWS_EVENTS.test(event.type || '')) latest = Math.max(latest, Date.parse(event.createdAt || '') || 0);
    for (const item of state.orchestra?.asks || []) if (item.projectId === row.id) latest = Math.max(latest, Date.parse(item.at || '') || 0);
    return latest;
  }
  function hasNews(row) {
    const seen = state.seen || (state.seen = store.get('seen', {}));
    const latest = lastNews(row);
    // The open project is always read; a project seen for the first time starts read.
    if (row.id === state.projectId || !(row.id in seen)) {
      if (seen[row.id] !== Math.max(seen[row.id] || 0, latest)) { seen[row.id] = Math.max(seen[row.id] || 0, latest); store.set('seen', seen); }
      return false;
    }
    return latest > seen[row.id];
  }
  function renderProjects() {
    const rows = state.orchestra?.projects || [];
    $('project-list').innerHTML = rows.length ? rows.map(row => {
      const status = projectStatus(row), work = workingNow(row);
      const icon = work ? `<span class="tile p-running spinning" aria-hidden="true">${ic('lead')}</span>` : tile('folder', 'p-' + (status.kind || 'idle'));
      const news = hasNews(row) ? '<span class="news-dot" role="img" aria-label="New activity" title="Something happened here since you last looked"></span>' : '';
      return `<button type="button" class="project-item ${status.kind}" data-project="${escape(row.id)}" aria-current="${row.id === state.projectId}">${icon}<span class="name-wrap"><span class="name">${escape(row.name)}</span><span class="sub">${escape(work ? workingText(work) : status.text)}</span></span>${news}</button>`;
    }).join('') : '<p class="muted small">No projects yet.</p>';
    // The compass in the logo turns while anything is being worked on.
    const busy = rows.some(row => workingNow(row));
    document.querySelector('.brand-mark')?.classList.toggle('spinning', busy);
    document.querySelector('.brand')?.setAttribute('title', busy ? 'Planwharf is working' : 'Planwharf');
  }
  function renderHeader() {
    const row = project();
    $('project-title').textContent = row ? row.name : 'Welcome';
    const status = projectStatus(row), work = workingNow(row);
    const pill = $('project-status'); pill.textContent = work ? workingText(work) : status.text; pill.className = 'pill ' + (work ? 'running' : status.kind);
    document.title = state.windowTitle || (row ? `${row.name} · Planwharf` : 'Planwharf');
    document.querySelectorAll('.tab').forEach(tab => tab.setAttribute('aria-pressed', String(tab.dataset.panel === state.panel)));
  }

  function eventView(event) {
    const type = String(event.type || '');
    // Plain words: no internal states, no repeated titles, no long verify prefix.
    const summary = String(event.summary || '').replace(/\s+/g, ' ').trim()
      .replace(/Verify the work independently against the job:?\s*/gi, 'Check: ')
      .replace(/^(.+?): Finished \1\.?$/, 'Step done: $1')
      .replace(/\.\s*(COMMITTED|COMPLETE|COMPLETED)\.?$/, '.')
      .replace(/: (\d+) proposed slice\(s\)\.?$/, (_, count) => `: proposed in ${count} step${count === '1' ? '' : 's'}.`)
      .replace(/^Completed (.+)$/, 'Finished: $1')
      .replace(/^Approved (.+)$/, 'You approved: $1');
    const short = summary.length > 260 ? summary.slice(0, 257) + '…' : summary;
    if (/COMPLETED|REACHED|APPROVED/.test(type)) return { icon: 'check', kind: 'good', text: short };
    if (/FAILED|BLOCKED|STOPPED|REFUS/.test(type)) return { icon: 'alert', kind: 'bad', text: short };
    if (/STARTED/.test(type)) return { icon: 'play', kind: 'start', text: short };
    if (/PLAN_REVISION/.test(type)) return { icon: 'pencil', kind: 'plan', text: 'Plan updated: ' + short.replace(/^Living Plan R\d+:\s*/, '') };
    if (/PROPOSED|ASSIGNED|PLAN/.test(type)) return { icon: 'pencil', kind: 'plan', text: short };
    return { icon: 'clock', kind: '', text: short };
  }
  const SHOWN_EVENTS = /PLAN_REVISION|MEGATASK_(PROPOSED|APPROVED|ASSIGNED|COMPLETED|FAILED|STOPPED|BLOCKED)|SUBTASK_(STARTED|COMPLETED|FAILED|BLOCKED)|CHECKPOINT|GOAL|AUTONOMY/;
  const clock = at => { const date = new Date(at); return Number.isFinite(date.getTime()) ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''; };

  // The ship from ship.svg, drawn with the page colours so it fits the light and the dark theme.
  const SHIP = '<svg class="ship" viewBox="0 0 240 170" aria-hidden="true"><rect class="mast" x="117" y="8" width="7" height="112" rx="3"/><path class="flag" d="M124 10c8-3 14 4 22 1 3-1 6-3 8-5-2 6-5 10-10 12-7 3-13-3-20 0Z"/><path class="sail-back" d="M113 30c-16 18-29 38-38 60 12-3 25-4 38-4-4-18-5-37 0-56Z"/><path class="sail" d="M128 26c26 17 43 42 50 70-15-4-31-5-48-4 5-22 4-44-2-66Z"/><path class="hull" d="M66 104h22l9-12h20l9 12h70c-6 10-13 22-21 30H76c-5-8-9-18-10-30Z"/><path class="keel" d="M76 134h109c-3 5-7 9-11 12H86c-4-3-7-7-10-12Z"/><circle class="port" cx="112" cy="118" r="5"/><circle class="port" cx="132" cy="118" r="5"/><circle class="port" cx="152" cy="118" r="5"/><path class="wave-far" d="M14 136c16-9 34-11 52-4-17-3-35-1-52 4Z M180 132c16-8 32-8 48 0-16-3-32-3-48 0Z"/><path class="wave-near" d="M42 152c30-10 58-10 86-2s58 8 86-2c-26 16-58 18-86 10s-58-9-86-6Z"/></svg>';
  function welcomeCard() {
    const leads = models().filter(model => model.roleEligibility?.orchestrator && model.available !== false);
    const current = lead();
    const hasProject = (state.orchestra?.projects || []).length > 0;
    const step = (n, done, text) => `<li class="${done ? 'done' : ''}"><span class="num" aria-hidden="true">${done ? ic('check') : n}</span><span>${text}</span></li>`;
    return `<div class="card welcome">${SHIP}<h2 class="head">${tile('rocket', 't-accent')}<span>Welcome aboard, captain</span></h2>
      <p>Planwharf runs a team of AI models for you. You give the goal; a <b class="c-lead">lead</b> plans it, a <b class="c-build">builder</b> writes the code and a <b class="c-verify">verifier</b> checks and fixes it.</p>
      <ol class="steps numbered">
        ${step(1, Boolean(current), 'Choose your lead.')}
        ${step(2, hasProject, 'Create a project: a name and a folder.')}
        ${step(3, false, 'Talk with the lead about what to build. When the plan is right, press <b>Let the crew do it</b>.')}
      </ol>
      <div class="row"><label class="sr-only" for="welcome-lead">Lead</label><select id="welcome-lead" data-lead-select><option value="">Choose your lead…</option>${leads.map(model => `<option value="${escape(model.id)}" ${current?.id === model.id ? 'selected' : ''}>${escape(model.label)}</option>`).join('')}</select>
      ${hasProject ? '' : `<button type="button" class="btn btn-primary" data-action="new-project">${ic('plus')}New project</button>`}</div></div>`;
  }
  function startCard(row) {
    const missing = readiness(row);
    return `<div class="card"><h3 class="head">${tile('folder', 't-accent')}<span>${escape(row.name)}</span></h3><p>${hasGoal(row) ? escape(row.goal) : 'Write below what you want built. When the lead has understood, press <b>Use as the goal</b> under your message.'}</p>
      <p class="muted small path">Folder: ${escape(row.folder || 'not set')}</p>
      ${missing.length ? `<p class="small"><b>Before the crew can start:</b></p><div class="row">${missing.map(item => `<button type="button" class="btn btn-small" ${item.panel ? `data-panel="${item.panel}"` : `data-action="${item.action}"`}>${escape(item.fix)}</button>`).join('')}</div>` : '<p class="small">Everything is ready. Press <b>Let the crew do it</b> below, or tell the lead more first.</p>'}</div>`;
  }
  function readiness(row) {
    const missing = [];
    if (!lead()) missing.push({ text: 'Choose a lead in Team.', fix: 'Choose the lead', panel: 'team' });
    if (!builders().length) missing.push({ text: 'Give at least one model the Build role in Team.', fix: 'Pick a builder', panel: 'team' });
    if (!row?.folder) missing.push({ text: 'Set the project folder in Settings.', fix: 'Set the folder', panel: 'settings' });
    if (row && !hasGoal(row) && !state.goalCandidate) missing.push({ text: 'Talk with the lead about what to build first.', fix: 'Write what to build', action: 'focus-input' });
    return missing;
  }
  // A project created with only a name and a folder has no goal until the user gives one in the chat.
  const NO_GOAL = 'Not set yet: tell the lead in the chat what to build.';
  const hasGoal = row => Boolean(row?.goal && row.goal.trim() && row.goal !== NO_GOAL);
  async function useAsGoal(text) {
    const row = project(); if (!row || !text) return;
    await act(async () => {
      const { version } = await getJson('/api/plan');
      return post('/api/revisions', { version, projectId: row.id, draft: { title: 'Goal from the chat', direction: text, state: 'Not started.', closed: [], open: [] } });
    }, 'Goal saved. Press “Let the crew do it” to start.');
  }

  // The lead's suggestion becomes the goal and, at once, a work proposal with the crew: one approval starts it.
  async function proposeSuggestion(text) {
    const row = project(); if (!row || !text) return;
    await act(async () => {
      const { version } = await getJson('/api/plan');
      await post('/api/revisions', { version, projectId: row.id, draft: { title: 'Goal suggested by the lead', direction: text, state: 'Not started.', closed: [], open: [] } });
      const fresh = await getJson('/api/plan');
      return post('/api/orchestrator/start', { version: fresh.version, projectId: row.id, timing: 'discretion' });
    }, 'The lead is preparing the work and the crew. Approve it when it appears.');
  }

  // The current piece of work: proposal, live crew, or the result.
  // "Running tests (x3)" instead of three identical lines.
  function foldRepeats(lines) {
    const out = [];
    for (const line of lines) { const last = out.at(-1); if (last && last.text === line) last.count++; else out.push({ text: line, count: 1 }); }
    return out.map(item => item.count > 1 ? `${item.text} (x${item.count})` : item.text);
  }
  // What this piece of work used, per model: time, tokens and quota before -> after.
  // A value the provider does not report stays "not reported"; nothing is estimated.
  function rememberQuota(mega, subs) {
    const key = 'quotaStart:' + mega.id;
    const saved = store.get(key, null) || {};
    let changed = false;
    for (const id of new Set(subs.map(sub => sub.assignedModelId).filter(Boolean))) {
      if (Object.hasOwn(saved, id)) continue;
      const model = models().find(item => item.id === id);
      saved[id] = model ? quotaLeft(model) : null; changed = true;
    }
    if (changed) store.set(key, saved);
  }
  function usageSummary(mega, subs) {
    const records = (state.orchestra?.aiUsage?.recent || []).filter(item => item.megaTaskId === mega.id);
    const ids = [...new Set([...subs.map(sub => sub.assignedModelId), ...records.map(item => item.modelId)].filter(Boolean))];
    if (!ids.length) return '';
    const start = store.get('quotaStart:' + mega.id, null) || {};
    const num = value => value.toLocaleString();
    const rows = ids.map(id => {
      const mine = records.filter(item => item.modelId === id);
      const ms = mine.reduce((sum, item) => sum + (Number.isFinite(item.durationMs) ? item.durationMs : 0), 0);
      const tokens = mine.map(item => item.tokens).filter(Boolean);
      const add = field => tokens.length && tokens.every(item => Number.isFinite(item[field])) ? tokens.reduce((sum, item) => sum + item[field], 0) : null;
      const input = add('input'), output = add('output'), cached = add('cachedInput');
      const model = models().find(item => item.id === id);
      const now = model ? quotaLeft(model) : null, before = Object.hasOwn(start, id) ? start[id] : null;
      const quota = before != null && now != null ? before + '% → ' + now + '%' : now != null ? 'now ' + now + '% left' : 'not reported';
      const tokenText = input == null && output == null ? 'tokens not reported' : [input != null ? num(input) + ' in' : null, output != null ? num(output) + ' out' : null, cached != null ? num(cached) + ' from cache' : null].filter(Boolean).join(' · ');
      return `<li>${providerDot(providerOf(id))}<b>${escape(modelLabel(id) || id)}</b><span class="small muted"> · ${ms ? duration(ms) : 'time not reported'} · ${escape(tokenText)} · quota ${escape(quota)}</span></li>`;
    });
    return `<details class="usage-summary" open><summary class="small">${ic('usage')}What this work used</summary><ul class="plain">${rows.join('')}</ul></details>`;
  }
  // What each step reported, in its own words: the explanation of the finished work.
  function doneList(row) {
    const said = (row.results || []).filter(result => result.summary && result.summary.trim());
    return said.length ? `<h4 class="small">What was done</h4><ul class="plain small">${said.map(result => `<li>${escape(result.summary)}</li>`).join('')}</ul>` : '';
  }
  const ROLE_FOR_STEP = { Build: ['worker'], Verify: ['verifier', 'worker'], Review: ['reviewer'], Research: ['worker'] };
  function crewPicker(sub, role) {
    const keys = ROLE_FOR_STEP[role] || ['worker'];
    const list = models().filter(model => model.enabled && model.available !== false && keys.some(key => model.roles?.[key]));
    if (!list.some(model => model.id === sub.assignedModelId)) list.unshift(models().find(model => model.id === sub.assignedModelId) || { id: sub.assignedModelId, label: sub.assignedModelId || 'not assigned' });
    const option = model => { const left = quotaLeft(model); return `<option value="${escape(model.id)}" ${model.id === sub.assignedModelId ? 'selected' : ''}>${escape(model.label || model.id)}${left != null ? ` · ${left}% left` : ''}</option>`; };
    return `<select class="crew-pick" data-crew-step="${escape(sub.id)}" data-running="${String(sub.status).toUpperCase() === 'RUNNING'}" aria-label="Model for ${escape(role)}">${list.map(option).join('')}</select>`;
  }
  function crewCard(row) {
    const mega = row.megaTask;
    const subs = row.subTasks || [];
    if (!mega || !subs.length) return '';
    const status = String(mega.status || '').toUpperCase();
    // Cancelled work is not current work: show nothing rather than a crew that looks busy.
    if (status === 'CANCELLED') return '';
    if (subs.some(sub => String(sub.status).toUpperCase() === 'RUNNING')) rememberQuota(mega, subs);
    const done = subs.filter(sub => sub.status === 'COMPLETE').length;
    const pct = Math.round(done / subs.length * 100);
    // Open work can change the model of a step that is not done or working: pick from the models with that role.
    const editable = !['COMPLETE', 'CANCELLED'].includes(status);
    const step = sub => {
      const verify = String(sub.title || '').startsWith(VERIFY_PREFIX);
      const role = sub.type === 'AUDIT' ? 'Review' : sub.type === 'RESEARCH' ? 'Research' : verify ? 'Verify' : 'Build';
      const s = String(sub.status || 'PENDING').toUpperCase();
      const label = { PENDING: 'waiting', RUNNING: 'working', COMPLETE: 'done', FAILED: 'failed', BLOCKED: 'blocked', PAUSED: 'paused' }[s] || s.toLowerCase();
      const waitingQuota = s === 'PENDING' && sub.waitingFor;
      const title = verify ? 'Check the work and fix what fails' : String(sub.title || '').slice(0, 120);
      const [roleIcon, roleTone] = ROLE_LOOK[role] || ROLE_LOOK.Build;
      return `<li class="step ${s.toLowerCase()} ${roleTone}">${tile(roleIcon, roleTone)}<span class="step-main"><b class="role-name">${escape(role)}</b> · ${editable && s !== 'COMPLETE' ? crewPicker(sub, role) : `${providerDot(providerOf(sub.assignedModelId))}${escape(modelLabel(sub.assignedModelId) || sub.assignedModelId || 'not assigned')}`}<br><span class="muted small">${escape(title)}</span></span><span class="badge ${s.toLowerCase()}">${s === 'COMPLETE' ? ic('check') : ''}${escape(waitingQuota ? 'waiting for quota' : label)}</span></li>`;
    };
    const steps = `<ol class="crew-steps">${subs.map(step).join('')}</ol>`;
    const bar = `<div class="progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${pct}" aria-label="Steps done"><span style="width:${pct}%"></span></div><p class="small muted">${done} of ${subs.length} steps done</p>`;
    if (status === 'READY_FOR_APPROVAL') {
      const auto = autonomyOf(row.id)?.enabled;
      return `<div class="card crew proposal"><h3 class="head">${tile('lead', 'r-lead')}<span>The lead proposes this work</span></h3><p>${escape(mega.title || '')}</p>${steps}
        ${auto ? '<p class="small muted">The crew approves this by itself.</p>' : `<div class="row"><button type="button" class="btn btn-primary" data-action="approve">${ic('check')}Approve and start</button><span class="small muted">Or tell the lead what to change.</span></div>`}</div>`;
    }
    if (status === 'COMPLETE') {
      const took = duration(Date.parse(mega.completedAt) - Date.parse(mega.approvedAt || mega.createdAt));
      const repo = state.repo[row.id];
      if (!repo && row.folder) loadRepo(row.id);
      const changed = repo?.local?.git ? (Number.isFinite(repo.local.uncommitted) ? repo.local.uncommitted : (repo.local.changes || []).length) : null;
      return `<div class="card crew done"><h3 class="head">${tile('check', 't-good')}<span>Work finished</span></h3><p>${escape(mega.title || '')}</p>
        <div class="facts"><span class="fact">${ic('check')}<span><b>${done}/${subs.length}</b> steps done</span></span>${took ? `<span class="fact">${ic('clock')}<b>${took}</b></span>` : ''}<span class="fact">${ic('file')}<span>${changed == null ? (repo && !repo.loading && !repo.local ? 'Changed files: unknown' : 'Counting changed files…') : `<b>${changed}</b> files not yet committed`}</span></span></div>
        ${doneList(row)}${steps}${usageSummary(mega, subs)}<p class="small">Try the result in the project folder. Nothing was committed or published.</p>
        <div class="row"><button type="button" class="btn btn-small" data-panel="files">${ic('file')}See changed files</button><button type="button" class="btn btn-small" data-panel="usage">${ic('usage')}See usage</button></div></div>`;
    }
    if (['FAILED', 'BLOCKED', 'PAUSED', 'STOPPED', 'CHECKPOINTED'].includes(status) || row.canResume) {
      return `<div class="card crew attention"><h3 class="head">${tile('pause', 't-warn')}<span>Work stopped</span></h3><p>${escape(mega.nextAction || row.next || row.label || '')}</p>${bar}${steps}${usageSummary(mega, subs)}
        <div class="row">${row.canResume ? `<button type="button" class="btn btn-primary" data-action="resume">${ic('play')}Resume</button>` : ''}</div></div>`;
    }
    const since = duration(Date.now() - Date.parse(mega.approvedAt || mega.createdAt));
    // Plain-language activity of the model working now; the raw log only when nothing else is readable.
    const running = (row.dispatch || []).find(part => part && part.alive) || null;
    const friendly = foldRepeats((running?.activitySession?.events || []).map(event => event.friendly).filter(text => text && !/^Unclassified worker activity/.test(text))).slice(-6);
    const tail = friendly.length ? friendly.join('\n') : String(row.workerTail || '').trim().split('\n').slice(-6).join('\n');
    const who = modelLabel(running?.modelId) || row.workerLabel || 'the crew';
    const waiting = subs.find(sub => String(sub.status).toUpperCase() === 'PENDING' && sub.waitingFor)?.waitingFor || '';
    return `<div class="card crew live"><h3 class="head"><span class="tile t-accent live-tile" aria-hidden="true"><span class="pulse"></span></span><span>Crew at work${since ? ` · ${since}` : ''}</span></h3><p>${escape(mega.title || '')}</p>${bar}${waiting ? `<p class="small">${ic('clock')}${escape(waiting)}</p>` : ''}${steps}
      ${tail ? `<details class="preview" open><summary>What ${escape(who)} is doing</summary><pre>${escape(tail)}</pre></details>` : ''}</div>`;
  }

  // Deterministic warnings from the Supervisor checks (no model call): stalled, failed or conflicting work.
  const alertsFor = id => (state.orchestra?.supervisor?.projects || []).find(entry => entry.projectId === id)?.alerts?.filter(alert => alert.active !== false && !alert.ignored) || [];
  function alertsCard(row) {
    const alerts = alertsFor(row.id);
    if (!alerts.length) return '';
    return `<div class="card crew attention" role="alert"><h3 class="head">${tile('alert', 't-warn')}<span>Something needs a look</span></h3>${alerts.map(alert => `<div class="alert-row"><p><b>${escape(alert.title)}</b><br><span class="small">${escape(alert.message || '')}</span></p><button type="button" class="btn btn-small" data-ignore-alert="${escape(alert.id)}">Ignore</button></div>`).join('')}
      <p class="small muted">Planwharf checks the work itself; this is a reason to look, not proof that something broke.</p></div>`;
  }

  function renderThread() {
    const thread = $('thread');
    const row = project();
    const asks = (state.orchestra?.asks || []).filter(item => row ? item.projectId === row.id : item.projectId == null);
    const events = row ? (state.orchestra?.execution?.timeline || []).filter(event => event.projectId === row.id && SHOWN_EVENTS.test(event.type || '')).slice(-60) : [];
    // Each project has its own message waiting for the lead; switching projects never cancels one.
    const pendingHere = state.pending[state.projectId] || null;
    const live = pendingHere ? state.orchestra?.thinkingByProject?.[state.projectId] || null : null;
    $('send').disabled = Boolean(pendingHere);
    const sig = JSON.stringify([state.projectId, asks.map(item => [item.at, item.answer?.length, item.ms]), events.map(event => event.id || event.createdAt), pendingHere, pendingHere && [live?.thinking?.length, live?.draft?.length], state.confirm, Boolean(lead()), row?.megaTask?.status, (row?.subTasks || []).map(sub => sub.status + sub.assignedModelId).join(), row?.workerTail?.length, (row?.dispatch || []).map(part => part?.activitySession?.events?.length).join(), row?.canResume, Math.floor(Date.now() / 60000), row && state.repo[row.id]?.local?.uncommitted, row && alertsFor(row.id).map(alert => alert.id).join(), models().map(model => model.roles?.worker).join(), row?.folder, row?.goal, (state.orchestra?.aiUsage?.recent || []).length, timedStop()?.at, row && autonomyOf(row.id)?.enabled, row?.canStop, (state.orchestra?.projects || []).length, projectStatus(row).text, state.orchestra?.projectTools?.[state.projectId]]);
    if (sig === state.threadSig) return;
    state.threadSig = sig;
    const nearBottom = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 120;
    const items = [
      ...asks.map(item => ({ at: Date.parse(item.at) || 0, html: askHtml(item) })),
      ...events.map(event => ({ at: Date.parse(event.createdAt) || 0, html: eventHtml(event) }))
    ];
    // Finished work is history: it takes its place in the conversation instead of staying at the bottom.
    const finished = row && String(row.megaTask?.status || '').toUpperCase() === 'COMPLETE' ? crewCard(row) : '';
    if (finished) items.push({ at: Date.parse(row.megaTask.completedAt) || 0, html: finished });
    items.sort((a, b) => a.at - b.at);
    let html = '';
    if (!row) html = welcomeCard();
    else if (!asks.length) html = (lead() ? '' : welcomeCard()) + startCard(row);
    html += items.map(item => item.html).join('');
    // A chat message never starts work by itself; say so under the last one and offer the next step.
    const lastAsk = asks.at(-1);
    const busy = row && (row.canStop || autonomyOf(row.id)?.enabled && !autonomyOf(row.id)?.stopReason || String(row.megaTask?.status || '').toUpperCase() === 'READY_FOR_APPROVAL');
    // The plan agreed in the chat is what "Let the crew do it" starts: the lead's suggested goal, or else the last message.
    state.goalCandidate = null; state.suggestedGoal = null;
    const suggested = String(lastAsk?.answer || '').match(/^\s*\**Suggested next goal:\**\s*(.+)$/im)?.[1]?.trim() || null;
    const nextGoal = suggested || lastAsk?.question?.trim() || '';
    if (row && lastAsk && !busy && !pendingHere && nextGoal && nextGoal !== row.goal?.trim() && lastAsk.question?.trim() !== row.goal?.trim()) {
      state.goalCandidate = lastAsk.question; state.suggestedGoal = suggested;
      html += `<div class="card hint"><p class="small">${ic('alert')}Nothing has started yet. When the plan is right, press <b>Let the crew do it</b>: the crew will work on</p><p class="small"><b>${escape(nextGoal)}</b></p><p class="small muted">Not right yet? Keep talking with the lead.</p></div>`;
    }
    if (row) html += (finished ? '' : crewCard(row)) + alertsCard(row);
    if (pendingHere) html += `<div class="msg you"><div class="bubble">${escape(pendingHere.text)}</div></div><div class="msg lead"><span class="who">${escape(lead()?.label || 'Lead')}</span><div class="bubble">${live?.draft ? escape(live.draft) : '<span class="dots">Thinking</span>'}</div>${live?.thinking ? `<details class="thinking" data-live-thinking${state.thinkingOpen ? ' open' : ''}><summary>What the lead is doing</summary><pre>${escape(live.thinking.slice(-4000))}</pre></details>` : ''}</div>`;
    if (state.confirm) html += `<div class="card"><p>${escape(state.confirm.text)}</p><div class="row"><button type="button" class="btn btn-primary" data-action="confirm-yes">Yes, do it</button><button type="button" class="btn" data-action="confirm-no">No</button></div></div>`;
    thread.innerHTML = html;
    if (nearBottom || pendingHere) thread.scrollTop = thread.scrollHeight;
  }
  function askHtml(item) {
    const failed = Boolean(item.diagnostic);
    const elapsed = Number.isFinite(item.ms) ? ` · ${Math.max(1, Math.round(item.ms / 1000))} s` : '';
    return `<div class="msg you"><div class="bubble">${escape(item.question)}</div></div>
      <div class="msg lead${failed ? ' error' : ''}"><span class="who">${escape(modelLabel(item.orchestratorId) || 'Lead')} · ${escape(clock(item.at))}${elapsed}</span><div class="bubble">${escape(item.answer || '')}</div>${item.answer ? `<button type="button" class="copy-msg" data-action="copy-msg" title="Copy this reply" aria-label="Copy this reply">${ic('copy')}<span>Copy</span></button>` : ''}${toolAsk(item)}${item.thinking ? `<details class="thinking"><summary>How the lead got there</summary><pre>${escape(String(item.thinking).slice(-4000))}</pre></details>` : ''}</div>`;
  }
  function eventHtml(event) {
    const view = eventView(event);
    return `<div class="event ${view.kind}"><span class="icon" aria-hidden="true">${ic(view.icon)}</span><span>${escape(view.text)}</span><time>${escape(clock(event.createdAt))}</time></div>`;
  }

  function renderActions() {
    const bar = $('action-bar');
    const row = project();
    if (!row) { bar.innerHTML = ''; return; }
    const auto = autonomyOf(row.id);
    const working = auto?.enabled && !auto.stopReason;
    const missing = readiness(row);
    const parts = [];
    const timed = timedStop();
    if (timed?.projectId === row.id) parts.push(`<span class="timed-stop">${ic('clock')}<span>Stops at ${escape(clock(timed.at))} (in ${Math.max(1, Math.ceil((timed.at - Date.now()) / 60000))} min)</span><button type="button" class="btn btn-small" data-action="cancel-timed-stop">Cancel</button></span>`);
    if (working) parts.push(`<button type="button" class="btn btn-danger" data-action="autonomy-off">${ic('stop')}Stop the crew</button><span class="status-text">${escape((row.subTasks || []).some(sub => sub.status === 'PENDING' && sub.waitingFor) ? 'Waiting for quota: the crew starts again by itself. You can change models in Team or in a step\'s menu at any time.' : 'The crew is working. You can keep chatting with the lead and change models at any time.')}</span>`);
    // One clear next action at a time.
    else if (row.status === 'needs_approval') parts.push(`<button type="button" class="btn btn-primary" data-action="approve">${ic('check')}Approve and start</button><span class="status-text">The lead proposed some work. Approve it, or tell the lead what to change.</span>`);
    else if (row.canStop) parts.push(`<button type="button" class="btn btn-danger" data-action="stop">${ic('stop')}Stop work</button><span class="status-text">Work in progress. You can keep chatting with the lead.</span>`);
    else if (row.canResume) parts.push(`<button type="button" class="btn btn-primary" data-action="resume">${ic('play')}Resume</button><span class="status-text">Work was stopped. Resume continues from the saved point.</span>`);
    else {
      // Right after start Planwharf shows saved data while it checks the AI tools again.
      const checking = state.orchestra?.modelsChecking === true;
      parts.push(`<button type="button" class="btn btn-primary" data-action="autonomy-on" ${missing.length || checking ? 'disabled' : ''} title="${escape(missing.map(item => item.text).join(' ') || 'Plan, build, verify and stop when the goal is reached')}">${ic('rocket')}Let the crew do it</button>`);
      // Why the last crew start stopped, in full, so it is never silent.
      const why = auto?.stopReason && auto.stopReason !== 'GOAL_REACHED' ? auto.stopReason.replace(/^[A-Z_]+:\s*/, '') : '';
      const whyText = why && why !== auto.stopReason ? `The crew stopped: ${why}` : auto?.stopReason && auto.stopReason !== 'GOAL_REACHED' ? (STOP_REASONS[auto.stopReason] || auto.stopReason) : '';
      parts.push(`<span class="status-text">${checking ? '<span class="dots">Checking your AI tools</span>' : escape(missing.length ? missing[0].text : whyText || 'Ready. The crew will plan, build, check and stop when the goal is reached.')}</span>`);
    }
    bar.innerHTML = parts.join('');
  }

  // ---------- side panels ----------
  function renderSide() {
    const side = $('side-panel');
    if (!state.panel) { side.hidden = true; state.sideSig = ''; return; }
    side.hidden = false;
    const body = ({ team: teamPanel, progress: progressPanel, usage: usagePanel, files: filesPanel, settings: settingsPanel }[state.panel] || teamPanel)();
    if (body === state.sideSig) return;
    // Never wipe what the user is typing or an open section; an action forces the redraw.
    const sideBody = $('side-body');
    const busy = sideBody.contains(document.activeElement) && /^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName) || sideBody.querySelector('form[data-dirty], details[open]');
    if (state.sideSig && busy && state.sideRendered === state.panel) return;
    state.sideRendered = state.panel;
    state.sideSig = body;
    const scroll = $('side-body').scrollTop;
    $('side-title').textContent = { team: 'Team', progress: 'Progress', usage: 'Usage', files: 'Changed files', settings: 'Settings' }[state.panel] || 'Team';
    $('side-body').innerHTML = body;
    $('side-body').scrollTop = scroll;
  }
  function memberHtml(model) {
    const left = quotaLeft(model);
    const unavailable = model.available === false;
    const chip = (role, label, help) => {
      const eligible = model.roleEligibility?.[role] !== false;
      const [icon, tone] = ROLE_LOOK[label] || ROLE_LOOK.Build;
      return `<button type="button" class="chip ${tone}" data-role-toggle="${escape(model.id)}" data-role="${role}" aria-pressed="${model.roles?.[role] === true}" ${eligible ? '' : 'disabled'} title="${escape(help)}">${ic(icon)}${label}</button>`;
    };
    const optional = ['reviewer'].filter(role => model.roleEligibility?.[role] !== false);
    const benched = onBench(model);
    // Name and details on their own full-width line, role buttons on the line below.
    return `<div class="member ${benched ? 'benched' : 'on-team'}"><div class="info"><div class="label">${providerDot(model.provider)}<span class="name">${escape(model.label)}</span>${model.id === state.orchestra?.orchestratorId ? ' <span class="badge lead-badge">Lead</span>' : ''}</div>
      <div class="meta">${escape(model.plan || model.provider || '')}${unavailable ? ' · not available now' : ''}${left != null ? ` · ${left}% quota left` : ''}</div>
      ${left != null && !benched ? `<div class="bar${left < 20 ? ' low' : ''}"><span style="width:${Math.max(0, Math.min(100, left))}%"></span></div>` : ''}</div>
      <div class="chips">${benched ? '<span class="small muted bring">Bring in as:</span>' : ''}${chip('worker', 'Build', 'Writes the code and the tests')}${chip('verifier', 'Verify', 'Checks the work and fixes what fails')}${optional.includes('reviewer') ? chip('reviewer', 'Review', 'Optional: reads the work and reports problems, without changing files') : ''}
      ${onBench(model) ? '' : `<button type="button" class="chip" data-bench="${escape(model.id)}" title="Take this model out of the team">Bench</button>`}</div></div>`;
  }
  function teamPanel() {
    const all = models();
    const current = lead();
    const leads = all.filter(model => model.roleEligibility?.orchestrator && model.available !== false);
    const active = all.filter(model => !onBench(model));
    const bench = all.filter(onBench);
    const row = project();
    return `${row ? `<p class="team-scope">${ic('folder')}<span>Team for <b>${escape(row.name)}</b>. Each project has its own team; changes here apply only to this project.</span></p>` : ''}
      <div class="section">${head('lead', 'r-lead', 'Lead')}<p class="explain">Talks with you, plans the work and picks who does what.</p>
        <select data-lead-select aria-label="Lead"><option value="">Choose your lead…</option>${leads.map(model => `<option value="${escape(model.id)}" ${current?.id === model.id ? 'selected' : ''}>${escape(model.label)}</option>`).join('')}</select></div>
      <div class="section team-box">${head('team', 't-accent', `On the team · ${active.length}`)}<p class="explain"><b class="c-build">Build</b> writes code and tests. <b class="c-verify">Verify</b> checks the work and fixes it, ideally a model from another company. <b class="c-review">Review</b> is optional. Planwharf always watches running work by itself: if something stalls or fails, the lead is told.</p>
        ${active.length ? active.map(memberHtml).join('') : '<p class="muted small">Nobody yet. Bring models in from the bench.</p>'}</div>
      <div class="section bench-box">${head('pause', 't-muted', `Bench · ${bench.length}`)}<p class="explain">Not working on this project. Press a role to bring a model in.</p>
        ${bench.length ? `<details class="bench-list" ${bench.length <= 4 ? 'open' : ''}><summary class="small">${bench.length <= 4 ? 'Models on the bench' : `Show the ${bench.length} models on the bench`}</summary>${bench.map(memberHtml).join('')}</details>` : '<p class="muted small">The bench is empty.</p>'}</div>
      <div class="section">${head('chip', 't-blue', 'AI tools')}<p class="explain">Planwharf finds the models of the AI tools signed in on this computer. A tool that is off is never used.</p>
        ${(state.orchestra?.providers || []).map(provider => `<div class="member"><div class="info"><div class="label">${providerDot(provider.id)}${escape(provider.label || provider.id)}</div><div class="meta">${(provider.models || []).length} models</div></div>
          <div class="chips"><button type="button" class="chip onoff" data-provider-toggle="${escape(provider.id)}" aria-pressed="${provider.enabled !== false}">${provider.enabled !== false ? 'On' : 'Off'}</button></div></div>`).join('')}
        <button type="button" class="btn btn-small" data-action="refresh-models">${ic('refresh')}Look for models again</button></div>
      <div class="section">${head('plug', 't-violet', 'Connect a model by API')}<p class="explain">For a model you reach through an API, such as DeepSeek or Kimi. The key stays in an environment variable on this computer; Planwharf stores only the variable's name.</p>
        <details><summary class="small">Add an API connection</summary><form id="api-form" class="stack">
          <label class="small" for="api-label">Name</label><input id="api-label" name="label" required maxlength="100" placeholder="DeepSeek">
          <label class="small" for="api-url">API address</label><input id="api-url" name="baseUrl" required placeholder="https://api.deepseek.com/v1">
          <label class="small" for="api-protocol">Type</label><select id="api-protocol" name="apiProtocol"><option value="openai">OpenAI-compatible</option><option value="anthropic">Anthropic Messages</option></select>
          <label class="small" for="api-env">Environment variable with the key</label><input id="api-env" name="authEnv" placeholder="DEEPSEEK_API_KEY" pattern="[A-Za-z_][A-Za-z0-9_]*">
          <label class="small" for="api-models">Exact model IDs, separated by commas</label><input id="api-models" name="modelIds" required placeholder="deepseek-chat">
          <label class="small"><input type="checkbox" name="toolsEnabled"> These models can use tools (needed to build code)</label>
          <p class="error" id="api-error" role="alert"></p><button type="submit" class="btn btn-small">Connect</button></form></details></div>`;
  }
  function progressPanel() {
    const row = project();
    if (!row) return '<p class="muted">Select a project.</p>';
    const status = projectStatus(row);
    const subs = row.subTasks || row.megaTask?.subTasks || [];
    const label = id => models().find(model => model.id === id)?.label || id || 'not assigned';
    const events = (state.orchestra?.execution?.timeline || []).filter(event => event.projectId === row.id).slice(-15).reverse();
    return `<div class="section">${head('play', 't-accent', 'Now')}<p><span class="pill ${status.kind}">${escape(status.text)}</span></p>${row.next ? `<p class="small">${escape(row.next)}</p>` : ''}</div>
      ${subs.length ? `<div class="section">${head('progress', 't-blue', 'Steps')}<ul class="status-list">${subs.map(sub => `<li><span class="badge ${escape(String(sub.status || '').toLowerCase())}">${escape(String(sub.status || 'planned').toLowerCase())}</span><span>${escape(String(sub.title || '').slice(0, 140))}<br><span class="muted small">${escape(label(sub.assignedModelId || sub.modelId))}</span></span></li>`).join('')}</ul></div>` : ''}
      <div class="section">${head('clock', 't-violet', 'Recent activity')}${events.length ? `<ul class="status-list">${events.map(event => `<li><span class="muted small">${escape(clock(event.createdAt))}</span><span class="small">${escape(String(event.summary || '').slice(0, 200))}</span></li>`).join('')}</ul>` : '<p class="muted small">Nothing yet.</p>'}</div>
      <p class="small muted path">Folder: ${escape(row.folder || 'not set')}</p>`;
  }
  function usagePanel() {
    const plans = state.orchestra?.plans || [];
    const usage = state.orchestra?.aiUsage || {};
    const seconds = ms => Number.isFinite(ms) ? (ms >= 60000 ? Math.round(ms / 60000) + ' min' : Math.round(ms / 1000) + ' s') : '—';
    const tokens = row => Number.isFinite(row.outputTokens) ? `${row.outputTokens.toLocaleString()} out` : 'not reported';
    const rows = list => (list || []).map(row => `<span>${escape(row.model)}</span><span class="v">${row.calls} · ${seconds(row.durationMs)} · ${escape(tokens(row))}</span>`).join('');
    // Each limit the provider reports (5-hour, weekly, ...) gets its own bar and reset time.
    const windowName = name => ({ '5-hour': '5 hours', weekly: 'Week' })[name] || String(name || 'Limit').replace(/[_-]+/g, ' ').replace(/^./, c => c.toUpperCase());
    const resetText = at => {
      const time = Date.parse(at || '');
      if (!Number.isFinite(time) || time <= Date.now()) return '';
      const date = new Date(time), sameDay = date.toDateString() === new Date().toDateString();
      return ' · resets ' + (sameDay ? '' : date.toLocaleDateString([], { weekday: 'short', day: 'numeric', month: 'short' }) + ' ') + date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    };
    const bar = left => `<div class="bar${left < 20 ? ' low' : ''}"><span style="width:${Math.max(0, Math.min(100, left))}%"></span></div>`;
    const limits = plan => {
      const windows = (plan.windows || []).filter(w => Number.isFinite(w?.leftPct));
      if (!plan.readable || (!windows.length && !Number.isFinite(plan.leftPct))) return `<div class="meta">${escape(plan.reason || 'Not reported by this provider')}</div>`;
      if (!windows.length) return `<div class="meta">${plan.leftPct}% left</div>${bar(plan.leftPct)}`;
      return windows.map(w => `<div class="meta"><b>${escape(windowName(w.name))}</b>: ${w.leftPct}% left${escape(resetText(w.resetAt))}</div>${bar(w.leftPct)}`).join('');
    };
    return `<div class="section">${head('usage', 't-accent', 'Quota left')}${plans.length ? plans.map(plan => `<div class="member"><div class="info"><div class="label">${escape(plan.label)}</div>${limits(plan)}</div></div>`).join('') : '<p class="muted small">No provider reports quota.</p>'}</div>
      <div class="section">${head('lead', 'r-lead', 'Lead calls')}<div class="kv">${rows(usage.orchestrator) || '<span class="muted small">None yet.</span>'}</div></div>
      <div class="section">${head('build', 'r-build', 'Crew work')}<div class="kv">${rows(usage.workers) || '<span class="muted small">None yet.</span>'}</div></div>
      <p class="small muted">Calls · time · output tokens. A provider that does not report tokens shows “not reported”, never a guess.</p>`;
  }

  const CHANGE_KIND = { M: 'changed', A: 'added', D: 'deleted', R: 'renamed', '??': 'new', U: 'conflict' };
  function filesPanel() {
    const row = project();
    if (!row) return '<p class="muted">Select a project.</p>';
    if (!row.folder) return '<p class="muted">This project has no folder yet. Set it in Settings.</p>';
    const entry = state.repo[row.id];
    if (!entry) { loadRepo(row.id); return '<p class="muted"><span class="dots">Reading the folder</span></p>'; }
    if (entry.error) return `<p class="error">${escape(entry.error)}</p><button type="button" class="btn btn-small" data-action="repo-refresh">Try again</button>`;
    const local = entry.local || {};
    if (!local.exists) return `<p>The folder <b>${escape(row.folder)}</b> does not exist.</p>`;
    if (!local.git) return `<p class="muted">${escape(row.folder)}</p><p>This folder is not tracked by Git, so Planwharf cannot list what changed.</p>`;
    const changes = local.changes || [];
    const list = changes.map(line => {
      const code = line.slice(0, 2).trim() || '?';
      const kind = CHANGE_KIND[code] || CHANGE_KIND[code[0]] || 'changed';
      return `<li><span class="badge">${escape(kind)}</span><span class="small" style="overflow-wrap:anywhere">${escape(line.slice(3))}</span></li>`;
    }).join('');
    return `<div class="section">${head('file', 't-blue', 'Not yet saved in Git')}<p class="explain">Files the crew or you changed since the last commit on <b>${escape(local.branch || 'the current branch')}</b>.</p>
      ${changes.length ? `<ul class="status-list">${list}</ul>` : '<p class="small">Nothing changed. The folder matches the last commit.</p>'}
      ${Number.isFinite(local.uncommitted) && local.uncommitted > changes.length ? `<p class="small muted">Showing ${changes.length} of ${local.uncommitted}.</p>` : ''}</div>
      <p class="small muted path">${escape(row.folder)}${entry.checkedAt ? ' · checked ' + escape(clock(entry.checkedAt)) : ''}</p>
      <button type="button" class="btn btn-small" data-action="repo-refresh">${ic('refresh')}Check again</button>`;
  }
  async function loadRepo(id, force = false) {
    if (state.repo[id]?.loading) return;
    state.repo[id] = { loading: true, ...(state.repo[id] || {}) };
    try {
      const result = await getJson('/api/repository?projectId=' + encodeURIComponent(id) + (force ? '&refresh=1' : ''));
      state.repo[id] = { ...result.repository };
    } catch (error) { state.repo[id] = { error: error.message }; }
    state.sideSig = ''; renderSide(); renderThread();
  }
  // The GitHub link is always optional. With none saved, the folder's git origin is offered once;
  // saving the field empty removes the link and stops the offer for that project.
  function githubOffer(row) {
    if (!row || row.github || !row.folder) return '';
    const repo = state.repo[row.id];
    if (!repo) { loadRepo(row.id); return ''; }
    const origin = repo.local?.originRepository;
    if (!origin || (store.get('githubDeclined', {})[row.id])) return '';
    return 'https://github.com/' + origin;
  }
  // Extra tools for this project's team, all off by default. Claude follows all four; Codex follows MCP; Grok uses its own settings.
  const TOOLS = [
    ['mcp', 'MCP servers', 'Your own MCP servers, as set up in Claude Code and Codex. They can act outside the project folder, and a server that controls the computer can move your mouse and type.'],
    ['browser', 'Browser', 'Claude in Chrome: open and try web pages in a browser tab. It does not move your mouse.'],
    ['hooks', 'Hooks', 'Your Claude Code hooks: commands that run by themselves before or after the model acts.'],
    ['memory', 'Memory', 'Notes Claude keeps for itself from one session to the next.']
  ];
  // The lead asked to turn on a tool: one button, and only a click from you turns it on.
  function toolAsk(item) {
    const tool = TOOLS.find(([name]) => name === item.toolRequest);
    if (!tool || !item.projectId || state.orchestra?.projectTools?.[item.projectId]?.[tool[0]]) return '';
    return `<div class="tool-ask">${tool[0] === 'mcp' ? `<p class="explain warn">${escape(tool[2])}</p>` : ''}<button type="button" class="btn btn-small" data-action="tool-on" data-tool="${tool[0]}">Turn on ${escape(tool[1])} for this project</button></div>`;
  }
  function toolsSection(row) {
    const on = state.orchestra?.projectTools?.[row.id] || {};
    return `<div class="section">${head('box', 't-violet', 'Tools')}<p class="explain">Extra tools for the team of ${escape(row.name)}. All off by default. Claude follows all four, Codex follows MCP, Grok uses its own settings. Subagents stay off: the lead splits the work.</p>
      ${TOOLS.map(([name, label, explain]) => state.armed === 'tool-' + name
        ? `<div class="stack"><p class="explain warn">${escape(explain)} Turn it on for this project?</p><div class="row"><button type="button" class="btn btn-danger btn-small" data-action="tool-on" data-tool="${name}">Yes, turn on ${escape(label)}</button><button type="button" class="btn btn-small" data-action="disarm">Cancel</button></div></div>`
        : `<button type="button" class="switch" role="switch" ${on[name] ? 'data-action="tool-off"' : name === 'mcp' ? 'data-arm="tool-mcp"' : 'data-action="tool-on"'} data-tool="${name}" aria-checked="${Boolean(on[name])}" title="${escape(explain)}"><span class="knob" aria-hidden="true"></span><span>${escape(label)}</span></button>`).join('')}</div>`;
  }
  async function setTool(name, value) {
    const row = project(); if (!row) return;
    state.armed = null;
    await act(async () => { const result = await post('/api/project-tools', { projectId: row.id, tools: { [name]: value } }); state.orchestra.projectTools = { ...(state.orchestra.projectTools || {}), [row.id]: result.tools }; state.sideSig = ''; return result.snapshot; }, value ? `${name === 'mcp' ? 'MCP servers' : name[0].toUpperCase() + name.slice(1)} on for ${row.name}.` : 'Turned off.');
  }
  // Skills and instructions each program reads from your own folders. Planwharf lists and opens them; you edit them.
  function skillsSection() {
    if (!state.skills) { if (!state.skillsLoading) { state.skillsLoading = true; getJson('/api/skills').then(value => { state.skills = value.programs || []; }).catch(() => { state.skills = []; }).finally(() => { state.skillsLoading = false; state.sideSig = ''; renderSide(); }); } return ''; }
    if (!state.skills.length) return '';
    return `<div class="section">${head('file', 't-accent', 'Skills and instructions')}<p class="explain">Skills are instructions a model reads when it needs them. The team uses the ones you have in each program. Add or change them there; Planwharf only shows and opens them.</p>
      ${state.skills.map(program => `<details class="skills"><summary><strong>${escape(program.label)}</strong> · ${program.skills.length} skill${program.skills.length === 1 ? '' : 's'}</summary>
        ${program.skills.length ? `<ul class="skill-list">${program.skills.map(skill => `<li><strong>${escape(skill.name)}</strong>${skill.source !== 'yours' ? ` <span class="muted small">(${escape(skill.source)})</span>` : ''}${skill.description ? `<br><span class="muted small">${escape(skill.description)}</span>` : ''}</li>`).join('')}</ul>` : `<p class="explain">No skills${program.skillsFolder ? ' yet' : ' folder for this program'}.</p>`}
        <div class="row">${program.skillsFolder ? `<button type="button" class="btn btn-small" data-action="skills-open" data-cli="${program.cli}" data-what="skills">${ic('folder')}Open skills folder</button>` : ''}${program.instructions ? `<button type="button" class="btn btn-small" data-action="skills-open" data-cli="${program.cli}" data-what="instructions">${ic('file')}Edit instructions</button>` : ''}${program.settingsExist ? `<button type="button" class="btn btn-small" data-action="skills-open" data-cli="${program.cli}" data-what="settings">${ic('file')}Open settings file</button>` : ''}</div></details>`).join('')}
      <button type="button" class="btn btn-small" data-action="skills-refresh">${ic('refresh')}Read again</button></div>`;
  }
  function settingsPanel() {
    const row = project();
    const offered = githubOffer(row);
    const dark = currentTheme() === 'dark';
    const mode = state.orchestra?.mode;
    return `${row ? `<div class="section">${head('folder', 't-accent', 'This project')}<form id="folder-form" class="stack"><label class="small" for="folder-input">Folder on your computer</label>
        <input id="folder-input" name="folder" value="${escape(row.folder || '')}" placeholder="C:\\Projects\\my-website" maxlength="1000">
        <label class="small" for="github-input">GitHub repository (optional)</label>
        <input id="github-input" name="github" value="${escape(row.github || offered)}" placeholder="https://github.com/you/my-website" maxlength="500">
        ${offered ? `<p class="explain">Filled in from this folder's git settings. Save to link it, or empty the field and save if you do not want a link.</p>` : ''}
        <p class="error" id="folder-error" role="alert"></p><button type="submit" class="btn btn-small">${ic('folder')}Save</button></form>
        ${row.github ? `<p class="small"><a href="${escape(row.github)}" target="_blank" rel="noopener noreferrer">Open on GitHub</a></p>` : ''}</div>` : ''}
      ${row ? toolsSection(row) : ''}
      <div class="section">${head('plus', 't-blue', 'New projects')}<form id="root-form" class="stack"><label class="small" for="root-input">Default folder for new projects</label>
        <input id="root-input" name="root" value="${escape(state.settings?.projectsRoot || '')}" placeholder="C:\\Projects" maxlength="1000">
        <p class="explain">When you leave the folder empty on a new project, Planwharf creates it here.</p>
        <p class="error" id="root-error" role="alert"></p><button type="submit" class="btn btn-small">Save</button></form></div>
      <div class="section">${head('verify', 'r-verify', 'Approvals')}<p class="explain">${mode === 'auto' ? 'The lead may start approved work without asking each time.' : 'The lead asks you before each piece of work starts.'}</p>
        <button type="button" class="switch" role="switch" data-action="toggle-mode" aria-checked="${mode === 'auto'}"><span class="knob" aria-hidden="true"></span><span>Let the lead start approved work by itself</span></button></div>
      <div class="section">${head('sun', 't-violet', 'Look')}<div class="segmented" role="group" aria-label="Theme">${[['light', 'sun', 'Light'], ['dark', 'moon', 'Dark']].map(([value, icon, label]) => (value === 'dark') === dark ? `<span class="seg on" aria-current="true">${ic(icon)}${label}</span>` : `<button type="button" class="seg" data-action="theme">${ic(icon)}${label}</button>`).join('')}</div>
        ${dark ? `<p class="explain ink-title">Text colour</p><div class="inks" role="group" aria-label="Text colour">${Object.entries(INKS).map(([key, [label, colour]]) => `<button type="button" class="ink" data-ink="${key}" aria-pressed="${currentInk() === key}" title="${label}"><span class="swatch" style="background:${colour}" aria-hidden="true"></span>${label}</button>`).join('')}<label class="ink custom" title="Any colour"><input type="color" data-ink-custom value="${escape(inkColour(currentInk()))}" aria-label="Any colour">${HEX.test(currentInk()) ? 'Your colour' : 'Any colour…'}</label></div>` : ''}</div>
      ${autoSwitchSection()}
      ${skillsSection()}
      <div class="section">${head('box', 't-blue', 'Move a project')}<p class="explain">Save a project to a file, or open one saved on another computer. Code stays in the project folder; the file holds the plan, history and saved points.</p>
        <div class="row">${row ? `<a class="btn btn-small" href="/api/project-export?projectId=${encodeURIComponent(row.id)}" download="${escape(row.name.replace(/[^\w.-]+/g, '-'))}-planwharf.json">${ic('file')}Save ${escape(row.name)} to a file</a>` : ''}
        <label class="btn btn-small">${ic('folder')}Open a project file<input type="file" id="import-file" accept="application/json,.json" hidden></label></div>
        <p class="error" id="import-error" role="alert"></p></div>
      ${updatesSection()}
      ${row ? `<div class="section">${head('refresh', 't-blue', 'Start over')}<p class="explain">Clears the chat, plan and run history of ${escape(row.name)} so you can begin again. The folder, its files, the GitHub link and the team stay. Nothing is deleted: the old history is kept in Planwharf's archive.</p>${armedButton('start-over', 'Start over', 'Yes, start over')}</div>` : ''}
      ${row ? `<div class="section">${head('trash', 't-bad', 'Remove project')}<p class="explain">Removes ${escape(row.name)} from Planwharf. The folder and its files stay on your computer.</p>${armedButton('remove-project', 'Remove project', 'Yes, remove it')}</div>` : ''}
      <div class="section">${head('power', 't-muted', 'Quit')}<p class="explain">Unfinished work is saved and can be resumed next time.</p>${armedButton('quit', 'Quit Planwharf', 'Yes, quit now')}</div>`;
  }
  // Dangerous buttons need a second press; nothing opens a browser dialog.
  const armedButton = (action, label, confirmLabel) => state.armed === action
    ? `<div class="row"><button type="button" class="btn btn-danger btn-small" data-action="${action}">${confirmLabel}</button><button type="button" class="btn btn-small" data-action="disarm">Cancel</button></div>`
    : `<button type="button" class="btn btn-small" data-arm="${action}">${label}</button>`;
  // Automatic model switch: on by default at 20% quota left.
  function autoSwitchSection() {
    if (!state.autoSwitch) { if (!state.autoSwitchLoading) { state.autoSwitchLoading = true; getJson('/api/orchestrator/auto-switch').then(value => { state.autoSwitch = value; state.sideSig = ''; renderSide(); }).catch(() => {}).finally(() => { state.autoSwitchLoading = false; }); } return ''; }
    const on = state.autoSwitch.enabled !== false;
    return `<div class="section">${head('usage', 't-accent', 'Model switch')}<p class="explain">${on ? `When a model working on a step drops to ${state.autoSwitch.thresholdPercent}% quota left or less, Planwharf hands the step to another model with the same role and more quota, with a handoff of what was done.` : 'Off: a model that runs out of quota stops its step, and you choose the next model.'}</p>
      <button type="button" class="switch" role="switch" data-action="toggle-auto-switch" aria-checked="${on}"><span class="knob" aria-hidden="true"></span><span>Switch model automatically</span></button>
      ${on ? `<form id="auto-switch-form" class="row"><label class="small" for="auto-switch-threshold">Switch at</label><input id="auto-switch-threshold" name="threshold" type="number" min="0" max="90" step="5" value="${escape(String(state.autoSwitch.thresholdPercent))}" style="width:5em"><span class="small">% left</span><button type="submit" class="btn btn-small">Save</button></form>` : ''}</div>`;
  }
  async function saveAutoSwitch(change) {
    try { state.autoSwitch = await post('/api/orchestrator/auto-switch', change); toast('Model switch saved.'); }
    catch (error) { toast(error.message); }
    state.sideSig = ''; renderSide();
  }
  function updatesSection() {
    const u = state.updates;
    if (!u) return `<div class="section">${head('refresh', 't-accent', 'Updates')}<button type="button" class="btn btn-small" data-action="check-updates">${ic('refresh')}Check for updates</button></div>`;
    const busy = ['downloading', 'checkpointing', 'restarting', 'checking'].includes(u.state);
    const line = busy ? { downloading: 'Downloading the update…', checkpointing: 'Saving unfinished work…', restarting: 'Restarting…', checking: 'Checking…' }[u.state]
      : u.state === 'error' ? 'Could not check: ' + (u.reason || 'unknown error')
      : u.available ? `Planwharf ${u.latestVersion} is available${u.currentVersion ? ` (you have ${u.currentVersion})` : ''}.`
      : u.currentVersion ? `Planwharf ${u.currentVersion} is up to date.` : 'Version unknown in a source checkout.';
    const install = u.available && u.canInstall && !busy
      ? armedButton('install-update', 'Update and restart', state.updateNeedsConfirm ? 'Work is running: save it and update' : 'Yes, update now') : '';
    const download = u.available && !u.canInstall && u.releaseUrl ? `<a href="${escape(u.releaseUrl)}" target="_blank" rel="noopener noreferrer">Download it</a>` : '';
    return `<div class="section">${head('refresh', 't-accent', 'Updates')}<p class="explain">${escape(line)}</p><div class="row">${install}${download}<button type="button" class="btn btn-small" data-action="check-updates" ${busy ? 'disabled' : ''}>Check again</button></div></div>`;
  }
  async function loadUpdates(check = false) {
    try { state.updates = await (check ? post('/api/app/updates/check', {}) : getJson('/api/app/updates')); } catch { /* Offline checks never interrupt work. */ }
    renderUpdateNotice(); state.sideSig = ''; renderSide();
  }
  function renderUpdateNotice() {
    const u = state.updates;
    const devCopy = u && u.canInstall === false && /git checkout/i.test(u.reason || '');
    $('update-notice').innerHTML = u?.available && !devCopy ? `<button type="button" class="link-quiet" data-panel="settings">Update available: ${escape(u.latestVersion || '')}</button>` : '';
  }
  const currentTheme = () => document.documentElement.dataset.theme || 'dark';
  // Text colours for the dark theme, all readable on the dark background.
  const INKS = { white: ['White', '#ecefeb'], silver: ['Silver', '#c9ced6'], cream: ['Cream', '#f3e6c8'], yellow: ['Yellow', '#fff3a3'], amber: ['Amber', '#ffd38a'], peach: ['Peach', '#ffcfb0'], coral: ['Coral', '#ff9e8a'], pink: ['Pink', '#ffc4dd'], lavender: ['Lavender', '#dccfff'], sky: ['Sky', '#bcdcff'], turquoise: ['Turquoise', '#99f6e4'], mint: ['Mint', '#b4f0d2'], lime: ['Lime', '#d9f99d'] };
  const HEX = /^#[0-9a-f]{6}$/i;
  // A preset name or any colour the user picked (#rrggbb); every choice is accepted.
  const currentInk = () => { const ink = store.get('ink', 'white'); return Object.prototype.hasOwnProperty.call(INKS, ink) || HEX.test(ink) ? ink : 'white'; };
  const inkColour = ink => HEX.test(ink) ? ink : INKS[ink][1];
  function setInk(ink) { if (!Object.prototype.hasOwnProperty.call(INKS, ink) && !HEX.test(ink)) return; store.set('ink', ink); document.documentElement.style.setProperty('--ink', inkColour(ink)); }
  async function saveFolder(form) {
    const row = project(); if (!row) return;
    const error = $('folder-error'); error.textContent = '';
    const folder = String(new FormData(form).get('folder') || '').trim();
    const github = String(new FormData(form).get('github') || '').trim().replace(/\.git$/, '').replace(/\/+$/, '');
    if (!folder) { error.textContent = 'Enter a folder.'; return; }
    if (!github) { const declined = store.get('githubDeclined', {}); declined[row.id] = true; store.set('githubDeclined', declined); }
    try {
      const { version } = await getJson('/api/plan');
      await post('/api/place', { version, project: { id: row.id, folder, ...(github ? { github } : {}) } });
      delete state.repo[row.id]; state.sideSig = '';
      toast('Saved.'); await refresh({ tick: false });
    } catch (failure) { error.textContent = failure.message; }
  }
  async function saveRoot(form) {
    const error = $('root-error'); error.textContent = '';
    try { state.settings = await post('/api/settings', { projectsRoot: String(new FormData(form).get('root') || '').trim() }); state.sideSig = ''; toast('Saved.'); renderSide(); }
    catch (failure) { error.textContent = failure.message; }
  }

  // ---------- actions ----------
  let toastTimer;
  function toast(text) { const el = $('toast'); el.textContent = text; el.classList.add('show'); clearTimeout(toastTimer); toastTimer = setTimeout(() => el.classList.remove('show'), 4000); }
  async function act(work, done) {
    try { const snapshot = await work(); if (snapshot && snapshot.projects) state.orchestra = { ...state.orchestra, ...snapshot }; if (done) toast(done); }
    catch (error) { toast(error.message); }
    state.sideSig = ''; await refresh({ tick: false });
  }
  function openPanel(name) { state.panel = state.panel === name ? null : name; store.set('panel', state.panel); render(); }

  // Short commands that only open a panel or change the team run here, without a model call.
  function localCommand(text) {
    const clean = text.trim().toLowerCase().replace(/[.!?]+$/, '');
    // Stopping is Planwharf's job, not the lead's: "stop", "fermati adesso", "fermati tra 10 minuti".
    const later = /^(?:stop|fermati|fermatevi|ferma(?:ti)?(?: tutto)?|stop the crew)\s+(?:in|tra|fra)\s+(\d{1,3})\s*(?:m|min|mins|minute|minutes|minuto|minuti)?\b/.exec(clean);
    if (later && project()) {
      const minutes = Number(later[1]);
      if (minutes < 1) { stopProject(project().id); return true; }
      setTimedStop({ projectId: project().id, at: Date.now() + minutes * 60000 });
      toast(`The crew stops in ${minutes} minute${minutes === 1 ? '' : 's'}. You can cancel it below.`);
      return true;
    }
    if (/^(?:stop|stop now|stop the crew|stop work|fermati|fermatevi|fermati (?:adesso|ora|subito)|fermatevi (?:adesso|ora|subito)|ferma tutto|basta)$/.test(clean) && project()) { stopProject(project().id); return true; }
    if (clean.split(/\s+/).length > 7) return false;
    if (/^(open |show |apri |mostra |vedi )?(the |la |il |i )?(team|crew|squadra|bench|panchina)$/.test(clean)) { state.panel = 'team'; store.set('panel', 'team'); render(); return true; }
    if (/^(open |show |apri |mostra |vedi )?(the |i |l')?(usage|quota|tokens?|consumi|utilizzo)$/.test(clean)) { state.panel = 'usage'; store.set('panel', 'usage'); render(); return true; }
    if (/^(open |show |apri |mostra |vedi )?(the |l')?(progress|status|avanzamento|stato)$/.test(clean)) { state.panel = 'progress'; store.set('panel', 'progress'); render(); return true; }
    if (/^(open |show |apri |mostra |vedi )?(the |i |le )?(files?|changes|changed files|file cambiati|modifiche)$/.test(clean)) { state.panel = 'files'; store.set('panel', 'files'); render(); return true; }
    if (/^(open |show |apri |mostra |vedi )?(the |le )?(settings|options|impostazioni|opzioni)$/.test(clean)) { state.panel = 'settings'; store.set('panel', 'settings'); render(); return true; }
    const bench = /^(?:bench|put|metti)\s+(.+?)(?:\s+(?:on the bench|in panchina))?$/.exec(clean);
    if (bench && (/^bench\s/.test(clean) || /(on the bench|in panchina)$/.test(clean))) {
      const wanted = bench[1].replace(/^(the |il |la )/, '');
      const model = models().find(item => item.label.toLowerCase().includes(wanted) || item.id.toLowerCase() === wanted);
      if (!model) { toast(`No model named “${wanted}”.`); return true; }
      state.confirm = { text: `Put ${model.label} on the bench? It will have no role until you bring it back.`, run: () => setRoles(model.id, NO_ROLES) };
      render(); return true;
    }
    return false;
  }
  // Team changes apply to the selected project only; every project keeps its own team.
  const teamProject = () => state.projectId ? { projectId: state.projectId } : {};
  async function setRoles(id, roles) { await act(() => post('/api/models/roles', { id, roles, ...teamProject() }), 'Team updated.'); }
  async function chooseLead(id) {
    if (!id) return;
    const model = models().find(item => item.id === id);
    await act(async () => { if (model && !model.roles?.orchestrator) await post('/api/models/roles', { id, roles: { orchestrator: true }, ...teamProject() }); return post('/api/orchestrator/lead', { id, ...teamProject() }); }, `${model?.label || 'Lead'} is now the lead.`);
  }
  async function send(text, extra = {}) {
    const row = project();
    if (!row) { toast('Create or select a project first.'); return; }
    if (!lead()) { toast('Choose a lead first (Team panel).'); openPanel('team'); return; }
    state.pending[row.id] = { text, since: Date.now() }; state.threadSig = '';
    render();
    const poll = setInterval(async () => { try { const snap = await getJson('/api/orchestrator?readOnly=1'); state.orchestra = { ...state.orchestra, thinkingByProject: snap.thinkingByProject || {} }; renderThread(); } catch {} }, 900);
    try {
      const body = await post('/api/orchestrator/ask', { question: text, projectId: row.id, chatSessionId: sessionFor(row.id), ...extra });
      if (body.snapshot) state.orchestra = { ...state.orchestra, ...body.snapshot };
      if (body.asks) state.orchestra.asks = body.asks;
    } catch (error) { toast(error.message); }
    finally { clearInterval(poll); delete state.pending[row.id]; if (state.orchestra?.thinkingByProject) delete state.orchestra.thinkingByProject[row.id]; state.threadSig = ''; await refresh({ tick: false }); }
  }
  async function autonomyOn() {
    const row = project(); if (!row) return;
    const missing = readiness(row);
    if (missing.length) { toast(missing[0].text); return; }
    const nextGoal = state.suggestedGoal || state.goalCandidate;
    if (nextGoal) {
      state.confirm = { text: `The crew will work on: ${nextGoal}`, run: async () => {
        try { const { version } = await getJson('/api/plan'); await post('/api/revisions', { version, projectId: row.id, draft: { title: 'Goal from the chat', direction: nextGoal, state: 'Not started.', closed: [], open: [] } }); }
        catch (error) { toast(error.message); return; }
        state.goalCandidate = null; state.suggestedGoal = null;
        await startCrew(row);
      } };
      state.threadSig = ''; render(); return;
    }
    await startCrew(row);
  }
  async function startCrew(row) {
    await act(async () => {
      if (state.orchestra.mode !== 'auto') await post('/api/orchestrator/mode', { mode: 'auto' });
      return post('/api/orchestrator/autonomy', { projectId: row.id, enabled: true });
    }, 'The crew is on it. Updates appear here.');
  }
  // Stops a project's work now: the crew mode first, then any running step. Work so far stays saved.
  async function stopProject(projectId) {
    const row = (state.orchestra?.projects || []).find(item => item.id === projectId);
    if (timedStop()?.projectId === projectId) setTimedStop(null);
    const auto = autonomyOf(projectId);
    if (!row || !(auto?.enabled && !auto.stopReason) && !row.canStop) { toast('Nothing is running on this project.'); return; }
    await act(async () => {
      const snap = auto?.enabled ? await post('/api/orchestrator/autonomy', { projectId, enabled: false }) : null;
      const current = snap ? (snap.projects || []).find(item => item.id === projectId) : row;
      return current?.canStop ? post('/api/orchestrator/stop', { projectId }) : snap;
    }, 'Stopped. Work so far is saved; Resume continues from there.');
  }
  const timedStop = () => { const value = store.get('timedStop', null); return value && typeof value.at === 'number' && typeof value.projectId === 'string' ? value : null; };
  function setTimedStop(value) { store.set('timedStop', value); state.threadSig = ''; render(); }
  setInterval(() => { const due = timedStop(); if (due && Date.now() >= due.at) { setTimedStop(null); stopProject(due.projectId); } }, 5000);
  async function autonomyOff() {
    const row = project(); if (!row) return;
    await act(async () => {
      const snap = await post('/api/orchestrator/autonomy', { projectId: row.id, enabled: false });
      const current = (snap.projects || []).find(item => item.id === row.id);
      return current?.canStop ? post('/api/orchestrator/stop', { projectId: row.id }) : snap;
    }, 'The crew stopped. Work so far is saved.');
  }
  function quitApp() {
    post('/api/app/quit', {}).then(() => { document.body.innerHTML = '<p style="padding:24px">Planwharf has closed. You can close this window.</p>'; try { window.close(); } catch {} }).catch(error => toast(error.message));
  }
  async function importProject(file) {
    const error = $('import-error'); if (error) error.textContent = '';
    try {
      if (file.size > 4 * 1024 * 1024) throw new Error('This file is larger than 4 MB.');
      let bundle; try { bundle = JSON.parse(await file.text()); } catch { throw new Error('This is not a Planwharf project file.'); }
      const { version } = await getJson('/api/plan');
      const result = await post('/api/project-import', { version, bundle });
      if (result.projectId) { state.projectId = result.projectId; store.set('project', result.projectId); }
      toast(result.continue ? 'Project opened. Check its folder in Settings, then press Resume.' : 'Project opened. Check its folder in Settings.');
      await refresh();
    } catch (failure) { if ($('import-error')) $('import-error').textContent = failure.message; else toast(failure.message); }
  }
  async function addConnection(form) {
    const error = $('api-error'); error.textContent = '';
    const data = new FormData(form);
    const label = String(data.get('label') || '').trim();
    const id = label.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').replace(/^(?![a-z])/, 'api-').slice(0, 60) || 'api';
    const authEnv = String(data.get('authEnv') || '').trim();
    const input = { id, label, adapter: 'api', baseUrl: String(data.get('baseUrl') || '').trim(), apiProtocol: data.get('apiProtocol'),
      modelIds: String(data.get('modelIds') || '').split(',').map(item => item.trim()).filter(Boolean), toolsEnabled: data.get('toolsEnabled') === 'on', ...(authEnv ? { authEnv } : {}) };
    try { const snapshot = await post('/api/providers/connections', input); state.orchestra = { ...state.orchestra, ...snapshot }; form.reset(); toast(`${label} connected. Give its models a role to use them.`); state.sideSig = ''; await refresh({ tick: false }); }
    catch (failure) { error.textContent = failure.message.replace(/^API_CONFIGURATION_INVALID:\s*/, ''); }
  }
  async function startOver() {
    const row = project(); if (!row) return;
    await act(async () => { const { version } = await getJson('/api/plan'); await post('/api/start-over', { version, projectId: row.id }); state.threadSig = ''; }, `${row.name} starts over. Tell the lead what to build.`);
  }
  async function removeProject() {
    const row = project(); if (!row) return;
    await act(async () => { const { version } = await getJson('/api/plan'); await post('/api/remove-project', { version, projectId: row.id }); state.projectId = null; store.set('project', null); }, `${row.name} removed. Its folder is untouched.`);
  }
  async function installUpdate() {
    try {
      const result = await post('/api/app/updates/install', { confirmRunning: Boolean(state.updateNeedsConfirm) });
      if (result.requiresConfirmation) { state.updateNeedsConfirm = true; state.armed = 'install-update'; toast('Some work is running. Press again to save it and update.'); }
      else toast('Planwharf is updating and will restart. Unfinished work can be resumed.');
    } catch (error) { toast(error.message); }
    await loadUpdates();
  }
  async function createProject(form) {
    const error = $('project-error'); error.textContent = '';
    const data = Object.fromEntries(new FormData(form));
    const folder = String(data.folder || '').trim();
    const github = String(data.github || '').trim().replace(/\.git$/, '').replace(/\/+$/, '');
    try {
      const [{ version }, settings] = await Promise.all([getJson('/api/plan'), getJson('/api/settings').catch(() => ({}))]);
      if (!folder && !settings.projectsRoot) throw new Error('Enter the folder for this project, for example C:\\Projects\\my-website.');
      // Only name, folder and an optional GitHub link: what to build is said later, in the chat.
      const created = await post('/api/projects', { version, project: { name: String(data.name).trim(), direction: NO_GOAL, ...(folder ? { folder } : {}), ...(github ? { github } : {}) }, createFolder: !folder });
      const added = created.plan?.projects?.at(-1)?.project?.id;
      $('project-dialog').close(); form.reset();
      if (added) { state.projectId = added; store.set('project', added); }
      toast('Project created.');
      await refresh();
    } catch (failure) { error.textContent = failure.message; }
  }

  // One click copies a lead reply as plain text, like the copy button in other chat apps.
  async function copyReply(button) {
    const text = button.closest('.msg')?.querySelector('.bubble')?.innerText || '';
    if (!text) return;
    let done = false;
    try { await navigator.clipboard.writeText(text); done = true; } catch {}
    if (!done) {
      const area = document.createElement('textarea');
      area.value = text; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0';
      document.body.appendChild(area); area.select();
      try { done = document.execCommand('copy'); } catch {}
      area.remove();
    }
    const label = button.querySelector('span');
    if (label) { label.textContent = done ? 'Copied' : 'Copy failed'; setTimeout(() => { label.textContent = 'Copy'; }, 1600); }
    button.classList.toggle('done', done);
    setTimeout(() => button.classList.remove('done'), 1600);
  }

  // ---------- events ----------
  document.addEventListener('click', event => {
    const target = event.target.closest('button, a');
    if (!target) return;
    if (target.dataset.project) { state.projectId = target.dataset.project; store.set('project', state.projectId); state.threadSig = ''; state.confirm = null; $('projects-panel').classList.remove('open'); state.sideSig = ''; render(); refresh({ tick: false }); return; }
    if (target.dataset.panel) { if (target.classList.contains('tab')) openPanel(target.dataset.panel); else { state.panel = target.dataset.panel; store.set('panel', state.panel); render(); } return; }
    if (target.id === 'side-close') { state.panel = null; store.set('panel', null); render(); return; }
    if (target.dataset.openSide) { $(target.dataset.openSide).classList.add('open'); return; }
    if (target.dataset.closeSide) { $(target.dataset.closeSide).classList.remove('open'); return; }
    if (target.id === 'new-project' || target.dataset.action === 'new-project') { $('project-error').textContent = ''; $('project-dialog').showModal(); return; }
    if (target.id === 'project-cancel') { $('project-dialog').close(); return; }
    if (target.dataset.ignoreAlert) { act(() => post('/api/supervisor/ignore', { projectId: state.projectId, alertId: target.dataset.ignoreAlert }), 'Warning ignored.'); return; }
    if (target.dataset.arm) { state.armed = target.dataset.arm; state.sideSig = ''; renderSide(); return; }
    if (target.dataset.roleToggle) { const model = models().find(item => item.id === target.dataset.roleToggle); if (model) setRoles(model.id, { [target.dataset.role]: !(model.roles?.[target.dataset.role] === true) }); return; }
    if (target.dataset.bench) { const model = models().find(item => item.id === target.dataset.bench); if (model) setRoles(model.id, NO_ROLES); return; }
    const action = target.dataset.action;
    if (action === 'copy-msg') { copyReply(target); return; }
    if (action === 'autonomy-on') autonomyOn();
    else if (action === 'autonomy-off') autonomyOff();
    else if (action === 'stop') act(() => post('/api/orchestrator/stop', { projectId: state.projectId }), 'Work stopped and saved.');
    else if (target.dataset.ink) { setInk(target.dataset.ink); state.sideSig = ''; renderSide(); }
    else if (action === 'theme') { document.documentElement.dataset.theme = currentTheme() === 'dark' ? 'light' : 'dark'; store.set('theme', document.documentElement.dataset.theme); state.sideSig = ''; renderSide(); }
    else if (action === 'toggle-mode') act(() => post('/api/orchestrator/mode', { mode: state.orchestra?.mode === 'auto' ? 'manual' : 'auto' }), 'Saved.');
    else if (action === 'repo-refresh') loadRepo(state.projectId, true);
    else if (action === 'approve') act(() => post('/api/orchestrator/approve', { projectId: state.projectId, ...(project()?.megaTaskId ? { megaTaskId: project().megaTaskId } : {}) }), 'Approved. The crew is starting.');
    else if (action === 'resume') act(() => post('/api/orchestrator/resume', { projectId: state.projectId }), 'Resumed.');
    else if (target.dataset.providerToggle) { const provider = (state.orchestra?.providers || []).find(item => item.id === target.dataset.providerToggle); if (provider) act(() => post('/api/catalog/settings', { providerId: provider.id, enabled: provider.enabled === false }), `${provider.label || provider.id} turned ${provider.enabled === false ? 'on' : 'off'}.`); }
    else if (action === 'refresh-models') act(() => post('/api/models/refresh', {}), 'Model list updated.');
    else if (action === 'check-updates') loadUpdates(true);
    else if (action === 'disarm') { state.armed = null; state.sideSig = ''; renderSide(); }
    else if (action === 'remove-project') { state.armed = null; removeProject(); }
    else if (action === 'start-over') { state.armed = null; startOver(); }
    else if (action === 'tool-on') setTool(target.dataset.tool, true);
    else if (action === 'skills-open') post('/api/skills/open', { cli: target.dataset.cli, what: target.dataset.what }).then(result => toast('Opened ' + result.path)).catch(error => toast(error.message));
    else if (action === 'skills-refresh') { state.skills = null; state.sideSig = ''; renderSide(); }
    else if (action === 'tool-off') setTool(target.dataset.tool, false);
    else if (action === 'quit') { state.armed = null; quitApp(); }
    // Quit is always in the top bar; it asks once in the chat before closing.
    else if (action === 'quit-ask') { state.confirm = { text: 'Quit Planwharf? Unfinished work is saved, and Resume continues it next time.', run: quitApp }; state.threadSig = ''; render(); }
    else if (action === 'install-update') installUpdate();
    else if (action === 'use-goal') useAsGoal(state.goalCandidate);
    else if (action === 'use-suggested-goal') proposeSuggestion(state.suggestedGoal);
    else if (action === 'focus-input') $('composer-input').focus();
    else if (action === 'cancel-timed-stop') { setTimedStop(null); toast('Timed stop cancelled.'); }
    else if (action === 'toggle-auto-switch') saveAutoSwitch({ enabled: !(state.autoSwitch?.enabled !== false) });
    else if (action === 'confirm-yes') { const run = state.confirm?.run; state.confirm = null; state.threadSig = ''; if (run) run(); else render(); }
    else if (action === 'confirm-no') { state.confirm = null; state.threadSig = ''; render(); }
  });
  // The chat redraws while the lead works: remember whether its activity box is open.
  document.addEventListener('toggle', event => { if (event.target.matches?.('details[data-live-thinking]')) state.thinkingOpen = event.target.open; }, true);
  document.addEventListener('change', event => {
    if (event.target.matches('[data-lead-select]')) chooseLead(event.target.value);
    else if (event.target.matches('[data-crew-step]')) {
      const row = project(), select = event.target, change = () => act(async () => { const result = await post('/api/orchestrator/crew', { projectId: row.id, assignments: { [select.dataset.crewStep]: select.value } }); if (result?.message) toast(result.message); return result; });
      if (!row) return;
      // A working step restarts with the new model: say so before doing it.
      if (select.dataset.running === 'true') { state.confirm = { text: `Switch this step to ${select.options[select.selectedIndex]?.text || select.value}? Its current worker stops and the step starts again with the new model. Files already written stay.`, run: change }; state.threadSig = ''; render(); }
      else change();
    }
    else if (event.target.matches('[data-ink-custom]')) { setInk(event.target.value.toLowerCase()); state.sideSig = ''; renderSide(); }
    else if (event.target.id === 'import-file' && event.target.files?.[0]) importProject(event.target.files[0]);
  });
  $('composer').addEventListener('submit', event => {
    event.preventDefault();
    const input = $('composer-input'); const text = input.value.trim();
    if (!text || state.pending[state.projectId]) return;
    input.value = ''; input.style.height = '';
    if (!localCommand(text)) send(text);
  });
  $('composer-input').addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); $('composer').requestSubmit(); } });
  $('composer-input').addEventListener('input', event => { const el = event.target; el.style.height = 'auto'; el.style.height = Math.min(200, el.scrollHeight) + 'px'; });
  $('side-body').addEventListener('input', event => { const form = event.target.closest('form'); if (form) form.dataset.dirty = '1'; });
  $('project-form').addEventListener('submit', event => { event.preventDefault(); createProject(event.target); });
  document.addEventListener('submit', event => {
    if (event.target.id === 'folder-form') { event.preventDefault(); saveFolder(event.target); }
    else if (event.target.id === 'root-form') { event.preventDefault(); saveRoot(event.target); }
    else if (event.target.id === 'auto-switch-form') { event.preventDefault(); saveAutoSwitch({ thresholdPercent: Number(event.target.threshold.value) }); }
    else if (event.target.id === 'api-form') { event.preventDefault(); addConnection(event.target); }
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && state.panel && !$('project-dialog').open) { state.panel = null; store.set('panel', null); render(); } });

  const windowId = 'chat-' + Math.random().toString(36).slice(2, 10);
  let focusSeen = 0;
  async function pulse() {
    try {
      const data = await post('/api/app/heartbeat', { id: windowId, view: 'planwharf', focusSeen });
      if (data.title && data.title !== state.windowTitle) { state.windowTitle = data.title; document.title = data.title; }
      focusSeen = data.focusVersion || 0;
      if (data.focus) window.focus();
    } catch { /* A disconnected page is not a request to stop work. */ }
  }
  window.addEventListener('pagehide', () => { try { navigator.sendBeacon('/api/app/heartbeat', new Blob([JSON.stringify({ id: windowId, view: 'planwharf', closed: true })], { type: 'application/json' })); } catch {} });
  window.addEventListener('pageshow', pulse);
  pulse(); setInterval(pulse, 3000);
  // Day and time, always visible at the bottom of the project list.
  function showNow() { const d = new Date(); const day = d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' }); const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }); $('now').textContent = day + ' · ' + time; $('now').title = d.toLocaleString(); }
  showNow(); setInterval(showNow, 15000);
  loadUpdates(); setInterval(loadUpdates, 60000);

  // Tabs keep their name for screen readers and as a tooltip when only the icon fits.
  document.querySelectorAll('.tab').forEach(tab => { tab.title = tab.textContent.trim(); tab.innerHTML = `<span class="tab-label">${escape(tab.textContent.trim())}</span>`; });
  document.querySelectorAll('[data-icon]').forEach(el => el.insertAdjacentHTML('afterbegin', ic(el.dataset.icon)));
  // Dark is the default; light only when the user chose it. The text colour applies to the dark theme.
  document.documentElement.dataset.theme = store.get('theme', null) === 'light' ? 'light' : 'dark';
  setInk(currentInk());
  getJson('/api/settings').then(value => { state.settings = value; }).catch(() => {});
  // The first read answers at once from saved data; the full check follows.
  refresh({ tick: false }).then(() => refresh());
  setInterval(() => { if (!document.hidden && !Object.keys(state.pending).length) refresh(); }, 4000);
})();
