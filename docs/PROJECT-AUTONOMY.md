# Project autonomy

Project autonomy lets the orchestrator carry one project forward without waiting for you. You can still step in at any time.

It is off by default. To use it, select **Automatic** mode and choose **Turn on project autonomy** for the project. The setting lasts for the current Planwharf session: after a restart, turn it on again.

## What happens

1. Planwharf starts the project without a clarification interview. The lead decides open questions itself and reports its assumptions.
2. Each proposed MegaTask is approved by your setting. The timeline records "by the project autonomy setting".
3. When a MegaTask finishes, the lead reviews the recorded results. It either declares the goal reached or names the next objective.
4. When the Supervisor detects a new alert, the backend writes a message from the detected facts and sends it to the lead.

Model text can only declare the goal reached or name an objective. It cannot approve work, change settings, or stop workers.

## Limits

- At most 5 approved MegaTasks per activation, and 3 Supervisor nudges per hour. Reaching the MegaTask limit stops autonomy and shows the reason.
- **Pause** and **Stop** are always respected. A stopped or checkpointed project is not restarted.
- An unreadable lead reply stops autonomy. Nothing is guessed from it.
- Provider quota thresholds still apply.

## Pushes

While autonomy is on, workers run with a Git setting that makes every `git push` fail. This prevents accidental pushes. **It is not a sandbox**: a process can override its own Git configuration, and workers keep the file and network access their provider permissions give them. Merging a branch locally is not blocked; workers are told not to merge to a shared branch.

## Costs

Each lead review and each nudge is a model call, recorded under the Orchestrator role in **AI usage**. Detecting an alert uses no model.

## Known limit: a worker's claim is not proof

On 2026-11-03, `grok-4.7` with `--json-schema` made one model call and reported files and passing tests that did not exist. The lead's review found the empty folder and asked for the work again. Planwharf does not use that option with Grok. A recorded result is still the worker's own report: inspect the output before relying on it.
