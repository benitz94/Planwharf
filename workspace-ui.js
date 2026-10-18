'use strict';
(() => {
  function boot() {
    const header = document.querySelector('.topbar');
    if (!header) return;
    const button = (label, className = 'secondary compact') => {
      const element = document.createElement('button');
      element.type = 'button'; element.textContent = label; element.className = className;
      return element;
    };
    const navigation = document.createElement('nav');
    navigation.className = 'workspace-navigation'; navigation.setAttribute('aria-label', 'Workspace views');
    for (const [view, label] of [['planwharf', 'Overview'], ['orchestrator', 'Workspace'], ['supervisor', 'Supervisor']]) {
      const control = button(label); control.dataset.workspaceView = view;
      control.addEventListener('click', () => window.PlanwharfShowView?.(view));
      navigation.append(control);
    }
    header.querySelector('.heading')?.after(navigation);
    const syncView = () => {
      for (const control of navigation.children) {
        const selected = control.dataset.workspaceView === (document.body.dataset.appView || 'planwharf');
        control.setAttribute('aria-current', selected ? 'page' : 'false');
      }
    };
    window.addEventListener('planwharf-view', syncView); syncView();
    // Keep existing controls and their listeners; only move them into a native dialog.
    const dialog = document.createElement('dialog'); dialog.id = 'workspace-settings';
    dialog.setAttribute('aria-labelledby', 'workspace-settings-title');
    dialog.innerHTML = '<div class="dialog-heading"><h2 id="workspace-settings-title">Workspace settings</h2><form method="dialog"><button class="secondary compact" aria-label="Close workspace settings">Close</button></form></div><p class="muted">Browser, appearance, notifications, and application controls.</p><div class="workspace-settings-body"></div><div class="workspace-settings-actions"></div>';
    document.body.append(dialog);
    const browserForm = document.createElement('form'); browserForm.id = 'workspace-browser-settings';
    browserForm.innerHTML = '<h3>Launch browser</h3><label>Browser<select name="browser"><option value="auto">Automatic detection</option><option value="default">System default browser</option><option value="custom">Custom executable</option></select></label><label>Custom executable path<input name="path" placeholder="Absolute path to the browser executable"></label><label>Custom browser family<select name="family"><option value="chromium">Chromium (Brave, Opera, Chrome, Edge)</option><option value="firefox">Firefox</option></select></label><p class="muted">Used next time you launch Planwharf.exe or the platform launcher. Quit and relaunch to apply.</p><button class="secondary" type="submit">Save browser</button><p role="status" data-browser-status></p>';
    dialog.querySelector('.workspace-settings-body').append(browserForm);
    const browserStatus = browserForm.querySelector('[data-browser-status]');
    const browserChoice = browserForm.elements.browser;
    const customFields = () => {
      for (const name of ['path', 'family']) browserForm.elements[name].closest('label').hidden = browserChoice.value !== 'custom';
    };
    browserChoice.addEventListener('change', customFields); customFields();
    fetch('/api/settings/browser').then(async response => {
      if (!response.ok) throw Error('Could not load browser preferences.');
      const data = await response.json();
      for (const browser of data.browsers) {
        const option = document.createElement('option'); option.value = browser.id;
        option.textContent = browser.label + (browser.path ? '' : ' (not detected)'); option.disabled = !browser.path; browserChoice.append(option);
      }
      browserChoice.value = data.selected.id;
      browserForm.elements.path.value = data.selected.path || '';
      browserForm.elements.family.value = data.selected.family || 'chromium'; customFields();
    }).catch(error => { browserStatus.textContent = error.message; });
    browserForm.addEventListener('submit', async event => {
      event.preventDefault();
      const selected = { id: browserChoice.value };
      if (selected.id === 'custom') Object.assign(selected, { path: browserForm.elements.path.value.trim(), family: browserForm.elements.family.value });
      try {
        const response = await fetch('/api/settings/browser', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(selected) });
        const result = await response.json(); if (!response.ok) throw Error(result.error || 'Could not save browser.');
        browserStatus.textContent = 'Browser saved. Quit and relaunch Planwharf to apply.';
      } catch (error) { browserStatus.textContent = error.message; }
    });
    const runtime = document.querySelector('.runtime-controls');
    if (runtime) {
      const skills = document.createElement('section'); skills.className = 'workspace-project-skills';
      skills.setAttribute('aria-label', 'Selected project skills');
      skills.innerHTML = '<h2>Project skills</h2><p class="muted">Active skills must be added, downloaded and documented in the selected project.</p>';
      for (const id of ['matt-skills-enabled', 'matt-skills-status', 'matt-skills-refresh']) {
        const element = document.getElementById(id); if (element) skills.append(element.closest('label') || element);
      }
      const list = document.createElement('div'); list.id = 'project-skills-list'; skills.append(list);
      document.querySelector('#orchestrator-view .orch-board')?.append(skills);
      runtime.querySelector('small')?.remove();
      dialog.querySelector('.workspace-settings-body').append(runtime);
    }
    const preferences = document.querySelector('.supervisor-preferences');
    if (preferences) {
      const section = document.createElement('section'); section.className = 'workspace-supervisor-settings';
      section.innerHTML = '<h2>Supervisor settings</h2>';
      section.append(document.getElementById('supervisor-settings-form')); preferences.replaceWith(section);
      document.querySelector('#orchestrator-view .orch-board')?.append(section);
    }
    const appearance = document.getElementById('harbor-settings-button');
    if (appearance) dialog.querySelector('.workspace-settings-body').append(appearance);
    const settingsButton = button('Settings'); settingsButton.id = 'workspace-settings-button';
    settingsButton.setAttribute('aria-haspopup', 'dialog'); settingsButton.setAttribute('aria-controls', dialog.id);
    settingsButton.addEventListener('click', () => dialog.showModal());
    settingsButton.innerHTML = '<span aria-hidden="true">&#9881;</span> Settings';
    const actions = dialog.querySelector('.workspace-settings-actions');
    const moveActions = () => {
      for (const selector of ['[data-layout-reset]', '.app-update-controls']) {
        const control = header.querySelector(selector); if (control) actions.append(control);
      }
      for (const control of header.querySelectorAll('button')) {
        if (control.textContent.startsWith('Notifications:')) actions.append(control);
      }
    };
    moveActions(); new MutationObserver(moveActions).observe(header, { childList: true, subtree: true });
    const rail = document.querySelector('.rail');
    if (rail) {
      const foot = document.createElement('div'); foot.className = 'workspace-rail-bottom';
      foot.append(settingsButton); rail.append(foot);
    }
    const chatHeader = document.querySelector('#orchestrator-view .orch-chat-head');
    if (chatHeader) {
      const title = document.createElement('h2'); title.textContent = 'Conversation'; title.className = 'workspace-pane-title'; chatHeader.prepend(title);
      const hint = document.getElementById('dashboard-orch-ask-hint');
      if (hint) {
        const detail = document.createElement('details'); detail.className = 'workspace-chat-help';
        const summary = document.createElement('summary'); summary.textContent = 'How to direct this workspace';
        hint.before(detail); detail.append(summary, hint);
      }
      const input = document.getElementById('dashboard-orch-ask-input');
      if (input) input.placeholder = 'Message the orchestrator…';
    }
    const board = document.querySelector('#orchestrator-view .orch-board');
    if (board) {
      const title = document.createElement('h2'); title.className = 'workspace-pane-title'; title.textContent = 'Activity';
      const tabs = document.createElement('nav'); tabs.className = 'workspace-activity-tabs'; tabs.setAttribute('aria-label', 'Activity sections');
      for (const [panel, label] of [['workers', 'Workers'], ['history', 'History'], ['models', 'Models & usage']]) {
        const tab = button(label); tab.setAttribute('aria-controls', 'dashboard-orchestrator');
        tab.addEventListener('click', () => selectPanel(panel)); tab.dataset.panel = panel; tabs.append(tab);
      }
      function selectPanel(panel) {
        board.dataset.panel = panel;
        for (const tab of tabs.children) tab.setAttribute('aria-pressed', String(tab.dataset.panel === panel));
      }
      board.prepend(title, tabs); selectPanel('workers');
      const filters = board.querySelector('.worker-filters');
      if (filters) {
        const detail = document.createElement('details'); detail.className = 'workspace-worker-filters';
        const summary = document.createElement('summary'); summary.textContent = 'Filter worker sessions';
        filters.before(detail); detail.append(summary, filters);
      }
    }
    // The runtime controls moved out of the header; observe its final height.
    new ResizeObserver(() => document.documentElement.style.setProperty('--app-header-height', `${header.getBoundingClientRect().height}px`)).observe(header);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true }); else boot();
})();
