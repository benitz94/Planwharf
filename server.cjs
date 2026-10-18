'use strict';
const http = require('node:http');
const fs = require('node:fs/promises');
const path = require('node:path');
const crypto = require('node:crypto');
const { AsyncLocalStorage } = require('node:async_hooks');
const { spawn } = require('node:child_process');
const Plan = require('./plan.js');
const { createAttemptEngine } = require('./engine/attempts.js');
const { createSentinel } = require('./engine/sentinel.js');
const { createOrchestrator, jobFingerprint } = require('./engine/orchestrator.js');
const { createSkillsCache } = require('./engine/skills.js');
const { createSkillCatalog } = require('./engine/skill-catalog.cjs');
const { detectBrowsers, validateBrowser, readBrowserPreference } = require('./engine/browser-settings.cjs');
const { createProviderModels } = require('./engine/provider-models.cjs');
const { loadCatalog } = require('./engine/provider-catalog.cjs');
const { adapterFor } = require('./engine/provider-adapters.cjs');
const defaultCatalog = loadCatalog(require('./models.json'));
const defaultModels = defaultCatalog.models;
const { exportProject } = require('./engine/project-export.cjs');
const { projectHome } = require('./engine/project-home.cjs');
const { ensureDefaultNotebook } = require('./engine/notebook-file.cjs');

// Notes and visit markers do not invalidate an open Living Plan editor.
const withoutHome = ({ home, ...project }) => project;
const version = plan => crypto.createHash('sha256').update(JSON.stringify(plan.schema === 2 ? { ...plan, projects: plan.projects.map(withoutHome) } : withoutHome(plan))).digest('hex');
class RequestError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}
const parsedBodies = new WeakMap();
async function readPlan(file) {
  ensureDefaultNotebook(file);
  const plan = JSON.parse(await fs.readFile(file, 'utf8'));
  if (!Plan.validate(plan)) throw new Error('The plan file is invalid.');
  return plan;
}
async function writePlan(file, plan) {
  const temporary = `${file}.${crypto.randomUUID()}.tmp`;
  let handle;
  try {
    handle = await fs.open(temporary, 'wx');
    await handle.writeFile(JSON.stringify(plan, null, 2) + '\n', 'utf8');
    await handle.sync();
    await handle.close();
    handle = null;
    await fs.rename(temporary, file);
  } finally {
    if (handle) await handle.close();
    await fs.rm(temporary, { force: true });
  }
}
async function body(request, maxBytes = 65536, tooLarge = 'Revision too large.') {
  const cached = parsedBodies.get(request);
  if (cached) { if (cached.size > maxBytes) throw new RequestError(413, tooLarge); return cached.value; }
  const chunks = [];
  let size = 0;
  for await (const chunk of request) {
    size += chunk.length;
    if (size > maxBytes) throw new RequestError(413, tooLarge);
    chunks.push(chunk);
  }
  try { const value = JSON.parse(Buffer.concat(chunks).toString('utf8')); parsedBodies.set(request, { value, size }); return value; }
  catch { throw new RequestError(400, 'Request unreadable.'); }
}
function defaultOpenFolder(folder) {
  const opts = { detached: true, stdio: 'ignore' };
  if (process.platform === 'win32') spawn('explorer.exe', [folder], opts).unref();
  else if (process.platform === 'darwin') spawn('open', [folder], opts).unref();
  else spawn('xdg-open', [folder], opts).unref();
}
function defaultOpenUrl(url) {
  if (process.platform === 'win32') spawn('cmd.exe', ['/c', 'start', '', url], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
  else if (process.platform === 'darwin') spawn('open', [url], { detached: true, stdio: 'ignore' }).unref();
  else spawn('xdg-open', [url], { detached: true, stdio: 'ignore' }).unref();
}
async function assertFolder(folder) {
  if (!folder) return;
  let st;
  try { st = await fs.stat(folder); } catch { throw new RequestError(400, 'Folder not found.'); }
  if (!st.isDirectory()) throw new RequestError(400, 'Folder not found.');
}
function createServer({ phase2 = true, planFile = path.join(__dirname, 'plan.json'), save = writePlan, attemptsDir, openFolder = defaultOpenFolder, openUrl = defaultOpenUrl, models = defaultModels, providers, inspectFolder, readRepository, skills, fetchSkills, syncSkills, skillsEnabled = true, discoverModels, readAccount, refreshModels = models === defaultModels, spawnWorker, isProcessAlive, killWorker, askModel, abortAsk, abortSupervisor, openWatchWindow, openPlanWindow, appWindows, quitApp, monitorIntervalMs = 4000, fetchPlans, updateManager, restartAfterUpdate } = {}) {
  const storeDir = attemptsDir || path.join(path.dirname(planFile), `.attempts-${path.basename(planFile, path.extname(planFile))}`);
  const connectionFile = path.join(storeDir, 'provider-connections.json');
  let connections = [];
  try { connections = JSON.parse(require('node:fs').readFileSync(connectionFile, 'utf8')); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (!Array.isArray(connections)) throw new Error('Invalid provider connections file.');
  const catalog = loadCatalog(providers ? { schema: 3, providers } : models === defaultModels ? require('./models.json') : models);
  models = catalog.models; providers = catalog.providers;
  for (const saved of connections) {
    const provider = require('./engine/provider-connections.cjs').connection(saved);
    if (providers.some(item => item.id === provider.id)) throw new Error('Duplicate saved provider connection: ' + provider.id);
    const extra = loadCatalog({ schema: 3, providers: [provider] });
    providers.push(...extra.providers); models.push(...extra.models);
  }
  const providerModels = createProviderModels(models, { providers, cacheFile: path.join(storeDir, 'provider-model-catalogs.json'), ...(discoverModels ? { discover: discoverModels } : {}) });
  const accountStates = new Map();
  let accountSync;
  async function syncAccounts() {
    // Injected discovery fixtures must not inspect real login profiles.
    if (!readAccount && (!refreshModels || discoverModels)) return;
    if (accountSync) return accountSync;
    accountSync = (async () => {
      for (const provider of providers) {
        let account;
        try { account = await (readAccount ? readAccount(provider) : adapterFor(provider)?.account?.(provider)); } catch { account = null; }
        account ||= require('./engine/provider-accounts.cjs').unknownAccount();
        const previous = accountStates.get(provider.id);
        provider.account = account;
        providerModels.observeScope(provider.id);
        // Do not commit the new account identity before its stale access cache
        // has been durably invalidated. Failed writes must be retried on restart.
        const changedOnDisk = orchestra.bindAccount(provider.id, account.key, { commit: false });
        if (changedOnDisk || previous && previous.key !== account.key) {
          await providerModels.invalidate(provider.id);
          for (const model of models.filter(model => model.providerId === provider.id)) {
            model.available = false;
            model.unavailableReason = 'CLI account changed. Access must be verified for the current login.';
          }
          orchestra.invalidateAccountCache(provider.id);
          if (previous || !refreshModels) await providerModels.refresh(true, provider.id);
        }
        orchestra.bindAccount(provider.id, account.key);
        accountStates.set(provider.id, account);
      }
    })().finally(() => { accountSync = null; });
    return accountSync;
  }
  if (fetchPlans === undefined) fetchPlans = async () => {
    const pools = new Map();
    for (const provider of providers) if (!pools.has(provider.quotaPoolId)) {
      const expectedAccount = provider.account?.key || null;
      let quota; try { quota = await adapterFor(provider)?.quota?.(provider); } catch { quota = null; }
      let current = provider.account;
      if (provider.account) {
        try { current = await (readAccount ? readAccount(provider) : adapterFor(provider)?.account?.(provider)); } catch { current = null; }
      }
      if (expectedAccount !== (current?.key || null)) quota = null;
      pools.set(provider.quotaPoolId, { ...(quota || { readable: false, exhausted: false, leftPct: null }), id: provider.quotaPoolId, label: require('./engine/provider-accounts.cjs').accountLabel(provider.label, provider.account), accountKey: provider.account?.key || null });
    }
    return [...pools.values()];
  };
  let queue = Promise.resolve();
  const transaction = new AsyncLocalStorage();
  const settingsFile = `${planFile}.settings.json`;
  async function readSettings() {
    try {
      const value = JSON.parse(await fs.readFile(settingsFile, 'utf8'));
      if (typeof value.projectsRoot !== 'string') throw new Error('Invalid projects folder settings.');
      return { projectsRoot: value.projectsRoot };
    } catch (error) {
      if (error.code === 'ENOENT') return { projectsRoot: '' };
      throw error;
    }
  }
  async function setProjectsRoot(value) {
    if (typeof value !== 'string' || value.length > 500) throw new RequestError(400, 'Invalid projects folder.');
    const folder = value.trim();
    if (folder && !path.isAbsolute(folder)) throw new RequestError(400, 'Use an absolute projects folder path.');
    await assertFolder(folder);
    const settings = { projectsRoot: folder ? path.resolve(folder) : '' };
    await writePlan(settingsFile, settings);
    return settings;
  }
  async function createProjectFolder(name) {
    const { projectsRoot } = await readSettings();
    if (!projectsRoot) throw new RequestError(400, 'Choose a projects folder first.');
    await assertFolder(projectsRoot);
    let leaf = name.normalize('NFKC').replace(/[<>:"/\\|?*\x00-\x1f]/g, '-').trim().slice(0, 100).replace(/[. ]+$/g, '');
    if (!leaf || leaf === '.' || leaf === '..') throw new RequestError(400, 'Choose a valid project name.');
    if (/^(con|prn|aux|nul|com[0-9]|lpt[0-9])(?:\.|$)/i.test(leaf)) leaf = 'Project-' + leaf;
    for (let n = 1; n <= 1000; n++) {
      const folder = path.join(projectsRoot, leaf + (n === 1 ? '' : `-${n}`));
      if (path.dirname(folder) !== path.resolve(projectsRoot)) throw new RequestError(400, 'Invalid project folder.');
      try { await fs.mkdir(folder); return folder; }
      catch (error) { if (error.code !== 'EEXIST') throw error; }
    }
    throw new RequestError(409, 'Too many folders with this project name.');
  }
  const enqueue = work => {
    if (transaction.getStore()?.active) return Promise.resolve().then(work);
    const pending = queue.then(() => transaction.run({ active: true }, async () => {
      const context = transaction.getStore();
      try { return await work(); } finally { context.active = false; }
    }));
    queue = pending.catch(() => {});
    return pending;
  };
  const attempts = createAttemptEngine({ dir: storeDir });
  let orchestra;
  const sentinel = createSentinel(attempts, {
    isAlive: attempt => {
      if (typeof spawnWorker === 'function' && typeof isProcessAlive === 'function') {
        const pid = orchestra && typeof orchestra.pidFor === 'function' ? orchestra.pidFor(attempt) : null;
        if (Number.isInteger(pid) && pid > 0) return Boolean(isProcessAlive(pid));
        return false;
      }
      return Boolean(attempt && attempt.status === 'active');
    }
  });
  const skillsCache = skills || createSkillsCache({ dir: path.join(storeDir, 'matt-skills'), fetchImpl: fetchSkills });
  // Catalog preparation is project scoped; it does not change legacy execution delivery.
  let projectSkills;
  try { projectSkills = createSkillCatalog({ dir: path.join(storeDir, 'project-skills'), ...(fetchSkills ? { fetchImpl: fetchSkills } : {}) }); } catch { /* Optional catalog failure must not prevent execution recovery. */ }
  let skillGeneration, procedureEvidence;
  try { procedureEvidence = require('./engine/skill-procedure-evidence.cjs').createSkillProcedureEvidence({
    dir: path.join(storeDir, 'project-skills'), attemptsDir: storeDir,
    snapshot: projectId => ({ ...orchestra.executionView(projectId), deliveries: projectSkills.reconcile(projectId).deliveries }),
    projectResolver: projectId => { const item = projectForSkills(projectId); return { id: item.project.id, folder: item.project.folder }; }
  }); } catch { /* Optional evidence failure must not affect execution recovery. */ }
  try { skillGeneration = require('./engine/skill-generation.cjs').createSkillGeneration({ dir: path.join(storeDir, 'project-skills'),
    ...(procedureEvidence ? { resolveProcedureProposal: (projectId, proposalId) => procedureEvidence.proposal(projectId, proposalId) } : {})
  }); } catch { /* Preserve a damaged optional draft store for recovery. */ }
  let catalogQueue = Promise.resolve();
  const enqueueCatalog = fn => { const pending = catalogQueue.then(fn); catalogQueue = pending.catch(() => {}); return pending; };
  const skillsPolicyFile = path.join(storeDir, 'skills-policy.json');
  try { skillsEnabled = JSON.parse(require('node:fs').readFileSync(skillsPolicyFile, 'utf8')).enabled !== false; } catch {}
  const skillsSnapshot = () => ({ ...skillsCache.snapshot(), enabled: skillsEnabled });
  let initialSkillsSync = syncSkills ? skillsCache.ensure() : null;
  const catalogEnabled = projectId => Boolean(projectSkills && projectId && projectSkills.snapshot(projectId).preferences.deliveryEnabled);
  function projectForSkills(projectId) {
    const plan = JSON.parse(require('node:fs').readFileSync(planFile, 'utf8'));
    if (!Plan.validate(plan)) throw new Error('Cannot authorize Skills against an invalid notebook.');
    const item = (plan.schema === 2 ? plan.projects : [plan]).find(item => item.project.id === projectId);
    if (!item) throw new Error('Skills delivery requires an existing explicit project.');
    return item;
  }
  function attachProjectSkills(info, role) {
    const item = projectForSkills(info.projectId), model = info.worker || info.model;
    if (!model?.cli || !(model.providerId || model.provider)) throw new Error('Skills delivery requires exact provider/model identity.');
    if (item.project.folder) projectSkills.discoverLocal(info.projectId, item.project.folder);
    const assigned = projectSkills.attach({ projectId: info.projectId, projectFolder: item.project.folder, role,
      taskId: info.subTaskId || info.job?.id || info.state?.currentRequest?.taskId || null,
      attemptId: info.attemptId || null, provider: model.providerId || model.provider, model: model.cli,
      adapter: model.adapter || model.provider, task: [info.job?.title, info.job?.detail, info.question].filter(Boolean).join('\n'),
      requestedSkills: (info.grillKind || info.clarificationKind) === 'docs' ? ['matt/grill-with-docs'] : [],
      requirements: item.revisions.at(-1)?.direction || '', projectType: item.project.type || '' });
    const settings = projectSkills.snapshot(info.projectId).preferences;
    if (settings.mode === 'manual') {
      const unavailable = settings.selected.filter(id => !settings.disabled.includes(id) && !settings.exclusions.includes(id)
        && !assigned.delivery.skills.some(skill => skill.id === id)
        && !assigned.delivery.rejected.some(skill => skill.id === id && ['Source excluded by project.', 'Role incompatible.', 'Explicit docs interview required.', 'Duplicate instruction content.'].includes(skill.reason)));
      if (unavailable.length) throw new Error('Manually selected Skills are unavailable for this execution: ' + unavailable.join(', ') + '. Inspect catalog eligibility and approve current content before retrying.');
    }
    return assigned;
  }
  async function readySkills(projectId) {
    if (catalogEnabled(projectId)) return;
    if (!projectId) {
      const plan = JSON.parse(require('node:fs').readFileSync(planFile, 'utf8'));
      const active = (plan.schema === 2 ? plan.projects : [plan]).filter(item => item.tasks.some(task => task.status === 'running'));
      if (!active.length || active.every(item => catalogEnabled(item.project.id))) return;
    }
    if (!skillsEnabled) return;
    if (!initialSkillsSync && !skillsCache.snapshot().sha) initialSkillsSync = skillsCache.ensure();
    if (initialSkillsSync) await initialSkillsSync;
    if (!skillsCache.prompt?.('lead')) throw new Error('Matt skills are enabled but no complete catalog is available. Check skills or turn off Use Matt skills before running models.');
  }
  function skillsPrompt(role, context) {
    if (!skillsEnabled) return 'Matt skills are disabled for this notebook. Do not load or invoke Matt skills for this request.';
    const prompt = skillsCache.prompt?.(role, context);
    if (!prompt) throw new Error('Matt skills are enabled but their complete catalog is unavailable.');
    return prompt;
  }
  orchestra = createOrchestrator({
    phase2,
    readPlan: () => readPlan(planFile),
    completeTasks: completions => enqueue(async () => {
      const current = await readPlan(planFile);
      let changed = false;
      const projects = (current.schema === 2 ? current.projects : [current]).map(item => {
        const tasks = item.tasks.map(task => {
          const completion = completions.find(record => record.projectId === item.project.id && record.taskId === task.id);
          if (!completion || task.status !== 'running' || jobFingerprint(task) !== completion.fingerprint) return task;
          changed = true;
          return { ...task, status: 'complete', completedAt: new Date().toISOString(), attemptId: completion.attemptId, ...(completion.megaTaskId ? { megaTaskId: completion.megaTaskId } : {}) };
        });
        return { ...item, tasks };
      });
      const next = current.schema === 2 ? { ...current, projects } : projects[0];
      if (changed) { if (!Plan.validate(next)) throw new Error('Completed tasks failed notebook validation.'); await save(planFile, next); }
      return next;
    }),
    attempts,
    sentinel,
    models,
    providers,
    enforceSessionBoundary: true,
    mattSkillsEnabled: projectId => catalogEnabled(projectId) || skillsEnabled,
    projectSkillState: projectId => {
      if (!projectSkills) return { available: false, error: 'Project Skills registry unavailable.' };
      const state = projectSkills.reconcile(projectId);
      return { available: true, deliveryEnabled: state.preferences.deliveryEnabled, mode: state.preferences.mode,
        enabled: state.catalog.filter(row => row.enabled).map(({ id, name, source, contentHash }) => ({ id, name, source, contentHash })),
        deliveries: state.deliveries.slice(-12) };
    },
    dir: storeDir,
    inspectFolder,
    readRepository,
    spawnWorker: typeof spawnWorker === 'function' ? info => {
      if (!projectSkills) throw new Error('Project Skills registry is unavailable; recover it before starting new work.');
      if (catalogEnabled(info.projectId)) {
        const assigned = attachProjectSkills(info, info.job?.kind === 'audit' ? 'reviewer' : 'worker');
        return spawnWorker({ ...info, skillsPrompt: assigned.prompt, ...(assigned.delivery.attached ? { skillDelivery: assigned.skillDelivery } : {}) });
      }
      return spawnWorker({ ...info, job: { ...info.job, detail: [info.job?.detail || '', skillsPrompt(info.job?.kind === 'research' ? 'research' : info.job?.kind === 'audit' ? 'audit' : 'worker', `${info.job?.title || ''} ${info.job?.detail || ''}`)].join('\n\n') } });
    } : undefined,
    isProcessAlive,
    killWorker,
    askModel: typeof askModel === 'function' ? async info => {
      if (info.purpose === 'supervisor') return askModel(info);
      if (!projectSkills) throw new Error('Project Skills registry is unavailable; recover it before starting new work.');
      if (catalogEnabled(info.projectId)) {
        try {
        const assigned = attachProjectSkills(info, 'orchestrator');
        if (info.grillKind || info.clarificationKind) {
          const required = (info.grillKind || info.clarificationKind) === 'docs' ? ['matt/grilling', 'matt/grill-with-docs'] : ['matt/grilling'];
          if (required.some(id => !assigned.delivery.skills.some(item => item.id === id))) throw new Error('The project catalog requires approved, selected ' + required.join(' and ') + ' for this interview. Check content approval, selection and dependencies; native Skill tools are not invoked.');
        }
        return await askModel({ ...info, skillsPrompt: assigned.prompt, ...(assigned.delivery.attached ? { skillDelivery: assigned.skillDelivery } : {}) });
        } catch (error) {
          if (info.grillKind || info.clarificationKind) error.code = 'PROJECT_SKILL_INTERVIEW_UNAVAILABLE';
          throw error;
        }
      }
      await readySkills(info.projectId);
      let prompt = skillsPrompt('lead', info.grillKind ? 'grilling ' + info.question : info.question);
      if (info.grillKind) {
        if (!skillsEnabled) throw new Error('Matt skills are disabled. Enable Use Matt skills to start Matt grilling.');
        const grilling = skillsCache.read?.('skills/productivity/grilling/SKILL.md');
        if (!grilling) throw new Error('Matt grilling skill is unavailable. Check skills updates before starting Grill me.');
        const docs = info.grillKind === 'docs' ? skillsCache.read?.('skills/engineering/grill-with-docs/SKILL.md') : '';
        if (info.grillKind === 'docs' && !docs) throw new Error('Matt Grill with docs skill is unavailable. Check skills updates.');
        prompt += '\n\nACTIVE MATT GRILLING SESSION:\n' + grilling + (docs ? '\n\n' + docs : '')
          + '\nConduct the interview in this chat. Use the selected project goal, existing decisions and prior answers. Inspect available project facts with your lead tools. Ask the currently answerable frontier, then wait for the user. Do not create implementation tasks, modify project source or dispatch workers. Only an explicit Start or approval after clarification may prepare execution.';
      }
      return askModel({ ...info, skillsPrompt: prompt });
    } : undefined,
    abortAsk, abortSupervisor,
    fetchPlans,
    readSkill: (relative, projectId) => (!catalogEnabled(projectId) && skillsEnabled && typeof skillsCache.read === 'function' ? skillsCache.read(relative) : ''),
    prepareTask: (projectId, question, stillAuthorized, goldModel) => enqueue(async () => {
      const current = await readPlan(planFile);
      const item = (current.schema === 2 ? current.projects : [current]).find(item => item.project.id === projectId);
      if (!item) return { ok: false, reason: 'Selected project no longer exists.' };
      const live = (await orchestra.snapshot(projectId)).projects.find(row => row.id === projectId);
      if (live && live.workerAlive) return { ok: false, reason: 'This project has active workers. Stop its current job before preparing a new task.' };
      if (!stillAuthorized()) return { ok: false, reason: 'Request cancelled before creating the task.' };
      const task = { id: 'chat-' + crypto.randomUUID(), projectId, revision_id: item.revisions.at(-1).id, title: question.split('\n')[0].slice(0, 120), detail: question, status: 'running', ...(phase2 ? { executionVersion: 2 } : {}), source: 'chat', ...(goldModel ? { goldModel } : {}), createdAt: new Date().toISOString(), timing: 'discretion', kind: /\b(audit|review)\b/i.test(question) && !/\b(implement|fix|sistema)\b/i.test(question) ? 'audit' : 'implementation' };
      const updated = { ...item, tasks: [...item.tasks.map(task => task.status === 'running' ? { ...task, status: 'superseded' } : task), task] };
      const next = current.schema === 2 ? { ...current, projects: current.projects.map(item => item.project.id === projectId ? updated : item) } : updated;
      if (!Plan.validate(next)) return { ok: false, reason: 'New task failed notebook validation.' };
      await save(planFile, next);
      orchestra.prepareForNewTask(projectId);
      return { ok: true, task, snapshot: await orchestra.tick(projectId) };
    }),
    applyStart: (projectId, options) => enqueue(async () => {
      const current = await readPlan(planFile);
      return executeStart(current, projectId, options);
    }),
    applySkip: projectId => enqueue(async () => {
      const current = await readPlan(planFile);
      return executeSkip(current, projectId);
    })
  });
  async function executeSkip(current, projectId) {
    const project = current.schema === 2 ? current.projects.find(item => item.project.id === projectId) : current;
    if (!project || (current.schema === 1 && projectId !== project.project.id)) return { ok: false, reason: 'project not found' };
    const latest = project.revisions.at(-1);
    let next = current;
    if (latest && Array.isArray(latest.open) && latest.open.length) {
      const draft = {
        title: 'Skip grill',
        direction: latest.direction,
        state: (latest.state && latest.state.trim()) ? latest.state : 'Skipped grill so Auto start can plan.',
        closed: Array.isArray(latest.closed) ? latest.closed : [],
        open: []
      };
      const updated = Plan.appendRevision(project, draft);
      next = current.schema === 2 ? { ...current, projects: current.projects.map(item => item.project.id === project.project.id ? updated : item) } : updated;
      if (!Plan.validate(next)) return { ok: false, reason: 'The updated notebook is invalid.' };
      await save(planFile, next);
    }
    const result = await orchestra.skipGrill(projectId);
    if (!result.ok) return { ok: false, reason: result.reason || 'failed' };
    return { ok: true, plan: next, version: version(next), snapshot: result.snapshot };
  }
  async function executeStart(current, projectId, options) {
    const prepared = await orchestra.startAutoTask(projectId, options || { timing: 'discretion' });
    if (!prepared.ok) return prepared;
    let project = current.schema === 2 ? current.projects.find(item => item.project.id === projectId) : current;
    if (!project || (current.schema === 1 && projectId !== project.project.id)) return { ok: false, reason: 'project not found' };
    let updated = project;
    try {
      if (prepared.clearJob) {
        const running = (updated.tasks || []).find(task => task.status === 'running');
        if (running) updated = running.executionVersion === 2 ? { ...updated, tasks: updated.tasks.map(task => task.id === running.id ? { ...task, status: 'superseded' } : task) } : Plan.removeTask(updated, { id: running.id });
      }
      if (Array.isArray(prepared.questions) && prepared.questions.length) {
        const ctx = { questions: prepared.questions, replace: Boolean(prepared.replace) };
        const grilled = prepared.kind === 'docs'
          ? Plan.startGrillDocs(updated, orchestra.docsFor(updated.project.folder), undefined, ctx)
          : Plan.startGrill(updated, undefined, ctx);
        if (grilled !== updated) updated = grilled;
      }
      if (prepared.job) { updated = Plan.createTask(updated, prepared.job); if (prepared.job.source === 'chat') updated.tasks.at(-1).source = 'chat'; if (prepared.job.goldModel) updated.tasks.at(-1).goldModel = prepared.job.goldModel; if (phase2) Object.assign(updated.tasks.at(-1), { executionVersion: 2, id: 'task-' + crypto.randomUUID() }); }
    } catch (error) {
      return { ok: false, reason: error.message };
    }
    if (updated === project) {
      const snapshot = await orchestra.tick();
      return { ok: true, plan: current, version: version(current), snapshot, phase: prepared.phase || 'grill' };
    }
    const next = current.schema === 2 ? { ...current, projects: current.projects.map(item => item.project.id === project.project.id ? updated : item) } : updated;
    if (!Plan.validate(next)) return { ok: false, reason: 'The updated notebook is invalid.' };
    await save(planFile, next);
    const snapshot = await orchestra.tick();
    return { ok: true, plan: next, version: version(next), snapshot, phase: prepared.phase || (prepared.job ? 'plan' : 'grill') };
  }
  if (initialSkillsSync) initialSkillsSync.catch(() => {});
  const initialModelsSync = syncAccounts().then(() => refreshModels ? providerModels.refresh(true) : undefined).catch(() => {});
  const staticFiles = { '/supervisor-ui.js': ['supervisor-ui.js', 'text/javascript'], '/supervisor.css': ['supervisor.css', 'text/css'], '/harbor-ui.js': ['harbor-ui.js', 'text/javascript'], '/harbor.css': ['harbor.css', 'text/css'], '/update-ui.js': ['update-ui.js', 'text/javascript'], '/layout-ui.js': ['layout-ui.js', 'text/javascript'], '/model-policy-ui.js': ['model-policy-ui.js', 'text/javascript'], '/notifications.js': ['notifications.js', 'text/javascript'], '/quota.js': ['quota.js', 'text/javascript'], '/home-ui.js': ['home-ui.js', 'text/javascript'], '/window-ui.js': ['window-ui.js', 'text/javascript'], '/': ['index.html', 'text/html'], '/index.html': ['index.html', 'text/html'], '/orchestrator.html': ['orchestrator.html', 'text/html'], '/orch.js': ['orch.js', 'text/javascript'], '/app.js': ['app.js', 'text/javascript'], '/plan.js': ['plan.js', 'text/javascript'], '/styles.css': ['styles.css', 'text/css'], '/models.js': ['models.js', 'text/javascript'], '/models.json': ['models.json', 'application/json'], '/engine/attempts.js': ['engine/attempts.js', 'text/javascript'], '/engine/sentinel.js': ['engine/sentinel.js', 'text/javascript'] };
  staticFiles['/project-skills-ui.js'] = ['project-skills-ui.js', 'text/javascript'];
  staticFiles['/browser-settings-ui.js'] = ['browser-settings-ui.js', 'text/javascript'];
  for (const name of ['port-dusk', 'vessel', 'vessel-pirate', 'captains-caribbean']) staticFiles['/assets/harbor/' + name + '.png'] = ['assets/harbor/' + name + '.png', 'image/png'];
  staticFiles['/assets/fonts/cinzel-variable.ttf'] = ['assets/fonts/cinzel-variable.ttf', 'font/ttf'];
  staticFiles['/assets/fonts/im-fell-english-sc.ttf'] = ['assets/fonts/im-fell-english-sc.ttf', 'font/ttf'];
  const server = http.createServer(async (request, response) => {
    const json = (status, value) => {
      response.writeHead(status, { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify(value));
    };
    try {
      const expectedHost = `127.0.0.1:${request.socket.localPort}`;
      if (request.headers.host !== expectedHost || (request.headers.origin && request.headers.origin !== `http://${expectedHost}`)) throw new RequestError(403, 'Open the interface from its local address.');
      const loc = new URL(request.url, `http://${expectedHost}`);
      const pathname = loc.pathname;
      if (updates.busy && pathname.startsWith('/api/') && !['/api/app/updates', '/api/app/heartbeat', '/api/app'].includes(pathname)) throw new RequestError(503, 'Planwharf is preparing an update. Task state is being preserved.');
      if (request.method === 'GET' && pathname === '/api/app/updates') return json(200, { ...updates.snapshot(), activeTasks: await updateActiveCount() });
      if (request.method === 'POST' && pathname === '/api/app/updates/check') {
        const input = await body(request);
        if (!input || Object.keys(input).length) throw new RequestError(400, 'Invalid update check.');
        return json(200, { ...await updates.check(), activeTasks: await updateActiveCount() });
      }
      if (request.method === 'POST' && pathname === '/api/app/updates/install') {
        const input = await body(request);
        if (!input || input.confirmRunning !== undefined && typeof input.confirmRunning !== 'boolean' || Object.keys(input).some(key => key !== 'confirmRunning')) throw new RequestError(400, 'Invalid update request.');
        const result = await enqueue(() => updates.install(input.confirmRunning === true));
        return json(result.requiresConfirmation ? 409 : 200, result);
      }
      if (pathname === '/api/orchestrator' || request.method === 'POST' && !/\/(?:stop|abort)(?:\/|$)/.test(pathname) && /^\/api\/(?:orchestrator|supervisor|models|catalog)\//.test(pathname)) {
        await initialModelsSync;
        await syncAccounts();
      }
      // Wait for the fresh startup catalog before reporting or executing model work.
      // Local shutdown and status polling remain available while discovery runs.
      if (pathname === '/api/orchestrator' || request.method === 'POST' && ['/api/orchestrator/propose', '/api/orchestrator/approve', '/api/orchestrator/resume', '/api/orchestrator/start', '/api/orchestrator/ask', '/api/orchestrator/lead', '/api/orchestrator/research/approve', '/api/orchestrator/research/resume'].includes(pathname)) await initialModelsSync;
      // Model discovery is explicit or startup-only; status polling never spawns provider CLIs.
      if (refreshModels && request.method === 'POST' && pathname === '/api/orchestrator/propose') await providerModels.refresh();
      if (skillsEnabled && typeof spawnWorker === 'function' && (pathname === '/api/orchestrator' && loc.searchParams.get('readOnly') !== '1' || request.method === 'POST' && ['/api/orchestrator/approve', '/api/orchestrator/resume', '/api/orchestrator/start', '/api/orchestrator/mode', '/api/orchestrator/research/resume', '/api/orchestrator/research/approve'].includes(pathname))) {
        try {
          const input = request.method === 'POST' ? await body(request) : null;
          // Global scheduling may contain legacy projects; explicit catalog launches
          // never wait for or inject the legacy downloadable instruction pipeline.
          if (input?.projectId) await readySkills(input.projectId);
          // Global ticks isolate projects. A cold legacy cache is reported by its
          // own launch wrapper and cannot block unrelated catalog-enabled work.
        } catch (error) { throw new RequestError(error.status || 503, error.message); }
      }
      if (request.method === 'GET' && pathname === '/' && (loc.searchParams.get('watch') === '1' || loc.searchParams.get('view') === 'watch')) {
        response.writeHead(302, { Location: '/#orchestrator' });
        response.end();
        return;
      }
      if (request.method === 'GET' && pathname === '/orchestrator.html') {
        response.writeHead(302, { Location: '/#orchestrator' });
        response.end();
        return;
      }
      if (request.method === 'GET' && pathname === '/api/plan') {
        const plan = await readPlan(planFile);
        return json(200, { plan, version: version(plan) });
      }
      if (request.method === 'GET' && pathname === '/api/app') return json(200, { planFile: path.resolve(planFile), attemptsDir: path.resolve(storeDir), pid: process.pid, views: appWindows ? appWindows.status() : {} });
      if (request.method === 'POST' && pathname === '/api/app/heartbeat') {
        const input = await body(request);
        if (!['planwharf', 'orchestrator'].includes(input.view) || typeof input.id !== 'string' || input.id.length > 100) throw new RequestError(400, 'Invalid view.');
        return json(200, appWindows ? appWindows.heartbeat(input) : { views: {}, focus: false });
      }
      if (request.method === 'POST' && pathname === '/api/app/quit') {
        if (!quitApp) throw new RequestError(409, 'Quit this server with Ctrl+C.');
        json(200, { ok: true });
        setImmediate(quitApp);
        return;
      }
      if (request.method === 'GET' && pathname === '/api/project-export') {
        const projectId = loc.searchParams.get('projectId');
        const notebook = await readPlan(planFile);
        const project = (notebook.schema === 2 ? notebook.projects : [notebook]).find(item => item.project.id === projectId);
        if (!project) throw new RequestError(404, 'Project not found.');
        const snapshot = await orchestra.snapshot(projectId);
        const bundle = exportProject(project, orchestra.executionView(projectId), { mode: snapshot.mode, orchestratorId: snapshot.orchestratorId,
          providerPolicies: Object.fromEntries(snapshot.providers.map(provider => [provider.id, provider.policy])), modelPolicies: Object.fromEntries(snapshot.models.map(model => [model.id, model.policy])), modelRoles: Object.fromEntries(snapshot.models.map(model => [model.id, model.roles])), enabled: Object.fromEntries(snapshot.models.map(model => [model.id, model.enabled])), providerEnabled: Object.fromEntries(snapshot.providers.map(provider => [provider.id, provider.enabled])) });
        response.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Disposition': 'attachment; filename="planwharf-project.json"', 'Cache-Control': 'no-store' });
        response.end(JSON.stringify(bundle, null, 2) + '\n'); return;
      }
      if (request.method === 'POST' && ['/api/project-home', '/api/notes'].includes(pathname)) {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON.');
        const input = await body(request, pathname === '/api/project-home' ? 1024 * 1024 : 65536);
        if (pathname === '/api/project-home') {
          const notebook = await readPlan(planFile);
          const project = (notebook.schema === 2 ? notebook.projects : [notebook]).find(p => p.project.id === input.projectId);
          if (!project) throw new RequestError(404, 'Project not found.');
          if (input.baseline != null && (!Array.isArray(input.baseline.revisionIds) || !input.baseline.revisionIds.every(Number.isSafeInteger) || (input.baseline.git?.head && !/^[a-f0-9]{40,64}$/.test(input.baseline.git.head)))) throw new RequestError(400, 'Invalid visit baseline.');
          // Git reads must not block notebook edits in the persistence queue.
          const home = await projectHome(project, await orchestra.snapshot(input.projectId), input.baseline, { planFile });
          if (input.visit === true) await enqueue(async () => {
            const latest = await readPlan(planFile);
            const target = (latest.schema === 2 ? latest.projects : [latest]).find(p => p.project.id === input.projectId);
            if (!target) throw new RequestError(404, 'Project not found.');
            target.home = { ...(target.home || { notes: [] }), lastVisit: home.current };
            await save(planFile, latest);
          });
          return json(200, home);
        }
        return json(200, await enqueue(async () => {
          const notebook = await readPlan(planFile);
          const project = (notebook.schema === 2 ? notebook.projects : [notebook]).find(p => p.project.id === input.projectId);
          if (!project) throw new RequestError(404, 'Project not found.');
          if (pathname === '/api/notes') {
            const home = project.home || { notes: [] };
            const notes = [...home.notes];
            if (input.action === 'add') {
              if (typeof input.text !== 'string' || !input.text.trim() || input.text.length > 10000 || notes.length >= 1000) throw new RequestError(400, 'Enter a note of up to 10,000 characters.');
              notes.push({ id: crypto.randomUUID(), text: input.text.trim(), createdAt: new Date().toISOString(), pinned: false, aiRelevant: input.aiRelevant === true });
            } else {
              const index = notes.findIndex(n => n.id === input.id);
              if (index < 0) throw new RequestError(404, 'Note not found.');
              if (input.action === 'delete') notes.splice(index, 1);
              else if (input.action === 'pin' && typeof input.pinned === 'boolean') notes[index] = { ...notes[index], pinned: input.pinned };
              else if (input.action === 'relevance' && typeof input.aiRelevant === 'boolean') notes[index] = { ...notes[index], aiRelevant: input.aiRelevant };
              else throw new RequestError(400, 'Invalid note action.');
            }
            project.home = { ...home, notes };
            await save(planFile, notebook);
            return { notes };
          }
        }));
      }
      if (request.method === 'POST' && pathname === '/api/planwharf/window') {
        const url = `http://127.0.0.1:${request.socket.localPort}/`;
        return json(200, { ok: true, url, opened: typeof openPlanWindow === 'function' ? Boolean(openPlanWindow(url)) : false });
      }
      if (request.method === 'GET' && pathname === '/api/settings/browser') return json(200, { selected: readBrowserPreference(planFile), browsers: detectBrowsers() });
      if (request.method === 'POST' && pathname === '/api/settings/browser') {
        const input = await body(request);
        let selected;
        try { selected = validateBrowser(input); } catch (error) { throw new RequestError(400, error.message); }
        await enqueue(() => writePlan(`${planFile}.browser.json`, selected));
        return json(200, { selected, browsers: detectBrowsers() });
      }
      if (request.method === 'GET' && pathname === '/api/settings') return json(200, await readSettings());
      if (request.method === 'POST' && pathname === '/api/settings') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON.');
        const input = await body(request);
        if (!input || Object.keys(input).some(key => key !== 'projectsRoot')) throw new RequestError(400, 'Invalid settings.');
        return json(200, await enqueue(() => setProjectsRoot(input.projectsRoot)));
      }
      if (request.method === 'GET' && pathname === '/api/attempts') {
        return json(200, { attempts: attempts.getAttempts() });
      }
      if (request.method === 'POST' && pathname === '/api/focus') {
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || Object.keys(input).some(key => key !== 'projectId')) throw new RequestError(400, 'Explicit projectId required.');
        const result = await enqueue(() => orchestra.setFocus(input.projectId));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result);
      }
      if (request.method === 'GET' && pathname === '/api/execution') {
        const projectId = loc.searchParams.get('projectId');
        const notebook = await readPlan(planFile);
        if (!(notebook.schema === 2 ? notebook.projects : [notebook]).some(item => item.project.id === projectId)) throw new RequestError(404, 'Project not found.');
        await enqueue(() => orchestra.snapshot(projectId));
        return json(200, orchestra.executionView(projectId));
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/propose') {
        const input = await body(request, 1024 * 1024);
        if (!input || Object.keys(input).some(key => !['projectId', 'title', 'goal', 'kind', 'subTasks'].includes(key)) || typeof input.projectId !== 'string'
          || typeof input.title !== 'string' || !input.title.trim() || typeof input.goal !== 'string' || !input.goal.trim()
          || !['implementation', 'audit', 'research'].includes(input.kind || 'implementation') || !Array.isArray(input.subTasks) || !input.subTasks.length || input.subTasks.length > 50) throw new RequestError(400, 'Invalid MegaTask proposal.');
        const result = await enqueue(async () => {
          const current = await readPlan(planFile);
          const item = (current.schema === 2 ? current.projects : [current]).find(item => item.project.id === input.projectId);
          if (!item) throw new RequestError(404, 'Project not found.');
          if (item.tasks.some(task => task.status === 'running')) throw new RequestError(409, 'Finish or cancel the current task before proposing another MegaTask.');
          const roster = (await orchestra.snapshot()).models;
          for (const [index, sub] of input.subTasks.entries()) {
            if (!sub || Object.keys(sub).some(key => !['title', 'detail', 'assignedModelId', 'dependsOn', 'type'].includes(key)) || typeof sub.title !== 'string' || !sub.title.trim()
              || typeof sub.detail !== 'string' || !roster.some(model => model.id === sub.assignedModelId && model.enabled && model.available !== false)
              || sub.dependsOn !== undefined && (!Array.isArray(sub.dependsOn) || !sub.dependsOn.every(parent => Number.isInteger(parent) && parent >= 0 && parent < index))
              || sub.type !== undefined && !['IMPLEMENTATION', 'RESEARCH', 'AUDIT'].includes(sub.type)) throw new RequestError(400, 'Invalid SubTask or dependency.');
          }
          const updated = Plan.createTask(item, { title: input.title, detail: input.goal, revision_id: item.revisions.at(-1).id });
          Object.assign(updated.tasks.at(-1), { executionVersion: 2, kind: input.kind || 'implementation', proposedSubTasks: input.subTasks });
          const next = current.schema === 2 ? { ...current, projects: current.projects.map(project => project === item ? updated : project) } : updated;
          await save(planFile, next);
          return { ok: true, snapshot: await orchestra.tick() };
        });
        return json(201, result);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/research/propose') {
        const input = await body(request);
        const result = await enqueue(() => orchestra.dispatchResearch({ ...input, type: 'dispatch_research' }));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(201, result);
      }
      if (request.method === 'GET' && pathname === '/api/repository') {
        const result = await orchestra.inspectRepository(loc.searchParams.get('projectId'), { force: loc.searchParams.get('refresh') === '1' });
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result);
      }
      if (request.method === 'GET' && pathname === '/api/orchestrator') {
        const snapshot = loc.searchParams.get('readOnly') === '1' ? await orchestra.snapshot() : await enqueue(() => orchestra.tick());
        snapshot.skills = skillsSnapshot();
        snapshot.providerModels = providerModels.snapshot();
        return json(200, snapshot);
      }
      if (request.method === 'GET' && pathname === '/api/orchestrator/activity-sessions') {
        if ([...loc.searchParams.keys()].some(key => !['projectId', 'offset', 'limit'].includes(key))) throw new RequestError(400, 'Invalid activity session page fields.');
        const projectId = loc.searchParams.get('projectId'), plan = await readPlan(planFile);
        if (!(plan.schema === 2 ? plan.projects : [plan]).some(item => item.project.id === projectId)) throw new RequestError(400, 'Select an existing project.');
        const offset = Number(loc.searchParams.get('offset') ?? '0'), limit = Number(loc.searchParams.get('limit') ?? '24');
        if (!Number.isSafeInteger(offset) || offset < 0 || !Number.isSafeInteger(limit) || limit < 1 || limit > 24) throw new RequestError(400, 'Invalid activity session page.');
        return json(200, await orchestra.activitySessionPage(projectId, offset, limit));
      }
      if (pathname.startsWith('/api/project-skills')) {
        if (!projectSkills) throw new RequestError(503, 'Project Skills catalog is unavailable. Preserve its registry for recovery.');
        const action = pathname.slice('/api/project-skills'.length);
        const read = request.method === 'GET' && ['', '/content', '/drafts', '/procedures'].includes(action);
        const write = request.method === 'POST' && ['/settings', '/discover', '/approve', '/install', '/drafts/create', '/drafts/create-assisted', '/drafts/edit', '/drafts/approve', '/procedures/prepare', '/procedures/accept'].includes(action);
        if (!read && !write) throw new RequestError(404, 'Resource unavailable.');
        const input = read ? Object.fromEntries(loc.searchParams) : await body(request);
        const allowed = action === '/procedures/prepare' ? ['projectId', 'attemptId', 'skillId', 'referencePaths'] : action === '/procedures/accept' ? ['projectId', 'candidateId', 'expectedHash', 'approved', 'reviewNote'] : action === '/drafts/create-assisted' ? ['projectId', 'proposalId', 'name', 'description', 'procedure', 'requested'] : action === '/drafts/create' ? ['projectId', 'name', 'description', 'procedure', 'references', 'requested'] : action === '/drafts/edit' ? ['projectId', 'id', 'revision', 'content'] : action === '/drafts/approve' ? ['projectId', 'id', 'revision', 'approved'] : action === '/content' ? ['projectId', 'id'] : action === '/settings' ? ['projectId', 'settings'] : action === '/approve' ? ['projectId', 'id', 'enabled', 'contentHash'] : action === '/install' ? ['projectId', 'id', 'approved'] : action === '' ? ['projectId', 'query', 'source'] : ['projectId'];
        if (!input || typeof input.projectId !== 'string' || Object.keys(input).some(key => !allowed.includes(key))) throw new RequestError(400, 'Select an existing project and valid catalog fields.');
        const result = await enqueueCatalog(async () => {
          const plan = await readPlan(planFile);
          const project = (plan.schema === 2 ? plan.projects : [plan]).find(item => item.project.id === input.projectId);
          if (!project) throw new RequestError(404, 'Project not found.');
          try {
            if (action.startsWith('/procedures')) {
              if (!procedureEvidence) throw new RequestError(503, 'Procedure review evidence is unavailable. Preserve its store for recovery.');
              if (action === '/procedures/prepare') return procedureEvidence.prepare(input);
              if (action === '/procedures/accept') {
                const acceptance = procedureEvidence.accept({ ...input, principal: 'user' });
                return { acceptance, ...procedureEvidence.suggest({ projectId: input.projectId }) };
              }
              return procedureEvidence.snapshot(input.projectId);
            }
            if (action.startsWith('/drafts')) {
              if (!skillGeneration) throw new RequestError(503, 'Project Skill drafts are unavailable. Preserve their store for recovery.');
              if (action === '/drafts') return skillGeneration.snapshot(input.projectId);
              if (action === '/drafts/edit') return skillGeneration.edit(input.projectId, input.id, input.revision, input.content);
              if (!project.project.folder) throw new RequestError(400, 'Set the project folder before generating a Skill.');
              if (action === '/drafts/create') return skillGeneration.create({ ...input, folder: project.project.folder });
              if (action === '/drafts/create-assisted') return skillGeneration.createAssisted({ ...input, folder: project.project.folder });
              return skillGeneration.approve({ ...input, folder: project.project.folder });
            }
            if (action === '/content') return projectSkills.inspect(input.projectId, input.id);
            if (action === '/settings') return projectSkills.configure(input.projectId, input.settings);
            if (action === '/approve') return projectSkills.approve(input.projectId, input.id, input.enabled, input.contentHash);
            if (action === '/install') return await projectSkills.install(input.projectId, input.id, input.approved);
            if (action === '/discover') {
              if (!project.project.folder) throw new RequestError(400, 'Set the project folder before discovering local Skills.');
              return projectSkills.discoverLocal(input.projectId, project.project.folder);
            }
            return { ...projectSkills.reconcile(input.projectId), ...projectSkills.snapshot(input.projectId, { query: input.query, source: input.source }), executionIntegration: true };
          } catch (error) { if (error.status) throw error; throw new RequestError(400, error.message); }
        });
        return json(200, result);
      }
      if (request.method === 'GET' && pathname === '/api/skills') {
        return json(200, skillsSnapshot());
      }
      if (request.method === 'POST' && pathname === '/api/skills') {
        initialSkillsSync = skillsCache.ensure();
        await initialSkillsSync;
        return json(200, skillsSnapshot());
      }
      if (request.method === 'POST' && pathname === '/api/skills/settings') {
        const input = await body(request);
        if (!input || typeof input.enabled !== 'boolean' || Object.keys(input).some(key => key !== 'enabled')) throw new RequestError(400, 'Invalid Matt skills setting.');
        await enqueue(() => writePlan(skillsPolicyFile, { enabled: input.enabled }));
        skillsEnabled = input.enabled;
        return json(200, skillsSnapshot());
      }
      if (request.method === 'POST' && pathname === '/api/models/refresh') {
        await providerModels.refresh(true);
        const snapshot = await orchestra.snapshot();
        return json(200, { ...snapshot, skills: skillsSnapshot(), providerModels: providerModels.snapshot() });
      }
      if (request.method === 'GET' && pathname === '/api/models/status') return json(200, providerModels.snapshot());
      if (request.method === 'POST' && pathname === '/api/catalog/settings') {
        const input = await body(request);
        if (!input || typeof input.providerId !== 'string' || Object.keys(input).some(key => !['providerId', 'enabled', 'showAll', 'allModelsEnabled'].includes(key)) || ['enabled', 'showAll', 'allModelsEnabled'].some(key => input[key] !== undefined && typeof input[key] !== 'boolean')) throw new RequestError(400, 'Invalid provider catalog settings.');
        const result = await enqueue(() => orchestra.setCatalogSettings(input));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'GET' && pathname === '/models.json') { await initialModelsSync; return json(200, models); }
      if (request.method === 'POST' && pathname === '/api/orchestrator/lead') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || Object.keys(input).some(key => key !== 'id') || (input.id != null && typeof input.id !== 'string')) throw new RequestError(400, 'Invalid data.');
        const result = await orchestra.setOrchestrator(input.id || '');
        if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && ['/api/supervisor/settings', '/api/supervisor/ignore', '/api/supervisor/ask'].includes(pathname)) {
        const input = await body(request, 16000, 'Supervisor request too large.');
        const action = pathname.split('/').at(-1);
        const allowed = action === 'settings' ? ['name', 'modelId', 'autoReview'] : action === 'ignore' ? ['projectId', 'alertId'] : ['projectId', 'question'];
        if (!input || typeof input !== 'object' || Array.isArray(input) || Object.keys(input).some(key => !allowed.includes(key))
          || action !== 'settings' && typeof input.projectId !== 'string'
          || input.name !== undefined && typeof input.name !== 'string'
          || input.modelId !== undefined && typeof input.modelId !== 'string'
          || input.autoReview !== undefined && typeof input.autoReview !== 'boolean'
          || action === 'ignore' && typeof input.alertId !== 'string') throw new RequestError(400, 'Invalid Supervisor request.');
        try { return json(200, await orchestra.supervisorRequest(action, input)); }
        catch (error) { throw new RequestError(400, error.message); }
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/ask/cancel') {
        return json(200, await orchestra.abortAsk());
      }
      if (request.method === 'GET' && pathname === '/api/orchestrator/research/report') {
        const result = orchestra.researchReport(loc.searchParams.get('id'));
        if (!result.ok) throw new RequestError(404, result.reason);
        return json(200, result);
      }
      if (request.method === 'POST' && ['/api/orchestrator/research/approve', '/api/orchestrator/research/stop', '/api/orchestrator/research/resume'].includes(pathname)) {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON.');
        const input = await body(request);
        if (!input || typeof input.id !== 'string' || Object.keys(input).some(key => !['id', 'projectId', 'megaTaskId', 'checkpointId', 'confirmChanges', 'assignments'].includes(key))) throw new RequestError(400, 'Invalid research job ID.');
        const result = await enqueue(() => pathname.endsWith('/approve') ? orchestra.approveResearch(input.id, input) : pathname.endsWith('/stop') ? orchestra.stopResearch(input.id, input) : orchestra.resumeResearch(input.id, input));
        if (result.requiresConfirmation) return json(409, result);
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/ask') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request, 1024 * 1024, 'Chat request too large (maximum 1 MiB).');
        if (!input || typeof input.question !== 'string' || input.chatSessionId !== undefined && (typeof input.chatSessionId !== 'string' || !/^[\w-]{1,100}$/.test(input.chatSessionId)) || Object.keys(input).some(key => !['question', 'projectId', 'chatSessionId', 'requestMode', 'grillKind'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const asked = await orchestra.ask(input.question, typeof input.projectId === 'string' ? input.projectId : undefined, input.chatSessionId, input.requestMode, input.grillKind);
        if (!asked.ok) throw new RequestError(400, asked.reason || 'Invalid data.');
        return json(200, asked);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/mode') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || (input.mode !== 'auto' && input.mode !== 'manual') || Object.keys(input).some(key => key !== 'mode')) throw new RequestError(400, 'Invalid data.');
        return json(200, await orchestra.setMode(input.mode));
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/grill') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (input && typeof input.propose === 'boolean' && Object.keys(input).every(key => ['propose', 'kind'].includes(key))) {
          if (input.kind != null && input.kind !== 'me' && input.kind !== 'docs') throw new RequestError(400, 'Invalid data.');
          return json(200, await orchestra.setGrillPropose(input.propose, input.kind));
        }
        if (input && typeof input.projectId === 'string' && input.skip === true && Object.keys(input).every(key => ['projectId', 'skip', 'version'].includes(key))) {
          const skipped = await enqueue(async () => {
            const current = await readPlan(planFile);
            if (input.version && input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
            const result = await executeSkip(current, input.projectId);
            if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
            return { plan: result.plan, version: result.version, snapshot: result.snapshot };
          });
          return json(200, skipped);
        }
        throw new RequestError(400, 'Invalid data.');
      }
      if (request.method === 'POST' && pathname === '/api/providers/connections') {
        const input = await body(request);
        let provider;
        try { provider = require('./engine/provider-connections.cjs').connection(input); }
        catch (error) { throw new RequestError(400, error.message); }
        const result = await enqueue(async () => {
          if (providers.some(item => item.id === provider.id)) throw new RequestError(400, 'Provider ID already exists. Use a new connection ID.');
          const extra = loadCatalog({ schema: 3, providers: [provider] });
          if (extra.models.some(model => models.some(existing => existing.id === model.id))) throw new RequestError(400, 'Model ID already exists.');
          await fs.mkdir(storeDir, { recursive: true });
          await writePlan(connectionFile, [...connections, input]);
          connections.push(input); providers.push(...extra.providers); models.push(...extra.models);
          return orchestra.snapshot();
        });
        return json(200, result);
      }
      if (request.method === 'POST' && pathname === '/api/providers/policy') {
        const input = await body(request);
        if (!input || typeof input.id !== 'string' || Object.keys(input).some(key => !['id', 'policy'].includes(key))) throw new RequestError(400, 'Invalid provider policy settings.');
        const result = await enqueue(() => orchestra.setProviderPolicy(input.id, input.policy));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/models/policy') {
        const input = await body(request);
        if (!input || typeof input.id !== 'string' || Object.keys(input).some(key => !['id', 'policy'].includes(key))) throw new RequestError(400, 'Invalid model policy settings.');
        const result = await enqueue(() => orchestra.setModelPolicy(input.id, input.policy));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/models/roles/bulk') {
        const input = await body(request);
        if (!input || Object.keys(input).some(key => !['role', 'enabled'].includes(key))) throw new RequestError(400, 'Invalid bulk role settings.');
        const result = await enqueue(() => orchestra.setRoleForAll(input.role, input.enabled));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/models/gold') {
        const input = await body(request);
        if (!input || !Object.hasOwn(input, 'id') || Object.keys(input).some(key => key !== 'id')) throw new RequestError(400, 'Invalid Gold model settings.');
        const result = await enqueue(() => orchestra.setGoldModel(input.id));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/models/roles') {
        const input = await body(request);
        if (!input || typeof input.id !== 'string' || Object.keys(input).some(key => !['id', 'roles'].includes(key))) throw new RequestError(400, 'Invalid model role settings.');
        const result = await enqueue(() => orchestra.setModelRoles(input.id, input.roles));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/model') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.id !== 'string' || typeof input.enabled !== 'boolean' || Object.keys(input).some(key => !['id', 'enabled'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const result = await orchestra.setModelEnabled(input.id, input.enabled);
        if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/approve') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || Object.keys(input).some(key => !['projectId', 'megaTaskId'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const result = await enqueue(() => orchestra.approve(input.projectId, input.megaTaskId));
        if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/resume') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || Object.keys(input).some(key => !['projectId', 'megaTaskId', 'checkpointId', 'confirmChanges', 'assignments'].includes(key)) || input.confirmChanges !== undefined && typeof input.confirmChanges !== 'boolean') throw new RequestError(400, 'Invalid data.');
        const result = await enqueue(() => orchestra.resume(input.projectId, input));
        if (result.requiresConfirmation) return json(409, result);
        if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/pause') {
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || typeof input.megaTaskId !== 'string' || Object.keys(input).some(key => !['projectId', 'megaTaskId'].includes(key))) throw new RequestError(400, 'Explicit projectId and megaTaskId required.');
        const result = await enqueue(() => orchestra.pauseAfterCurrent(input.projectId, input.megaTaskId));
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/worker/stop') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON.');
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || !Number.isSafeInteger(input.pid) || input.pid <= 0 || Object.keys(input).some(key => !['projectId', 'pid'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const result = await orchestra.stopWorker(input.projectId, input.pid);
        if (!result.ok) throw new RequestError(400, result.reason);
        return json(200, result);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/stop') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || Object.keys(input).some(key => !['projectId', 'megaTaskId'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const result = await enqueue(() => orchestra.stop(input.projectId, input.megaTaskId));
        if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
        return json(200, result.snapshot);
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/window') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (input && typeof input === 'object' && Object.keys(input).length) throw new RequestError(400, 'Invalid data.');
        const watchUrl = `http://127.0.0.1:${request.socket.localPort}/#orchestrator`;
        let opened = false;
        if (typeof openWatchWindow === 'function') {
          try {
            opened = Boolean(openWatchWindow(watchUrl));
          } catch (_) {
            opened = false;
          }
        }
        return json(200, { ok: true, url: watchUrl, opened });
      }
      if (request.method === 'POST' && pathname === '/api/orchestrator/start') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.version !== 'string' || typeof input.projectId !== 'string' || Object.keys(input).some(key => !['version', 'projectId', 'timing', 'deadlineAt'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const created = await enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          const result = await executeStart(current, input.projectId, { timing: input.timing, deadlineAt: input.deadlineAt });
          if (!result.ok) throw new RequestError(400, result.reason || 'Invalid data.');
          return result;
        });
        return json(created.phase === 'plan' ? 201 : 200, created);
      }
      if (request.method === 'POST' && pathname === '/api/attempts') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        const result = attempts.claim(input);
        return json(result.ok ? 201 : 409, { ...result, attempts: attempts.getAttempts() });
      }
      if (request.method === 'POST' && pathname === '/api/projects') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.version !== 'string' || input.project === undefined || (input.createFolder != null && typeof input.createFolder !== 'boolean') || Object.keys(input).some(key => !['version', 'project', 'createFolder'].includes(key))) throw new RequestError(400, 'Invalid data.');
        if (!input.project || typeof input.project !== 'object' || Array.isArray(input.project) || Object.keys(input.project).some(key => !['name', 'direction', 'folder', 'github'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const pending = enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          let next;
          try {
            next = Plan.createProject(current, input.project);
            if (!Plan.validate(next)) throw new Error('The updated notebook is invalid.');
          } catch (error) { throw new RequestError(400, error.message); }
          let createdFolder;
          try {
            if (input.createFolder) {
              if (input.project.folder && input.project.folder.trim()) throw new RequestError(400, 'Choose either a new folder or an existing folder.');
              const added = next.projects.at(-1);
              createdFolder = await createProjectFolder(added.project.name);
              added.project.folder = createdFolder;
            }
            await assertFolder(next.projects.at(-1).project.folder);
            await save(planFile, next);
          } catch (error) {
            // Only remove our own new directory if it is still empty.
            if (createdFolder) await fs.rmdir(createdFolder).catch(() => {});
            throw error;
          }
          return { plan: next, version: version(next) };
        });
        queue = pending.catch(() => {});
        return json(201, await pending);
      }
      if (request.method === 'POST' && pathname === '/api/remove-project') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.version !== 'string' || typeof input.projectId !== 'string' || Object.keys(input).some(key => !['version', 'projectId'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const pending = enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          let next;
          try {
            next = Plan.removeProject(current, { id: input.projectId });
            if (!Plan.validate(next)) throw new Error('The updated notebook is invalid.');
          } catch (error) { throw new RequestError(400, error.message); }
          for (const item of attempts.getAttempts()) {
            if (item.status === 'active' && item.projectId === input.projectId) attempts.closeAttempt({ attemptId: item.id, reason: 'removed' });
          }
          await save(planFile, next);
          return { plan: next, version: version(next) };
        });
        queue = pending.catch(() => {});
        return json(201, await pending);
      }
      if (request.method === 'POST' && pathname === '/api/remove-task') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.version !== 'string' || typeof input.projectId !== 'string' || !input.task || typeof input.task !== 'object' || Array.isArray(input.task) || Object.keys(input.task).some(key => key !== 'id') || Object.keys(input).some(key => !['version', 'projectId', 'task'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const pending = enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          const project = current.schema === 2 ? current.projects.find(item => item.project.id === input.projectId) : current;
          if (!project || (current.schema === 1 && input.projectId !== project.project.id)) throw new RequestError(400, 'Project not found.');
          let updated;
          try { updated = Plan.removeTask(project, input.task); }
          catch (error) { throw new RequestError(400, error.message); }
          const next = current.schema === 2 ? { ...current, projects: current.projects.map(item => item.project.id === project.project.id ? updated : item) } : updated;
          if (!Plan.validate(next)) throw new RequestError(400, 'The updated notebook is invalid.');
          for (const item of attempts.getAttempts()) {
            if (item.status === 'active' && item.taskId === input.task.id) attempts.closeAttempt({ attemptId: item.id, reason: 'removed' });
          }
          await save(planFile, next);
          return { plan: next, version: version(next) };
        });
        queue = pending.catch(() => {});
        return json(201, await pending);
      }
      if (request.method === 'POST' && pathname === '/api/place') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.version !== 'string' || input.project === undefined || Object.keys(input).some(key => !['version', 'project'].includes(key))) throw new RequestError(400, 'Invalid data.');
        if (!input.project || typeof input.project !== 'object' || Array.isArray(input.project) || Object.keys(input.project).some(key => !['id', 'folder', 'github'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const pending = enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          let next;
          try {
            next = Plan.setPlace(current, input.project);
            if (!Plan.validate(next)) throw new Error('The updated notebook is invalid.');
          } catch (error) { throw new RequestError(400, error.message); }
          const updated = next.schema === 2 ? next.projects.find(item => item.project.id === input.project.id) : next;
          await assertFolder(updated.project.folder);
          await save(planFile, next);
          return { plan: next, version: version(next) };
        });
        queue = pending.catch(() => {});
        return json(201, await pending);
      }
      if (request.method === 'POST' && pathname === '/api/open') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.projectId !== 'string' || !['folder', 'github'].includes(input.target) || Object.keys(input).some(key => !['projectId', 'target'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const current = await readPlan(planFile);
        const project = current.schema === 2 ? current.projects.find(item => item.project.id === input.projectId) : (current.project.id === input.projectId ? current : null);
        if (!project) throw new RequestError(400, 'Project not found.');
        if (input.target === 'folder') {
          if (!project.project.folder) throw new RequestError(400, 'No folder for this project.');
          await assertFolder(project.project.folder);
          openFolder(project.project.folder);
        } else {
          if (!project.project.github) throw new RequestError(400, 'No GitHub for this project.');
          if (!Plan.isGithubUrl(project.project.github)) throw new RequestError(400, 'Repository must be an HTTPS GitHub or Gitea owner/repository URL.');
          openUrl(project.project.github);
        }
        return json(200, { ok: true });
      }
      if (request.method === 'POST' && pathname === '/api/grill-start') {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        if (!input || typeof input.version !== 'string' || typeof input.projectId !== 'string' || (input.kind !== undefined && input.kind !== 'me' && input.kind !== 'docs') || Object.keys(input).some(key => !['version', 'projectId', 'kind'].includes(key))) throw new RequestError(400, 'Invalid data.');
        const pending = enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          const project = current.schema === 2 ? current.projects.find(item => item.project.id === input.projectId) : current;
          if (!project || (current.schema === 1 && input.projectId !== project.project.id)) throw new RequestError(400, 'Project not found.');
          let updated;
          try {
            if (input.kind === 'docs') {
              if (!project.project.folder) throw new Error('no folder');
              const docs = orchestra.docsFor(project.project.folder);
              updated = Plan.startGrillDocs(project, docs, undefined, {
                name: project.project.name,
                goal: project.revisions.at(-1).direction,
                folder: project.project.folder
              });
            } else {
              updated = Plan.startGrill(project, undefined, {
                name: project.project.name,
                goal: project.revisions.at(-1).direction,
                folder: project.project.folder
              });
            }
          }
          catch (error) { throw new RequestError(400, error.message); }
          if (updated === project) return { plan: current, version: version(current) };
          const next = current.schema === 2 ? { ...current, projects: current.projects.map(item => item.project.id === project.project.id ? updated : item) } : updated;
          if (!Plan.validate(next)) throw new RequestError(400, 'The updated notebook is invalid.');
          await save(planFile, next);
          return { plan: next, version: version(next) };
        });
        queue = pending.catch(() => {});
        return json(201, await pending);
      }
      if (request.method === 'POST' && ['/api/revisions', '/api/grill', '/api/import', '/api/tasks'].includes(pathname)) {
        if (request.headers['content-type'] !== 'application/json') throw new RequestError(415, 'Use JSON for the revision.');
        const input = await body(request);
        const field = { '/api/revisions': 'draft', '/api/grill': 'round', '/api/import': 'json', '/api/tasks': 'task' }[pathname];
        if (!input || typeof input.version !== 'string' || input[field] === undefined || Object.keys(input).some(key => !['version', 'projectId', field].includes(key))) throw new RequestError(400, 'Invalid data.');
        const keys = { draft: ['title', 'direction', 'state', 'closed', 'open'], round: ['title', 'answers'], task: ['title', 'detail', 'revision_id', 'timing', 'deadlineAt'] };
        if (field !== 'json' && (!input[field] || typeof input[field] !== 'object' || Array.isArray(input[field]) || Object.keys(input[field]).some(key => !keys[field].includes(key)))) throw new RequestError(400, 'Invalid data.');
        if (field === 'json' && typeof input.json !== 'string') throw new RequestError(400, 'Invalid import JSON.');
        // Serialize disk reads and writes: a second browser cannot replace history.
        const pending = enqueue(async () => {
          const current = await readPlan(planFile);
          if (input.version !== version(current)) throw new RequestError(409, 'The plan has changed. Reload the page before saving.');
          let next;
          try {
            const project = current.schema === 2 ? current.projects.find(p => p.project.id === input.projectId) : current;
            if (!project || (current.schema === 1 && input.projectId && input.projectId !== project.project.id)) throw new Error('Project not found.');
            const operations = { draft: Plan.appendRevision, round: Plan.grill, json: Plan.importRevision, task: Plan.createTask };
            const updated = operations[field](project, input[field]);
            if (field === 'task' && phase2) Object.assign(updated.tasks.at(-1), { executionVersion: 2, id: 'task-' + crypto.randomUUID() });
            next = current.schema === 2 ? { ...current, projects: current.projects.map(p => p.project.id === project.project.id ? updated : p) } : updated;
            if (!Plan.validate(next)) throw new Error('The updated notebook is invalid.');
          }
          catch (error) { throw new RequestError(400, error.message); }
          await save(planFile, next);
          if (field === 'task') { await orchestra.tick(); next = await readPlan(planFile); }
          return { plan: next, version: version(next) };
        });
        queue = pending.catch(() => {});
        return json(201, await pending);
      }
      if (request.method === 'GET' && staticFiles[pathname]) {
        const [file, type] = staticFiles[pathname];
        const content = await fs.readFile(path.join(__dirname, file));
        response.writeHead(200, { 'Content-Type': `${type}; charset=utf-8`, 'Cache-Control': 'no-store' });
        return response.end(content);
      }
      throw new RequestError(404, 'Resource unavailable.');
    } catch (error) {
      json(error.status || 500, { error: error.status ? error.message : 'Failed to read or save file. No revision was added.' });
    }
  });
  async function updateActiveCount() {
    const plan = await readPlan(planFile), state = await orchestra.snapshot();
    return (plan.schema === 2 ? plan.projects : [plan]).reduce((sum, project) => sum + project.tasks.filter(task => task.status === 'running').length, 0)
      + (state.researches || []).filter(row => ['watching', 'needs_approval'].includes(row.status)).length + (state.asking ? 1 : 0);
  }
  const updates = updateManager || require('./engine/app-updates.cjs').createUpdateManager({ root: __dirname,
    checkpoint: () => orchestra.checkpointForUpdate(), activeCount: updateActiveCount, restart: restartAfterUpdate });
  server.updates = updates;
  let closing;
  let monitorTimer, monitoring = false;
  server.on('listening', () => {
    monitorTimer = setInterval(async () => {
      if (closing || monitoring || updates.busy) return;
      monitoring = true;
      try {
        await initialModelsSync;
        await syncAccounts();
        await orchestra.refreshRepositories();
        const state = await orchestra.snapshot();
        if (state.projects.some(row => row.status === 'watching') || (state.researches || []).some(row => row.status === 'watching')) await enqueue(() => updates.busy ? orchestra.snapshot() : orchestra.tick());
      } catch (error) { console.error('Execution monitoring failed:', error.message); }
      finally { monitoring = false; }
    }, monitorIntervalMs);
    monitorTimer.unref();
  });
  server.shutdown = () => { clearInterval(monitorTimer); return closing || (closing = orchestra.shutdown()); };
  const close = server.close.bind(server);
  server.close = callback => { server.shutdown().then(() => close(callback), error => { console.error('Checkpoint shutdown failed:', error.message); close(callback); }); return server; };
  return server;
}
if (require.main === module) {
  const port = Number(process.env.PORT || 4173);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    console.error('PORT deve essere un numero da 1 a 65535.');
    process.exitCode = 1;
  } else {
    readPlan(path.join(__dirname, 'plan.json')).then(() => {
      const server = createServer({ syncSkills: true });
      server.on('error', error => { console.error(error.code === 'EADDRINUSE' ? `Port ${port} is occupied. Choose another PORT.` : `Server failed to start: ${error.message}`); process.exitCode = 1; });
      const shutdown = async () => { await server.shutdown(); server.close(() => process.exit(0)); };
      process.on('SIGINT', shutdown); process.on('SIGTERM', shutdown);
      server.listen(port, '127.0.0.1', () => console.log(`Planwharf zombie: http://127.0.0.1:${port}\nPiano: ${path.join(__dirname, 'plan.json')}\nCtrl+C per chiudere.`));
    }).catch(error => { console.error(`Cannot open plan.json: ${error.message}`); process.exitCode = 1; });
  }
}
module.exports = { createServer, readPlan, writePlan };
