# Job 04 (phase 2): refusals, diagnostics, briefing, project history, evidence, autonomy

**Goal.** Port six engine modules from JavaScript to C#, with their tests and their comparison cases,
following `native/porting/README.md` (rules 1 to 12). See [the plan](../PLAN.md), phase 2. All six
work on data alone: no files, no processes, no network.

## Port

| JavaScript | C# | Tests to port |
|---|---|---|
| `engine/provider-refusal.cjs` (46 lines) | `ProviderRefusal.cs` | the checks of `engine/autonomy-crew.test.cjs` that use `refusalReason`, `activeRefusal` and `recordRefusal` alone |
| `engine/provider-diagnostics.cjs` (26 lines) | `ProviderDiagnostics.cs` | the checks of `engine/grill-chat.test.cjs` and `engine/model-policy.test.cjs` that call `diagnoseProvider` |
| `engine/briefing.cjs` (24 lines) | `Briefing.cs` | the checks of `engine/final.test.cjs` that call `compactState` and `recentContext` |
| `engine/project-history.cjs` (32 lines) | `ProjectHistory.cs` | the checks of `engine/final.test.cjs` that call `summarizeTimeline` and `checkpointDiff` |
| `engine/activity-evidence.cjs` (103 lines) | `ActivityEvidence.cs` | the checks of `engine/activity-evidence.test.cjs` that do not use `activity-sessions` (not ported yet) |
| `engine/autonomy.cjs` (224 lines) | `Autonomy.cs` | `engine/autonomy.test.cjs`, and the checks of `engine/autonomy-crew.test.cjs` that use this module alone |

The C# files go in `native/Planwharf.Engine/`, their tests in `native/Planwharf.Engine.Tests/`
(`ProviderRefusalTests.cs`, and so on). The six modules do not use each other: they can be ported at
the same time by different workers, each changing only its own two files.

## What to watch

- **Time comes from the caller.** `activeRefusal`, `recordRefusal` and `nextStep` take the current
  time as an argument (`nowMs`, `nowIso`, `now`). Keep it an argument in C#, and give fixed times in
  the tests and in the comparison cases, never the clock of the computer.
- **`pushGuardEnvironment(env)`** reads the environment it is given. In C# take a dictionary, with the
  process environment only as the default; the tests always pass one. The check of
  `autonomy.test.cjs` that runs a real `git push` with this environment needs git on the computer
  (rule 11): port what it checks on the returned values, and list the push itself under "Deferred cases".
- **Functions that change their input.** `activeRefusal` removes an expired entry from `store` and
  `recordRefusal` adds one. The C# must change the store the same way (a `JsonObject`); check the
  store after the call in the C# tests, since the comparison records only what the function returns.
- **Regular expressions** (rule 8): `provider-refusal`, `provider-diagnostics` and
  `activity-evidence` are mostly patterns with the `i` flag, including the typographic apostrophe
  `’` in `you’ve hit your … limit`. Test the cases where JavaScript and .NET could differ, for
  example `\b429\b` next to letters, `\s` on non-breaking spaces and upper-case non-ASCII letters.
- **Text read by a person or a model** (rule 4): the suggestions and the report of
  `diagnoseProvider`, the milestone summaries of `summarizeTimeline`, the changes listed by
  `checkpointDiff`, and every prompt of `autonomy.cjs` (`splitPrompt`, `strictDecisionPrompt`,
  `evaluationPrompt`, `nudgePrompt`, `assignmentPrompt`) are copied character for character.
  `reportUrl` must encode as `encodeURIComponent` does (`Uri.EscapeDataString` differs on some
  characters: test `!'()*`).
- **`compactState`** builds a new object from many optional fields and drops some
  (`lastLog: undefined`, each worker's `log`). The JSON it gives must be the same, field order
  included: compare whole objects in the tests, not single fields.
- **Constants** (`COOLDOWN_MS`, `LIMITS`, `MAX_PARTS`) keep their names and values.

## Comparison (rule 12)

Add each module's cases to `native/porting/parity/cases.cjs` (from its tests, plus edge cases: empty
and missing values, text longer than the limits, expired and future refusals, every category of
`diagnoseProvider`), run `node native/porting/parity/record.cjs`, and wire every exported function in
`ParityTests.Ports`. Each worker adds **its own block** at the end of the module list and of `Ports`,
and touches no other line of those two files, so the parts can be put together without conflicts.

## Before you start

Read `native/porting/README.md`, then the JavaScript file and tests of your part, completely. Look at
`ModelPolicy.cs`, `ActivityEvents.cs` and their tests, and at `ParityTests.cs`, for the expected
shape.

## Check

```
node native/porting/parity/record.cjs
node engine/parity.test.cjs
dotnet build native/Planwharf.Engine
dotnet test native/Planwharf.Engine.Tests
```

All must pass with no warning.

## Files you may change

`native/Planwharf.Engine/`, `native/Planwharf.Engine.Tests/`, `native/porting/parity/cases.cjs` and
the `.json` files that `record.cjs` writes. Nothing else: not the JavaScript (rule 1).

## Hand over

A commit on branch `port/04-briefing-refusals-autonomy`, with a message that lists: what was ported,
the deferred cases and why, the number of comparison cases per module, and any difference from the
JavaScript found on the way.
