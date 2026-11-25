'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');

const sheets = {
  master: {
    path: 'native/porting/jobs/46-phase6-orchestrator.md',
    requirements: {
      responsibilities: [
        /createOrchestrator\(opts\)/,
        /native\/Planwharf\.Engine\/Orchestrator\.cs/,
        /composition root/i,
        /execution bridge/i,
        /ProjectHome/i,
        /AsyncLocal/i,
        /cancellation token/i,
      ],
      signatures: [
        /Orchestrator\.Create\(OrchestratorOptions options\)/,
        /public static class OrchestratorOrderParsing/,
        /public static class OrchestratorTeamModels/,
        /public static class OrchestratorJobBuilding/,
        /public static class OrchestratorLeadQuestions/,
        /public sealed class OrchestratorState/,
      ],
      boundaries: [
        /unidirectional data flow through composition root/i,
        /no peer module store imports/i,
        /never substitute requested models or fabricate completion evidence/i,
        /sole store authority/i,
      ],
      dependencies: [
        /46a-orchestrator-team-models\.md.*OrchestratorTeamModels\.cs.*Job 48/,
        /46b-orchestrator-job-building\.md.*OrchestratorJobBuilding\.cs.*Job 49/,
        /46c-orchestrator-lead-questions\.md.*OrchestratorLeadQuestions\.cs.*Job 50/,
        /46d-orchestrator-state\.md.*OrchestratorState\.cs.*Job 51/,
        /46e-orchestrator-order-parsing\.md.*OrchestratorOrderParsing\.cs.*Job 47/,
      ],
      mappedTests: [
        /`orchestrator\.test\.cjs`/,
        /`lifecycle\.test\.cjs`/,
        /`worker-snapshot\.test\.cjs`/,
        /`saving-modes\.test\.cjs`/,
        /`project-recipient\.test\.cjs`/,
        /`home-containment\.test\.cjs`/,
      ],
      parity: [
        /native\/porting\/parity\/cases\.cjs/,
        /native\/porting\/parity\/scenarios\.cjs/,
        /ScenarioParityTests/,
        /Rule 12/,
      ],
      migration: [
        /Step 1: OrderParsing \(Job 47\)/,
        /Step 2: TeamModels \(Job 48\)/,
        /Step 3: JobBuilding \(Job 49\)/,
        /Step 4: LeadQuestions \(Job 50\)/,
        /Step 5: State \(Job 51\)/,
        /Step 6: Facade Composition and Full Test Pass \(Job 52\)/,
      ],
    },
  },
  teamModels: {
    path: 'native/porting/jobs/46a-orchestrator-team-models.md',
    requirements: {
      responsibilities: [
        /normalizeModels/i,
        /usageOf/i,
        /leadTier/i,
        /leadRanking/i,
        /leadQuality/i,
        /pickWorker/i,
        /pickWorkers/i,
        /dispatchPlan/i,
        /fleetOf/i,
        /roster/i,
        /selectLead/i,
        /setLeadAllowed/i,
        /setOrchestrator/i,
        /Normal,\s*Safe,\s*(?:and\s*)?Ultra safe/i,
        /ReassignCrew/i,
      ],
      signatures: [
        /public static JsonArray NormalizeModels\(JsonNode\? models\);/,
        /public static JsonObject UsageOf\(JsonObject\? item\);/,
        /public static int LeadTier\(JsonObject\? model\);/,
        /public static JsonArray LeadRanking\(JsonArray\? list, JsonArray\? plans = null\);/,
        /public static JsonArray LeadQuality\(JsonArray\? list\);/,
        /public static JsonObject\? PickWorker\(JsonArray\? models, JsonObject\? options = null\);/,
        /public static JsonArray PickWorkers\(JsonArray\? models, JsonObject\? options = null\);/,
        /public static JsonArray DispatchPlan\(JsonArray\? workers, JsonObject\? job, JsonObject\? facts = null\);/,
        /public static JsonArray FleetOf\(JsonArray\? models, JsonArray\? projects, string\? leadId, JsonArray\? plans = null\);/,
        /public static JsonArray BuildRoster\(TeamRosterContext context\);/,
        /public static JsonObject\? SelectLead\(TeamRosterContext context\);/,
        /public static JsonObject SetLeadAllowed\(TeamRosterContext context, string id, bool allowed, string\? projectId = null\);/,
        /public static JsonObject SetOrchestrator\(TeamRosterContext context, string\? id, string\? projectId = null\);/,
        /public static bool AllowedBySavingMode\(string\? mode, JsonObject\? item\);/,
        /public static Task<JsonObject> ReassignCrew\(TeamRosterContext context, JsonObject action, JsonObject row, string by = "lead"\);/,
      ],
      boundaries: [
        /Immutable Inputs/i,
        /No Model Substitution or Fabricated Evidence/i,
        /composition root.*alone commits/i,
        /No Cross-Module Store Coupling/i,
      ],
      dependencies: [
        /composition root/i,
        /Orchestrator\.cs/i,
      ],
      mappedTests: [
        /model-roles/i,
        /model-policy/i,
        /model-routing/i,
        /saving-mode/i,
        /quota/i,
        /project-teams/i,
        /lead/i,
        /gold-model/i,
        /crew-reassign/i,
      ],
      parity: [
        /cases\.cjs/i,
        /Rule 12/i,
      ],
      migration: [
        /Job 48/i,
      ],
    },
  },
  jobBuilding: {
    path: 'native/porting/jobs/46b-orchestrator-job-building.md',
    requirements: {
      responsibilities: [
        /jobKind/i,
        /sliceCount/i,
        /dispatchPlan.*consumption/i,
        /jobFromFacts/i,
        /planLines/i,
        /decide/i,
        /planByLead/i,
        /assignByLead/i,
        /startAutoTask/i,
        /openWorkPlan/i,
        /planSteps/i,
      ],
      signatures: [
        /public static string JobKind\(JobDescriptor\? job\);/,
        /public static int SliceCount\(JobDescriptor\? job, InspectionFacts\? facts, int eligible\);/,
        /public static IReadOnlyList<DispatchPart> BuildDispatchPlan\(/,
        /public static ProposedJob JobFromFacts\(Revision\? revision, InspectionFacts\? facts\);/,
        /public static IReadOnlyList<string> PlanLines\(/,
        /public static IReadOnlyList<PlanStepEntry> PlanSteps\(/,
        /public static WorkPlanResult OpenWorkPlan\(/,
      ],
      boundaries: [
        /Project revision snapshots/i,
        /Canonical inspection facts/i,
        /Eligible crew rosters/i,
        /Timing and deadline constraints/i,
        /no direct worker launches/i,
        /composition root.*sole authority/i,
      ],
      dependencies: [
        /composition root/i,
        /Execution bridge/i,
      ],
      mappedTests: [
        /job-title/i,
        /orchestrator/i,
        /phase1/i,
        /phase2/i,
        /phase2-git/i,
        /task-completion/i,
        /always-approve/i,
        /plan-file/i,
        /saving-mode-pr/i,
      ],
      parity: [
        /sliceCount/i,
        /planLines/i,
        /cases\.cjs/i,
        /Rule 12/i,
      ],
      migration: [
        /Job 49/i,
      ],
    },
  },
  leadQuestions: {
    path: 'native/porting/jobs/46c-orchestrator-lead-questions.md',
    requirements: {
      responsibilities: [
        /listDocs/i,
        /collectDocs/i,
        /parseGrillQuestions/i,
        /grillPrompt/i,
        /grillDone/i,
        /answerQuestion/i,
        /packState/i,
        /leadEnvelope/i,
        /ask/i,
        /composeGrillRound/i,
        /askLeadDecision/i,
      ],
      signatures: [
        /public static string\[\] ListDocs\(string\? folder/,
        /public static string\[\] CollectDocs\(InspectionFacts\? facts\);/,
        /public static string\[\] ParseGrillQuestions\(string\? text\);/,
        /public static string GrillPrompt\(NotebookProject item, InspectionFacts\? facts, IReadOnlyList<string>\? docs\);/,
        /public static bool GrillDone\(NotebookProject\? item\);/,
        /public static DeterministicAnswer AnswerQuestion\(string question, OrchestratorSnapshot snap, string\? projectId\);/,
        /public static JsonObject PackState\(OrchestratorSnapshot snap, ProjectRow\? row, string\? stateFile = null\);/,
        /public static LeadEnvelope LeadEnvelope\(string\? answer, JsonArray\? actions = null\);/,
        /public static ValueTask<IReadOnlyList<string>> ComposeGrillRoundAsync\(/,
        /public static ValueTask<LeadDecisionResult> AskLeadDecisionAsync\(/,
        /public static ValueTask<LeadAskTurnResult> AskAsync\(/,
      ],
      boundaries: [
        /context isolation and state packing/i,
        /injected lead adapter/i,
        /composition root/i,
      ],
      dependencies: [
        /ILeadModelAdapter/i,
        /composition root/i,
      ],
      mappedTests: [
        /orchestrator/i,
        /project-context/i,
        /project-recipient/i,
        /conversation-history/i,
        /grill-chat/i,
        /autonomy/i,
        /parallel-projects/i,
        /research-dispatch/i,
      ],
      parity: [
        /deterministic prompt generation/i,
        /grill questionnaire parsing vectors/i,
        /Rule 12/i,
      ],
      migration: [
        /Job 50/i,
      ],
    },
  },
  state: {
    path: 'native/porting/jobs/46d-orchestrator-state.md',
    requirements: {
      responsibilities: [
        /emptyEntry/i,
        /stepOf/i,
        /pipelineOf/i,
        /crewOf/i,
        /progressKey/i,
        /defaultInspect/i,
        /orchestrator\.json/i,
        /loadState/i,
        /persist/i,
        /snapshot/i,
        /tick/i,
        /hydrateCheckpoints/i,
        /refreshResearch/i,
        /archiveProject/i,
        /setFocus/i,
      ],
      signatures: [
        /public static ProjectEntry EmptyEntry\(\);/,
        /public static string StepOf\(ProjectEntry\? row\);/,
        /public static IReadOnlyList<PipelineStepState> PipelineOf\(ProjectEntry\? row\);/,
        /public static IReadOnlyList<CrewMember> CrewOf\(/,
        /public static string ProgressKey\(/,
        /public static InspectionFacts DefaultInspect\(string folder\);/,
        /public static OrchestratorState Load\(/,
        /public void Persist\(\);/,
        /public ProjectEntry Entry\(string projectId\);/,
        /public OrchestratorSnapshot Snapshot\(NotebookPlan plan, string\? projectId = null\);/,
        /public Task<OrchestratorSnapshot> TickAsync\(string\? projectId = null/,
        /public void HydrateCheckpoints\(\);/,
        /public void RefreshResearch\(\);/,
        /public Task<ArchiveProjectResult> ArchiveProjectAsync\(/,
        /public Task<SetFocusResult> SetFocusAsync\(/,
      ],
      boundaries: [
        /sole authority/i,
        /immutable views/i,
        /atomic state transitions/i,
        /renameWithRetry/i,
        /bridge sharing/i,
      ],
      dependencies: [
        /composition root bridge sharing/i,
        /Execution bridge/i,
      ],
      mappedTests: [
        /lifecycle/i,
        /worker-snapshot/i,
        /progress-detail/i,
        /activity-sessions/i,
        /reconcile-attempts/i,
        /parallel-projects/i,
        /home-containment/i,
        /project-export/i,
        /runtime-integration/i,
      ],
      parity: [
        /golden state snapshots/i,
        /restart recovery fixtures/i,
        /scenario replay/i,
        /byte-level persistence parity/i,
        /Rule 12/i,
      ],
      migration: [
        /Job 51/i,
      ],
    },
  },
  orderParsing: {
    path: 'native/porting/jobs/46e-orchestrator-order-parsing.md',
    requirements: {
      responsibilities: [
        /looksLikeQuestion/i,
        /looksLikeWork/i,
        /freshTaskIntent/i,
        /checkpointTalk/i,
        /researchIntent/i,
        /pickAskedProject/i,
        /parseOrder/i,
        /Order Routing in `ask` and `runOrder`/i,
      ],
      signatures: [
        /public static bool LooksLikeQuestion\(string\? question\);/,
        /public static bool LooksLikeWork\(string\? question\);/,
        /public static bool FreshTaskIntent\(string\? question\);/,
        /public static bool ResearchIntent\(string\? question/,
        /public static CheckpointTalkInfo CheckpointTalk\(string\? reason\);/,
        /public static ProjectSummary\? PickAskedProject\(/,
        /public static ParsedOrder\? ParseOrder\(/,
        /public static OrderRouteResult RouteAsk\(/,
      ],
      boundaries: [
        /pure functional parsing/i,
        /stateless and side-effect-free/i,
        /zero I\/O/i,
        /explicit error reporting/i,
        /Composition Seam/i,
      ],
      dependencies: [
        /orchestrator composition root/i,
        /runOrder/i,
      ],
      mappedTests: [
        /orchestrator/i,
        /project-recipient/i,
        /add-to-work/i,
        /saving-mode/i,
        /start-over/i,
        /phase2/i,
        /phone-decisions/i,
        /pr-fix/i,
      ],
      parity: [
        /cases\.cjs/i,
        /Rule 12/i,
      ],
      migration: [
        /Job 47/i,
      ],
    },
  },
};

function readSheet(rel) {
  return fs.readFileSync(path.join(root, rel), 'utf8');
}

function assessSheet(text, sheetReqs) {
  const missing = [];
  for (const [category, checks] of Object.entries(sheetReqs)) {
    for (const check of checks) {
      if (!check.test(text || '')) {
        missing.push(`${category}: ${check}`);
      }
    }
  }
  return missing;
}

test('all Job 46 specification sheets exist and are nonempty', () => {
  for (const [key, spec] of Object.entries(sheets)) {
    const content = readSheet(spec.path);
    assert.ok(content.length > 500, `${key} (${spec.path}) must be present and substantive`);
  }
});

test('Master sheet (46-phase6-orchestrator.md) satisfies all structural and behavioral requirements', () => {
  const content = readSheet(sheets.master.path);
  const errors = assessSheet(content, sheets.master.requirements);
  assert.deepEqual(errors, []);
});

test('Sub-module sheets (46a-e) satisfy all responsibilities, signatures, boundaries, dependencies, tests, parity, and migration', () => {
  for (const [key, spec] of Object.entries(sheets)) {
    if (key === 'master') continue;
    const content = readSheet(spec.path);
    const errors = assessSheet(content, spec.requirements);
    assert.deepEqual(errors, [], `Errors in ${key} (${spec.path})`);
  }
});

test('every requirement pattern across master and split modules detects its own omission', () => {
  for (const [key, spec] of Object.entries(sheets)) {
    const content = readSheet(spec.path);
    for (const [category, checks] of Object.entries(spec.requirements)) {
      for (const check of checks) {
        const flags = check.flags.includes('g') ? check.flags : check.flags + 'g';
        const broken = content.replace(new RegExp(check.source, flags), '');
        assert.notEqual(broken, content, `${key} mutation must alter text for ${category}: ${check}`);
        const errors = assessSheet(broken, spec.requirements);
        assert.ok(
          errors.some(err => err === `${category}: ${check}`),
          `omission escaped in ${key} for ${category}: ${check}`
        );
      }
    }
  }
});

test('empty, null, undefined, and truncated sheets fail assessment', () => {
  for (const [key, spec] of Object.entries(sheets)) {
    const content = readSheet(spec.path);
    assert.ok(assessSheet('', spec.requirements).length > 0, `${key} empty string must fail`);
    assert.ok(assessSheet(null, spec.requirements).length > 0, `${key} null must fail`);
    assert.ok(assessSheet(undefined, spec.requirements).length > 0, `${key} undefined must fail`);
    assert.ok(assessSheet(content.slice(0, 80), spec.requirements).length > 0, `${key} truncated must fail`);
  }
});

test('interleaved assessments stay independent across concurrent runs', async () => {
  const masterContent = readSheet(sheets.master.path);
  const runs = Array.from({ length: 30 }, (_, index) => {
    const isOdd = index % 2 === 1;
    const text = isOdd ? '' : masterContent;
    return Promise.resolve().then(() => ({ isOdd, errors: assessSheet(text, sheets.master.requirements) }));
  });
  const results = await Promise.all(runs);
  results.forEach(({ isOdd, errors }) => {
    if (isOdd) {
      assert.ok(errors.length > 0, 'empty run must produce errors');
    } else {
      assert.deepEqual(errors, [], 'valid run must have no errors');
    }
  });
});

test('representative architectural, state boundary, parity, and migration mutations are detected', () => {
  const masterContent = readSheet(sheets.master.path);
  const criticalPhrases = [
    ['unidirectional data flow', 'unidirectional', 'bidirectional'],
    ['no peer store imports', 'no peer module store imports', 'peer store imports permitted'],
    ['asynclocal isolation', 'AsyncLocal', 'ThreadLocalUnused'],
    ['cancellation token isolation', 'cancellation token', 'shared cancellation'],
    ['no model substitution', 'never substitute requested models', 'silently substitute models'],
    ['parity rule 12', 'Rule 12', 'Rule 999'],
    ['step 1 order parsing', 'Step 1: OrderParsing', 'Step 1: Discarded'],
    ['step 6 facade', 'Step 6: Facade Composition', 'Step 6: Dropped'],
  ];

  for (const [label, target, replacement] of criticalPhrases) {
    const rx = new RegExp(target, 'gi');
    assert.ok(rx.test(masterContent), `master sheet must contain ${label} ("${target}")`);
    const mutated = masterContent.replace(rx, replacement);
    const errors = assessSheet(mutated, sheets.master.requirements);
    assert.ok(errors.length > 0, `mutation of ${label} (${target} -> ${replacement}) must be detected`);
  }
});

