'use strict';
(() => {
  const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const boot = () => {
    let data = null, snapshot = null, projectId = null, available = false, settingsDirty = false, posting = false, lastContent = '', modelSignature = '', migrated = false;
    const draftKey = id => 'planwharf_supervisor_draft:' + encodeURIComponent(id || 'global');
    let legacyName = '';
    try { const value = JSON.parse(localStorage.getItem('planwharf_harbor_v2') || '{}').supervisorName; if (typeof value === 'string') legacyName = value; } catch {}
    const standalone = !document.getElementById('project-view');
    let root = document.getElementById('supervisor-view');
    if (!root) { root = document.createElement('section'); root.id = 'supervisor-view'; root.hidden = true; root.dataset.workspacePane = 'supervisor'; document.querySelector('.orch-board')?.append(root); }
    root.setAttribute('aria-label', 'Supervisor');
    root.innerHTML = `<header class="supervisor-heading"><div><p class="eyebrow">EXECUTION OVERSIGHT</p><h1 id="supervisor-title">Supervisor</h1><p class="muted">Live evidence, understandable updates, and Supervisor advice.</p></div><label>Project<select id="supervisor-project" aria-label="Supervisor project"></select></label><div class="supervisor-actions">${standalone ? '' : '<button class="secondary" type="button" data-return-project>Project Home</button>'}<button class="secondary" type="button" data-return-orchestrator>Orchestrator</button></div></header>
      <details class="supervisor-preferences"><summary>Name and Supervisor settings</summary><form id="supervisor-settings-form">
        <label>Supervisor name<input name="name" maxlength="80" placeholder="Supervisor" autocomplete="off"></label>
        <label>Supervisor model for AI answers<select name="modelId"><option value="">Live facts only (no AI calls)</option></select></label>
        <label class="choice"><input type="checkbox" name="autoReview"> Analyze new anomalies with this Supervisor (maximum 3 per hour)</label>
        <p class="muted">Monitoring always uses live backend evidence. AI analysis uses the exact selected Supervisor in a separate read-only conversation. It cannot approve, dispatch or edit work.</p><button class="secondary" type="submit">Save Supervisor settings</button>
      </form></details>
      <p id="supervisor-error" class="notice" role="status" hidden></p><p id="supervisor-feed" class="muted" role="status">Waiting for execution evidence.</p>
      <div class="supervisor-layout"><section class="supervisor-observation" aria-label="Live execution overview"><h2>What is happening</h2><p id="supervisor-summary"></p>
        <h2>Alerts</h2><label class="choice"><input type="checkbox" id="supervisor-show-ignored"> Show ignored alerts</label><div id="supervisor-alerts"></div>
        <h2>Workers</h2><div id="supervisor-workers"></div><h2>Reviewers</h2><div id="supervisor-reviewers"></div><details><summary>Recent project events</summary><ol id="supervisor-events"></ol></details>
      </section><section class="supervisor-conversation" aria-label="Ask the Supervisor"><h2>Ask the Supervisor</h2><div id="supervisor-thread" class="supervisor-thread" aria-live="polite"></div>
        <form id="supervisor-ask-form"><label for="supervisor-question">Your question</label><textarea id="supervisor-question" required maxlength="8000" rows="4" placeholder="What is happening? Which worker needs attention? What do you suggest?"></textarea><p id="supervisor-answer-mode" class="muted"></p><button class="primary" type="submit">Ask Supervisor</button></form>
      </section></div>`;
    const $ = id => document.getElementById(id);
    const settings = $('supervisor-settings-form');
    const nav = document.createElement('button'); nav.id = 'supervisor-navigation'; nav.type = 'button'; nav.className = 'secondary compact'; nav.textContent = 'Supervisor'; nav.setAttribute('aria-controls', 'supervisor-view'); nav.setAttribute('aria-pressed', String(document.body.dataset.appView === 'supervisor'));
    $('theme-toggle')?.before(nav);
    const notice = document.createElement('aside'); notice.id = 'supervisor-notice'; notice.className = 'supervisor-notice'; notice.hidden = true; notice.setAttribute('role', 'alert'); document.body.append(notice);
    const evidence = document.createElement('dialog'); evidence.id = 'supervisor-evidence'; evidence.setAttribute('aria-label', 'Execution evidence'); evidence.innerHTML = '<h2>Execution evidence</h2><pre></pre><form method="dialog"><button class="secondary">Close</button></form>'; document.body.append(evidence);
    const seen = new Set();
    function show() {
      if (window.PlanwharfShowView) window.PlanwharfShowView('supervisor');
      else window.PlanwharfSelectWorkspacePanel?.('supervisor');
      notice.hidden = true;
    }
    nav.addEventListener('click', show);
    root.querySelector('[data-return-project]')?.addEventListener('click', () => window.PlanwharfShowView?.('planwharf'));
    root.querySelector('[data-return-orchestrator]').addEventListener('click', () => window.PlanwharfSelectWorkspacePanel?.('overview'));
    function fail(error) { $('supervisor-error').textContent = error.message; $('supervisor-error').hidden = false; }
    async function post(action, body) {
      const response = await fetch('/api/supervisor/' + action, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      const result = await response.json(); if (!response.ok) throw Error(result.error || 'Supervisor request failed.');
      data = result; render(); return result;
    }
    function project() { return data?.projects?.find(p => p.projectId === projectId); }
    function render() {
      if (!data) return;
      const p = project(), activeAlerts = (p?.alerts || []).filter(a => !a.ignored);
      const name = typeof data.name === 'string' && data.name.trim() ? data.name.trim() : 'Supervisor';
      const eligibleModels = (data.models || []).filter(model => snapshot?.models?.some(candidate => candidate.id === model.id && candidate.roles?.supervisor === true && candidate.roleEligibility?.supervisor === true));
      const selectedAI = eligibleModels.some(model => model.id === data.modelId && model.available);
      $('supervisor-title').textContent = name;
      nav.textContent = activeAlerts.length ? `${name} · ${activeAlerts.length}` : name;
      nav.setAttribute('aria-label', `Open Supervisor${activeAlerts.length ? ', ' + activeAlerts.length + ' active alerts' : ''}`);
      const options = (snapshot?.projects || []).map(p => `<option value="${escape(p.id)}"${p.id === projectId ? ' selected' : ''}>${escape(p.name)}</option>`).join('');
      if (document.activeElement !== $('supervisor-project')) $('supervisor-project').innerHTML = '<option value="">Select a project</option>' + options;
      const signature = JSON.stringify(eligibleModels);
      if (signature !== modelSignature) {
        const value = settings.elements.modelId.value || data.modelId;
        settings.elements.modelId.innerHTML = '<option value="">Live facts only (no AI calls)</option>' + eligibleModels.map(m => `<option value="${escape(m.id)}"${m.available ? '' : ' disabled'}>${escape(m.label)} · ${escape(m.provider)}/${escape(m.model)}${m.available ? '' : ' · ' + escape(m.reason || m.unavailableReason || 'unavailable')}</option>`).join('');
        settings.elements.modelId.value = value; modelSignature = signature;
      }
      if (!settingsDirty && !settings.contains(document.activeElement)) { settings.elements.name.value = name; settings.elements.modelId.value = selectedAI ? data.modelId : ''; settings.elements.autoReview.checked = selectedAI && data.autoReview === true; }
      $('supervisor-feed').textContent = !available ? 'Execution updates are unavailable. No current state is being inferred.' : p ? 'Live monitoring follows backend updates. AI is not polled continuously.' : 'Select a project to follow its work.';
      $('supervisor-summary').textContent = available ? p?.summary || 'No project selected.' : 'Live evidence is temporarily unavailable.';
      $('supervisor-answer-mode').textContent = data.modelId ? selectedAI ? 'AI answer from the selected Supervisor model. Suggestions require your action.' : 'Selected Supervisor model unavailable or not explicitly enabled for this role. Choose Live facts only or an eligible Supervisor.' : 'Live facts only. Enable the Supervisor role for a compatible model to use AI interpretation.';
      $('supervisor-ask-form').querySelector('button').disabled = posting || data.busy || !available || !p || Boolean(data.modelId && !selectedAI);
      const content = JSON.stringify([p, available, $('supervisor-show-ignored').checked]);
      if (content !== lastContent) {
        const previousScroll = $('supervisor-thread').scrollTop;
        const followLatest = $('supervisor-thread').scrollHeight - previousScroll - $('supervisor-thread').clientHeight < 80;
        const alerts = available ? (p?.alerts || []).filter(a => !a.ignored || $('supervisor-show-ignored').checked) : [];
        $('supervisor-alerts').innerHTML = alerts.map(a => `<article class="supervisor-alert severity-${escape(a.severity)}${a.ignored ? ' is-ignored' : ''}"><h3>${escape(a.title)}${a.ignored ? ' · ignored' : ''}</h3><p>${escape(a.message)}</p><div class="supervisor-actions">${a.actions.filter(action => !a.ignored || action !== 'ignore').map(action => `<button class="secondary compact" type="button" data-supervisor-action="${action}" data-alert="${escape(a.id)}">${{ inspect: 'Inspect evidence', ask: 'Ask Supervisor', ignore: 'Ignore', pause: 'Pause after active work', stop: 'Stop and checkpoint' }[action]}</button>`).join('')}</div></article>`).join('') || `<p class="muted">${available ? 'No active alerts for this project.' : 'Alert status unavailable.'}</p>`;
        for (const role of ['worker', 'reviewer']) {
          const entries = available ? (p?.workers || []).filter(w => w.role === role) : [];
          $('supervisor-' + (role === 'worker' ? 'workers' : 'reviewers')).innerHTML = entries.map(w => `<article class="supervisor-assignment"><header><strong>${escape(w.task)}</strong><span class="badge">${escape(w.status)}</span></header><p>${escape(w.summary)}</p><button type="button" class="secondary compact" data-inspect-assignment="${escape(w.id)}">Inspect assignment</button><p class="muted">${escape(w.provider || 'unknown')}/${escape(w.model || 'unknown')} · ${escape(w.modelId || 'not retained')}</p>${w.activity ? `<details><summary>Recorded activity</summary><p>${escape(w.activity)}</p></details>` : ''}</article>`).join('') || `<p class="muted">${available ? `No ${role} assignments recorded for this execution.` : 'Updates unavailable.'}</p>`;
        }
        $('supervisor-events').innerHTML = (p?.timeline || []).map(e => `<li>${escape(e.summary)}</li>`).join('');
        $('supervisor-thread').innerHTML = (p?.turns || []).map(t => `<article><p><strong>You${t.automatic ? ' · anomaly review' : ''}</strong>: ${escape(t.question)}</p><p class="supervisor-answer">${escape(t.answer || 'Review in progress…')}</p><small>${escape(['supervisor', 'reviewer'].includes(t.mode) ? `${t.provider || ''}/${t.model || t.modelId}` : 'Live backend evidence')} · ${escape(t.status)}</small></article>`).join('') || '<p class="muted">Ask about progress, a worker, a reviewer or an alert.</p>';
        lastContent = content;
        $('supervisor-thread').scrollTop = followLatest ? $('supervisor-thread').scrollHeight : previousScroll;
      }
      if (available) {
        const fresh = activeAlerts.filter(a => !seen.has(a.id));
        if (fresh.length && (root.hidden || root.checkVisibility?.() === false)) {
          const alert = fresh[0]; notice.innerHTML = `<strong>${escape(data.name)}: ${escape(alert.title)}</strong><p>${escape(alert.message)}</p><button class="secondary compact" data-open-supervisor>Open Supervisor</button><button class="secondary compact" data-dismiss-supervisor>Dismiss notice</button>`; notice.hidden = false;
          notice.querySelector('[data-open-supervisor]').addEventListener('click', show); notice.querySelector('[data-dismiss-supervisor]').addEventListener('click', () => { notice.hidden = true; });
        }
        for (const a of activeAlerts) seen.add(a.id);
        if (!activeAlerts.length) notice.hidden = true;
      }
    }
    window.addEventListener('planwharf-orchestrator', event => {
      if (projectId !== event.detail.projectId) {
        try { localStorage.setItem(draftKey(projectId), $('supervisor-question').value); $('supervisor-question').value = localStorage.getItem(draftKey(event.detail.projectId)) || ''; } catch { /* Keep the current draft if storage is unavailable. */ }
        lastContent = '';
      }
      snapshot = event.detail.snapshot || snapshot; projectId = event.detail.projectId; available = event.detail.available;
      if (snapshot?.supervisor) data = snapshot.supervisor;
      render();
      if (data && !migrated) {
        migrated = true;
        if (!data.configured && legacyName && legacyName.trim() !== 'Supervisor') void post('settings', { name: legacyName }).catch(fail);
      }
    });
    window.addEventListener('planwharf-workspace-panel', event => { nav.setAttribute('aria-pressed', String(event.detail === 'supervisor')); if (event.detail === 'supervisor') notice.hidden = true; });
    $('supervisor-question').addEventListener('input', () => { try { localStorage.setItem(draftKey(projectId), $('supervisor-question').value); } catch {} });
    $('supervisor-project').addEventListener('change', event => {
      const select = document.getElementById(standalone ? 'orch-project' : 'project-select');
      if (select) { select.value = event.target.value; select.dispatchEvent(new Event('change', { bubbles: true })); }
    });
    settings.addEventListener('input', () => { settingsDirty = true; });
    settings.addEventListener('change', () => { settingsDirty = true; });
    settings.addEventListener('submit', async event => {
      event.preventDefault();
      try { await post('settings', { name: settings.elements.name.value, modelId: settings.elements.modelId.value, autoReview: settings.elements.autoReview.checked }); settingsDirty = false; $('supervisor-error').hidden = true; settings.elements.name.value = data.name; }
      catch (error) { fail(error); }
    });
    $('supervisor-show-ignored').addEventListener('change', render);
    root.addEventListener('click', async event => {
      const assignment = event.target.closest('[data-inspect-assignment]');
      if (assignment) { window.PlanwharfInspectAssignment?.(assignment.dataset.inspectAssignment); return; }
      const button = event.target.closest('[data-supervisor-action]'); if (!button) return;
      const p = project(), alert = p?.alerts.find(a => a.id === button.dataset.alert); if (!alert || !available) return;
      try {
        const action = button.dataset.supervisorAction;
        if (action === 'inspect') { evidence.querySelector('pre').textContent = JSON.stringify({ alert, assignments: p.workers, events: p.timeline }, null, 2); evidence.showModal(); }
        if (action === 'ask') { $('supervisor-question').value = `Explain this alert and suggest what I should do: ${alert.title}. ${alert.message}`; $('supervisor-question').focus(); }
        if (action === 'ignore') await post('ignore', { projectId, alertId: alert.id });
        if (['pause', 'stop'].includes(action)) {
          if (!p.canPause || alert.megaTaskId !== p.megaTaskId) throw Error('This execution is no longer running. Wait for fresh evidence.');
          if (action === 'stop' && !window.confirm('Stop the active workers for this project and save a checkpoint?')) return;
          button.disabled = true;
          const response = await fetch('/api/orchestrator/' + action, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ projectId, megaTaskId: p.megaTaskId }) });
          const result = await response.json(); if (!response.ok) throw Error(result.error || 'Execution action failed.');
          snapshot = result; data = result.supervisor || data; render();
        }
      } catch (error) { fail(error); } finally { button.disabled = false; }
    });
    $('supervisor-ask-form').addEventListener('submit', async event => {
      event.preventDefault(); if (posting || !available || !project()) return;
      const question = $('supervisor-question').value, requestedProject = projectId; posting = true; render();
      try {
        await post('ask', { projectId: requestedProject, question });
        if (projectId === requestedProject && $('supervisor-question').value === question) $('supervisor-question').value = '';
        try { if (localStorage.getItem(draftKey(requestedProject)) === question) localStorage.removeItem(draftKey(requestedProject)); } catch {}
        $('supervisor-error').hidden = true;
      }
      catch (error) { fail(error); } finally { posting = false; render(); }
    });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true }); else boot();
})();
