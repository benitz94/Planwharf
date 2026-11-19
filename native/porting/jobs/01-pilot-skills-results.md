# Job 01 (pilot): skills and worker results

**Goal.** Port three small engine modules from JavaScript to C#, with their tests, following
`native/porting/README.md`. This is the first job done by Planwharf on its own code.

## Port

| JavaScript | C# | Tests to port |
|---|---|---|
| `engine/skills.cjs` (70 lines) | `native/Planwharf.Engine/Skills.cs` | `engine/skills.test.cjs` |
| `engine/structured-final.cjs` (48 lines) | `native/Planwharf.Engine/StructuredFinal.cs` | `engine/structured-final.test.cjs` |
| `engine/worker-result.cjs` (55 lines) | `native/Planwharf.Engine/WorkerResult.cs` | the cases of `engine/structured-final.test.cjs` that use it |

`worker-result.cjs` uses `structured-final.cjs`: port `StructuredFinal` first.

## Before you start

Read `native/porting/README.md`, then the three JavaScript files and their tests, completely.
Look at `AtomicRename.cs` and `AtomicRenameTests.cs` for the expected shape.

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
