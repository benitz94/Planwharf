'use strict';
const { spawn } = require('node:child_process');
const { randomUUID } = require('node:crypto');
function focusNative(view, child, title) {
  if (process.platform !== 'win32') return Promise.resolve(false);
  return new Promise(resolve => {
    const helper = spawn('powershell.exe', ['-NoProfile', '-NonInteractive', '-WindowStyle', 'Hidden', '-Command', `$shell = New-Object -ComObject WScript.Shell; if ($shell.AppActivate('${title}')) { exit 0 } else { exit 1 }`], { windowsHide: true, stdio: 'ignore' });
    const timer = setTimeout(() => { helper.kill(); resolve(false); }, 3000);
    const done = success => { clearTimeout(timer); resolve(success); };
    helper.on('error', () => done(false));
    helper.on('exit', code => done(code === 0));
  });
}
function createAppWindows({ open, focus = focusNative, now = Date.now, ttl = 15000, separateWindows = false, onWindowClosed = () => {}, closeGraceMs = 5000 } = {}) {
  const clients = new Map(), children = new Map(), ownedChildren = new Set(), requested = new Map(), launching = new Map();
  // A unique native title keeps focus within this notebook's application.
  const instance = randomUUID().slice(0, 8);
  const titleFor = view => `${view === 'planwharf' ? 'Planwharf' : 'Orchestrator'} · ${instance}`;
  let origin;
  let closed = false;
  let closeTimer;
  let requestedView = 'planwharf';
  function status() {
    const views = { planwharf: false, orchestrator: false };
    for (const [id, client] of clients) {
      if (now() - client.at > ttl) clients.delete(id);
      else if (separateWindows) views[client.view] = true;
      else { views.planwharf = true; views.orchestrator = true; }
    }
    return views;
  }
  function openView(view, url) {
    if (closed) return false;
    if (!['planwharf', 'orchestrator'].includes(view)) throw new Error('Invalid view');
    origin = new URL(url).origin;
    requestedView = view;
    const target = separateWindows ? view : 'planwharf';
    requested.set(target, (requested.get(target) || 0) + 1);
    if (status()[target]) {
      // A recent heartbeat can outlive its window. Reopen if native focus fails.
      Promise.resolve(focus(target, children.get(target), titleFor(target))).then(success => {
        if (success === false) launch(target);
      }).catch(error => { console.error('Window focus failed:', error.message); launch(target); });
      return true;
    }
    return launch(target);
  }
  function launch(view = 'planwharf') {
    if (closed) return false;
    const url = origin + '/';
    if (now() - (launching.get(view) || -Infinity) < ttl) return true;
    const child = open(url, view);
    if (!child) return false;
    launching.set(view, now());
    children.set(view, child);
    ownedChildren.add(child);
    const finished = code => {
      ownedChildren.delete(child);
      if (children.get(view) !== child) return;
      children.delete(view);
      launching.delete(view);
      for (const [id, client] of clients) if (client.view === view) clients.delete(id);
      if (code === 0 && !closed && !child.planwharfClosing && !child.planwharfExternalBrowser) onWindowClosed();
    };
    child.once('exit', finished);
    child.once('error', finished);
    // Only a normal exit from the current owned window requests application Quit.
    return true;
  }
  function heartbeat({ id, view, closed, focusSeen = 0 }) {
    view = separateWindows && view === 'orchestrator' ? 'orchestrator' : 'planwharf';
    if (closed) {
      const registered = clients.delete(id);
      launching.delete(view);
      // External browser tabs have no owned process. Allow reloads to reconnect before Quit.
      if (registered && clients.size === 0) {
        clearTimeout(closeTimer);
        closeTimer = setTimeout(() => { if (!clients.size && !ownedChildren.size) onWindowClosed(); }, closeGraceMs);
        closeTimer.unref();
      }
    } else { clearTimeout(closeTimer); clients.set(id, { view, at: now() }); launching.delete(view); }
    const focusVersion = requested.get(view) || 0;
    return { views: status(), focus: focusVersion > focusSeen, focusVersion, requestedView: separateWindows ? view : requestedView, title: titleFor(view) };
  }
  function openApp(url) { openView('planwharf', url + '/'); if (separateWindows) openView('orchestrator', url + '/'); }
  function closeAll() {
    closed = true;
    clearTimeout(closeTimer);
    for (const child of ownedChildren) {
      child.planwharfClosing = true;
      try { child.kill(); } catch {}
    }
  }
  return { openView, openApp, openBoth: openApp, heartbeat, status, closeAll, get origin() { return origin; } };
}
module.exports = { createAppWindows };
