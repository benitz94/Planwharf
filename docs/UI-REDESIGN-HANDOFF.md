# UI redesign handoff — 2026-10-17

## Owner's requested direction

Build a calm, clear BridgeMind-inspired workspace based on
`output/mockups/planwharf-workspace-concept-04.png`. The sailing ship and port
must cover the entire background, not appear in a thumbnail. Use subdued
scenery and readable opaque panels. The captain watches the horizon through a
telescope while stopped and steers while real workers are alive. Flag selection
changes costume, especially the hat, and the ship/environment treatment. The
pirate is a Caribbean buccaneer in the style of Jack Sparrow, not a Viking.
Show the personalized name above the captain's head as a clickable game-style
nameplate. The name opens existing name/Reviewer settings.

## Implemented locally

- `workspace.css` and `workspace-ui.js`: compact view navigation, settings dialog,
  worker/history/models sections, quieter surfaces, responsive layout.
- `harbor-ui.js` and `harbor.css`: separate illustrated background, transparent
  vessel, five costumes with two poses, backend-driven voyage state, nameplate,
  immersive Ship view, motion settings and reduced-motion support.
- `assets/harbor/`: port-dusk.png, vessel.png, vessel-pirate.png,
  captains-caribbean.png. These are generated artwork, not execution evidence.
- `supervisor-ui.js`: broadcasts the saved supervisor name for the nameplate.
  The legacy default Supervisor is displayed as Captain; custom names are used
  verbatim as text. The existing backend remains the source of saved names.
- `index.html` and `orchestrator.html`: load the new stylesheet; unified index
  loads the workspace behavior. `server.cjs` serves explicit static asset paths.
- `layout-ui.js`: new layouts default to 240px rail and 60% conversation width;
  previously saved dimensions remain valid. `window-ui.js` excludes settings
  dialog controls when measuring the workspace header.
- `engine/app-updates.cjs`: allows packaged PNG assets only under assets/harbor.
  No update, release, commit or push was performed.

## Verification and evidence

- Syntax checks passed for the new JavaScript and changed server/update code.
- `node verify-harbor.cjs` passed.
- `node --test engine/app-updates.test.cjs`: 8 passed.
- `validation/harbor-redesign-browser-02/results.json`: complete browser suite
  PASS, no JavaScript or unexpected network errors; personal notebook unchanged.
  This run exposed a sprite-sheet clipping defect, subsequently corrected.
- `validation/harbor-redesign-final/`: final rerun after that correction, with
  additional asset-route, sprite-crop, activity-tab and nameplate checks. Check
  `results.json` for the final outcome; do not infer success from screenshots.
  Final outcome: PASS, no JavaScript or unexpected network errors, personal
  notebook unchanged.
- `validation/harbor-redesign-browser/results.json` records an earlier DevTools
  startup failure. Preserved as historical evidence.
- Browser fixtures mock worker transport and Reviewer responses. The reported
  single AI call is a fixture invocation, not a real provider call.

Useful actual app screenshots in `validation/harbor-redesign-final/`:
`orchestrator-dark-running.png`, `ship-view-pirate.png`,
`ship-view-running.png`, `workspace-models.png`, `workspace-history.png`,
`home-mobile.png`, and `browser-restarted.png` when generated.

## Resume

Latest workspace polish (owner said to proceed after accepting the vessel name):
worker cards keep status, exact provider/model, assignment and activity visible;
PID, internal model ID and execution metadata move into a native disclosure.
Diagnostics and Stop actions remain visible. Open technical disclosures persist
across backend snapshot refreshes. Project controls follow the worker project
filter; All projects restores the notebook-wide view. Repeated process/log
details are collapsed and the empty conversation guidance is shorter.
Long custom captain names in navigation are bounded with a full-name tooltip.
The approved ship illustration, inscription placement and font were preserved.

Verification: engine/worker-ui.test.cjs passed in standalone and embedded modes.
validation/harbor-workspace-polish-1792277932283/results.json is PASS for the full
backend/browser lifecycle, with no unexpected errors and notebook unchanged.
Final wording/metadata cleanup was then checked by
validation/harbor-workspace-final-1792278291697/results.json (PASS): native
disclosure persistence, provider identity, selected/all project filters, dark
and light text contrast, pause/stop checkpoint controls. No real provider calls.
The current UI screenshots are workspace-running-dark.png and
workspace-running-light.png in that final directory.

Current selected inscription font: **IM FELL English SC**, approved by the
owner after Cinzel. Locally bundled with its OFL license in assets/fonts,
regular weight 400, 18px SVG size, uppercase, aged gold, same curved path.
Latest placement adjustment moves the inscription farther right toward the
flatter mid-hull, reducing the baseline curvature almost to horizontal.
validation/harbor-historic-font-1792276533530/results.json is PASS, including
font loading, project-name changes and captain interaction. Its
captain-name-custom.png is the latest visual evidence. Earlier font notes
below describe superseded iterations, not the active face.

Latest font refinement: vessel inscriptions now use the locally bundled Cinzel
variable font at weight 600, uppercase, on the existing curved baseline.
The owner requested a stronger ship-lettering character. Font and OFL license
are in assets/fonts; explicit static serving and update packaging include them.
Google Fonts source is recorded in THIRD-PARTY-NOTICES.md.
validation/harbor-nautical-font-1792275579747/results.json is PASS, including
actual font loading and project-name updates. The targeted update-package
rejection test passed. A broader update-suite run with TEMP inside this Git
checkout had 5 failures; inspect its fixture/Git-checkout assumptions before
treating these as product regressions. The earlier 8-pass update result above
predates this font change.

The ship now carries the selected project's name painted on its forward hull,
using upright Palatino/Georgia serif capitals with wider letter spacing,
separate from the captain name. The owner's latest correction places the
inscription lower on the wooden planking and requires ALL CAPS for display;
the underlying project name and full SVG title remain unchanged.
The latest correction replaces the straight rotated inscription with an SVG
textPath following the forward hull's curved lower planking. Long names fit
within the bow inscription path. Browser validation and screenshots:
validation/harbor-curved-name-1792274795988 (PASS).
The harbor backdrop is mirrored so the pier sits behind the stern on the right;
the bow and captain's telescope face open water on the left. The initial
orientation/inscription check is in validation/harbor-bow-1792274056117 (PASS);
the inscription was then nudged inward to sit fully on the wood.
The label is part of the background and cannot cover or intercept controls.
Long display names are shortened after 32 characters; the full project name
is retained in the SVG title and the project data is unchanged.
`validation/harbor-vessel-name-1792273720244/results.json` is PASS including
switching projects and confirming the vessel label updates accordingly.

Latest owner refinement: the captain's name should resemble a restrained
Modern Warfare teammate label. Implemented as small muted green text and a
tiny marker, without a box or permanent customization subtitle. It retains a
24px keyboard-accessible click target, full-name tooltip and personalization
action. Labels yield to panels/controls and disappear when the character/name
would be obscured; they do not relocate over other controls.
`validation/harbor-nameplate-1792272460478/results.json` is PASS for styling,
occlusion, click-to-personalize, escaped custom names and narrow layout, with
no JS/network errors and personal notebook unchanged. See
`captain-name-custom.png` and `captain-name-workspace.png` in that directory.

Read AGENTS.md and the installed skill catalog first. Inspect current diffs;
this checkout already contained extensive owner changes before this task.
Preserve those changes, notebooks and historical evidence. No personal project
was dispatched during validation. Do not reset, overwrite, commit or publish.

First read the final browser results and inspect its desktop/mobile screenshots.
Then show the owner the real UI and ask for visual feedback before further
styling changes. In particular review nameplate placement over working panels,
captain scale, background brightness and density of the activity panel.
The painterly pirate vessel is a dedicated sprite; other themes currently use
the ivory-sail vessel with different color treatments, flags and costumes.
There are no storm or weather simulation claims and no 3D scene.

If rerunning browser checks, use a NEW validation directory (the script creates
fixture folders) and set PLANWHARF_TEST_NO_SANDBOX=1 for this environment.
Set TEMP and TMP to a project-local validation folder before running tests.
The final browser test takes several minutes because it exercises real backend
polling, pause, checkpoints, restart and failure states with isolated fixtures.

Image generation used the built-in imagegen tool. It automatically writes its
original outputs on C:, which the owner subsequently authorized provided they
are copied into this project. All consumed artwork is now in assets/harbor.
The unused first costume study is in output/design-source/captains-initial.png.
