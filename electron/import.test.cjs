'use strict';
// node --test electron/import.test.cjs (after `node electron/prepare.cjs`): the first-start import, with Electron's dialogs replaced.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Module = require('node:module');

const answers = [];
const asked = [];
const paths = {};
const fake = {
  app: { requestSingleInstanceLock: () => true, on() {}, whenReady: () => new Promise(() => {}), getPath: name => paths[name] || path.join(os.tmpdir(), 'pw-none'), getVersion: () => '0.0.0' },
  dialog: { showMessageBox: async options => { asked.push(options.message); return answers.shift(); }, showOpenDialog: async () => answers.shift() },
  BrowserWindow: class {}, shell: {}, Menu: {}
};
const load = Module._load;
Module._load = function (request, ...rest) { return request === 'electron' ? fake : load.call(this, request, ...rest); };
const { offerImport, findPortable } = require('./main.cjs');

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'pw-import-'));
const starter = path.join(__dirname, 'app', 'fixtures', 'starter-notebook.json');
const base = JSON.parse(fs.readFileSync(starter, 'utf8'));
const notebook = (...names) => JSON.stringify({ ...base, projects: names.map(name => ({ ...base.projects[0], project: { ...base.projects[0].project, name } })) });
function portable(parent, ...names) {
  const dir = path.join(parent, 'Projects', 'Planwharf');
  fs.mkdirSync(path.join(dir, '.attempts-plan', 'chat'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'planwharf.cjs'), '');
  fs.writeFileSync(path.join(dir, 'plan.json'), notebook(...names));
  fs.writeFileSync(path.join(dir, '.attempts-plan', 'chat', 'log.jsonl'), '{"a":1}\n');
  fs.writeFileSync(path.join(dir, 'plan.json.settings.json'), '{"team":"mine"}');
  return dir;
}

test('the ZIP version is found on the Desktop without asking where it is', async () => {
  const desktop = tmp();
  paths.desktop = desktop;
  const dir = portable(desktop, 'Grid', 'RaceEngineer');
  const found = await findPortable([]);
  assert.ok(found.some(c => c.dir === dir && c.names.includes('Grid')));
  delete paths.desktop;
});

test('a fresh desktop app offers the projects it found and copies them, keeping both', async () => {
  const data = tmp(), zip = portable(tmp(), 'Grid');
  const plan = path.join(data, 'plan.json');
  fs.copyFileSync(starter, plan);
  answers.push({ response: 0 });
  asked.length = 0;
  await offerImport(data, plan, { find: async () => [{ dir: zip, names: ['Grid'], time: 1 }] });
  assert.match(asked[0], /1 Planwharf project/);
  assert.ok(JSON.parse(fs.readFileSync(plan, 'utf8')).projects.some(p => p.project.name === 'Grid'));
  assert.equal(fs.readFileSync(path.join(data, '.attempts-plan', 'chat', 'log.jsonl'), 'utf8'), '{"a":1}\n');
  assert.equal(fs.readFileSync(path.join(data, 'plan.json.settings.json'), 'utf8'), '{"team":"mine"}', 'the team settings come too');
  assert.ok(fs.existsSync(path.join(zip, 'plan.json')), 'the ZIP version keeps its files');
  const marker = JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8'));
  assert.equal(marker.decided, 'imported');
  assert.ok(fs.existsSync(path.join(marker.backup, 'plan.json')), 'the desktop notebook is kept aside, not deleted');
  await offerImport(data, plan, { find: async () => { throw new Error('asked twice'); } });
});

test('nothing found: the app starts without any question', async () => {
  const data = tmp();
  asked.length = 0;
  await offerImport(data, path.join(data, 'plan.json'), { find: async () => [] });
  assert.equal(asked.length, 0);
  assert.equal(JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8')).decided, 'nothing found');
});

test('Start without them keeps the desktop notebook', async () => {
  const data = tmp(), zip = portable(tmp(), 'Grid');
  answers.push({ response: 1 });
  await offerImport(data, path.join(data, 'plan.json'), { find: async () => [{ dir: zip, names: ['Grid'], time: 1 }] });
  assert.equal(fs.existsSync(path.join(data, 'plan.json')), false, 'the caller then copies the starter notebook');
  assert.equal(JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8')).decided, 'started without import');
});

test('a desktop app that already has its own projects is never asked', async () => {
  const data = tmp();
  const plan = path.join(data, 'plan.json');
  fs.writeFileSync(plan, notebook('My own'));
  await offerImport(data, plan, { find: async () => { throw new Error('should not search'); } });
  assert.equal(JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8')).decided, 'kept own projects');
});
