'use strict';
// Step outcomes: records how each finished step went, in Planwharf's data folder.
// Used later to rank and choose models by real results.
// No code, no file names, no paths in here.

const fs = require('node:fs');
const path = require('node:path');
let savingMode = null;
try { savingMode = require('./saving-mode.cjs'); } catch {}

const ROLES = new Set(['Build', 'Verify', 'Review', 'Research']);
const CANONICAL_ROLES = {
  build: 'Build',
  builder: 'Build',
  verify: 'Verify',
  verifier: 'Verify',
  review: 'Review',
  reviewer: 'Review',
  audit: 'Review',
  research: 'Research',
  researcher: 'Research'
};

const CLASSES = new Set(['flagship', 'mid', 'light']);

function canonicalRole(role) {
  if (!role) return 'Build';
  const key = String(role).trim().toLowerCase();
  return CANONICAL_ROLES[key] || (ROLES.has(role) ? role : 'Build');
}

function canonicalClass(cls) {
  if (!cls) return 'mid';
  const key = String(cls).trim().toLowerCase();
  if (key === 'premium' || key === 'flagship') return 'flagship';
  if (key === 'default' || key === 'mid') return 'mid';
  if (key === 'light') return 'light';
  return CLASSES.has(cls) ? cls : 'mid';
}

function resolveModelMeta(model) {
  if (model && typeof model === 'object') {
    const company = model.companyId || model.company || model.providerId || model.provider || 'unknown';
    let cls = model.class || (savingMode ? savingMode.modelClass(model) : null);
    if (!cls || cls === 'mid') {
      const id = String(model.id || model.name || model.cli || '').toLowerCase();
      if (id.includes('opus') || id.includes('luna') || id.includes('business') || id.includes('pro')) cls = 'flagship';
      else if (id.includes('haiku') || id.includes('mini') || id.includes('flash-lite') || id.includes('sol')) cls = 'light';
    }
    return { company, class: canonicalClass(cls) };
  }
  const id = String(model || '').toLowerCase();
  let company = 'unknown';
  if (id.includes('claude') || id.includes('sonnet') || id.includes('opus') || id.includes('haiku') || id.includes('fable')) company = 'claude';
  else if (id.includes('gpt') || id.includes('codex') || id.includes('luna') || id.includes('sol') || id.includes('astra')) company = 'codex';
  else if (id.includes('grok')) company = 'grok';
  else if (id.includes('gemini') || id.includes('antigravity')) company = 'antigravity';

  let cls = 'mid';
  if (id.includes('opus') || id.includes('luna') || id.includes('business') || id.includes('pro')) cls = 'flagship';
  else if (id.includes('haiku') || id.includes('mini') || id.includes('flash-lite') || id.includes('sol')) cls = 'light';

  return { company, class: canonicalClass(cls) };
}

function stripPathsAndCode(value) {
  if (value == null) return null;
  let text = String(value);
  text = text.replace(/```[\s\S]*?```/g, '')
    .replace(/`[^`]*`/g, '')
    .replace(/\{[^{}]*\}/g, '')
    .replace(/[A-Za-z]:[\\/][^\s'"]+/g, '')
    .replace(/\\\\[^\s'"]+/g, '')
    .replace(/(?:^|\s)\/[^\s'"]+/g, '')
    .replace(/(?:^|\s)(?:\.{1,2}[\\/]|[a-zA-Z0-9_.-]+[\\/])[^\s'"]+/g, '')
    .replace(/(?:^|\s)[a-zA-Z0-9_.-]+\.(?:cjs|mjs|js|jsx|ts|tsx|cs|py|rb|go|rs|cpp|c|h|java|php|sh|bat|cmd|ps1|json|ya?ml|toml|xml|html?|css|sql)(?=[\s.,:;!?]|$)/gi, '');
  return text.replace(/\s+/g, ' ').trim();
}

function stepOutcomeRecord({
  model,
  company,
  class: modelClass,
  role,
  kind,
  size,
  accepted,
  fixed,
  testsReported,
  testsPassed,
  tests,
  refusal,
  switch: isSwitch,
  modelChange,
  durationMs,
  startedAt,
  endedAt,
  quota
} = {}) {
  let modelId = '';
  if (model && typeof model === 'object') {
    modelId = String(model.id || model.name || model.cli || '').trim();
  } else {
    modelId = String(model || '').trim();
  }
  if (!modelId || modelId === '[object Object]') return null;

  const resolvedRole = canonicalRole(role);
  const inferred = resolveModelMeta(model);
  const resolvedClass = modelClass ? canonicalClass(modelClass) : inferred.class;
  const resolvedCompany = String(company || '').trim() || inferred.company;
  const resolvedKind = stripPathsAndCode(String(kind || 'implementation').trim()) || 'implementation';
  const resolvedSize = (size !== undefined && size !== null && size !== '')
    ? (typeof size === 'number' && Number.isFinite(size) ? (size >= 0 ? size : 0) : (stripPathsAndCode(String(size).slice(0, 40)) || 'normal'))
    : 'normal';

  const switched = Boolean(isSwitch || modelChange);
  const refusedReason = refusal ? (stripPathsAndCode(String(refusal).slice(0, 200)) || null) : null;

  // Verify acceptance / fix
  let isAccepted = false;
  let fixedCount = 0;
  if (!switched && !refusedReason) {
    if (typeof accepted === 'boolean') {
      isAccepted = accepted;
      fixedCount = Number.isFinite(Number(fixed)) ? Math.max(0, Math.round(Number(fixed))) : (isAccepted ? 0 : 1);
    } else if (Number.isFinite(Number(fixed))) {
      fixedCount = Math.max(0, Math.round(Number(fixed)));
      isAccepted = fixedCount === 0;
    } else {
      isAccepted = true;
      fixedCount = 0;
    }
  }

  // Tests
  let reported = 0;
  let passed = 0;
  if (tests && typeof tests === 'object') {
    if (Array.isArray(tests)) {
      reported = tests.length;
      passed = tests.filter(t => t && (t.status === 'PASSED' || t.status === 'passed')).length;
    } else {
      reported = Number.isFinite(Number(tests.reported)) ? Math.max(0, Math.round(Number(tests.reported))) : 0;
      passed = Number.isFinite(Number(tests.passed)) ? Math.max(0, Math.round(Number(tests.passed))) : 0;
    }
  } else {
    reported = Number.isFinite(Number(testsReported)) ? Math.max(0, Math.round(Number(testsReported))) : 0;
    passed = Number.isFinite(Number(testsPassed)) ? Math.max(0, Math.round(Number(testsPassed))) : 0;
  }
  reported = Math.max(reported, passed);

  // Duration
  let duration = null;
  if (Number.isFinite(Number(durationMs))) {
    duration = Math.max(0, Math.round(Number(durationMs)));
  } else if (startedAt && endedAt) {
    const s = Date.parse(startedAt);
    const e = Date.parse(endedAt);
    if (Number.isFinite(s) && Number.isFinite(e)) duration = Math.max(0, e - s);
  }

  // Quota
  let quotaInfo = null;
  if (quota && typeof quota === 'object') {
    const input = Number.isFinite(Number(quota.inputTokens ?? quota.input)) ? Math.max(0, Math.round(Number(quota.inputTokens ?? quota.input))) : null;
    const output = Number.isFinite(Number(quota.outputTokens ?? quota.output)) ? Math.max(0, Math.round(Number(quota.outputTokens ?? quota.output))) : null;
    if (input !== null || output !== null) {
      quotaInfo = { inputTokens: input, outputTokens: output };
      if (Number.isFinite(Number(quota.cachedInput))) quotaInfo.cachedInput = Math.max(0, Math.round(Number(quota.cachedInput)));
    }
  }

  const record = {
    model: modelId,
    company: resolvedCompany,
    class: resolvedClass,
    role: resolvedRole,
    kind: resolvedKind,
    size: resolvedSize,
    accepted: isAccepted,
    fixed: fixedCount,
    fixedByVerify: fixedCount > 0,
    testsReported: reported,
    testsPassed: passed,
    tests: { reported, passed },
    refusal: refusedReason,
    switch: switched,
    durationMs: duration,
    quota: quotaInfo
  };

  return record;
}

function outcomesFile(dir) {
  return path.join(dir, 'step-outcomes.jsonl');
}

function appendStepOutcome(dir, record) {
  if (!dir || !record) return null;
  const clean = stepOutcomeRecord(record);
  if (!clean) return null;
  try {
    fs.mkdirSync(dir, { recursive: true });
    fs.appendFileSync(outcomesFile(dir), JSON.stringify(clean) + '\n', 'utf8');
    return clean;
  } catch {
    return null;
  }
}

function readStepOutcomes(dir) {
  if (!dir) return [];
  const file = outcomesFile(dir);
  if (!fs.existsSync(file)) return [];
  const lines = [];
  try {
    for (const raw of fs.readFileSync(file, 'utf8').split('\n')) {
      const line = raw.trim();
      if (!line) continue;
      try {
        const parsed = JSON.parse(line);
        const clean = stepOutcomeRecord(parsed);
        if (clean) lines.push(clean);
      } catch { /* skip corrupted lines */ }
    }
  } catch { return []; }
  return lines;
}

function outcomesSummary(records) {
  const map = new Map();
  for (const item of Array.isArray(records) ? records : []) {
    if (!item || !item.model) continue;
    const id = item.model;
    const current = map.get(id) || { model: id, steps: 0, accepted: 0, fixed: 0 };
    current.steps += 1;
    if (item.accepted === true) current.accepted += 1;
    if (item.fixedByVerify === true || item.fixed > 0) current.fixed += 1;
    map.set(id, current);
  }
  return [...map.values()];
}

function summaryText(row) {
  if (!row) return '';
  return `${row.steps} steps · ${row.accepted} accepted as they were · ${row.fixed} fixed by Verify`;
}

module.exports = {
  stepOutcomeRecord,
  outcomesFile,
  appendStepOutcome,
  readStepOutcomes,
  outcomesSummary,
  summaryText,
  canonicalRole,
  canonicalClass,
  resolveModelMeta
};
