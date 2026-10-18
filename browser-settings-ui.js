'use strict';
(() => {
  const toolbar = document.querySelector('.top-end, .orch-top-actions');
  if (!toolbar) return;
  const open = document.createElement('button');
  open.type = 'button';
  open.className = 'secondary compact';
  open.textContent = 'Launch browser';
  toolbar.insertBefore(open, toolbar.querySelector('#app-quit') || toolbar.firstChild);
  const dialog = document.createElement('dialog');
  dialog.className = 'app-update-dialog';
  dialog.setAttribute('aria-labelledby', 'launch-browser-title');
  dialog.innerHTML = '<form id="launch-browser-form"><h2 id="launch-browser-title">Launch browser</h2><label>Browser<select name="browser"><option value="auto">Automatic detection</option><option value="default">System default browser</option><option value="custom">Custom executable</option></select></label><label>Custom executable path<input name="path" placeholder="Absolute path to the browser executable"></label><label>Custom browser family<select name="family"><option value="chromium">Chromium (Brave, Opera, Chrome, Edge)</option><option value="firefox">Firefox</option></select></label><p class="muted">Used the next time you launch Planwharf.exe or the platform launcher. Quit and relaunch to apply.</p><p role="status" data-browser-status></p><div class="dialog-actions"><button class="primary" type="submit">Save browser</button><button class="secondary" type="button" data-close>Close</button></div></form>';
  document.body.append(dialog);
  const form = dialog.querySelector('#launch-browser-form');
  const status = form.querySelector('[data-browser-status]');
  const choice = form.elements.browser;
  const customFields = () => {
    for (const name of ['path', 'family']) form.elements[name].closest('label').hidden = choice.value !== 'custom';
  };
  choice.addEventListener('change', customFields);
  customFields();
  dialog.querySelector('[data-close]').addEventListener('click', () => dialog.close());
  open.addEventListener('click', () => dialog.showModal());
  fetch('/api/settings/browser').then(async response => {
    if (!response.ok) throw new Error('Could not load browser preferences.');
    const data = await response.json();
    for (const browser of data.browsers) {
      const option = document.createElement('option');
      option.value = browser.id;
      option.textContent = browser.label + (browser.path ? '' : ' (not detected)');
      option.disabled = !browser.path;
      choice.append(option);
    }
    choice.value = data.selected.id;
    form.elements.path.value = data.selected.path || '';
    form.elements.family.value = data.selected.family || 'chromium';
    customFields();
  }).catch(error => { status.textContent = error.message; });
  form.addEventListener('submit', async event => {
    event.preventDefault();
    const selected = { id: choice.value };
    if (selected.id === 'custom') Object.assign(selected, { path: form.elements.path.value.trim(), family: form.elements.family.value });
    try {
      const response = await fetch('/api/settings/browser', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(selected) });
      const result = await response.json();
      if (!response.ok) throw new Error(result.error || 'Could not save browser.');
      status.textContent = 'Browser saved. Quit and relaunch Planwharf to apply.';
    } catch (error) { status.textContent = error.message; }
  });
})();
