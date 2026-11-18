## Summary

The native app can switch between English and Italian in Settings → Look → Language.
The selection is saved in `native-window.json`; English remains the default.

```text
Language picker → saved preferences → rebuilt window
                                 → translated labels and date/number culture
```

This branch restores completed job 17 commit `074f341` onto refreshed main and
adds its missing plan status and provenance row. The placeholder regression check
also catches missing lower-numbered arguments and duplicate arguments, even when
the maximum argument index still matches.

## Evidence

- **Before:** job 17 existed only on
  `planwharf/implement-job-17-on-feat-languages-from`; the work plan still said
  `To do`, and `feat/languages` did not exist locally or on origin.
- **After:** local `feat/languages` contains the completed implementation and the
  plan says `Done, waiting for review`.
- `dotnet test native/Planwharf.Engine.Tests`: 474 passed, none failed or skipped.
- `dotnet test native/Planwharf.Native.Tests`: 159 passed, none failed or skipped.
  This includes isolated preference persistence/recovery, Italian headless
  rendering, language switching, command compatibility and preserved project data.
- The first simultaneous native/engine builds collided on their shared output
  DLL. Both suites subsequently passed when their builds ran sequentially.
- `node scripts/engineering/ci.cjs`: passed with exit code 0; all listed syntax,
  engine, worktree and engineering checks reported PASS.
- Manual platform checks and a complete visual clipping audit were not run.

### Engine text left for a later job

Engine-provided text remains verbatim in either language: lead questions and
answers, step titles, worker result summaries and findings, activity descriptions,
alert titles/messages, waiting-for-quota explanations, and API error details.
Frequently visible policy reasons include:

- `Configured remaining quota threshold cannot be verified: provider quota is unread.`
- `Remaining quota stop threshold reached (...% remaining / stop at ...%).`
- `Low provider quota (...% remaining / warning at ...%).`

These are engine output, separate from native labels derived from status codes.
Personal content and historical execution evidence are not translated.

## Merge Danger

**Door:** two-way

**Blast Radius:** native

The inherited implementation puts the shared catalog in
`native/Planwharf.Engine/Strings.cs`, rather than the sheet's requested
`native/Planwharf.Native/Strings.cs`, and routes several C# engine errors through
it. English remains the engine default; reviewers should assess this scope
difference before merging. No dependency was added. Native globalization is
enabled for Italian dates and numbers.

Publication must use the authorized owner/lead path: `git push -u origin HEAD`
failed in this attempt with `remote helper 'planwharf-push-blocked' aborted session`.
The local branch and committed review material are preserved. From a lead/owner
session with publishing enabled, run:

```sh
git push -u origin feat/languages
gh pr create --base main --head feat/languages --title "Job 17: English and Italian in the native app" --body-file docs/engineering/job-17-pr.md
```

Neither this PR nor job 16 is authorized for an
automatic merge. Job 18's sheet starts only after both jobs are merged, and its
implementation remains subject to sheet review.
