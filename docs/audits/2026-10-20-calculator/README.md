# Planwharf calculator dogfooding audit — 20 October 2026

**Assessment: NOT READY FOR REAL USE in the tested first-time delegation workflow.** The orchestration engine can coordinate controlled child processes, persist checkpoints, resume unfinished slices, and gate review. A real AI-generated calculator was **not** achieved: both attempted Codex model routes were rejected. The Send control was obstructed, and a Home request returned an intermittent HTTP 500. This judgment concerns the observed environment and workflow, not every provider/account combination.

Audited authoritative main: `d6c49d0952ded776a45b444e81788ce0ae435fbe`. No open upstream PRs were returned at inspection. Windows x64, Node.js 24.18.0, installed Chrome 155.0.8059.80, Codex CLI 0.144.3. A new notebook, attempts store, browser profiles and disposable folders were used. No production application files were repaired during the audit. No personal project was used. No successful paid/model inference or external API billing was established.

## Evidence boundaries

| Evidence class | What ran | What it establishes |
| --- | --- | --- |
| Real provider transport | Two actual Codex child-process calls through the production runner: `gpt-6-luna`, then explicitly selected CLI default `gpt-6.1-sol` | Both returned HTTP 400 unsupported-model errors; neither produced a useful AI answer. Login and model enumeration do not prove inference access. |
| Simulated execution, real processes | An explicitly named `AUDIT deterministic CLI (not AI)` connection, configured through the normal endpoint in the isolated notebook | Real stdin, cwd, process IDs/exits, structured Results, files, tests, review rejection, persistence and cancellation. No commercial-provider impersonation. |
| Independent browser observations | Actual application HTTP state and real controlled workers; no injected execution events or mocked orchestrator responses in these audit scripts | UI, calculator input, media time, cross-tab ownership, Ship states and rendering. |
| Supplemental regression tests | Existing contained fixtures and unit/integration suites | Regression signals only; not proof of end-user AI delegation or independent AI review. |

Raw notebook/attempt logs, prompts, account hashes, user filesystem paths and browser profiles remain outside this PR. Published JSON uses `<AUDIT_ROOT>` and `<PLANWHARF_CHECKOUT>` placeholders. The Send screenshot is a live viewport crop excluding account/quota UI. Screenshots show actual rendering; the calculator was not built outside Planwharf and imported as success.

Start with [workflow-events.json](workflow-events.json), [real-provider-outcomes.json](real-provider-outcomes.json), [browser-audit.json](browser-audit.json), [state-audit.json](state-audit.json), [issues.md](issues.md), and [the calculator artifact](calculator/README.md).

## Functional audit matrix

PASS applies only to the scope stated in the evidence column. BLOCKED and NOT TESTED are not passes.

| Capability | Status | Evidence / practical limit |
| --- | --- | --- |
| Stock server startup and normal Quit | PASS | Actual `planwharf.cjs` entry, loopback listener, normal Quit, process exit, restart on same notebook. |
| Privacy / local-only operation | PARTIAL | Backend bound to loopback; startup model/quota/update discovery uses external services. Calculator is offline. Full network-payload privacy was not independently audited. |
| Desktop packaged executable/full-screen browser launch | NOT TESTED | Audit used the supported no-window server path plus installed Chrome automation. |
| Account login/model enumeration | PARTIAL | Login reported; production discovery listed models, but both tested routes failed inference. |
| Real provider instruction/output transport | FAIL | Two actual CLI errors; no successful reasoning or implementation. See I-02/I-03. |
| Account switching / other commercial providers | BLOCKED | No other provider login or credentials established; no account mutation attempted. |
| Provider roles and policy persistence | PASS | Normal controls in isolated store; persisted across restart. Ineligible CLI Supervisor role was rejected. |
| Add project and legitimate folder link | PASS | Calculator created with actual Add project dialog; subsequent dummy projects used normal endpoints. |
| Selection/switching/project identity | PASS | Three simultaneous projects and rapid UI selection, with separate linked folders/processes. |
| Project rename | NOT TESTED | No verified rename run. Folder reconfiguration was exercised separately. |
| Folder moved/relinked | PASS | Missing linked folder yielded blocked work/relink guidance and no worker receipt; explicit relink plus a fresh work request completed. Proposal HTTP acceptance alone was not counted as execution. |
| History/export/import | PASS | Calculator history exported and imported as a new identity; imported folder absent until explicit relink. No source or live worker transfer claimed. |
| Natural-language request to actionable work | PARTIAL | Exact calculator request received a coordination response but no MegaTask; Start, Skip Grill and approval were needed. A later explicit “Fix the …” request prepared correction work. Real AI route blocked. |
| Grill me | PARTIAL | With Manual selected Skills unavailable, request returned a specific eligibility/reapproval message. Successful AI grilling and Grill with docs were not verified. |
| MegaTask/SubTasks/dependencies | PASS | Two dependent slices; first completed, second interrupted; first had one attempt and was not replayed after restart. |
| Approval / Manual / Automatic modes | PASS | No calculator source before approval. A fresh Automatic-mode proposal also waited for approval; mode returned to Manual. |
| Reject a proposal before launch | NOT TESTED | Reviewer rejection was tested; proposal rejection was not a separate UI scenario. |
| Worker launch/cwd/instruction delivery | PASS | Actual deterministic child receipts include cwd, argv, PID, prompt hash and result identity. Scope is configured CLI transport, not AI behavior. |
| Calculator creation/tests/GUI | PASS | Created only in approved child execution, 12 calculator tests, actual GUI arithmetic/decimals/keyboard/reset/error checks. Deterministic fixture provenance is explicit. |
| Reviewer sees files/catches defect | PARTIAL | Separate AUDIT child inspected files and executed tests; injected divide-by-zero defect caused 11 passes/1 failure and FAILED review. Correction and re-review passed 12/12. Not independent AI review. |
| Completion before required review | PARTIAL | Build-only task could complete before separately requested review. Audit explicitly added review; no mandatory universal review gate was demonstrated. |
| Supervisor factual counts/anomalies | PASS | Actual stopped/running process evidence, zero active at cleanup, stable alert IDs on repeated reads, and failed-job alerts. |
| Supervisor intelligent advisory/stall detection | BLOCKED | No supported API advisory connection configured; CLI models are intentionally ineligible. Long-duration stall detection was not tested. |
| Supervisor notification clarity | PARTIAL | Different alerts can produce visually identical “Supervisor anomaly detected” notices; see I-06. |
| READY/approval → RUNNING → checkpoint → RESUME → COMPLETE | PASS | Real controlled processes, persistent checkpoint and changed-Git HTTP 409 confirmation. Transitional PREPARING/PAUSING durations were not independently timed. |
| Non-zero/malformed/missing Result | PASS | Actual processes failed; exit zero without valid structured evidence did not become completed work. |
| Hang/Finish-by deadline/Stop | PASS | Actual waiting processes, checkpoint, termination of owned PIDs. |
| Server restart after checkpoint | PASS | Same durable store, explicit changed-repository confirmation, remaining slice resumed once. |
| Browser reload during execution | PASS | Backend worker remained alive after real browser reload. |
| Three isolated active projects | PASS | Three real owned child processes in three linked directories; third started after the first two. No AI concurrency claim. |
| Local Git detection/diff/commit | PARTIAL | Disposable dependency repo initialized/committed; changed HEAD and two untracked files were reported before resume. Application auto-commit/push was not claimed. |
| Invalid folder relink | PASS | Nonexistent/traversing-to-nonexistent path rejected as “Folder not found.” Not a claim that all traversal/symlink attacks were exercised. |
| Foreign Git metadata | PARTIAL | App inspector refused redirected `.git` metadata (`local.git=false`); configured worker still launched and completed in the selected directory. Source launch validation is not an OS sandbox. |
| Arbitrary worker access outside project | PARTIAL | Controlled CLI read a harmless sibling canary. Project cwd validation does not confine arbitrary process filesystem access; I-07 documents this boundary. |
| Skills catalog/approval/manual selection/persistence | PASS | Project-local Skill discovered, hash approved, selected, retained across restart. |
| Actual Skill execution delivery | PASS | Marker observed by the real child; delivery metadata reports stdin submission and honestly leaves loaded/followed unverified. Other-project isolation has supplemental test coverage, not a second live semantic-use claim. |
| Missing selected Skill | PASS | Temporarily missing selected file prevented launch and produced FAILED execution. Restored file still required current eligibility/approval for Grill. |
| Overview/Workspace/Supervisor navigation | PARTIAL | Views rendered; Send obstruction and Home error impaired the normal workflow. |
| Friendly/Technical assignment panels | NOT TESTED | Not independently exercised as user interactions in this audit. |
| Keyboard/settings/mobile reflow | PASS | Settings keyboard open/close and 390px reflow. No full assistive-technology conformance claim. |
| Dialog accessible names | FAIL | Eight dialogs lack explicit names; opened Add project dialog has empty Chrome accessibility-tree name. I-05. |
| Home/error feedback | FAIL | Browser observed `/api/project-home` HTTP 500 with generic “Failed to read or save file.” Intermittence preserved; I-04. |
| Ship selected-project state/idle/helm/paused/completed | PASS | Actual backend states, rendered screenshots, helm transforms sampled at two times. |
| Calm/storm steering/reduced motion/off | PASS | Actual running worker, manual decorative weather, changing wheel transforms, reduced-motion and disabled-animation settings. No fabricated storm/failure association. |
| Soundtrack default/explicit Play/navigation/Ship | PASS | Fresh context silent, enable alone silent, actual media time advanced only after Play, navigation continuity observed. |
| Soundtrack three projects/two tabs | PASS | One player in the window; exactly one unpaused media element across two same-origin tabs. Second tab could not acquire playback. Scope excludes separate profiles/devices and physical loudspeaker measurements. |
| Soundtrack modes/playlists/personal persistence | PARTIAL | Adaptive live-state selection, sticky Pause, Stop and corrupt personal WAV feedback verified. The full Continuous/Manual editing/persistence matrix was not requalified here; existing tests are supplemental. |
| Physical speaker output/autoplay denial | BLOCKED | Headless browser observation cannot establish human hearing; natural policy denial not reproduced. |
| Multi-browser compatibility | NOT TESTED | Chrome only. |

## Fault injection results

| Requested scenario | Status | Observed outcome |
| --- | --- | --- |
| Missing executable | PASS | Configuration/proposal accepted, then execution FAILED; no fixture child receipt. Availability alone was not treated as proof of invocation. |
| Non-zero exit | PASS | Exit 7 propagated to failed work. |
| Hangs/timeouts | PASS | Controlled wait stopped; separate Finish-by deadline checkpointed and terminated owned PID. |
| Malformed output | PASS | Invalid JSON/no result rejected. |
| Worker disappears | PASS | Process exited without Result; failed rather than completed. |
| Folder moved | PASS | Launch blocked; re-link recovered through normal controls. |
| Browser reload | PASS | Controlled work survived. |
| Restart after checkpoint | PASS | Completed slice one attempt, resumed slice two attempts. |
| Invalid calculator operation | PASS | Actual GUI error and actual core tests. |
| Reviewer rejection | PASS | Deterministic reviewer ran actual tests against the injected defect. |
| Two concurrent / third joins | PASS | Actual three-process/folder evidence; no mocked liveness. |
| Local audio unavailable | PARTIAL | Corrupt selected WAV gave feedback; reload remained silent. Original-file move/deletion with consented retained audio was not separately exercised. |
| Foreign directory | PARTIAL | Application inspector rejects foreign Git metadata; arbitrary configured CLI can read a sibling test canary. |

## Workflow chronology and interventions

1. Created isolated stock application and calculator via Add project. Configured one bounded real route in the disposable store, not a personal notebook.
2. Current visible navigation is Workspace; an initial script targeted a deliberately hidden legacy Show Orchestrator control. That was a harness mistake, not a product failure.
3. Send was covered by Soundtrack. Keyboard focus + Enter was an explicit human-style workaround; no force-click or DOM event dispatch was used to claim pointer success.
4. Two real Codex calls failed. Exact models, durations and provider responses are preserved without account identifiers.
5. Disabled real model roles in the audit notebook, configured clearly labeled deterministic CLI, and approved a local Skill.
6. Natural-language response alone did not create a job. Explicit Skip Grill, Start and approval produced the calculator through the application. Calculator source did not exist before approval.
7. Injected one reversible divide-by-zero defect. Separate reviewer child ran actual tests and rejected it.
8. Stop on already failed work was rejected; direct new proposal returned 409 while its notebook task remained running.
9. Ran safe fault projects and started three waiting projects, each in its own folder.
10. A fresh “Fix the …” chat request superseded the failed calculator task and enabled approved correction. Separate re-review passed. This intervention is part of the usability assessment.
11. Exercised the calculator GUI and UI/audio against the three actual live project states.
12. Stopped the three projects, checkpointed dependent work, quit normally, added a harmless commit in the disposable dependency repo, and restarted the same application store. HTTP 409 required confirmation before resuming; no completed slice replayed.
13. Exported/imported/relinked, tested moved/missing/foreign boundaries and the deadline, and captured actual Captain state/motion.
14. Tested Automatic approval gating and missing Skill behavior. The later intentional missing-Skill probe left the audit notebook's calculator project failed; its calculator files were unchanged from the accepted review and still passed 12/12. This state was preserved rather than reset to manufacture a green project.
15. Confirmed zero owned live workers and quit normally before publication.

Audit harness adjustments were limited to correct visible selectors, waiting for the actual HTTP response rather than an enabled button, correct HTTP 409 expectations, bounded assertion output, and recognizing that accepted proposals can remain safely blocked until launch. No production defect was silently repaired. Initial harness failures remain in private raw evidence; they are distinguished from product findings above.

## Ten readiness answers

1. **Can it create and complete a project from natural language?** Not demonstrated with real AI here. The deterministic workflow required Start/approval and separate review/correction interventions.
2. **Does it launch and coordinate Worker processes?** Yes, verified with actual child PIDs/cwd/stdin/Results; deterministic execution only.
3. **Can it resume safely?** Yes for the tested persisted dependency checkpoint and explicit changed-Git confirmation. Not a universal arbitrary-edit proof.
4. **Three isolated active projects?** Yes in this controlled process test; no assertion about simultaneous real AI cost/quality.
5. **Do Skills reach agents?** The actual CLI received the unique marker. Semantic understanding and compliance remain unverified.
6. **Is Supervisor truthful?** Observed counts/alerts matched process evidence and repeated IDs stayed stable. Intelligent API advisory and long-duration stall inference are unverified.
7. **Does Reviewer catch defects?** The deterministic reviewer detected an actual test failure. Independent AI judgment was not established.
8. **Do Ship/Soundtrack reflect execution?** Tested live states, steering and single-origin playback ownership worked; the fixed audio UI obstructed Send.
9. **Can a first-time user delegate without developer assistance?** Not reliably in this environment: unsupported routes, misleading diagnosis, blocked Send, generic Home error and recovery detours required intervention.
10. **Five priorities?** Fix Send obstruction (I-01), validate account/model route usability (I-02), correct unsupported-model diagnostics (I-03), diagnose Home 500 (I-04), and name dialogs (I-05).

## Verification and reproduction

See [reproduce.md](reproduce.md) for exact commands, prerequisite/test-only dependencies, harness adjustments and result counts. Passing regression checks did not override product failures. All test folders are disposable; raw evidence is not automatically suitable for publication.

No merge, release, global provider reconfiguration or CLA acceptance was performed. Targeted production corrections are recommendations in [issues.md](issues.md), not changes in this PR.
