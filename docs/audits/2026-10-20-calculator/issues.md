# Reproduced issues and boundaries

All reproduction folders below are disposable. Findings apply to main `d6c49d0952ded776a45b444e81788ce0ae435fbe`; no fixes were applied during observation.

## I-01 — High — Disabled Soundtrack bar obstructs chat Send

**Component:** Workspace composer / fixed Soundtrack player.

**Steps:** Open the actual application in Chrome at 1440×1000. Select a project, choose Workspace, enter a request, and scroll Send into view with the default Soundtrack state (disabled). Attempt a normal pointer click on Send.

**Expected:** The composer and Send remain reachable independently of audio state.

**Actual:** The fixed Soundtrack bar covers Send. Playwright repeatedly reports the Soundtrack volume range intercepting the click. The recorded Send rectangle was x≈865, y≈925, width≈57, height≈40. Keyboard focus + Enter submitted successfully; pointer success was not fabricated with a force-click.

**Evidence:** [live viewport crop](03-send-covered-by-soundtrack.png); actual browser geometry/hit-target trace retained privately.

**Likely cause (hypothesis):** The player’s fixed bottom position and z-order lack corresponding composer clearance. **Recommended correction:** Reserve space for the player in the scroll/compose layout, or collapse inactive transport; verify pointer and keyboard reachability at desktop/mobile/zoom sizes, including disabled audio.

## I-02 — High — Available model routes reject actual inference

**Component:** Provider discovery, account/model selection and native CLI compatibility.

**Steps:** With Codex CLI 0.144.3 reporting a ChatGPT login, use production discovery in a new notebook. Select discovered `gpt-6-luna`, configure only that route, and submit the calculator request. Explicitly change to the installed CLI’s configured default `gpt-6.1-sol` and repeat once.

**Expected:** A route shown as available is usable, or clearly distinguished as enumerated/configured but inference-unverified, with actionable compatibility/access guidance.

**Actual:** Both real child processes returned HTTP 400: the exact model “is not supported when using Codex with a ChatGPT account.” No successful AI response or AI-generated calculator resulted. The change to the second model was explicit, recorded and bounded; no silent fallback occurred.

**Evidence:** [real-provider-outcomes.json](real-provider-outcomes.json). Durations were approximately 4.3s and 3.2s. No reliable token charge or monetary usage was reported, so none is invented.

**Likely cause (hypothesis):** Model-catalog/account/CLI-version mismatch or an enumerated model name unavailable on this inference route. The underlying account/provider cause was not isolated. **Recommended correction:** Distinguish discovered/configured identity from inference validation, reconcile installed CLI compatibility with catalog names, invalidate rejected model access, and display the provider’s exact unsupported-route reason.

## I-03 — Medium — Unsupported model presented as quota failure

**Component:** Provider diagnostics and issue guidance.

**Steps:** Perform I-02. Read both the provider’s unsupported-model response and Planwharf’s diagnostic banner.

**Expected:** Model/access/compatibility diagnosis; no unsupported claim that quota is exhausted.

**Actual:** Both requests were labeled `PROVIDER_USAGE_LIMIT`, directing the user to quota/settings despite the retained fatal response naming an unsupported model. This gives the wrong recovery path.

**Evidence:** [real-provider-outcomes.json](real-provider-outcomes.json). **Likely cause (hypothesis):** Classification operates on broad output and ordered regular expressions; `provider-diagnostics.cjs` recognizes “unsupported” but not the spaced “not supported” wording in its model pattern. That source observation alone does not prove why the full CLI output matched the quota branch. **Recommended correction:** Prefer the structured fatal error, recognize “not supported,” and avoid unrelated log text overriding model/access errors. Add a regression at the real captured error seam.

## I-04 — High — Intermittent Home HTTP 500 while navigating

**Component:** `/api/project-home`, Home state/persistence/error reporting.

**Steps:** Run the fresh audit sequence through three controlled active projects. Use `browser-audit.cjs` to switch projects, open/close settings, move through Workspace and Ship, and reload after selecting a corrupt temporary audio file.

**Expected:** Home requests succeed or return a specific recoverable conflict without losing the visit view.

**Actual:** One actual browser request returned HTTP 500 with `{"error":"Failed to read or save file. No revision was added."}`. Other requests succeeded and no browser JavaScript exception was recorded. This is intermittent; the audit does not claim every replay reproduces it or that data was lost.

**Evidence:** `failedRequests` in [browser-audit.json](browser-audit.json), separate from the 11 passed/1 failed capability checks. The failing request was not mocked. **Cause:** Unresolved. Concurrent persistence or another Home computation error is a hypothesis, not a finding. **Recommended correction:** Capture bounded internal error category/stack in local diagnostics, construct a tight reproduction under concurrent Home visits and persistence, then fix at the reproduced seam. Do not suppress the request from acceptance evidence.

## I-05 — Medium — Eight unnamed dialogs

**Component:** Revision, Grill, import, task, Auto start, project, folder-link and project-import dialogs.

**Steps:** Open Add project through the real UI. Inspect Chrome’s accessibility tree. Also inspect the remaining native dialog naming attributes.

**Expected:** Each dialog exposes a useful accessible name tied to its visible heading.

**Actual:** Open Add project has `role=dialog`, `name=""` in Chrome’s accessibility tree. Eight dialogs lack explicit accessible names: `revision-dialog`, `grill-dialog`, `import-dialog`, `task-dialog`, `auto-start-dialog`, `project-dialog`, `place-dialog`, `import-project-dialog`.

**Evidence:** [dialog-accessibility-tree.json](dialog-accessibility-tree.json), failed naming check in [browser-audit.json](browser-audit.json). This is not a complete screen-reader/WCAG audit. **Cause (source-supported):** Visible headings are not referenced through `aria-labelledby`/equivalent labels. **Recommended correction:** Add unique heading IDs and explicit dialog naming; verify computed names and keyboard focus restoration.

## I-06 — Low — Distinct anomalies produce identical-looking notices

**Component:** Ship notifications / Supervisor presentation.

**Steps:** Run the non-zero and malformed-output projects, then enter Ship View while another project runs.

**Expected:** Users can distinguish a new failure from repeated descriptions of the same failure.

**Actual:** Separate alerts render as identical “Supervisor anomaly detected” notices for the same project, alongside worker/assignment failure notices. Supervisor alert IDs themselves were stable on repeated reads; this is not proof that the backend creates duplicate events.

**Evidence:** [actual running Ship screenshot](ship-three-projects-running.png), `supervisor-observed` in [workflow-events.json](workflow-events.json). **Likely cause (hypothesis):** Distinct alert kinds share one generic display label. **Recommended correction:** Show the reason or group related failure notices without discarding distinct raw evidence.

## I-07 — Medium boundary / expectation mismatch — cwd is not process confinement

**Component:** Configured CLI permissions, folder containment and foreign Git metadata.

**Steps:** Configure the explicitly labeled audit CLI with empty restricted arguments in the isolated instance. Give it a selected folder and place a harmless canary in its sibling audit root. Run `foreign-read`. In a separate disposable folder, create a `.git` file pointing to the dependency project’s `.git` and approve a receipt-only worker.

**Expected boundary:** Application inspection must not follow foreign Git metadata. Any claim of protection against arbitrary child commands would require a real OS/process sandbox.

**Actual:** The application’s repository inspector returned `local.git=false` for redirected metadata, which is the intended inspection safeguard. Nevertheless, the configured child ran in the selected directory and could read the sibling canary; redirected `.git` did not itself prevent worker launch. No personal data was read and no foreign repository command was executed. The source already documents that launch-path validation is not an OS sandbox.

**Evidence:** `configured-cli-foreign-read` in [workflow-events.json](workflow-events.json); [final-boundaries.json](final-boundaries.json). **Cause (source-supported):** `requireWorkerWorkspace` verifies canonical working-directory equality; `safeGitWorkspace` guards application inspection. Neither confines arbitrary native CLI operations. **Recommended correction:** Keep this boundary explicit in user-facing setup; where stronger isolation is intended, require a verified sandbox or reject risky metadata before dispatch. Do not report this audit as proof that arbitrary foreign-directory access is rejected.
