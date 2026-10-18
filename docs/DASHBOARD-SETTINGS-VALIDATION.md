# Dashboard, project skills and launch browser validation

Validated locally on Windows on 2026-10-18.

## Behavior

- Quit application remains visible in the header.
- General settings use the gear at the bottom of the project rail. Appearance settings replace the rejected user-facing name.
- Supervisor navigation has a stable label. Supervisor settings is an open section in the Workspace dashboard, including the name and exact Reviewer controls.
- Goal, progress, current work, next action and notes have distinct visual treatments. Conversation and activity use separate surfaces.
- Project skills require registration in the selected project's manifest, nonempty local guidance and a documentation reference. Each skill has an independent Active flag; Matt also has a project switch. Global cache contents cannot activate guidance. Eligibility is rechecked at model and worker boundaries. Isolated researchers use their source project's eligible guidance.
- The launch browser choice is saved next to the notebook and applies after Quit and relaunch. Choices include system default, detected browsers and custom executable paths. Firefox receives its own profile/window arguments; Chromium browsers retain app mode. CLI/environment overrides take precedence over the saved preference.

## Checks and evidence

The following targeted suite passed all 36 tests:

```powershell
node --test engine/project-skills.test.cjs engine/project-skills-policy.test.cjs engine/browser-settings.test.cjs engine/runtime-integration.test.cjs engine/grill-chat.test.cjs engine/skills.test.cjs engine/supervisor.test.cjs engine/phase1.test.cjs engine/lifecycle.test.cjs engine/window-execution.test.cjs engine/research-dispatch.test.cjs engine/worker-ui.test.cjs engine/model-roles.test.cjs engine/app-updates.test.cjs
```

Real Edge browser acceptance used isolated notebooks, profiles and attempts. Worker transport and Reviewer responses were fixtures, with no real provider calls:

- `validation/harbor-v2-1792334240195/results.json`: complete browser lifecycle PASS, including checkpoint/stop/resume, notifications, name persistence, desktop/mobile and reopening. One fixture Reviewer invocation. Personal notebook unchanged.
- `validation/harbor-v2-1792334416224/results.json`: focused settings PASS after final visual adjustments. Browser preference saved through the UI; project prerequisite flags and toggles checked; a second project inherited no skills; desktop/mobile geometry checked. No JavaScript or unexpected network errors. No AI calls. Personal notebook unchanged.

Run the focused browser check with:

```powershell
$env:PLANWHARF_TEST_NO_SANDBOX='1'
$env:PLANWHARF_SETTINGS_ONLY='1'
node verify-harbor-browser.cjs
```

The no-sandbox setting is limited to the isolated browser test. Earlier failed runs remain preserved in separate validation directories. They include a DevTools connection timeout and obsolete test navigation assumptions exposed by moving the settings.

Firefox, Brave and Opera command construction and preference persistence were tested with controlled subprocess transport; those installed browsers were not each launched end to end. macOS and Linux launches were not verified. The existing executable delegates to the changed Node launcher, so no executable rebuild was required.

The required read-only skill update check returned GitHub HTTP 403 rate-limit errors. Upstream updates and local differences against pinned upstream copies could not be determined. No skill updates were installed.
