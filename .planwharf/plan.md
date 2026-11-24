# Work plan

## Goal

Write Job 44’s phase 5 specification sheet from refreshed `main` on `port/44-sheet`, covering `execution.cjs`, `project-home`, and the proposed orchestrator split, with tests, parity requirements and allowed files. Obtain independent verification, update the progress documents, commit without pushing or merging, and stop for the owner’s review.

## Base

origin/main 0154fc6

## Branch

port/44-sheet

## Steps

- [x] Build (GPT-6 Sol) — not committed
- [x] Build (GPT-6.1 Terra) — not committed
- [x] Build (Gemini 3.8 Flash (High)) — committed; independent verification remains
- [x] Verify (Codex gpt-6.1-sol, attempt-106) — reviewed 1f05291; added specification corrections and omission checks

## Rules

No push or merge. Files outside the goal are not touched.

## Done

Goal: Write Job 44’s phase 5 specification sheet from refreshed `main` on `port/44-sheet`, covering `execution.cjs`, `project-home`, and the proposed orchestrator split, with tests, parity requirements and allowed files. Obtain independent verification, update the progress documents, commit without pushing or merging, and stop for the owner’s review.
Summary: Prepared the Phase 5 execution and project home specification sheet (`native/porting/jobs/44-phase5-execution.md`), covering C# module mappings, lifecycle and Git containment watch-points, pure and scenario parity requirements, and the 5-concern orchestrator split proposal. Added durable sheet tests (`engine/job44-sheet.test.cjs`) with omission and mutation controls. Updated progress documentation (`docs/NEXT-WORK.md`, `docs/BUILT-WITH-PLANWHARF.md`, `native/porting/PLAN.md`) with accurate authorship, test evidence, and phase reconciliation. Verified via Job 44 tests (5), sheet and parity tests (26), read-only recorder checks (103 scenarios), and engineering CI. Committed on port/44-sheet without pushing or merging; handed off to independent verifier.
Branch: port/44-sheet
Pushed: no

Independent verification: Codex `gpt-6.1-sol` handled attempt-106 after the configured Antigravity verifier was rerouted. No Claude verification is claimed. Added explicit JavaScript exceptions for addition kill failure, paused-work additions first decision answers and unrestricted delegated answers. Job 44 checks now pass 7 tests; prior sheet/parity checks pass 26 tests; targeted lifecycle/recovery/containment suites pass 34 tests; both read-only parity recorders pass (103 existing scenarios). The independent engineering CI run passed all 154 engine suites and three engineering test scripts, but its committed-branch patch gate failed on trailing blank lines in the original sheet. The sheet EOF was repaired; `git diff --check origin/main` passes. The final verification handoff records the committed-branch gate rerun separately; the full CI run is not claimed as clean. C# build/publish and live-program tests were not run for this specification-only job. Stop for owner review; Job 45 remains pending.
