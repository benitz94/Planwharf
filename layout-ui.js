'use strict';
(() => {
  const key = 'planwharf_layout_v1';
  const defaults = { railWidth: 240, chatRatio: 0.6 };
  const settings = value => ({
    railWidth: Number.isFinite(value?.railWidth) ? Math.max(200, Math.min(480, value.railWidth)) : defaults.railWidth,
    chatRatio: Number.isFinite(value?.chatRatio) ? Math.max(0, Math.min(1, value.chatRatio)) : defaults.chatRatio
  });
  let preferences;
  try { preferences = settings(JSON.parse(localStorage.getItem(key))); }
  catch { preferences = { ...defaults }; }
  const splits = [];
  let drag = null;
  const announce = text => { const toast = document.getElementById('toast'); if (toast) toast.textContent = text; };
  function save() {
    try { localStorage.setItem(key, JSON.stringify(preferences)); }
    catch { announce('Layout changed, but this browser could not save it.'); }
  }
  function limits(split) {
    const width = split.parent.getBoundingClientRect().width;
    const rail = split.name === 'rail';
    const stacked = rail ? window.innerWidth <= 960 : window.innerWidth <= 900 || width < 650;
    const min = rail ? 200 : 280;
    const max = rail ? Math.min(480, width - 370) : width - 330;
    return { width, min, max: Math.max(min, max), stacked };
  }
  function refresh(split) {
    const { width, min, max, stacked } = limits(split);
    split.parent.dataset.layoutStacked = String(stacked);
    split.handle.tabIndex = stacked ? -1 : 0;
    const desired = split.name === 'rail' ? preferences.railWidth : preferences.chatRatio * (width - 10);
    split.size = Math.max(min, Math.min(max, desired));
    split.parent.style.setProperty('--layout-first-size', `${split.size}px`);
    split.handle.setAttribute('aria-valuemin', String(Math.round(min)));
    split.handle.setAttribute('aria-valuemax', String(Math.round(max)));
    split.handle.setAttribute('aria-valuenow', String(Math.round(split.size)));
    split.handle.setAttribute('aria-valuetext', `${Math.round(split.size)} pixels`);
    if (drag?.split === split && stacked) finish(false);
  }
  function setSize(split, size) {
    const { width, min, max } = limits(split);
    const bounded = Math.max(min, Math.min(max, size));
    if (split.name === 'rail') preferences.railWidth = bounded;
    else preferences.chatRatio = bounded / Math.max(1, width - 10);
    refresh(split);
  }
  function finish(commit) {
    if (!drag) return;
    const current = drag; drag = null;
    if (!commit) preferences = current.previous;
    if (current.split.handle.hasPointerCapture?.(current.pointerId)) current.split.handle.releasePointerCapture(current.pointerId);
    delete document.body.dataset.layoutResizing;
    splits.forEach(refresh);
    if (commit) save();
  }
  function install(parent, first, second, name, label) {
    if (!parent || !first || !second) return;
    const handle = document.createElement('div');
    handle.className = 'layout-splitter';
    handle.setAttribute('role', 'separator');
    handle.setAttribute('aria-orientation', 'vertical');
    handle.setAttribute('aria-label', label);
    handle.title = `${label}. Drag or use Left/Right arrows. Double-click to reset.`;
    if (!first.id) first.id = `layout-${name}-first`;
    if (!second.id) second.id = `layout-${name}-second`;
    handle.setAttribute('aria-controls', `${first.id} ${second.id}`);
    first.after(handle);
    parent.dataset.layoutSplit = name;
    const split = { parent, handle, name, size: 0 }; splits.push(split);
    handle.addEventListener('pointerdown', event => {
      if (event.button !== 0 || limits(split).stacked) return;
      event.preventDefault();
      finish(false);
      drag = { split, pointerId: event.pointerId, x: event.clientX, size: split.size, previous: { ...preferences } };
      handle.focus(); handle.setPointerCapture(event.pointerId);
      document.body.dataset.layoutResizing = 'true';
    });
    handle.addEventListener('lostpointercapture', () => finish(false));
    handle.addEventListener('keydown', event => {
      if (limits(split).stacked || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const { min, max } = limits(split);
      setSize(split, event.key === 'Home' ? min : event.key === 'End' ? max : split.size + (event.key === 'ArrowRight' ? 1 : -1) * (event.shiftKey ? 50 : 20));
      save();
    });
    handle.addEventListener('dblclick', () => {
      finish(false);
      if (name === 'rail') preferences.railWidth = defaults.railWidth;
      else preferences.chatRatio = defaults.chatRatio;
      refresh(split); save(); announce('Panel size reset.');
    });
    refresh(split);
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(() => refresh(split)).observe(parent);
  }
  const app = document.querySelector('.app');
  install(app, app?.querySelector('.rail'), app?.querySelector('.frame'), 'rail', 'Resize projects sidebar');
  for (const shell of document.querySelectorAll('.orch-shell')) {
    install(shell, shell.querySelector('.orch-chat'), shell.querySelector('.orch-board'), 'chat', 'Resize orchestrator chat and dashboard');
  }
  window.addEventListener('pointermove', event => {
    if (drag && event.pointerId === drag.pointerId) setSize(drag.split, drag.size + event.clientX - drag.x);
  });
  window.addEventListener('pointerup', event => { if (drag && event.pointerId === drag.pointerId) finish(true); });
  window.addEventListener('pointercancel', event => { if (drag && event.pointerId === drag.pointerId) finish(false); });
  window.addEventListener('blur', () => finish(false));
  window.addEventListener('keydown', event => { if (event.key === 'Escape' && drag) { event.preventDefault(); finish(false); } });
  window.addEventListener('resize', () => splits.forEach(refresh));
  window.addEventListener('planwharf-view', () => splits.forEach(refresh));
  window.addEventListener('storage', event => {
    if (event.key !== key) return;
    finish(false);
    try { preferences = settings(JSON.parse(event.newValue)); } catch { preferences = { ...defaults }; }
    splits.forEach(refresh);
  });
  for (const button of document.querySelectorAll('[data-layout-reset]')) button.addEventListener('click', () => {
    finish(false); preferences = { ...defaults };
    try { localStorage.removeItem(key); } catch { /* Reset still applies for this window. */ }
    splits.forEach(refresh); announce('Layout reset.');
  });
})();


// Height controls survive dashboard refreshes and keep content accessible by scrolling.
(() => {
  const key = 'planwharf_layout_heights_v1';
  let heights = {};
  function read(value) {
    try {
      const data = JSON.parse(value);
      return Object.fromEntries(Object.entries(data || {}).filter(([name, height]) => name.length < 150 && Number.isFinite(height) && height >= 80 && height <= 1600));
    } catch { return {}; }
  }
  try { heights = read(localStorage.getItem(key)); } catch { /* Storage may be disabled. */ }
  const controls = new Set();
  let active = null;
  const notify = text => { const toast = document.getElementById('toast'); if (toast) toast.textContent = text; };
  function save() {
    try { localStorage.setItem(key, JSON.stringify(heights)); }
    catch { notify('Panel height changed, but this browser could not save it.'); }
  }
  function bounds(control) {
    const shell = control.panel.closest('.orch-shell');
    const enabled = innerWidth > 900 && shell?.dataset.layoutStacked !== 'true';
    const min = control.chat ? 80 : 100;
    const max = control.chat
      ? Math.max(min, control.panel.clientHeight - [...control.panel.children].filter(child => child !== control.content && child !== control.handle).reduce((sum, child) => sum + child.getBoundingClientRect().height, 0) - 10)
      : Math.min(1600, Math.max(400, innerHeight * 1.5));
    return { min, max, enabled };
  }
  function render(control) {
    const { min, max, enabled } = bounds(control);
    const size = heights[control.name];
    const applied = Math.max(min, Math.min(max, size || control.content.getBoundingClientRect().height));
    control.handle.hidden = !enabled;
    control.handle.tabIndex = enabled ? 0 : -1;
    if (control.chat) {
      control.content.style.flex = enabled && size ? `0 0 ${applied}px` : '';
      control.content.style.minHeight = enabled ? '80px' : '';
    } else control.content.style.height = enabled && size ? `${applied}px` : '';
    control.handle.setAttribute('aria-valuemin', String(Math.round(min)));
    control.handle.setAttribute('aria-valuemax', String(Math.round(max)));
    control.handle.setAttribute('aria-valuenow', String(Math.round(applied)));
    control.handle.setAttribute('aria-valuetext', `${Math.round(applied)} pixels`);
  }
  function refresh() {
    if (active && !active.control.handle.isConnected) finish(false);
    for (const control of controls) {
      if (!control.handle.isConnected) { controls.delete(control); continue; }
      render(control);
    }
  }
  function size(control, height) {
    const { min, max } = bounds(control);
    heights[control.name] = Math.max(min, Math.min(max, height));
    render(control);
  }
  function finish(commit) {
    if (!active) return;
    const previous = active; active = null;
    if (!commit) heights = previous.previous;
    if (previous.control.handle.hasPointerCapture?.(previous.pointerId)) previous.control.handle.releasePointerCapture(previous.pointerId);
    delete document.body.dataset.layoutHeightResizing;
    refresh(); if (commit) save();
  }
  function install(panel, content, name, chat = false) {
    const handle = document.createElement('div');
    handle.className = 'layout-height-splitter';
    handle.setAttribute('role', 'separator');
    handle.setAttribute('aria-orientation', 'horizontal');
    handle.setAttribute('aria-label', chat ? 'Resize conversation height' : `Resize ${name} section height`);
    handle.title = 'Drag or use Up/Down arrows to resize height. Double-click to reset.';
    if (!content.id) content.id = `layout-height-${controls.size}-${name.replace(/[^a-z0-9]/gi, '-')}`;
    handle.setAttribute('aria-controls', content.id);
    content.after(handle);
    const control = { panel, content, handle, name, chat }; controls.add(control);
    handle.addEventListener('pointerdown', event => {
      if (event.button !== 0 || !bounds(control).enabled) return;
      event.preventDefault(); finish(false);
      active = { control, pointerId: event.pointerId, y: event.clientY, height: content.getBoundingClientRect().height, previous: { ...heights } };
      handle.focus(); handle.setPointerCapture(event.pointerId);
      document.body.dataset.layoutHeightResizing = 'true';
    });
    handle.addEventListener('lostpointercapture', () => finish(false));
    handle.addEventListener('keydown', event => {
      if (!bounds(control).enabled || !['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault(); const { min, max } = bounds(control);
      size(control, event.key === 'Home' ? min : event.key === 'End' ? max : content.getBoundingClientRect().height + (event.key === 'ArrowDown' ? 1 : -1) * (event.shiftKey ? 50 : 20));
      save();
    });
    handle.addEventListener('dblclick', () => { finish(false); delete heights[name]; render(control); save(); notify('Panel height reset.'); });
    render(control);
  }
  for (const chat of document.querySelectorAll('.orch-chat')) {
    const thread = chat.querySelector('.orch-chat-thread');
    if (thread) install(chat, thread, 'conversation', true);
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(refresh).observe(chat);
  }
  function sections(board) {
    for (const section of board.querySelectorAll('.dash-section')) {
      if (section.querySelector(':scope > .layout-section-content')) continue;
      const name = section.getAttribute('aria-label') || section.querySelector('h3')?.textContent?.trim();
      if (!name) continue;
      const content = document.createElement('div'); content.className = 'layout-section-content';
      while (section.firstChild) content.append(section.firstChild);
      section.append(content); section.classList.add('layout-height-section');
      install(section, content, name);
    }
    refresh();
  }
  for (const board of document.querySelectorAll('.orch-board')) {
    sections(board);
    const monitor = board.querySelector('.monitor');
    if (monitor) new MutationObserver(() => sections(board)).observe(monitor, { childList: true });
  }
  window.addEventListener('pointermove', event => { if (active && event.pointerId === active.pointerId) size(active.control, active.height + event.clientY - active.y); });
  window.addEventListener('pointerup', event => { if (active && event.pointerId === active.pointerId) finish(true); });
  window.addEventListener('pointercancel', event => { if (active && event.pointerId === active.pointerId) finish(false); });
  window.addEventListener('keydown', event => { if (active && event.key === 'Escape') { event.preventDefault(); finish(false); } });
  window.addEventListener('blur', () => finish(false));
  window.addEventListener('resize', refresh);
  window.addEventListener('planwharf-view', refresh);
  window.addEventListener('storage', event => { if (event.key === key) { finish(false); heights = read(event.newValue); refresh(); } });
  for (const button of document.querySelectorAll('[data-layout-reset]')) button.addEventListener('click', () => {
    finish(false); heights = {};
    try { localStorage.removeItem(key); } catch { /* Reset remains available without storage. */ }
    refresh();
  });
})();
