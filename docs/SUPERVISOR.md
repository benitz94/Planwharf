# Supervisor

The Supervisor button in the application header opens a dedicated third view.
Project Home and Orchestrator remain available from that view. This places
monitoring and questions outside the Orchestrator command thread. The freely editable name is under **Name and Reviewer settings**;
blank names show `Supervisor`, with an 80-character limit and text-only rendering.

## Live monitoring and alerts

The Supervisor consumes the existing Orchestrator backend snapshots. It introduces
no additional status polling.
Its summary, Worker assignments, Reviewer assignments and recent project events
refer to the selected project. Pending review work is distinguished from live
review processes; validated results and process liveness remain separate evidence.

Deterministic alerts cover blocked or failed execution, unsuccessful validated
results, three consecutive unsuccessful results, a stopped process without a
validated result, completion conflicting with live work, and ten minutes without
recorded progress while a process remains alive. The last condition is explicitly
a reason to inspect, not proof of failure. This is a bounded detector, not a
claim to recognize every semantic mistake or deviation from an approved plan.

A new alert produces a discreet notice and navigation badge. Inspect evidence
opens a dialog; Ask Supervisor fills a question for the user to submit; Ignore
acknowledges only that alert. It does not resume or approve work, alter results,
or delete evidence. Ignored alerts survive restart and can be shown again.
A resolved condition that recurs produces a new alert.

When active work and a valid current MegaTask permit it, alerts also offer Pause
after active work and Stop and checkpoint. These call the existing backend gates.
Stop requires the existing explicit confirmation. Model text is never parsed as
an executable action or granted approval authority.

The existing snapshot interval (normally four seconds) determines observation
latency. Network unavailability is shown explicitly; it is not inferred success.

## Questions and optional AI interpretation

By default, questions return clearly labeled live evidence without AI
interpretation. To obtain an AI answer, select an exact eligible Reviewer model.
The provider and model are shown in settings and recorded with the answer.
There is no automatic model substitution. Reviewer permission, provider
availability and quota policy are checked again when calling the model.

Only Codex and API adapters currently have verified advisory transports. Codex
uses a read-only sandbox in an isolated temporary working directory; API requests
advertise no tools and reject returned tool calls. Other adapters are unavailable
for Supervisor answers until an equivalent transport is validated.

Supervisor questions have their own conversation and process, so they do not
cancel an Orchestrator response. Prompts include bounded execution facts and
recent conversation. AI answers are advisory text; they cannot dispatch, approve,
change permissions, or edit project files. Calls are recorded under the existing
Reviewer role. No fourth provider/model role is introduced.

With **project autonomy** on, a newly detected alert also sends the lead one bounded nudge. The backend writes that message from the detected facts. See [PROJECT-AUTONOMY.md](PROJECT-AUTONOMY.md).

Optional automatic analysis is off by default. Once explicitly enabled with a
Reviewer, only a newly detected anomaly can trigger analysis, capped at three
calls per hour. This is event driven, not continuous LLM polling. Busy periods or
the hourly limit can leave an anomaly without an AI interpretation; deterministic
alerts remain available. Questions and settings persist in `supervisor.json` under
the backend attempts directory. Interrupted answers are marked as interrupted on
backend restart rather than shown as still running.

## Validation boundaries

`engine/supervisor.test.cjs` exercises live HTTP endpoints, persistent alerts,
project/role isolation, model permission revocation, read-only API child processes,
rejection of malicious tool calls, exact model identity and concurrent lead/advisory
requests. It uses local fixtures, not paid provider inference.

