'use strict';
// The inputs given to each ported JavaScript function. record.cjs runs them through the JavaScript and
// writes what comes out; the C# tests (ParityTests.cs) give the same inputs to the C# port and must get
// the same results. Add cases here when a module is ported or when a JavaScript module changes.
// Only JSON inputs: a function that takes a callback is called with one that accepts everything.

const plans = [
  { id: 'chatgpt', readable: true, leftPct: 12, windows: [{ name: '5-hour', leftPct: 69 }, { name: 'weekly', leftPct: 12 }] },
  { id: 'claude', readable: true, leftPct: 60, windows: [{ name: '5-hour', leftPct: 60 }, { name: 'weekly', leftPct: 81 }] },
  { id: 'gemini', readable: true, leftPct: 98 },
  { id: 'grok', readable: false },
  { id: 'empty', readable: true, exhausted: true, leftPct: 0 }
];
const model = (id, plan, extra = {}) => ({ id, label: id, plan, enabled: true, available: true, ...extra });
const terra = model('terra', 'chatgpt', { provider: 'codex' });
const sonnet = model('sonnet', 'claude', { provider: 'claude' });
const gemini = model('gemini-pro', 'gemini', { provider: 'antigravity' });
const grok = model('grok', 'grok', { provider: 'grok' });
const spent = model('spent', 'empty', { provider: 'codex' });
const crew = [terra, sonnet, gemini, grok, spent];

module.exports = {
  'model-policy': [
    ['migratePolicy', [{ maxUsedPercent: 80, warningUsedPercent: 70 }]],
    ['migratePolicy', [{ maxUsedPercent: null }]],
    ['migratePolicy', [{ maxUsedPercent: 80, minRemainingPercent: 20 }]],
    ['migratePolicy', [{ maxUsedPercent: 120 }]],
    ['migratePolicy', ['not a policy']],
    ['validatePolicy', [{}]],
    ['validatePolicy', [{ minRemainingPercent: 10, warningRemainingPercent: 25, fallbackModelId: 'sonnet', alwaysApprove: true }]],
    ['validatePolicy', [{ minRemainingPercent: 30, warningRemainingPercent: 20 }]],
    ['validatePolicy', [{ minRemainingPercent: 101 }]],
    ['validatePolicy', [{ fallbackModelId: '   ' }]],
    ['validatePolicy', [{ alwaysApprove: 'yes' }]],
    ['validatePolicy', [{ unknown: 1 }]],
    ['usagePolicy', [terra, plans]],
    ['usagePolicy', [{ ...terra, policy: { minRemainingPercent: 20 } }, plans]],
    ['usagePolicy', [{ ...terra, policy: { minRemainingPercent: 5 } }, plans]],
    ['usagePolicy', [{ ...grok, policy: { minRemainingPercent: 10 } }, plans]],
    ['usagePolicy', [{ ...sonnet, policy: { minRemainingPercent: 50, warningRemainingPercent: 70 } }, plans]],
    ['quotaLeft', [terra, plans]],
    ['quotaLeft', [grok, plans]],
    ['quotaLeft', [model('none', 'missing'), plans]],
    ['quotaLeft', [model('used', 'used'), [{ id: 'used', readable: true, pct: 30 }]]],
    ['quotaLimit', [20]],
    ['quotaLimit', [{ fiveHourPercent: 40, weeklyPercent: 25 }]],
    ['quotaLimit', [{ thresholdPercent: 30 }]],
    ['quotaLimit', [{ none: true }]],
    ['quotaLimit', [{ fiveHourPercent: 150 }]],
    ['quotaLimit', [null]],
    ['quotaWindows', [terra, plans]],
    ['quotaWindows', [gemini, plans]],
    ['quotaWindows', [grok, plans]],
    ['quotaWindows', [sonnet, [{ id: 'claude', readable: true, windows: [{ name: 'five_hour', leftPct: 61 }, { name: 'seven_day', leftPct: 44 }, { name: 'Weekly Opus', leftPct: 30 }] }]]],
    ['windowKind', ['5-hour']], ['windowKind', ['weekly']], ['windowKind', ['seven_day']], ['windowKind', ['Daily']], ['windowKind', ['']],
    ['lowQuota', [terra, plans]],
    ['lowQuota', [terra, plans, 10]],
    ['lowQuota', [terra, plans, { fiveHourPercent: 35, weeklyPercent: 10 }]],
    ['lowQuota', [sonnet, plans, { fiveHourPercent: 65, weeklyPercent: 35 }]],
    ['lowQuota', [sonnet, plans, { none: true }]],
    ['lowQuota', [spent, plans, { none: true }]],
    ['lowQuota', [grok, plans, 90]],
    ['lowQuota', [gemini, plans, 99]],
    ['quotaSwitches', [{ running: [{ subTaskId: 's1', modelId: 'terra' }], models: crew, plans }]],
    ['quotaSwitches', [{ running: [{ subTaskId: 's1', modelId: 'terra' }], models: crew, plans, thresholdPercent: 10 }]],
    ['quotaSwitches', [{ running: [{ subTaskId: 's1', modelId: 'terra', avoidProviders: ['claude'] }], models: crew, plans }]],
    ['quotaSwitches', [{ running: [{ subTaskId: 's1', modelId: 'terra' }], models: [terra, grok], plans }]],
    ['quotaSwitches', [{ running: [{ subTaskId: 's1', modelId: 'terra' }], models: crew, plans, enabled: false }]],
    ['quotaSwitches', [{ running: [{ subTaskId: 's1', modelId: 'sonnet' }, { subTaskId: 's2', modelId: 'spent' }], models: crew, plans }]],
    ['availableAgain', [{ available: false, unavailableAt: '2026-11-13T10:00:00.000Z' }, Date.parse('2026-11-13T10:06:00.000Z')]],
    ['availableAgain', [{ available: false, unavailableAt: '2026-11-13T10:00:00.000Z' }, Date.parse('2026-11-13T10:02:00.000Z')]],
    ['availableAgain', [{ available: false, unavailableAt: '2026-11-13T10:00:00.000Z' }, Date.parse('2026-11-13T10:02:00.000Z'), 60000]],
    ['availableAgain', [{ available: true, unavailableAt: '2026-11-13T10:00:00.000Z' }, Date.parse('2026-11-13T10:06:00.000Z')]],
    ['resolveUsageFallback', [terra, crew, plans]],
    ['resolveUsageFallback', [{ ...terra, policy: { minRemainingPercent: 20, fallbackModelId: 'sonnet' } }, crew, plans]],
    ['resolveUsageFallback', [{ ...terra, policy: { minRemainingPercent: 20 }, fallbackProviderId: '@automatic' }, crew, plans]],
    ['resolveUsageFallback', [{ ...grok, policy: { minRemainingPercent: 20 } }, crew, plans]]
  ],
  'model-roles': [
    ['isVerifySlice', ['Verify the work independently against the job.']],
    ['isVerifySlice', ['Implement the whole job.']],
    ['supportsSupervisor', [{ provider: 'claude', capabilities: { canUseTools: true } }]],
    ['supportsSupervisor', [{ provider: 'grok' }]],
    ['roleEligibility', [{ id: 'm', kind: 'premium', capabilities: { canUseTools: true } }]],
    ['roleEligibility', [{ id: 'm', kind: 'local', workerEligible: false }]],
    ['roleEligibility', [{ id: 'm', capabilities: { canUseTools: false } }]],
    ['withModelRoles', [{ id: 'm', kind: 'premium', capabilities: { canUseTools: true } }, { worker: true, verifier: false }]],
    ['withModelRoles', [{ id: 'm', kind: 'local', workerEligible: false, extra: 1 }, { worker: true, orchestrator: true }]],
    ['assignmentRole', [{ title: 'Verify the work independently against the job: check it.' }]],
    ['assignmentRole', [{ type: 'AUDIT', title: 'Review' }]],
    ['assignmentRole', [{ title: 'Build it' }]],
    ['assignmentRole', [null]],
    ['permitsAssignment', [{ id: 'm', roles: { worker: true, verifier: false } }, { title: 'Build it' }]],
    ['permitsAssignment', [{ id: 'm', roles: { worker: true, verifier: false } }, { title: 'Verify the work independently against the job.' }]],
    ['permitsAssignment', [{ id: 'm', roles: { reviewer: true } }, { type: 'AUDIT', title: 'Review' }]]
  ],
  'structured-final': [
    ['supportsFinalResult', [{ provider: 'claude', adapter: 'claude' }]],
    ['supportsFinalResult', [{ provider: 'codex', adapter: 'codex' }]],
    ['resultSchema', [{ resultId: 'R-1', attemptId: 'a1', subTaskId: 's1', megaTaskId: 'MT-1' }]],
    ['readFinalResult', ['claude', '{"type":"result","is_error":false,"structured_output":{"status":"COMPLETE"}}\n']],
    ['readFinalResult', ['claude', '{"type":"result","is_error":true,"structured_output":{"status":"COMPLETE"}}\n']],
    ['readFinalResult', ['claude', 'not json\n{"type":"result","is_error":false,"result":"{\\"status\\":\\"BLOCKED\\"}"}\n']],
    ['readFinalResult', ['codex', '{"type":"result","is_error":false,"structured_output":{"status":"COMPLETE"}}']],
    ['readFinalResult', ['claude', '']]
  ],
  'activity-events': [
    ['sanitize', ['Plain text']],
    ['sanitize', ['token sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789 end']],
    ['sanitize', ['Authorization: Bearer abc.def.ghi and password=hunter2']],
    ['sanitize', ['  many    spaces\n\nand lines  ']],
    ['sanitize', ['x'.repeat(400)]],
    ['sanitize', ['abcdefghij', 5]]
  ],
  'provider-accounts': [
    ['accountType', ['business']], ['accountType', ['Plus']], ['accountType', ['self_serve_business_usage_based']],
    ['accountType', ['enterprise_cbp_x']], ['accountType', ['team ']], ['accountType', [null]], ['accountType', ['mystery']],
    ['accountLabel', ['ChatGPT', { type: 'business' }]], ['accountLabel', ['ChatGPT', { type: 'normal' }]], ['accountLabel', ['Grok', null]]
  ]
};
