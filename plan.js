(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.Planwharf = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';
  const clone = value => JSON.parse(JSON.stringify(value));
  const fields = ['direction', 'state', 'closed', 'open'];
  function example() {
    return {
      schema: 1,
      project: { id: 'biblioteca', name: 'Neighborhood library' },
      revisions: [
        { id: 1, mode: 'manual', title: 'Starting point', createdAt: '2026-10-06T09:00:00+02:00', direction: 'Open a small neighborhood library accessible to everyone.', state: 'Two possible locations identified. Initial collection: 120 donated books.', closed: ['Free borrowing for residents.', 'Start with a single location.'], open: ['Which location should we choose?', 'Who covers the first opening shift?'] },
        { id: 2, mode: 'manual', title: 'One location, a first step', createdAt: '2026-10-07T10:30:00+02:00', direction: 'Open the library on Harbor Street for a one-month trial.', state: 'Harbor Street selected. 120 books cataloged. The trial schedule is being prepared.', closed: ['Free borrowing for residents.', 'Start with a single location.', 'Use the Harbor Street location.'], open: ['Who covers the first opening shift?', 'Should we confirm Saturday morning opening?'] }
      ],
      tasks: [{ id: 'calendario', projectId: 'biblioteca', revision_id: 2, title: 'Prepare the trial-month schedule', detail: 'Define four shifts and collect volunteer availability.', status: 'running' }]
    };
  }
  function isFolderPath(value) {
    if (typeof value !== 'string' || !value.length || value.length > 500 || value.includes('\0')) return false;
    if (/^[A-Za-z]:[\\/]/.test(value) || value.startsWith('\\\\')) return true;
    return value.startsWith('/') && value.length > 1;
  }
  function isGithubUrl(value) {
    return typeof value === 'string' && /^https:\/\/[A-Za-z0-9.-]+(?::[0-9]{1,5})?(?:\/[A-Za-z0-9_.-]+)*\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(value) && !value.split('/').some(part => part === '.' || part === '..');
  }
  function normalizeFolder(value) {
    let folder = value.trim().replace(/^["']+|["']+$/g, '').trim();
    folder = folder.replace(/^file:\/\//i, '');
    if (/^\/[A-Za-z]:/.test(folder)) folder = folder.slice(1);
    return folder;
  }
  function normalizeGithub(value) {
    let github = value.trim();
    github = github.replace(/^git@github\.com:/i, 'https://github.com/');
    github = github.replace(/^https?:\/\/(www\.)?github\.com\//i, 'https://github.com/');
    const match = github.match(/^https:\/\/github\.com\/([A-Za-z0-9_.-]+)\/([A-Za-z0-9_.-]+?)(?:\.git)?(?:\/.*)?$/i);
    return match ? `https://github.com/${match[1]}/${match[2].replace(/\.git$/i, '')}` : github.replace(/\/+$/, '');
  }
  function placeFrom(input) {
    const folder = typeof input?.folder === 'string' ? normalizeFolder(input.folder) : '';
    const github = typeof input?.github === 'string' ? normalizeGithub(input.github) : '';
    if (folder && !isFolderPath(folder)) throw new Error('Folder must be an absolute path on this PC.');
    if (github && !isGithubUrl(github)) throw new Error('Repository must be an HTTPS GitHub or Gitea owner/repository URL.');
    return { folder, github };
  }
  function projectRecord(id, name, place) {
    const project = { id, name };
    if (place.folder) project.folder = place.folder;
    if (place.github) project.github = place.github;
    return project;
  }
  function validateProject(data) {
    if (data?.home !== undefined) {
      const h = data.home;
      if (!h || !Array.isArray(h.notes) || h.notes.length > 1000 || !h.notes.every(n => n && typeof n.id === 'string' && n.id && typeof n.text === 'string' && n.text.trim() && n.text.length <= 10000 && typeof n.createdAt === 'string' && Number.isFinite(Date.parse(n.createdAt)) && (n.pinned === undefined || typeof n.pinned === 'boolean') && (n.aiRelevant === undefined || typeof n.aiRelevant === 'boolean')) || new Set(h.notes.map(n => n.id)).size !== h.notes.length) return false;
      if (h.lastVisit != null && (typeof h.lastVisit.viewedAt !== 'string' || !Number.isFinite(Date.parse(h.lastVisit.viewedAt)) || !Array.isArray(h.lastVisit.revisionIds) || !h.lastVisit.revisionIds.every(Number.isSafeInteger))) return false;
    }
    if (!data || data.schema !== 1 || typeof data.project?.id !== 'string' || !data.project.id.trim() || typeof data.project.name !== 'string' || !data.project.name.trim() || Object.keys(data.project).some(key => !['id', 'name', 'folder', 'github'].includes(key)) || ('folder' in data.project && !isFolderPath(data.project.folder)) || ('github' in data.project && !isGithubUrl(data.project.github)) || !Array.isArray(data.revisions) || !data.revisions.length || !Array.isArray(data.tasks)) return false;
    const ids = new Set();
    for (const r of data.revisions) {
      if (!r || !Number.isSafeInteger(r.id) || r.id < 1 || ids.has(r.id) || !['manual', 'grill', 'import'].includes(r.mode) || typeof r.title !== 'string' || typeof r.createdAt !== 'string' || !Number.isFinite(Date.parse(r.createdAt)) || typeof r.direction !== 'string' || typeof r.state !== 'string' || !Array.isArray(r.closed) || !Array.isArray(r.open) || ![...r.closed, ...r.open].every(x => typeof x === 'string')) return false;
      ids.add(r.id);
    }
    const activeProjects = new Set();
    const taskIds = new Set();
    for (const t of data.tasks) {
      if (!t || typeof t.id !== 'string' || !t.id.trim() || taskIds.has(t.id) || t.projectId !== data.project.id || !Number.isSafeInteger(t.revision_id) || !ids.has(t.revision_id) || typeof t.title !== 'string' || typeof t.detail !== 'string' || typeof t.status !== 'string') return false;
      if (t.timing !== undefined && t.timing !== 'deadline' && t.timing !== 'discretion') return false;
      if (t.timing === 'deadline' && (typeof t.deadlineAt !== 'string' || !Number.isFinite(Date.parse(t.deadlineAt)))) return false;
      taskIds.add(t.id);
      if (t.status === 'running') { if (activeProjects.has(t.projectId)) return false; activeProjects.add(t.projectId); }
    }
    return true;
  }
  function validate(data) {
    if (data?.schema === 1) return validateProject(data);
    if (!data || data.schema !== 2 || !Array.isArray(data.projects) || !data.projects.length) return false;
    const ids = new Set();
    return data.projects.every(project => {
      if (!validateProject(project) || ids.has(project.project.id)) return false;
      ids.add(project.project.id);
      return true;
    });
  }
  function append(data, draft, mode, timestamp = new Date().toISOString()) {
    if (!validateProject(data)) throw new Error('The saved notebook is invalid.');
    if (!draft || !['title', 'direction', 'state'].every(key => typeof draft[key] === 'string' && draft[key].trim()) || !Array.isArray(draft.closed) || !Array.isArray(draft.open) || ![...draft.closed, ...draft.open].every(x => typeof x === 'string')) throw new Error('Fill in title, goal, and where it stands.');
    const next = clone(data);
    const revision = { id: Math.max(...data.revisions.map(r => r.id)) + 1, mode, title: draft.title.trim(), createdAt: timestamp, direction: draft.direction.trim(), state: draft.state.trim(), closed: draft.closed.map(x => x.trim()).filter(Boolean), open: draft.open.map(x => x.trim()).filter(Boolean) };
    next.revisions.push(revision);
    if (!validate(next)) throw new Error('The new revision is invalid.');
    return next;
  }
  function appendRevision(data, draft, timestamp) { return append(data, draft, 'manual', timestamp); }
  const GENERIC_FRONTIER = [
    'What is the smallest next slice that still serves the current goal?',
    'What is explicitly out of scope for this slice?',
    'How will we know this slice is done?',
    'Which assumption, if wrong, would waste the most work?',
    'What must stay true (local, existing files, no paid APIs)?'
  ];
  function isGenericFrontier(open) {
    if (!Array.isArray(open) || open.length !== GENERIC_FRONTIER.length) return false;
    return open.every((question, index) => question === GENERIC_FRONTIER[index]);
  }
  function grillFrontier(context) {
    const ctx = context && typeof context === 'object' ? context : {};
    if (Array.isArray(ctx.questions) && ctx.questions.length) {
      return ctx.questions.map(item => String(item).trim()).filter(Boolean).slice(0, 8);
    }
    const name = String(ctx.name || 'this project').trim() || 'this project';
    const goal = String(ctx.goal || '').trim();
    const folder = String(ctx.folder || '').trim();
    const docs = (Array.isArray(ctx.docs) ? ctx.docs : []).map(item => String(item).trim()).filter(Boolean).slice(0, 12);
    const questions = [];
    questions.push(goal
      ? `Is this run the next slice of ${name}: ${goal}? Recommended: yes.`
      : `Is this run the next slice of ${name}? Recommended: yes, stay on the notebook goal.`);
    questions.push(folder
      ? `Stay inside ${folder} and the files already there. No paid APIs. Recommended: yes.`
      : 'Keep this local: existing files, no paid APIs. Recommended: yes.');
    questions.push('Is the slice done when the work is on disk with tests, and nothing extra ships? Recommended: yes.');
    questions.push('Is everything else out of scope until this slice is done? Recommended: yes.');
    if (docs.length) {
      questions.push(`Should ${docs.join(', ')} bound this slice? Recommended: yes, those docs are the authority.`);
    }
    return questions;
  }
  function starterFrontier() {
    return grillFrontier({});
  }
  function docsFrontier(docs, context) {
    const names = (Array.isArray(docs) ? docs : []).map(name => String(name).trim()).filter(Boolean).slice(0, 12);
    return grillFrontier({ ...(context && typeof context === 'object' ? context : {}), docs: names });
  }
  function beginGrill(data, title, open, timestamp = new Date().toISOString(), replace = false) {
    if (!validateProject(data)) throw new Error('The saved notebook is invalid.');
    const latest = data.revisions.at(-1);
    if (latest.open.length && !replace && !isGenericFrontier(latest.open)) return data;
    if (!latest.direction.trim()) throw new Error('Fill in title, goal, and where it stands.');
    const previous = clone(data.revisions);
    const next = clone(data);
    next.revisions.push({
      id: Math.max(...data.revisions.map(r => r.id)) + 1,
      mode: 'grill',
      title,
      createdAt: timestamp,
      direction: latest.direction,
      state: latest.state,
      closed: clone(latest.closed),
      open
    });
    if (!validate(next) || JSON.stringify(next.revisions.slice(0, -1)) !== JSON.stringify(previous) || JSON.stringify(next.tasks) !== JSON.stringify(data.tasks)) throw new Error('The updated notebook is invalid.');
    return next;
  }
  function startGrill(data, timestamp = new Date().toISOString(), context) {
    const latest = data.revisions.at(-1);
    const questions = grillFrontier({
      name: data.project.name,
      goal: latest && latest.direction,
      folder: data.project.folder,
      ...(context && typeof context === 'object' ? context : {})
    });
    return beginGrill(data, 'Grill me', questions, timestamp, Boolean(context && context.replace) || isGenericFrontier(latest.open));
  }
  function startGrillDocs(data, docs, timestamp = new Date().toISOString(), context) {
    const names = (Array.isArray(docs) ? docs : []).map(name => String(name).trim()).filter(Boolean).slice(0, 12);
    if (!names.length) throw new Error('No docs in the folder to grill with.');
    const latest = data.revisions.at(-1);
    const questions = docsFrontier(names, {
      name: data.project.name,
      goal: latest && latest.direction,
      folder: data.project.folder,
      ...(context && typeof context === 'object' ? context : {})
    });
    return beginGrill(data, 'Grill with the docs', questions, timestamp, Boolean(context && context.replace) || isGenericFrontier(latest.open));
  }
  function grill(data, round, timestamp) {
    if (!validateProject(data)) throw new Error('The saved notebook is invalid.');
    const latest = data.revisions.at(-1);
    if (!round || typeof round.title !== 'string' || !round.title.trim() || !Array.isArray(round.answers) || round.answers.length !== latest.open.length || !latest.open.length) throw new Error('Answer the open questions of the current revision.');
    const draft = clone(latest);
    draft.title = round.title;
    draft.open = [];
    let directionChanges = 0;
    const responses = [];
    for (let i = 0; i < latest.open.length; i++) {
      const response = round.answers[i];
      if (!response || response.question !== latest.open[i] || !['keep', 'close', 'direction'].includes(response.action) || typeof response.answer !== 'string' || (response.action !== 'keep' && !response.answer.trim())) throw new Error('Each answer must indicate the current question and a valid choice.');
      const answer = response.answer.trim();
      responses.push({ question: response.question, action: response.action, answer });
      if (response.action === 'keep') draft.open.push(response.question);
      else {
        draft.closed.push(`${response.question} → ${answer}`);
        if (response.action === 'direction') {
          if (++directionChanges > 1) throw new Error('Choose only one new direction per round.');
          draft.direction = answer;
        }
      }
    }
    const next = append(data, draft, 'grill', timestamp);
    next.revisions.at(-1).responses = responses;
    return next;
  }
  function importRevision(data, json, timestamp) {
    let draft;
    try { draft = JSON.parse(json); } catch { throw new Error('Invalid import JSON.'); }
    if (!draft || typeof draft !== 'object' || Array.isArray(draft) || Object.keys(draft).some(key => !['id', 'mode', 'createdAt', 'title', 'direction', 'state', 'closed', 'open'].includes(key)) || ('id' in draft && (!Number.isSafeInteger(draft.id) || draft.id < 1)) || ('mode' in draft && !['manual', 'grill', 'import'].includes(draft.mode)) || ('createdAt' in draft && (typeof draft.createdAt !== 'string' || !Number.isFinite(Date.parse(draft.createdAt))))) throw new Error('Import an object with title, goal, where it stands, decided decisions, and open questions.');
    return append(data, draft, 'import', timestamp);
  }
  function projectIdFromName(name, used) {
    const base = name.trim().toLowerCase().normalize('NFKD').replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '').slice(0, 48) || 'project';
    if (!used.has(base)) return base;
    let n = 2;
    while (used.has(`${base}-${n}`)) n += 1;
    return `${base}-${n}`;
  }
  function createProject(data, input, timestamp = new Date().toISOString()) {
    if (!validate(data)) throw new Error('The saved notebook is invalid.');
    if (!input || typeof input.name !== 'string' || !input.name.trim() || typeof input.direction !== 'string' || !input.direction.trim()) throw new Error('Fill in name and goal.');
    const place = placeFrom(input);
    const notebook = data.schema === 2 ? clone(data) : { schema: 2, projects: [clone(data)] };
    const used = new Set(notebook.projects.map(item => item.project.id));
    const id = projectIdFromName(input.name, used);
    const previous = notebook.projects.map(item => clone(item));
    notebook.projects.push({
      schema: 1,
      project: projectRecord(id, input.name.trim(), place),
      revisions: [{ id: 1, mode: 'manual', title: '', createdAt: timestamp, direction: input.direction.trim(), state: '', closed: [], open: [] }],
      tasks: []
    });
    if (!validate(notebook)) throw new Error('The updated notebook is invalid.');
    previous.forEach((item, index) => {
      if (JSON.stringify(item) !== JSON.stringify(notebook.projects[index])) throw new Error('The updated notebook is invalid.');
    });
    return notebook;
  }
  function restoreProject(data, source, place = {}, timestamp = new Date().toISOString()) {
    if (!validate(data)) throw new Error('The saved notebook is invalid.');
    if (!source || typeof source.name !== 'string' || !source.name.trim()) throw new Error('Export is missing a project name.');
    const located = placeFrom({ folder: place.folder || '', github: place.github || '' });
    const notebook = data.schema === 2 ? clone(data) : { schema: 2, projects: [clone(data)] };
    const used = new Set(notebook.projects.map(item => item.project.id));
    const id = projectIdFromName(source.name, used);
    const revisions = Array.isArray(source.revisions) && source.revisions.length
      ? source.revisions.map(revision => ({
        id: revision.id,
        mode: ['manual', 'grill', 'import'].includes(revision.mode) ? revision.mode : 'import',
        title: typeof revision.title === 'string' ? revision.title : '',
        createdAt: typeof revision.createdAt === 'string' && Number.isFinite(Date.parse(revision.createdAt)) ? revision.createdAt : timestamp,
        direction: typeof revision.direction === 'string' ? revision.direction : '',
        state: typeof revision.state === 'string' ? revision.state : '',
        closed: Array.isArray(revision.closed) ? revision.closed.filter(item => typeof item === 'string') : [],
        open: Array.isArray(revision.open) ? revision.open.filter(item => typeof item === 'string') : []
      }))
      : [{ id: 1, mode: 'import', title: 'Imported project', createdAt: timestamp, direction: source.name.trim(), state: '', closed: [], open: [] }];
    const tasks = (Array.isArray(source.tasks) ? source.tasks : []).map(task => ({
      id: task.id,
      projectId: id,
      revision_id: task.revision_id,
      title: task.title,
      detail: task.detail,
      status: task.status === 'running' ? 'paused' : task.status,
      ...(task.timing ? { timing: task.timing } : {}),
      ...(task.deadlineAt ? { deadlineAt: task.deadlineAt } : {})
    }));
    const notes = (Array.isArray(source.notes) ? source.notes : []).filter(note => note && typeof note.id === 'string' && note.id && typeof note.text === 'string' && note.text.trim() && typeof note.createdAt === 'string').map(note => ({
      id: note.id,
      text: note.text,
      createdAt: note.createdAt,
      ...(note.pinned === true ? { pinned: true } : {}),
      ...(note.aiRelevant === true ? { aiRelevant: true } : {})
    }));
    const item = { schema: 1, project: projectRecord(id, source.name.trim(), located), revisions, tasks };
    if (notes.length) item.home = { notes };
    if (!validateProject(item)) throw new Error('Import is not a valid Planwharf project.');
    const previous = notebook.projects.map(entry => clone(entry));
    notebook.projects.push(item);
    if (!validate(notebook)) throw new Error('The updated notebook is invalid.');
    previous.forEach((entry, index) => {
      if (JSON.stringify(entry) !== JSON.stringify(notebook.projects[index])) throw new Error('The updated notebook is invalid.');
    });
    return notebook;
  }
  function setPlace(data, input) {
    if (!validate(data)) throw new Error('The saved notebook is invalid.');
    if (!input || typeof input.id !== 'string' || !input.id.trim()) throw new Error('Project not found.');
    const place = placeFrom(input);
    if (data.schema === 1) {
      if (input.id !== data.project.id) throw new Error('Project not found.');
      const next = clone(data);
      next.project = projectRecord(data.project.id, data.project.name, place);
      if (!validate(next) || JSON.stringify(next.revisions) !== JSON.stringify(data.revisions) || JSON.stringify(next.tasks) !== JSON.stringify(data.tasks)) throw new Error('The updated notebook is invalid.');
      return next;
    }
    const notebook = clone(data);
    const index = notebook.projects.findIndex(item => item.project.id === input.id);
    if (index < 0) throw new Error('Project not found.');
    const previous = notebook.projects.map(item => clone(item));
    const current = notebook.projects[index];
    notebook.projects[index] = { schema: current.schema, project: projectRecord(current.project.id, current.project.name, place), revisions: clone(current.revisions), tasks: clone(current.tasks) };
    if (!validate(notebook)) throw new Error('The updated notebook is invalid.');
    previous.forEach((item, i) => {
      if (i === index) {
        if (JSON.stringify(item.revisions) !== JSON.stringify(notebook.projects[i].revisions) || JSON.stringify(item.tasks) !== JSON.stringify(notebook.projects[i].tasks) || item.project.id !== notebook.projects[i].project.id || item.project.name !== notebook.projects[i].project.name) throw new Error('The updated notebook is invalid.');
      } else if (JSON.stringify(item) !== JSON.stringify(notebook.projects[i])) throw new Error('The updated notebook is invalid.');
    });
    return notebook;
  }
  function removeProject(data, input) {
    if (!validate(data)) throw new Error('The saved notebook is invalid.');
    if (!input || typeof input.id !== 'string' || !input.id.trim()) throw new Error('Project not found.');
    if (data.schema === 1 || !Array.isArray(data.projects) || data.projects.length < 2) throw new Error('The notebook must keep at least one project.');
    const notebook = clone(data);
    const index = notebook.projects.findIndex(item => item.project.id === input.id);
    if (index < 0) throw new Error('Project not found.');
    const remaining = notebook.projects.filter((_, i) => i !== index).map(item => clone(item));
    notebook.projects.splice(index, 1);
    if (!validate(notebook)) throw new Error('The updated notebook is invalid.');
    remaining.forEach((item, i) => {
      if (JSON.stringify(item) !== JSON.stringify(notebook.projects[i])) throw new Error('The updated notebook is invalid.');
    });
    return notebook;
  }
  function taskTiming(input) {
    if (!input || input.timing === undefined || input.timing === null || input.timing === '') return {};
    if (input.timing !== 'deadline' && input.timing !== 'discretion') throw new Error('Choose a deadline or leave timing to the worker.');
    if (input.timing === 'discretion') return { timing: 'discretion' };
    if (typeof input.deadlineAt !== 'string' || !Number.isFinite(Date.parse(input.deadlineAt))) throw new Error('Set the task deadline.');
    return { timing: 'deadline', deadlineAt: new Date(input.deadlineAt).toISOString() };
  }
  function createTask(data, input) {
    if (!validateProject(data)) throw new Error('The saved notebook is invalid.');
    if (data.tasks.some(task => task.status === 'running')) throw new Error('This project already has a running task. No second task was created.');
    if (!input || !Number.isSafeInteger(input.revision_id) || !data.revisions.some(r => r.id === input.revision_id) || typeof input.title !== 'string' || !input.title.trim() || typeof input.detail !== 'string') throw new Error('The task requires a title and an existing revision.');
    const extra = taskTiming(input);
    const next = clone(data);
    let counter = 1;
    while (next.tasks.some(task => task.id === `task-${counter}`)) counter++;
    next.tasks.push({ id: `task-${counter}`, projectId: data.project.id, revision_id: input.revision_id, title: input.title.trim(), detail: input.detail.trim(), status: 'running', ...extra });
    return next;
  }
  function removeTask(data, input) {
    if (!validateProject(data)) throw new Error('The saved notebook is invalid.');
    if (!input || typeof input.id !== 'string' || !input.id.trim()) throw new Error('Job not found.');
    const next = clone(data);
    const index = next.tasks.findIndex(task => task.id === input.id);
    if (index < 0) throw new Error('Job not found.');
    next.tasks.splice(index, 1);
    if (!validate(next) || JSON.stringify(next.revisions) !== JSON.stringify(data.revisions) || JSON.stringify(next.project) !== JSON.stringify(data.project)) throw new Error('The updated notebook is invalid.');
    return next;
  }
  function diff(previous, current) {
    const changes = fields.filter(key => JSON.stringify(previous?.[key]) !== JSON.stringify(current[key])).map(key => ({ key, before: previous?.[key], after: current[key] }));
    const normalize = value => value.trim().toLocaleLowerCase('it-IT');
    const reopened = (previous?.closed || []).filter(decision => current.open.some(question => normalize(question) === normalize(decision)));
    if (reopened.length) changes.push({ key: 'reopened', before: [], after: reopened });
    return changes;
  }
  return { example, validate, validateProject, appendRevision, startGrill, startGrillDocs, grill, importRevision, createProject, restoreProject, removeProject, setPlace, createTask, removeTask, taskTiming, diff, isFolderPath, isGithubUrl, starterFrontier, docsFrontier, grillFrontier, isGenericFrontier, GENERIC_FRONTIER };
});
