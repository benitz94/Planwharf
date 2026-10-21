# CLI repair and rerun: initialization restored, native tools still blocked

Historical repair attempt retained below. See [the completed post-setup pilot](POST_SETUP.md) for the later native-access repair and six scheduled task outcomes.

The follow-up on 2026-10-20 restored CLI initialization and authenticated inference. **The requested paired performance rerun could not proceed:** native Windows sandbox editing and command execution remain unavailable in this session. PR #23 retains the original six blocked trials and adds the new evidence rather than replacing historical outcomes or inventing task results.

## What was fixed

The desktop profile was readable but rejected temporary writes in its root, tmp, sessions and SQLite directories. The supported `CODEX_SQLITE_HOME` override removed state-database warnings but did not fix app-server initialization. A separate, writable runtime profile supplied through the supported `CODEX_HOME` setting allowed the same installed `0.163.0-alpha.10.2` CLI and exact `gpt-5.6-luna` model to initialize and complete authenticated requests with usage receipts. No global CLI installation or desktop configuration was changed.

The private profile temporarily used the existing ChatGPT login file outside the repository. Credentials were never printed or exported; its temporary `auth.json` was removed after testing. Persistent desktop authentication was not modified. Only private calibration state remains outside the repository. An empty Windows configuration caused the private-profile sessions to resolve to read-only; preserving the desktop's existing `[windows] sandbox = "elevated"` selection restored the declared workspace-write filesystem profile and restricted tool networking.

These are supported location/configuration settings, described in the official [environment variable reference](https://learn.chatgpt.com/docs/config-file/environment-variables), [configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference), and [Windows sandbox guide](https://learn.chatgpt.com/docs/windows/windows-sandbox). A separate profile must retain the operator's approved native Windows sandbox selection; an empty profile is not an equivalent execution environment.

## Remaining failure

With the correct declared permissions, native `apply_patch` still failed to write the disposable file. Native command execution reported:

```text
Failed to create unified exec process: open sandbox provisioning pipe
```

The model accurately reported the failures. Its successful process exit and `turn.completed` receipt did **not** mean it had working engineering tools. An explicit workspace root and the documented `--add-dir` option did not resolve the earlier read-only state. The official portable `0.144.3` package was also downloaded, integrity-verified and tested under the same intended permissions; it did not solve native editing/execution. This was a separate diagnostic, not a silent change to either benchmark arm. Its archive metadata and receipt are retained; no executable is committed.

The remaining repair requires a working Windows sandbox provisioning service accessible to the CLI process. This restricted session could not establish that access. No sandbox bypass, weakened network isolation, credential/sandbox-secret migration, broad ACL changes, administrator prompt or visible window was used to force execution. A benchmark result from nonfunctional tools would be invalid.

## Attempted rerun and new gate

The updated live harness now runs a bounded capability check before submitting tasks. It requires an observed completed native file-change event, an observed successful Node command and a matching independently read verification file. A successful AI narration alone cannot pass. If capabilities fail, it saves the diagnostic and measured usage, writes an empty task dataset, and stops with a nonzero status. The normal offline command remains provider-free.

The recorded rerun requested one pair per task, the original model, eight minutes per task and at most six task launches per run. Its capability check was bounded to 90 seconds. The CLI completed the model turn but native file editing failed; the check stopped after that failure as instructed. **Zero task trials were submitted**, not six failed or defective implementations. Fault injection and recovery were not reached. The [rerun manifest](evidence/rerun-2026-10-20/manifest.json), [capability result](evidence/rerun-2026-10-20/preflight.json), [empty task dataset](evidence/rerun-2026-10-20/runs.json) and [native receipts](evidence/rerun-2026-10-20/receipts) preserve the attempt.

Eight completed diagnostic/calibration receipts expose a measured subtotal of **281,606 input tokens, including 259,072 cached input tokens, and 3,669 output tokens, including 1,127 reasoning tokens**. One additional initialization probe has no usage receipt and remains UNKNOWN. These counts include the rerun capability check and are **separate from both arms**, not task-performance measurements. The measured subtotal is not a complete study consumption total because that missing receipt is not zero. Billed cost, attributable quota decrement and financial savings remain UNKNOWN. See [all calibration receipts and usage](evidence/rerun-2026-10-20/calibration/summary.json).

## Interpretation and completion requirement

All seven executive comparison questions remain inconclusive or UNKNOWN: accepted completion time, token efficiency, human effort, engineering reliability, output quality, conditions where Planwharf is worse, and cash savings. Neither a full 30-run study nor new accepted pairs exist. The original grader and task specifications were not changed; there were no production engine optimizations.

An operator or administrator must restore native Windows sandbox provisioning for the intended execution environment, then rerun the documented `--live` command with the same CLI, model and permissions in both arms. The capability check must pass before a paired pilot can estimate usage/duration and justify expansion. This is the remaining external prerequisite; it has not been represented as completed work.
