'use strict';
// node --test electron/import.test.cjs (after `node electron/prepare.cjs`): the first-start import, with Electron's dialogs replaced.
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const Module = require('node:module');

const answers = [];
const fake = {
  app: { requestSingleInstanceLock: () => true, on() {}, whenReady: () => new Promise(() => {}), getPath: () => os.tmpdir(), getVersion: () => '0.0.0' },
  dialog: { showMessageBox: async () => answers.shift(), showOpenDialog: async () => answers.shift() },
  BrowserWindow: class {}, shell: {}, Menu: {}
};
const load = Module._load;
Module._load = function (request, ...rest) { return request === 'electron' ? fake : load.call(this, request, ...rest); };
const { offerImport } = require('./main.cjs');

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'pw-import-'));
const starter = path.join(__dirname, 'app', 'fixtures', 'starter-notebook.json');

test('a fresh desktop app copies projects and history from the ZIP version, keeping both', async () => {
  const data = tmp(), zip = tmp();
  const plan = path.join(data, 'plan.json');
  fs.copyFileSync(starter, plan);
  const mine = JSON.parse(fs.readFileSync(starter, 'utf8'));
  const theirs = { ...mine, projects: [...mine.projects, { ...mine.projects[0], project: { ...mine.projects[0].project, name: 'Grid' } }] };
  fs.writeFileSync(path.join(zip, 'plan.json'), JSON.stringify(theirs));
  fs.mkdirSync(path.join(zip, '.attempts-plan', 'chat'), { recursive: true });
  fs.writeFileSync(path.join(zip, '.attempts-plan', 'chat', 'log.jsonl'), '{"a":1}\n');
  answers.push({ response: 0 }, { canceled: false, filePaths: [zip] });
  await offerImport(data, plan);
  assert.ok(JSON.parse(fs.readFileSync(plan, 'utf8')).projects.some(p => p.project.name === 'Grid'));
  assert.equal(fs.readFileSync(path.join(data, '.attempts-plan', 'chat', 'log.jsonl'), 'utf8'), '{"a":1}\n');
  assert.ok(fs.existsSync(path.join(zip, 'plan.json')), 'the ZIP version keeps its files');
  const marker = JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8'));
  assert.equal(marker.decided, 'imported');
  assert.ok(fs.existsSync(path.join(marker.backup, 'plan.json')), 'the desktop notebook is kept aside, not deleted');
  // It asks only once.
  await offerImport(data, plan);
  assert.equal(answers.length, 0);
});

test('a folder without plan.json is refused and the user can start without importing', async () => {
  const data = tmp(), empty = tmp();
  const plan = path.join(data, 'plan.json');
  answers.push({ response: 0 }, { canceled: false, filePaths: [empty] }, { response: 0 }, { response: 1 });
  await offerImport(data, plan);
  assert.equal(answers.length, 0);
  assert.equal(fs.existsSync(plan), false, 'the caller then copies the starter notebook');
  assert.equal(JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8')).decided, 'started without import');
});

test('a desktop app that already has its own projects is never asked', async () => {
  const data = tmp();
  const plan = path.join(data, 'plan.json');
  const mine = JSON.parse(fs.readFileSync(starter, 'utf8'));
  fs.writeFileSync(plan, JSON.stringify({ ...mine, projects: [{ ...mine.projects[0], project: { ...mine.projects[0].project, name: 'My own' } }] }));
  await offerImport(data, plan);
  assert.equal(JSON.parse(fs.readFileSync(path.join(data, 'desktop-import.json'), 'utf8')).decided, 'kept own projects');
});
