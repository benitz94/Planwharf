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
      control.addEventListener('click', () => {
        window.PlanwharfShowView?.(view);
        if (view === 'orchestrator') window.PlanwharfSelectWorkspacePanel?.('overview');
      });
      navigation.append(control);
    }
    header.querySelector('.heading')?.after(navigation);
    const syncView = () => {
      const current = document.body.dataset.appView || 'planwharf';
      for (const control of navigation.children) {
        const selected = control.dataset.workspaceView === current;
        control.setAttribute('aria-current', selected ? 'page' : 'false');
      }
    };
    window.addEventListener('planwharf-view', syncView); syncView();
    const supervisorNav = document.getElementById('supervisor-navigation');
    const workspaceSupervisor = navigation.querySelector('[data-workspace-view="supervisor"]');
    const syncSupervisorName = () => {
      if (!supervisorNav || !workspaceSupervisor) return;
      const name = supervisorNav.textContent.trim() || 'Supervisor';
      workspaceSupervisor.textContent = name;
      workspaceSupervisor.title = name;
    };
    if (supervisorNav) new MutationObserver(syncSupervisorName).observe(supervisorNav, { childList: true, characterData: true, subtree: true });
    syncSupervisorName();
    const dialog = document.createElement('dialog'); dialog.id = 'workspace-settings';
    dialog.setAttribute('aria-labelledby', 'workspace-settings-title');
    dialog.innerHTML = '<div class="dialog-heading"><h2 id="workspace-settings-title">Workspace settings</h2><form method="dialog"><button class="secondary compact" aria-label="Close workspace settings">Close</button></form></div><p class="muted">Browser, appearance, notifications, and application controls.</p><div class="workspace-settings-body"></div><div class="workspace-settings-actions"></div>';
    document.body.append(dialog);
    const body = dialog.querySelector('.workspace-settings-body');
    const launchForm = document.getElementById('launch-browser-form');
    if (launchForm) {
      launchForm.querySelector('[data-close]')?.remove();
      body.append(launchForm);
    }
    const skillsPanel = document.getElementById('project-skills-panel');
    if (skillsPanel && skillsPanel.id === 'project-skills-panel') {
      const skills = document.createElement('section');
      skills.className = 'workspace-project-skills';
      skills.setAttribute('aria-label', 'Selected project skills');
      const heading = document.createElement('h2'); heading.textContent = 'Project skills';
      skills.append(heading, skillsPanel);
      document.querySelector('#orchestrator-view [data-workspace-pane="overview"]')?.append(skills);
    }
    const runtime = document.querySelector('.runtime-controls');
    if (runtime) {
      runtime.querySelector('small')?.remove();
      body.append(runtime);
    }
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
    const overview = document.querySelector('#orchestrator-view [data-workspace-pane="overview"]');
    const board = document.querySelector('#orchestrator-view .orch-board');
    if (overview && board) {
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
      overview.prepend(title, tabs); selectPanel('workers');
      const filters = overview.querySelector('.worker-filters');
      if (filters) {
        const detail = document.createElement('details'); detail.className = 'workspace-worker-filters';
        const summary = document.createElement('summary'); summary.textContent = 'Filter worker sessions';
        filters.before(detail); detail.append(summary, filters);
      }
    }
    new ResizeObserver(() => document.documentElement.style.setProperty('--app-header-height', `${header.getBoundingClientRect().height}px`)).observe(header);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true }); else boot();
})();
