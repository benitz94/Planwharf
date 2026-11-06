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
    sending: false, repo: {}, pendingText: '', pendingSince: 0, sessions: store.get('sessions', {}), threadSig: '', sideSig: '', confirm: null, loaded: false,
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
  const ROLE_KEYS = ['orchestrator', 'worker', 'verifier', 'reviewer', 'supervisor'];
  const onBench = model => !ROLE_KEYS.some(role => model.roles?.[role]);
  const NO_ROLES = Object.fromEntries(ROLE_KEYS.map(role => [role, false]));
  // One colour per provider, so the same company is recognisable everywhere.
  const PROVIDER_COLOURS = { codex: '#10a37f', openai: '#10a37f', claude: '#d97757', anthropic: '#d97757', grok: '#6b7280', xai: '#6b7280', antigravity: '#4285f4', gemini: '#4285f4', google: '#4285f4', api: '#8b5cf6' };
  const providerOf = id => models().find(model => model.id === id)?.provider || '';
  const providerDot = provider => `<span class="prov" style="background:${PROVIDER_COLOURS[String(provider).toLowerCase()] || '#9ca3af'}" title="${escape(provider || 'unknown provider')}" aria-hidden="true"></span>`;
  const VERIFY_PREFIX = 'Verify the work independently';
  const duration = ms => !Number.isFinite(ms) || ms < 0 ? '' : ms < 60000 ? Math.max(1, Math.round(ms / 1000)) + ' s' : ms < 3600000 ? Math.round(ms / 60000) + ' min' : (ms / 3600000).toFixed(1) + ' h';
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

  // The current piece of work: proposal, live crew, or the result.
  function crewCard(row) {
    const mega = row.megaTask;
    const subs = row.subTasks || [];
    if (!mega || !subs.length) return '';
    const status = String(mega.status || '').toUpperCase();
    const done = subs.filter(sub => sub.status === 'COMPLETE').length;
    const pct = Math.round(done / subs.length * 100);
    const step = sub => {
      const verify = String(sub.title || '').startsWith(VERIFY_PREFIX);
      const role = sub.type === 'AUDIT' ? 'Review' : sub.type === 'RESEARCH' ? 'Research' : verify ? 'Verify' : 'Build';
      const s = String(sub.status || 'PENDING').toUpperCase();
      const label = { PENDING: 'waiting', RUNNING: 'working', COMPLETE: 'done', FAILED: 'failed', BLOCKED: 'blocked', PAUSED: 'paused' }[s] || s.toLowerCase();
      const title = verify ? 'Check the work and fix what fails' : String(sub.title || '').slice(0, 120);
      return `<li class="step ${s.toLowerCase()}">${providerDot(providerOf(sub.assignedModelId))}<span class="step-main"><b>${escape(role)}</b> · ${escape(modelLabel(sub.assignedModelId) || sub.assignedModelId || 'not assigned')}<br><span class="muted small">${escape(title)}</span></span><span class="badge ${s.toLowerCase()}">${escape(label)}</span></li>`;
    };
    const steps = `<ol class="crew-steps">${subs.map(step).join('')}</ol>`;
    const bar = `<div class="progress" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${pct}" aria-label="Steps done"><span style="width:${pct}%"></span></div><p class="small muted">${done} of ${subs.length} steps done</p>`;
    if (status === 'READY_FOR_APPROVAL') {
      const auto = autonomyOf(row.id)?.enabled;
      return `<div class="card crew"><h3>The lead proposes this work</h3><p>${escape(mega.title || '')}</p>${steps}
        ${auto ? '<p class="small muted">The crew approves this by itself.</p>' : '<div class="row"><button type="button" class="btn btn-primary" data-action="approve">Approve and start</button><span class="small muted">Or tell the lead what to change.</span></div>'}</div>`;
    }
    if (status === 'COMPLETE') {
      const took = duration(Date.parse(mega.completedAt) - Date.parse(mega.approvedAt || mega.createdAt));
      const repo = state.repo[row.id];
      if (!repo && row.folder) loadRepo(row.id);
      const changed = repo?.local?.git ? (Number.isFinite(repo.local.uncommitted) ? repo.local.uncommitted : (repo.local.changes || []).length) : null;
      return `<div class="card crew done"><h3>✓ Work finished</h3><p>${escape(mega.title || '')}</p>
        <div class="facts"><span><b>${done}/${subs.length}</b> steps done</span>${took ? `<span><b>${took}</b></span>` : ''}<span>${changed == null ? (repo && !repo.loading && !repo.local ? 'Changed files: unknown' : 'Counting changed files…') : `<b>${changed}</b> files not yet committed`}</span></div>
        ${steps}<p class="small">Try the result in the project folder. Nothing was committed or published.</p>
        <div class="row"><button type="button" class="btn btn-small" data-panel="files">See changed files</button><button type="button" class="btn btn-small" data-panel="usage">See usage</button></div></div>`;
    }
    if (['FAILED', 'BLOCKED', 'PAUSED', 'STOPPED', 'CHECKPOINTED'].includes(status) || row.canResume) {
      return `<div class="card crew attention"><h3>Work stopped</h3><p>${escape(row.next || row.label || mega.nextAction || '')}</p>${bar}${steps}
        ${row.canResume ? '<div class="row"><button type="button" class="btn btn-primary" data-action="resume">Resume</button></div>' : ''}</div>`;
    }
    const since = duration(Date.now() - Date.parse(mega.approvedAt || mega.createdAt));
    // Plain-language activity of the model working now; the raw log only when nothing else is readable.
    const running = (row.dispatch || []).find(part => part && part.alive) || null;
    const friendly = (running?.activitySession?.events || []).map(event => event.friendly).filter(Boolean).slice(-6);
    const tail = friendly.length ? friendly.join('\n') : String(row.workerTail || '').trim().split('\n').slice(-6).join('\n');
    const who = modelLabel(running?.modelId) || row.workerLabel || 'the crew';
    return `<div class="card crew live"><h3><span class="pulse" aria-hidden="true"></span> Crew at work${since ? ` · ${since}` : ''}</h3><p>${escape(mega.title || '')}</p>${bar}${steps}
      ${tail ? `<details class="preview" open><summary>What ${escape(who)} is doing</summary><pre>${escape(tail)}</pre></details>` : ''}</div>`;
  }

  // Deterministic warnings from the Supervisor checks (no model call): stalled, failed or conflicting work.
  const alertsFor = id => (state.orchestra?.supervisor?.projects || []).find(entry => entry.projectId === id)?.alerts?.filter(alert => alert.active !== false && !alert.ignored) || [];
  function alertsCard(row) {
    const alerts = alertsFor(row.id);
    if (!alerts.length) return '';
    return `<div class="card crew attention" role="alert"><h3>⚠ Something needs a look</h3>${alerts.map(alert => `<div class="alert-row"><p><b>${escape(alert.title)}</b><br><span class="small">${escape(alert.message || '')}</span></p><button type="button" class="btn btn-small" data-ignore-alert="${escape(alert.id)}">Ignore</button></div>`).join('')}
      <p class="small muted">Planwharf checks the work itself; this is a reason to look, not proof that something broke.</p></div>`;
  }

  function renderThread() {
    const thread = $('thread');
    const row = project();
    const asks = (state.orchestra?.asks || []).filter(item => row ? item.projectId === row.id : item.projectId == null);
    const events = row ? (state.orchestra?.execution?.timeline || []).filter(event => event.projectId === row.id && SHOWN_EVENTS.test(event.type || '')).slice(-60) : [];
    const live = state.orchestra?.thinking;
    const pendingHere = state.sending && state.pendingProject === state.projectId;
    const sig = JSON.stringify([state.projectId, asks.map(item => [item.at, item.answer?.length, item.ms]), events.map(event => event.id || event.createdAt), pendingHere, pendingHere && [live?.thinking?.length, live?.draft?.length], state.confirm, Boolean(lead()), row?.megaTask?.status, (row?.subTasks || []).map(sub => sub.status + sub.assignedModelId).join(), row?.workerTail?.length, (row?.dispatch || []).map(part => part?.activitySession?.events?.length).join(), row?.canResume, Math.floor(Date.now() / 60000), row && state.repo[row.id]?.local?.uncommitted, row && alertsFor(row.id).map(alert => alert.id).join(), models().map(model => model.roles?.worker).join(), row?.folder, (state.orchestra?.projects || []).length, projectStatus(row).text]);
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
    if (row) html += crewCard(row) + alertsCard(row);
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
    // One clear next action at a time.
    else if (row.status === 'needs_approval') parts.push('<button type="button" class="btn btn-primary" data-action="approve">Approve and start</button><span class="status-text">The lead proposed some work. Approve it, or tell the lead what to change.</span>');
    else if (row.canStop) parts.push('<button type="button" class="btn btn-danger" data-action="stop">Stop work</button><span class="status-text">Work in progress. You can keep chatting with the lead.</span>');
    else if (row.canResume) parts.push('<button type="button" class="btn btn-primary" data-action="resume">Resume</button><span class="status-text">Work was stopped. Resume continues from the saved point.</span>');
    else {
      parts.push(`<button type="button" class="btn btn-primary" data-action="autonomy-on" ${missing.length ? 'disabled' : ''} title="${escape(missing.map(item => item.text).join(' ') || 'Plan, build, verify and stop when the goal is reached')}">Let the crew do it</button>`);
      parts.push(`<span class="status-text">${escape(missing.length ? missing[0].text : 'Ready. The crew will plan, build, check and stop when the goal is reached.')}</span>`);
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
      return `<button type="button" class="chip" data-role-toggle="${escape(model.id)}" data-role="${role}" aria-pressed="${model.roles?.[role] === true}" ${eligible ? '' : 'disabled'} title="${escape(help)}">${label}</button>`;
    };
    const optional = ['reviewer', 'supervisor'].filter(role => model.roleEligibility?.[role] !== false);
    return `<div class="member"><div class="info"><div class="label">${providerDot(model.provider)}${escape(model.label)}${model.id === state.orchestra?.orchestratorId ? ' <span class="badge running">Lead</span>' : ''}</div>
      <div class="meta">${escape(model.plan || model.provider || '')}${unavailable ? ' · not available now' : ''}${left != null ? ` · ${left}% quota left` : ''}</div>
      ${left != null ? `<div class="bar${left < 20 ? ' low' : ''}"><span style="width:${Math.max(0, Math.min(100, left))}%"></span></div>` : ''}</div>
      <div class="chips">${chip('worker', 'Build', 'Writes the code and the tests')}${chip('verifier', 'Verify', 'Checks the work and fixes what fails')}${optional.includes('reviewer') ? chip('reviewer', 'Review', 'Optional: reads the work and reports problems, without changing files') : ''}${optional.includes('supervisor') ? chip('supervisor', 'Watch', 'Optional: an AI that watches running work and warns you') : ''}
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
      <div class="section"><h3>On the team</h3><p class="explain"><b>Build</b> writes code and tests. <b>Verify</b> checks the work and fixes it, ideally a model from another company. <b>Review</b> and <b>Watch</b> are optional.</p>
        ${active.length ? active.map(memberHtml).join('') : '<p class="muted small">Nobody yet. Bring models in from the bench.</p>'}</div>
      <div class="section"><h3>Bench</h3><p class="explain">Available but not used. Press a role to bring a model in.</p>
        ${bench.length ? bench.map(memberHtml).join('') : '<p class="muted small">The bench is empty.</p>'}</div>
      <div class="section"><h3>AI tools</h3><p class="explain">Planwharf finds the models of the AI tools signed in on this computer. A tool that is off is never used.</p>
        ${(state.orchestra?.providers || []).map(provider => `<div class="member"><div class="info"><div class="label">${providerDot(provider.id)}${escape(provider.label || provider.id)}</div><div class="meta">${(provider.models || []).length} models</div></div>
          <div class="chips"><button type="button" class="chip" data-provider-toggle="${escape(provider.id)}" aria-pressed="${provider.enabled !== false}">${provider.enabled !== false ? 'On' : 'Off'}</button></div></div>`).join('')}
        <button type="button" class="btn btn-small" data-action="refresh-models">Look for models again</button></div>
      <div class="section"><h3>Connect a model by API</h3><p class="explain">For a model you reach through an API, such as DeepSeek or Kimi. The key stays in an environment variable on this computer; Planwharf stores only the variable's name.</p>
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
    state.sideSig = ''; renderSide(); renderThread();
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
      <div class="section"><h3>Move a project</h3><p class="explain">Save a project to a file, or open one saved on another computer. Code stays in the project folder; the file holds the plan, history and saved points.</p>
        <div class="row">${row ? `<a class="btn btn-small" href="/api/project-export?projectId=${encodeURIComponent(row.id)}" download="${escape(row.name.replace(/[^\w.-]+/g, '-'))}-planwharf.json">Save ${escape(row.name)} to a file</a>` : ''}
        <label class="btn btn-small">Open a project file<input type="file" id="import-file" accept="application/json,.json" hidden></label></div>
        <p class="error" id="import-error" role="alert"></p></div>
      ${updatesSection()}
      ${row ? `<div class="section"><h3>Remove project</h3><p class="explain">Removes ${escape(row.name)} from Planwharf. The folder and its files stay on your computer.</p>${armedButton('remove-project', 'Remove project', 'Yes, remove it')}</div>` : ''}
      <div class="section"><h3>Quit</h3><p class="explain">Unfinished work is saved and can be resumed next time.</p>${armedButton('quit', 'Quit Planwharf', 'Yes, quit now')}</div>`;
  }
  // Dangerous buttons need a second press; nothing opens a browser dialog.
  const armedButton = (action, label, confirmLabel) => state.armed === action
    ? `<div class="row"><button type="button" class="btn btn-danger btn-small" data-action="${action}">${confirmLabel}</button><button type="button" class="btn btn-small" data-action="disarm">Cancel</button></div>`
    : `<button type="button" class="btn btn-small" data-arm="${action}">${label}</button>`;
  function updatesSection() {
    const u = state.updates;
    if (!u) return '<div class="section"><h3>Updates</h3><button type="button" class="btn btn-small" data-action="check-updates">Check for updates</button></div>';
    const busy = ['downloading', 'checkpointing', 'restarting', 'checking'].includes(u.state);
    const line = busy ? { downloading: 'Downloading the update…', checkpointing: 'Saving unfinished work…', restarting: 'Restarting…', checking: 'Checking…' }[u.state]
      : u.state === 'error' ? 'Could not check: ' + (u.reason || 'unknown error')
      : u.available ? `Planwharf ${u.latestVersion} is available${u.currentVersion ? ` (you have ${u.currentVersion})` : ''}.`
      : u.currentVersion ? `Planwharf ${u.currentVersion} is up to date.` : 'Version unknown in a source checkout.';
    const install = u.available && u.canInstall && !busy
      ? armedButton('install-update', 'Update and restart', state.updateNeedsConfirm ? 'Work is running: save it and update' : 'Yes, update now') : '';
    const download = u.available && !u.canInstall && u.releaseUrl ? `<a href="${escape(u.releaseUrl)}" target="_blank" rel="noopener noreferrer">Download it</a>` : '';
    return `<div class="section"><h3>Updates</h3><p class="explain">${escape(line)}</p><div class="row">${install}${download}<button type="button" class="btn btn-small" data-action="check-updates" ${busy ? 'disabled' : ''}>Check again</button></div></div>`;
  }
  async function loadUpdates(check = false) {
    try { state.updates = await (check ? post('/api/app/updates/check', {}) : getJson('/api/app/updates')); } catch { /* Offline checks never interrupt work. */ }
    renderUpdateNotice(); state.sideSig = ''; renderSide();
  }
  function renderUpdateNotice() {
    const u = state.updates;
    $('update-notice').innerHTML = u?.available ? `<button type="button" class="link-quiet" data-panel="settings">Update available: ${escape(u.latestVersion || '')}</button>` : '';
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
    state.sideSig = ''; await refresh({ tick: false });
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
      state.confirm = { text: `Put ${model.label} on the bench? It will have no role until you bring it back.`, run: () => setRoles(model.id, NO_ROLES) };
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
    if (target.dataset.ignoreAlert) { act(() => post('/api/supervisor/ignore', { projectId: state.projectId, alertId: target.dataset.ignoreAlert }), 'Warning ignored.'); return; }
    if (target.dataset.arm) { state.armed = target.dataset.arm; state.sideSig = ''; renderSide(); return; }
    if (target.dataset.roleToggle) { const model = models().find(item => item.id === target.dataset.roleToggle); if (model) setRoles(model.id, { [target.dataset.role]: !(model.roles?.[target.dataset.role] === true) }); return; }
    if (target.dataset.bench) { const model = models().find(item => item.id === target.dataset.bench); if (model) setRoles(model.id, NO_ROLES); return; }
    const action = target.dataset.action;
    if (action === 'autonomy-on') autonomyOn();
    else if (action === 'autonomy-off') autonomyOff();
    else if (action === 'stop') act(() => post('/api/orchestrator/stop', { projectId: state.projectId }), 'Work stopped and saved.');
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
    else if (action === 'quit') { state.armed = null; post('/api/app/quit', {}).then(() => { document.body.innerHTML = '<p style="padding:24px">Planwharf has closed. You can close this window.</p>'; try { window.close(); } catch {} }).catch(error => toast(error.message)); }
    else if (action === 'install-update') installUpdate();
    else if (action === 'grill') send('Grill me', { grillKind: 'me' });
    else if (action === 'confirm-yes') { const run = state.confirm?.run; state.confirm = null; state.threadSig = ''; if (run) run(); else render(); }
    else if (action === 'confirm-no') { state.confirm = null; state.threadSig = ''; render(); }
  });
  document.addEventListener('change', event => {
    if (event.target.matches('[data-lead-select]')) chooseLead(event.target.value);
    else if (event.target.id === 'import-file' && event.target.files?.[0]) importProject(event.target.files[0]);
  });
  $('composer').addEventListener('submit', event => {
    event.preventDefault();
    const input = $('composer-input'); const text = input.value.trim();
    if (!text || state.sending) return;
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
  loadUpdates(); setInterval(loadUpdates, 60000);

  const theme = store.get('theme', null);
  if (theme === 'dark' || theme === 'light') document.documentElement.dataset.theme = theme;
  getJson('/api/settings').then(value => { state.settings = value; }).catch(() => {});
  refresh();
  setInterval(() => { if (!document.hidden && !state.sending) refresh(); }, 4000);
})();
