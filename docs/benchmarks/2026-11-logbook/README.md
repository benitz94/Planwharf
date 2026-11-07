# Benchmark: logbook.js (November 2026)

One task, given to four AI coding tools working alone and to Planwharf with a two-model crew.
Every arm reached the maximum score, so **this task does not separate them**. The result is
published because it is honest, not because it shows a winner.

## The task

[`TASK.md`](TASK.md): a Node.js command-line program, `logbook.js`, with no dependencies. It
stores named streams of immutable events in one `db.json` file and must stay correct when many
processes use it at once or are killed mid-write. It covers:

- 15 commands with exact output and error codes;
- idempotent appends, compaction and cursors;
- atomic imports;
- a file lock with safe takeover of locks left by dead processes;
- crash safety on Windows and POSIX.

The first text came from a ChatLLM agent. Claude Opus 5.5 removed ten ambiguities, then wrote a
reference implementation and the hidden grader. Opus is also a contestant, which is a bias to
keep in mind.

## How it was graded

[`grader.cjs`](grader.cjs) runs 65 hidden checks in 13 groups:

- deliverables, command line and store;
- streams, append, read and compact;
- cursors, limits and import;
- locks, concurrency and crash.

It also runs each arm's own tests.

The lock, concurrency and crash checks were repeated twice more on every workspace, because
races do not always show the first time.

The grader was itself tested against [`reference/`](reference/) and against broken copies of it.
A broken copy that still scored full marks would have meant a blind spot. See
[Grader validation](#grader-validation).

## Results

| Arm | How it worked | Hidden checks | Locks, concurrency, crash (2 reruns) | Time |
| --- | --- | --- | --- | --- |
| Codex alone | one model | 65/65 | 12/12, 12/12 | ~6 min |
| Claude Code alone | one model | 65/65 | 12/12, 12/12 | ~10 min |
| Planwharf | GPT-6 Astra leads and builds, Claude Opus 5.5 verifies | 65/65 | 12/12, 12/12 | 13 min (build ~7, verify ~6) |
| Gemini (Antigravity) alone | one model | 65/65 | 12/12, 12/12 | ~15 min |
| Grok alone | one model | 65/65 | 12/12, 12/12 | ~29 min |

- The four solo arms ran in parallel, in separate folders, starting together.
- Planwharf ran on its own later, also in a fresh folder.
- No arm was given hints or corrected during the run.
- No workspace contained or referred to the grader or the reference.

| Arm | logbook.js lines | Test lines | Own test suite |
| --- | --- | --- | --- |
| Codex | 344 | 235 | 16 s |
| Claude Code | 741 | 610 | 29 s |
| Planwharf | 328 | 367 | not timed |
| Gemini | 1,047 | 635 | 20 s |
| Grok | 1,001 | 1,237 | 26 s |

**Tokens.** Missing values are shown as missing, never as 0 or an estimate.

- For the Planwharf arm, the verifier (Opus) used 21,064 output tokens and read 1,049,835 from the cache.
- The builder's (Astra) tokens were not reported by its tool.
- The lead's calls could not be separated from the session total.
- Token use for the solo arms was not collected.

## What it shows

- **On this task, quality ties.** Every arm scored the maximum, so the task is too easy to rank
  these tools on correctness. The clarified text probably helped: it states error orders, flag
  rules and the Windows lock pitfalls.
- **Time differs.** Codex was fastest. Grok took about five times as long as Codex, and its own
  test suite does not explain the gap.
- **Planwharf ties on quality and lands in the middle on time.** Its build step alone (~7 min)
  was close to Codex alone. The verify step added ~6 minutes, but found nothing to fix, because
  there was nothing left to find. A verifier pays off only when the builder leaves real defects.
- **To show what a crew adds, the next task must be one where the best single tool clearly fails.**

## Limits

- One run per arm.
- The solo arms' transcripts were not reviewed. The leak check covers workspaces only.
- The author of the task, reference and grader (Claude Opus 5.5) is also a contestant, both alone and as Planwharf's verifier.
- Gradings were run on the same Windows machine.
- Times are wall-clock times reported by the owner and the tools, rounded to the minute.

## Grader validation

The grader was run against the reference implementation and against 27 deliberately broken copies
of it. The result is in [`VALIDATION.md`](VALIDATION.md).

## Files

| File | What it is |
| --- | --- |
| [`TASK.md`](TASK.md) | The task, exactly as given to every arm. |
| [`grader.cjs`](grader.cjs) | The hidden checks: `node grader.cjs <workspace> [--skip-suite] [--only <regex>] [--out <file>]`. |
| [`validate.cjs`](validate.cjs) | Runs the grader on the reference and on the broken copies. |
| [`reference/`](reference/) | Reference implementation and its tests. |
| [`results/`](results/) | Grader output for every arm and rerun. |
