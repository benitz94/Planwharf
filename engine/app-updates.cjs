'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const REPO = 'benitz94/Planwharf';
const MAX_DOWNLOAD = 32 * 1024 * 1024;
const MAX_EXPANDED = 96 * 1024 * 1024;
const digest = data => crypto.createHash('sha256').update(data).digest('hex');
const versionParts = value => /^v?\d+\.\d+\.\d+$/.test(value || '') ? value.replace(/^v/, '').split('.').map(Number) : null;
function newer(next, current) {
  const a = versionParts(next), b = versionParts(current);
  if (!a || !b) return false;
  for (let i = 0; i < 3; i++) { if (a[i] !== b[i]) return a[i] > b[i]; }
  return false;
}
function appFile(name) {
  return typeof name === 'string' && !name.includes('..') && !name.includes('\\') && !/[:\x00-\x1f]/.test(name)
    && (/^[a-zA-Z0-9_-]+\.(?:js|cjs|html|css|cmd|sh)$/.test(name) || ['Planwharf.exe', 'models.json', 'LICENSE', 'COPYRIGHT', 'README.md', 'fixtures/starter-notebook.json'].includes(name)
      || /^assets\/harbor\/[a-z-]+\.png$/.test(name) || ['assets/fonts/cinzel-variable.ttf', 'assets/fonts/cinzel-OFL.txt', 'assets/fonts/im-fell-english-sc.ttf', 'assets/fonts/im-fell-english-sc-OFL.txt'].includes(name)
      || /^engine\/[a-zA-Z0-9_-]+\.(?:js|cjs)$/.test(name) || /^docs\/[a-zA-Z0-9_-]+\.md$/.test(name));
}
function checkout(root) {
  for (let current = path.resolve(root); ; current = path.dirname(current)) {
    if (fs.existsSync(path.join(current, '.git'))) return true;
    if (path.dirname(current) === current) return false;
  }
}
function installation(root) {
  try {
    const value = JSON.parse(fs.readFileSync(path.join(root, '.planwharf-install.json'), 'utf8'));
    if (value.schema !== 1 || !versionParts(value.version) || !value.files || Array.isArray(value.files) || !Object.keys(value.files).length || Object.entries(value.files).some(([name, hash]) => !appFile(name) || !/^[a-f0-9]{64}$/.test(hash))) throw new Error('Invalid installation manifest.');
    return value;
  } catch (error) { if (error.code === 'ENOENT') return null; throw error; }
}
function inside(root, name) {
  if (!appFile(name) && name !== '.planwharf-install.json') throw new Error('Update contains a protected or invalid file: ' + name);
  const target = path.resolve(root, name);
  if (!target.startsWith(path.resolve(root) + path.sep)) throw new Error('Update path escapes the installation.');
  let current = root;
  for (const part of name.split('/')) {
    current = path.join(current, part);
    try { if (fs.lstatSync(current).isSymbolicLink()) throw new Error('Update cannot replace symbolic links: ' + name); }
    catch (error) { if (error.code !== 'ENOENT') throw error; }
  }
  return target;
}
function verifyLocal(root, current, nextNames) {
  if (checkout(root)) throw new Error('Automatic updates are unavailable in a Git checkout.');
  for (const [name, hash] of Object.entries(current.files)) {
    const file = inside(root, name);
    if (!fs.existsSync(file) || digest(fs.readFileSync(file)) !== hash) throw new Error('Application files have local changes: ' + name + '. Download the release separately to preserve them.');
  }
  for (const name of nextNames) if (!Object.hasOwn(current.files, name) && fs.existsSync(inside(root, name))) throw new Error('Update would overwrite an existing local file: ' + name);
}
function unpack(data, expectedVersion) {
  const bundle = JSON.parse(zlib.gunzipSync(data, { maxOutputLength: MAX_EXPANDED }));
  if (bundle.schema !== 1 || bundle.version !== expectedVersion || !versionParts(bundle.version) || !Array.isArray(bundle.files) || !bundle.files.length || bundle.files.length > 2000) throw new Error('Invalid update package.');
  const names = new Set(); let size = 0;
  for (const file of bundle.files) {
    if (!appFile(file.path) || names.has(file.path.toLowerCase()) || !/^[a-f0-9]{64}$/.test(file.sha256) || typeof file.data !== 'string' || !/^[A-Za-z0-9+/]*={0,2}$/.test(file.data)) throw new Error('Invalid update file.');
    names.add(file.path.toLowerCase());
    const bytes = Buffer.from(file.data, 'base64'); size += bytes.length;
    if (size > MAX_EXPANDED || digest(bytes) !== file.sha256) throw new Error('Update file checksum failed: ' + file.path);
    file.bytes = bytes;
  }
  for (const required of ['planwharf.cjs', 'server.cjs', 'index.html', 'engine/app-updates.cjs', 'engine/apply-update.cjs', 'fixtures/starter-notebook.json']) if (!names.has(required)) throw new Error('Incomplete update package: ' + required);
  return bundle;
}
async function bounded(response, maximum) {
  if (!response.ok) throw new Error(`GitHub request failed (${response.status}). Try again later.`);
  if (Number(response.headers?.get('content-length')) > maximum) throw new Error('Update download is too large.');
  const chunks = []; let size = 0;
  for await (const chunk of response.body) { size += chunk.length; if (size > maximum) throw new Error('Update download is too large.'); chunks.push(Buffer.from(chunk)); }
  return Buffer.concat(chunks);
}
function createUpdateManager({ root, fetchImpl = fetch, checkpoint, activeCount = async () => 0, restart } = {}) {
  root = path.resolve(root);
  let state = { state: 'unchecked', currentVersion: null, latestVersion: null, available: false, canInstall: false, releaseUrl: null, reason: null, checkedAt: null };
  let latest = null, pending = null, installing = false;
  function snapshot() {
    let lastUpdateError = null;
    try { const result = JSON.parse(fs.readFileSync(path.join(root, '.planwharf-update-result.json'), 'utf8')); if (!result.ok) lastUpdateError = result.error; } catch { /* No previous update result. */ }
    return { ...state, lastUpdateError };
  }
  async function check() {
    if (installing) return snapshot();
    if (pending) return pending;
    state.state = 'checking';
    pending = (async () => {
      try {
        const installed = installation(root);
        state.currentVersion = installed?.version || null;
        const bytes = await bounded(await fetchImpl(`https://api.github.com/repos/${REPO}/releases/latest`, { headers: { Accept: 'application/vnd.github+json', 'User-Agent': 'Planwharf', 'X-GitHub-Api-Version': '2022-11-28' }, signal: AbortSignal.timeout(10000) }), 2 * 1024 * 1024);
        latest = JSON.parse(bytes.toString('utf8'));
        if (latest.draft || latest.prerelease || !versionParts(latest.tag_name) || latest.html_url !== `https://github.com/${REPO}/releases/tag/${latest.tag_name}`) throw new Error('GitHub returned an invalid stable release.');
        const asset = latest.assets?.find(asset => asset.name === 'planwharf-update.json.gz');
        state.latestVersion = latest.tag_name; state.releaseUrl = latest.html_url;
        state.available = installed ? newer(latest.tag_name, installed.version) : true;
        const git = checkout(root);
        state.canInstall = state.available && !git && Boolean(installed && restart && asset && /^sha256:[a-f0-9]{64}$/.test(asset.digest || ''));
        state.reason = git ? 'Git checkout: update manually to preserve local changes.' : !installed ? 'Download the portable release ZIP once to enable in-app updates.' : !restart ? 'Use the desktop launcher to update and restart.' : !asset?.digest ? 'This release has no verified update package. Download it manually.' : null;
        state.state = state.available ? 'available' : 'current'; state.checkedAt = new Date().toISOString();
      } catch (error) { latest = null; state.state = 'error'; state.canInstall = false; state.reason = error.message; }
      return snapshot();
    })().finally(() => { pending = null; });
    return pending;
  }
  async function install(confirmRunning = false) {
    if (installing) throw new Error('An update is already in progress.');
    if (!state.canInstall || !latest) throw new Error(state.reason || 'Check for an available update first.');
    if (await activeCount() > 0 && !confirmRunning) return { requiresConfirmation: true };
    installing = true; state.state = 'downloading';
    try {
      const selected = latest;
      const asset = selected.assets.find(asset => asset.name === 'planwharf-update.json.gz');
      const prefix = `https://github.com/${REPO}/releases/download/${selected.tag_name}/`;
      if (!asset.browser_download_url.startsWith(prefix)) throw new Error('Update download is outside the official release.');
      const data = await bounded(await fetchImpl(asset.browser_download_url, { signal: AbortSignal.timeout(60000) }), MAX_DOWNLOAD);
      if ('sha256:' + digest(data) !== asset.digest) throw new Error('Update download checksum failed.');
      const bundle = unpack(data, selected.tag_name);
      const installed = installation(root);
      if (!installed || !newer(bundle.version, installed.version)) throw new Error('The installed version changed. Check updates again.');
      verifyLocal(root, installed, bundle.files.map(file => file.path));
      const stage = path.join(root, '.planwharf-updates', crypto.randomUUID());
      fs.mkdirSync(stage, { recursive: true });
      fs.writeFileSync(path.join(stage, 'package.json.gz'), data, { flag: 'wx' });
      const helper = path.join(stage, 'apply-update.cjs'), support = path.join(stage, 'app-updates.cjs');
      fs.copyFileSync(path.join(root, 'engine/apply-update.cjs'), helper);
      fs.copyFileSync(path.join(root, 'engine/app-updates.cjs'), support);
      if (await activeCount() > 0 && !confirmRunning) { state.state = 'available'; return { requiresConfirmation: true }; }
      state.state = 'checkpointing';
      await checkpoint();
      state.state = 'restarting';
      await restart({ root, stage, helper, version: bundle.version, installed });
      return { ok: true, restarting: true, version: bundle.version };
    } catch (error) { state.state = 'error'; state.reason = 'Update stopped: ' + error.message; throw error; }
    finally { installing = false; }
  }
  return { snapshot, check, install, get busy() { return installing || state.state === 'restarting'; } };
}
module.exports = { createUpdateManager, digest, newer, appFile, installation, inside, checkout, verifyLocal, unpack };
