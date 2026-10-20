# Commands, evidence, and limitations

## Recorded environment and commands

Baseline: `d6c49d0952ded776a45b444e81788ce0ae435fbe`, Windows x64, Node 24.18.0, Chrome 155.0.8059.80, Codex CLI 0.144.3. Account login was inspected only as status; no credential or account identifier is published.

The original startup wrapper called the stock `planwharf.cjs.main` with:

```text
--plan <AUDIT_ROOT>/notebook.json
--attempts-dir <AUDIT_ROOT>/attempts
--port 0
--no-window
```

`scripts/audit/start.cjs` packages that same startup path with a guard against unrelated projects. The audit began from an empty calculator folder. Only `dogfood-fixture.cjs`, launched by Planwharf after approval, created its source files. The source fixture contains the deterministic implementation; copying its source into this PR is not presented as AI creation.

Actual executed controller stages, in order (paths normalized):

```text
real-provider-probe: init, create, ask, select-default, ask
dogfood: setup, build, review, recover-review, faults, concurrency
browser-audit
dogfood: checkpoint, quit
normal application restart on the same notebook
dogfood: resume, advanced, advanced-continue, deadline, containment
state-audit
dogfood: controls
```

The first `review` continuation incorrectly tried Stop on an already failed task; the first `recover-review` used a direct proposal still blocked by the old running notebook task. The successful continuation used a new explicit Fix chat request. The first resume check expected HTTP 400; the application correctly returned 409. Missing/moved-folder proposals were initially assumed to reject creation, while the actual application accepts preparation and blocks/fails dispatch. These were harness assumptions, corrected and documented rather than relabeled as product defects. One earlier script also used a hidden legacy navigation control and waited for button enablement instead of the actual ask response. No successful AI outcome was inferred from those attempts.

## Reproduce in a fresh disposable directory

Run from the repository root. Requires Node, Git, an installed Chromium-compatible browser and Playwright available to the test process. These are audit prerequisites, not new production dependencies. Set `PLANWHARF_TEST_BROWSER` to the installed browser executable. If Playwright is in the desktop runtime bundle, set `CODEX_PRIMARY_RUNTIME_NODE_MODULES` to its node_modules directory; otherwise normal module resolution is used.

Use a new absolute directory outside personal projects. Do not reuse the included historical evidence as a live notebook. In one terminal:

```powershell
node scripts/audit/start.cjs <NEW_DISPOSABLE_DIRECTORY>
```

Leave that server running. In another terminal, first create the calculator using the actual UI; this stage does not run a paid provider:

```powershell
node scripts/audit/real-provider-probe.cjs create <NEW_DISPOSABLE_DIRECTORY>
```

Optional real-provider stages (`init`, `ask`, `select-default`, `ask`) perform actual bounded inference attempts with the recorded exact model routes. Review the scripts and choose the intended account/model before opting in. Model access may differ elsewhere; do not silently substitute a model or label deterministic output as AI validation.

For the controlled-process audit:

```powershell
node scripts/audit/dogfood.cjs setup <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/dogfood.cjs build <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/dogfood.cjs review <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/dogfood.cjs faults <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/dogfood.cjs concurrency <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/browser-audit.cjs <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/dogfood.cjs checkpoint <NEW_DISPOSABLE_DIRECTORY>
node scripts/audit/dogfood.cjs quit <NEW_DISPOSABLE_DIRECTORY>
```

Restart with `start.cjs` against the same directory. Before the recorded resume run, a harmless local Git repository/commit was created for `Audit-Dependencies/receipt.txt`; that intentional change caused confirmation to be required. The `resume` stage expects this changed baseline and writes a controlled resume marker. Without a changed baseline its confirmation assertion is not the same experiment.

```powershell
git -C <AUDIT_ROOT>/Audit-Dependencies init
git -C <AUDIT_ROOT>/Audit-Dependencies add receipt.txt
git -C <AUDIT_ROOT>/Audit-Dependencies -c user.name=Codex -c user.email=codex@openai.com commit -m "Record disposable audit receipt"
node scripts/audit/dogfood.cjs resume <AUDIT_ROOT>
node scripts/audit/dogfood.cjs advanced <AUDIT_ROOT>
node scripts/audit/dogfood.cjs deadline <AUDIT_ROOT>
node scripts/audit/dogfood.cjs containment <AUDIT_ROOT>
node scripts/audit/state-audit.cjs <AUDIT_ROOT>
node scripts/audit/dogfood.cjs controls <AUDIT_ROOT>
node scripts/audit/dogfood.cjs quit <AUDIT_ROOT>
```

The stage scripts are opt-in diagnostic tools, not an atomic test runner: inspect a failed stage before continuing. They use a single isolated application store and preserve evidence of failures. `recover-review` and `advanced-continue` are explicit recovery entry points for the recorded investigation, not steps to hide failures. The directory guard verifies the target notebook path; never point `server.json` or evidence files at personal data. Native configured CLIs remain outside an OS filesystem sandbox.

## Exact supplemental test commands

```text
node --test engine/worker-containment.test.cjs engine/project-containment.test.cjs engine/home-containment.test.cjs engine/phase2-deadline.test.cjs engine/skill-execution-delivery.test.cjs engine/supervisor.test.cjs engine/supervisor-role.test.cjs engine/captain.test.cjs engine/soundtrack.test.cjs engine/soundtrack-library.test.cjs engine/soundtrack-http.test.cjs engine/soundtrack-assets.test.cjs
node scripts/engineering/ci.cjs
node --test docs/audits/2026-10-20-calculator/calculator/calculator.test.cjs
```

Initial supplemental regression run: **86 tests, 83 passed, 0 failed, 3 skipped**. Skips require Windows symlink capabilities unavailable to that run. They were not converted into passes. Initial engineering CI: **211 passed, 0 failed** before adding the packaged startup/probe and copied artifact files. Final counts are in [verification.json](verification.json).

Independent browser collector: **12 checks, 11 passed, 1 failed** (dialog naming), plus **one actual project-home HTTP 500** recorded separately. The initial collector returned exit 0 while preserving finding counts; the packaged collector now returns nonzero for observed failed checks/errors/HTTP failures. Product failures remain findings, not green CI claims. State/Captain audit: **8 passed**, based on actual execution state. Calculator tests: **12/12** after correction/re-review; injected defect run: **11 passed, 1 failed**.

The browser checks did not prove physical audio output, human listening, natural autoplay denial, multi-browser equivalence, or a complete accessibility conformance claim. No live AI Worker or Reviewer succeeded in this environment. No mocked API success is counted as real AI execution.

## Publication and cleanup

Only sanitized summaries, reviewed screenshots and the four worker-created calculator files are included. The raw 40+ MiB HTTP journal, notebook, attempts, provider account keys and browser profiles stay private. Evidence hashes in [evidence-sha256.json](evidence-sha256.json) cover the published artifacts. A normal application Quit followed confirmation that no owned audit worker was live. No production code fix, merge, release or CLA acceptance is part of this audit.
