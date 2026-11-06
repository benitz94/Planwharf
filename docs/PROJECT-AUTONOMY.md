# Project autonomy

Project autonomy lets the orchestrator carry one project forward without waiting for you. You can still step in at any time.

It is off by default. To use it, select **Automatic** mode and choose **Turn on project autonomy** for the project. The setting lasts for the current Planwharf session: after a restart, turn it on again.

## What happens

1. Planwharf starts the project without a clarification interview. The lead decides open questions itself and reports its assumptions.
2. A build job is split into two slices: one model implements the whole job with tests, then a model from another provider verifies it, concentrating on what the tests do not cover, and fixes what fails. A model with the **Worker** role builds; a model with the **Verifier** role checks, preferably from another provider. With no Verifier available the job is one slice and Planwharf only runs the tests. When a slice has more than one allowed model, the lead picks one before approval; with one allowed model per slice your roles decide the crew and no lead call is made. If the lead reply is unusable, the proposed crew stays.
3. Each proposed MegaTask is approved by your setting. The timeline records "by the project autonomy setting".
4. When a MegaTask finishes, Planwharf runs the project's tests itself (`npm test`, or `node --test` when test files exist) If every slice finished without blockers and the tests pass, the goal is reached without a lead call. Otherwise Planwharf gives the lead the fresh result. The lead reviews it with the recorded results, without running commands, and either declares the goal reached or names the next objective.
5. When the Supervisor detects a new alert, the backend writes a message from the detected facts and sends it to the lead.

Model text can only declare the goal reached or name an objective. It cannot approve work, change settings, or stop workers.

## Limits

- At most 5 approved MegaTasks per activation, and 3 Supervisor nudges per hour. Reaching the MegaTask limit stops autonomy and shows the reason.
- **Pause** and **Stop** are always respected. A stopped or checkpointed project is not restarted.
- An unreadable or failed lead reply is retried once with a lead from another provider. If that also fails, autonomy stops. Nothing is guessed from it.
- A worker or lead refused for credits, quota or a usage limit is skipped for an hour, and its slice moves to another eligible model. The timeline records the switch.
- Provider quota thresholds still apply.

## Pushes

While autonomy is on, workers run with a Git setting that makes every `git push` fail. This prevents accidental pushes. **It is not a sandbox**: a process can override its own Git configuration, and workers keep the file and network access their provider permissions give them. Merging a branch locally is not blocked; workers are told not to merge to a shared branch.

## Costs

Each crew choice, lead review and nudge is a model call, recorded under the Orchestrator role in **AI usage**. Detecting an alert and running the project's tests use no model.

## Known limit: a worker's claim is not proof

On 2026-11-03, `grok-4.7` with `--json-schema` made one model call and reported files and passing tests that did not exist. The lead's review found the empty folder and asked for the work again. Planwharf does not use that option with Grok. A recorded result is still the worker's own report: inspect the output before relying on it.
