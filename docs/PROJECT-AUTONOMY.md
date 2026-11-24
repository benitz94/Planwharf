# Project autonomy

Project autonomy lets the orchestrator carry one project forward without waiting for you. You can still step in at any time.

It is off by default. To use it, press **Let the crew do it** at the bottom of the chat, or set the chat switch to **Auto**. Either way Planwharf switches approvals to automatic. **Stop the crew** turns it off at once and puts the switch back on **Plan**. Moving the switch from Auto to Talk or Plan leaves the current goal running and does not start a later one. The setting lasts for the current Planwharf session: after a restart the project comes back as **Plan**, and you turn autonomy on again.

## What happens

1. Planwharf starts the project without a clarification interview. The lead decides open questions itself and reports its assumptions.
2. Before the work starts, the lead sizes it and splits it into parts. It sizes the work by what the goal asks for, not by the length of the goal line: the sheets and plans the goal names (a path, or "sheet 42" for a numbered sheet) are in its question, and it may read any project file. A goal that lists several modules or screens becomes several parts (up to 30), each small enough for one worker to finish; up to 10 run at the same time when they touch different files, and the rest start as those finish. A reply that cannot be used is asked again once, saying what was wrong; a part given a model that may not build it gets one by its weight instead of losing the whole split. The parts are also spread: no model and no company takes more than its share while another model of an allowed weight has room, so a lead that picks its own company for every part does not drain that company's quota. Models of the same company count as different models. A part that waits for others (for example the last one that wires them together) may also change their files once they are finished; any other file outside its list stops it. Then one model verifies the whole, concentrating on what the tests do not cover, and fixes what fails. A model with the **Worker** role builds; any available model with the **Verifier** role checks. Another company is a preference, not a rule. With no Verifier available the job is one slice and Planwharf only runs the tests. When a slice has more than one allowed model, the lead picks one before approval; with one allowed model per slice your roles decide the crew and no lead call is made. If the lead reply is unusable, the proposed crew stays.
3. Each proposed MegaTask is approved by your setting. The timeline records "by the project autonomy setting".
4. When a MegaTask finishes, Planwharf runs the project's tests itself (`npm test`, or `node --test` when test files exist) If every slice finished without blockers and the tests pass, the goal is reached without a lead call. Otherwise Planwharf gives the lead the fresh result. The lead reviews it with the recorded results and may inspect the project folder (files, git history, diffs). It does not rerun the tests or edit files. It either declares the goal reached or names the next objective.
5. When a step stops part way (blocked, failed, or with only part of the work done), the lead hears about it at once, with what the step did and the files it changed. It names the rest of the work; Planwharf prepares it and the lead splits it again, so a step that was too big becomes several smaller ones. The crew carries on.
6. When the Supervisor detects a new alert, the backend writes a message from the detected facts and sends it to the lead.

Model text can only declare the goal reached or name an objective. It cannot approve work, change settings, or stop workers.

## Limits

- At most 20 approved MegaTasks per activation, and 3 Supervisor nudges per hour. Reaching the MegaTask limit stops autonomy and shows the reason.
- A blocked part does not abandon the parts that do not need it: the work stops only when nothing else can start. Work that continues a stopped one may change what the stopped work finished.
- Two stops in a row with no progress (no file changed, or the same step stopped for the same reason) are no progress. The crew stops (**Stopped: no progress twice**) and asks you in **Decisions** how to continue. Your answer starts it again from there.
- An error inside Planwharf does not end the crew at once: it is shown in **Progress** and the step is tried again. Three in a row stop it (**Stopped: error**, with the message).
- What Planwharf decides on its own (why the work was not split, which model a part moved to, why the crew stopped) is written in **Progress**.
- **Pause** and **Stop** are always respected. A stopped or checkpointed project is not restarted.
- An unreadable or failed lead reply is retried once with a lead from another provider. If that also fails, autonomy stops. Nothing is guessed from it.
- A worker or lead refused for quota or a usage limit is skipped for an hour; one that needs credits is skipped for a day. Its slice moves to another eligible model, and the timeline records the switch.
- Provider quota thresholds still apply.

## Pushes

While autonomy is on, workers run with a Git setting that makes every `git push` fail. This prevents accidental pushes. **It is not a sandbox**: a process can override its own Git configuration, and workers keep the file and network access their provider permissions give them. Merging a branch locally is not blocked; workers are told not to merge to a shared branch.

## Costs

Each crew choice, lead review and nudge is a model call, recorded under **Lead calls** in **Usage**. Detecting an alert and running the project's tests use no model.

## Known limit: a worker's claim is not proof

On 2026-11-03, `grok-4.7` with `--json-schema` made one model call and reported files and passing tests that did not exist. The lead's review found the empty folder and asked for the work again. Planwharf does not use that option with Grok. A recorded result is still the worker's own report: inspect the output before relying on it.
