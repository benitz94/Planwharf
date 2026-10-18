'use strict';
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const NAMES = { edge: 'Microsoft Edge', chrome: 'Google Chrome', brave: 'Brave', opera: 'Opera', firefox: 'Mozilla Firefox', chromium: 'Chromium' };
function browserCandidates(id) {
  const local = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  const program = process.env.ProgramFiles || 'C:\\Program Files';
  const program86 = process.env['ProgramFiles(x86)'] || 'C:\\Program Files (x86)';
  const windows = { edge: ['Microsoft/Edge/Application/msedge.exe'], chrome: ['Google/Chrome/Application/chrome.exe'], brave: ['BraveSoftware/Brave-Browser/Application/brave.exe'], opera: ['Programs/Opera/launcher.exe', 'Programs/Opera/opera.exe', 'Opera/launcher.exe', 'Opera/opera.exe'], firefox: ['Mozilla Firefox/firefox.exe'], chromium: ['Chromium/Application/chrome.exe'] };
  const commands = { edge: ['msedge','microsoft-edge','microsoft-edge-stable'], chrome: ['google-chrome','google-chrome-stable'], brave: ['brave-browser','brave'], opera: ['opera'], firefox: ['firefox'], chromium: ['chromium','chromium-browser'] };
  const mac = { edge: 'Microsoft Edge.app/Contents/MacOS/Microsoft Edge', chrome: 'Google Chrome.app/Contents/MacOS/Google Chrome', brave: 'Brave Browser.app/Contents/MacOS/Brave Browser', opera: 'Opera.app/Contents/MacOS/Opera', firefox: 'Firefox.app/Contents/MacOS/firefox', chromium: 'Chromium.app/Contents/MacOS/Chromium' };
  return [...windows[id].flatMap(relative => [program, program86, local].map(root => path.join(root, relative))),
    path.join('/Applications', mac[id]), path.join(os.homedir(), 'Applications', mac[id]),
    ...(process.env.PATH || '').split(path.delimiter).filter(Boolean).flatMap(root => commands[id].map(command => path.join(root, command + (process.platform === 'win32' ? '.exe' : ''))))];
}
function executable(file) { try { return fs.statSync(file).isFile(); } catch { return false; } }
function detectBrowsers() { return Object.entries(NAMES).map(([id,label]) => ({ id, label, path: browserCandidates(id).find(executable) || null })); }
function validateBrowser(value) {
  if (!value || Object.keys(value).some(key => !['id','path','family'].includes(key)) || !['auto','default','custom',...Object.keys(NAMES)].includes(value.id)) throw new Error('Invalid browser selection.');
  if (value.id === 'custom') {
    if (typeof value.path !== 'string' || value.path.length > 1000 || !path.isAbsolute(value.path) || !executable(value.path) || (process.platform === 'win32' && path.extname(value.path).toLowerCase() !== '.exe') || !['chromium','firefox'].includes(value.family)) throw new Error('Choose an existing absolute browser executable path and its browser family.');
    return { id: 'custom', path: value.path, family: value.family };
  }
  if (value.id !== 'auto' && value.id !== 'default' && !detectBrowsers().find(browser => browser.id === value.id)?.path) throw new Error('The selected browser is not installed. Choose a custom executable path.');
  return { id: value.id };
}
function readBrowserPreference(planFile) {
  try { return JSON.parse(fs.readFileSync(`${planFile}.browser.json`, 'utf8')); }
  catch (error) { if (error.code === 'ENOENT') return { id: 'auto' }; throw error; }
}
function resolveBrowser(value) {
  if (value.id === 'default') return null;
  if (value.id === 'custom') return validateBrowser(value);
  const catalog = detectBrowsers();
  const browser = value.id === 'auto' ? catalog.find(browser => browser.path) : catalog.find(browser => browser.id === value.id && browser.path);
  if (!browser && value.id !== 'auto') throw new Error('Saved browser is unavailable. Use --browser default or choose another executable.');
  return browser ? { ...browser, family: browser.id === 'firefox' ? 'firefox' : 'chromium' } : null;
}
function browserArguments(browser, url, profileDir) {
  return browser.family === 'firefox' ? ['--no-remote', '--profile', profileDir, '--new-window', url]
    : ['--no-first-run','--no-default-browser-check','--start-fullscreen','--disable-background-mode','--app=' + url, '--user-data-dir=' + profileDir];
}
module.exports = { detectBrowsers, validateBrowser, readBrowserPreference, resolveBrowser, browserArguments };
