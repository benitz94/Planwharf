'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createAttemptEngine } = require('./attempts.js');
const { createOrchestrator } = require('./orchestrator.js');

const prView = JSON.stringify({
  headRefName: 'feat/x', baseRefName: 'main', mergeable: 'MERGEABLE', state: 'OPEN',
  statusCheckRollup: [{ name: 'ci', conclusion: 'FAILURE' }]
});

function runCommand(_folder, cmd, args) {
  if (cmd === 'gh') return Promise.resolve({ status: 0, stdout: prView, stderr: '' });
  if (cmd === 'git' && args[0] === 'rev-parse') return Promise.resolve({ status: 0, stdout: 'abc\n', stderr: '' });
  if (cmd === 'git' && args[0] === 'merge') return Promise.resolve({ status: 0, stdout: '', stderr: '' });
  return Promise.resolve({ status: 0, stdout: '', stderr: '' });
}

test('a background pull request fix obeys the saving mode and leaves the running crew alone', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'pw-saving-pr-'));
  const folder = path.join(root, 'project');
  fs.mkdirSync(folder);
  const dir = path.join(root, 'data');
  let pid = 400;
  const alive = new Set();
  const launches = [];
  const plans = [{ id: 'flag', readable: true, leftPct: 90 }, { id: 'light', readable: true, leftPct: 80 }];
  const models = [
    { id: 'fable', label: 'Claude Fable', provider: 'grok', adapter: 'grok', cli: 'grok-fable', providerCatalog: true, plan: 'flag', enabled: true, available: true, cost: 1, limit: 100, kind: 'premium', capabilities: { canCode: true, canUseTools: true, canInspectFilesystem: true }, roles: { orchestrator: true, worker: true, verifier: true } }
  ];
  const plan = { schema: 2, projects: [{ schema: 1, project: { id: 'keep', name: 'keep', folder }, revisions: [{ id: 1, direction: 'Ship keep', open: [] }], tasks: [{ id: 'job-keep', projectId: 'keep', revision_id: 1, title: 'Keep', detail: 'Build', status: 'running', executionVersion: 2, source: 'chat', proposedSubTasks: [{ title: 'Build the list', assignedModelId: 'fable', type: 'IMPLEMENTATION', detail: 'Build' }] }] }] };
  const orchestra = createOrchestrator({
    dir, readPlan: async () => plan, models, attempts: createAttemptEngine({ dir: path.join(dir, 'attempts') }),
    fetchPlans: async () => plans.map(item => ({ ...item })), plansTtl: 0, runCommand,
    inspectFolder: () => ({ git: false, dirty: [], names: ['notes.md'], fileCount: 1 }),
    spawnWorker: info => { pid += 1; if (!String(info.subTaskId || '').startsWith('fix-')) alive.add(pid); launches.push({ ...info, pid }); return { pid }; },
    isProcessAlive: id => alive.has(id),
    killWorker: id => { alive.delete(id); return true; }
  });
  const lines = async type => ((await orchestra.snapshot()).execution.timeline || []).filter(item => item.type === type);
  const waitFor = async (read, ms = 8000) => {
    const end = Date.now() + ms;
    let value = await read();
    while (!value && Date.now() < end) { await new Promise(resolve => setTimeout(resolve, 40)); value = await read(); }
    return value;
  };
  try {
    assert.equal((await orchestra.setModelRoles('fable', { orchestrator: true, worker: true, verifier: true })).ok, true);
    assert.equal((await orchestra.setSavingMode('keep', 'normal')).ok, true);
    await orchestra.tick();
    const row = (await orchestra.snapshot()).projects.find(item => item.id === 'keep');
    assert.equal((await orchestra.approve('keep', row.megaTaskId)).ok, true);
    const running = (await orchestra.snapshot()).projects.find(item => item.id === 'keep').subTasks.find(sub => sub.title === 'Build the list');
    assert.equal(running.status, 'RUNNING');
    assert.equal(running.assignedModelId, 'fable');
    const crewPid = launches.find(item => item.subTaskId === running.id).pid;
    await orchestra.setSavingMode('keep', 'ultra-safe');
    assert.equal((await orchestra.fixPullRequest('keep', 7)).started, true);
    const why = await waitFor(async () => (await lines('PR_FIX_FAILED')).map(item => item.summary).join('\n'));
    assert.match(why, /Ultra safe allows no model/);
    assert.match(why, /leaves out Claude Fable/);
    assert.equal(launches.some(item => String(item.subTaskId || '').startsWith('fix-')), false);
    const still = (await orchestra.snapshot()).projects.find(item => item.id === 'keep').subTasks.find(sub => sub.title === 'Build the list');
    assert.equal(still.status, 'RUNNING');
    assert.equal(still.assignedModelId, 'fable');
    assert.equal(alive.has(crewPid), true);
  } finally {
    await orchestra.shutdown();
    for (let attempt = 0; attempt < 20; attempt += 1) {
      try { fs.rmSync(root, { recursive: true, force: true }); break; }
      catch (error) {
        if (!['EBUSY', 'EPERM', 'ENOTEMPTY'].includes(error.code) || attempt === 19) throw error;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
    }
  }
});
