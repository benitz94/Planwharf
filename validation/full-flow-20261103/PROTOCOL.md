# Full-flow benchmark protocol (draft)

Status: draft. Nothing here has been frozen or run. Lock this file before the first
calibration run; changes after that point must be listed as deviations.

## Question

Does Planwharf, used as a user would use it with every provider available, produce better
results than each model working alone on the same task, and at what cost?

Planwharf may lose or tie. Either outcome is reported as it stands.

## Why a separate harness

`validation/multi-project-serial-20261023/` hashes `arm.cjs`, `host.cjs` and `campaign.ps1`
into the frozen 2026-10-25 and 2026-11-02 manifests, and its host enforces one model,
serial calls and no reviewers. It is not modified for this benchmark. Its results measure
Planwharf's overhead around one model, with one invocation per arm. They do not measure the
full flow.

## Arms per task (7)

Each arm gets the identical task, in a new workspace and new native sessions, and runs alone.

| Arm | Kind | Lead | Model or roster |
| --- | --- | --- | --- |
| solo-grok | Direct | none | `grok-4.7` |
| solo-claude | Direct | none | `claude-fable-5-1` |
| solo-codex | Direct | none | `gpt-6-astra` |
| solo-gemini | Direct | none | `gemini-3.8-flash-high` |
| pw-lead-grok | Planwharf | `grok-4.7` | all four models |
| pw-lead-claude | Planwharf | `claude-fable-5-1` | all four models |
| pw-lead-codex | Planwharf | `gpt-6-astra` | all four models |

- Antigravity is not a lead, by owner preference. This is a preference and not a measured
  finding. It stays available as a worker and as a solo arm.
- All arms use the same model IDs throughout. Substitution stops the arm.
- `isolated` stays `false`: the arms share one Windows user.

## Execution

- One arm at a time, because Planwharf arms draw on every provider's quota.
- The arm order is drawn once before the freeze and recorded.
- The same hard limit applies to every arm: 30 minutes.
- No retry of an arm. A failure is a result, unless a login or quota failure happens before
  any inference, in which case the arm has not started.
- The Planwharf build under test is recorded by hash at freeze.

## User behaviour in Planwharf arms

- Settings stay at what a user would have: model roles from the normal catalog rules, and
  the lead chooses workers and any review work in its plan.
- Each arm uses Automatic mode with project autonomy on. The operator sets the goal, turns
  autonomy on and then does nothing until the arm stops or reaches the limit.
- There are no scripted approvals or answers. Any human action is an intervention and is
  recorded.
- The Supervisor's rule-based nudges are part of the test. Optional AI analysis stays off.
- An arm waiting on a person at the limit is recorded as blocked.
- Skills state and any setting that differs from the defaults are recorded in the manifest.

## Task requirements

- Medium difficulty for a user, and hard enough that a solo model does not reliably reach
  the maximum: several modules, interacting requirements, and hidden checks.
- Same grader for all arms, never reachable from a workspace.
- Before the freeze: the reference passes every check, a seed workspace fails, and a mutant
  is rejected.
- One calibration run with one solo model, on a task then retained only if it does not hit
  the ceiling. That model's calibration result is reported and is excluded from the
  comparison. If a calibration task is reused, its calibration model's solo arm is rerun.

## Measures, in priority order

1. Quality: hidden checks passed, and whether the arm finished inside the limit.
2. Human interventions: questions and approvals.
3. Elapsed time.
4. Tokens, by category and by provider. A missing value stays `null`, never 0 or an
   estimate. Totals across providers are not published, because providers count cache
   differently.

Any lead or reviewer invocation counts as a cost of its arm. Provider token categories must
cover every invocation, or that arm is reported as partial.

## Reading the results

- Primary comparison: each Planwharf arm against the best solo arm and against the median
  solo arm on that task. The best solo arm is known only afterwards, so a user could not
  have selected it in advance; both comparisons are shown.
- One run per arm on one task is a pilot. It can show a problem or a direction, and it does
  not establish that either approach is better.
- A public claim needs several tasks or repetitions, set before any of them runs.
