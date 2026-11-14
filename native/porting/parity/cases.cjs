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
  ],
  'provider-refusal': [
    ['refusalReason', ['work...\nFable 5.1 requires usage credits. Switch to another model.']],
    ['refusalReason', ['{"result":"You\'ve hit your 5-hour limit · resets 3am (Europe/Rome)"}']],
    ['refusalReason', ['All 42 tests passed.']],
    ['refusalReason', ['requires usage credits' + ' x'.repeat(5000)]],
    ['refusalReason', ['x'.repeat(5000) + '\nout of credits']],
    ['refusalReason', ['out of credits' + 'y'.repeat(3990)]],
    ['refusalReason', ['z'.repeat(250) + ' quota exceeded']],
    ['refusalReason', ['x429 too many requests']],
    ['refusalReason', ['é429 rate limited']],
    ['refusalReason', ['HTTP 429: Too Many Requests']],
    ['refusalReason', ['429' + 'a'.repeat(81) + ' quota']],
    ['refusalReason', ['YOU’VE HIT YOUR weekly LIMIT']],
    ['refusalReason', ['you’ve hit your limit. More text']],
    ['refusalReason', ['\tRate limit exceeded  now　']],
    ['refusalReason', ['  Rate  Limit Exceeded  ']],
    ['refusalReason', ['requireſ usage credits']],
    ['refusalReason', ['Kout of credits']],
    ['refusalReason', ['QUOTA DEPLETED\r\nnext']],
    ['refusalReason', ['resource_exhausted']], ['refusalReason', ['RESOURCE EXHAUSTED']],
    ['refusalReason', ['insufficient balance']], ['refusalReason', ['Credit exhausted']],
    ['refusalReason', ['usage limit reached']], ['refusalReason', ['rate limit reached']],
    ['refusalReason', ['']], ['refusalReason', [null]],
    ['activeRefusal', [{ refusals: { m: { reason: 'credits', at: '2026-11-04T10:00:00.000Z', until: '2026-11-04T10:00:01.000Z' } } }, 'm', Date.parse('2026-11-04T10:00:00.500Z')]],
    ['activeRefusal', [{ refusals: { m: { reason: 'credits', at: '2026-11-04T10:00:00.000Z', until: '2026-11-04T10:00:01.000Z' } } }, 'm', Date.parse('2026-11-04T10:00:01.000Z')]],
    ['activeRefusal', [{ refusals: { m: { reason: 'credits', at: '2026-11-04T10:00:00.000Z', until: '2026-11-04T10:00:01.000Z' } } }, 'm', Date.parse('2026-11-04T10:00:02.000Z')]],
    ['activeRefusal', [{ refusals: { m: { reason: 'credits', until: 'never' } } }, 'm', 0]],
    ['activeRefusal', [{ refusals: {} }, 'm', 0]],
    ['activeRefusal', [{}, 'm', 0]],
    ['activeRefusal', [null, 'm', 0]],
    ['recordRefusal', [{}, 'm', 'credits', '2026-11-04T10:00:00.000Z', 1000]],
    ['recordRefusal', [{ refusals: { other: { reason: 'x' } } }, 'm', 'r'.repeat(300), '2026-11-04T10:00:00.000Z']],
    ['recordRefusal', [{}, 'm', 'quota', '2026-11-04T10:00:00Z']],
    ['recordRefusal', [{}, 'm', 'quota', 'not a date']]
  ],
  'provider-diagnostics': [
    ['diagnoseProvider', [{ id: 'lead', provider: 'antigravity', cli: 'gemini-3.1-pro-high', enabled: true, available: true, kind: 'default' }, 'invalid model selection (--model "gemini-3.1-pro-high" --effort ""): model gemini-3.1-pro-high is not recognized as a known model or custom model in settings']],
    ['diagnoseProvider', [{ id: 'lead', provider: 'codex', cli: 'gpt-6-astra', plan: 'ChatGPT', enabled: true, kind: 'premium' }, 'unknown flag --old token secret personal prompt', 2]],
    ['diagnoseProvider', [{ id: 'lead', provider: 'codex', cli: 'gpt-6-astra' }, 'Missing or invalid structured worker result.']],
    ['diagnoseProvider', [{ id: 'lead', provider: 'codex', cli: 'gpt-6-astra' }, 'PROVIDER_PROTOCOL_CHANGED']],
    ['diagnoseProvider', [{ id: 'm', provider: 'claude' }, 'PERMISSION_MODE_UNSUPPORTED: bypass', 1]],
    ['diagnoseProvider', [{ id: 'm', provider: 'claude' }, 'permission_mode_unsupported']],
    ['diagnoseProvider', [{ id: 'm', provider: 'claude' }, 'PROVIDER_MODEL_IDENTITY_CHANGED: answered as sonnet']],
    ['diagnoseProvider', [{ id: 'm', provider: 'api' }, 'API_CONFIGURATION_INVALID']],
    ['diagnoseProvider', [{ id: 'm', provider: 'api' }, 'CLI_CONFIGURATION_INVALID']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'error: unexpected argument \'--x\' found']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'flag provided but not defined: -y']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Unsupported output format']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'unsupported\noutput format']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'unsupported format']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Incompatible wire protocol']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'unreadable session metadata']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'No recognized response']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'HTTP 401']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Not logged in. Run login.']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Invalid API key']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, '429 Too Many Requests']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Spending limit reached']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'You are out of CREDITS']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Model gpt-x not found']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'You do not have access to this model']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'spawn grok ENOENT']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'executable not found in PATH']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'connect ETIMEDOUT 1.2.3.4:443']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'getaddrinfo ENOTFOUND api.example']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'self signed certificate in chain']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Network error']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Segmentation fault', 139]],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, 'Key invalid']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, '']],
    ['diagnoseProvider', [{ id: 'm', provider: 'grok' }, null]],
    ['diagnoseProvider', [{}, 'boom']],
    ['diagnoseProvider', [{ providerId: 'custom', adapter: 'api', provider: 'p', cli: 'model (beta)!*\'~', id: 'm' }, 'timeout', 0]],
    ['diagnoseProvider', [{ provider: 'café & co', id: 'müde/\u{1F600}?#' }, 'boom']]
  ],
  'briefing': [
    ['compactState', [{ focus: { id: 'p', lastCommit: 'abc', dirty: [] }, executionHandoff: { megaTasks: [{ id: 'm', goal: 'goal' }], subTasks: [{ megaTaskId: 'm', id: 'done', status: 'COMPLETE' }, { megaTaskId: 'm', id: 'pending', status: 'PENDING' }], results: [], checkpoints: [{ megaTaskId: 'm', id: 'cp', completedSubTaskIds: ['done'], pendingSubTaskIds: ['pending'] }], notes: [{ text: 'private' }, { text: 'relevant', aiRelevant: true }] } }]],
    ['compactState', [{}]],
    ['compactState', [{ focus: null, executionHandoff: null, others: null, researches: null }]],
    ['compactState', [{ mode: 'auto', repositoryDelta: 'old', researches: [{ id: 'r1', projectId: 'q', title: 'other' }], focus: { name: 'Demo', id: 'p', lastLog: 'drop me', analysis: 'a'.repeat(1300), state: 's'.repeat(1201), dirty: [' M a.js'], lastCommit: 'c1',
      repository: { remote: { accessible: true, private: false, branch: 'main', sha: 'abc', url: 'https://x' }, comparison: { status: 'behind', ahead: 0, behind: 2, error: 'e'.repeat(1500), extra: 1 }, local: 'drop' },
      plan: Array.from({ length: 10 }, (_, i) => String(i).repeat(450)), workers: [{ id: 'w1', log: 'secret log', activity: 'x'.repeat(400), pid: 1 }, { id: 'w2', log: 'l' }], extra: { kept: true } },
      others: [{ id: 'o1', name: 'Other', repository: { remote: {} } }, { id: 'o2' }],
      executionHandoff: { megaTasks: [{ id: 'old' }, { id: 'm2', title: 'T', goal: 'g'.repeat(3000), status: 'RUNNING', nextAction: 'n'.repeat(1300), extra: 'drop' }],
        subTasks: [{ megaTaskId: 'm2', id: 's1', title: 'A', status: 'RUNNING', assignedModelId: 'x', dependencies: ['s0'], detail: 'drop' }, { megaTaskId: 'old', id: 's2', status: 'PENDING' }, { megaTaskId: 'm2', id: 's3', status: 'COMPLETE' }],
        results: [1, 2, 3, 4, 5].map(n => ({ megaTaskId: 'm2', subTaskId: 's' + n, status: 'COMPLETE', summary: 'u'.repeat(1250), nextAction: 'none', evidencePath: '/e' + n, logPath: 'drop' })).concat([{ megaTaskId: 'old', subTaskId: 'z' }]),
        checkpoints: [{ megaTaskId: 'm2', id: 'cp1' }, { megaTaskId: 'm2', id: 'cp2', completedSubTaskIds: [], pendingSubTaskIds: ['s1'], failedSubTaskIds: [], nextAction: 'resume', repoState: 'drop' }, { megaTaskId: 'old', id: 'cp3' }],
        notes: Array.from({ length: 15 }, (_, i) => ({ id: 'n' + i, text: 't' + i, aiRelevant: i % 5 !== 0 })).concat([{ id: 'nx', text: 'y'.repeat(1300), aiRelevant: true }, { id: 'ny', aiRelevant: 'true' }]) } }, { head: 'c1', dirty: [' M a.js'] }]],
    ['compactState', [{ focus: { id: 'p', lastCommit: 'c2', dirty: [] }, researches: Array.from({ length: 7 }, (_, i) => ({ id: 'r' + i, projectId: 'p', title: 'R', status: 'DONE', reportPath: '/r', sources: ['x'] })) }, { head: 'c1', dirty: [' M a.js'] }]],
    ['compactState', [{ focus: { id: 'p', repository: { comparison: null } } }, { head: 'c1' }]],
    ['compactState', [{ focus: { id: 'p', lastCommit: null, dirty: null, analysis: null, plan: [], workers: [] } }, { head: null, dirty: null }]],
    ['compactState', [{ focus: { id: 'p' } }, {}]],
    ['compactState', [{ executionHandoff: { megaTasks: [], checkpoints: [{ id: 'orphan' }], subTasks: [{ id: 'loose', status: 'PENDING' }], results: [{ subTaskId: 'r' }], notes: [] } }]],
    ['compactState', [{ executionHandoff: { megaTasks: [{ title: 'no id' }], checkpoints: [{ id: 'cp' }, { id: 'cp-null', megaTaskId: null }] } }]],
    ['compactState', [{ focus: { id: 'p' }, executionHandoff: {} }, null]],
    ['recentContext', [Array.from({ length: 100 }, () => ({ question: 'q'.repeat(4000), answer: 'a'.repeat(8000) }))]],
    ['recentContext', [[{ question: 'first', answer: 'one' }, { question: 'second' }, { answer: 'third' }, { question: null, answer: 7 }]]],
    ['recentContext', [[]]]
  ],
  'project-history': [
    ['summarizeTimeline', [
      ['SUBTASK_STARTED', 'SUBTASK_COMPLETED', 'MEGATASK_COMPLETED'].map((type, n) => ({ id: String(n), type, createdAt: new Date(n * 1000).toISOString(), reference: { megaTaskId: 'm' }, summary: type })),
      { megaTasks: [{ id: 'm', title: 'Add arithmetic', status: 'COMPLETE' }], results: [{ megaTaskId: 'm', status: 'COMPLETE', summary: 'Added module and two tests.' }] }
    ]],
    ['summarizeTimeline', [
      [
        { id: 'e1', createdAt: '2026-11-04T10:00:02.000Z', reference: { megaTaskId: 'm1' }, summary: 's1' },
        { id: 'e0', createdAt: '2026-11-04T10:00:00.000Z', summary: 'independent' },
        { id: 'e2', createdAt: '2026-11-04T10:00:03.000Z', reference: { megaTaskId: 'm2' }, summary: 's2' },
        { id: 'e3', createdAt: '2026-11-04T10:00:01.000Z', reference: { megaTaskId: 'orphan' }, summary: 's3' }
      ],
      {
        megaTasks: [
          { id: 'm1', title: 'Task 1', status: 'RUNNING' },
          { id: 'm2', title: 'Task 2', status: 'COMPLETE' }
        ],
        results: [
          { megaTaskId: 'm1', status: 'COMPLETE', summary: 'summary 1' },
          { megaTaskId: 'm2', status: 'COMPLETE', summary: 'result 1.' },
          { megaTaskId: 'm2', status: 'COMPLETE', summary: 'x'.repeat(700) },
          { megaTaskId: 'm2', status: 'FAILED', summary: 'ignored' }
        ]
      }
    ]],
    ['summarizeTimeline', [[], { megaTasks: [], results: [] }]],
    ['checkpointDiff', [
      { repoState: { repoHead: 'a', fingerprint: 'old', comparable: true }, planRevisionId: 1, completedSubTaskIds: [] },
      { repoHead: 'b', fingerprint: 'new', comparable: true, dirtyFiles: [' M f'] },
      1,
      ['done']
    ]],
    ['checkpointDiff', [
      { repoState: { repoHead: 'a', fingerprint: 'fp', comparable: true }, planRevisionId: 2, completedSubTaskIds: ['s1'] },
      { repoHead: 'a', fingerprint: 'fp', comparable: true, dirtyFiles: [] },
      2,
      ['s1', 's2']
    ]],
    ['checkpointDiff', [
      { repoState: { repoHead: null, fingerprint: 'fp1', comparable: false }, planRevisionId: 1 },
      { repoHead: '', fingerprint: 'fp2', comparable: false, dirtyFiles: ['a', 'b', 'c'] },
      3,
      []
    ]],
    ['checkpointDiff', [
      { repoState: { repoHead: 'same', fingerprint: 'same', comparable: true }, planRevisionId: 1, completedSubTaskIds: ['s1'] },
      { repoHead: 'same', fingerprint: 'same', comparable: true, dirtyFiles: [] },
      1
    ]]
  ],
  'activity-evidence': [
    ['redact', ['api_key=VALUE-1\npassword="VALUE 2"\nAuthorization: Bearer VALUE-3\nhttps://user:VALUE-4@example.invalid?token=VALUE-5\n-----BEGIN PRIVATE KEY-----\nVALUE-6\n-----END PRIVATE KEY-----\n<script>unsafe</script>\x1b[31m\x1b]0;secret title\x07']],
    ['redact', ['--token=secret-token-12345 --password "my pass" --api-key VALUE']],
    ['redact', ['nested: {"api_key":"NESTED-SECRET","password":"NESTED-PASSWORD"}']],
    ['redact', ['sk-1234567890abcdef gh_1234567890 github_pat_1234567890abcdef']],
    ['redact', ['QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE=']],
    ['redact', ['Normal string without secrets <tag> & ok']],
    ['redact', ['']],
    ['redact', [null]],
    ['technicalEvidence', [
      JSON.stringify({ type: 'item.completed', timestamp: '2026-10-16T10:00:00Z', item: { type: 'command_execution', command: 'node --test', aggregated_output: 'ok 1', exit_code: 0, CodeContent: 'NEVER' } }),
      { adapter: 'codex' }
    ]],
    ['technicalEvidence', [
      JSON.stringify({ type: 'assistant', message: { content: [{ type: 'tool_use', name: 'Bash', input: { command: 'node --test' } }] } }),
      { adapter: 'claude' }
    ]],
    ['technicalEvidence', [
      JSON.stringify({ event: 'step_update', step_update: { step_type: 'tool', state: 'DONE', tool_name: 'run_command', tool_info: { parameters: { CommandLine: 'npm test' } } } }),
      { adapter: 'antigravity' }
    ]],
    ['technicalEvidence', ['compiler output line 1\nline 2\n', { adapter: 'codex' }]],
    ['technicalEvidence', ['{"broken": json', { adapter: 'codex' }]],
    ['technicalEvidence', [
      JSON.stringify({ type: 'item.completed', item: { type: 'command_execution', command: 'cmd', aggregated_output: 'out' } }),
      { adapter: 'unknown' }
    ]],
    ['technicalEvidence', ['', {}]],
    ['technicalEvidence', [null, null]]
  ],
  'autonomy': [
    ['createSettings', ['s1', '2026-11-03T10:00:00Z']],
    ['pushGuardEnvironment', [{}]],
    ['pushGuardEnvironment', [{ GIT_CONFIG_COUNT: '2' }]],
    ['nextStep', [{ settings: { enabled: false } }]],
    ['nextStep', [{ mode: 'manual', sessionId: 's1', settings: { enabled: true, sessionId: 's1' }, row: {} }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's2', settings: { enabled: true, sessionId: 's1' }, row: {} }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', stopReason: 'GOAL_REACHED' }, row: {} }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1' }, row: null }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1' }, row: { status: 'checkpointed', checkpoint: { reason: 'stop' } } }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', nudgedKeys: [], nudgeTimes: [] }, row: { status: 'checkpointed', checkpoint: { reason: 'worker failure' } }, alerts: [{ key: 'k1', title: 't', message: 'm' }], now: 1000000 }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', approved: [] }, row: { status: 'needs_approval', megaTaskId: 'm1' } }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', approved: ['m1', 'm2', 'm3', 'm4', 'm5'] }, row: { status: 'needs_approval', megaTaskId: 'm6' } }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: false }, row: { status: 'complete', megaTaskId: 'm1' } }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: true, evaluated: [] }, row: { status: 'complete', megaTaskId: 'm1' } }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: true, nudgedKeys: [], nudgeTimes: [] }, row: { status: 'watching' }, alerts: [{ key: 'a1', title: 't', message: 'm' }], now: 1000000 }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: true, nudgedKeys: ['a1'], nudgeTimes: [] }, row: { status: 'watching' }, alerts: [{ key: 'a1', title: 't', message: 'm' }], now: 1000000 }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: true, nudgedKeys: [], nudgeTimes: ['1970-01-01T00:10:00Z', '1970-01-01T00:20:00Z', '1970-01-01T00:30:00Z'] }, row: { status: 'watching' }, alerts: [{ key: 'a1', title: 't', message: 'm' }], now: 2000000 }]],
    ['strictDecisionPrompt', ['Review project.']],
    ['cleanFinish', [[{ status: 'COMPLETE', blockers: [] }, { status: 'COMPLETE', blockers: [] }], { exitCode: 0, timedOut: false }]],
    ['cleanFinish', [[{ status: 'COMPLETE', blockers: ['err'] }], { exitCode: 0, timedOut: false }]],
    ['cleanFinish', [[], null]],
    ['evaluationPrompt', [{ name: 'Proj', goal: 'Goal', results: [{ status: 'COMPLETE', summary: 'ok' }], checks: { label: 'test', exitCode: 0, seconds: 1, output: 'ok' } }]],
    ['evaluationPrompt', [{ name: 'Proj', goal: null, results: [] }]],
    ['nudgePrompt', [{ name: 'Proj', goal: 'Goal', alert: { title: 'T', message: 'M' } }]],
    ['assignmentPrompt', [{ name: 'Proj', goal: 'Goal', slices: ['Build', 'Verify'], candidates: [[{ id: 'a', label: 'A', provider: 'p1' }], [{ id: 'b', label: 'B', provider: 'p2' }]], said: 'Suggest crew.' }]],
    ['parseDecision', ['{"done":true,"next":"ignored","assumptions":["a"]}']],
    ['parseDecision', ['Sure:\n{"done":false,"next":"Add tests"}']],
    ['parseDecision', ['{"done":false,"next":null}']],
    ['parseDecision', ['invalid']],
    ['parseDecision', [null]],
    ['parseAssignment', ['{"assign":["b","a"],"why":"fast"}', 2, ['a', 'b']]],
    ['parseAssignment', ['{"assign":["a","b"]}', 2, [['a'], ['b']]]],
    ['parseAssignment', ['broken', 2, ['a']]],
    ['splitPrompt', [{ name: 'Proj', goal: 'Goal', builders: [{ id: 'a', label: 'A', provider: 'p1', tier: 'light', left: 50 }], verifiers: [{ id: 'b', label: 'B', provider: 'p2', tier: 'premium' }], files: ['a.js', 'b.js'] }]],
    ['fitWeights', [[{ title: 'T', size: 'light', model: 'heavy-m' }], [{ id: 'light-m', tier: 'light', left: 90 }, { id: 'heavy-m', tier: 'premium' }]]],
    ['parseSplit', ['{"parts":[{"title":"T1","detail":"D1","files":["a.js"],"size":"normal","model":"m1"},{"title":"T2","detail":"D2","files":["a.js","b.js"],"size":"normal","model":"m1"}],"verify":"v1","why":"ok"}', { builders: ['m1'], verifiers: ['v1'] }]],
    ['parseSplit', ['not json', { builders: [], verifiers: [] }]]
  ]
};
