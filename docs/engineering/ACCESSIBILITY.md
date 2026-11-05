# Accessibility and responsive source audit

Inspected main d3fae01 and PR #7 7b70ea6 through Git source; JSON inventories are alongside this report. No actual browser, screen reader or zoom execution was available. Findings below distinguish concrete source defects from runtime questions.

| Priority | Finding and reproduction | Suggested correction | Ownership |
| --- | --- | --- | --- |
| P1 | Seven static dialogs have no explicit accessible name on both baselines. Open Edit plan, Grill, Import, New job, Auto start, Add project and Set place; inspect each dialog's computed accessible name. Source has h2 headings but no aria-labelledby/aria-label on dialog. | Give each existing heading a unique ID and reference it from aria-labelledby on its dialog. Verify announcement with a screen reader. | PR #7 index.html; integration deferred |
| P1 | Light theme primary tokens: white text on #0e8f86 has 3.968:1 contrast. Base buttons are 13px/550, below the large-text exception; normal text needs 4.5:1. Both inspected stylesheets retain these values. Reproduce with light-theme primary button and inspect computed colors, including hover. | Darken light-theme cyan for primary fills or choose darker foreground. Recheck all affected states against actual composited backgrounds. | PR #7 styles.css; deferred |

Positive source evidence: native buttons and dialogs, visible focus rules, labelled resize separators, polite home/toast regions, reduced-motion preference handling, responsive breakpoints and scrollable activity panels. PR Ship View sets workspace inert, focuses Exit, handles Escape and restores prior focus. These are implementation observations, not accessibility conformance passes.

Runtime acceptance handoff: use Edge then Chrome with sandbox enabled. Tab/Shift-Tab every route and dialog; Escape closes the active dialog and restores the trigger; screen-reader headings/names and live statuses must be useful. Resize a running activity panel while focused, append long content, and ensure polling preserves focus and scroll position. Test 320 CSS pixels, 200% and 400% zoom, short landscape viewport, both themes, keyboard-only Ship entry/exit and OS reduced-motion. Measure computed contrast on translucent/background scene composites. No broad UI redesign or overlapping patch was applied.

Run source inventory:

```
node scripts/engineering/accessibility-audit.cjs
node scripts/engineering/accessibility-audit.cjs 7b70ea6881b1fd5fd48c920ba4fd22cadb5efca9
```
