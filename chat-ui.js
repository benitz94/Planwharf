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
    sending: false, repo: {}, pendingText: '', pendingSince: 0, sessions: store.get('sessions', {}), threadSig: '', sideSig: '', confirm: null, loaded: false
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
      const [orchestra, plan] = await Promise.all([getJson(tick ? '/api/orchestrator' : '/api/orchestrator?readOnly=1'), getJson('/api/plan')]);
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
  const onBench = model => !model.roles?.orchestrator && !model.roles?.worker && !model.roles?.verifier;
  const STOP_REASONS = {
    GOAL_REACHED: 'Goal reached', MEGATASK_LIMIT: 'Stopped: step limit reached', NO_LEAD_AVAILABLE: 'Stopped: no lead available',
    START_UNAVAILABLE: 'Stopped: could not start', TIME_LIMIT: 'Stopped: time limit reached'
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
  function renderProjects() {
    const rows = state.orchestra?.projects || [];
    $('project-list').innerHTML = rows.length ? rows.map(row => {
      const status = projectStatus(row);
      return `<button type="button" class="project-item" data-project="${escape(row.id)}" aria-current="${row.id === state.projectId}"><span class="dot ${status.kind === 'running' ? 'running' : status.kind === 'attention' ? 'attention' : ''}" aria-hidden="true"></span><span class="name">${escape(row.name)}</span></button>`;
    }).join('') : '<p class="muted small">No projects yet.</p>';
  }
  function renderHeader() {
    const row = project();
    $('project-title').textContent = row ? row.name : 'Welcome';
    const status = projectStatus(row);
    const pill = $('project-status'); pill.textContent = status.text; pill.className = 'pill ' + status.kind;
    document.title = row ? `${row.name} · Planwharf` : 'Planwharf';
    document.querySelectorAll('.tab').forEach(tab => tab.setAttribute('aria-pressed', String(tab.dataset.panel === state.panel)));
  }

  function eventView(event) {
    const type = String(event.type || '');
    const summary = String(event.summary || '').replace(/\s+/g, ' ').trim();
    const short = summary.length > 260 ? summary.slice(0, 257) + '…' : summary;
    if (/COMPLETED|REACHED|APPROVED/.test(type)) return { icon: '✓', kind: 'good', text: short };
    if (/FAILED|BLOCKED|STOPPED|REFUS/.test(type)) return { icon: '!', kind: 'bad', text: short };
    if (/STARTED/.test(type)) return { icon: '▶', kind: '', text: short };
    if (/PLAN_REVISION/.test(type)) return { icon: '✎', kind: '', text: 'Plan updated: ' + short.replace(/^Living Plan R\d+:\s*/, '') };
    if (/PROPOSED|ASSIGNED|PLAN/.test(type)) return { icon: '✎', kind: '', text: short };
    return { icon: '·', kind: '', text: short };
  }
  const SHOWN_EVENTS = /PLAN_REVISION|MEGATASK_(PROPOSED|APPROVED|ASSIGNED|COMPLETED|FAILED|STOPPED|BLOCKED)|SUBTASK_(STARTED|COMPLETED|FAILED|BLOCKED)|CHECKPOINT|GOAL|AUTONOMY/;
  const clock = at => { const date = new Date(at); return Number.isFinite(date.getTime()) ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }) : ''; };

  function welcomeCard() {
    const leads = models().filter(model => model.roleEligibility?.orchestrator && model.available !== false);
    const current = lead();
    return `<div class="card"><h2>Welcome aboard, captain</h2>
      <p>Planwharf runs a team of AI models for you. You give the goal; a <b>lead</b> plans it, a <b>builder</b> writes the code and a <b>verifier</b> checks and fixes it.</p>
      <ol class="steps">
        <li class="${current ? 'done' : ''}">Choose your lead.</li>
        <li class="${(state.orchestra?.projects || []).length ? 'done' : ''}">Create a project: a name, what to build, and a folder.</li>
        <li>Press <b>Let the crew do it</b> and watch the work here.</li>
      </ol>
      <div class="row"><label class="sr-only" for="welcome-lead">Lead</label><select id="welcome-lead" data-lead-select><option value="">Choose your lead…</option>${leads.map(model => `<option value="${escape(model.id)}" ${current?.id === model.id ? 'selected' : ''}>${escape(model.label)}</option>`).join('')}</select>
      ${(state.orchestra?.projects || []).length ? '' : '<button type="button" class="btn btn-primary" data-action="new-project">+ New project</button>'}</div></div>`;
  }
  function startCard(row) {
    const missing = readiness(row);
    return `<div class="card"><h3>${escape(row.name)}</h3><p>${escape(row.goal || 'No goal written yet.')}</p>
      <p class="muted small path">Folder: ${escape(row.folder || 'not set')}</p>
      ${missing.length ? `<p class="small"><b>Before the crew can start:</b></p><div class="row">${missing.map(item => `<button type="button" class="btn btn-small" data-panel="${item.panel}">${escape(item.fix)}</button>`).join('')}</div>` : '<p class="small">Everything is ready. Press <b>Let the crew do it</b> below, or tell the lead more first.</p>'}
      <div class="row"><button type="button" class="btn" data-action="grill" ${lead() ? '' : 'disabled'}>Ask me questions first</button></div></div>`;
  }
  function readiness(row) {
    const missing = [];
    if (!lead()) missing.push({ text: 'Choose a lead in Team.', fix: 'Choose the lead', panel: 'team' });
    if (!builders().length) missing.push({ text: 'Give at least one model the Build role in Team.', fix: 'Pick a builder', panel: 'team' });
    if (!row?.folder) missing.push({ text: 'Set the project folder in Settings.', fix: 'Set the folder', panel: 'settings' });
    return missing;
  }

  function renderThread() {
    const thread = $('thread');
    const row = project();
    const asks = (state.orchestra?.asks || []).filter(item => row ? item.projectId === row.id : item.projectId == null);
    const events = row ? (state.orchestra?.execution?.timeline || []).filter(event => event.projectId === row.id && SHOWN_EVENTS.test(event.type || '')).slice(-60) : [];
    const live = state.orchestra?.thinking;
    const pendingHere = state.sending && state.pendingProject === state.projectId;
    const sig = JSON.stringify([state.projectId, asks.map(item => [item.at, item.answer?.length, item.ms]), events.map(event => event.id || event.createdAt), pendingHere, pendingHere && [live?.thinking?.length, live?.draft?.length], state.confirm, Boolean(lead()), models().map(model => model.roles?.worker).join(), row?.folder, (state.orchestra?.projects || []).length, projectStatus(row).text]);
    if (sig === state.threadSig) return;
    state.threadSig = sig;
    const nearBottom = thread.scrollHeight - thread.scrollTop - thread.clientHeight < 120;
    const items = [
      ...asks.map(item => ({ at: Date.parse(item.at) || 0, html: askHtml(item) })),
      ...events.map(event => ({ at: Date.parse(event.createdAt) || 0, html: eventHtml(event) }))
    ].sort((a, b) => a.at - b.at);
    let html = '';
    if (!row) html = welcomeCard();
    else if (!asks.length) html = (lead() ? '' : welcomeCard()) + startCard(row);
    html += items.map(item => item.html).join('');
    if (pendingHere) html += `<div class="msg you"><div class="bubble">${escape(state.pendingText)}</div></div><div class="msg lead"><span class="who">${escape(lead()?.label || 'Lead')}</span><div class="bubble">${live?.draft ? escape(live.draft) : '<span class="dots">Thinking</span>'}</div>${live?.thinking ? `<details class="thinking"><summary>What the lead is doing</summary><pre>${escape(live.thinking.slice(-4000))}</pre></details>` : ''}</div>`;
    if (state.confirm) html += `<div class="card"><p>${escape(state.confirm.text)}</p><div class="row"><button type="button" class="btn btn-primary" data-action="confirm-yes">Yes, do it</button><button type="button" class="btn" data-action="confirm-no">No</button></div></div>`;
    thread.innerHTML = html;
    if (nearBottom || pendingHere) thread.scrollTop = thread.scrollHeight;
  }
  function askHtml(item) {
    const failed = Boolean(item.diagnostic);
    const elapsed = Number.isFinite(item.ms) ? ` · ${Math.max(1, Math.round(item.ms / 1000))} s` : '';
    return `<div class="msg you"><div class="bubble">${escape(item.question)}</div></div>
      <div class="msg lead${failed ? ' error' : ''}"><span class="who">${escape(modelLabel(item.orchestratorId) || 'Lead')} · ${escape(clock(item.at))}${elapsed}</span><div class="bubble">${escape(item.answer || '')}</div>${item.thinking ? `<details class="thinking"><summary>How the lead got there</summary><pre>${escape(String(item.thinking).slice(-4000))}</pre></details>` : ''}</div>`;
  }
  function eventHtml(event) {
    const view = eventView(event);
    return `<div class="event ${view.kind}"><span class="icon" aria-hidden="true">${view.icon}</span><span>${escape(view.text)}</span><time>${escape(clock(event.createdAt))}</time></div>`;
  }

  function renderActions() {
    const bar = $('action-bar');
    const row = project();
    if (!row) { bar.innerHTML = ''; return; }
    const auto = autonomyOf(row.id);
    const working = auto?.enabled && !auto.stopReason;
    const missing = readiness(row);
    const parts = [];
    if (working) parts.push('<button type="button" class="btn btn-danger" data-action="autonomy-off">Stop the crew</button><span class="status-text">The crew is working. You can keep chatting with the lead.</span>');
    else {
      parts.push(`<button type="button" class="btn btn-primary" data-action="autonomy-on" ${missing.length ? 'disabled' : ''} title="${escape(missing.map(item => item.text).join(' ') || 'Plan, build, verify and stop when the goal is reached')}">Let the crew do it</button>`);
      if (row.canStop) parts.push('<button type="button" class="btn btn-danger" data-action="stop">Stop work</button>');
      if (missing.length) parts.push(`<span class="status-text">${escape(missing[0].text)}</span>`);
      else if (row.canResume) parts.push('<span class="status-text">Work was paused. Press the button to continue.</span>');
      else parts.push('<span class="status-text">Ready. The crew will plan, build, check and stop when the goal is reached.</span>');
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
      return `<button type="button" class="chip" data-role-toggle="${escape(model.id)}" data-role="${role}" aria-pressed="${model.roles?.[role] === true}" ${eligible ? '' : 'disabled'} title="${escape(help)}">${label}</button>`;
    };
    return `<div class="member"><div class="info"><div class="label">${escape(model.label)}${model.id === state.orchestra?.orchestratorId ? ' <span class="badge running">Lead</span>' : ''}</div>
      <div class="meta">${escape(model.plan || model.provider || '')}${unavailable ? ' · not available now' : ''}${left != null ? ` · ${left}% quota left` : ''}</div>
      ${left != null ? `<div class="bar${left < 20 ? ' low' : ''}"><span style="width:${Math.max(0, Math.min(100, left))}%"></span></div>` : ''}</div>
      <div class="chips">${chip('worker', 'Build', 'Writes the code and the tests')}${chip('verifier', 'Verify', 'Checks the work and fixes what fails')}
      ${onBench(model) ? '' : `<button type="button" class="chip" data-bench="${escape(model.id)}" title="Take this model out of the team">Bench</button>`}</div></div>`;
  }
  function teamPanel() {
    const all = models();
    const current = lead();
    const leads = all.filter(model => model.roleEligibility?.orchestrator && model.available !== false);
    const active = all.filter(model => !onBench(model));
    const bench = all.filter(onBench);
    return `<div class="section"><h3>Lead</h3><p class="explain">Talks with you, plans the work and picks who does what.</p>
        <select data-lead-select aria-label="Lead"><option value="">Choose your lead…</option>${leads.map(model => `<option value="${escape(model.id)}" ${current?.id === model.id ? 'selected' : ''}>${escape(model.label)}</option>`).join('')}</select></div>
      <div class="section"><h3>On the team</h3><p class="explain"><b>Build</b> writes code and tests. <b>Verify</b> checks the work and fixes it, ideally a model from another company.</p>
        ${active.length ? active.map(memberHtml).join('') : '<p class="muted small">Nobody yet. Bring models in from the bench.</p>'}</div>
      <div class="section"><h3>Bench</h3><p class="explain">Available but not used. Press Build or Verify to bring a model in.</p>
        ${bench.length ? bench.map(memberHtml).join('') : '<p class="muted small">The bench is empty.</p>'}</div>
      <p class="small muted">Reviewer and Supervisor roles, providers and model policies are in the <a href="/advanced#orchestrator">Advanced view</a>.</p>`;
  }
  function progressPanel() {
    const row = project();
    if (!row) return '<p class="muted">Select a project.</p>';
    const status = projectStatus(row);
    const subs = row.subTasks || row.megaTask?.subTasks || [];
    const label = id => models().find(model => model.id === id)?.label || id || 'not assigned';
    const events = (state.orchestra?.execution?.timeline || []).filter(event => event.projectId === row.id).slice(-15).reverse();
    return `<div class="section"><h3>Now</h3><p><span class="pill ${status.kind}">${escape(status.text)}</span></p>${row.next ? `<p class="small">${escape(row.next)}</p>` : ''}</div>
      ${subs.length ? `<div class="section"><h3>Steps</h3><ul class="status-list">${subs.map(sub => `<li><span class="badge ${escape(String(sub.status || '').toLowerCase())}">${escape(String(sub.status || 'planned').toLowerCase())}</span><span>${escape(String(sub.title || '').slice(0, 140))}<br><span class="muted small">${escape(label(sub.assignedModelId || sub.modelId))}</span></span></li>`).join('')}</ul></div>` : ''}
      <div class="section"><h3>Recent activity</h3>${events.length ? `<ul class="status-list">${events.map(event => `<li><span class="muted small">${escape(clock(event.createdAt))}</span><span class="small">${escape(String(event.summary || '').slice(0, 200))}</span></li>`).join('')}</ul>` : '<p class="muted small">Nothing yet.</p>'}</div>
      <p class="small muted path">Folder: ${escape(row.folder || 'not set')}</p>`;
  }
  function usagePanel() {
    const plans = state.orchestra?.plans || [];
    const usage = state.orchestra?.aiUsage || {};
    const seconds = ms => Number.isFinite(ms) ? (ms >= 60000 ? Math.round(ms / 60000) + ' min' : Math.round(ms / 1000) + ' s') : '—';
    const tokens = row => Number.isFinite(row.outputTokens) ? `${row.outputTokens.toLocaleString()} out` : 'not reported';
    const rows = list => (list || []).map(row => `<span>${escape(row.model)}</span><span class="v">${row.calls} · ${seconds(row.durationMs)} · ${escape(tokens(row))}</span>`).join('');
    return `<div class="section"><h3>Quota left</h3>${plans.length ? plans.map(plan => `<div class="member"><div class="info"><div class="label">${escape(plan.label)}</div><div class="meta">${plan.readable && Number.isFinite(plan.leftPct) ? plan.leftPct + '% left' : escape(plan.reason || 'Not reported by this provider')}</div>${plan.readable && Number.isFinite(plan.leftPct) ? `<div class="bar${plan.leftPct < 20 ? ' low' : ''}"><span style="width:${Math.max(0, Math.min(100, plan.leftPct))}%"></span></div>` : ''}</div></div>`).join('') : '<p class="muted small">No provider reports quota.</p>'}</div>
      <div class="section"><h3>Lead calls</h3><div class="kv">${rows(usage.orchestrator) || '<span class="muted small">None yet.</span>'}</div></div>
      <div class="section"><h3>Crew work</h3><div class="kv">${rows(usage.workers) || '<span class="muted small">None yet.</span>'}</div></div>
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
    return `<div class="section"><h3>Not yet saved in Git</h3><p class="explain">Files the crew or you changed since the last commit on <b>${escape(local.branch || 'the current branch')}</b>.</p>
      ${changes.length ? `<ul class="status-list">${list}</ul>` : '<p class="small">Nothing changed. The folder matches the last commit.</p>'}
      ${Number.isFinite(local.uncommitted) && local.uncommitted > changes.length ? `<p class="small muted">Showing ${changes.length} of ${local.uncommitted}.</p>` : ''}</div>
      <p class="small muted path">${escape(row.folder)}${entry.checkedAt ? ' · checked ' + escape(clock(entry.checkedAt)) : ''}</p>
      <button type="button" class="btn btn-small" data-action="repo-refresh">Check again</button>`;
  }
  async function loadRepo(id, force = false) {
    if (state.repo[id]?.loading) return;
    state.repo[id] = { loading: true, ...(state.repo[id] || {}) };
    try {
      const result = await getJson('/api/repository?projectId=' + encodeURIComponent(id) + (force ? '&refresh=1' : ''));
      state.repo[id] = { ...result.repository };
    } catch (error) { state.repo[id] = { error: error.message }; }
    state.sideSig = ''; renderSide();
  }
  function settingsPanel() {
    const row = project();
    const dark = currentTheme() === 'dark';
    const mode = state.orchestra?.mode;
    return `${row ? `<div class="section"><h3>This project</h3><form id="folder-form" class="stack"><label class="small" for="folder-input">Folder on your computer</label>
        <input id="folder-input" name="folder" value="${escape(row.folder || '')}" placeholder="C:\\Projects\\my-website" maxlength="1000">
        <p class="error" id="folder-error" role="alert"></p><button type="submit" class="btn btn-small">Save folder</button></form></div>` : ''}
      <div class="section"><h3>New projects</h3><form id="root-form" class="stack"><label class="small" for="root-input">Default folder for new projects</label>
        <input id="root-input" name="root" value="${escape(state.settings?.projectsRoot || '')}" placeholder="C:\\Projects" maxlength="1000">
        <p class="explain">When you leave the folder empty on a new project, Planwharf creates it here.</p>
        <p class="error" id="root-error" role="alert"></p><button type="submit" class="btn btn-small">Save</button></form></div>
      <div class="section"><h3>Approvals</h3><p class="explain">${mode === 'auto' ? 'The lead may start approved work without asking each time.' : 'The lead asks you before each piece of work starts.'}</p>
        <button type="button" class="btn btn-small" data-action="toggle-mode">${mode === 'auto' ? 'Ask me before each step' : 'Let the lead start approved work'}</button></div>
      <div class="section"><h3>Look</h3><button type="button" class="btn btn-small" data-action="theme">${dark ? 'Use light theme' : 'Use dark theme'}</button></div>
      <p class="small muted">Providers, accounts, model policies and the Reviewer and Supervisor roles are in the <a href="/advanced#orchestrator">Advanced view</a>.</p>`;
  }
  const currentTheme = () => document.documentElement.dataset.theme || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light');
  async function saveFolder(form) {
    const row = project(); if (!row) return;
    const error = $('folder-error'); error.textContent = '';
    const folder = String(new FormData(form).get('folder') || '').trim();
    if (!folder) { error.textContent = 'Enter a folder.'; return; }
    try {
      const { version } = await getJson('/api/plan');
      await post('/api/place', { version, project: { id: row.id, folder } });
      delete state.repo[row.id]; state.sideSig = '';
      toast('Folder saved.'); await refresh({ tick: false });
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
    await refresh({ tick: false });
  }
  function openPanel(name) { state.panel = state.panel === name ? null : name; store.set('panel', state.panel); render(); }

  // Short commands that only open a panel or change the team run here, without a model call.
  function localCommand(text) {
    const clean = text.trim().toLowerCase().replace(/[.!?]+$/, '');
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
      state.confirm = { text: `Put ${model.label} on the bench? It will not build, verify or lead until you bring it back.`, run: () => setRoles(model.id, { orchestrator: false, worker: false, verifier: false }) };
      render(); return true;
    }
    return false;
  }
  async function setRoles(id, roles) { await act(() => post('/api/models/roles', { id, roles }), 'Team updated.'); }
  async function chooseLead(id) {
    if (!id) return;
    const model = models().find(item => item.id === id);
    await act(async () => { if (model && !model.roles?.orchestrator) await post('/api/models/roles', { id, roles: { orchestrator: true } }); return post('/api/orchestrator/lead', { id }); }, `${model?.label || 'Lead'} is now the lead.`);
  }
  async function send(text, extra = {}) {
    const row = project();
    if (!row) { toast('Create or select a project first.'); return; }
    if (!lead()) { toast('Choose a lead first (Team panel).'); openPanel('team'); return; }
    state.sending = true; state.pendingText = text; state.pendingProject = row.id; state.pendingSince = Date.now(); state.threadSig = '';
    $('send').disabled = true; render();
    const poll = setInterval(async () => { try { const snap = await getJson('/api/orchestrator?readOnly=1'); state.orchestra = { ...state.orchestra, thinking: snap.thinking }; renderThread(); } catch {} }, 900);
    try {
      const body = await post('/api/orchestrator/ask', { question: text, projectId: row.id, chatSessionId: sessionFor(row.id), ...extra });
      if (body.snapshot) state.orchestra = { ...state.orchestra, ...body.snapshot };
      if (body.asks) state.orchestra.asks = body.asks;
    } catch (error) { toast(error.message); }
    finally { clearInterval(poll); state.sending = false; state.orchestra && (state.orchestra.thinking = null); $('send').disabled = false; state.threadSig = ''; await refresh({ tick: false }); }
  }
  async function autonomyOn() {
    const row = project(); if (!row) return;
    const missing = readiness(row);
    if (missing.length) { toast(missing[0].text); return; }
    await act(async () => {
      if (state.orchestra.mode !== 'auto') await post('/api/orchestrator/mode', { mode: 'auto' });
      return post('/api/orchestrator/autonomy', { projectId: row.id, enabled: true });
    }, 'The crew is on it. Updates appear here.');
  }
  async function autonomyOff() {
    const row = project(); if (!row) return;
    await act(async () => {
      const snap = await post('/api/orchestrator/autonomy', { projectId: row.id, enabled: false });
      const current = (snap.projects || []).find(item => item.id === row.id);
      return current?.canStop ? post('/api/orchestrator/stop', { projectId: row.id }) : snap;
    }, 'The crew stopped. Work so far is saved.');
  }
  async function createProject(form) {
    const error = $('project-error'); error.textContent = '';
    const data = Object.fromEntries(new FormData(form));
    const folder = String(data.folder || '').trim();
    try {
      const [{ version }, settings] = await Promise.all([getJson('/api/plan'), getJson('/api/settings').catch(() => ({}))]);
      if (!folder && !settings.projectsRoot) throw new Error('Enter the folder for this project, for example C:\\Projects\\my-website.');
      const created = await post('/api/projects', { version, project: { name: String(data.name).trim(), direction: String(data.direction).trim(), ...(folder ? { folder } : {}) }, createFolder: !folder });
      const added = created.plan?.projects?.at(-1)?.project?.id;
      $('project-dialog').close(); form.reset();
      if (added) { state.projectId = added; store.set('project', added); }
      toast('Project created.');
      await refresh();
    } catch (failure) { error.textContent = failure.message; }
  }

  // ---------- events ----------
  document.addEventListener('click', event => {
    const target = event.target.closest('button, a');
    if (!target) return;
    if (target.dataset.project) { state.projectId = target.dataset.project; store.set('project', state.projectId); state.threadSig = ''; state.confirm = null; $('projects-panel').classList.remove('open'); render(); return; }
    if (target.dataset.panel) { if (target.classList.contains('tab')) openPanel(target.dataset.panel); else { state.panel = target.dataset.panel; store.set('panel', state.panel); render(); } return; }
    if (target.id === 'side-close') { state.panel = null; store.set('panel', null); render(); return; }
    if (target.dataset.openSide) { $(target.dataset.openSide).classList.add('open'); return; }
    if (target.dataset.closeSide) { $(target.dataset.closeSide).classList.remove('open'); return; }
    if (target.id === 'new-project' || target.dataset.action === 'new-project') { $('project-error').textContent = ''; $('project-dialog').showModal(); return; }
    if (target.id === 'project-cancel') { $('project-dialog').close(); return; }
    if (target.dataset.roleToggle) { const model = models().find(item => item.id === target.dataset.roleToggle); if (model) setRoles(model.id, { [target.dataset.role]: !(model.roles?.[target.dataset.role] === true) }); return; }
    if (target.dataset.bench) { const model = models().find(item => item.id === target.dataset.bench); if (model) setRoles(model.id, { orchestrator: false, worker: false, verifier: false }); return; }
    const action = target.dataset.action;
    if (action === 'autonomy-on') autonomyOn();
    else if (action === 'autonomy-off') autonomyOff();
    else if (action === 'stop') act(() => post('/api/orchestrator/stop', { projectId: state.projectId }), 'Work stopped and saved.');
    else if (action === 'theme') { document.documentElement.dataset.theme = currentTheme() === 'dark' ? 'light' : 'dark'; store.set('theme', document.documentElement.dataset.theme); state.sideSig = ''; renderSide(); }
    else if (action === 'toggle-mode') act(() => post('/api/orchestrator/mode', { mode: state.orchestra?.mode === 'auto' ? 'manual' : 'auto' }), 'Saved.');
    else if (action === 'repo-refresh') loadRepo(state.projectId, true);
    else if (action === 'grill') send('Grill me', { grillKind: 'me' });
    else if (action === 'confirm-yes') { const run = state.confirm?.run; state.confirm = null; state.threadSig = ''; if (run) run(); else render(); }
    else if (action === 'confirm-no') { state.confirm = null; state.threadSig = ''; render(); }
  });
  document.addEventListener('change', event => { if (event.target.matches('[data-lead-select]')) chooseLead(event.target.value); });
  $('composer').addEventListener('submit', event => {
    event.preventDefault();
    const input = $('composer-input'); const text = input.value.trim();
    if (!text || state.sending) return;
    input.value = ''; input.style.height = '';
    if (!localCommand(text)) send(text);
  });
  $('composer-input').addEventListener('keydown', event => { if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); $('composer').requestSubmit(); } });
  $('composer-input').addEventListener('input', event => { const el = event.target; el.style.height = 'auto'; el.style.height = Math.min(200, el.scrollHeight) + 'px'; });
  $('project-form').addEventListener('submit', event => { event.preventDefault(); createProject(event.target); });
  document.addEventListener('submit', event => {
    if (event.target.id === 'folder-form') { event.preventDefault(); saveFolder(event.target); }
    else if (event.target.id === 'root-form') { event.preventDefault(); saveRoot(event.target); }
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && state.panel && !$('project-dialog').open) { state.panel = null; store.set('panel', null); render(); } });

  const theme = store.get('theme', null);
  if (theme === 'dark' || theme === 'light') document.documentElement.dataset.theme = theme;
  getJson('/api/settings').then(value => { state.settings = value; }).catch(() => {});
  refresh();
  setInterval(() => { if (!document.hidden && !state.sending) refresh(); }, 4000);
})();
