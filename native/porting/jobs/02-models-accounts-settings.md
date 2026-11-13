# Job 02: model roles and policy, provider accounts, browser settings, activity events

**Goal.** Port five engine modules from JavaScript to C#, with their tests, following
`native/porting/README.md`. This is the first job meant for a crew working in parallel.

## Port

| JavaScript | C# | Tests to port |
|---|---|---|
| `engine/model-roles.cjs` (36 lines) | `native/Planwharf.Engine/ModelRoles.cs` | `engine/model-roles.test.cjs` |
| `engine/model-policy.cjs` (111 lines) | `native/Planwharf.Engine/ModelPolicy.cs` | `engine/model-policy.test.cjs` |
| `engine/provider-accounts.cjs` (47 lines) | `native/Planwharf.Engine/ProviderAccounts.cs` | `engine/provider-accounts.test.cjs` |
| `engine/browser-settings.cjs` (44 lines) | `native/Planwharf.Engine/BrowserSettings.cs` | `engine/browser-settings.test.cjs` |
| `engine/activity-events.cjs` (109 lines) | `native/Planwharf.Engine/ActivityEvents.cs` | the checks of `engine/activity-events.test.cjs` that use only `LIMITS` and `sanitize` |

The five modules do not use each other, and each one has its own two files (the module and its
test file). They can be ported at the same time by different workers, each changing only its own
two files.

Many JavaScript tests start the server or the orchestrator. Those cases are **not** ported now: list
them under "Deferred cases" with the reason (rule 3 of the README). Port every case, or part of a
case, that checks the module alone.

`provider-accounts.cjs` reads account files of other tools: it must never return credentials or
email addresses. Keep that rule and its comment.

## Before you start

Read `native/porting/README.md`, then the JavaScript files and tests of your part, completely.
Look at `AtomicRename.cs`, `Skills.cs` and their tests for the expected shape.

## Check

```
dotnet build native/Planwharf.Engine
dotnet test native/Planwharf.Engine.Tests
```

Both must pass with no warning. Do not change any file outside `native/Planwharf.Engine` and
`native/Planwharf.Engine.Tests`.

## Hand over

A commit on its own branch, with a message that lists: what was ported, the deferred cases and why,
and any difference from the JavaScript.
