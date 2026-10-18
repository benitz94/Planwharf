'use strict';
(() => {
  const nativeWindow = new URLSearchParams(location.search).get('window');
  const view = nativeWindow === 'orchestrator' ? 'orchestrator' : 'planwharf';
  const id = crypto.randomUUID();
  window.PlanwharfChatSessionId = crypto.randomUUID();
  window.PlanwharfRunExecution = async (action, row) => {
    if (!row?.id) throw new Error('Select the target project.');
    await window.PlanwharfFocusReady;
    const target = { projectId: row.id, ...(row.megaTaskId ? { megaTaskId: row.megaTaskId } : {}) };
    if (action === 'resume' && row.megaTaskId) {
      const choices = row.resumableCheckpoints || [];
      let cp = choices[0];
      if (choices.length > 1) {
        const chosen = window.prompt('Choose checkpoint number:\n' + choices.map((item, index) => `${index + 1}. ${item.summary} (${item.createdAt})`).join('\n'));
        cp = choices[Number(chosen) - 1];
      }
      if (!cp) throw new Error('Choose an explicit checkpoint to resume.');
      target.megaTaskId = cp.megaTaskId; target.checkpointId = cp.id;
    }
    const send = value => fetch(`/api/orchestrator/${action}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    let response = await send(target);
    if (response.status === 409) {
      const result = await response.clone().json();
      if (result.requiresConfirmation && window.confirm(`${result.reason}\n${(result.diff?.changes || ['Repository changes require review.']).join('\n')}\nContinue remaining work?`)) response = await send({ ...target, confirmChanges: true });
    }
    return response;
  };
  const control = document.getElementById('watch-window');
  const projectView = document.getElementById('project-view');
  const orchestratorView = document.getElementById('orchestrator-view');
  const supervisorView = document.getElementById('supervisor-view');
  let activeView = 'planwharf';
  const scrollPositions = { planwharf: 0, orchestrator: 0, supervisor: 0 };
  function showView(next, initial = false) {
    if (!projectView || !orchestratorView) return;
    scrollPositions[activeView] = window.scrollY;
    const supervisorRequested = next === 'supervisor' && supervisorView;
    activeView = supervisorRequested || next === 'orchestrator' ? 'orchestrator' : 'planwharf';
    projectView.hidden = activeView !== 'planwharf';
    orchestratorView.hidden = activeView !== 'orchestrator';
    if (supervisorRequested) {
      if (window.PlanwharfSelectWorkspacePanel) window.PlanwharfSelectWorkspacePanel('supervisor');
      else window.PlanwharfRequestedWorkspacePanel = 'supervisor';
    }
    document.body.dataset.appView = supervisorRequested ? 'supervisor' : activeView;
    const intro = document.getElementById('project-lede');
    if (intro) intro.textContent = supervisorRequested ? 'Execution oversight, alerts, and Reviewer advice.' : activeView === 'orchestrator' ? 'Orchestrator chat, workers, and provider usage.' : 'Goal, progress, current work, next action, and your notes.';
    if (control) {
      control.textContent = nativeWindow ? (view === 'planwharf' ? 'Open Orchestrator' : 'Open Planwharf') : activeView === 'planwharf' ? 'Show Orchestrator' : 'Show Planwharf';
      control.setAttribute('aria-pressed', String(activeView === 'orchestrator'));
    }
    if (!initial) {
      history.replaceState(null, '', supervisorRequested ? '#supervisor' : activeView === 'orchestrator' ? '#orchestrator' : '#project');
      window.scrollTo(0, scrollPositions[activeView]);
    }
    window.dispatchEvent(new CustomEvent('planwharf-view', { detail: document.body.dataset.appView }));
  }
  window.PlanwharfShowView = showView;
  control?.addEventListener('click', async () => { if (nativeWindow) { try { await post(view === 'planwharf' ? '/api/orchestrator/window' : '/api/planwharf/window', {}); } catch (error) { document.getElementById('toast').textContent = error.message; } } else showView(activeView === 'planwharf' ? 'orchestrator' : 'planwharf'); });
  const hashView = () => location.hash === '#supervisor' ? 'supervisor' : location.hash === '#orchestrator' ? 'orchestrator' : 'planwharf';
  window.addEventListener('hashchange', () => showView(hashView(), true));
  showView(hashView(), true);
  const header = document.querySelector('.topbar');
  if (header && typeof ResizeObserver !== 'undefined') {
    const controls = document.querySelector('.runtime-controls');
    const observer = new ResizeObserver(() => {
      const extra = controls && !controls.closest('dialog') ? (controls.getBoundingClientRect().height || 0) : 0;
      document.documentElement.style.setProperty('--app-header-height', `${header.getBoundingClientRect().height + extra}px`);
    });
    observer.observe(header);
    if (controls) observer.observe(controls);
  }
  const notificationsKey = 'planwharf_local_notifications';
  let notificationEventsPrimed = false;
  const notificationButton = document.createElement('button');
  notificationButton.type = 'button'; notificationButton.className = 'secondary compact';
  const notificationEnabled = () => localStorage.getItem(notificationsKey) === 'true';
  const labelNotifications = () => { notificationButton.textContent = `Notifications: ${notificationEnabled() ? 'On' : 'Off'}`; };
  if ('Notification' in window) {
    document.getElementById('app-quit')?.before(notificationButton); labelNotifications();
    notificationButton.addEventListener('click', async () => {
      const enabled = !notificationEnabled();
      const permission = enabled ? await Notification.requestPermission() : Notification.permission;
      localStorage.setItem(notificationsKey, String(enabled && permission === 'granted')); labelNotifications();
    });
    window.addEventListener('storage', labelNotifications);
    window.addEventListener('planwharf-evidence', event => {
      const seen = JSON.parse(localStorage.getItem('planwharf_notification_events') || '[]');
      for (const milestone of window.PlanwharfNotifications.important(event.detail || [])) {
        if (seen.includes(milestone.id)) continue;
        seen.push(milestone.id);
        if (notificationEventsPrimed && notificationEnabled() && Notification.permission === 'granted') {
          try { new Notification('Planwharf', { body: milestone.summary.slice(0, 250), tag: milestone.id }); } catch { /* Platform may not allow native notifications in app windows. */ }
        }
      }
      localStorage.setItem('planwharf_notification_events', JSON.stringify(seen.slice(-200)));
      notificationEventsPrimed = true;
    });
  }
  let focusSeen = 0;
  window.addEventListener('planwharf-project', event => {
    try { localStorage.setItem('planwharf_chat_project', event.detail); } catch { /* The recipient can also be selected in chat. */ }
    const projectId = event.detail;
    if (window.PlanwharfFocusedProjectId === projectId) return;
    window.PlanwharfFocusedProjectId = projectId;
    window.PlanwharfFocusReady = (window.PlanwharfFocusReady || Promise.resolve()).catch(() => {}).then(async () => {
      const response = await fetch('/api/focus', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId }) });
      if (!response.ok) throw new Error('The selected project could not be synchronized with the backend.');
    });
    window.PlanwharfFocusReady.catch(() => {});
  });
  document.addEventListener('click', async event => {
    const button = event.target.closest('[data-copy-output], [data-copy-conversation]');
    if (!button) return;
    const text = button.dataset.copyConversation ? Array.from(document.getElementById(button.dataset.copyConversation).querySelectorAll('.chat-turn:not(.is-pending)')).map(turn => `You: ${turn.querySelector('.chat-you p')?.textContent || ''}\nOrchestrator: ${turn.querySelector('[data-chat-output]')?.textContent || ''}`).join('\n\n') : button.parentElement.querySelector('[data-chat-output]')?.textContent || '';
    const toast = document.getElementById('toast');
    if (!text.trim()) { if (toast) toast.textContent = 'No chat output to copy yet.'; return; }
    try {
      if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(text);
      else {
        const field = document.createElement('textarea');
        field.value = text;
        field.style.position = 'fixed';
        field.style.opacity = '0';
        document.body.append(field);
        try { field.select(); if (!document.execCommand('copy')) throw new Error('Clipboard unavailable.'); }
        finally { field.remove(); }
      }
      if (toast) toast.textContent = 'Output copied.';
    } catch (error) { if (toast) toast.textContent = `Could not copy chat: ${error.message}`; }
  });
  let skillsSaving = false, skillsVersion = 0;
  const skillsDate = value => {
    const date = new Date(value);
    return value && Number.isFinite(date.getTime()) ? date.toLocaleString('en-GB', { year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }) : 'not recorded';
  };
  function displaySkills(data) {
    const control = document.getElementById('matt-skills-enabled');
    const status = document.getElementById('matt-skills-status');
    if (!Number.isSafeInteger(data.skillCount) || data.skillCount < 0) {
      if (control) { control.disabled = true; control.dataset.legacyBackend = 'true'; }
      if (status) status.textContent = 'Previous Planwharf backend detected. Its legacy cache does not report a complete skill catalog or confirm use by all models. Use Quit application, then relaunch Planwharf; reopening the window reuses the old backend.';
      return;
    }
    if (control?.dataset.legacyBackend) { control.disabled = false; delete control.dataset.legacyBackend; }
    if (control && !control.disabled) control.checked = data.enabled !== false;
    if (!status) return;
    const revision = data.sha ? data.sha.slice(0, 7) : 'unavailable';
    const usage = data.enabled === false ? 'Matt skills OFF for new model requests.' : 'Matt skills ON for all models.';
    const dates = `Revision date: ${skillsDate(data.date)}. Installed locally: ${skillsDate(data.installedAt)}. Last checked: ${skillsDate(data.checkedAt)}.`;
    status.textContent = data.checking ? `${usage} Checking for updates... ${dates}` : data.status === 'current' && data.skillCount > 0 && data.catalogComplete !== false
      ? `${usage} ${data.skillCount} skills. ${data.updated ? 'Installed' : 'Up to date with'} latest main revision ${revision}. ${dates}`
      : data.status === 'current' || data.status === 'incomplete' ? `Matt skills catalog incomplete at revision ${revision}. Check skills updates before running models with Matt skills enabled.`
      : `${usage} Update status: ${data.status || 'unknown'}; revision ${revision}. ${dates} ${data.error || ''}`;
  }
  async function refreshSkills() {
    if (skillsSaving) return;
    const version = skillsVersion;
    try { const response = await fetch('/api/skills'); const data = response.ok ? await response.json() : null; if (data && !skillsSaving && version === skillsVersion) displaySkills(data); } catch {}
  }
  function displayProviderStatus(data) {
    const status = document.getElementById('provider-models-status');
    if (!status) return;
    if (data.checking) { status.textContent = 'Checking provider model catalogs for the current login...'; return; }
    const providers = data.providers || [data];
    status.textContent = providers.map(provider => {
      const name = provider.label || provider.providerId || 'Provider';
      if (provider.status === 'disabled') return `${name}: provider disabled.`;
      const checked = provider.checkedAt ? ` Last checked: ${skillsDate(provider.checkedAt)}.` : '';
      const access = provider.preservingPreviousAvailability === true
        ? ' Last verified availability is retained for the same account.'
        : provider.preservingPreviousAvailability === false
          ? ' Cached access is not restored; fresh verification is required.'
          : ' Current model access is unverified.';
      return provider.status === 'current'
        ? `${name}: ${(provider.ids || []).length} exact models listed for the current login.${checked}`
        : `${name}: availability unread. ${provider.reason || 'No current catalog check recorded.'}${checked}${access}`;
    }).join(' ');
  }
  async function refreshProviderStatus() {
    const status = document.getElementById('provider-models-status');
    if (!status || document.getElementById('provider-models-refresh')?.disabled) return;
    try {
      const response = await fetch('/api/models/status');
      if (!response.ok) return;
      const data = await response.json();
      displayProviderStatus(data);
    } catch {}
  }
  document.getElementById('matt-skills-enabled')?.addEventListener('change', async event => {
    const control = event.target, enabled = control.checked;
    skillsSaving = true; skillsVersion += 1;
    control.disabled = true;
    try { const data = await post('/api/skills/settings', { enabled }); control.disabled = false; displaySkills(data); }
    catch (error) { control.checked = !enabled; document.getElementById('matt-skills-status').textContent = error.message; }
    finally { skillsSaving = false; control.disabled = false; }
  });
  document.getElementById('matt-skills-refresh')?.addEventListener('click', async event => {
    event.target.disabled = true;
    try { displaySkills(await post('/api/skills', undefined)); }
    catch (error) { document.getElementById('matt-skills-status').textContent = error.message; }
    finally { event.target.disabled = false; }
  });
  document.getElementById('provider-models-refresh')?.addEventListener('click', async event => {
    event.target.disabled = true;
    const status = document.getElementById('provider-models-status');
    status.textContent = 'Checking provider model catalogs for the current login...';
    try {
      const data = await post('/api/models/refresh', {});
      displayProviderStatus(data.providerModels);
      window.dispatchEvent(new CustomEvent('planwharf-view', { detail: document.body.dataset.appView }));
    } catch (error) { status.textContent = error.message; }
    finally { event.target.disabled = false; }
  });
  if (document.getElementById('matt-skills-status')) { refreshSkills(); refreshProviderStatus(); setInterval(() => { refreshSkills(); refreshProviderStatus(); }, 4000); }
  async function post(url, value) {
    const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(value) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'Window request failed.');
    return data;
  }
  async function pulse() {
    try {
      const data = await post('/api/app/heartbeat', { id, view, focusSeen });
      if (data.title) document.title = data.title;
      focusSeen = data.focusVersion || 0;
      if (data.focus) { window.focus(); if (data.requestedView) showView(data.requestedView); }
    } catch { /* A disconnected UI is not a request to stop work. */ }
  }
  document.getElementById('app-quit')?.addEventListener('click', async () => {
    if (!confirm('Quit Planwharf? Active work will follow the shutdown and checkpoint policy.')) return;
    try { await post('/api/app/quit', {}); window.close(); }
    catch (error) { document.getElementById('toast').textContent = error.message; }
  });
  window.addEventListener('pagehide', () => { navigator.sendBeacon('/api/app/heartbeat', new Blob([JSON.stringify({ id, view, closed: true })], { type: 'application/json' })); });
  window.addEventListener('pageshow', pulse);
  pulse();
  setInterval(pulse, 3000);
})();
