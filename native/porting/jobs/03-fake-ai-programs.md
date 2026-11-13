# Job 03 (phase 1a): fake AI programs for the tests

**Goal.** Build one small program that stands in for the four AI programs Planwharf runs (`claude`,
`codex`, `grok`, `agy`), so the tests of phases 3 to 6 can run the engine, JavaScript and C#, against
recorded answers instead of real AI programs and real quota. See [the plan](../PLAN.md), phase 1a.
This job ports no module: it builds the test bench.

## What to build

A C# console program, `native/Planwharf.FakeCli`, and its tests, `native/Planwharf.FakeCli.Tests`.

1. **Which program it plays.** From the environment variable `PLANWHARF_FAKE_AS` (`claude`, `codex`,
   `grok` or `agy`). The engine already lets a test point it at another program:
   `PLANWHARF_CLAUDE`, `PLANWHARF_CODEX`, `PLANWHARF_GROK` and `PLANWHARF_ANTIGRAVITY` (see
   `executable()` in `engine/provider-runtime.cjs`).
2. **What it answers.** From a scenario file named by `PLANWHARF_FAKE_SCENARIO`: a JSON list of
   answers. Each answer says when it applies (the arguments contain a text, the prompt read from
   standard input contains a text, or "the n-th call") and what to do: lines to write to standard
   output and to standard error, files to write (for example the result file whose path follows a
   given argument), a pause before answering, and the exit code. The first answer that applies is
   used; with none, it exits with code 2 and says why on standard error.
3. **What it was asked.** Each call is appended to the file named by `PLANWHARF_FAKE_LOG`, one JSON
   line per call: the arguments one by one, the standard input, the working folder, and the
   `PLANWHARF_*`, `CODEX_*` and `CLAUDE_*` environment variables. A test reads it to check what the
   engine sent.
4. **Recorded answers.** For each of the four programs, at least: a normal answer of the lead, a
   worker that finishes with a valid structured result, a worker that fails, a refusal for quota or
   credits, the model list (`agy models` and the others' equivalent), and a usage answer where the
   program has one. Put them in `native/porting/fakes/<program>/`. **Copy the exact formats the
   engine reads**: read `engine/provider-runtime.cjs` (`launchSpec`), `engine/runner.cjs`,
   `engine/structured-final.cjs`, `engine/provider-models.cjs`, `engine/antigravity.cjs`,
   `engine/claude-usage.cjs`, `engine/planUsage.js` and the fixtures in `fixtures/`
   (`antigravity-lead.jsonl`, `antigravity-usage.json`). No real account, email, token, path or
   project name in them.

## Rules

- Follow `native/porting/README.md`: Native AOT, no NuGet package in the program, arguments one by
  one, paths with spaces and non-ASCII names, English everywhere, tests that do not depend on the
  machine (rule 11).
- The tests use **exactly the packages and versions of `native/Planwharf.Engine.Tests`** (xunit 2.9.2,
  Microsoft.NET.Test.Sdk 17.11.1, xunit.runner.visualstudio 2.8.2, GitHubActionsTestLogger 2.4.1) and
  no other: they are already in the NuGet cache of this computer, and a worker may have no internet.
- It must run on Windows, macOS and Linux, including Windows and Linux on ARM.
- Do not change the engine (`engine/`, `server.cjs`, `native/Planwharf.Engine`) or the app. If the
  engine needs a change to use the fake, write it in the pull request instead.

## Check

```
dotnet build native/Planwharf.FakeCli
dotnet test native/Planwharf.FakeCli.Tests
```

Both must pass with no warning. The tests cover: each program's recorded answers read back as the
engine expects them (for example, `StructuredFinal.ReadFinalResult` accepts the worker answer of
`claude`), choosing the answer by argument, by prompt and by call number, the files written, the
call log, the exit codes, the pause, and the "no answer applies" case.

Add a step to `.github/workflows/native.yml` that runs `dotnet test native/Planwharf.FakeCli.Tests`
on every system, next to the engine tests.

## Files you may change

`native/Planwharf.FakeCli/`, `native/Planwharf.FakeCli.Tests/`, `native/porting/fakes/` and
`.github/workflows/native.yml`. Nothing else.

## Hand over

A commit on branch `port/03-fake-ai-programs`, with a message that lists what the fake can do, the
recorded answers for each program and where their format comes from, and anything the engine would
need to change to use it.
