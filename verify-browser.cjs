// Real browser acceptance check, using Edge's DevTools protocol without dependencies.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const Plan = require('./plan.js');
const { createServer } = require('./server.cjs');
const { launch, connect } = require('./scripts/engineering/browser.cjs');
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const output = process.env.PLANWHARF_VALIDATION_DIR || path.join(__dirname, 'validation', 'browser-' + Date.now());
fs.mkdirSync(output, { recursive: true });
const planFile = path.join(output, `browser-plan-${Date.now()}.json`);
fs.writeFileSync(planFile, fs.readFileSync(path.join(__dirname, 'fixtures', 'notebook.json')));
const server = createServer({ planFile, models: [], fetchPlans: async () => [] });
let browser;
let session;
let command;
(async () => {
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const appUrl = `http://127.0.0.1:${server.address().port}`;
  browser = await launch();
  console.log(`Browser: ${browser.kind} ${browser.version}`);
  session = await connect(browser);
  command = session.command;
  const evaluate = async expression => {
    const result = await command('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  await command('Emulation.setDeviceMetricsOverride', { width: 1365, height: 1100, deviceScaleFactor: 1, mobile: false });
  await command('Page.navigate', { url: appUrl });
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('R2')")) break;
    await pause(50);
  }
  assert.equal(await evaluate("document.querySelectorAll('.project-home > section').length"), 5);
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R2 · manual · Current');
  assert.ok(await evaluate("document.getElementById('changes-content').textContent.toLowerCase().includes('nothing new')"), 'First access must show nothing new');
  assert.equal(await evaluate("document.getElementById('changes-content').textContent.includes('Harbor Street')"), false, 'Must not show example diff on first access');
  assert.equal(await evaluate("document.querySelectorAll('.task').length"), 0);
  const initial = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'desktop-example.png'), Buffer.from(initial.data, 'base64'));
  await evaluate("document.querySelector('[data-revision=\"1\"]').click()");
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R1 · manual · Historical');
  assert.ok(await evaluate("document.getElementById('plan-content').textContent.includes('Two possible locations identified')"));
  await evaluate("document.getElementById('new-revision').click()");
  assert.ok(await evaluate("document.getElementById('revision-form').elements.state.value.includes('Harbor Street selected')"), 'Draft must use latest revision even while R1 selected');
  await evaluate("(() => { const form = document.getElementById('revision-form'); form.elements.title.value = 'Turni confermati'; form.elements.state.value = 'Calendario completato; quattro volontari confermati.'; form.elements.closed.value += '\\nApertura il sabato mattina.'; form.elements.open.value = 'Chi segue il primo turno di apertura?'; form.requestSubmit(); })()");
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('R3')")) break;
    await pause(50);
  }
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R3 · manual · Current');
  assert.equal(await evaluate("document.querySelectorAll('.history-row').length"), 3);
  const persisted = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  assert.deepEqual(persisted.projects[0].revisions.slice(0, 2), Plan.example().revisions);
  assert.equal(persisted.projects[0].tasks[0].revision_id, 2);
  assert.equal(persisted.projects[0].tasks[0].revision_id, 2);
  assert.ok(await evaluate("document.getElementById('changes-content').textContent.includes('Calendario completato')"));
  await command('Page.reload');
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('R3')")) break;
    await pause(50);
  }
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R3 · manual · Current');
  assert.ok(await evaluate("document.getElementById('changes-content').textContent.toLowerCase().includes('nothing new')"), 'Reload without new revisions must show nothing new');
  // Wait for the visit marker save before simulating an external notebook edit.
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('home-goal')?.textContent.includes('Harbor Street')")) break;
    await pause(50);
  }

  // Multi-revision jump test: append R4 and R5 behind the scenes to planFile, reload, and verify jump From R3 to R5
  const multiPlan = JSON.parse(fs.readFileSync(planFile, 'utf8'));
  const lib = multiPlan.projects.find(p => p.project.id === 'biblioteca');
  const r4 = Plan.appendRevision(lib, { title: 'R4 Intermedio', direction: 'Direzione R4.', state: 'Stato R4.', closed: [...lib.revisions.at(-1).closed, 'Passo 4 chiuso.'], open: ['Domanda 4?'] });
  const r5 = Plan.appendRevision(r4, { title: 'R5 Finale', direction: 'Direzione R5 finale.', state: 'Stato R5 finale.', closed: [...r4.revisions.at(-1).closed, 'Passo 5 chiuso.'], open: ['Chi segue il primo turno di apertura?'] });
  multiPlan.projects[0] = r5;
  fs.writeFileSync(planFile, JSON.stringify(multiPlan, null, 2) + '\n');

  await command('Page.reload');
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('R5')")) break;
    await pause(50);
  }
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R5 · manual · Current');
  assert.ok(await evaluate("document.getElementById('change-context').textContent.includes('From R3 to R5')"), 'Must show jump From R3 to R5');
  assert.ok(await evaluate("document.getElementById('changes-content').textContent.includes('Passo 4 chiuso')"), 'Jump must include intermediate R4 changes');
  assert.ok(await evaluate("document.getElementById('changes-content').textContent.includes('Passo 5 chiuso')"), 'Jump must include R5 changes');

  // Reopen after jump: must now show nothing new
  await command('Page.reload');
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('R5')")) break;
    await pause(50);
  }
  assert.ok(await evaluate("document.getElementById('changes-content').textContent.toLowerCase().includes('nothing new')"), 'Subsequent reload must show nothing new');

  const updated = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'desktop-new-revision.png'), Buffer.from(updated.data, 'base64'));
  await command('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  assert.equal(await evaluate('document.documentElement.scrollWidth <= window.innerWidth'), true, 'Mobile layout must not overflow');
  const mobile = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
  fs.writeFileSync(path.join(output, 'mobile-new-revision.png'), Buffer.from(mobile.data, 'base64'));
  assert.equal(await evaluate("document.getElementById('project-select').options.length"), 2);
  await evaluate("document.getElementById('grill-open').click()");
  assert.equal(await evaluate("document.getElementById('grill-questions').textContent.includes('Free borrowing')"), false);
  await evaluate("(() => { const f = document.getElementById('grill-form'); f.elements['action-0'].value = 'close'; f.elements['answer-0'].value = 'Marta segue il primo turno.'; f.requestSubmit(); })()");
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('grill')")) break;
    await pause(50);
  }
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R6 · grill · Current');
  await evaluate("(() => { const s = document.getElementById('project-select'); s.value = 'orto'; s.dispatchEvent(new Event('change')); })()");
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R1 · import · Current');
  await evaluate("document.getElementById('new-task').click()");
  await evaluate("(() => { const f = document.getElementById('task-form'); f.elements.title.value = 'Definire irrigazione'; f.requestSubmit(); })()");
  for (let i = 0; i < 100; i++) {
    if (await evaluate("fetch('/api/plan').then(r => r.json()).then(b => b.plan.projects.find(p => p.project.id === 'orto').tasks.some(t => t.title === 'Definire irrigazione'))")) break;
    await pause(50);
  }
  const gardenJob = JSON.parse(fs.readFileSync(planFile, 'utf8')).projects.find(p => p.project.id === 'orto').tasks[0];
  assert.ok(gardenJob, await evaluate("JSON.stringify({taskError: document.getElementById('task-error').textContent, toast: document.getElementById('toast').textContent, dialog: document.getElementById('task-dialog').open})"));
  assert.equal(gardenJob.revision_id, 1);
  await evaluate("document.getElementById('new-task').click()");
  await evaluate("(() => { const f = document.getElementById('task-form'); f.elements.title.value = 'Secondo task'; f.requestSubmit(); })()");
  for (let i = 0; i < 100; i++) {
    if (await evaluate("!document.getElementById('task-error').hidden")) break;
    await pause(50);
  }
  assert.ok(await evaluate("document.getElementById('task-error').textContent.includes('already has a running task')"));
  await evaluate("document.getElementById('task-cancel').click()");
  await evaluate("document.getElementById('import-revision').click()");
  await evaluate("(() => { const f = document.getElementById('import-form'); f.elements.json.value = JSON.stringify({title:'Importato',direction:'Coltivare erbe aromatiche.',state:'Pronto.',closed:[],open:[]}); f.requestSubmit(); })()");
  for (let i = 0; i < 100; i++) {
    if (await evaluate("document.getElementById('revision-badge')?.textContent.includes('R2')")) break;
    await pause(50);
  }
  assert.equal(await evaluate("document.getElementById('revision-badge').textContent"), 'R2 · import · Current');
  assert.equal(session.consoleFailures.length, 0, 'Application browser console failures');
  const report = 'PASS: real selected Edge/Chrome project home, revisions, Grill, mobile layout, and isolated projects.\n';
  fs.writeFileSync(path.join(output, 'report.txt'), report);
  console.log(report.trim());
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  if (command) { try { await command('Browser.close'); } catch (_) {} }
  session?.close();
  await browser?.close();
  if (server.listening) await new Promise(resolve => server.close(resolve));
  fs.rmSync(planFile, { force: true });
});
