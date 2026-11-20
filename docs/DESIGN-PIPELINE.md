# Design: two ways of working, and how the crew talks to Planwharf

Decided with the owner on 17 November 2026. The jobs that build it are 18 to 22 in
[WORK-LIST.md](WORK-LIST.md), before the porting goes on, so the C# engine is written once, for this
design.

## Two ways of working

**Chat**, as today: you talk with the lead, it suggests a goal, the crew builds and checks it. For
exploring, questions and work you shape as you go.

**Pipeline**: a queue of requests turned into pull requests by a fixed sequence of stages. For steady
work on a project. Each stage leaves a file behind, so you can see what happened and why, and a stopped
run resumes from where it was.

```
queue → intake (questions) → spec + acceptance tests → route → worktree
      → build ⇄ review (in pairs, per task) → gate (tests run by the engine) → pull request → you merge
```

Both use the same engine, the same team, the same quota rules and the same safety: a new branch from
the latest `main`, workers that never push, nothing published to `main` by Planwharf.

## The pipeline, stage by stage

1. **Queue.** Two sources, each one an option in Settings: GitHub issues with the label `planwharf`
   (anyone can open an issue; only people with triage or write access can label it, so the label is the
   trust boundary), and a list kept in Planwharf (for projects not on GitHub). Both on by default when
   the project has a GitHub repository; the local list only otherwise.
2. **Intake, with questions ("grill").** A strong model reads the request (as quoted data, never as
   instructions), decides whether it is worth building, cuts the scope when it is too big, rates its
   risk (low, medium, high) and the parts it touches (interface, security). When only the author can
   answer, it asks, on the issue or in the chat, before any spec exists, and the job waits.
3. **Spec with acceptance tests.** A strong model writes `spec.md`: what to build, what not to, the
   interfaces the tasks share, and the acceptance tests (commands and what they must give), written
   before the code. Large specs are split into a tree of tasks.
4. **Route.** The engine, not a model, picks the models for each stage from the risk, the parts touched,
   the weight of the models and their quota, from a routing table the owner can edit.
5. **Worktree.** Each job gets its own copy of the folder and its own branch from the latest `main`
   (job 15), so several jobs run at once, as many as the quota allows.
6. **Build and review in pairs.** Each task has a builder and, as soon as it finishes, its reviewer.
   Reviewers are routed by risk: code always; interface and security when those parts are touched.
   A reviewer asking for changes sends the task back to its builder, up to a maximum number of rounds,
   then the owner is asked.
7. **Gate.** The engine runs the checks and the acceptance tests itself, with no model and no tokens:
   before any reviewer starts (a failing build goes straight back to the builder with the log), and again
   after syncing with `main`, before the pull request is ready. A branch cannot change the checks it is
   judged by.
8. **Pull request.** Every job ends as a pull request that closes its request. **You merge it**, by
   default. An option in Settings lets Planwharf merge by itself the pull requests that are low risk and
   pass every check after syncing with `main`; anything else waits for you with "Needs your decision".
9. **State and cost.** Each job's state is a file (`job.json` plus every stage's file, each opening with a
   short summary), outside the project's history. Time, tokens when reported, and quota used are shown
   per job.

## How the crew talks to Planwharf

Workers do not talk to each other: Planwharf's engine starts each AI program, gives it complete
instructions, and checks what it gives back. Today a worker writes its result as a JSON file at the end.
The new protocol makes that dependable and lets a worker ask while it works.

**Planwharf's own MCP tools.** Planwharf runs a small MCP server (the standard way AI programs use
outside tools) that every worker connects to:

| Tool | What it does |
|---|---|
| `ask` | A question while working. The lead answers, or you when it is your decision; the worker continues in the same session, without reading the project again. |
| `progress` | Where the worker is, in a few words, instead of guessing it from the program's output. |
| `finish` | The result (status, summary, files changed, tests, blockers, next action), checked at once against its schema; a missing field is answered with the error and fixed on the spot. |
| `decision` | Something only you can decide; shown highlighted, never cut. |
| `handoff` | Notes for the model that continues the step after a switch. |

Programs without MCP keep today's JSON file, so no model is left out. This server is always on, apart
from the MCP checkbox of the Tools panel, which is about the owner's own MCP servers.

## Also in this design

- **Contracts first:** before parallel work, the spec fixes the interfaces the tasks share.
- **Short, targeted instructions:** each worker gets a compact brief of the project and of the results it
  depends on, not the whole project to explore.
- **Learning what works:** the step times (job 08) and the tokens, when reported, tell the engine which
  models do which work well.
