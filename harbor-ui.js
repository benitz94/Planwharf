'use strict';
(() => {
  const SETTINGS_KEY = 'planwharf_harbor_v2';
  const SESSION_KEY = 'planwharf_harbor_v2_ensign';
  const FLAGS = {
    classic: { name: 'Nautical', field: '#163c59', ink: '#f5e8ba', art: '<path d="M0 24h96M32 0v64" stroke="#f5e8ba" stroke-width="10"/><path d="M0 24h96M32 0v64" stroke="#b64d50" stroke-width="4"/>' },
    tech: { name: 'Technology', field: '#123e42', ink: '#92e2cd', art: '<path d="M0 12h24l12 20h24l12 20h24M0 52h24l12-20h24l12-20h24" fill="none" stroke="currentColor" stroke-width="3"/><circle cx="48" cy="32" r="10" fill="none" stroke="currentColor" stroke-width="3"/>' },
    exploration: { name: 'Exploration', field: '#292c54', ink: '#ead49b', art: '<circle cx="48" cy="32" r="23" fill="none" stroke="currentColor"/><path d="m48 5 5 20 22 7-22 7-5 20-5-20-22-7 22-7Z" fill="currentColor"/>' },
    merchant: { name: 'Merchant', field: '#254337', ink: '#e0c485', art: '<path d="M17 42h64L69 53H30Zm28-31v28H26Zm7 0v28h24Z" fill="currentColor"/><path d="M13 57q10-5 20 0t20 0t20 0t15 0" fill="none" stroke="currentColor" stroke-width="2"/>' },
    pirate: { name: 'Pirate', field: '#222632', ink: '#e8e6da', art: '<path d="m24 15 49 40m-49 0 49-40" stroke="currentColor" stroke-width="5"/><path d="M32 24a16 16 0 0 1 32 0v11l-7 5v8H39v-8l-7-5Z" fill="currentColor"/><circle cx="41" cy="27" r="4" fill="#222632"/><circle cx="55" cy="27" r="4" fill="#222632"/><path d="m48 34-3 5h6Z" fill="#222632"/>' }
  };
  const DEFAULTS = { enabled: true, animations: 'full', flag: 'random', effects: true };
  const CONDITIONS = ['calm', 'light', 'moderate', 'rough', 'storm'];
  const WEATHER_INTERVAL = 60000;
  function shipSettingsFrom(value = {}) {
    return { ...settingsFrom(value), weatherMode: ['adaptive', 'random', 'manual'].includes(value.weatherMode) ? value.weatherMode : 'adaptive',
      seaCondition: CONDITIONS.includes(value.seaCondition) ? value.seaCondition : 'calm',
      notificationDensity: ['all', 'important', 'action'].includes(value.notificationDensity) ? value.notificationDensity : 'important' };
  }
  function weatherCondition(settings, row, random = Math.random) {
    if (settings.weatherMode === 'manual') return settings.seaCondition;
    const subs = Array.isArray(row?.subTasks) ? row.subTasks : [];
    // Planning complexity bounds visual variety. Execution health is never input.
    const count = subs.length, edges = subs.reduce((sum, sub) => sum + (Array.isArray(sub.dependencies) ? sub.dependencies.length : 0), 0);
    const maximum = settings.weatherMode === 'random' ? 4 : Math.min(4, 1 + Math.floor((count + edges) / 3));
    return CONDITIONS[Math.min(maximum, Math.max(0, Math.floor(random() * (maximum + 1))))];
  }
  function snapshotNotices(snapshot) {
    const notices = [];
    for (const row of snapshot?.projects || []) {
      const execution = row.megaTaskId || row.attemptId || row.jobId || 'idle';
      const add = (kind, detail, label, critical = false) => notices.push({ key: JSON.stringify([row.id, execution, kind, detail]), projectId: row.id, kind, label, critical });
      if (resolveState(row) === 'completed') add('completed', row.attemptId, 'Task completed');
      if (row.status === 'failed') add('worker-failed', row.attemptId, 'Worker failed', true);
      if (row.status?.startsWith('blocked_') || row.megaTask?.status === 'BLOCKED') add('blocked', row.status, 'Execution blocked', true);
      if (row.status === 'checkpointed') add('checkpoint', row.checkpointId || row.checkpoint?.at, 'Checkpoint created');
      if (row.status === 'needs_approval') add('approval', row.jobId, 'User approval required', true);
      const subs = row.subTasks || [];
      for (const result of snapshot.execution?.results || []) {
        if (!row.megaTaskId || result.megaTaskId !== row.megaTaskId) continue;
        const sub = subs.find(sub => sub.id === result.subTaskId && sub.megaTaskId === row.megaTaskId);
        if (!sub) continue;
        if (result.status === 'FAILED') add('worker-failed', result.resultId, 'Assignment failed', true);
        if (result.status === 'BLOCKED') add('blocked', result.resultId, 'Execution blocked', true);
        if (sub.type === 'AUDIT' && ['COMPLETE', 'FAILED', 'BLOCKED'].includes(result.status)) add('review-result', result.resultId, 'Reviewer result available');
      }
      for (const alert of snapshot.supervisor?.projects?.find(project => project.projectId === row.id)?.alerts || []) {
        if (alert.active !== false && !alert.ignored) add('anomaly', alert.id || alert.key, 'Supervisor anomaly detected', true);
      }
    }
    return notices;
  }
  function visibleNotice(notice, density) { return notice.critical || density === 'all' || density === 'important' && notice.kind !== 'checkpoint'; }
  function nameLabel(value) { return typeof value === 'string' && value.trim() ? Array.from(value.trim()).slice(0, 80).join('') : 'Supervisor'; }
  function settingsFrom(value = {}) {
    return { enabled: typeof value.enabled === 'boolean' ? value.enabled : true,
      animations: ['full', 'reduced', 'off'].includes(value.animations) ? value.animations : 'full',
      flag: value.flag === 'random' || Object.hasOwn(FLAGS, value.flag) ? value.flag : 'random',
      effects: typeof value.effects === 'boolean' ? value.effects : true };
  }
  function readSettings() {
    try { return shipSettingsFrom(JSON.parse(localStorage.getItem(SETTINGS_KEY)) || {}); } catch { return shipSettingsFrom(); }
  }
  function flagSvg(id) {
    const flag = Object.hasOwn(FLAGS, id) ? FLAGS[id] : FLAGS.classic;
    // Original local artwork: fixed viewport, no shared paint-server IDs or external assets.
    return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 64" width="96" height="64" aria-hidden="true" style="color:${flag.ink}"><path fill="${flag.field}" d="M0 0h96v64H0Z"/>${flag.art}<path d="M1 1h94v62H1Z" fill="none" stroke="${flag.ink}" stroke-opacity=".5"/></svg>`;
  }
  function resolveState(row, available = true) {
    if (!available || !row) return 'neutral';
    const parts = Array.isArray(row.dispatch) ? row.dispatch.filter(part => part && !part.historicalAssignment && (!row.megaTaskId || !part.megaTaskId || part.megaTaskId === row.megaTaskId)) : [];
    const live = row.workerAlive || parts.some(part => part.alive);
    const terminal = ['complete', 'checkpointed', 'failed'].includes(row.status) || row.status?.startsWith('blocked_');
    if (terminal && live) return row.status === 'complete' ? 'running' : 'pausing';
    // A raw PID may be reused; an ambiguous terminal snapshot is not completion proof.
    if (terminal && parts.some(part => part.observedAlive && !part.stoppedByUser)) return 'neutral';
    if (row.status === 'complete') return 'completed';
    if (row.status === 'checkpointed') return 'checkpoint';
    if (row.status === 'failed' || row.status?.startsWith('blocked_')) return 'failed';
    if (row.megaTask?.pauseRequested && live) return 'pausing';
    if (row.status === 'watching' || row.status === 'resuming') return live ? 'running' : 'preparing';
    if (['waiting_on_you', 'propose_grill', 'needs_approval'].includes(row.status)) return 'preparing';
    return row.status === 'idle' ? 'idle' : 'neutral';
  }
  const scene = `<svg class="harbor-scene" viewBox="0 0 1600 1000" preserveAspectRatio="xMaxYMid slice" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
    <g transform="translate(1600 0) scale(-1 1)"><image class="harbor-landscape harbor-pier" href="/assets/harbor/port-dusk.png" width="1600" height="1000" preserveAspectRatio="xMidYMid slice"/></g>
    <g class="harbor-water" fill="none" stroke="var(--harbor-line)" opacity=".12"><path d="M0 900q160-9 320 0t320 0t320 0t320 0t320 0M0 965q180-8 360 0t360 0t360 0t360 0t360 0"/></g>
    <g class="harbor-route"><g class="harbor-ship">
      <ellipse cx="-25" cy="-12" rx="390" ry="14" fill="#07131e" opacity=".35"/>
      <g class="harbor-wake" fill="none" stroke="var(--harbor-line)" stroke-width="3"><path d="M360-8q100 12 230 4m-220 8q130 12 260 3"/></g>
      <image class="harbor-vessel harbor-vessel-classic" href="/assets/harbor/vessel.png" x="-520" y="-680" width="1000" height="666.667"/>
      <image class="harbor-vessel harbor-vessel-pirate" href="/assets/harbor/vessel-pirate.png" x="-520" y="-680" width="1000" height="666.667"/>
      <g class="harbor-flag-mount" transform="translate(57 -668)"><g class="harbor-flag" transform="scale(.42)"></g></g>
      <svg class="harbor-captain" x="340" y="-300" width="88" height="88" viewBox="0 0 100 100" overflow="hidden"><defs><clipPath id="harbor-captain-crop"><rect width="100" height="100"/></clipPath></defs><image clip-path="url(#harbor-captain-crop)" href="/assets/harbor/captains-caribbean.png" width="500" height="200" preserveAspectRatio="none"/></svg>
      <circle class="harbor-captain-anchor" cx="384" cy="-300" r="0"/>
      <circle class="harbor-portholes harbor-beacon" cx="435" cy="-94" r="3" fill="var(--harbor-ensign-ink)"/>
      <g class="harbor-vessel-title" style="display:none"><title></title><defs><path id="harbor-name-curve" d="M-162-65 C-118-61-61-60 32-60"/></defs><text class="harbor-project-name" text-anchor="middle"><textPath href="#harbor-name-curve" startOffset="50%"></textPath></text></g>
    </g></g>
    <g class="harbor-atmosphere"><path d="M0 995h1600" stroke="var(--harbor-line)" stroke-opacity=".1"/></g>
  </svg>`;
  class HarborUI {
    constructor() {
      this.settings = readSettings(); this.snapshot = null; this.projectId = null; this.available = false;
      this.state = 'neutral'; this.executionId = null; this.randomFlag = null;
      this.notices = []; this.seenNotices = new Set(); this.weatherTimer = null; this.immersive = false;
      this.motion = window.matchMedia('(prefers-reduced-motion: reduce)');
    }
    init() {
      this.root = document.createElement('div'); this.root.id = 'harbor-background';
      this.root.className = 'harbor-background'; this.root.setAttribute('aria-hidden', 'true'); this.root.setAttribute('inert', '');
      this.root.innerHTML = scene; document.body.prepend(this.root);
      this.root.querySelector('.harbor-route').addEventListener('transitionend', event => {
        if (event.target !== this.root.querySelector('.harbor-route') || event.propertyName !== 'transform') return;
        if (this.root.dataset.state === 'resuming') this.root.dataset.state = 'running';
        if (this.state === 'completed') this.root.dataset.arrived = 'true';
      });
      this.createSettings(); this.createShipControls();
      window.addEventListener('planwharf-orchestrator', event => {
        this.snapshot = event.detail.snapshot; this.projectId = event.detail.projectId; this.available = event.detail.available;
        this.update(); this.observeNotices();
      });
      window.addEventListener('planwharf-project', event => { this.projectId = event.detail; this.update(); });
      window.addEventListener('storage', event => { if (event.key === SETTINGS_KEY || event.key === null) { this.settings = readSettings(); this.applySettings(); } });
      document.addEventListener('visibilitychange', () => { this.applyMotion(); this.scheduleWeather(); });
      this.motion.addEventListener('change', () => this.applyMotion());
      this.applySettings(); this.update();
    }
    effectiveFlag(choice = this.settings.flag) {
      if (Object.hasOwn(FLAGS, choice)) return choice;
      if (!this.randomFlag) {
        try { this.randomFlag = sessionStorage.getItem(SESSION_KEY); } catch {}
        if (!Object.hasOwn(FLAGS, this.randomFlag)) {
          const ids = Object.keys(FLAGS); this.randomFlag = ids[Math.floor(Math.random() * ids.length)];
          try { sessionStorage.setItem(SESSION_KEY, this.randomFlag); } catch {}
        }
      }
      return this.randomFlag;
    }
    applyMotion() {
      const motion = this.motion.matches && this.settings.animations === 'full' ? 'reduced' : this.settings.animations;
      this.root.dataset.motion = motion; this.root.dataset.suspended = String(document.hidden);
      if (motion !== 'full' && this.root.dataset.state === 'resuming') this.root.dataset.state = 'running';
    }
    applySettings() {
      this.root.hidden = !this.settings.enabled && !this.immersive;
      document.body.classList.toggle('harbor-enabled', this.settings.enabled || this.immersive);
      this.root.dataset.effects = String(this.settings.effects);
      const flag = this.effectiveFlag();
      if (this.root.dataset.flag !== flag) {
      this.root.querySelector('.harbor-flag').innerHTML = flagSvg(flag); this.root.dataset.flag = flag;
      this.root.style.setProperty('--harbor-livery', FLAGS[flag].field);
      this.root.style.setProperty('--harbor-ensign-ink', FLAGS[flag].ink);
      }
      this.applyMotion();
      this.applyWeather(); this.renderNotices();
    }
    update() {
      const row = this.snapshot?.projects?.find(row => row.id === this.projectId);
      const next = resolveState(row, this.available), execution = `${this.projectId || ''}/${row?.megaTaskId || row?.jobId || ''}`;
      const switched = this.root.dataset.project !== (this.projectId || '') || execution !== this.executionId;
      const prior = this.state;
      this.root.dataset.project = this.projectId || '';
      this.root.dataset.state = next === 'running' && prior === 'checkpoint' && !switched && this.root.dataset.motion === 'full' ? 'resuming' : next;
      this.root.dataset.underway = String(['running', 'pausing'].includes(next));
      this.root.dataset.switching = String(switched);
      if (next !== prior || switched) this.root.dataset.arrived = String(next === 'completed' && !['running', 'pausing'].includes(prior));
      this.state = next; this.executionId = execution;
      // Project changes snap to the new project's facts, rather than animating a
      // previous project's voyage. Subsequent same-project updates may transition.
      if (switched) { this.root.querySelector('.harbor-route').getBoundingClientRect(); this.root.querySelector('.harbor-pier').getBoundingClientRect(); }
      this.root.dataset.switching = 'false';
      const label = this.dialog.querySelector('[data-harbor-status]');
      label.textContent = row && this.available ? `${row.name}: ${next}.` : 'Execution state unavailable or no project selected.';
      if (switched) this.applyWeather();
      this.sceneLabel.textContent = `${row?.name || 'Ship'} · ${next} · Decorative ${this.root.dataset.weather || 'calm'} sea`;
    }
    applyWeather() {
      const row = this.snapshot?.projects?.find(row => row.id === this.projectId);
      this.root.dataset.weatherMode = this.settings.weatherMode;
      this.root.dataset.weather = weatherCondition(this.settings, row);
      const amplitude = [2, 5, 9, 14, 20][CONDITIONS.indexOf(this.root.dataset.weather)];
      this.root.querySelector('.harbor-water path')?.setAttribute('d', `M-100 690q100-${amplitude} 200 0t200 0t200 0t200 0t200 0t200 0t200 0t300 0M-100 825q140-${amplitude} 280 0t280 0t280 0t280 0t280 0t300 0M0 930q120-${amplitude} 240 0t240 0t240 0t240 0t240 0t400 0`);
      if (this.sceneLabel) this.sceneLabel.textContent = `${row?.name || 'Ship'} · ${this.state} · Decorative ${this.root.dataset.weather} sea`;
      this.scheduleWeather();
    }
    scheduleWeather() {
      clearTimeout(this.weatherTimer); this.weatherTimer = null;
      if (document.hidden || this.settings.animations === 'off' || this.settings.weatherMode === 'manual') return;
      this.weatherTimer = setTimeout(() => this.applyWeather(), WEATHER_INTERVAL);
    }
    toggleShipView(active) {
      if (active === this.immersive) return;
      if (active) {
        this.returnFocus = document.activeElement;
        this.savedScroll = { x: window.scrollX, y: window.scrollY };
        this.workspaceNodes = [...document.body.children].filter(node => node.matches('.app,.orch-top,.orch-shell,main'));
        this.workspaceNodes.forEach(node => { node.dataset.shipHidden = 'true'; node._shipInert = node.inert; node.inert = true; });
      } else {
        this.workspaceNodes?.forEach(node => { delete node.dataset.shipHidden; node.inert = node._shipInert; delete node._shipInert; });
      }
      this.immersive = active; document.body.classList.toggle('ship-view', active);
      this.shipControls.hidden = !active; this.shipButton.setAttribute('aria-pressed', String(active));
      this.root.hidden = !this.settings.enabled && !active;
      document.body.classList.toggle('harbor-enabled', this.settings.enabled || active);
      if (active) this.exitButton.focus({ preventScroll: true });
      else { window.scrollTo(this.savedScroll.x, this.savedScroll.y); if (this.returnFocus?.isConnected) this.returnFocus.focus({ preventScroll: true }); }
    }
    createShipControls() {
      this.shipButton = document.createElement('button'); this.shipButton.id = 'ship-view-button'; this.shipButton.type = 'button';
      this.shipButton.textContent = 'Ship View'; this.shipButton.className = 'secondary compact'; this.shipButton.setAttribute('aria-pressed', 'false');
      this.shipButton.addEventListener('click', () => this.toggleShipView(true));
      document.getElementById('harbor-settings-button').before(this.shipButton);
      this.shipControls = document.createElement('section'); this.shipControls.id = 'ship-view-controls'; this.shipControls.hidden = true;
      this.shipControls.setAttribute('aria-label', 'Ship View controls');
      this.exitButton = document.createElement('button'); this.exitButton.type = 'button'; this.exitButton.id = 'ship-view-exit'; this.exitButton.textContent = 'Return to workspace (Esc)';
      this.exitButton.addEventListener('click', () => this.toggleShipView(false));
      const settingsButton = document.createElement('button'); settingsButton.type = 'button'; settingsButton.textContent = 'Ship settings'; settingsButton.addEventListener('click', () => document.getElementById('harbor-settings-button').click());
      this.sceneLabel = document.createElement('p'); this.sceneLabel.id = 'ship-scene-label';
      this.shipControls.append(this.exitButton, settingsButton, this.sceneLabel); document.body.append(this.shipControls);
      this.noticeRegion = document.createElement('section'); this.noticeRegion.id = 'ship-notifications'; this.noticeRegion.setAttribute('aria-label', 'Execution notifications');
      this.noticeRegion.setAttribute('aria-live', 'polite'); this.noticeRegion.setAttribute('aria-relevant', 'additions'); this.noticeRegion.hidden = true; document.body.append(this.noticeRegion);
      document.addEventListener('keydown', event => { if (event.key === 'Escape' && this.immersive && !this.dialog.open) { event.preventDefault(); this.toggleShipView(false); } });
    }
    observeNotices() {
      if (!this.available || !this.snapshot) return;
      const facts = snapshotNotices(this.snapshot);
      const activeKeys = new Set(facts.map(notice => notice.key));
      this.notices = this.notices.filter(notice => !['approval', 'anomaly'].includes(notice.kind) || activeKeys.has(notice.key));
      for (const notice of facts) {
        if (this.seenNotices.has(notice.key)) continue;
        this.seenNotices.add(notice.key);
        // Initial history is not a new event; actionable facts still need attention.
        if (this.noticesPrimed || notice.critical) this.notices.push(notice);
      }
      if (this.notices.length > 200) { this.earlierNotices = (this.earlierNotices || 0) + this.notices.length - 200; this.notices = this.notices.slice(-200); }
      // A fact absent from a later snapshot may legitimately recur; retain only
      // the current authoritative keys, not an ever-growing lifetime set.
      this.seenNotices = new Set(facts.map(notice => notice.key));
      this.noticesPrimed = true;
      this.renderNotices();
    }
    renderNotices() {
      if (!this.noticeRegion) return;
      const visible = this.notices.filter(notice => visibleNotice(notice, this.settings.notificationDensity));
      this.noticeRegion.hidden = visible.length === 0;
      const signature = JSON.stringify([this.earlierNotices || 0, visible.map(notice => [notice.key, this.snapshot?.projects?.find(row => row.id === notice.projectId)?.name])]);
      if (signature === this.noticeSignature) { if (this.earlierNotices) this.noticeRegion.hidden = false; return; }
      this.noticeSignature = signature;
      // Keep undismissed facts; render only a bounded list with an explicit count.
      this.noticeRegion.replaceChildren();
      if (this.earlierNotices) { const earlier = document.createElement('p'); earlier.textContent = `${this.earlierNotices} older notices left this bounded view. Inspect execution history for their evidence.`; this.noticeRegion.append(earlier); this.noticeRegion.hidden = false; }
      if (visible.length > 12) { const summary = document.createElement('p'); summary.textContent = `${visible.length - 12} earlier notices remain. Dismiss recent notices to inspect them.`; this.noticeRegion.append(summary); }
      for (const notice of visible.slice(-12)) {
        const line = document.createElement('div'); line.className = notice.critical ? 'ship-notice is-critical' : 'ship-notice'; line.dataset.kind = notice.kind;
        const inspect = document.createElement('button'); inspect.type = 'button';
        const name = this.snapshot?.projects?.find(row => row.id === notice.projectId)?.name || notice.projectId;
        inspect.textContent = `${name}: ${notice.label}`;
        inspect.addEventListener('click', () => {
          if (this.immersive) this.toggleShipView(false);
          const select = document.getElementById('project-select') || document.getElementById('orch-project');
          if (select && select.value !== notice.projectId && [...select.options].some(option => option.value === notice.projectId)) { select.value = notice.projectId; select.dispatchEvent(new Event('change', { bubbles: true })); }
          const workspace = document.getElementById('orchestrator-view');
          if (typeof window.PlanwharfShowView === 'function') window.PlanwharfShowView('orchestrator');
          else if (workspace?.hidden) document.getElementById('watch-window')?.click();
          if (typeof window.PlanwharfSelectWorkspacePanel === 'function') window.PlanwharfSelectWorkspacePanel(notice.kind === 'anomaly' ? 'supervisor' : ['worker-failed', 'review-result'].includes(notice.kind) ? 'sessions' : 'overview');
          else if (notice.kind === 'anomaly') document.getElementById('supervisor-navigation')?.click();
        });
        const dismiss = document.createElement('button'); dismiss.type = 'button'; dismiss.textContent = 'Dismiss'; dismiss.setAttribute('aria-label', `Dismiss ${notice.label}`);
        dismiss.addEventListener('click', () => { this.notices = this.notices.filter(item => item !== notice); this.renderNotices(); });
        line.append(inspect, dismiss); this.noticeRegion.append(line);
      }
    }
    preview() {
      this.dialog.querySelector('[data-flag-preview]').innerHTML = flagSvg(this.effectiveFlag(this.form.elements.flag.value));
    }
    createSettings() {
      this.dialog = document.createElement('dialog'); this.dialog.id = 'harbor-dialog'; this.dialog.className = 'harbor-dialog';
      this.dialog.setAttribute('aria-labelledby', 'harbor-settings-title');
      this.dialog.innerHTML = `<form id="harbor-settings-form"><h2 id="harbor-settings-title">Ship settings</h2>
        <p class="muted">Where projects dock, agents work, and progress stays anchored.</p>
        <label class="choice"><input type="checkbox" name="enabled"> Sailing Ship background</label>
        <label>Animations<select name="animations"><option value="full">Full</option><option value="reduced">Reduced</option><option value="off">Off</option></select></label>
        <label>Flag<select name="flag"><option value="random">Random (stable for this session)</option>${Object.entries(FLAGS).map(([id, flag]) => `<option value="${id}">${flag.name}</option>`).join('')}</select></label>
        <div class="harbor-flag-preview" data-flag-preview aria-hidden="true"></div>
        <label class="choice"><input type="checkbox" name="effects"> Ship status effects</label>
        <label>Weather mode<select name="weatherMode"><option value="adaptive">Adaptive</option><option value="random">Random</option><option value="manual">Manual</option></select></label>
        <label>Manual sea<select name="seaCondition">${CONDITIONS.map(value => `<option value="${value}">${value}</option>`).join('')}</select></label>
        <p class="muted">Weather is decorative, independent of execution health or progress. Automatic changes occur gradually, at most once a minute.</p>
        <label>Execution notifications<select name="notificationDensity"><option value="all">All relevant events</option><option value="important">Important events</option><option value="action">Action required only</option></select></label>
        <p class="muted" data-harbor-status></p><p class="notice" data-harbor-error hidden></p>
        <div class="dialog-actions"><button type="button" data-harbor-cancel class="secondary">Cancel</button><button type="submit" class="primary">Save settings</button></div>
      </form>`;
      document.body.append(this.dialog); this.form = this.dialog.querySelector('form');
      this.form.elements.flag.addEventListener('change', () => this.preview());
      this.dialog.querySelector('[data-harbor-cancel]').addEventListener('click', () => this.dialog.close());
      this.form.addEventListener('submit', event => {
        event.preventDefault();
        const next = shipSettingsFrom({ enabled: this.form.elements.enabled.checked, animations: this.form.elements.animations.value,
          flag: this.form.elements.flag.value, effects: this.form.elements.effects.checked, weatherMode: this.form.elements.weatherMode.value,
          seaCondition: this.form.elements.seaCondition.value, notificationDensity: this.form.elements.notificationDensity.value });
        try { localStorage.setItem(SETTINGS_KEY, JSON.stringify(next)); }
        catch { const error = this.dialog.querySelector('[data-harbor-error]'); error.hidden = false; error.textContent = 'Browser storage is unavailable. Settings could not be saved.'; return; }
        this.settings = next; this.applySettings(); this.dialog.close();
      });
      const button = document.createElement('button'); button.type = 'button'; button.id = 'harbor-settings-button';
      button.className = 'secondary compact'; button.textContent = '⚓'; button.title = 'Ship settings'; button.setAttribute('aria-label', 'Ship settings');
      button.addEventListener('click', () => {
        for (const key of ['enabled', 'effects']) this.form.elements[key].checked = this.settings[key];
        for (const key of ['animations', 'flag', 'weatherMode', 'seaCondition', 'notificationDensity']) this.form.elements[key].value = this.settings[key];
        this.dialog.querySelector('[data-harbor-error]').hidden = true; this.preview(); this.dialog.showModal();
      });
      document.getElementById('theme-toggle')?.before(button);
    }
  }
  const exports = { FLAGS, SETTINGS_KEY, SESSION_KEY, nameLabel, settingsFrom, flagSvg, resolveState, shipSettingsFrom, weatherCondition, snapshotNotices, visibleNotice, CONDITIONS, WEATHER_INTERVAL, HarborUI };
  if (typeof module !== 'undefined' && module.exports) module.exports = exports;
  if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    const boot = () => { const ui = new HarborUI(); ui.init(); };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot, { once: true }); else boot();
  }
})();
