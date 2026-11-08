# Benchmark: minidb.js (November 2026)

One hard task, given to four AI coding tools working alone and to Planwharf with a crew of two
models. The numbers are reported as they came out; read them and draw your own conclusions.

## The task

[`TASK.md`](TASK.md): write `minidb.js`, a small SQL database in one file, for Node.js 18 or later,
with no third-party packages. It must:

- answer SQL exactly like a reference implementation: expressions, filters, aggregates, joins,
  subqueries, ordering, writes and DDL;
- keep transactions correct, including `ROLLBACK` of DDL and failed multi-row statements;
- survive a process killed at any moment without losing a committed transaction;
- let several processes use the same data, with a `BUSY` error after about 10 seconds of waiting;
- meet three speed limits on 100,000 and 20,000 rows.

Each tool also delivers its own tests, a README and a work report.

## How it was graded

A hidden grader ran **992 SQL statements** (generated queries compared with the reference
implementation, plus fixed statements for rules and errors) and **19 checks** on deliverables,
command line, transactions, storage after crashes, concurrency and speed. The grader and the
reference implementation are kept private so the task can be used again.

## Conditions

- Same `TASK.md` for every arm, in an empty folder that held only that file.
- Same Windows PC. The four solo arms started together on 7 November 2026; Planwharf ran
  afterwards, on its own.
- A 45-minute limit, stated in the task.
- Each tool ran with full permissions in its folder and no help on the task. One exception:
  Codex got stuck on a single command for about 5 minutes, and the owner stopped and restarted
  it. The lost time is counted in its 20 minutes.
- Times come from each tool's own log. Tokens come from the logs too, never from the models'
  reports; a value the logs do not make clear is left empty.

## Results

| Arm | Model | Time | SQL statements | Checks | Tokens |
|---|---|---|---|---|---|
| Claude Code alone | Claude Opus 5.5 | 20 min | 992 / 992 | 19 / 19 | 3.72 M |
| Codex alone | GPT-6 Astra | about 20 min | 992 / 992 | 18 / 19 | 1.25 M |
| Antigravity alone | Gemini 3.8 Flash (High) | about 23 min | 988 / 992 | 19 / 19 | not clear (1) |
| Grok Build alone | Grok 4.7 Build | 59 min (over the limit) | 978 / 992 | 19 / 19 | 4.31 M |
| **Planwharf** | Lead: Claude Opus 5.5 · Builder: GPT-6 Astra | **about 15 min** | 992 / 992 | 18 / 19 | 1.24 M (2) |

The check missed by Codex and by Planwharf is the same one: `BUSY` came after 20 seconds
instead of about 10.

(1) The Antigravity log counts about 377 thousand tokens plus 10.5 million read from cache, and
does not say whether the first number already includes the second. The total is either about
0.38 M or about 10.9 M, so it is left empty.

(2) GPT-6 Astra 1.13 M, of which almost all was context read from cache; the lead 0.11 M for two
short calls (the plan and the final check).

## What to know about the Planwharf run

- **Small steps.** Planwharf tells every builder to work in small steps: write a working part,
  test it, then add the next. The solo tools did not get this instruction.
- **No verifier in this run.** The team had a verifier, but the lead planned the job as one
  piece and, because of a bug, a one-piece job skipped the independent check. Planwharf's own
  fresh test run (19 of 19 passing) was the only check. The bug is fixed: approved work now
  always goes through the verifier when the team has one.
- **Earlier runs.** Planwharf was run more than once during this benchmark. The earlier runs
  showed bugs in Planwharf (a stall after a blocked step, a turn cap and a permission mode for
  one provider, a worker left waiting on a leftover process). They were fixed, and they are not
  in the table. The table shows the run on the fixed version.
- The crew was chosen by the owner. Planwharf does not pick or recommend models.

## Not checked

The grader kills processes but cannot cut the power, so it cannot tell whether a program calls
`fsync` before reporting a commit. The crash checks cover a killed process, not a power cut.
