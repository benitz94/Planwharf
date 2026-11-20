# Job 18: Planwharf worker MCP protocol

**Goal.** Give a running worker five Planwharf-owned MCP tools: `ask`, `progress`,
`finish`, `decision`, and `handoff`, as decided in
[the design](../../../docs/DESIGN-PIPELINE.md). This sheet is the deliverable of
the current job. Review it before implementing the protocol on `feat/mcp-protocol`.
The C# engine is the target. The JavaScript engine continues to run until phase 7.

## Build after this sheet is reviewed

| Part | Contract and owner | Tests to add |
|---|---|---|
| Transport | A small Planwharf-owned MCP server, available whenever Planwharf runs. Register its tools for each supported worker program independently of the owner's MCP checkbox. Give each attempt a private connection and bind it to its project, worktree, subtask, attempt, provider, and model. | Launch a worker with the owner's MCP checkbox both on and off; its Planwharf tools remain available. A different attempt cannot call its tools. |
| `ask` | Accept a concise question, save it against the attempt, route it to the lead or owner, and return the answer to the same live worker session, without reading the project again. Only the owner answers a decision reserved for the owner. | Lead answer, owner answer, cancellation, timeout, and restart while waiting; no answer crosses attempts. |
| `progress` | Accept a short status and update the attempt's visible progress without declaring success, instead of guessing it from the program's output. Bound and sanitize the text before persistence or display. | Ordered updates, oversized text, malformed input, and a late call after the attempt ends. |
| `finish` | Accept the existing structured result fields and exact identity values (`resultId`, `attemptId`, `subTaskId`, `megaTaskId`, `status`, `summary`, `filesChanged`, `tests`, `blockers`, `nextAction`, and, when the contract already allows them, `reportPath`, `sources`, and `recommendedNextAction`). Validate at once and return field errors to the worker so it can correct them in the same session. Persist one accepted result atomically; the engine still checks process exit and file claims before completion. | Missing fields, wrong IDs, invalid status, duplicate calls, process failure after `COMPLETE`, and a corrected result in the same session. |
| `decision` | Accept an owner-only question, save and show it highlighted and prominently, in full and never cut, then pause the dependent work until the owner answers. Do not silently shorten or route it to another model. | Long decision, owner answer, cancellation, and recovery after restart. |
| `handoff` | Save bounded notes for the next attempt of the same subtask after a model switch. Include them in the successor's brief; do not send them directly to another worker. | Model switch, no switch, wrong subtask, duplicate notes, and restart. |
| Compatibility | A program with verified MCP support uses the tools. A program without it keeps today's UTF-8 JSON result file and the same validation and collection path. Keep existing structured-final support where already verified. | One MCP-capable fake program and one without MCP finish the same result contract. Unsupported or failed MCP setup falls back without losing an attempt. |

The tools belong to Planwharf and are separate from the owner's configured MCP
servers. Do not turn on the owner's servers when registering these tools. Keep
provider and model IDs explicit; verify capability for each adapter instead of
inferring it from a provider name. Do not claim an adapter works until a real
launch path and a fake-program test prove it. Use only the .NET base library in
the C# engine, following [the porting rules](../README.md).

## Trust and recovery boundaries

- At every tool call, validate the connection's attempt identity and active
  lifecycle state before routing, writing, or showing anything. Reject stale,
  unknown, cross-project, cross-worktree, and cross-attempt calls. Never accept
  a model-supplied path, project ID, or recipient as authority.
- Treat all tool arguments as untrusted data. Give each field a documented size
  limit, reject unbounded configuration, validate UTF-8 and schema, and keep
  control characters and secrets out of logs and visible errors. Return a
  bounded, specific error without echoing sensitive input.
- Persist a question, decision, handoff, or accepted finish before reporting
  success to the worker. On restart, restore pending owner decisions and
  questions; a completed attempt must not be completed twice. Record the
  attempt ID and outcome for audit without copying private text to logs.
- Tool calls are worker-to-engine only. Workers never call each other. The
  engine alone routes answers and notes, and the owner remains the authority
  for decisions. An MCP `finish` is a claim, not proof that work or tests ran.

## Before implementation

Read the current C# engine boundary, the JavaScript `worker-result.cjs`,
`structured-final.cjs`, `execution.cjs`, and each provider launch adapter with
its tests. Map which programs support MCP and how their sessions receive a
server. Record unsupported programs as JSON-file users. Do not port unrelated
JavaScript modules or change the protocol on an unverified assumption about a
provider CLI.

## Acceptance check for the implementation job

1. A fake MCP-capable worker calls all five tools through the real launch and
   server boundary. `ask` resumes in its original session, `decision` waits for
   the owner, `handoff` reaches only a successor, and `progress` never marks
   work done.
2. `finish` rejects incomplete and mismatched results at the call, then accepts
   a corrected result. A failed process or false file claim is not reported as
   completion. Test entries remain worker claims until job 19's engine-run gate
   checks them. Repeated or late calls do not duplicate a result.
3. A worker without MCP writes the existing JSON result. Both paths produce the
   same engine result and preserve historical evidence. A broken MCP connection
   has a visible recovery path and cannot silently turn an attempt into success.
4. Parallel attempts and restarted work cannot read or answer one another's
   questions, decisions, notes, or results. Invalid identifiers, malformed and
   oversized arguments, and unauthorized recipients fail before side effects.
5. Run `dotnet build native/Planwharf.Engine`,
   `dotnet test native/Planwharf.Engine.Tests`, and
   `node scripts/engineering/ci.cjs`; record observed output and any deferred
   provider or platform case. The C# build has no warnings.

## Files allowed for the implementation job

The C# engine and its tests, plus the narrow provider-launch and integration
files needed to connect workers to the Planwharf server. Name those files after
the adapter inspection and before editing. Do not change the owner's MCP
settings semantics, unrelated features, or historical result evidence. If a
JavaScript module already ported to C# must change, update its comparison cases
in the same branch under porting rule 12.

## Hand over

Review this sheet before implementation. The current job commits this sheet,
marks job 18 as "Done, waiting for review" in `docs/WORK-LIST.md`, and adds
job 18 to `docs/BUILT-WITH-PLANWHARF.md` with pull request `#?`.
