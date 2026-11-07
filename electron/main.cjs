'use strict';
// Planwharf desktop: an app window around the same local Planwharf server.
// The server runs as a child process (Electron in Node mode), exactly as the portable version runs it,
// with its data in the user's app-data folder. The window only shows http://127.0.0.1:<port>/.
const { app, BrowserWindow, shell, dialog, Menu } = require('electron');
const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');

const APP_DIR = path.join(__dirname, 'app');
let child = null, win = null, url = null, quitting = false;

if (!app.requestSingleInstanceLock()) app.quit();
app.on('second-instance', () => { if (win) { if (win.isMinimized()) win.restore(); win.focus(); } });

const STARTER = path.join(APP_DIR, 'fixtures', 'starter-notebook.json');
const projectNames = file => { try { return (JSON.parse(fs.readFileSync(file, 'utf8')).projects || []).map(p => p && p.project && p.project.name); } catch { return null; } };

// Someone who used the ZIP (portable) version can bring its projects, settings and history.
// The files are copied, never moved: the ZIP version keeps its own copy.
async function offerImport(data, plan) {
  const marker = path.join(data, 'desktop-import.json');
  if (fs.existsSync(marker)) return;
  const mine = fs.existsSync(plan) ? projectNames(plan) : [];
  const starter = projectNames(STARTER) || [];
  const fresh = mine !== null && mine.every(name => starter.includes(name));
  if (!fresh) { fs.writeFileSync(marker, JSON.stringify({ decided: 'kept own projects', at: new Date().toISOString() })); return; }
  for (;;) {
    const { response } = await dialog.showMessageBox({
      type: 'question', title: 'Planwharf', buttons: ['Choose the folder…', 'Start without them'], defaultId: 0, cancelId: 1, noLink: true,
      message: 'Do you already use Planwharf from the ZIP download?',
      detail: 'Choose the folder where Planwharf.exe or planwharf.sh is, and the app copies your projects, team and history from there.\n\nClose the other Planwharf first. The ZIP version keeps its own copy: from now on use one version at a time.'
    });
    if (response !== 0) { fs.writeFileSync(marker, JSON.stringify({ decided: 'started without import', at: new Date().toISOString() })); return; }
    const pick = await dialog.showOpenDialog({ title: 'Folder of your Planwharf ZIP version', properties: ['openDirectory'] });
    if (pick.canceled || !pick.filePaths[0]) continue;
    const from = pick.filePaths[0];
    const names = projectNames(path.join(from, 'plan.json'));
    if (!names) { await dialog.showMessageBox({ type: 'warning', title: 'Planwharf', message: 'No Planwharf projects in that folder.', detail: `${from} has no readable plan.json. Choose the folder that contains Planwharf.exe or planwharf.sh.` }); continue; }
    // Keep whatever the desktop app had, then copy the notebook and its history folder.
    const backup = path.join(data, 'before-import-' + new Date().toISOString().replace(/[:.]/g, '-'));
    fs.mkdirSync(backup, { recursive: true });
    for (const name of ['plan.json', '.attempts-plan']) if (fs.existsSync(path.join(data, name))) fs.renameSync(path.join(data, name), path.join(backup, name));
    fs.copyFileSync(path.join(from, 'plan.json'), plan);
    if (fs.existsSync(path.join(from, '.attempts-plan'))) fs.cpSync(path.join(from, '.attempts-plan'), path.join(data, '.attempts-plan'), { recursive: true });
    fs.writeFileSync(marker, JSON.stringify({ decided: 'imported', from, projects: names, backup, at: new Date().toISOString() }, null, 2));
    return;
  }
}

async function startServer() {
  const data = path.join(app.getPath('userData'), 'data');
  fs.mkdirSync(data, { recursive: true });
  const plan = path.join(data, 'plan.json');
  await offerImport(data, plan);
  // A first start gets the same starter notebook as the portable version.
  if (!fs.existsSync(plan)) fs.copyFileSync(STARTER, plan, fs.constants.COPYFILE_EXCL);
  const env = {
    ...process.env,
    ELECTRON_RUN_AS_NODE: '1', // the child and every helper it starts run as plain Node
    PLANWHARF_NO_WINDOW: '1',
    PLANWHARF_DESKTOP: '1',
    PLANWHARF_DESKTOP_VERSION: 'v' + app.getVersion(),
    PLANWHARF_PLAN: plan
  };
  return new Promise((resolve, reject) => {
    child = spawn(process.execPath, [path.join(APP_DIR, 'planwharf.cjs')], { cwd: APP_DIR, env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    let out = '', settled = false;
    const log = fs.createWriteStream(path.join(data, 'desktop.log'), { flags: 'a' });
    const take = chunk => {
      log.write(chunk); out += chunk;
      const m = /^(http:\/\/127\.0\.0\.1:\d+)\s*$/m.exec(out);
      if (m && !settled) { settled = true; resolve(m[1]); }
    };
    child.stdout.on('data', take); child.stderr.on('data', chunk => log.write(chunk));
    child.on('error', error => { if (!settled) { settled = true; reject(error); } });
    child.on('exit', code => {
      child = null;
      if (!settled) { settled = true; reject(new Error(`Planwharf stopped while starting (code ${code}). See ${path.join(data, 'desktop.log')}.`)); return; }
      // Quit from inside Planwharf (Settings > Quit) or a crash: close the window too.
      if (!quitting) { quitting = true; app.quit(); }
    });
    setTimeout(() => { if (!settled) { settled = true; reject(new Error('Planwharf did not start within 60 seconds.')); } }, 60000);
  });
}

function createWindow() {
  win = new BrowserWindow({
    width: 1280, height: 860, minWidth: 380, minHeight: 500, title: 'Planwharf', backgroundColor: '#101312', show: false,
    webPreferences: { contextIsolation: true, nodeIntegration: false, sandbox: true, spellcheck: true }
  });
  win.once('ready-to-show', () => win.show());
  // Only the local Planwharf page opens inside the app; every other link opens in the normal browser.
  const local = target => { try { return new URL(target).origin === new URL(url).origin; } catch { return false; } };
  win.webContents.setWindowOpenHandler(({ url: target }) => { if (/^https?:/.test(target) && !local(target)) shell.openExternal(target); return { action: 'deny' }; });
  win.webContents.on('will-navigate', (event, target) => { if (!local(target)) { event.preventDefault(); if (/^https?:/.test(target)) shell.openExternal(target); } });
  win.webContents.session.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  win.loadURL(url + '/');
  win.on('closed', () => { win = null; });
}

// Closing the window saves checkpoints and stops the server, like the portable version.
async function stopServer() {
  if (!child) return;
  const running = child;
  const exited = new Promise(resolve => running.once('exit', resolve));
  try { await fetch(url + '/api/app/quit', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}', signal: AbortSignal.timeout(3000) }); } catch {}
  const timer = setTimeout(() => { try { running.kill(); } catch {} }, 8000);
  await exited; clearTimeout(timer);
}

app.on('before-quit', event => {
  if (child && !app.isStoppingServer) {
    event.preventDefault(); app.isStoppingServer = true; quitting = true;
    stopServer().finally(() => app.quit());
  }
});
app.on('window-all-closed', () => app.quit());

module.exports = { offerImport };

app.whenReady().then(async () => {
  Menu.setApplicationMenu(process.platform === 'darwin' ? Menu.buildFromTemplate([{ role: 'appMenu' }, { role: 'editMenu' }, { role: 'windowMenu' }]) : null);
  try { url = await startServer(); }
  catch (error) { dialog.showErrorBox('Planwharf could not start', error.message); quitting = true; app.quit(); return; }
  createWindow();
});
