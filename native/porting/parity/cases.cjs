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
const agyUsage = groups => ({ status: 'SUCCESS', command: { name: 'usage', data: { groups } } });
const agyGroup = (name, buckets) => ({ name, buckets });
const model = (id, plan, extra = {}) => ({ id, label: id, plan, enabled: true, available: true, ...extra });
const terra = model('terra', 'chatgpt', { provider: 'codex' });
const sonnet = model('sonnet', 'claude', { provider: 'claude' });
const gemini = model('gemini-pro', 'gemini', { provider: 'antigravity' });
const grok = model('grok', 'grok', { provider: 'grok' });
const spent = model('spent', 'empty', { provider: 'codex' });
const crew = [terra, sonnet, gemini, grok, spent];

const path = require('node:path');
const Module = require('node:module');
const orchPath = path.join(__dirname, '..', '..', '..', 'engine', 'orchestrator.js');
const savingMode = require(path.join(__dirname, '..', '..', '..', 'engine', 'saving-mode.cjs'));
const orchestrator = require(orchPath);

const allowedBySavingMode = (mode, item) => {
  if (!mode || savingMode.persisted(mode) === 'normal') return true;
  const tier = Number.isInteger(item?.leadTier) ? item.leadTier : orchestrator.leadTier(item);
  return savingMode.allows(mode, item, tier);
};

const teamModelsMod = {
  ...orchestrator,
  allowedBySavingMode,
  modelClass: savingMode.modelClass
};

orchestrator.allowedBySavingMode = allowedBySavingMode;
orchestrator.modelClass = savingMode.modelClass;

const virtualModPath = path.join(__dirname, '..', '..', '..', 'engine', 'orchestrator-team-models.cjs');
const virtualJobBuildingModPath = path.join(__dirname, '..', '..', '..', 'engine', 'orchestrator-job-building.cjs');
const origResolve = Module._resolveFilename;
Module._resolveFilename = function(request, parent, isMain, options) {
  if (request === virtualModPath || request.endsWith('orchestrator-team-models.cjs')) {
    return virtualModPath;
  }
  if (request === virtualJobBuildingModPath || request.endsWith('orchestrator-job-building.cjs')) {
    return virtualJobBuildingModPath;
  }
  return origResolve.call(this, request, parent, isMain, options);
};
require.cache[virtualModPath] = {
  id: virtualModPath,
  filename: virtualModPath,
  loaded: true,
  exports: teamModelsMod
};
require.cache[virtualJobBuildingModPath] = {
  id: virtualJobBuildingModPath,
  filename: virtualJobBuildingModPath,
  loaded: true,
  exports: orchestrator
};

const leadCandidatesA = [
  { id: 'opus-3', label: 'Claude 3 Opus', plan: 'claude' },
  { id: 'opus-4', label: 'Claude 4 Opus', plan: 'claude' },
  { id: 'fable-1', label: 'Claude 1 Fable', plan: 'claude' },
  { id: 'sonnet-3-5', label: 'Claude 3.5 Sonnet', plan: 'claude' },
  { id: 'haiku-3-5', label: 'Claude 3.5 Haiku', plan: 'claude' },
  { id: 'gemini-flash', label: 'Gemini Flash', plan: 'gemini' },
  { id: 'terra', label: 'GPT-6 Terra', kind: 'premium', plan: 'chatgpt' }
];

const leadCandidatesB = [
  { id: 'custom-flag', label: 'Custom Flag', kind: 'premium' },
  { id: 'custom-mid', label: 'Custom Mid', kind: 'default' },
  { id: 'custom-local', label: 'Custom Local', kind: 'local' },
  { id: 'luna', label: 'Luna Light' }
];

const diverseCrew = [
  { id: 'terra', label: 'Terra', plan: 'chatgpt', enabled: true, available: true, provider: 'codex', cli: 'gpt-5' },
  { id: 'sol', label: 'Sol', plan: 'chatgpt', enabled: true, available: true, provider: 'codex', cli: 'gpt-5' },
  { id: 'sonnet', label: 'Sonnet', plan: 'claude', enabled: true, available: true, provider: 'claude', cli: 'claude-3-5-sonnet' },
  { id: 'gemini', label: 'Gemini Pro', plan: 'gemini', enabled: true, available: true, provider: 'antigravity', cli: 'gemini-flash' }
];

const stateEntry = (overrides = {}) => ({
  status: 'idle', doing: '', plan: [], facts: null, jobId: null, jobTitle: null, attemptId: null,
  modelId: null, approved: false, waitingFor: '', next: '', workerLabel: null, resultPath: null,
  watchingSince: null, holdApproval: false, grillSkipped: false, docs: [], log: [],
  pendingAutoStart: null, checkpoint: null, dispatch: [], resumeRequested: false,
  deadlineHonored: false, workerPid: null, workerLog: null, workerAlive: false, tried: [],
  progressAt: null, progressKey: null, workerTail: '', workerActivity: '', ...overrides
});

const orchestratorStateCases = [
  ['emptyEntry', []],
  ...[null, {},
    { status: 'blocked_no_folder' }, { status: 'propose_grill' }, { status: 'waiting_on_you' },
    { status: 'idle' }, { status: 'idle', pendingAutoStart: { timing: 'discretion' } },
    { status: 'needs_approval' }, { status: 'blocked_no_worker' }, { status: 'watching' },
    { status: 'checkpointed' }, { status: 'failed' }, { status: 'complete' },
    { status: 'mystery' }, { status: null }
  ].map(row => ['stepOf', [row]]),
  ...[null,
    stateEntry(), stateEntry({ status: 'idle', pendingAutoStart: { timing: 'deadline' } }),
    stateEntry({ status: 'propose_grill' }), stateEntry({ status: 'needs_approval' }),
    stateEntry({ status: 'watching' }), stateEntry({ status: 'complete' }),
    stateEntry({ status: 'watching', grillSkipped: true }),
    stateEntry({ status: 'complete', grillSkipped: true }),
    stateEntry({ status: 'mystery', grillSkipped: true })
  ].map(row => ['pipelineOf', [row]]),
  ['crewOf', [null, null]],
  ['crewOf', [stateEntry({ status: 'propose_grill' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Fallback', workerPid: 17, workerAlive: true, workerActivity: 'Compiling' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'needs_approval', dispatch: [
    { subTaskId: 'sub-1', modelId: 'm1', label: 'Builder', slice: 'Build slice', activity: 'Waiting' },
    { subTaskId: 'sub-2', modelId: 'm2', label: 'Reviewer', slice: 'Review slice', activitySession: { role: 'reviewer' } }
  ] }), 'Lead', { subTasks: [] }]],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [
    { modelId: 'm1', label: 'Current', slice: 'Current slice', alive: true, pid: 21, activity: 'Running' },
    { modelId: 'm2', label: 'Historical', slice: 'Old slice', historicalAssignment: true, assignmentJobTitle: 'Old job', activity: 'Finished' }
  ] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [{ modelId: 'm1', label: 'Unhealthy', slice: 'Slice', alive: true }], workerActivity: 'Hit an error: provider' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', workerPid: 0, workerAlive: true, workerActivity: '' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', workerPid: 42, workerAlive: false, workerActivity: '' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', workerPid: 42, workerAlive: true, workerActivity: '' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', workerPid: null, workerAlive: false, workerActivity: '' }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', facts: { fileCount: 0 } }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', facts: { fileCount: 1 } }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', facts: { fileCount: 5 } }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', facts: { fileCount: null } }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', workerLabel: 'Solo', facts: null }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [{ modelId: 'm1', label: 'P1', slice: 'Slice', pid: 0, alive: true }] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [{ modelId: 'm1', label: 'P1', slice: 'Slice', pid: 55, alive: false }] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [{ modelId: 'm1', label: 'P1', slice: 'Slice', pid: 55, alive: true }] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [{ modelId: 'm1', label: 'P1', slice: null, pid: 77, alive: true }] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'watching', dispatch: [{ modelId: 'm1', label: 'P1', slice: '', pid: 77, alive: false }] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'complete', dispatch: [{ modelId: 'm1', label: 'P1', slice: 'Slice' }] }), 'Lead']],
  ['crewOf', [stateEntry({ status: 'blocked_no_worker', dispatch: [{ modelId: 'm1', label: 'P1', slice: 'Slice' }] }), 'Lead']],
  ['progressKey', [null, null, null]],
  ['progressKey', [{ fileCount: 3, names: ['a.cs', 'docs'], dirty: [' M a.cs'] }, 'missing-parity-log-a', null]],
  ['progressKey', [{ fileCount: 3, names: ['a.cs', 'docs'], dirty: [' M a.cs', '?? b.cs'] }, null,
    [{ logPath: 'missing-parity-log-a' }, { logPath: 'missing-parity-log-b' }]]],
  ['progressKey', [{ fileCount: null, names: 'not-an-array', dirty: null }, '', []]],
  ['progressKey', [{ fileCount: 0, names: ['src'], dirty: [] }, null, null]],
  ['progressKey', [{ fileCount: 1, names: ['a', 'b'], dirty: ['M a'] }, null, null]],
  ['progressKey', [{ fileCount: '10', names: [], dirty: [] }, null, null]],
  ['progressKey', [{ fileCount: -2, names: [], dirty: [] }, null, null]],
  ['progressKey', [{ fileCount: 0, names: [], dirty: [] }, 'missing-parity-log-c', []]]
];

module.exports = {
  antigravity: [
    ['parseUsage', [require('../../../fixtures/antigravity-usage.json')]],
    ...['claude-opus-4-6-thinking', 'gpt-oss-120b-medium', 'gemini-3-flash', 'gpt-6', 'Claude-opus', '', null, 123, ['claude-opus']]
      .map(cli => ['poolOf', [cli, 'Gemini']]),
    ['poolOf', ['gemini-flash', null]],
    ...[null, {}, { status: 'SUCCESS' }, { status: 'ERROR', error: 'unknown' },
      { status: 'ERROR', error: 'subscription expired' },
      { status: 'ERROR', result: { error: { message: 'subscription expired' } } },
      { status: 'ERROR', result: { error: 'sign in to agy' }, error: 'ignored' },
      agyUsage([]), agyUsage('wrong shape'),
      agyUsage([agyGroup('Gemini Models', [{ remaining_fraction: 0.5, window: 'daily', reset_time: '2026-11-24T10:00:00Z' }]),
        agyGroup('Claude and GPT models', [{ remaining_fraction: 0 }])]),
      agyUsage([agyGroup('gemini models', [{ remaining_fraction: 0.0001 }, { remaining_fraction: 1 }])]),
      agyUsage([agyGroup('Gemini Models', [{ remaining_fraction: '0' }, { remaining_fraction: true },
        { remaining_fraction: -1 }, { remaining_fraction: 1.01 }, {}, null])]),
      agyUsage([agyGroup('Gemini Models', [{ remaining_fraction: 0.0041, name: 'first' }, { remaining_fraction: 0.004, name: 'tightest' }])]),
      agyUsage([agyGroup('Gemini Models', [{ remaining_fraction: 0.125, window: '', name: null, reset_time: false }])]),
      agyUsage([agyGroup('Gemini Models ', [{ remaining_fraction: 0 }]), agyGroup('Claude and GPT models', [])]),
      agyUsage([agyGroup('Gemini Models', [{ remaining_fraction: 0 }]), agyGroup('Gemini Models', [{ remaining_fraction: 1 }])])]
      .map(body => ['parseUsage', [body]]),
    ...['', null, 'Opus latest', 'gemini-flash\nclaude-opus-4-6  Claude Opus 4.6\ngpt-oss-120b  GPT OSS',
      '\u001b[32mclaude-opus-4-6\u001b[0m\nclaude-opus-4-6  First label\nclaude-opus-4-6  Changed label',
      'gemini-flash\r\ngemini-flash  Flash\r\n_gemini-hidden\ngpt-oss-120b:high',
      '\uFEFFgemini-flash  Flash\nclaude-opus\u00a0\u00a0Opus',
      'gemini-flash\u2003\u2003Flash\ngemini-flash  Later label',
      'GEMINI-flash\ngemini-\nclaude-!\ngpt-oss-120b_medium']
      .map(output => ['parseModels', [output]])
  ],
  runner: [
    ['parseArgs', [['node', 'runner.cjs', '--folder', 'f', '--result', 'r.json', '--title', 't', '--kind', 'audit']]],
    ['parseArgs', [['node', 'runner.cjs', '--folder', 'f', '--result', 'r.json', '--detail', 'd', '--worker', 'w', '--attempt', 'a', '--slice', 's', '--linked-folder', 'lf', '--model-json', '{"id":"m1","provider":"codex","cli":"gpt-5"}']]],
    ['parseArgs', [['node', 'runner.cjs']]],
    ['parseArgs', [[]]],
    ['parseArgs', [['node', 'runner.cjs', '--unknown']]],
    ['dropThinkingPrefix', ['Thinking Process:\n1. Think\n\nActual answer', 'Thinking Process:\n1. Think']],
    ['dropThinkingPrefix', ['Direct answer without prefix', 'Some thoughts']],
    ['dropThinkingPrefix', ['', '']],
    ['dropThinkingPrefix', [null, null]],
    ['dropThinkingPrefix', ['Only answer', null]],
    ['toolRequestRule', [{ mcp: true }]],
    ['toolRequestRule', [{}]],
    ['toolRequestRule', [{ mcp: true, browser: false, hooks: false, memory: false }]],
    ['toolRequestRule', [{ mcp: true, browser: true, hooks: true, memory: true }]],
    ['toolRequestRule', [{ mcp: false, browser: false, hooks: false, memory: false }]],
    ['cliModelOf', [{ id: 'sonnet', provider: 'claude', cli: 'claude-3-5-sonnet' }]],
    ['cliModelOf', [{ id: 'gpt-4o', provider: 'codex', cli: 'gpt-4o' }]],
    ['cliModelOf', [{ id: 'custom' }]],
    ['cliModelOf', [{}]],
    ['cliModelOf', [null]],
    ['parseAskLine', [{ expectedModel: 'claude-3-opus', answer: '', thinking: '', ms: 0 }, { type: 'assistant', message: { model: 'claude-3-opus', content: [{ type: 'text', text: 'Hello' }] } }]],
    ['parseAskLine', [{ expectedModel: 'claude-3-opus', answer: '', thinking: '', ms: 0 }, { type: 'assistant', message: { model: 'other-model', content: [{ type: 'text', text: 'Hello' }] } }]],
    ['parseAskLine', [{ expectedModel: 'claude-3-opus', answer: '', thinking: '', ms: 0 }, { type: 'system', subtype: 'init', model: 'claude-3-opus' }]],
    ['parseAskLine', [{ expectedModel: 'claude-3-opus', answer: '', thinking: '', ms: 0 }, { type: 'turn.failed', error: { message: 'Rate limit exceeded' } }]],
    ['parseAskLine', [{ expectedModel: 'claude-3-opus', answer: '', thinking: '', ms: 0 }, { type: 'turn.completed', usage: { input_tokens: 100, output_tokens: 50, cached_input_tokens: 20 } }]],
    ['parseAskLine', [{ answer: 'Start: ', thinking: '', ms: 0 }, { event: 'step_update', step_update: { step_type: 'agent_response', text_delta: 'continued' } }]],
    ['parseAskLine', [{ answer: '', thinking: 'Initial', ms: 0 }, { event: 'step_update', step_update: { step_type: 'tool', tool_name: 'read_file' } }]],
    ['parseAskLine', [{ answer: '', thinking: '', ms: 0 }, { event: 'result', result: { response: 'Final response', duration_seconds: 3.5, status: 'SUCCESS' } }]],
    ['parseAskLine', [{ answer: '', thinking: '', ms: 0 }, { event: 'result', result: { status: 'DEADLINE_EXCEEDED', error: 'Timed out' } }]],
    ['parseAskLine', [{ expectedModel: 'claude-3-opus', answer: '', thinking: '', ms: 0 }, { type: 'result', subtype: 'success', is_error: false, duration_ms: 1200, result: 'Done', usage: { input_tokens: 80, output_tokens: 40, cache_read_input_tokens: 10 } }]],
    ['parseAskLine', [{ answer: '', thinking: 'Step 1', ms: 0 }, { type: 'user', message: { content: [{ type: 'tool_result', is_error: true, content: 'permission denied' }] } }]],
    ['parseAskLine', [{ answer: '', thinking: '', ms: 0 }, null]],
    ['parseAskLine', [{ answer: '', thinking: '', ms: 0 }, {}]]
  ],
  'external-adapters': [
    ['apiConfig', [{ baseUrl: 'https://api.openai.com/v1', apiProtocol: 'openai' }]],
    ['apiConfig', [{ baseUrl: 'http://127.0.0.1:8000/v1' }]],
    ['apiConfig', [{ baseUrl: 'https://api.anthropic.com', apiProtocol: 'anthropic', authEnv: 'KEY', maxOutputTokens: 8192, maxTurns: 50, requestTimeoutMs: 120000 }]],
    ['apiConfig', [{ baseUrl: 'http://localhost:8080' }]],
    ['apiConfig', [{ baseUrl: 'https://example.invalid:443/v1' }]],
    ['apiConfig', [{ baseUrl: 'ftp://bad' }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com?query=1' }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com#fragment' }]],
    ['apiConfig', [{ baseUrl: 'https://user:pass@api.example.com' }]],
    ['apiConfig', [{ baseUrl: 'http://192.168.1.1:8000' }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com', maxOutputTokens: -1 }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com', maxTurns: 0 }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com', requestTimeoutMs: 500 }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com', apiProtocol: 'invalid-protocol' }]],
    ['apiConfig', [{ baseUrl: 'https://api.example.com', authEnv: 'invalid env!' }]],
    ['apiConfig', [null]],
    ['apiHeaders', [{ apiProtocol: 'openai', authEnv: 'FIXTURE_KEY' }, { FIXTURE_KEY: 'test-key' }]],
    ['apiHeaders', [{ apiProtocol: 'anthropic', authEnv: 'FIXTURE_KEY' }, { FIXTURE_KEY: 'test-key' }]],
    ['apiHeaders', [{ apiProtocol: 'openai', authEnv: 'true' }, { 'true': 'key-for-true' }]],
    ['apiHeaders', [{ apiProtocol: 'openai', authEnv: 'MISSING_KEY' }, { OTHER: 'val' }]],
    ['apiHeaders', [{ apiProtocol: 'openai' }, {}]]
  ],
  'provider-connections': [
    ['connection', [{ id: 'custom-api', label: 'Custom API', adapter: 'api', baseUrl: 'https://api.example.com/v1', apiProtocol: 'openai', authEnv: 'KEY', modelIds: ['model-a', 'model-b'], toolsEnabled: true }]],
    ['connection', [{ id: 'anthropic-api', label: 'Anthropic', adapter: 'api', baseUrl: 'https://api.anthropic.com', apiProtocol: 'anthropic', authEnv: 'KEY', maxOutputTokens: 8192, maxTurns: 50, requestTimeoutMs: 120000, modelIds: ['claude-custom'], toolsEnabled: false }]],
    ['connection', [{ id: 'custom-cli', label: 'Custom CLI', adapter: 'configured-cli', cliCommand: 'my-cli', cliArgs: ['run', '{model}'], yoloArgs: [], restrictedArgs: [], outputProtocol: 'codex-jsonl', modelIds: ['cli-model'], toolsEnabled: true }]],
    ['connection', [{ id: 'custom-cli-2', label: 'Custom CLI 2', adapter: 'configured-cli', cliCommand: 'my-cli', cliArgs: ['--model', '{model}'], yoloArgs: ['--yolo'], restrictedArgs: ['--restricted'], outputProtocol: 'antigravity-jsonl', modelIds: ['model-1', 'model-2'], toolsEnabled: false }]],
    ['connection', [{}]],
    ['connection', [null]],
    ['connection', [{ id: 'BAD_ID', label: 'Label', adapter: 'api', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: '', adapter: 'api', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'a'.repeat(101), adapter: 'api', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'unsupported-adapter', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'api', modelIds: [], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'api', modelIds: ['duplicate', 'duplicate'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'api', modelIds: [' '], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'api', modelIds: ['m1'], toolsEnabled: 'not-bool' }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'api', modelIds: ['m1'], toolsEnabled: true, unknownField: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'configured-cli', cliCommand: '', cliArgs: ['{model}'], yoloArgs: [], restrictedArgs: [], outputProtocol: 'codex-jsonl', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'configured-cli', cliCommand: 'cli', cliArgs: ['no-placeholder'], yoloArgs: [], restrictedArgs: [], outputProtocol: 'codex-jsonl', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'configured-cli', cliCommand: 'cli', cliArgs: ['{model}'], yoloArgs: [], restrictedArgs: [], outputProtocol: 'unsupported-proto', modelIds: ['m1'], toolsEnabled: true }]],
    ['connection', [{ id: 'good-id', label: 'Label', adapter: 'configured-cli', cliCommand: 'cli', cliArgs: 'not-array', yoloArgs: [], restrictedArgs: [], outputProtocol: 'codex-jsonl', modelIds: ['m1'], toolsEnabled: true }]]
  ],
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
    ['parseDecision', ['{"done":false,"next":"Wire the parts together.","summary":"Ported sixteen modules. Tests pass. One part is left."}']],
    ['parseAssignment', ['{"assign":["b","a"],"why":"fast"}', 2, ['a', 'b']]],
    ['parseAssignment', ['{"assign":["a","b"]}', 2, [['a'], ['b']]]],
    ['parseAssignment', ['broken', 2, ['a']]],
    ['splitPrompt', [{ name: 'Proj', goal: 'Goal', builders: [{ id: 'a', label: 'A', provider: 'p1', tier: 'light', left: 50 }], verifiers: [{ id: 'b', label: 'B', provider: 'p2', tier: 'premium' }], files: ['a.js', 'b.js'] }]],
    ['fitWeights', [[{ title: 'T', size: 'light', model: 'heavy-m' }], [{ id: 'light-m', tier: 'light', left: 90 }, { id: 'heavy-m', tier: 'premium' }]]],
    ['parseSplit', ['{"parts":[{"title":"T1","detail":"D1","files":["a.js"],"size":"normal","model":"m1"},{"title":"T2","detail":"D2","files":["a.js","b.js"],"size":"normal","model":"m1"}],"verify":"v1","why":"ok"}', { builders: ['m1'], verifiers: ['v1'] }]],
    ['parseSplit', ['not json', { builders: [], verifiers: [] }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: true }, row: { status: 'checkpointed', megaTaskId: 'm1', checkpoint: { reason: 'blocked worker' }, megaTask: { pauseRequested: true } } }]],
    ['nextStep', [{ mode: 'auto', sessionId: 's1', settings: { enabled: true, sessionId: 's1', started: true, continued: ['m1'] }, row: { status: 'checkpointed', megaTaskId: 'm1', checkpoint: { reason: 'worker failure' } }, alerts: [] }]],
    ['continuePrompt', [{ name: 'Proj', goal: 'Port the modules', reason: 'a step stopped blocked or part way', results: [{ status: 'BLOCKED', summary: 'Ported 1 of 16 modules.', filesChanged: ['a.cs', 'a.test.cs'], blockers: ['The full port is not complete'] }] }]],
    ['continuePrompt', [{ name: 'Proj', results: [] }]],
    ['evaluationPrompt', [{ name: 'Proj', goal: 'Goal', results: [{ status: 'COMPLETE', summary: 'ok', filesChanged: ['x.js'], blockers: ['minor'] }] }]],
    ['splitPrompt', [{ name: 'Proj', goal: 'Port as sheet 42 says', builders: [{ id: 'a', label: 'A', provider: 'p1', tier: 'light' }], verifiers: [{ id: 'b', label: 'B', provider: 'p2', tier: 'premium', left: 40 }], files: ['jobs/42-port.md'], documents: [{ path: 'jobs/42-port.md', text: 'Modules: a, b, c', clipped: true }] }]],
    ['namedDocuments', ['Port, as sheet 42 says, the modules; see docs/NEXT-WORK.md and README.md', ['native/porting/jobs/42-phase4.md', 'native/porting/jobs/4-x.md', 'docs/NEXT-WORK.md', 'README.md', 'other/42-copy.txt']]],
    ['namedDocuments', ['Job 7 and job 9', ['a/07-seven.md', 'b/9-nine.md', 'c/9-nine-again.md']]],
    ['namedDocuments', ['nothing here', []]],
    ['parseSplit', ['{"parts":[{"title":"T1","detail":"D1","files":["a.js"],"size":"huge","model":"nobody"},{"title":"T2","detail":"D2","files":["b.js"],"size":"light","model":"m1"}],"verify":"x","why":"ok"}', { builders: ['m1'], verifiers: ['v1', 'v2'] }]],
    ['parseSplit', [JSON.stringify({ parts: Array.from({ length: 16 }, (_, i) => ({ title: 'P' + i, detail: 'D', files: ['f' + i + '.cs'], size: 'normal', model: 'm1' })), verify: 'v1' }), { builders: ['m1'], verifiers: ['v1'] }]],
    ['parseSplit', [JSON.stringify({ parts: Array.from({ length: 31 }, (_, i) => ({ title: 'P' + i, detail: 'D', files: ['f' + i + '.cs'], size: 'normal', model: 'm1' })), verify: 'v1' }), { builders: ['m1'], verifiers: ['v1'] }]],
    ['fitWeights', [[{ title: 'T', size: 'normal', model: '' }], [{ id: 'light-m', tier: 'light', left: 90 }, { id: 'heavy-m', tier: 'premium' }]]],
    ['splitPrompt', [{ name: 'P', goal: 'G', builders: [], verifiers: [], files: [] }]],
    ['spreadParts', [Array.from({ length: 16 }, (_, i) => ({ title: 'Port ' + i, size: 'normal', model: i % 2 ? 'sol6' : 'sol61' })), [{ id: 'sol61', provider: 'codex', tier: 'default', left: 84 }, { id: 'sol6', provider: 'codex', tier: 'default', left: 84 }, { id: 'grok', provider: 'grok', tier: 'default' }, { id: 'gemini', provider: 'gemini', tier: 'default', left: 90 }, { id: 'luna', provider: 'codex', tier: 'light', left: 20 }]]],
    ['spreadParts', [[{ title: 'a', size: 'light', model: 'luna' }, { title: 'b', size: 'heavy', model: 'x' }, { title: 'c', model: 'luna' }], [{ id: 'luna', provider: 'codex', tier: 'light' }, { id: 'opus', tier: 'premium' }, { id: 'flash', provider: 'gemini', tier: 'light', left: 10 }]]],
    ['spreadParts', [[{ title: 'only', size: 'normal', model: 'a' }], []]]
  ],
  plan: [
    ['example', []],
    ['isFolderPath', ['C:\\project\\folder']],
    ['isFolderPath', ['\\\\server\\share\\project']],
    ['isFolderPath', ['/var/project']],
    ['isFolderPath', ['"C:\\project"']],
    ['isFolderPath', ['/C:']],
    ['isFolderPath', ['C:\\project\0bad']],
    ['isFolderPath', ['C:\\' + 'a'.repeat(500)]],
    ['isFolderPath', ['']],
    ['isFolderPath', [null]],
    ['isGithubUrl', ['https://github.com/owner/repo']],
    ['isGithubUrl', ['https://gitea.example.com/owner/repo']],
    ['isGithubUrl', ['https://user:secret@github.com/owner/repo']],
    ['isGithubUrl', ['git@github.com:owner/repo.git']],
    ['isGithubUrl', ['https://github.com/owner/../repo']],
    ['isGithubUrl', ['https://github.com/owner/.']],
    ['isGithubUrl', [null]],
    ['taskTiming', [{ timing: 'deadline', deadlineAt: '2026-11-20T10:00:00.000Z' }]],
    ['taskTiming', [{ timing: 'discretion' }]],
    ['taskTiming', [{ timing: '' }]],
    ['taskTiming', [{}]],
    ['taskTiming', [null]],
    ['taskTiming', [{ timing: 'invalid' }]],
    ['taskTiming', [{ timing: 'deadline', deadlineAt: 'bad date' }]],
    ['validate', [require('../../../plan.js').example()]],
    ['validate', [{ schema: 1, project: { id: 'p', name: 'P' }, revisions: [], tasks: [] }]],
    ['validate', [{ schema: 2, projects: [require('../../../plan.js').example()] }]],
    ['validate', [{ schema: 2, projects: [] }]],
    ['validate', [{ schema: 2, projects: [require('../../../plan.js').example(), { ...require('../../../plan.js').example(), project: { ...require('../../../plan.js').example().project, id: 'biblioteca' } }] }]],
    ['validate', [{ schema: 2, projects: [require('../../../plan.js').example(), { ...require('../../../plan.js').example(), project: { ...require('../../../plan.js').example().project, id: 'p2' }, tasks: [{ ...require('../../../plan.js').example().tasks[0], id: 't2', projectId: 'p2' }] }] }]],
    ['validateProject', [require('../../../plan.js').example()]],
    ['validateProject', [{ ...require('../../../plan.js').example(), project: { ...require('../../../plan.js').example().project, unknown: 1 } }]],
    ['validateProject', [{ ...require('../../../plan.js').example(), project: { ...require('../../../plan.js').example().project, folder: 'bad\0folder' } }]],
    ['validateProject', [{ ...require('../../../plan.js').example(), project: { ...require('../../../plan.js').example().project, github: 'http://insecure.url' } }]],
    ['createProject', [require('../../../plan.js').example(), { name: 'New Project', direction: 'Build something' }, '2026-11-20T10:00:00.000Z']],
    ['createProject', [require('../../../plan.js').example(), { name: 'Neighborhood library', direction: 'Duplicate name' }, '2026-11-20T10:00:00.000Z']],
    ['createProject', [require('../../../plan.js').example(), { name: 'Project with place', direction: 'Build with place', folder: 'C:\\valid\\folder', github: 'https://github.com/owner/repo' }, '2026-11-20T10:00:00.000Z']],
    ['createProject', [require('../../../plan.js').example(), { name: '', direction: 'No name' }, '2026-11-20T10:00:00.000Z']],
    ['createProject', [require('../../../plan.js').example(), { name: 'P', direction: 'D', folder: 'bad\0folder' }, '2026-11-20T10:00:00.000Z']],
    ['restoreProject', [require('../../../plan.js').example(), { name: 'Restored', revisions: require('../../../plan.js').example().revisions, tasks: require('../../../plan.js').example().tasks, notes: [{ id: 'n1', text: 'Important', createdAt: '2026-11-20T10:00:00.000Z' }] }, { folder: 'C:\\restored' }, '2026-11-20T10:00:00.000Z']],
    ['restoreProject', [require('../../../plan.js').example(), { name: '' }, null, '2026-11-20T10:00:00.000Z']],
    ['setPlace', [require('../../../plan.js').example(), { id: 'biblioteca', folder: 'C:\\new\\folder', github: 'https://github.com/owner/repo' }]],
    ['setPlace', [{ schema: 2, projects: [require('../../../plan.js').example()] }, { id: 'biblioteca', folder: 'C:\\new\\folder' }]],
    ['setPlace', [require('../../../plan.js').example(), { id: 'missing', folder: 'C:\\folder' }]],
    ['resetProject', [require('../../../plan.js').example(), { id: 'biblioteca', direction: 'New fresh start' }, '2026-11-20T10:00:00.000Z']],
    ['resetProject', [require('../../../plan.js').example(), { id: 'missing', direction: 'D' }, '2026-11-20T10:00:00.000Z']],
    ['removeProject', [{ schema: 2, projects: [require('../../../plan.js').example(), { ...require('../../../plan.js').example(), project: { ...require('../../../plan.js').example().project, id: 'p2' }, tasks: [{ ...require('../../../plan.js').example().tasks[0], id: 't2', projectId: 'p2' }] }] }, { id: 'p2' }]],
    ['removeProject', [require('../../../plan.js').example(), { id: 'biblioteca' }]],
    ['appendRevision', [require('../../../plan.js').example(), { title: 'Rev 3', direction: 'Keep going', state: 'Good', closed: ['Done step'], open: ['Next question'] }, '2026-11-20T10:00:00.000Z']],
    ['appendRevision', [require('../../../plan.js').example(), { title: '' }, '2026-11-20T10:00:00.000Z']],
    ['startGrill', [require('../../../plan.js').example(), '2026-11-20T10:00:00.000Z']],
    ['startGrill', [require('../../../plan.js').example(), '2026-11-20T10:00:00.000Z', { replace: true }]],
    ['startGrillDocs', [require('../../../plan.js').example(), ['README.md', 'DESIGN.md'], '2026-11-20T10:00:00.000Z']],
    ['startGrillDocs', [require('../../../plan.js').example(), [], '2026-11-20T10:00:00.000Z']],
    ['grill', [require('../../../plan.js').example(), { title: 'Grill answer', answers: [{ question: 'Who covers the first opening shift?', action: 'close', answer: 'Alice' }, { question: 'Should we confirm Saturday morning opening?', action: 'keep', answer: '' }] }, '2026-11-20T10:00:00.000Z']],
    ['grill', [require('../../../plan.js').example(), { title: 'Double direction', answers: [{ question: 'Who covers the first opening shift?', action: 'direction', answer: 'New direction 1' }, { question: 'Should we confirm Saturday morning opening?', action: 'direction', answer: 'New direction 2' }] }, '2026-11-20T10:00:00.000Z']],
    ['importRevision', [require('../../../plan.js').example(), JSON.stringify({ id: 3, mode: 'import', title: 'Imported', createdAt: '2026-11-20T10:00:00.000Z', direction: 'Dir', state: 'St', closed: ['C'], open: ['O'] }), '2026-11-20T10:00:00.000Z']],
    ['importRevision', [require('../../../plan.js').example(), '{ invalid json', '2026-11-20T10:00:00.000Z']],
    ['importRevision', [require('../../../plan.js').example(), JSON.stringify({ extraKey: true }), '2026-11-20T10:00:00.000Z']],
    ['createTask', [{ ...require('../../../plan.js').example(), tasks: [] }, { revision_id: 2, title: 'Task 1', detail: 'Do work' }]],
    ['createTask', [require('../../../plan.js').example(), { revision_id: 2, title: 'Task 2', detail: 'Detail' }]],
    ['createTask', [{ ...require('../../../plan.js').example(), tasks: [] }, { revision_id: 2, title: '', detail: 'No title' }]],
    ['removeTask', [require('../../../plan.js').example(), { id: 'calendario' }]],
    ['removeTask', [require('../../../plan.js').example(), { id: 'missing' }]],
    ['diff', [require('../../../plan.js').example().revisions[0], require('../../../plan.js').example().revisions[1]]],
    ['diff', [{ closed: ['Quale sede scegliere?'] }, { open: ['quale sede scegliere?'] }]],
    ['diff', [{ closed: ['CITTÀ'] }, { open: ['città'] }]]
  ],
  quota: [
    ['describe', [{ readable: true, leftPct: 90 }]],
    ['describe', [{ readable: true, leftPct: 75 }]],
    ['describe', [{ readable: true, leftPct: 60 }]],
    ['describe', [{ readable: true, leftPct: 50 }]],
    ['describe', [{ readable: true, leftPct: 40 }]],
    ['describe', [{ readable: true, leftPct: 25 }]],
    ['describe', [{ readable: true, leftPct: 10 }]],
    ['describe', [{ readable: true, leftPct: 1 }]],
    ['describe', [{ readable: true, leftPct: 0 }]],
    ['describe', [{ readable: false }]],
    ['describe', [{ readable: true, pct: 30 }]],
    ['describe', [{ readable: true, leftPct: -5 }]],
    ['describe', [{ readable: true, leftPct: 150 }]],
    ['describe', [null]],
    ['html', [{ readable: true, leftPct: 0 }]],
    ['html', [{ readable: false }]],
    ['html', [{ readable: true, leftPct: 80 }]]
  ],
  models: [
    ['getEnabledModels', [[{ id: 'm1', enabled: true, available: true }, { id: 'm2', enabled: false, available: true }, { id: 'm3', enabled: true, available: false }, null]]],
    ['getEnabledModels', [null]],
    ['setModelEnabled', [[{ id: 'm1', enabled: false, available: true }, { id: 'm2', enabled: true, available: false }, null], 'm1', true]],
    ['setModelEnabled', [[{ id: 'm1', enabled: true, available: false }], 'm1', true]],
    ['setModelEnabled', [null, 'm1', true]],
    ['validateWorkerClaim', [[{ id: 'm1', enabled: true, available: true }], 'm1']],
    ['validateWorkerClaim', [[{ id: 'm1', enabled: false, available: true }], 'm1']],
    ['validateWorkerClaim', [[{ id: 'm1', enabled: true, available: true }], 'm2']],
    ['validateWorkerClaim', [[{ id: 'm1', enabled: true, available: true }, { id: 'm2', enabled: false, available: true }], 'm2']],
    ['validateWorkerClaim', [[{ id: 'm1', enabled: true, available: false }], 'm1']],
    ['validateWorkerClaim', [[{ id: 'm1', enabled: true, available: true }, { id: 'm2', enabled: true, available: false }], 'm2']],
    ['validateWorkerClaim', [null, 'm1']]
  ],
  'project-export': [
    ['sanitize', ['Bearer sk-ant-api03-abcdef12345678901234']],
    ['sanitize', ['sk-1234567890abcdef123456']],
    ['sanitize', [{ credentials: 'secret', auth: 'token', password: '123', secret: 'abc', env: 'x', thinking: 'thought', normal: 'hello' }]],
    ['sanitize', [[{ apiKey: 'secret' }, { text: 'Bearer my-token-here' }, 42]]],
    ['sanitize', [null]],
    ['continueFrom', [{ megaTasks: [{ id: 'm1', status: 'PAUSED', currentCheckpointId: 'cp1' }] }]],
    ['continueFrom', [{ megaTasks: [{ id: 'm2', status: 'READY_FOR_APPROVAL' }] }]],
    ['continueFrom', [{ megaTasks: [{ id: 'm3', status: 'COMPLETE' }] }]],
    ['continueFrom', [{ megaTasks: [] }]],
    ['continueFrom', [null]]
  ],
  'execution-next': [
    ['executionNext', [{ id: 'm1', status: 'READY_FOR_APPROVAL' }, {}]],
    ['executionNext', [{ id: 'm1', status: 'READY_FOR_APPROVAL', nextAction: 'Custom approval' }, {}]],
    ['executionNext', [{ id: 'm1', status: 'COMPLETE' }, { results: [{ megaTaskId: 'm1', status: 'COMPLETE', nextAction: 'Done!' }] }]],
    ['executionNext', [{ id: 'm1', status: 'COMPLETE' }, {}]],
    ['executionNext', [{ id: 'm1', status: 'RUNNING' }, { subTasks: [{ megaTaskId: 'm1', id: 's1', title: 'Build task', status: 'RUNNING' }] }]],
    ['executionNext', [{ id: 'm1', status: 'RUNNING' }, { subTasks: [{ megaTaskId: 'm1', id: 's2', title: 'Next task', status: 'PENDING' }] }]],
    ['executionNext', [{ id: 'm1', status: 'RUNNING' }, { subTasks: [{ megaTaskId: 'm1', id: 's2', title: 'Task 2', status: 'PENDING', dependencies: ['s1'] }, { megaTaskId: 'm1', id: 's1', title: 'Task 1', status: 'PENDING' }] }]],
    ['executionNext', [{ id: 'm1', status: 'PAUSED', currentCheckpointId: 'cp1' }, { checkpoints: [{ id: 'cp1', megaTaskId: 'm1', projectId: 'p1', nextAction: 'Resume action' }] }]],
    ['executionNext', [{ id: 'm1', status: 'FAILED' }, { subTasks: [{ megaTaskId: 'm1', id: 's1', title: 'Failed task', status: 'FAILED' }], results: [{ megaTaskId: 'm1', subTaskId: 's1', blockers: ['Missing token'] }] }]],
    ['executionNext', [null]],
    ['homeNext', [{ project: { folder: 'bad/folder' } }, null, null]],
    ['homeNext', [{ project: { folder: 'C:\\valid', id: 'p1' } }, { canResume: true }, null]],
    ['homeNext', [{ project: { folder: 'C:\\valid', id: 'p1' } }, { status: 'needs_approval' }, null]],
    ['homeNext', [{ project: { folder: 'C:\\valid', id: 'p1' }, revisions: [{ open: ['Question?'] }] }, null, null]],
    ['homeNext', [{ project: { folder: 'C:\\valid', id: 'p1' }, revisions: [{ open: [] }] }, null, null]]
  ],
  'provider-catalog': [
    ['notInstalledReason', [{ adapter: 'claude' }]],
    ['notInstalledReason', [{ adapter: 'codex' }]],
    ['notInstalledReason', [{ adapter: 'grok' }]],
    ['notInstalledReason', [{ adapter: 'antigravity' }]],
    ['notInstalledReason', [{ adapter: 'custom', label: 'My Tool' }]],
    ['providerConfiguration', [{ baseUrl: 'https://api.example', maxTurns: 10, otherField: 'ignored' }]],
    ['providerConfiguration', [{ yoloArgs: ['--yolo'], requestTimeoutMs: 5000 }]],
    ['loadCatalog', [{ schema: 3, providers: [{ id: 'codex', adapter: 'codex', models: [{ id: 'm1', cli: 'gpt-5' }] }] }]],
    ['loadCatalog', [[{ id: 'm1', provider: 'codex', cli: 'gpt-5' }]]],
    ['loadCatalog', [{ schema: 2 }]],
    ['loadCatalog', [{ schema: 3, providers: [{ id: 'dup' }, { id: 'dup' }] }]],
    ['providerViews', [[{ id: 'p1', adapter: 'codex', quotaPoolId: 'codex', enabled: true, available: true }], [{ id: 'm1', providerId: 'p1', enabled: true, available: true }], [{ id: 'codex', readable: true, leftPct: 80 }]]],
    ['providerViews', [[{ id: 'p1', adapter: 'codex', quotaPoolId: 'codex' }], [{ id: 'm1', providerId: 'p1', notInstalled: true }]]]
  ],
  'provider-policy': [
    ['validateProviderPolicy', [{ minRemainingPercent: 20, warningRemainingPercent: 30 }]],
    ['validateProviderPolicy', [{ fallbackProviderId: 'other' }]],
    ['validateProviderPolicy', [{ alwaysApprove: true }]],
    ['validateProviderPolicy', [{ unknown: 1 }]],
    ['validateProviderPolicy', [{ fallbackProviderId: '' }]],
    ['providerPolicyFor', [{ id: 'p1' }, [], { providerPolicies: { p1: { minRemainingPercent: 15 } } }]],
    ['providerPolicyFor', [{ id: 'p1', policy: { warningRemainingPercent: 40 } }, []]],
    ['providerPolicyFor', [{ id: 'p1' }, [{ id: 'm1', providerId: 'p1', policy: { minRemainingPercent: 25 }, alwaysApprove: true }]]],
    ['providerPolicyFor', [{ id: 'p1' }, []]]
  ],
  'model-routing': [
    ['modelRouteError', [null]],
    ['modelRouteError', [{ available: false, unavailableReason: 'Not enabled' }]],
    ['modelRouteError', [{ adapter: 'unsupported', cli: 'gpt-4' }]],
    ['modelRouteError', [{ adapter: 'codex', cli: '   ' }]],
    ['modelRouteError', [{ adapter: 'codex', cli: 'gpt-5\x00bad' }]],
    ['modelRouteError', [{ adapter: 'codex', cli: 'gpt-5' }]],
    ['modelRouteError', [{ adapter: 'codex', cli: 'claude-3', modelRouting: 'configured' }]],
    ['modelRouteError', [{ adapter: 'codex', cli: 'claude-3' }]],
    ['modelRouteError', [{ adapter: 'codex', cli: 'gpt-5', modelRouting: 'invalid' }]],
    ['resolveCliModel', [{ adapter: 'codex', cli: 'gpt-5' }]],
    ['resolveCliModel', [{ adapter: 'codex', cli: 'bad\0' }]],
    ['savingModeError', [{ label: 'Claude Opus', weight: 'flagship' }, 'safe', 0]],
    ['savingModeError', [{ label: 'Claude Haiku', weight: 'light' }, 'safe', 2]],
    ['savingModeError', [{ label: 'Claude Sonnet', weight: 'mid' }, 'ultra-safe', 1]],
    ['savingModeError', [{ label: 'Claude Sonnet', weight: 'mid' }, 'normal', 1]]
  ],
  'claude': [
    ['claudeToolArgs', [{}]],
    ['claudeToolArgs', [{ hooks: true }]],
    ['claudeToolArgs', [{ mcp: true, browser: true, memory: true }]],
    ['claudeToolArgs', [{ hooks: true, mcp: true, browser: true, memory: true }]],
    ['claudeToolArgs', [null]],
    ['parseModels', [[{ resolvedModel: 'claude-3-5-sonnet-20241022', displayName: 'Sonnet' }]]],
    ['parseModels', [[{ value: 'claude-3-5-sonnet-20241022' }, { resolvedModel: 'claude-3-5-sonnet-20241022', displayName: 'Named' }]]],
    ['parseModels', [[{ resolvedModel: 'invalid_name' }]]],
    ['parseModels', [[]]],
    ['parseModels', [null]]
  ],
  'claude-usage': [
    ['parseClaudeUsage', [{ five_hour: { utilization: 25, resets_at: '2026-11-23T22:00:00.000Z' } }]],
    ['parseClaudeUsage', [{ five_hour: { utilization: 80 }, seven_day: { utilization: 50 } }]],
    ['parseClaudeUsage', [{ five_hour: { utilization: 100 } }]],
    ['parseClaudeUsage', [{ five_hour: { utilization: -5 } }]],
    ['parseClaudeUsage', [{ five_hour: { utilization: 50, resets_at: 'invalid-date' } }]],
    ['parseClaudeUsage', [{}]],
    ['parseClaudeUsage', [null]]
  ],
  'repository': [
    ['githubRepo', ['https://github.com/foo/bar.git']],
    ['githubRepo', ['git@github.com:foo/bar.git']],
    ['githubRepo', ['https://github.com/foo/bar/']],
    ['githubRepo', ['https://gitlab.com/foo/bar']],
    ['githubRepo', ['']],
    ['githubRepo', [null]],
    ['repositoryAddress', ['https://github.com/foo/bar.git']],
    ['repositoryAddress', ['https://gitea.example.com/org/repo.git']],
    ['repositoryAddress', ['https://gitea.example.com/sub/path/org/repo']],
    ['repositoryAddress', ['ftp://gitea.example.com/org/repo']],
    ['repositoryAddress', ['https://gitea.example.com/org/repo?query=1']],
    ['repositoryAddress', ['']],
    ['repositoryAddress', [null]],
    ['cleanError', ['error at https://user:pass@github.com/foo/bar']],
    ['cleanError', ['failed with token ghp_1234567890abcdefghijklmnopqrstuvwxyzAB']],
    ['cleanError', ['short clean error']],
    ['cleanError', [null]],
    ['relation', [0, 0]],
    ['relation', [2, 0]],
    ['relation', [0, 3]],
    ['relation', [1, 1]]
  ],
  'push-email': [
    ['privateEmailRefused', ['remote: error: GH007: Your push would contain a private email address']],
    ['privateEmailRefused', ['Everything up-to-date']],
    ['privateEmailRefused', ['']],
    ['privateEmailRefused', [null]]
  ],
  'plan-status': [
    ['statusFrom', ['claude', { readable: true }]],
    ['statusFrom', ['claude', { reason: 'subscription has expired' }]],
    ['statusFrom', ['claude', { reason: 'not logged in' }]],
    ['statusFrom', ['claude', { reason: 'connection reset' }]],
    ['statusFrom', ['codex', { reason: 'your plan has expired' }]],
    ['statusFrom', ['codex', { reason: 'sign in with codex' }]],
    ['statusFrom', ['grok', { reason: 'plan is expired' }]],
    ['statusFrom', ['antigravity@user', { reason: 'subscription is inactive' }]],
    ['statusFrom', ['custom', { reason: 'not logged in' }]],
    ['withStatus', ['claude', { id: 'Claude', readable: true }]],
    ['withStatus', ['claude', { id: 'Claude', readable: false, reason: 'subscription expired' }]],
    ['blockedStatus', [{ planStatus: 'expired' }]],
    ['blockedStatus', [{ planStatus: 'not signed in' }]],
    ['blockedStatus', [{ planStatus: 'active' }]],
    ['blockedStatus', [null]],
    ['providerError', ['claude', 'subscription has expired', 'Claude']],
    ['providerError', ['claude', 'login expired', 'Claude']],
    ['providerError', ['claude', 'timeout', 'Claude']],
    ['notePlanRefusal', [[{ id: 'pool-1', planStatus: 'active' }], { quotaPoolId: 'pool-1', adapter: 'claude' }, 'subscription expired']],
    ['notePlanRefusal', [[], { quotaPoolId: 'pool-2', adapter: 'claude' }, 'not logged in']],
    ['notePlanRefusal', [[{ id: 'pool-1' }], { quotaPoolId: 'pool-1', adapter: 'claude' }, 'other error']],
    ['notePlanRefusal', [[], null, 'subscription expired']],
    ['mergePlans', [[{ id: 'p1', planStatus: 'expired' }], []]],
    ['mergePlans', [[{ id: 'p1', planStatus: 'active' }], []]],
    ['mergePlans', [[{ id: 'p1', planStatus: 'expired' }], [{ id: 'p1', planStatus: 'unknown' }]]],
    ['mergePlans', [[{ id: 'p1', planStatus: 'expired' }], [{ id: 'p1', readable: true, planStatus: 'active' }]]]
  ],
  'supervisor': [
    ['label', ['   ']],
    ['label', ['My Custom Supervisor']],
    ['label', [null]],
    ['advisoryError', [null, []]],
    ['advisoryError', [{ roles: { supervisor: false } }, []]],
    ['advisoryError', [{ roles: { supervisor: true }, adapter: 'unknown' }, []]],
    ['advisoryError', [{ roles: { supervisor: true }, adapter: 'antigravity', enabled: false }, []]],
    ['advisoryError', [{ roles: { supervisor: true }, adapter: 'antigravity', enabled: true, available: true, providerEnabled: true }, []]],
    ['advisoryError', [{ roles: { supervisor: true }, adapter: 'antigravity', enabled: true, available: true, providerEnabled: true, quotaPoolId: 'p1' }, [{ id: 'p1', readable: true, exhausted: true }]]],
    ['projectFacts', [{ projects: [] }, 'missing']],
    ['projectFacts', [{ projects: [{ id: 'p1', name: 'Project 1', status: 'needs_approval' }] }, 'p1', 1700000000000]],
    ['projectFacts', [{ projects: [{ id: 'p1', name: 'Project 1', status: 'complete', dispatch: [] }] }, 'p1', 1700000000000]]
  ],
  'activity': [
    ['activityCapabilities', [{ adapter: 'antigravity' }]],
    ['activityCapabilities', [{ adapter: 'claude' }]],
    ['activityCapabilities', [{ adapter: 'codex', lead: true }]],
    ['activityCapabilities', [{ adapter: 'codex', lead: false }]],
    ['activityCapabilities', [{ adapter: 'api' }]],
    ['activityCapabilities', [{}]],
    ['activityMessages', ['Worker connected. Starting the assigned task.']],
    ['activityMessages', ['Test output: 5 passed']],
    ['activityMessages', ['Hit an error: fatal error occurred']],
    ['activityMessages', ['']],
    ['interpretActivity', ['Worker connected. Starting the assigned task.', null, true]],
    ['interpretActivity', ['Worker connected. Starting the assigned task.', null, false]],
    ['interpretActivity', ['', null, true]],
    ['interpretActivity', ['', null, false]],
    ['workerReport', ['{"type":"result","result":"Final output text"}']],
    ['workerReport', ['{"item":{"type":"agent_message","text":"Agent response text"}}']],
    ['workerReport', ['invalid json text']],
    ['activityEvents', ['{"type":"thread.started"}\n{"item":{"type":"command_execution","command":"dotnet test"}}']],
    ['activityEvents', ['{"event":"init"}\n{"step_update":{"step_type":"tool","tool_name":"read_file","parameters":{"path":"foo.txt"}}}']]
  ],
  'activity-sessions': [
    ['assignmentSession', [{ projectId: 'p1', row: { id: 'p1' }, part: { projectId: 'p1', subTaskId: 's1' }, execution: {} }]],
    ['assignmentSession', [{ projectId: 'p1', row: { id: 'p1', megaTaskId: 'm1' }, part: { projectId: 'p1', subTaskId: 's1', megaTaskId: 'm1', role: 'worker', providerId: 'codex', cli: 'gpt-5' }, execution: { megaTasks: [{ id: 'm1', projectId: 'p1' }], subTasks: [{ id: 's1', megaTaskId: 'm1' }] } }]],
    ['assignmentSession', [{ projectId: 'p2', part: { projectId: 'p1' } }]],
    ['assignmentSession', [{ projectId: null }]]
  ],
  'provider-runtime': [
    ['providerEnvironment', ['claude', { FOO: 'bar' }, 'win32']],
    ['providerEnvironment', ['codex', { FOO: 'bar' }, 'linux']],
    ['providerEnvironment', ['grok', { A: '1' }, 'win32']],
    ['accountEnvironment', ['claude', { settingsFolder: 'C:\\custom\\claude' }, { CLAUDE_CODE_OAUTH_TOKEN: 'token', ANTHROPIC_API_KEY: 'key', KEEP: 'val' }, 'win32']],
    ['accountEnvironment', ['codex', { settingsFolder: 'C:\\custom\\codex' }, { OPENAI_API_KEY: 'key', KEEP: 'val' }, 'linux']],
    ['accountEnvironment', ['claude', null, { KEEP: 'val' }, 'linux']],
    ['launchSpec', [{ provider: 'claude', adapter: 'claude', cli: 'claude-3-5-sonnet', policy: { alwaysApprove: 'bad' } }, 'C:\\proj', 'prompt']],
    ['launchSpec', [{ provider: 'claude', adapter: 'claude', cli: 'claude-3-5-sonnet', policy: { alwaysApprove: false } }, 'C:\\proj', 'prompt', false, true]],
    ['launchSpec', [{ provider: 'antigravity', adapter: 'antigravity', cli: 'gemini-2.0', policy: { alwaysApprove: false } }, 'C:\\proj', 'prompt']],
    ['launchSpec', [{ provider: 'claude', adapter: 'claude', cli: 'claude-3-5-sonnet', policy: { alwaysApprove: false } }, 'C:\\proj', 'prompt', false, false]]
  ],
  'time-left': [
    ['roleOf', [{ role: 'build' }]],
    ['roleOf', [{ role: 'reviewer' }]],
    ['roleOf', [{ type: 'AUDIT' }]],
    ['roleOf', [{ type: 'RESEARCH' }]],
    ['roleOf', [{ title: 'Verify the work independently' }]],
    ['roleOf', [null]],
    ['stepRecord', [{ role: 'build', model: 'gpt-5', startedAt: '2026-11-23T10:00:00Z', endedAt: '2026-11-23T10:20:00Z', status: 'COMPLETE', files: 3 }]],
    ['stepRecord', [{ role: 'verify', model: 'claude-3-5', startedAt: '2026-11-23T10:00:00Z', endedAt: '2026-11-23T10:05:00Z', status: 'FAILED' }]],
    ['stepRecord', [{ role: 'build', modelChange: true }]],
    ['stepRecord', [{}]],
    ['parseHistory', ['{"role":"Build","model":"m1","files":1,"minutes":10,"outcome":"done"}\n{"role":"Verify","model":"m2","files":0,"minutes":5,"outcome":"failed"}']],
    ['parseHistory', ['']],
    ['parseHistory', [null]],
    ['historyText', [[{ role: 'Build', model: 'm1', files: 1, minutes: 10, outcome: 'done' }]]],
    ['historyText', [[]]],
    ['historyText', [null]],
    ['estimate', [{ steps: [{ id: 's1', status: 'RUNNING', startedAt: '2026-11-23T10:00:00Z', role: 'Build' }], history: [{ role: 'Build', minutes: 20 }], now: '2026-11-23T10:05:00Z' }]],
    ['estimate', [{ steps: [{ id: 's1', status: 'COMPLETE' }] }]],
    ['estimate', [{ steps: [] }]]
  ],
  'phone-voice': [
    ['normalizeLanguage', ['en']],
    ['normalizeLanguage', ['it']],
    ['normalizeLanguage', ['en-US']],
    ['normalizeLanguage', ['it_IT']],
    ['normalizeLanguage', ['inglese']],
    ['normalizeLanguage', ['italiano']],
    ['normalizeLanguage', ['fr']],
    ['normalizeLanguage', [null]],
    ['languageOrDefault', ['it']],
    ['languageOrDefault', ['es']],
    ['languageOrDefault', [null]],
    ['say', ['en', 'pairing']],
    ['say', ['it', 'pairing']],
    ['say', ['en', 'crewStopped', { name: 'My Project' }]],
    ['say', ['it', 'crewStopped', { name: 'Mio Progetto' }]],
    ['say', ['en', 'unknownKey']],
    ['telegramAlertLabel', ['en', 'done']],
    ['telegramAlertLabel', ['it', 'done']],
    ['telegramAlertLabel', ['en', 'unknown']],
    ['plainAlert', ['en', 'done']],
    ['plainAlert', ['it', 'done']],
    ['plainAlert', ['en', 'unknown']],
    ['stateLabel', ['en', 'Working']],
    ['stateLabel', ['it', 'Working']],
    ['stateLabel', ['en', 'Other']],
    ['isDetailsRequest', ['/details']],
    ['isDetailsRequest', ['/dettagli']],
    ['isDetailsRequest', ['technical note']],
    ['isDetailsRequest', ['hello']],
    ['fitTelegram', ['short text']],
    ['fitTelegram', ['a'.repeat(4100)]],
    ['reasonLine', ['en', 'timeout']],
    ['reasonLine', ['it', 'timeout']],
    ['reasonLine', ['en', 'unreachable']]
  ],
  'step-outcomes': [
    ['canonicalRole', ['build']],
    ['canonicalRole', ['verifier']],
    ['canonicalRole', ['unknown']],
    ['canonicalRole', [null]],
    ['canonicalClass', ['premium']],
    ['canonicalClass', ['default']],
    ['canonicalClass', ['light']],
    ['canonicalClass', ['unknown']],
    ['canonicalClass', [null]],
    ['resolveModelMeta', [{ provider: 'claude', cli: 'claude-3-5-sonnet' }]],
    ['resolveModelMeta', [{ provider: 'codex', cli: 'gpt-4o' }]],
    ['resolveModelMeta', ['claude-3-opus']],
    ['resolveModelMeta', ['gpt-4o-mini']],
    ['stepOutcomeRecord', [{ model: 'claude-3-5-sonnet', role: 'build', accepted: true }]],
    ['stepOutcomeRecord', [{ model: 'gpt-4o', role: 'verify', fixed: 2 }]],
    ['stepOutcomeRecord', [{ model: 'invalid' }]],
    ['stepOutcomeRecord', [{}]],
    ['outcomesSummary', [[{ model: 'm1', accepted: true, fixed: 0 }, { model: 'm1', accepted: false, fixed: 1 }, { model: 'm2', accepted: true, fixed: 0 }]]],
    ['outcomesSummary', [[]]],
    ['outcomesSummary', [null]],
    ['summaryText', [{ steps: 3, accepted: 2, fixed: 1 }]],
    ['summaryText', [null]]
  ],
  planUsage: [
    ['unreadPlan', ['ChatGPT', 'Usage not loaded yet.']],
    ['unreadPlan', ['Grok', null]],
    ['unreadPlan', ['Gemini', 'Custom failure']],
    ['emptyPlans', []],
    ['windowOf', [{ used_percent: 50, reset_at: 1700000000 }, '5-hour']],
    ['windowOf', [{ used_percent: 0, reset_at: '2026-11-24T12:00:00Z' }, 'weekly']],
    ['windowOf', [{ used_percent: 100, reset_after_seconds: 3600 }, '5-hour']],
    ['windowOf', [{ used_percent: 120 }, '5-hour']],
    ['windowOf', [{ used_percent: -5 }, '5-hour']],
    ['windowOf', [{ used_percent: 'not-number' }, '5-hour']],
    ['windowOf', [null, '5-hour']],
    ['windowOf', [{}, 'weekly']],
    ['planExhausted', [[{ id: 'ChatGPT', exhausted: true }], 'ChatGPT']],
    ['planExhausted', [[{ id: 'ChatGPT', exhausted: false }], 'ChatGPT']],
    ['planExhausted', [[{ id: 'Grok', exhausted: true }], 'ChatGPT']],
    ['planExhausted', [[], 'ChatGPT']],
    ['planExhausted', [null, 'ChatGPT']],
    ['parseGrokCredits', [{ config: { creditUsagePercent: 40, currentPeriod: { end: '2026-11-24T12:00:00Z' } } }]],
    ['parseGrokCredits', [{ config: { creditUsagePercent: 0 } }]],
    ['parseGrokCredits', [{ config: { creditUsagePercent: 100 } }]],
    ['parseGrokCredits', [{ config: { creditUsagePercent: 150 } }]],
    ['parseGrokCredits', [{ config: {} }]],
    ['parseGrokCredits', [null]],
    ['parseGrokCredits', [{}]],
    ['parseChatGptUsage', [{ rate_limit: { primary_window: { used_percent: 30, reset_at: 1700000000 }, secondary_window: { used_percent: 10, reset_at: 1700000000 } } }]],
    ['parseChatGptUsage', [{ rate_limit: { primary_window: { used_percent: 90, reset_at: 1700000000 } }, plan_type: 'team' }]],
    ['parseChatGptUsage', [{ rate_limit: { primary_window: { used_percent: 100, reset_at: 1700000000 }, limit_reached: true } }]],
    ['parseChatGptUsage', [{ rate_limit: {} }]],
    ['parseChatGptUsage', [null]],
    ['parseChatGptUsage', [{}]]
  ],
  'plan-file': [
    ['isNoise', ['?? .planwharf/plan.md']],
    ['isNoise', ['?? cache/__pycache__/file.pyc']],
    ['isNoise', ['M src/main.rs']],
    ['isNoise', ['']],
    ['dirtyMessage', [['file1.txt', 'file2.txt']]],
    ['dirtyMessage', [[]]],
    ['namedBranch', ['Fix login on `feat/auth`']],
    ['namedBranch', ['Implement work on branch `fix/issue-12` today']],
    ['namedBranch', ['No branch here']],
    ['namedBranch', ['']],
    ['namedBranch', [null]],
    ['branchName', ['Fix login', null]],
    ['branchName', ['Fix login', 'custom-branch']],
    ['branchName', ['Implement job 26 on `feat/gate`', null]],
    ['branchName', [null, null]],
    ['pullRequestTitle', ['Implement job 26 on `feat/gate`', ['Step 1', 'Step 2']]],
    ['pullRequestTitle', ['Simple goal', []]],
    ['pullRequestTitle', ['', null]],
    ['crewSection', [{ leadLabel: 'Lead GPT-4o' }, [{ id: 'gpt-4o', label: 'GPT-4o', provider: 'codex' }]]],
    ['crewSection', [{ leadLabel: 'Lead', subTasks: [{ id: 'build', title: 'Build the feature', assignedModelId: 'm2' }, { id: 'verify', title: 'Verify the work independently', assignedModelId: 'm1' }], results: [{ subTaskId: 'build', modelId: 'm1' }, { subTaskId: 'build', modelId: 'm2' }] }, [{ id: 'm1', label: 'Model One' }, { id: 'm2', label: 'Model Two' }]]],
    ['crewSection', [{ leadLabel: 'Lead Claude' }, []]],
    ['crewSection', []],
    ['shortGoal', ['Fix login bug in auth system']],
    ['shortGoal', ['A very long goal with special characters !@#$% and punctuation...']],
    ['shortGoal', ['']],
    ['shortGoal', [null]],
    ['parse', ['# Work plan\n\n## Goal\n\nFix it\n\n## Base\n\norigin/main 1234567\n\n## Branch\n\nfeat/fix\n\n## Steps\n\n- [ ] Step 1\n- [x] Step 2 — 0045080\n\n## Rules\n\nNo push\n\n## Done\n\nFinished']],
    ['parse', ['# Work plan\r\n\r\n## Goal\r\n\r\nCRLF Goal\r\n\r\n## Base\r\n\r\nbase\r\n\r\n## Branch\r\n\r\nbranch\r\n\r\n## Steps\r\n\r\n- [ ] Step 1\r\n\r\n## Rules\r\n\r\nRules\r\n\r\n## Done\r\n\r\n']],
    ['parse', ['']],
    ['parse', [null]]
  ],
  phone: [
    ['alertForStatus', ['idle', 'running']],
    ['alertForStatus', ['running', 'done']],
    ['alertForStatus', ['running', 'failed']],
    ['alertForStatus', ['running', 'blocked']],
    ['alertForStatus', ['running', 'running']],
    ['alertForStatus', [null, 'running']],
    ['alertForStep', [{ index: 0, title: 'Old' }, { index: 1, title: 'New' }]],
    ['alertForStep', [{ index: 1, title: 'Same' }, { index: 1, title: 'Same' }]],
    ['alertForStep', [null, { index: 0, title: 'Start' }]],
    ['alertForStep', [null, null]],
    ['crewOfProject', [{ running: true, models: [{ id: 'm1', role: 'build' }] }, [{ id: 'm1', label: 'Model 1', provider: 'claude' }], {}]],
    ['crewOfProject', [null, null, null]],
    ['escapeHtml', ['<script>alert("xss")</script> & "quotes"']],
    ['escapeHtml', ['Clean text']],
    ['escapeHtml', ['']],
    ['escapeHtml', [null]],
    ['formatProgressBar', [2, 5]],
    ['formatProgressBar', [0, 5]],
    ['formatProgressBar', [5, 5]],
    ['formatProgressBar', [null, null]],
    ['sanitizeText', ['Normal text']],
    ['sanitizeText', ['Text with <HTML> & entities']],
    ['sanitizeText', ['']],
    ['sanitizeText', [null]],
    ['sanitizeGoalTitle', ['A clear goal']],
    ['sanitizeGoalTitle', ['Goal with <b>tags</b> & "quotes"']],
    ['sanitizeGoalTitle', ['']],
    ['formatDuration', [12.5]],
    ['formatDuration', [0.2]],
    ['formatDuration', [65]],
    ['formatDuration', [null]],
    ['maskToken', ['1234567890:ABC-DEF1234ghIkl-zyx57W2v1u123ew11']],
    ['maskToken', ['short']],
    ['maskToken', ['']],
    ['maskToken', [null]],
    ['formatAlertMessage', [{ status: 'done', goal: 'Fix login', language: 'en', projectName: 'MyProject', duration: 15 }]],
    ['formatAlertMessage', [{ status: 'failed', goal: 'Deploy', language: 'it', projectName: 'Progetto', error: 'Build error' }]],
    ['formatAlertMessage', [{ status: 'blocked', goal: 'Review', language: 'en', reason: 'Waiting for decision' }]],
    ['formatTelegramAlertMessage', [{ status: 'done', goal: 'Fix login', language: 'en', projectName: 'MyProject', duration: 15 }]],
    ['formatTelegramAlertMessage', [{ status: 'failed', goal: 'Deploy', language: 'it', projectName: 'Progetto', error: 'Build error' }]],
    ['formatStatusMessage', [[{ name: 'P1', status: 'running', goal: 'Goal 1', step: 2, totalSteps: 5 }], 'en']],
    ['formatStatusMessage', [[{ name: 'P1', status: 'idle', goal: 'Idle project' }], 'it']],
    ['formatStatusMessage', [[], 'en']],
    ['matchProject', [[{ id: 'p1', name: 'Alpha' }, { id: 'p2', name: 'Beta' }], 'alpha']],
    ['matchProject', [[{ id: 'p1', name: 'Alpha' }, { id: 'p2', name: 'Alpha Two' }], 'alpha']],
    ['matchProject', [[{ id: 'p1', name: 'Alpha' }, { id: 'p2', name: 'Beta' }], 'gamma']],
    ['matchProject', [[], 'query']],
    ['matchProject', [[{ id: 'p1', name: 'Alpha' }], '']]
  ],
  'phone-ollama': [
    ['defaultOllamaSettings', []],
    ['normalizeOllamaSettings', [{ enabled: true, url: 'http://127.0.0.1:11434', model: 'qwen2.5:3b' }]],
    ['normalizeOllamaSettings', [{ url: 'http://localhost:11434/subpath' }]],
    ['normalizeOllamaSettings', [{ url: 'http://remote.host:11434', model: 'invalid model!!' }]],
    ['normalizeOllamaSettings', [null]],
    ['normalizeOllamaSettings', [{}]],
    ['escapeHtml', ['<b>Bold</b> & "entities"']],
    ['escapeHtml', ['Plain']],
    ['escapeHtml', ['']],
    ['withCaptain', ['System ready.', 'en']],
    ['withCaptain', ['Tutto pronto.', 'it']],
    ['withCaptain', ['', null]],
    ['isClearMemory', ['/dimentica']],
    ['isClearMemory', ['/dimentica@harbor_bot']],
    ['isClearMemory', ['/forget']],
    ['isClearMemory', ['hello']],
    ['isClearMemory', ['']],
    ['isBareConfirmation', ['SÌ!']],
    ['isBareConfirmation', ['si']],
    ['isBareConfirmation', ['YES']],
    ['isBareConfirmation', ['no']],
    ['isBareConfirmation', ['maybe']],
    ['projectFacts', [[{ name: 'Proj1', goal: 'Build app', status: 'running', step: { step: 2, total: 5 }, timeLeft: { minutes: 10 } }]]],
    ['projectFacts', [[]]],
    ['projectFacts', [null]],
    ['buildReplyMessages', [{ question: 'How is it going?', projects: [{ name: 'Proj1', status: 'running', goal: 'Testing' }], history: [], language: 'en' }]],
    ['buildReplyMessages', [{ question: 'Stato?', projects: [{ name: 'Proj1', status: 'done', goal: 'Finito' }], history: [{ role: 'user', content: 'Ciao' }], language: 'it' }]],
    ['buildReplyMessages', [{ question: 'Only question' }]],
    ['buildReplyMessages', [{}]],
    ['buildReplyMessages', []],
    ['unavailableReply', ['en', 'HTTP 500']],
    ['unavailableReply', ['it', 'Timeout']],
    ['unavailableReply', [null, null]]
  ],
  'phone-handover': [
    ['handoverPlan', [{ folder: '/test/dir', sourceExecutable: '/bin/node' }]],
    ['handoverPlan', [{ installed: { path: '/bin/same' }, updatedBuild: '/bin/same', backupOk: false, tokenPresent: false }]],
    ['handoverPlan', [{}]],
    ['exchangeEvidence', [{ outbound: { text: 'outbound msg', messageId: 10, updateId: 20 }, inbound: { text: 'inbound msg' }, reply: { text: 'reply msg' } }]],
    ['exchangeEvidence', [{}]],
    ['exchangeEvidence', [{ outbound: { text: 'token 1234567890:ABC-DEF1234ghIkl-zyx57W2v1u123ew11' } }]]
  ],
  'phone-handover-live': [
    ['redact', ['telegram token: 1234567890:ABC-DEF1234ghIkl-zyx57W2v1u123ew11 in text']],
    ['redact', ['normal text without secrets']],
    ['redact', ['']],
    ['redact', [null]],
    ['projectsFromPlan', [{ schema: 2, projects: [{ id: 'p1', name: 'Alpha', status: 'running', goal: 'Do it' }] }]],
    ['projectsFromPlan', [{ schema: 2, projects: [{ id: 'p2', name: 'Beta', status: 'done', direction: 'Direct' }] }]],
    ['projectsFromPlan', [null]]
  ],
  execution: [
    ['splitSummary', [[{ id: 'sub-1', title: 'Single task', assignedModelId: 'terra' }]]],
    ['splitSummary', [[{ id: 'sub-1', title: 'Part 1', assignedModelId: 'terra' }, { id: 'sub-2', title: 'Part 2', assignedModelId: 'sonnet' }]]],
    ['splitSummary', [[{ id: 'sub-1', title: 'Build part', assignedModelId: 'terra' }, { id: 'sub-2', title: 'Verify part', assignedModelId: 'gemini-pro', dependencies: ['sub-1'] }]]],
    ['splitSummary', [[{ id: 'sub-1', title: 'Build 1', assignedModelId: 'terra' }, { id: 'sub-2', title: 'Build 2', assignedModelId: 'sonnet' }, { id: 'sub-3', title: 'Verify both', assignedModelId: 'gemini-pro', dependencies: ['sub-1', 'sub-2'] }], 'All clear']],
    ['splitSummary', [[{ id: 'sub-1', title: 'Build 1', assignedModelId: 'terra' }, { id: 'sub-2', title: 'Build 2', assignedModelId: 'sonnet' }, { id: 'sub-3', title: 'Verify 1 only', assignedModelId: 'gemini-pro', dependencies: ['sub-1'] }], 'Partial verification']],
    ['noVerdictPlan', [0]],
    ['noVerdictPlan', [1]],
    ['noVerdictPlan', [2]],
    ['noVerdictPlan', [3]],
    ['noVerdictPlan', [-1]],
    ['noVerdictPlan', [0.5]],
    ['noVerdictPlan', [1.5]],
    ['noVerdictPlan', [10]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: ['src'], names: ['src/file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 1, scope: ['src'], names: ['src/file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '0123456789abcdef0123456789abcdef01234567', exitCode: 0, scope: ['src'], names: ['src/file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: '', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: ['src'], names: ['src/file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: 'not-a-valid-hex-hash', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: ['src'], names: ['src/file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: 'short', exitCode: 0, scope: ['src'], names: ['src/file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: ['src'], names: [] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: ['src'], names: ['src/file.js', 'other/file.txt'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: [], names: ['any/path.txt', 'other/file.txt'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef01234567', after: '123456789abcdef0123456789abcdef012345678', exitCode: 0, scope: ['src\\nested'], names: ['src\\nested\\file.js'] }]],
    ['deliveredWithoutVerdict', [{ folder: 'C:/repo', before: '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef', after: 'abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789', exitCode: 0, scope: ['docs'], names: ['docs/README.md'] }]]
  ],
  'orchestrator-order-parsing': [
    ['parseOrder', ['Turn off Luna', { models: [{ id: 'luna', label: 'Luna' }], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['Turn on luna', { models: [{ id: 'luna', label: 'Luna' }], projects: [] }, null]],
    ['parseOrder', ['Use Grok as orchestrator', { models: [{ id: 'grok', label: 'Grok' }], projects: [] }, null]],
    ['parseOrder', ['Stop Alpha', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, null]],
    ['parseOrder', ['riprendi', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['approva', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['avvia', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['mode safe', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['modalità ultra sicura', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['mode talk', { models: [], projects: [{ id: 'p1', name: 'Alpha' }] }, 'p1']],
    ['parseOrder', ['Switch to automatic', { models: [], projects: [] }, null]],
    ['parseOrder', ['Switch to manual', { models: [], projects: [] }, null]],
    ['parseOrder', ['mode pipeline', { models: [], projects: [] }, null]],
    ['parseOrder', ['Do not resume Fixture alpha', { models: [], projects: [{ id: 'p1', name: 'Fixture alpha' }] }, 'p1']],
    ['parseOrder', ['What is Luna doing?', { models: [{ id: 'luna', label: 'Luna' }], projects: [] }, null]],
    ['parseOrder', ['', null, null]],
    ['parseOrder', [null, null, null]],
    ...[
      'do it', 'fallo', 'procedi', 'smista', 'go',
      'implement the auth parser', 'implementa il sistema', 'dispatch workers',
      'metti i worker', 'dai il lavoro', 'vai avanti con la build',
      'fix the failing test', 'sistema il bug',
      'What is the current status?', 'How far did we get?', 'Tell me about the architecture',
      'Can we discuss the plan?', '', null
    ].map(question => ['looksLikeWork', [question]]),
    ...[
      'Is the build passing?', 'status', 'quota', 'how far along are we',
      'what is the lead model', 'come sta il worker', 'perché fallisce',
      'quanto manca', 'quale modello è attivo',
      'Proceed with implementation', 'Fallo subito', 'Start the job',
      'Turn off Luna', '', null
    ].map(question => ['looksLikeQuestion', [question]])
  ],
  'orchestrator-team-models': [
    // 1. normalizeModels
    ['normalizeModels', [[]]],
    ['normalizeModels', [null]],
    ['normalizeModels', [[null, 123, 'bad', {}, { label: 'No ID' }, { id: 456 }]]],
    ['normalizeModels', [[{ id: 'legacy-1', plan: 'chatgpt' }, { id: 'legacy-2', label: 'Custom Label', plan: 'claude', enabled: true }]]],
    ['normalizeModels', [[{ id: 'terra', label: 'Terra', provider: 'codex', cli: 'gpt-5', cost: 2, limit: 100, enabled: true }]]],
    ['normalizeModels', [[{ id: 'sonnet', label: 'Sonnet', provider: 'claude', cli: 'claude-3-5-sonnet', kind: 'premium', cost: 0, limit: 10 }]]],
    ['normalizeModels', [[{ id: 'local-ollama', label: 'Llama', provider: 'ollama', cli: 'llama3:8b', kind: 'local' }]]],
    ['normalizeModels', [[{ id: 'bad-route', provider: 'nonexistent-adapter', cli: 'some-cli', enabled: true }]]],
    ['normalizeModels', [[{ id: 'bad-cli', provider: 'codex', cli: '', enabled: true }]]],
    ['normalizeModels', [[{ id: 'cat-ex', catalogExcluded: true, enabled: true }]]],

    // 2. usageOf
    ['usageOf', [{ used: 0, limit: 50 }]],
    ['usageOf', [{ used: 15, limit: 50 }]],
    ['usageOf', [{ used: 50, limit: 50 }]],
    ['usageOf', [{ used: 60, limit: 50 }]],
    ['usageOf', [{ used: 10, limit: 100, remaining: 85 }]],
    ['usageOf', [{ used: 5, limit: 0 }]],
    ['usageOf', [{ used: 25, limit: 100 }]],
    ['usageOf', [{}]],
    ['usageOf', [null]],

    // 3. leadTier
    ['leadTier', [{ id: 'claude-3-7-fable' }]],
    ['leadTier', [{ label: 'Anthropic Fable 1.0' }]],
    ['leadTier', [{ id: 'claude-3-opus' }]],
    ['leadTier', [{ cli: 'claude-opus-4-6-thinking' }]],
    ['leadTier', [{ id: 'claude-3-5-sonnet' }]],
    ['leadTier', [{ label: 'Sonnet 3.5' }]],
    ['leadTier', [{ id: 'claude-3-5-haiku' }]],
    ['leadTier', [{ id: 'haiku-latest' }]],
    ['leadTier', [{ id: 'flagship-gpt', kind: 'premium' }]],
    ['leadTier', [{ id: 'local-llama', kind: 'local' }]],
    ['leadTier', [{ id: 'std-model', kind: 'default' }]],
    ['leadTier', [{ id: 'gemini-flash' }]],
    ['leadTier', [{ id: 'gpt-4o-mini' }]],
    ['leadTier', [{ id: 'luna-v1' }]],
    ['leadTier', [{ id: 'custom-lite-model' }]],
    ['leadTier', [{ id: 'deepseek-nano' }]],
    ['leadTier', [{ id: 'unknown-model' }]],
    ['leadTier', [null]],

    // 4. leadRanking
    ['leadRanking', [leadCandidatesA, plans]],
    ['leadRanking', [leadCandidatesA, []]],
    ['leadRanking', [leadCandidatesB, plans]],
    ['leadRanking', [[], plans]],
    ['leadRanking', [null, plans]],

    // 5. leadQuality
    ['leadQuality', [leadCandidatesA]],
    ['leadQuality', [leadCandidatesB]],
    ['leadQuality', [[]]],
    ['leadQuality', [null]],

    // 6. pickWorker
    ['pickWorker', [crew, { job: { title: 'Implement feature', detail: 'Write code' } }]],
    ['pickWorker', [crew, { job: { title: 'Verify tests', detail: 'Check edge cases', kind: 'verify' } }]],
    ['pickWorker', [crew, { job: { title: 'Review pull request', detail: 'Check standards', kind: 'review' } }]],
    ['pickWorker', [crew, { job: { title: 'Research docs', detail: 'Find facts', kind: 'research' } }]],
    ['pickWorker', [crew, { job: { title: 'Build', detail: 'code' }, skip: ['terra'] }]],
    ['pickWorker', [crew, { job: { title: 'Build', detail: 'code' }, savingMode: 'safe' }]],
    ['pickWorker', [crew, { job: { title: 'Build', detail: 'code' }, usage: { terra: { used: 100 } } }]],
    ['pickWorker', [[], { job: { title: 'Build', detail: 'code' } }]],

    // 7. pickWorkers
    ['pickWorkers', [crew, { job: { title: 'Build engine', detail: 'Write tests' } }]],
    ['pickWorkers', [diverseCrew, { job: { title: 'Build feature', detail: 'Code' }, count: 2 }]],
    ['pickWorkers', [crew, { job: { title: 'Build feature', detail: 'Code' }, count: 1 }]],
    ['pickWorkers', [crew, { job: { title: 'Review changes', detail: 'Audit code', kind: 'review' }, count: 3 }]],
    ['pickWorkers', [crew, { job: { title: 'Build feature', detail: 'Code' }, usage: { terra: { used: 49 } }, count: 2 }]],
    ['pickWorkers', [crew, { job: { title: 'Build feature', detail: 'Code' }, savingMode: 'ultra-safe' }]],

    // 8. dispatchPlan
    ['dispatchPlan', [[{ id: 'terra', label: 'Terra', why: 'selected' }], { title: 'Build' }, null]],
    ['dispatchPlan', [[{ id: 'terra', label: 'Terra' }, { id: 'sonnet', label: 'Sonnet' }], { title: 'Build' }, { dirty: [] }]],
    ['dispatchPlan', [[{ id: 'terra', label: 'Terra' }, { id: 'sonnet', label: 'Sonnet' }], { title: 'Build' }, { dirty: ['file.cs', 'test.cs'] }]],
    ['dispatchPlan', [[{ id: 'terra', label: 'Terra' }, { id: 'sonnet', label: 'Sonnet' }, { id: 'gemini', label: 'Gemini' }], { title: 'Review', kind: 'review' }, null]],
    ['dispatchPlan', [[], { title: 'Build' }, null]],

    // 9. allowedBySavingMode
    ['allowedBySavingMode', ['normal', { id: 'fable', leadTier: 0 }]],
    ['allowedBySavingMode', ['normale', { id: 'sonnet', leadTier: 1 }]],
    ['allowedBySavingMode', ['normal', { id: 'haiku', leadTier: 2 }]],
    ['allowedBySavingMode', ['safe', { id: 'fable', leadTier: 0 }]],
    ['allowedBySavingMode', ['sicura', { id: 'sonnet', leadTier: 1 }]],
    ['allowedBySavingMode', ['safe', { id: 'haiku', leadTier: 2 }]],
    ['allowedBySavingMode', ['ultra-safe', { id: 'fable', leadTier: 0 }]],
    ['allowedBySavingMode', ['ultra sicura', { id: 'sonnet', leadTier: 1 }]],
    ['allowedBySavingMode', ['ultra-safe', { id: 'haiku', leadTier: 2 }]],
    ['allowedBySavingMode', ['safe', { id: 'fable', leadTier: 0, weightSource: 'team', weight: 'light' }]],
    ['allowedBySavingMode', ['ultra-safe', { id: 'sonnet', leadTier: 1, weightSource: 'team', weight: 'light' }]],
    ['allowedBySavingMode', ['safe', { id: 'haiku', leadTier: 2, weightSource: 'team', weight: 'premium' }]],
    ['allowedBySavingMode', ['safe', { id: 'fable', leadTier: 0, weightSource: 'lineup', weight: 'light' }]],
    ['allowedBySavingMode', ['', { id: 'fable', leadTier: 0 }]],
    ['allowedBySavingMode', [null, { id: 'fable', leadTier: 0 }]],

    // 10. modelClass
    ['modelClass', [{ id: 'm1' }, 0]],
    ['modelClass', [{ id: 'm2' }, 1]],
    ['modelClass', [{ id: 'm3' }, 2]],
    ['modelClass', [{ id: 'm1', leadTier: 0 }]],
    ['modelClass', [{ id: 'm2', leadTier: 1 }]],
    ['modelClass', [{ id: 'm3', leadTier: 2 }]],
    ['modelClass', [{ id: 'm1', kind: 'premium' }]],
    ['modelClass', [{ id: 'm2', kind: 'default' }]],
    ['modelClass', [{ id: 'm3', kind: 'local' }]],
    ['modelClass', [{ id: 'm1', leadTier: 0, weightSource: 'team', weight: 'light' }, 0]],
    ['modelClass', [{ id: 'm2', leadTier: 2, weightSource: 'team', weight: 'premium' }, 2]],
    ['modelClass', [{ id: 'm3', leadTier: 0, weightSource: 'team', weight: 'default' }, 0]],
    ['modelClass', [{ id: 'm1', leadTier: 0, weightSource: 'lineup', weight: 'light' }, 0]],
    ['modelClass', [null]]
  ],
  'orchestrator-job-building': [
    // 1. sliceCount matrix: kinds ('hard', 'build', 'inspect') x dirty (0, 3, 4, 12) x eligible (0, 1, 2, 5)
    ...(() => {
      const kinds = [
        { title: 'Hard architecture changes', detail: 'complex algorithm escalation' },
        { title: 'Implement feature', detail: 'write code and tests' },
        { title: 'Review repository status', detail: 'check notes' }
      ];
      const dirtyCounts = [
        [],
        ['src/a.js', 'src/b.js', 'src/c.js'],
        ['src/a.js', 'src/b.js', 'src/c.js', 'src/d.js'],
        ['1.js', '2.js', '3.js', '4.js', '5.js', '6.js', '7.js', '8.js', '9.js', '10.js', '11.js', '12.js']
      ];
      const eligibleCounts = [0, 1, 2, 5];
      const res = [];
      for (const job of kinds) {
        for (const dirty of dirtyCounts) {
          for (const eligible of eligibleCounts) {
            res.push(['sliceCount', [job, { dirty }, eligible]]);
          }
        }
      }
      res.push(['sliceCount', [null, null, 2]]);
      res.push(['sliceCount', [{ title: 'Complex task' }, null, 0]]);
      res.push(['sliceCount', [null, { dirty: ['1', '2', '3', '4'] }, 3]]);
      res.push(['sliceCount', [{ title: 'Build task' }, { dirty: null }, -1]]);
      res.push(['sliceCount', [{ title: 'Inspect' }, { dirty: [] }, 10]]);
      return res;
    })(),

    // 2. planLines matrix: git (clean, dirty, absent) x dispatch (single, multi) x timing (discretion, deadline) x active (true, false) x checkpoint (present, null)
    ...(() => {
      const gitScenarios = [
        { facts: { git: true, lastCommit: '9a8b7c6', branch: 'main', dirty: [] }, name: 'git-clean' },
        { facts: { git: true, head: '1a2b3c4', branch: 'feature/login', dirty: ['app.js', 'app.test.js'] }, name: 'git-dirty' },
        { facts: { git: false, fileCount: 17 }, name: 'git-absent' }
      ];
      const dispatchScenarios = [
        { dispatch: [{ modelId: 'm1', label: 'Gemini Pro', slice: 'Whole job' }], name: 'single' },
        { dispatch: [{ modelId: 'm1', label: 'Builder', slice: 'Implement code' }, { modelId: 'm2', label: 'Verifier', slice: 'Verify tests' }], name: 'multi' }
      ];
      const timingScenarios = [
        { job: { timing: 'discretion' }, name: 'discretion' },
        { job: { timing: 'deadline', deadlineAt: '2026-12-01T15:00:00.000Z' }, name: 'deadline' }
      ];
      const activeScenarios = [true, false];
      const checkpointScenarios = [
        { checkpoint: { path: '.planwharf/checkpoints/cp-42.json' }, name: 'with-checkpoint' },
        { checkpoint: null, name: 'no-checkpoint' }
      ];

      const res = [];
      const shown = { id: 'm1', label: 'Gemini Flash', why: 'fast' };
      const baseItem = {
        project: { id: 'p1', name: 'Planwharf', folder: 'C:/repos/test-project' },
        revisions: [{ id: 'rev-1', direction: 'Build the parity test suite' }],
        tasks: []
      };

      for (const git of gitScenarios) {
        for (const disp of dispatchScenarios) {
          for (const timing of timingScenarios) {
            for (const active of activeScenarios) {
              for (const cp of checkpointScenarios) {
                const extras = {
                  dispatch: disp.dispatch,
                  job: timing.job,
                  checkpoint: cp.checkpoint
                };
                res.push(['planLines', [baseItem, git.facts, shown, active, extras]]);
              }
            }
          }
        }
      }

      // Edge cases for planLines
      res.push(['planLines', [
        { project: { id: 'p1', name: 'Proj', folder: '/app' }, revisions: [] },
        { git: true, lastCommit: 'c1' },
        { id: 'm1', label: 'Flash' },
        true,
        null
      ]]);
      res.push(['planLines', [
        { project: { id: 'p1', name: 'Proj', folder: '/app' }, revisions: [{ direction: 'A'.repeat(250) }] },
        { git: true, lastCommit: 'c1' },
        { id: 'm1' },
        false,
        {}
      ]]);
      res.push(['planLines', [
        {
          project: { id: 'p1', name: 'Proj', folder: '/app' },
          revisions: [{ direction: 'Test' }],
          tasks: [{ status: 'running', timing: 'deadline', deadlineAt: '2026-12-31T23:59:59Z' }]
        },
        { git: false, fileCount: 0 },
        { id: 'm2', label: 'Sonnet', why: '' },
        true,
        { dispatch: [] }
      ]]);
      res.push(['planLines', [
        { project: { id: 'p1', name: 'Proj', folder: '/app' }, revisions: [{ direction: 'Multi' }] },
        { git: true, head: 'h1', dirty: [] },
        { id: 'm1', label: 'Model1' },
        false,
        {
          dispatch: [
            { modelId: 'm1', label: '', slice: 'Slice 1' },
            { modelId: 'm2', label: 'Verifier', slice: 'Slice 2' },
            { modelId: 'm3', label: 'Reviewer', slice: 'Slice 3' }
          ]
        }
      ]]);
      res.push(['planLines', [
        { project: { id: 'p1', name: 'Proj', folder: '/app' }, revisions: [{ direction: 'Branchless' }] },
        { git: true, lastCommit: 'abc', branch: null, dirty: ['file.txt'] },
        { id: 'm1', label: 'Model1', why: 'chosen' },
        true,
        null
      ]]);

      return res;
    })()
  ],
  'orchestrator-lead-questions': [
    // 1. parseGrillQuestions
    ['parseGrillQuestions', ['["Is this the calculator slice? Recommended: yes."]']],
    ['parseGrillQuestions', ['[" Do it? ", false, null, 42, "  Keep existing tests? Recommended: yes  "]']],
    ['parseGrillQuestions', ['[{"title":"Goal","body":"Why?","recommend":"yes"},{"question":"Scope","recommended":"minimal"}]']],
    ['parseGrillQuestions', ['[{"title":"Only title"},{"body":"Only body","recommend":"skip"},{"title":1e3,"recommend":true},{"title":1e-6},{"title":1e-7}]']],
    ['parseGrillQuestions', ['[{"title":"first","title":"last"}]']],
    ['parseGrillQuestions', ['Here is the interview:\n```json\n["Use native AOT? Recommended: yes", "Keep JS fallback? Recommended: yes"]\n```\nThanks.']],
    ['parseGrillQuestions', ['- First bullet? Recommended: yes\n* Second bullet?\n1. Numbered dot? Recommended: no\n2) Numbered paren?']],
    ['parseGrillQuestions', ['❓ **Q1** — 日本語?\n❓ **Q2** – Accented perché? Recommended: sì\n❓ **Q3** - Hyphenated?']],
    ['parseGrillQuestions', ['Intro statement without question mark.\nIs parity strict? Recommended: yes\nAnother statement without marker\nConfirm scope: recommended: keep minimal']],
    ['parseGrillQuestions', ['\uFEFF\u00A0- Ready?\uFEFF']],
    ['parseGrillQuestions', [JSON.stringify(Array.from({ length: 10 }, (_, i) => `Question ${i + 1}? Recommended: yes`))]],
    ['parseGrillQuestions', [Array.from({ length: 10 }, (_, i) => `${i + 1}. Markdown question ${i + 1}?`).join('\n')]],
    ['parseGrillQuestions', ['[{"title":"broken"\n- Fallback question? Recommended: yes\n- Plain statement']],
    ['parseGrillQuestions', ['']],
    ['parseGrillQuestions', ['   \n\t  ']],
    ['parseGrillQuestions', [null]],
    ['parseGrillQuestions', [42]],
    ['parseGrillQuestions', [{}]],

    // 2. grillPrompt
    ['grillPrompt', [
      {
        project: { id: 'p1', name: 'Demo', folder: '/repo/demo' },
        revisions: [{ id: 1, direction: 'Old goal' }, { id: 2, direction: 'Ship Job 50 parity' }]
      },
      {
        lastCommit: 'abc1234 Add parser',
        names: Array.from({ length: 14 }, (_, i) => `file-${i + 1}.cs`)
      },
      ['README.md', 'docs/spec.md']
    ]],
    ['grillPrompt', [{ project: { name: 'Minimal' }, revisions: [] }, null, null]],
    ['grillPrompt', [{ project: { name: 'Partial', folder: '' }, revisions: [{ direction: '' }] }, { names: [] }, []]],
    ['grillPrompt', [{ project: { name: 'DocsOnly' }, revisions: [{ direction: 'Verify' }] }, null, ['AGENTS.md']]],

    // 3. grillDone
    ['grillDone', [{ revisions: [{ mode: 'grill', title: 'R1' }] }]],
    ['grillDone', [{ revisions: [{ mode: 'manual', title: 'Grill me' }] }]],
    ['grillDone', [{ revisions: [{ mode: 'manual', title: 'Grill with the docs' }] }]],
    ['grillDone', [{ revisions: [{ mode: 'Grill', title: 'Other' }] }]],
    ['grillDone', [{ revisions: [] }]],
    ['grillDone', [null]],

    // 4. collectDocs & isDocName & listDocs
    ['collectDocs', [{ docs: [' README.md ', '', '   ', 'docs/a.md'], names: ['AGENTS.md'] }]],
    ['collectDocs', [{ docs: [null, 12, {}, true, ' x '] }]],
    ['collectDocs', [{ docs: [' ', ''], names: ['README.md'] }]],
    ['collectDocs', [{ docs: Array.from({ length: 15 }, (_, i) => `doc-${i + 1}.md`) }]],
    ['collectDocs', [{ names: ['README.md', 'README', 'AGENTS.md', 'CLAUDE.md', 'SKILL.md', 'CONTRIBUTING.md', 'docs/spec.md', 'docs\\sub\\guide.md', 'src/CLAUDE.md', 'index.js', 'package.json', 'docs/note.txt', 'src/other.md'] }]],
    ['collectDocs', [{ names: Array.from({ length: 15 }, (_, i) => `docs/d${i + 1}.md`) }]],
    ['collectDocs', [null]],
    ...['README.md', 'docs/spec.md', 'docs\\nested\\guide.md', 'src/CLAUDE.md', 'docs/plain.txt', 'index.js', '', null].map(n => ['isDocName', [n]]),
    ['listDocs', [null]],
    ['listDocs', ['']],
    ['listDocs', ['nonexistent-docs-folder-parity']],

    // 5. leadEnvelope
    ['leadEnvelope', [' plain ']],
    ['leadEnvelope', ['Line one\nLine two']],
    ['leadEnvelope', ['```plain```']],
    ['leadEnvelope', ['```json\n{"answer":"text","actions":[{"type":"dispatch_research","projectId":"p1"}]}\n```']],
    ['leadEnvelope', ['```JSON\n{"answer":"ok","actions":[]}\n```']],
    ['leadEnvelope', ['{"answer":"ok","actions":[{"type":"new","extra":{"x":1}}]}']],
    ['leadEnvelope', ['{"answer":"first","answer":"last","actions":[]}']],
    ['leadEnvelope', ['{bad}']],
    ['leadEnvelope', ['```json\n{bad}\n```']],
    ['leadEnvelope', ['{"answer":"ok","actions":[],"extra":1}']],
    ['leadEnvelope', ['{"answer":"ok"}']],
    ['leadEnvelope', ['{"answer":123,"actions":[]}']],
    ['leadEnvelope', ['{"answer":"ok","actions":{}}']],
    ['leadEnvelope', [' untrimmed ', [{ type: 'custom', unknown: 'keep' }]]],
    ['leadEnvelope', ['']],
    ['leadEnvelope', [null]],

    // 6. answerQuestion & briefRow
    ...(() => {
      const detailSnap = {
        orchestratorLabel: 'Lead',
        projects: [
          {
            id: 'x',
            name: 'Demo',
            label: 'working',
            jobTitle: 'Port parity',
            orchestratorDoing: 'Watching.',
            waitingFor: 'tests',
            next: 'review',
            workerTail: 'old\nnew',
            monitoring: { alive: 1, workers: 2, folderFiles: 7 },
            dispatch: [
              { modelId: 'model-code', label: 'Codeworker', slice: 'build', pid: 99, alive: true },
              { modelId: 'model-review', label: 'Reviewworker', slice: 'verify', activity: 'Checking.', pid: 100, alive: false }
            ]
          },
          {
            id: 'y',
            name: 'Beta',
            label: 'idle',
            workerLabel: 'Sonnet',
            monitoring: { alive: 0, workers: 1, folderFiles: null }
          }
        ],
        plans: [
          { id: 'p1', label: 'ChatGPT', readable: true, exhausted: false, pct: 30, leftPct: 70, windows: [{ name: '5-hour', pct: 30, leftPct: 70 }] },
          { id: 'p2', label: 'Grok', readable: true, exhausted: true, pct: 100, leftPct: 0, windows: [] },
          { id: 'p3', label: 'Gemini', readable: false, reason: 'Not logged in' }
        ],
        models: [
          { id: 'm1', label: 'ModelA', used: 10, limit: 50 },
          { id: 'm2', label: 'ModelB', used: -5, limit: 100 }
        ]
      };
      const noPlansSnap = {
        orchestratorLabel: 'Lead',
        projects: [{ id: 'x', name: 'Demo', label: 'idle' }],
        plans: [],
        models: detailSnap.models
      };
      const emptyPlansAndModelsSnap = {
        orchestratorLabel: 'Lead',
        projects: [{ id: 'x', name: 'Demo', label: 'idle', workerTail: '{"type":"result","result":"JSONL activity"}' }],
        plans: [],
        models: []
      };
      return [
        ['answerQuestion', ['', detailSnap, 'x']],
        ['answerQuestion', ['   ', detailSnap]],
        ['answerQuestion', [null, detailSnap]],
        ['answerQuestion', ['all projects', detailSnap]],
        ['answerQuestion', ['tutti', detailSnap]],
        ['answerQuestion', ['zzz', { orchestratorLabel: 'Lead', projects: [] }]],
        ['answerQuestion', ['zzz', detailSnap]],
        ['answerQuestion', ['Codeworker', detailSnap, 'x']],
        ['answerQuestion', ['model-review', detailSnap, 'x']],
        ['answerQuestion', ['split', detailSnap, 'x']],
        ['answerQuestion', ['split', detailSnap, 'y']],
        ['answerQuestion', ['How much quota is left?', detailSnap, 'x']],
        ['answerQuestion', ['quota', noPlansSnap, 'x']],
        ['answerQuestion', ['quota', emptyPlansAndModelsSnap, 'x']],
        ['answerQuestion', ['monitor', detailSnap, 'x']],
        ['answerQuestion', ['monitor', detailSnap, 'y']],
        ['answerQuestion', ['next', detailSnap, 'x']],
        ['answerQuestion', ['log', detailSnap, 'x']],
        ['answerQuestion', ['log', emptyPlansAndModelsSnap, 'x']],
        ['answerQuestion', ['log', detailSnap, 'y']],
        ['answerQuestion', ['summary', detailSnap, 'x']],
        ['briefRow', [detailSnap.projects[0], 'Lead']],
        ['briefRow', [detailSnap.projects[1], null]],
        ['briefRow', [null, 'Lead']]
      ];
    })(),

    // 7. packState (privacy, caps, unscoped vs focused)
    ...(() => {
      const multiSnap = {
        orchestratorLabel: 'Lead',
        orchestratorId: 'chosen',
        mode: 'plan',
        grillPropose: true,
        grillKind: 'docs',
        projects: [
          {
            id: 'alpha',
            name: 'Alpha',
            folder: null,
            github: 'owner/alpha',
            repository: { branch: 'main' },
            status: 'working',
            label: 'Working',
            goal: 'Build parity',
            stateArt: 'Ready',
            open: Array.from({ length: 14 }, (_, i) => `open-${i}`),
            analysis: 'Checked.',
            jobTitle: 'Job 50',
            plan: Array.from({ length: 18 }, (_, i) => `step-${i}`),
            checkpoint: { reason: 'pause', at: '2026-11-26T00:00:00Z', path: 'cp.json', jobTitle: 'Job 50', plan: Array.from({ length: 14 }, (_, i) => `cp-${i}`) },
            orchestratorDoing: 'Watching',
            waitingFor: 'tests',
            next: 'verify',
            savingMode: 'safe',
            facts: { git: true, fileCount: 3, lastCommit: 'c1', dirty: Array.from({ length: 14 }, (_, i) => `d-${i}`) },
            notes: ['alpha-own-note'],
            dispatch: [{ modelId: 'm1', label: 'M1', slice: 's1', pid: 42, alive: true, activity: 'Running' }],
            monitoring: { alive: 1, workers: 1, folderFiles: 3 },
            workerTail: 'line 1\nline 2'
          },
          {
            id: 'beta',
            name: 'Beta',
            folder: null,
            status: 'idle',
            jobTitle: 'PRIVATE_BETA_JOB',
            notes: ['PRIVATE_BETA_NOTE']
          }
        ],
        execution: {
          megaTasks: [{ id: 'ma', projectId: 'alpha' }, { id: 'mb', projectId: 'beta' }],
          subTasks: [{ id: 'sa', megaTaskId: 'ma' }, { id: 'sb', megaTaskId: 'mb', secret: 'PRIVATE' }],
          results: [{ megaTaskId: 'ma', status: 'COMPLETE' }, { megaTaskId: 'mb', summary: 'PRIVATE' }],
          checkpoints: [{ id: 'ca', projectId: 'alpha' }, { id: 'cb', projectId: 'beta' }]
        },
        researches: [
          { id: 'ra', projectId: 'alpha', title: 'Alpha Research' },
          { id: 'rb', projectId: 'beta', title: 'PRIVATE_BETA_RESEARCH' }
        ],
        fleet: [
          { id: 'm1', label: 'M1', role: 'worker', plan: 'p1', exhausted: false, enabled: true, roles: { worker: true }, roleEligibility: { worker: true }, lead: false }
        ],
        plans: [
          { id: 'p1', label: 'Plan 1', readable: true, exhausted: false, pct: 20, leftPct: 80, windows: [] }
        ]
      };
      return [
        ['packState', [multiSnap, multiSnap.projects[0]]],
        ['packState', [multiSnap, null]],
        ['packState', [{ grillPropose: false, projects: [] }, { id: 'p', name: 'P', savingMode: 'normal' }]],
        ['packState', [null, null]],
        ['packState', [{}, { id: 'p1', name: 'P1' }]]
      ];
    })(),

    // 8. Autonomy prompt generators (from engine/autonomy.cjs)
    ['evaluationPrompt', [{ name: 'Proj', goal: 'Goal', results: [{ status: 'COMPLETE', summary: 'ok', filesChanged: ['a.cs'], blockers: [] }], checks: { label: 'test', exitCode: 0, seconds: 2, output: 'pass' } }]],
    ['evaluationPrompt', [{ name: 'Proj', goal: null, results: [] }]],
    ['nudgePrompt', [{ name: 'Proj', goal: 'Goal', alert: { title: 'Stalled', message: 'No progress' } }]],
    ['nudgePrompt', [{ name: 'Proj', goal: null, alert: { title: 'Alert', message: 'Check' } }]],
    ['continuePrompt', [{ name: 'Proj', goal: 'Port modules', reason: 'blocked worker', results: [{ status: 'BLOCKED', summary: 'Partial', filesChanged: ['a.cs'], blockers: ['Needs test'] }] }]],
    ['continuePrompt', [{ name: 'Proj', results: [] }]],
    ['splitPrompt', [{ name: 'Proj', goal: 'Port sheet 46c', builders: [{ id: 'a', label: 'A', provider: 'p1', tier: 'light', left: 50 }], verifiers: [{ id: 'b', label: 'B', provider: 'p2', tier: 'premium' }], files: ['a.cs', 'b.cs'], said: 'x'.repeat(2600), documents: [{ path: 'docs/spec.md', text: 'Spec content', clipped: true }] }]],
    ['splitPrompt', [{ name: 'Proj', goal: null, builders: [{ id: 'a', label: 'A', provider: 'p1', tier: 'default' }], verifiers: [{ id: 'b', label: 'B', provider: 'p2', tier: 'premium', left: 80 }], files: [] }]],
    ['assignmentPrompt', [{ name: 'Proj', goal: 'Goal', slices: ['Build', 'Verify'], candidates: [[{ id: 'a', label: 'A', provider: 'p1' }], [{ id: 'b', label: 'B', provider: 'p2' }]], said: 'y'.repeat(2600) }]],
    ['assignmentPrompt', [{ name: 'Proj', goal: null, slices: ['Build'], candidates: [[{ id: 'a', label: 'A', provider: 'p1' }]] }]],
    ['strictDecisionPrompt', ['Review project.']],
    ['strictDecisionPrompt', ['Line 1\nLine 2']]
  ],

  // Job 51 pure state helpers. Inputs deliberately use absent log paths so the
  // recorder never depends on the host filesystem's size or timestamps.
  'orchestrator-state': orchestratorStateCases
};
