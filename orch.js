'use strict';
// Shared pure presentation contract; no backend authority or provider calls.
(() => {
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const roles = ['orchestrator', 'worker', 'verifier', 'reviewer', 'supervisor'];
  const roleAllowed = (model, role) => role === 'supervisor' ? model.roles?.supervisor === true && model.roleEligibility?.supervisor === true : model.roles?.[role] === true;
  function evidenceText(session) {
    const evidence = session?.technicalEvidence;
    if (!Array.isArray(evidence?.entries) || !evidence.entries.length) return 'Source-specific original command/output evidence is not retained in this view. No original output is reconstructed from Friendly descriptions.';
    return evidence.entries.slice(-32).map(entry => [
      `${entry.protocol || 'unknown protocol'} · ${entry.provenance || 'unknown provenance'}${entry.eventType ? ' · ' + entry.eventType : ''}`,
      typeof entry.command === 'string' ? 'Original command: ' + entry.command.slice(0, 4096) : '',
      typeof entry.output === 'string' ? 'Original output:\n' + entry.output.slice(0, 8192) : '',
      Number.isInteger(entry.exitCode) ? 'Exit code: ' + entry.exitCode : '',
      entry.sourceTimestamp ? 'Recorded timestamp: ' + entry.sourceTimestamp : ''
    ].filter(Boolean).join('\n')).join('\n\n') + (evidence.truncated ? '\n\nEarlier evidence was truncated by the display bound.' : '') + (evidence.warnings || []).slice(0, 8).map(warning => '\n' + warning).join('');
  }
  function sessionHtml(session, mode = 'friendly') {
    if (!session) return '<p class="muted">No recorded activity session is available for this selection.</p>';
    const events = (Array.isArray(session.events) ? session.events : []).slice(-50);
    const title = `${session.role || 'Unknown role'} · ${session.provider || 'unknown provider'}/${session.model || 'unknown model'}`;
    const details = { projectId: session.projectId, jobId: session.jobId, megaTaskId: session.megaTaskId, subTaskId: session.subTaskId, attemptId: session.attemptId,
      modelId: session.modelId, provider: session.provider, adapter: session.adapter, model: session.model, role: session.role,
      processState: session.processState, state: session.state, startedAt: session.startedAt, completedAt: session.completedAt,
      validatedResultStatus: session.validatedResultStatus, recordedResultStatus: session.recordedResultStatus, exitCode: session.exitCode,
      capabilities: { expectedFormat: session.capabilities?.expectedFormat, detailedEventsExpected: session.capabilities?.detailedEventsExpected, liveDeliveryVerified: session.capabilities?.liveDeliveryVerified === true } };
    const state = session.validatedResultStatus === 'COMPLETE' ? 'Completed · backend-validated result' : `${session.state || 'unknown'} · ${session.processState || 'unknown process state'}`;
    return `<header class="activity-heading"><h3>${escape(title)}</h3><span class="badge">${escape(state)}</span></header><p class="muted">${escape(session.attemptId || 'Attempt identity not retained')} · ${escape(session.modelId || 'Configured model identity not retained')}</p><p class="activity-validation">${session.validatedResultStatus ? `Backend result: ${escape(session.validatedResultStatus)}` : 'Result not validated. Process termination and reported test output do not prove completion.'}</p>${(session.warnings || []).slice(0, 8).map(warning => `<p class="muted">${escape(String(warning).slice(0, 400))}</p>`).join('')}${mode === 'technical' ? `<details><summary>Assignment metadata</summary><pre class="activity-technical">${escape(JSON.stringify(details, null, 2))}</pre></details>` : ''}<ol class="activity-timeline">${events.map(event => {
      const category = ['read', 'working', 'build', 'test', 'warning', 'review', 'report'].includes(event.classification) ? event.classification.toUpperCase() : 'UNKNOWN';
      const provenance = ['observed', 'reported'].includes(event.provenance) ? event.provenance : 'unknown';
      const technical = { protocol: event.protocol, state: event.state, eventType: event.technical?.eventType, tool: event.technical?.tool, exitCode: event.technical?.exitCode, validated: event.validated === true };
      return `<li class="activity-event"><span class="activity-tag">[${category}]</span> <span>${escape(String(event.friendly || 'Unclassified activity').slice(0, 260))}</span><small>${provenance === 'reported' ? 'Worker reported' : provenance === 'observed' ? 'Observed event' : 'Unclassified evidence'}${event.timestamp ? ' · ' + escape(event.timestamp) : ''}</small>${mode === 'technical' ? `<pre>${escape(JSON.stringify(technical, null, 2))}</pre>` : ''}</li>`;
    }).join('') || '<li class="muted">No readable event evidence retained. No activity is inferred.</li>'}</ol>${mode === 'technical' ? '<p class="muted">Only retained sanitized metadata is shown. Raw source logs remain in their existing local storage; missing event timestamps are not synthesized.</p>' : ''}`;
  }
  const api = { sessionHtml, evidenceText, roles, roleAllowed, escape };
  globalThis.PlanwharfWorkspaceUI = api;
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
})();

'use strict';
(() => {
  if (typeof document === 'undefined') return;
  const embedded = document.documentElement.hasAttribute?.('data-unified-app') || false;
  const $ = id => document.getElementById(embedded && id !== 'toast' ? `dashboard-${id}` : id);
  const escape = value => String(value).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const themeKey = 'planwharf_theme';
  const policyDrafts = new Map();
  let selectedPolicyProviderId = null;
  const usageNotices = globalThis.PlanwharfModelPolicyUI?.createAlerts();
  let orchestra = { mode: 'manual', models: [], projects: [], asks: [], fleet: [] };
  let version = null;
  let executionAvailable = false;
  let refreshGeneration = 0;
  let projectId = null;
  let toastTimer;
  let chatSig = '';
  // The reader's place in the chat survives re-renders and a recreated thread element.
  let chatScroll = { top: 0, nearBottom: true };
  let chatSettledSig = '';
  let catalogSearch = '';
  let searchControl;
  let modelRoleFilter = 'all';
  let roleFilterControl;
  let sending = false;
  let pendingText = '';
  let pendingProjectId = null;
  let pendingStarted = 0;
  let thinkTimer = null;
  let askAbort = null;
  let sendGen = 0;
  const researchReports = new Map();
  const workerSessionStartedAt = Date.now();
  let workspaceProject;
  let workspacePreferences = {};
  let activitySignature = '';
  let historyGeneration = 0;
  const historyPages = new Map();
  const historyLoading = new Set();
  const workspace = document.querySelector('.orch-board');
  const workspaceKey = id => 'planwharf_workspace_v1:' + encodeURIComponent(id || 'global');
  function saveWorkspace() {
    try { localStorage.setItem(workspaceKey(workspaceProject), JSON.stringify(workspacePreferences)); } catch { /* In-memory state still works. */ }
  }
  function workspacePanel(panel, restoring = false) {
    if (!['overview', 'sessions', 'supervisor'].includes(panel)) panel = 'overview';
    workspacePreferences.panelScroll ||= {};
    if (!restoring && workspace && workspacePreferences.panel) workspacePreferences.panelScroll[workspacePreferences.panel] = workspace.scrollTop;
    workspacePreferences.panel = panel;
    workspace?.querySelectorAll('[data-workspace-pane]').forEach(pane => { pane.hidden = pane.dataset.workspacePane !== panel; });
    workspace?.querySelectorAll('[data-workspace-panel]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.workspacePanel === panel)));
    saveWorkspace();
    if (workspace) workspace.scrollTop = Number(workspacePreferences.panelScroll[panel]) || 0;
    window.dispatchEvent(new CustomEvent('planwharf-workspace-panel', { detail: panel }));
  }
  window.PlanwharfSelectWorkspacePanel = workspacePanel;
  function syncWorkspaceProject() {
    if (workspaceProject === projectId) return;
    const input = $('orch-ask-input'), thread = $('orch-ask-thread');
    if (workspaceProject !== undefined) {
      workspacePreferences.draft = input?.value || '';
      workspacePreferences.chatScroll = thread?.scrollTop || 0;
      workspacePreferences.panelScroll ||= {};
      if (workspace) workspacePreferences.panelScroll[workspacePreferences.panel || 'overview'] = workspace.scrollTop;
      saveWorkspace();
    }
    workspaceProject = projectId;
    historyGeneration++;
    try { workspacePreferences = JSON.parse(localStorage.getItem(workspaceKey(projectId)) || '{}') || {}; } catch { workspacePreferences = {}; }
    if (typeof workspacePreferences !== 'object' || Array.isArray(workspacePreferences)) workspacePreferences = {};
    if (input) input.value = typeof workspacePreferences.draft === 'string' ? workspacePreferences.draft : '';
    chatSig = ''; activitySignature = '';
    if (thread) thread.dataset.restoreScroll = String(workspacePreferences.chatScroll || 0);
    workspacePanel(window.PlanwharfRequestedWorkspacePanel || workspacePreferences.panel || 'overview', true);
    window.PlanwharfRequestedWorkspacePanel = null;
  }
  function renderWorkspace() {
    if (!workspace) return;
    const row = (orchestra.projects || []).find(row => row.id === projectId);
    const role = workspace.querySelector('[data-workspace-role]');
    const selector = workspace.querySelector('[data-workspace-session]');
    const view = workspace.querySelector('[data-workspace-activity]');
    if (!selector || !view) return;
    const pagination = row?.activitySessionPagination;
    const pageSize = Number.isInteger(pagination?.pageSize) && pagination.pageSize > 0 ? Math.min(24, pagination.pageSize) : 24;
    const total = Number.isInteger(pagination?.total) && pagination.total >= 0 ? pagination.total : (row?.activitySessions || []).length;
    const storedOffset = Number.isInteger(workspacePreferences.offset) && workspacePreferences.offset > 0 ? workspacePreferences.offset : 0;
    const offset = pagination?.available === true ? Math.min(storedOffset, Math.max(0, Math.floor((total - 1) / pageSize) * pageSize)) : 0;
    workspacePreferences.offset = offset;
    const key = projectId + ':' + total + ':' + offset;
    if (offset && !historyPages.has(key) && !historyLoading.has(key)) {
      historyLoading.add(key);
      const targetProject = projectId, generation = historyGeneration;
      fetch('/api/orchestrator/activity-sessions?' + new URLSearchParams({ projectId: targetProject, offset: String(offset), limit: String(pageSize) }))
        .then(async response => { if (!response.ok) throw Error('Recorded assignment history is unavailable.'); return response.json(); })
        .then(page => {
          if (generation !== historyGeneration || targetProject !== projectId) return;
          if (!Array.isArray(page.sessions) || page.offset !== offset || page.sessions.some(session => session.projectId !== targetProject)) throw Error('Recorded assignment history does not match the selected project.');
          historyPages.set(key, page.sessions.slice(0, pageSize)); renderWorkspace();
        }).catch(error => { if (generation === historyGeneration && targetProject === projectId) notice(error.message); })
        .finally(() => { historyLoading.delete(key); });
    }
    const sessions = (offset ? historyPages.get(key) || [] : row?.activitySessions || []).filter(session => session.projectId === projectId).slice(0, 24);
    if (document.activeElement !== role) role.value = workspacePreferences.role || 'all';
    const visible = sessions.filter(session => role.value === 'all' || (session.role || 'unknown') === role.value);
    const selected = visible.find(session => session.id === (workspacePreferences.sessionsByPage?.[offset] || workspacePreferences.session)) || visible[0] || null;
    if (document.activeElement !== selector) {
      selector.innerHTML = visible.map(session => `<option value="${escape(session.id)}">${escape(session.role || 'Unknown role')} · ${escape(session.model || session.modelId || 'Unknown model')} · ${escape(session.state || 'unknown')}${session.historical ? ' · recorded' : ''}</option>`).join('') || '<option value="">No recorded assignments</option>';
      selector.value = selected?.id || '';
    }
    if (selected) workspacePreferences.session = selected.id;
    const history = workspace.querySelector('[data-workspace-history]');
    if (history) {
      history.hidden = pagination?.available !== true;
      history.querySelector('[data-history-previous]').disabled = offset === 0;
      history.querySelector('[data-history-next]').disabled = offset + pageSize >= total;
      history.querySelector('[data-history-label]').textContent = total ? `Assignments ${offset + 1}–${Math.min(offset + pageSize, total)} of ${total}` : 'No retained assignments';
    }
    const mode = workspacePreferences.mode === 'technical' ? 'technical' : 'friendly';
    workspace.querySelectorAll('[data-activity-mode]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.activityMode === mode)));
    workspace.querySelector('[data-workspace-feed]').textContent = !executionAvailable ? 'Updates unavailable. Retained evidence is shown; no current state is inferred.' : !row ? 'Select a project to inspect its assignments.' : `${visible.length} retained assignment${visible.length === 1 ? '' : 's'} for ${row.name}.`;
    const sig = JSON.stringify([selected, mode]);
    if (activitySignature !== sig) {
      const scroll = view.scrollTop;
      view.innerHTML = PlanwharfWorkspaceUI.sessionHtml(selected, mode);
      if (mode === 'technical' && selected) {
        const original = document.createElement('section'), title = document.createElement('h4'), output = document.createElement('pre');
        title.textContent = 'Sanitized original assignment evidence'; output.className = 'activity-technical'; output.dataset.workspaceOriginal = '';
        output.textContent = PlanwharfWorkspaceUI.evidenceText(selected); original.append(title, output);
        view.querySelector('.activity-timeline')?.before(original);
      }
      view.scrollTop = scroll;
      activitySignature = sig;
    }
  }
  window.PlanwharfInspectAssignment = attemptId => {
    const session = (orchestra.projects || []).find(row => row.id === projectId)?.activitySessions?.find(session => session.attemptId === attemptId);
    if (!session) { notice('No retained activity session is available for this assignment.'); return; }
    workspacePreferences.offset = 0;
    workspacePreferences.sessionsByPage ||= {};
    workspacePreferences.sessionsByPage[0] = session.id;
    workspacePreferences.session = session.id; workspacePreferences.role = 'all'; workspacePanel('sessions'); renderWorkspace();
  };
  workspace?.addEventListener('click', event => {
    const previous = event.target.closest('[data-history-previous]'), next = event.target.closest('[data-history-next]');
    if (previous || next) {
      const row = (orchestra.projects || []).find(row => row.id === projectId);
      if (row?.activitySessionPagination?.available !== true) return;
      const size = Math.min(24, row.activitySessionPagination.pageSize || 24);
      workspacePreferences.sessionsByPage ||= {};
      workspacePreferences.sessionsByPage[workspacePreferences.offset || 0] = workspacePreferences.session;
      workspacePreferences.offset = Math.max(0, (workspacePreferences.offset || 0) + (next ? size : -size));
      saveWorkspace(); renderWorkspace(); return;
    }
    const panel = event.target.closest('[data-workspace-panel]');
    if (panel) workspacePanel(panel.dataset.workspacePanel);
    const mode = event.target.closest('[data-activity-mode]');
    if (mode) { workspacePreferences.mode = mode.dataset.activityMode; saveWorkspace(); renderWorkspace(); }
    const focus = event.target.closest('[data-workspace-focus]');
    if (focus) {
      const shell = workspace.closest('.orch-shell'), enabled = shell.dataset.workspaceFocusState !== 'true';
      shell.dataset.workspaceFocusState = String(enabled); focus.setAttribute('aria-pressed', String(enabled)); focus.textContent = enabled ? 'Show conversation' : 'Focus activity';
      window.dispatchEvent(new Event('resize'));
    }
  });
  workspace?.addEventListener('change', event => {
    if (event.target.matches('[data-workspace-session]')) { workspacePreferences.session = event.target.value; workspacePreferences.sessionsByPage ||= {}; workspacePreferences.sessionsByPage[workspacePreferences.offset || 0] = event.target.value; }
    else if (event.target.matches('[data-workspace-role]')) workspacePreferences.role = event.target.value;
    else return;
    saveWorkspace(); renderWorkspace();
  });
  $('orch-ask-input')?.addEventListener('input', () => { workspacePreferences.draft = $('orch-ask-input').value; saveWorkspace(); });
  window.addEventListener('pagehide', () => { workspacePreferences.draft = $('orch-ask-input')?.value || ''; workspacePreferences.chatScroll = $('orch-ask-thread')?.scrollTop || 0; saveWorkspace(); });
  function restoreDraft(id, draft) {
    if (id === projectId) {
      const input = $('orch-ask-input');
      if (input && !input.value) { input.value = draft; workspacePreferences.draft = draft; saveWorkspace(); }
    } else {
      try { const key = workspaceKey(id), preferences = JSON.parse(localStorage.getItem(key) || '{}'); if (!preferences.draft) preferences.draft = draft; localStorage.setItem(key, JSON.stringify(preferences)); } catch { /* Original request is retained in pending/backend history. */ }
    }
  }

  function formatMs(ms) {
    const n = Math.max(0, Number(ms) || 0);
    if (n < 1000) return `${Math.round(n)} ms`;
    const s = n / 1000;
    if (s < 60) return (s < 10 ? s.toFixed(1) : String(Math.round(s))) + 's';
    const m = Math.floor(s / 60);
    const r = Math.round(s % 60);
    return `${m}m ${r}s`;
  }

  function thinkHtml(thinking, ms, open) {
    const hasThink = Boolean(thinking && String(thinking).trim());
    const hasMs = Number.isFinite(ms);
    if (!open && !hasThink && !hasMs) return '';
    if (open) {
      const body = hasThink
        ? `<pre class="chat-think-body">${escape(thinking)}</pre>`
        : '<p class="chat-think-body is-wait">Working…</p>';
      return `<div class="chat-think is-live"><p class="chat-think-head">Thinking · ${escape(formatMs(ms || 0))}</p>${body}</div>`;
    }
    if (hasThink) {
      return `<details class="chat-think"><summary>Thought for ${escape(formatMs(ms || 0))}</summary><pre class="chat-think-body">${escape(thinking)}</pre></details>`;
    }
    return `<p class="chat-elapsed">Answered in ${escape(formatMs(ms || 0))}</p>`;
  }

  function currentTheme() {
    return document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
  }
  function applyTheme(theme) {
    const next = theme === 'light' ? 'light' : 'dark';
    document.documentElement.setAttribute('data-theme', next);
    const meta = document.querySelector('meta[name="color-scheme"]');
    if (meta) meta.setAttribute('content', next);
    const btn = $('theme-toggle');
    if (btn) {
      btn.setAttribute('aria-pressed', next === 'light' ? 'true' : 'false');
      btn.setAttribute('aria-label', next === 'light' ? 'Switch to dark mode' : 'Switch to light mode');
      btn.setAttribute('title', next === 'light' ? 'Dark' : 'Light');
    }
    try { localStorage.setItem(themeKey, next); } catch {}
  }
  applyTheme(currentTheme());
  if ($('theme-toggle')) {
    $('theme-toggle').addEventListener('click', () => applyTheme(currentTheme() === 'dark' ? 'light' : 'dark'));
  }

  function clock(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  function planById(id) {
    return (orchestra.plans || []).find(item => item && item.id === id) || null;
  }

  function formatReset(iso) {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleString('en-GB', { weekday: 'short', hour: '2-digit', minute: '2-digit' });
  }

  function quotaHtml(plan) {
    const summary = PlanwharfQuota.html(plan);
    if (!plan || !plan.readable) return summary + (plan?.reason ? `<p class="muted">${escape(plan.reason)}</p>` : '');
    const windows = Array.isArray(plan.windows) && plan.windows.length ? plan.windows : [{ name: 'Provider quota', pct: plan.pct, leftPct: plan.leftPct }];
    return summary + windows.map(win => {
      const q = PlanwharfQuota.describe({ ...win, readable: true });
      const reset = formatReset(win.resetAt);
      return `<div class="quota quota-${q.band}"><p class="quota-window">${escape(win.name)}</p><div class="fleet-bar" role="progressbar" aria-valuemin="0" aria-valuenow="${q.remaining}" aria-valuemax="100" aria-label="${q.text}"><span style="width:${q.remaining}%"></span></div><p class="quota-line"><strong>${q.text}</strong><span>${reset ? `Resets ${escape(reset)}` : ''}</span></p></div>`;
    }).join('');
  }

  function notice(text) {
    const el = $('toast');
    if (!el) return;
    el.textContent = text;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { el.textContent = ''; }, 6000);
  }

  function leadUnavailableReason(item) {
    if (item.roles?.orchestrator === false) return 'Orchestrator role disabled';
    if (item.roleEligibility?.orchestrator === false) return 'Incompatible with the Orchestrator role';
    if (item.providerEnabled === false) return 'Provider disabled';
    if (item.available === false) return item.unavailableReason || 'Exact CLI model unavailable. Refresh models to verify access.';
    if (!item.enabled) return 'Model disabled';
    return '';
  }

  function fillLead() {
    const el = $('orch-lead');
    if (!el) return;
    if (document.activeElement === el) return;
    const current = orchestra.selectedOrchestratorId || orchestra.orchestratorId || '';
    const runnable = new Set((orchestra.orchestrators || []).map(item => item.id));
    const models = (orchestra.models || []).filter(item => !item.catalogExcluded && (item.id === current || (
      item.roles?.orchestrator !== false &&
      (item.roleEligibility?.orchestrator ?? (runnable.has(item.id) || item.kind === 'premium' || item.kind === 'default'))
    )));
    if (typeof el.setAttribute === 'function') el.setAttribute('data-quota-band', PlanwharfQuota.describe((orchestra.plans || []).find(p => p.id === (orchestra.models || []).find(m => m.id === current)?.plan)).band);
    const groups = [];
    const seen = new Map();
    models.forEach(item => {
      const plan = item.plan || 'Models';
      if (!seen.has(plan)) {
        seen.set(plan, []);
        groups.push(plan);
      }
      seen.get(plan).push(item);
    });
    const roster = orchestra.models || [];
    const options = groups.map(plan => `<optgroup label="${escape(plan)} | ${PlanwharfQuota.describe(planById(plan)).text}">${seen.get(plan).map(item => {
      const meter = planById(item.plan || plan);
      const tag = ` | ${PlanwharfQuota.describe(meter).text}`;
      const reason = leadUnavailableReason(item);
      const identity = `${item.providerId || item.provider || 'unknown'}/${item.cli || item.id}`;
      const status = reason ? ` | Unavailable: ${reason}` : '';
      return `<option class="quota-${PlanwharfQuota.describe(meter).band}" value="${escape(item.id)}" title="${escape(identity + status)}"${reason ? ' disabled' : ''}${item.id === current ? ' selected' : ''}>${escape(item.label)}${escape(tag + status)}</option>`;
    }).join('')}</optgroup>`).join('');
    el.innerHTML = `<option value="">Choose orchestrator</option>${options}`;
    const now = $('orch-lead-now');
    if (now) {
      const lead = roster.find(item => item.id === current);
      const meter = lead ? planById(lead.plan) : null;
      const tag = ` | ${PlanwharfQuota.describe(meter).text}`;
      now.textContent = lead ? `Lead: ${lead.label}${tag}${leadUnavailableReason(lead) ? ' | Unavailable: ' + leadUnavailableReason(lead) : ''}` : 'Lead: none yet. Pick a model.';
    }
  }

  function chatSignature(asks) {
    const live = orchestra.thinking || {};
    const elapsed = sending && pendingStarted ? Math.floor((Date.now() - pendingStarted) / 250) : 0;
    return JSON.stringify((asks || []).map(item => [item.at, item.question, item.answer, item.ms, item.thinking]))
      + (sending ? `|pending:${pendingText}|${elapsed}|${live.thinking || ''}|${live.draft || ''}|${live.ms || 0}` : '');
  }

  function diagnosticHtml(diagnostic) {
    if (!diagnostic || !String(diagnostic.reportUrl || '').startsWith('https://github.com/benitz94/Planwharf/issues/new?')) return '';
    return `<p class="notice">${escape(diagnostic.message)} <a href="${escape(diagnostic.reportUrl)}" target="_blank" rel="noopener noreferrer">Review an issue draft</a></p>`;
  }

  function renderChat() {
    const el = $('orch-ask-thread');
    if (!el) return;
    const sessionFilter = projectId || 'global';
    const turns = (orchestra.asks || []).filter(item => projectId ? item.projectId === projectId : item.projectId == null);
    const items = turns.slice().reverse();
    const sig = sessionFilter + chatSignature(turns);
    if (sig === chatSig && el.childElementCount) return;
    if (!el.dataset.scrollWatch) {
      el.dataset.scrollWatch = '1';
      el.addEventListener('scroll', () => { chatScroll = { top: el.scrollTop, nearBottom: el.scrollHeight - el.scrollTop - el.clientHeight < 96 }; }, { passive: true });
    }
    const nearBottom = chatScroll.nearBottom;
    // While a reply streams in, only the pending turn changes: update it alone so a re-render
    // never interrupts the reader's scrolling.
    const settledSig = sessionFilter + JSON.stringify(turns.map(item => [item.at, item.question, item.answer, item.ms, item.thinking]));
    const pendingEl = el.querySelector('.chat-turn.is-pending');
    if (sending && pendingProjectId === projectId && pendingEl && settledSig === chatSettledSig) {
      chatSig = sig;
      const live = orchestra.thinking;
      const liveMs = live && Number.isFinite(live.ms) ? live.ms : (pendingStarted ? Date.now() - pendingStarted : 0);
      pendingEl.innerHTML = `<div class="chat-you"><p>${escape(pendingText)}</p></div><div class="chat-bot">${thinkHtml((live && live.thinking) || '', liveMs, true)}${live && live.draft ? `<p class="chat-draft">${escape(live.draft)}</p>` : ''}</div>`;
      if (nearBottom) el.scrollTop = el.scrollHeight;
      return;
    }
    chatSettledSig = settledSig;
    chatSig = sig;
    if (!items.length && !sending) {
      el.innerHTML = '<div class="chat-empty"><p>Select a project recipient, then discuss the work or give an explicit command.</p><ul><li>Start</li><li>Resume</li><li>Stop</li><li>Skip grill</li><li>Switch to automatic</li><li>How much quota is left?</li></ul></div>';
      return;
    }
    const live = orchestra.thinking;
    const liveMs = live && Number.isFinite(live.ms) ? live.ms : (pendingStarted ? Date.now() - pendingStarted : 0);
    const pending = sending && pendingProjectId === projectId
      ? `<article class="chat-turn is-pending"><div class="chat-you"><p>${escape(pendingText)}</p></div><div class="chat-bot">${thinkHtml((live && live.thinking) || '', liveMs, true)}${live && live.draft ? `<p class="chat-draft">${escape(live.draft)}</p>` : ''}</div></article>`
      : '';
    el.innerHTML = items.map(item => {
      const order = item.order ? `<span class="chat-order">${escape(item.order)}</span>` : '';
      const elapsed = Number.isFinite(item.ms) ? ` · ${formatMs(item.ms)}` : '';
      return `<article class="chat-turn${item.order ? ' is-order' : ''}"><div class="chat-you"><p>${escape(item.question)}</p>${item.goldModel ? `<p class="muted">Gold model: ${escape(item.goldModel.provider)}/${escape(item.goldModel.cli)} (${escape(item.goldModel.id)})</p>` : ''}</div><div class="chat-bot">${order}${thinkHtml(item.thinking, item.ms, false)}<p data-chat-output>${escape(item.answer)}</p>${diagnosticHtml(item.diagnostic)}<button type="button" class="secondary compact chat-copy-output" data-copy-output>Copy output</button><time>${escape(clock(item.at))}${elapsed}</time></div></article>`;
    }).join('') + pending;
    if (el.dataset.restoreScroll !== undefined) { el.scrollTop = Number(el.dataset.restoreScroll) || 0; delete el.dataset.restoreScroll; }
    else if (nearBottom || !items.length) el.scrollTop = el.scrollHeight;
    else el.scrollTop = chatScroll.top;
  }

  function checkpointBanner(row) {
    if (!row || !row.checkpoint) return '';
    const why = row.checkpoint.reason;
    const line = why === 'stop'
      ? 'You stopped this job.'
      : (why === 'deadline' ? 'Time ran out.' : 'The job ended without completing.');
    return `<p class="checkpoint-flag" role="status"><span class="badge">Checkpoint saved</span> ${escape(line)} Resume continues from the last plan.</p>`;
  }

  function projectActions(row) {
    if (!row) return '';
    const id = row.id;
    const approve = row.status === 'needs_approval' ? `<button type="button" class="primary" data-approve="${escape(id)}">${row.megaTaskId ? 'Approve MegaTask' : 'Go'}</button>` : '';
    const resume = row.canResume && ['checkpointed', 'failed'].includes(row.status) ? `<button type="button" class="primary" data-resume="${escape(id)}">Resume</button>` : '';
    const pause = row.canStop && row.megaTaskId ? `<button type="button" class="secondary" data-pause="${escape(id)}">Pause after current tasks</button>` : '';
    const stop = row.canStop ? `<button type="button" class="primary" data-stop="${escape(id)}">Stop</button>` : '';
    const propose = orchestra.grillPropose !== false;
    const kind = orchestra.grillKind === 'docs' ? 'docs' : (propose ? 'me' : 'off');
    const docsOk = row.canGrillDocs !== false || Boolean(row.github);
    const busy = row.status === 'watching';
    const grillTriggers = approve || resume || stop || busy
      ? ''
      : `<button type="button" class="${kind === 'me' ? 'primary' : 'secondary'}" data-grill-kind="me" data-grill-start="${escape(id)}">Grill me</button><button type="button" class="${kind === 'docs' ? 'primary' : 'secondary'}" data-grill-kind="docs" data-grill-start="${escape(id)}"${docsOk ? '' : ' disabled'}>Grill with the docs</button>`;
    const autonomy = orchestra.autonomy?.[id];
    const autonomyOn = Boolean(autonomy?.enabled);
    const autonomyToggle = orchestra.mode === 'auto' || autonomyOn ? `<button type="button" class="secondary" data-autonomy="${escape(id)}" data-autonomy-enable="${autonomyOn ? 'false' : 'true'}">${autonomyOn ? 'Turn off project autonomy' : 'Turn on project autonomy'}</button>` : '';
    const autonomyNote = autonomy?.stopReason ? `<span class="muted" role="status">Autonomy stopped: ${escape(autonomy.stopReason)}</span>` : '';
    return `${approve}${grillTriggers}${resume}${stop}${pause}${autonomyToggle}${autonomyNote}`;
  }

  function workerState(row, part) {
    if (part.validatedResultStatus === 'COMPLETE') return 'Complete';
    if (part.validatedResultStatus === 'FAILED') return 'Failed';
    if (part.validatedResultStatus === 'BLOCKED') return 'Blocked';
    if (part.stoppedByUser) return 'Paused';
    if (row.status === 'complete') return 'Finished';
    if (row.status === 'checkpointed') return 'Paused';
    if (row.status === 'failed') return 'Stopped';
    if (part.pid && part.alive) return 'Running';
    if (part.pid) return 'Exited';
    return 'Awaiting launch';
  }

  function workersHtml(rows) {
    const models = orchestra.models || orchestra.fleet || [];
    const assignmentRow = (row, part, retainedHistory) => {
      const owner = row.projectId || row.id;
      const session = part.activitySession || (row.activitySessions || []).find(item => item.projectId === owner && item.attemptId === part.attemptId && (!part.megaTaskId || item.megaTaskId === part.megaTaskId));
      const megaId = part.megaTaskId || session?.megaTaskId;
      const mega = (orchestra.execution?.megaTasks || []).find(item => item.projectId === owner && item.id === megaId);
      const historical = retainedHistory || Boolean(megaId && row.megaTaskId && megaId !== row.megaTaskId) || session?.historical === true;
      if (!historical) return { row, part };
      // A retained attempt belongs to its original MegaTask. The current project
      // title, progress summary and combined tail are never evidence for it.
      const status = mega ? ({ COMPLETE: 'complete', FAILED: 'failed', PAUSED: 'checkpointed', RUNNING: 'recorded' }[mega.status] || 'recorded') : retainedHistory ? row.status : 'recorded';
      return { row: { ...row, jobTitle: mega?.title || (retainedHistory ? row.jobTitle : null) || 'Assignment title not retained', status, label: mega?.status || (retainedHistory ? row.label || row.status : null) || 'Historical assignment; status unknown', workerActivity: '', workerTail: '', progressAt: null, monitoring: null }, part: { ...part, alive: false } };
    };
    const workers = [...rows.map(row => ({ row, retainedHistory: false })), ...(orchestra.workerHistory || []).map(row => ({ row, retainedHistory: true }))].flatMap(({ row, retainedHistory }) => {
      const parts = Array.isArray(row.dispatch) ? row.dispatch.filter(part => part && (part.pid || part.startedAt || part.resultPath)) : [];
      if (parts.length) return parts.map((part, index) => ({ ...assignmentRow(row, part, retainedHistory), index }));
      return row.workerPid ? [{ row, part: { modelId: row.modelId, label: row.workerLabel, pid: row.workerPid, alive: row.workerAlive, activity: row.workerActivity }, index: 0 }] : [];
    });
    (orchestra.researches || []).forEach(job => {
      const row = { id: job.id, projectId: job.projectId, research: true, name: job.projectName || 'Research', jobTitle: job.title, attemptId: job.attemptId, status: job.status, label: job.status, workerTail: job.workerTail || '', progressAt: job.updatedAt, reportAvailable: job.reportAvailable };
      workers.push({ row, part: { diagnostic: job.diagnostic, modelId: job.modelId, label: job.workerLabel, provider: job.provider, cli: job.cli, pid: job.pid, alive: job.alive, activity: job.activity || job.reason || '', slice: 'Research', sessionId: job.sessionId, startedAt: job.startedAt || job.createdAt }, index: 0 });
    });
    const globalRunning = workers.filter(({ row, part }) => workerState(row, part) === 'Running').length;
    const projectFilter = $('worker-project-filter')?.value || (projectId ? 'current' : 'all');
    const sessionFilter = $('worker-session-filter')?.value || 'current';
    const stateFilter = $('worker-state-filter')?.value || 'all';
    const visible = workers.filter(({ row, part }) =>
      (projectFilter === 'all' || (row.projectId || row.id) === (projectFilter === 'current' ? projectId : projectFilter)) &&
      (sessionFilter === 'all' || sessionFilter === 'legacy' && !part.sessionId || sessionFilter === 'current' && (orchestra.sessionId ? part.sessionId === orchestra.sessionId && Date.parse(part.startedAt) >= workerSessionStartedAt : false) || part.sessionId === sessionFilter) &&
      (stateFilter === 'all' || workerState(row, part) === stateFilter || stateFilter === 'Finished' && workerState(row, part) === 'Complete'
        || stateFilter === 'Stopped' && workerState(row, part) === 'Failed' || stateFilter === 'Paused' && workerState(row, part) === 'Blocked'));
    const running = visible.filter(({ row, part }) => workerState(row, part) === 'Running').length;
    const paused = visible.filter(({ row, part }) => ['Paused', 'Blocked'].includes(workerState(row, part))).length;
    const finished = visible.filter(({ row, part }) => ['Finished', 'Complete'].includes(workerState(row, part))).length;
    visible.sort((a, b) => Number(workerState(b.row, b.part) === 'Running') - Number(workerState(a.row, a.part) === 'Running'));
    return `<section class="dash-section dash-zone-workers" aria-label="Workers"><div class="workers-heading"><h3>Workers</h3><span class="badge${running ? ' green' : ''}">${running} running</span></div><p class="worker-legend" role="status">${globalRunning} active across all projects and sessions | ${running} running, ${paused} paused, ${finished} finished in this view. Previous sessions are hidden by default.</p><div class="dash-workers">${visible.map(({ row, part, index }) => {
      const model = models.find(item => item.id === part.modelId) || {};
      const state = workerState(row, part);
      const key = `${row.id}:${row.attemptId || ''}:${index}`;
      const progress = clock(row.monitoring && row.monitoring.lastProgress || row.progressAt);
      const activity = part.activity || row.workerActivity || 'No worker output yet.';
      const log = row.workerTail ? `<details class="monitor-log" data-worker-log="${escape(key)}"><summary>${(row.dispatch || []).length > 1 ? 'Project worker logs (all slices)' : 'Worker output'}</summary><pre class="worker-tail">${escape(row.workerTail)}</pre></details>` : '<p class="muted">No log output yet.</p>';
      const report = researchReports.get(row.id);
      const actions = row.research ? `<div class="now-actions">${row.status === 'needs_approval' ? `<button type="button" class="primary compact" data-research-approve="${escape(row.id)}">Approve research MegaTask</button>` : ''}${row.reportAvailable || row.status === 'complete' ? `<button type="button" class="secondary compact" data-research-report="${escape(row.id)}">Read research report</button>` : ''}${state === 'Running' ? `<button type="button" class="secondary compact" data-research-stop="${escape(row.id)}">Stop</button>` : ''}${row.status === 'checkpointed' ? `<button type="button" class="secondary compact" data-research-resume="${escape(row.id)}">Resume</button>` : ''}</div>${report != null ? `<details class="monitor-log" data-worker-log="${escape(key)}:report" open><summary>Research report</summary><pre class="worker-tail">${escape(report)}</pre></details>` : ''}` : (state === 'Running' ? `<div class="now-actions"><button type="button" class="secondary compact" data-worker-stop="${escape(row.id)}" data-worker-pid="${escape(part.pid)}">Stop worker</button></div>` : '');
      return `<article class="dash-worker${state === 'Running' ? ' is-running' : ''}" data-worker-state="${escape(state)}"><header><strong>${escape(part.label || model.label || part.modelId || 'Unknown worker')}</strong><span class="badge${state === 'Running' ? ' green' : ''}">${escape(state)}</span></header><p class="worker-model">Provider: ${escape(part.provider || model.provider || 'unknown')} · Model: ${escape(part.cli || model.cli || part.modelId || 'unknown')} · ID: ${escape(part.modelId || 'unknown')}</p><p><strong>${escape(row.name)}</strong>${row.jobTitle ? ` · ${escape(row.jobTitle)}` : ''}</p><p class="muted">${escape(part.slice || 'Whole job')} · ${part.pid ? `PID ${escape(part.pid)} · ${part.observedAlive && !part.alive ? 'recorded PID exists; this attempt is inactive' : part.alive ? 'alive' : 'not alive'}` : (state === 'Awaiting launch' ? 'No process launched' : 'No live process')} · Project: ${escape(row.label || row.status || 'unknown')}</p><p class="worker-activity">${escape(activity)}</p>${progress ? `<p class="muted">Last ${row.research ? 'research update' : 'project progress'}: ${escape(progress)}</p>` : ''}${diagnosticHtml(part.diagnostic)}${log}${actions}</article>`;
    }).join('') || '<p class="workers-empty">No workers launched in the selected filters. Previous sessions remain available under All sessions.</p>'}</div></section>`;
  }

  function renderPolicySection() {
    const el = $('orchestrator')?.querySelector('.dash-zone-policy');
    if (!el) return;
    const keepSelectorFocus = document.activeElement === el.querySelector('[data-policy-provider-select]');
    el.outerHTML = PlanwharfModelPolicyUI.section(orchestra.providers || [], selectedPolicyProviderId, policyDrafts.get(selectedPolicyProviderId));
    if (keepSelectorFocus) $('orchestrator').querySelector('[data-policy-provider-select]').focus();
  }

  function renderBoard() {
    if (document.activeElement?.closest?.('form[data-provider-policy], [data-policy-provider-select]')) return;
    const live = $('orch-live');
    const el = $('orchestrator');
    if (!el) return;
    const rows = (orchestra.projects || []).filter(row => !projectId || row.id === projectId);
    const sessionControl = $('worker-session-filter');
    if (sessionControl) {
      const selectedSession = sessionControl.value || 'current';
      sessionControl.innerHTML = '<option value="current">Current view session</option><option value="all">All sessions</option><option value="legacy">Earlier sessions (before tracking)</option>' + (orchestra.sessions || []).slice().reverse().map(session => `<option value="${escape(session.id)}">${escape(session.startedAt ? new Date(session.startedAt).toLocaleString('en-GB') : 'Date not recorded')} (${escape(session.id.slice(0, 8))})</option>`).join('');
      sessionControl.value = ['current', 'all', 'legacy', ...(orchestra.sessions || []).map(session => session.id)].includes(selectedSession) ? selectedSession : 'current';
    }
    const projectFilter = $('worker-project-filter');
    if (projectFilter) {
      const selectedFilter = projectFilter.value || 'current';
      projectFilter.innerHTML = '<option value="current">Selected project</option><option value="all">All projects</option>' + rows.map(row => `<option value="${escape(row.id)}">${escape(row.name)}</option>`).join('');
      projectFilter.value = ['current', 'all', ...rows.map(row => row.id)].includes(selectedFilter) ? selectedFilter : 'current';
    }
    const fleet = Array.isArray(orchestra.fleet) && orchestra.fleet.length ? orchestra.fleet : (orchestra.models || []);
    const interventions = (orchestra.interventions || []).filter(item => !projectId || item.projectId === projectId || item.project === rows[0]?.name);
    const looked = clock(orchestra.updatedAt);
    const lead = orchestra.orchestratorLabel || 'Orchestrator';
    if (live) live.textContent = `${lead} · updated ${looked || 'just now'}. I intervene on stall, death, or FAILED.`;
    const plans = Array.isArray(orchestra.plans) ? orchestra.plans : [];
    const planCards = `<section class="dash-section dash-zone-plans"><h3>Provider quota remaining</h3><p class="muted">Shared across every model on that provider. If ChatGPT is exhausted, every ChatGPT model is exhausted.</p><div class="dash-models">${plans.map(item => {
      const badge = item.exhausted ? '<span class="badge off">Exhausted</span>' : (item.readable ? '<span class="badge green">Live</span>' : '<span class="badge">Unread</span>');
      return `<article class="dash-model${item.exhausted ? ' state-off' : ''}"><header><strong>${escape(item.label)}</strong>${badge}</header>${quotaHtml(item)}</article>`;
    }).join('') || '<p class="muted">Provider usage loads from the local Grok and ChatGPT logins.</p>'}</div></section>`;
    if (!searchControl && typeof document.createElement === 'function' && typeof el.before === 'function') {
      const label = document.createElement('label'); label.textContent = 'Find models';
      searchControl = document.createElement('input'); searchControl.type = 'search'; searchControl.placeholder = 'Provider, model name or exact ID'; searchControl.setAttribute('aria-label', 'Find models');
      searchControl.addEventListener('input', () => { catalogSearch = searchControl.value.toLowerCase(); renderBoard(); });
      label.append(searchControl); el.before(label);
    }
    if (!roleFilterControl && typeof document.createElement === 'function' && typeof el.before === 'function') {
      const label = document.createElement('label'); label.textContent = 'Model role';
      roleFilterControl = document.createElement('select'); roleFilterControl.setAttribute('aria-label', 'Filter models by role');
      roleFilterControl.innerHTML = '<option value="all">All roles</option><option value="orchestrator">Orchestrator</option><option value="worker">Worker</option><option value="verifier">Verifier</option><option value="reviewer">Reviewer</option><option value="supervisor">Supervisor</option>';
      roleFilterControl.addEventListener('change', () => { modelRoleFilter = roleFilterControl.value; renderBoard(); });
      label.append(roleFilterControl); el.before(label);
    }
    const rolesSupported = fleet.every(item => item.roles && item.roleEligibility);
    const roleSupportNotice = rolesSupported ? '' : '<p class="notice" role="status">Backend update required for model roles. Use Quit application, then relaunch Planwharf. Model role changes are unavailable until the backend is updated.</p>';
    const providerList = orchestra.providers || [];
    const excludedFleet = fleet.filter(item => item.catalogExcluded);
    const visibleFleet = fleet.filter(item => {
      if (item.catalogExcluded) return false;
      if (modelRoleFilter !== 'all' && item.roles?.[modelRoleFilter] !== true) return false;
      if (catalogSearch) return [item.id, item.label, item.provider, item.cli].some(value => String(value || '').toLowerCase().includes(catalogSearch));
      if (modelRoleFilter !== 'all') return true;
      const provider = providerList.find(provider => provider.id === (item.providerId || item.provider));
      const siblings = fleet.filter(model => (model.providerId || model.provider) === (item.providerId || item.provider));
      return !provider || provider.showAll || siblings.length <= 12 || item.enabled || item.lead || siblings.indexOf(item) < 8;
    });
    const providerControls = providerList.map(provider => `<article class="dash-model"><strong>${escape(provider.label)}</strong><p>${provider.models.length} configured models | ${escape(provider.availability)}</p><button class="secondary compact" data-provider="${escape(provider.id)}" data-provider-enabled="${!provider.enabled}">${provider.enabled ? 'Disable provider' : 'Enable provider'}</button><button class="secondary compact" data-provider-show="${escape(provider.id)}" data-provider-show-all="${!provider.showAll}">${provider.showAll ? 'Show selected models' : 'Show all models'}</button></article>`).join('');
    const models = `<section class="dash-section dash-zone-models"><h3>Models</h3>${roleSupportNotice}${rolesSupported ? `<div class="model-role-bulk" aria-label="Select models by role">${PlanwharfWorkspaceUI.roles.map(role => `<fieldset><legend>${role[0].toUpperCase() + role.slice(1)}</legend><button type="button" class="secondary compact" data-role-bulk="${role}" ${role === 'supervisor' && !fleet.some(model => model.roleEligibility?.supervisor === true) ? 'disabled' : ''} data-role-enabled="true">Select all</button><button type="button" class="secondary compact" data-role-bulk="${role}" ${role === 'supervisor' && !fleet.some(model => model.roleEligibility?.supervisor === true) ? 'disabled' : ''} data-role-enabled="false">Deselect all</button></fieldset>`).join('')}</div><p class="muted">Role buttons apply to all compatible models, including models hidden by filters.</p>` : ''}<div class="dash-models">${providerControls}</div><p class="muted">Showing ${visibleFleet.length} of ${fleet.length} models. Search includes current models. ${excludedFleet.length} models no longer listed by their provider are hidden; saved roles and execution history are retained.</p><div class="dash-models">${visibleFleet.map(item => {
      const jobs = item.assignments || [];
      const doing = jobs.map(job => `${job.project}${job.taskTitle ? ' / ' + job.taskTitle : ''}${job.attemptId ? ' / attempt ' + job.attemptId : ''}: ${job.activity || job.slice || item.state}`).join(' · ') || (item.available === false ? 'Unavailable' : item.exhausted ? 'Exhausted' : item.providerEnabled === false ? 'Provider disabled' : item.enabled ? 'Idle' : 'No roles assigned');
      const availabilityText = item.availabilityWarning
        ? `Availability unread: ${item.availabilityWarning} Last known status: ${item.available === false ? 'unavailable' : 'available'}.`
        : item.available === false ? item.unavailableReason : doing;
      const pids = jobs.map(job => `pid ${job.pid || '—'} ${job.alive ? 'alive' : 'stopped'}`).join(' · ');
      const roleFlags = rolesSupported ? PlanwharfWorkspaceUI.roles.map(role => {
        const compatible = role === 'supervisor' ? item.roleEligibility?.supervisor === true && typeof item.roles?.supervisor === 'boolean' : item.roleEligibility?.[role] !== false;
        const selected = role === 'supervisor' ? PlanwharfWorkspaceUI.roleAllowed(item, role) : item.roles?.[role] ?? (role === 'orchestrator' ? item.orchestratorEligible !== false : true);
        const title = compatible ? 'Permit this model to perform this role' : 'This model is not compatible with this role';
        return `<label title="${escape(title)}"><input type="checkbox" data-model-role="${escape(item.id)}" data-role="${role}" ${selected ? 'checked' : ''} ${compatible ? '' : 'disabled'}>${role[0].toUpperCase() + role.slice(1)}${compatible ? '' : ' (not supported)'}</label>`;
      }).join('') : '';
      const leadBadge = item.lead ? '<span class="badge green">Lead</span>' : (item.exhausted ? '<span class="badge off">Exhausted</span>' : `<span class="badge">${escape(item.availabilityWarning ? 'Availability unread' : item.state || 'idle')}</span>`);
      return `<article class="dash-model state-${escape(item.state || 'idle')}${item.lead ? ' is-lead' : ''}"><header><strong>${escape(item.label)}</strong>${leadBadge}</header><p class="muted">${escape(item.plan || '—')} · ${escape(item.role || 'Worker')}</p>${PlanwharfQuota.html(planById(item.plan))}<p>${escape(availabilityText)}</p>${item.availabilityCheckedAt ? `<p class="muted">Availability checked: ${escape(new Date(item.availabilityCheckedAt).toLocaleString('en-GB'))}</p>` : ''}${pids ? `<p class="muted">${escape(pids)}</p>` : ''}${rolesSupported ? `<fieldset class="model-role-flags"><legend>Allowed roles</legend>${roleFlags}</fieldset>` : ''}</article>`;
    }).join('')}</div></section>`;
    const processes = `<section class="dash-section dash-zone-procs"><h3>Processes</h3><div class="dash-procs">${rows.map(row => {
      const parts = row.dispatch || [];
      const slices = parts.length
        ? parts.map(part => `<li><strong>${escape(part.label || part.modelId)}</strong> · ${escape(part.assignmentJobTitle || (part.historicalAssignment ? 'Historical assignment title not retained' : row.jobTitle) || part.slice || 'whole job')}${part.attemptId ? ' · attempt ' + escape(part.attemptId) : ''} · pid ${escape(String(part.pid || '—'))} ${part.alive ? 'alive' : 'stopped'} · ${escape(part.activity || (part.historicalAssignment ? 'Original activity is not retained.' : row.workerActivity || 'quiet'))}</li>`).join('')
        : `<li class="muted">${row.workerLabel ? escape(row.workerLabel) : 'No process yet.'}</li>`;
      const log = row.workerTail ? `<details class="monitor-log"><summary>Live log</summary><pre class="worker-tail">${escape(row.workerTail)}</pre></details>` : '';
      const monitor = row.monitoring ? `<p class="muted">${row.monitoring.alive}/${row.monitoring.workers} alive · ${Number.isFinite(row.monitoring.folderFiles) ? row.monitoring.folderFiles + ' files' : 'File count unavailable'}</p>` : '';
      return `<article class="dash-proc ${escape(row.status)}"><header><strong>${escape(row.name)}</strong><span class="orch-status">${escape(row.label)}</span></header>${checkpointBanner(row)}<p>${escape(row.analysis || row.orchestratorDoing || row.doing || '')}</p>${monitor}<ol class="dash-slices">${slices}</ol>${log}<div class="now-actions">${projectActions(row)}</div></article>`;
    }).join('') || '<p class="muted">No projects in the notebook.</p>'}</div></section>`;
    const iv = `<section class="dash-section dash-zone-iv"><h3>Interventions</h3>${interventions.length
      ? `<ol class="dash-iv">${interventions.map(item => `<li><time>${escape(clock(item.at))}</time> ${escape(item.project)} · ${escape(item.text)}</li>`).join('')}</ol>`
      : '<p class="muted">No interventions yet. I switch workers on stall, death, or FAILED.</p>'}</section>`;
    const openLogs = new Set(Array.from(el.querySelectorAll('details[data-worker-log][open]')).map(detail => detail.getAttribute('data-worker-log')));
    const policyProviders = orchestra.providers || [];
    if (!policyProviders.some(provider => provider.id === selectedPolicyProviderId)) selectedPolicyProviderId = policyProviders[0]?.id || null;
    const policySection = orchestra.providerPolicySupported ? PlanwharfModelPolicyUI.section(policyProviders, selectedPolicyProviderId, policyDrafts.get(selectedPolicyProviderId)) : '';
    const usage = orchestra.aiUsage || { orchestrator: [], workers: [], recent: [] };
    const usageRows = groups => groups.map(group => `<li>${escape(group.provider)} / ${escape(group.model)}: ${group.calls} calls or attempts${group.tokenReadings ? ' | ' + group.inputTokens + ' input / ' + group.outputTokens + ' output tokens (reported)' : ''} | ${escape(formatMs(group.durationMs))} | ${group.COMPLETE} COMPLETE / ${group.FAILED} FAILED / ${group.BLOCKED} BLOCKED${group.RUNNING ? ' / ' + group.RUNNING + ' RUNNING' : ''}</li>`).join('') || '<li>No invocations recorded.</li>';
    const usageHtml = `<section class="dash-section" aria-label="AI usage"><h3>AI usage</h3><p class="muted">Observed calls and duration. No billing estimates.</p><h4>Orchestrator | ${usage.orchestrator.reduce((sum, group) => sum + group.calls, 0)} calls | ${escape(formatMs(usage.orchestrator.reduce((sum, group) => sum + group.durationMs, 0)))}</h4><ul>${usageRows(usage.orchestrator)}</ul><h4>Workers | ${usage.workers.reduce((sum, group) => sum + group.calls, 0)} attempts | ${escape(formatMs(usage.workers.reduce((sum, group) => sum + group.durationMs, 0)))}</h4><ul>${usageRows(usage.workers)}</ul><h4>Supervisors</h4><ul>${usageRows(usage.supervisors || [])}</ul><details><summary>Recent calls</summary><ol>${usage.recent.map(call => `<li>${escape(call.role)} | ${escape(call.provider)} / ${escape(call.model)} | ${escape(call.status)} | ${escape(formatMs(call.durationMs))} | ${escape(call.startTime)}</li>`).join('')}</ol></details></section>`;
    const gold = fleet.find(model => model.id === orchestra.goldModelId);
    const goldSection = rolesSupported ? `<section class="dash-section" aria-label="Gold model"><h3>Gold model</h3><label>Preferred worker<select data-gold-model><option value="">Choose Gold model</option>${fleet.filter(model => model.roleEligibility?.worker && !model.catalogExcluded).map(model => `<option value="${escape(model.id)}" ${model.id === orchestra.goldModelId ? 'selected' : ''}>${escape(model.label)} | ${escape(model.providerId || model.provider)}/${escape(model.cli)} (${escape(model.id)})${model.roles?.worker ? '' : ' - Worker role disabled'}</option>`).join('')}${orchestra.goldModelId && (!gold || gold.catalogExcluded) ? `<option selected disabled value="${escape(orchestra.goldModelId)}">Configured Gold model unavailable: ${escape(gold?.label || orchestra.goldModelId)}</option>` : ''}</select></label><p class="muted">Use Gold model below the chat for one request. Its implementation and research jobs keep this exact model through approval, pause and resume. Worker permission and quota limits still apply; unavailable work pauses without substitution. Reviewers keep their settings.</p></section>` : '';
    el.innerHTML = `${goldSection}${workersHtml(rows)}${usageHtml}${planCards}${policySection}${models}${processes}${iv}`;

    el.querySelectorAll('details[data-worker-log]').forEach(detail => {
      if (openLogs.has(detail.getAttribute('data-worker-log'))) detail.open = true;
    });
  }

  function renderRequestMode() {
    const control = $('orch-request-mode'), hint = $('orch-gold-hint');
    if (!control || !hint) return;
    const model = (orchestra.models || []).find(model => model.id === orchestra.goldModelId);
    hint.textContent = control.value === 'gold'
      ? model?.catalogExcluded ? 'The configured Gold model is no longer available from its provider. Choose another model before sending.' : model ? `Gold: ${model.providerId || model.provider}/${model.cli} (${model.id}). Exact worker for this request; no fallback. Returns to Default after sending.` : 'Choose a Gold model in the dashboard before sending.'
      : 'Default: the orchestrator chooses workers normally.';
  }
  if ($('orch-request-mode')) $('orch-request-mode').addEventListener('change', renderRequestMode);

  function render() {
    usageNotices?.(orchestra);
    if (typeof window !== 'undefined' && typeof CustomEvent === 'function') window.dispatchEvent(new CustomEvent('planwharf-evidence', { detail: orchestra.execution?.timeline || [] }));
    const recipient = $('orch-project');
    const projects = orchestra.projects || [];
    try { projectId = localStorage.getItem('planwharf_chat_project') || projectId; } catch (_) {}
    if (!projects.some(row => row.id === projectId)) projectId = null;
    syncWorkspaceProject();
    if (typeof CustomEvent === 'function') window.dispatchEvent(new CustomEvent('planwharf-orchestrator', { detail: { snapshot: orchestra, projectId, available: executionAvailable } }));
    if (recipient) recipient.innerHTML = '<option value="">Select a project</option>' + projects.map(row => `<option value="${escape(row.id)}"${row.id === projectId ? ' selected' : ''}>${escape(row.name)}</option>`).join('');
    fillLead();
    renderRequestMode();
    const grillStatus = $('orch-grill-status');
    const lastTurn = (orchestra.asks || []).find(turn => turn.projectId === projectId && (!window.PlanwharfChatSessionId || turn.chatSessionId === window.PlanwharfChatSessionId));
    if (grillStatus) {
      grillStatus.hidden = !lastTurn?.grillKind;
      grillStatus.textContent = lastTurn?.grillKind ? `${lastTurn.grillKind === 'docs' ? 'Grill with docs' : 'Grill me'} in chat. Reply here; use Start when the plan is agreed, or Stop grill to end the interview.` : '';
    }
    renderChat();
    renderBoard();
    renderWorkspace();
  }

  async function refresh() {
    const ticket = ++refreshGeneration;
    try {
      const response = await fetch('/api/orchestrator');
      if (!response.ok) throw new Error('Execution state unavailable');
      const snap = await response.json();
      if (ticket !== refreshGeneration) return;
      executionAvailable = true;
      const keep = sending ? orchestra.thinking : null;
      orchestra = snap;
      if (keep && !orchestra.thinking) orchestra.thinking = keep;
      render();
    } catch (_) {
      if (ticket !== refreshGeneration) return;
      executionAvailable = false;
      renderWorkspace();
      if (typeof CustomEvent === 'function') window.dispatchEvent(new CustomEvent('planwharf-orchestrator', { detail: { snapshot: null, projectId, available: false } }));
    }
  }

  async function loadPlan() {
    const response = await fetch('/api/plan');
    const body = await response.json();
    if (!response.ok) { const error = new Error(body.error || body.reason || 'Save failed.'); error.confirmation = body.requiresConfirmation; throw error; }
    version = body.version;
  }

  async function executionPost(action, id) {
    const row = (orchestra.projects || []).find(row => row.id === id);
    if (!window.PlanwharfRunExecution) return post('/api/orchestrator/' + action, { projectId: id });
    const response = await window.PlanwharfRunExecution(action, row), result = await response.json();
    if (!response.ok) throw new Error(result.error || result.reason);
    return result;
  }

  async function post(url, payload, signal) {
    if (url === '/api/orchestrator/ask') await window.PlanwharfFocusReady;
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload || {}), signal });
    const body = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(body.error || 'Save failed.');
    if (body.snapshot?.projects || body.projects) executionAvailable = true;
    return body;
  }

  function setComposer(busy) {
    const input = $('orch-ask-input');
    const submit = $('orch-ask-submit');
    const stop = $('orch-ask-stop');
    if (input) input.disabled = false;
    if (submit) {
      submit.disabled = false;
      submit.hidden = Boolean(busy);
    }
    if (stop) stop.hidden = !busy;
  }

  function stopThinkTimers() {
    if (!thinkTimer) return;
    clearInterval(thinkTimer.clock);
    clearInterval(thinkTimer.poll);
    thinkTimer = null;
  }

  function endSend() {
    stopThinkTimers();
    sending = false;
    pendingText = '';
    pendingProjectId = null;
    pendingStarted = 0;
    orchestra.thinking = null;
    askAbort = null;
    setComposer(false);
    chatSig = '';
    render();
  }

  if ($('orch-lead')) {
    $('orch-lead').addEventListener('change', async event => {
      const id = event.target.value;
      try {
        orchestra = await post('/api/orchestrator/lead', { id });
        render();
        notice(id ? `Orchestrator is now ${(orchestra.orchestratorLabel || id)}.` : 'Orchestrator lead cleared.');
      } catch (error) {
        notice(error.message);
      }
    });
  }

  async function cancelAsk() {
    if (!sending) return;
    sendGen += 1;
    const previous = pendingText;
    const cancelledProject = pendingProjectId;
    try {
      const body = await post('/api/orchestrator/ask/cancel', {});
      if (body.snapshot) orchestra = Object.assign(orchestra, body.snapshot);
      if (Array.isArray(body.asks)) orchestra.asks = body.asks;
    } catch (_) {}
    if (askAbort) {
      try { askAbort.abort(); } catch (_) {}
    }
    endSend();
    const input = $('orch-ask-input');
    if (previous) restoreDraft(cancelledProject, previous);
    if (input && projectId === cancelledProject) input.focus();
  }

  async function sendChat(options = {}) {
    const input = $('orch-ask-input');
    const question = input && input.value ? input.value.trim() : '';
    if (sending) {
      const next = question;
      await cancelAsk();
      if (next && input) input.value = next;
      if (!next) return;
    }
    if (!question && !(input && input.value.trim())) return;
    const text = (input && input.value ? input.value.trim() : question);
    if (!text) return;
    const targetProjectId = projectId;
    if (!targetProjectId || !(orchestra.projects || []).some(row => row.id === targetProjectId)) { notice('Select the project recipient before sending.'); return; }
    const myGen = ++sendGen;
    sending = true;
    chatScroll.nearBottom = true;
    pendingText = text;
    pendingProjectId = targetProjectId;
    pendingStarted = Date.now();
    orchestra.thinking = { question: text, thinking: '', draft: '', ms: 0 };
    askAbort = typeof AbortController === 'function' ? new AbortController() : null;
    setComposer(true);
    if (input) input.value = '';
    workspacePreferences.draft = ''; saveWorkspace();
    chatSig = '';
    renderChat();
    const pollThink = async () => {
      if (myGen !== sendGen) return;
      try {
        const response = await fetch('/api/orchestrator');
        if (!response.ok) return;
        const snap = await response.json();
        const keepThinking = orchestra.thinking;
        orchestra = snap;
        if (!orchestra.thinking) orchestra.thinking = keepThinking;
        render();
      } catch (_) {}
    };
    stopThinkTimers();
    thinkTimer = {
      clock: setInterval(() => { renderChat(); }, 250),
      poll: setInterval(pollThink, 500)
    };
    pollThink();
    try {
      const body = await post('/api/orchestrator/ask', { question: text, projectId: targetProjectId, requestMode: options.grillKind ? 'default' : $('orch-request-mode')?.value || 'default', ...(options.grillKind ? { grillKind: options.grillKind } : {}), ...(typeof window !== 'undefined' && window.PlanwharfChatSessionId ? { chatSessionId: window.PlanwharfChatSessionId } : {}) }, askAbort ? askAbort.signal : undefined);
      if (myGen !== sendGen) return;
      if (body.snapshot) orchestra = Object.assign(orchestra, body.snapshot);
      if ($('orch-request-mode')) $('orch-request-mode').value = 'default';
      orchestra.asks = body.asks || orchestra.asks || [];
      orchestra.thinking = null;
    } catch (error) {
      if (myGen !== sendGen) return;
      if (!error || error.name !== 'AbortError') notice(error.message);
      restoreDraft(targetProjectId, text);
    } finally {
      if (myGen !== sendGen) return;
      endSend();
      if (input && projectId === targetProjectId) input.focus();
    }
  }

  async function startGrillChat(id, kind = 'me') {
    if (sending) { notice('The orchestrator is already responding. Stop or wait before starting another Grill session.'); return; }
    if (!(orchestra.projects || []).some(row => row.id === id)) throw new Error('Select a registered project before starting Grill.');
    const input = $('orch-ask-input');
    if (!input) throw new Error('Orchestrator chat is unavailable in this window.');
    if (input.value.trim()) { notice('Send or clear your draft before starting Grill.'); input.focus(); return; }
    projectId = id;
    try { localStorage.setItem('planwharf_chat_project', id); } catch { /* The explicit request still targets this project. */ }
    window.dispatchEvent(new CustomEvent('planwharf-project', { detail: id }));
    window.PlanwharfShowView?.('orchestrator');
    input.value = kind === 'docs' ? 'Grill with the docs' : 'Grill me';
    await sendChat({ grillKind: kind });
  }
  if (typeof window !== 'undefined') window.PlanwharfStartGrillChat = startGrillChat;

  if ($('orch-ask-form')) {
    $('orch-ask-form').addEventListener('submit', event => {
      event.preventDefault();
      sendChat();
    });
  }
  if ($('orch-ask-stop')) {
    $('orch-ask-stop').addEventListener('click', event => {
      event.preventDefault();
      cancelAsk();
    });
  }
  if ($('orch-ask-input')) {
    $('orch-ask-input').addEventListener('keydown', event => {
      if (event.key === 'Escape' && sending) {
        event.preventDefault();
        cancelAsk();
        return;
      }
      if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault();
        sendChat();
      }
    });
  }

  if ($('orchestrator')) {
    $('orchestrator').addEventListener('input', event => {
      const form = event.target.closest('form[data-provider-policy]');
      if (form) {
        policyDrafts.set(form.dataset.providerPolicy, PlanwharfModelPolicyUI.read(form));
        form.querySelector('[data-policy-edit-status]').textContent = 'Unsaved changes for this provider.';
        form.querySelector('[data-policy-discard]').disabled = false;
      }
    });
    $('orchestrator').addEventListener('submit', async event => {
      const form = event.target.closest('form[data-provider-policy]');
      if (!form) return;
      event.preventDefault();
      const id = form.dataset.providerPolicy;
      try {
        orchestra = await post('/api/providers/policy', { id, policy: PlanwharfModelPolicyUI.read(form) });
        policyDrafts.delete(id); renderPolicySection(); notice('Provider policy saved. Applies to every model in this provider.');
      } catch (error) { notice(error.message); }
    });
    $('orchestrator').addEventListener('change', async event => {
      const selector = event.target.closest('[data-policy-provider-select]');
      if (selector) {
        selectedPolicyProviderId = selector.value;
        renderPolicySection();
        return;
      }
      const gold = event.target.closest('[data-gold-model]');
      if (gold) {
        gold.disabled = true;
        try { orchestra = await post('/api/models/gold', { id: gold.value || null }); render(); }
        catch (error) { notice(error.message); render(); }
        finally { gold.disabled = false; }
        return;
      }
      const control = event.target.closest('[data-model-role]');
      if (!control) return;
      const enabled = control.checked; control.disabled = true;
      try { orchestra = await post('/api/models/roles', { id: control.dataset.modelRole, roles: { [control.dataset.role]: enabled } }); render(); }
      catch (error) { control.checked = !enabled; notice(error.message); }
      finally { control.disabled = false; }
    });
    $('orchestrator').addEventListener('click', async event => {
      const bulk = event.target.closest('[data-role-bulk]');
      if (bulk) {
        if (bulk.dataset.roleBulk === 'supervisor' && !(orchestra.models || []).some(model => model.roleEligibility?.supervisor === true && typeof model.roles?.supervisor === 'boolean')) { notice('This backend has no eligible Supervisor model. No role changes were sent.'); return; }
        bulk.disabled = true;
        try { orchestra = await post('/api/models/roles/bulk', { role: bulk.dataset.roleBulk, enabled: bulk.dataset.roleEnabled === 'true' }); render(); }
        catch (error) { notice(error.message); }
        finally { bulk.disabled = false; }
        return;
      }
      if (event.target.closest('[data-add-provider]')) {
        PlanwharfModelPolicyUI.openConnection(async input => {
          orchestra = await post('/api/providers/connections', input);
          selectedPolicyProviderId = input.id; renderBoard(); notice('Connection saved. Account access and model inference have not been verified.');
        });
        return;
      }
      if (event.target.closest('[data-policy-discard]')) {
        policyDrafts.delete(selectedPolicyProviderId); renderPolicySection(); return;
      }
      const researchReport = event.target && event.target.getAttribute && event.target.getAttribute('data-research-report');
      const researchStop = event.target && event.target.getAttribute && event.target.getAttribute('data-research-stop');
      const researchApprove = event.target && event.target.getAttribute && event.target.getAttribute('data-research-approve');
      const researchResume = event.target && event.target.getAttribute && event.target.getAttribute('data-research-resume');
      const leadId = event.target && event.target.getAttribute && event.target.getAttribute('data-lead');
      const modelId = event.target && event.target.getAttribute && event.target.getAttribute('data-model');
      const autonomyId = event.target && event.target.getAttribute && event.target.getAttribute('data-autonomy');
      if (autonomyId) {
        orchestra = await post('/api/orchestrator/autonomy', { projectId: autonomyId, enabled: event.target.getAttribute('data-autonomy-enable') === 'true' });
        render();
        return;
      }
      const pauseId = event.target && event.target.getAttribute && event.target.getAttribute('data-pause');
      const stopId = event.target && event.target.getAttribute && event.target.getAttribute('data-stop');
      const resumeId = event.target && event.target.getAttribute && event.target.getAttribute('data-resume');
      const approveId = event.target && event.target.getAttribute && event.target.getAttribute('data-approve');
      const grillStartId = event.target && event.target.getAttribute && event.target.getAttribute('data-grill-start');
      const grillStartKind = event.target && event.target.getAttribute && event.target.getAttribute('data-grill-kind');
      try {
        const provider = event.target.closest('[data-provider], [data-provider-show]');
        if (provider) {
          orchestra = await post('/api/catalog/settings', { providerId: provider.dataset.provider || provider.dataset.providerShow,
            ...(provider.dataset.provider ? { enabled: provider.dataset.providerEnabled === 'true' } : { showAll: provider.dataset.providerShowAll === 'true' }) });
          render(); return;
        }
        const workerStop = event.target.closest('[data-worker-stop]');
        if (workerStop) {
          workerStop.disabled = true;
          try {
            const body = await post('/api/orchestrator/worker/stop', { projectId: workerStop.dataset.workerStop, pid: Number(workerStop.dataset.workerPid) });
            orchestra = body.snapshot;
            render();
            notice('Worker stopped. Other slices continue.');
          } finally { workerStop.disabled = false; }
          return;
        }
        if (researchReport) {
          const response = await fetch(`/api/orchestrator/research/report?id=${encodeURIComponent(researchReport)}`);
          const body = await response.json();
          if (!response.ok) throw new Error(body.error || 'Report unavailable.');
          researchReports.set(researchReport, body.report || 'No report output yet.');
          renderBoard();
          return;
        }
        if (researchStop || researchResume || researchApprove) {
          const record = orchestra.researches.find(record => record.id === (researchStop || researchResume || researchApprove));
          const target = { id: record.id, ...(record.megaTaskId ? { projectId: record.projectId, megaTaskId: record.megaTaskId } : {}) };
          if (researchResume && record.megaTaskId) { const choices = (orchestra.execution?.checkpoints || []).filter(cp => cp.megaTaskId === record.megaTaskId); const selected = choices.length === 1 ? choices[0] : choices[Number(window.prompt('Choose checkpoint number:\n' + choices.map((cp, index) => `${index + 1}. ${cp.summary}`).join('\n'))) - 1]; if (!selected) throw new Error('Choose an explicit checkpoint.'); target.checkpointId = selected.id; }
          let body;
          const route = `/api/orchestrator/research/${researchApprove ? 'approve' : researchStop ? 'stop' : 'resume'}`;
          try { body = await post(route, target); } catch (error) { if (!error.confirmation || !window.confirm(error.message + '\nContinue remaining research?')) throw error; body = await post(route, { ...target, confirmChanges: true }); }
          if (body.ok === false) throw new Error(body.reason || 'Research action failed.');
          if (body.snapshot) orchestra = body.snapshot;
          render();
          return;
        }
        if (leadId) {
          orchestra = await post('/api/orchestrator/lead', { id: leadId });
          render();
          notice(`Orchestrator is now ${(orchestra.orchestratorLabel || leadId)}.`);
          return;
        }
        if (modelId) {
          const enabled = event.target.getAttribute('data-enabled') === '1';
          orchestra = await post('/api/orchestrator/model', { id: modelId, enabled });
          render();
          return;
        }
        if (pauseId) { orchestra = await executionPost('pause', pauseId); render(); notice('Scheduling paused. Active workers may finish.'); return; }
        if (stopId) {
          orchestra = await executionPost('stop', stopId);
          render();
          notice('Stopped. Checkpoint saved. Press Resume to continue from the last plan.');
          return;
        }
        if (resumeId) {
          orchestra = await executionPost('resume', resumeId);
          render();
          notice('Resumed from checkpoint.');
          return;
        }
        if (approveId) {
          orchestra = await executionPost('approve', approveId);
          render();
          notice('Run approved.');
          return;
        }
        if (grillStartId && (grillStartKind === 'me' || grillStartKind === 'docs')) {
          await startGrillChat(grillStartId, grillStartKind);
        }
      } catch (error) {
        notice(error.message);
      }
    });
  }

  ['worker-project-filter', 'worker-session-filter', 'worker-state-filter'].forEach(id => { if ($(id)) $(id).addEventListener('change', () => { renderChat(); renderBoard(); }); });
  if ($('orch-project')) $('orch-project').addEventListener('change', event => {
    projectId = event.target.value || null;
    try { localStorage.setItem('planwharf_chat_project', projectId || ''); } catch (_) {}
    if (embedded && projectId) window.dispatchEvent(new CustomEvent('planwharf-select-project', { detail: projectId }));
    render();
  });
  if (embedded) window.addEventListener('planwharf-project', event => {
    projectId = event.detail;
    render();
  });
  if (embedded) window.addEventListener('planwharf-view', event => {
    if (event.detail === 'orchestrator') refresh();
  });
  if (typeof window !== 'undefined') window.addEventListener('storage', event => {
    if (event.key === 'planwharf_chat_project') { projectId = event.newValue || null; render(); }
  });
  loadPlan().catch(() => {});
  refresh();
  setInterval(refresh, 4000);
  document.body.setAttribute('data-orch-ready', '1');
})();
