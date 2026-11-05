# Claude Code CLI verification — 2026-10-23

The native `claude` adapter was verified on Windows with Claude Code 2.1.309. The installed CLI reported an authenticated `claude.ai` Pro login. Credentials and account email were not copied into Planwharf configuration or this report.

## Live checks

`node validation/check-claude-cli.cjs --live claude-sonnet-5-5` exited 0.

- Authentication and initialization returned 12 unique exact model IDs without a user turn. Catalog presence does not establish inference access to every model.
- The coordinator used `Read` on a temporary fixture and returned its exact contents with the requested model identity.
- The worker created `answer.txt`, read it back, and saved a matching structured COMPLETE result and successful provider exit evidence. The original fixture remained unchanged.
- Activity translation was checked against that actual worker log: PowerShell/Bash command events and the Write used for the result report appeared as observed tools. The bounded technical tail retained Write metadata; earlier records were explicitly reported as potentially absent. These activity observations do not themselves validate completion; task identities, structured result, process exit and independently checked file contents supplied the completion checks.

Raw attempt evidence was preserved locally under `C:\Users\benit\AppData\Local\Temp\pw-claude-live-WAx2eQ`. No personal notebook was loaded, dispatched or modified. The test did not install dependencies, commit, push or publish. This report does not claim live verification of other models, reviewer behavior, macOS/Linux, or desktop browser rendering.

## Offline regression checks

The following command exited 0: **60 tests passed, zero failed or skipped**.

```text
node --test engine/claude.test.cjs engine/provider-transport.test.cjs engine/providers.test.cjs engine/provider-models.test.cjs engine/provider-accounts.test.cjs engine/model-policy.test.cjs engine/model-roles.test.cjs engine/model-routing.test.cjs engine/external-adapters.test.cjs engine/provider-connections.test.cjs engine/skill-catalog.test.cjs engine/skill-transport.test.cjs engine/skill-execution-delivery.test.cjs engine/project-containment.test.cjs engine/activity.test.cjs engine/activity-events.test.cjs engine/activity-evidence.test.cjs engine/activity-sessions.test.cjs
```

Coverage includes native process prompt transport beyond Windows argv limits, Unicode, model discovery through CLI control messages, exact identity mismatch, missing final results, provider errors with process exit zero, early pipe closure, permission mode selection, unread quota, HTTP model visibility and explicit roles, Skill transport, activity redaction, source containment and existing API/provider behavior. Two existing worker fixtures were updated to provide the linked project directory now required by the runner. Syntax checks for changed JavaScript and new scripts, JSON parsing and `git diff --check` also passed.

## Known existing test failure

`node --test engine/orchestrator.test.cjs` fails at line 201 because its fixture expects the nonexistent `D:\demo` directory to be forwarded as a usable linked folder. The identical failure was reproduced using an isolated copy of committed HEAD source, before the Claude changes. Its original baseline output is preserved at `C:\Users\benit\AppData\Local\Temp\pw-claude-baseline-sYRJxy\orchestrator-baseline.log`. The test remains unchanged; no full-suite passing claim is made.

## Activation and limits

Use **Quit application**, relaunch Planwharf, select **Claude Code** under **Usage and permissions**, refresh models and enable the desired model roles. Newly discovered models start disabled. Existing personal settings were not edited during validation.

Per-account role isolation, source-protected research and the no-tool Supervisor transport remain unsupported. See [setup, permissions and limitations](EXTERNAL-PROVIDERS.md#claude-code-subscription-cli) for the integration contract and official protocol references.

## Follow-up: subscription quota reader

The initial integration returned an explicit unread quota placeholder. This was reproduced by asserting `adapterFor('claude').quota().readable === true`: the assertion failed before the quota reader was connected. The same assertion passed after the change using the installed subscription login, without inference or credential writes.

At `2026-10-23T20:19:08.918Z`, the real adapter returned **78% remaining** in the 5-hour window and **83% remaining** in the weekly window. These values describe that observation, not current quota. Credential material and raw account responses are excluded from this report.

`node --test engine/claude-usage.test.cjs engine/claude.test.cjs` passed all 14 tests. The quota tests cover numeric validation, shared versus model-specific windows, the provider and percentage display path, CLI credential selection, expired credentials, errors without credential disclosure, bounded response bodies, concurrent requests, rate limits, reset-aware caching and login changes. The isolated notebook HTTP test also verifies that readable Claude quota appears as AVAILABLE instead of UNREAD. No personal notebook is used.

Quota reads use the observed Anthropic OAuth usage endpoint and are cached for at most five minutes. Unsupported credential stores, changed service contracts and failed authentication remain explicitly unread. This change requires restarting the backend before an already-running application can use it.

The earlier combined regression command, with `engine/claude-usage.test.cjs` added, subsequently passed **68 tests**, zero failed or skipped. The separately documented existing orchestrator fixture failure remains outside that passing selection.
