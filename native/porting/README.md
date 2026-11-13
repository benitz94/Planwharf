# Porting the engine to C#

Planwharf's engine is moving from JavaScript (`engine/*.cjs`, `server.cjs`, `plan.js`) to C#
(`native/Planwharf.Engine`), so that no kind of Planwharf needs Node.js. The JavaScript engine keeps
working, unchanged, until the C# one replaces it. Each piece is ported by a job described in
`jobs/`, then reviewed before it is merged. The phases are in [PLAN.md](PLAN.md).

## Rules for every job

1. **Do not change the JavaScript.** The JavaScript engine is the specification. If you find a bug in
   it, write it in the pull request; do not fix it in the same change.
2. **One C# file per JavaScript module**, in `native/Planwharf.Engine/`, named after it in PascalCase
   (`skills.cjs` → `Skills.cs`). The first line says which file it ports: `// Port of engine/skills.cjs.`
   Keep the comments that explain *why*; translate them, do not drop them.
3. **One test file per module**, in `native/Planwharf.Engine.Tests/`, porting the cases of the
   JavaScript test that exercise this module alone. Keep each case's meaning and name it after the
   JavaScript case. A case that needs the server, the orchestrator or another module not ported yet is
   **not** ported now: list it in the pull request under "Deferred cases", with the reason.
4. **Same behaviour, character for character.** Text the user or a model reads (messages, prompts,
   summaries, reasons) is copied exactly. Same limits, same defaults, same order of checks.
5. **Same files on disk.** Read and write the same JSON with the same field names. A file loaded and
   saved again must stay byte-identical. Keep fields you do not know.
6. **Native AOT from the first line.** No reflection: JSON goes through `System.Text.Json` with
   `JsonNode` or a source-generated `JsonSerializerContext`, never `JsonSerializer.Serialize<object>`.
   The engine project treats every warning as an error, including the AOT analyzers.
7. **Base library only.** No NuGet package in the engine. Tests use xUnit, already referenced.
8. **Regular expressions** differ between JavaScript and .NET (`\d`, `\w` and `\s` on Unicode, named
   groups, the `g` and `y` flags). Use `RegexOptions.ECMAScript` when the pattern allows it; otherwise
   test the cases where the two could differ.
9. **Processes and paths**: never build a command line by joining strings; pass arguments one by one
   (`ProcessStartInfo.ArgumentList`). Paths with spaces and non-ASCII names must work on Windows.
10. **English** for code, comments, tests and the pull request.
11. **Tests do not depend on the machine.** A test must pass on Windows, macOS and Linux, whatever
    is installed there: never let a real program, browser or file of the computer decide the result
    (on the macOS runners Chrome is installed in `/Applications`). Give the code the files the test
    made, through the hooks the module offers.

## Example

`AtomicRename.cs` and `AtomicRenameTests.cs` port `engine/atomic-rename.cjs` and its test cases:
copy their shape.

## Done means

- `dotnet test native/Planwharf.Engine.Tests` passes, and `dotnet build native/Planwharf.Engine`
  has no warning.
- Every case of the module's JavaScript test is either ported or listed under "Deferred cases".
- The pull request says what was ported, which cases were deferred and why, and any difference from
  the JavaScript found on the way. The "Native app" workflow is green on Windows, macOS and Linux.
