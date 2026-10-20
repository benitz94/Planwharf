# Planwharf — capabilities, evidence, and limits (20 October 2026)

This page describes features integrated into `main` through [PR #21](https://github.com/benitz94/Planwharf/pull/21) and distinguishes reported acceptance evidence from independent proof of broader benefits. Read the [quick start](QUICKSTART.md), [user guide](USER-GUIDE.md), and [project status](PROJECT-STATUS.md) for operational details. Planwharf is under active development.

## What Planwharf adds to an AI coding CLI

Planwharf is a local-first project notebook and orchestrator, **not** a new foundation model. A single supported coding model can be used for planning, implementation and review, subject to its configured roles and permissions; multiple eligible models can share assignments. Its potential advantages are durable project context, approved MegaTasks and dependent SubTasks, observable worker attempts, structured results, reviews, checkpoint/restart recovery and project isolation. These workflows can also introduce extra model calls, latency and overhead.

A completed process is not sufficient evidence of a successful implementation. Recorded results, status transitions, changed files and reported tests remain inspectable. Implementation and review workers require a linked project directory; launch and Git-inspection containment do **not** amount to a general OS sandbox.

## Recent integrated improvements

| Area | Current implemented behavior | Evidence and qualifications |
| --- | --- | --- |
| Workspace and project continuity | Overview / Workspace / Supervisor navigation; project-specific state, Skills and session history; export/import as a **new** notebook project; relink before checkpoint Resume | [PR #10](https://github.com/benitz94/Planwharf/pull/10), [PR #13](https://github.com/benitz94/Planwharf/pull/13), [PR #14](https://github.com/benitz94/Planwharf/pull/14); import does not restore source contents, private logs or credentials |
| Worker containment and Home Next | Linked-folder launch validation, conservative Git inspection, clear relink/approve/resume progression | [PR #17](https://github.com/benitz94/Planwharf/pull/17). Not a security sandbox for arbitrary generated shell commands |
| Soundtrack | Optional local audio, user playlists, six thematic playlists from four documented CC0 recordings; adaptive playback tracks the *selected* project; one audio controller; Web Locks prevent simultaneous players across same-origin tabs in one browser profile | [PR #18](https://github.com/benitz94/Planwharf/pull/18), [Soundtrack guide](SOUNDTRACK.md). Music is off until explicitly started; lock scope does not extend across different browsers/profiles/origins |
| Ship View and Captain | Decorative nautical scene; Captain uses lookout/helm poses by lifecycle, steering during RUNNING with calm/storm motion variants; reduced-motion support | [Captain steering guide](CAPTAIN-STEERING.md) and [merge commit](https://github.com/benitz94/Planwharf/commit/d6c49d0952ded776a45b444e81788ce0ae435fbe); weather is decorative, not proof of execution health |
| Real Codex Worker results and review | Verified native structured-result transport in trusted runner, rejection of stale/invalid completion output, valid negative reviews kept distinct from provider errors, disabled untracked native Codex subagent fan-out | [PR #20](https://github.com/benitz94/Planwharf/pull/20) and its [real-model audit](https://github.com/Rocco-Skudo/Planwharf/blob/e86da8a95c1cb735e33eccebde427aede317ac39/docs/audits/2026-10-20-real-model/README.md). Real Codex CLI/gpt-5.6-sol test evidence includes build, deliberately failing review, correction, three-project operation and restart/resume; provider 429s were observed |
| Smart Worker Routing | Optional **Catalog Order / Smart Routing / exact Manual-Gold** selection; persistent Standard/Premium preferences; capability, complexity, eligibility and quota-aware routing rationale; premium assignments under existing MegaTask authorization **without a second per-task premium prompt** | [PR #21](https://github.com/benitz94/Planwharf/pull/21), [Smart Routing audit](https://github.com/Rocco-Skudo/Planwharf/blob/468eae821494b5fd0e218acd65a8f2739777f628/docs/audits/2026-10-20-smart-routing/README.md). Existing installations stay on Catalog Order until Smart is enabled. Gold/exact-model constraints remain authoritative |

### Smart Routing in practice

Smart Routing is a **model-assignment policy**, not a fifth model role. Existing Orchestrator, Worker, Reviewer and optional Supervisor permissions remain separate. Eligible models are selected from configured, available, account-bound connections. Standard/Premium preferences are user configuration, not verified general intelligence or pricing rankings.

After the user explicitly enables Smart Routing and makes premium candidates eligible, the Orchestrator may select them automatically for sufficiently demanding **approved** work. **It does not add a premium-specific approval**, and it does not bypass normal MegaTask approval, role restrictions, quota thresholds or exact Gold requests. The complexity assessment is a transparent heuristic, **not a guarantee** of optimum model choice or lower spending. HTTP 429 rate limits are provider-capacity errors, not proof that a stronger model is required.

The HarborQueue acceptance exercise documented automatic assignment of an ordinary dashboard task to `gpt-5.6-luna` and a difficult recovery task to `gpt-5.6-sol` using the available Codex CLI account. Its bundled dummy scheduler is not a production deployment or a general model-ranking benchmark. The PR reports 312 passing active tests (no failures/skips), 313 Windows engineering checks and a passing [Linux CI workflow](https://github.com/benitz94/Planwharf/actions/runs/39447322620). These are implementation-validation results, not measured consumer savings.

## One-model usage

Planwharf can still provide project planning, implementation/review sequencing, audit trails and checkpoint recovery when a user has **one** eligible model, provided its connection and roles support the required work. Smart Routing cannot choose among models when only one is eligible. An independent review performed by the same model is not the same as independent-model diversity.

For small tasks, direct CLI use may be faster and use fewer calls. For longer tasks, Planwharf may reduce manual coordination and lost progress. **Neither advantage has yet been established by a controlled head-to-head efficiency study.**

## Verification boundaries

- Real-model acceptance has been demonstrated for specified Codex CLI models and environments; this does not certify all available Grok, Antigravity, OpenAI-compatible, Anthropic-compatible or third-party accounts.
- Linux offline CI success does not establish a complete Linux or macOS GUI/provider end-to-end acceptance. Windows has the broadest browser and real-provider evidence.
- A model name visible in ChatGPT or another product does not establish an executable CLI model ID or entitlement. Verify the actual provider/account.
- Provider quotas and prices may be unknown or differ by billing model. Recorded invocations or tokens are not automatically money saved. A subscription's monthly fee is not a per-task charge.
- AI work may still produce bugs. Use actual tests, reviews and human inspection of important outputs. Account/provider rate limits, transient Windows file locks and partial failures need honest diagnostics.

## Planned efficiency benchmark — NOT yet demonstrated

A controlled study will compare **the same exact model** directly through its coding CLI against the same model operating under Planwharf. It should use equivalent prompts, independent clean repositories, repeated paired tasks of several difficulty levels, fair permissions, acceptance tests and all-in usage measurements (including Orchestrator planning/review calls). Measure wall-clock time, attempts, human interventions, quality, token/quota usage where available and *actual billed money* only where reliably observable.

Report **both wins and losses**, including overhead on short tasks, provider 429/cooldown effects and incomplete trials. Separate fixed subscription economics, measured usage, API-equivalent hypothetical rates and time saved. A single-model experiment is distinct from a multi-model Smart Routing experiment. Until completed and independently checked, **do not claim Planwharf saves users money, tokens or time**.

For benchmark contributions and future results, see GitHub Issues and the forthcoming separately reviewed benchmark evidence. No headline savings percentage is currently established.
