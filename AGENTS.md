# Planwharf contributor instructions

Use English for current documentation, product messages, code comments, and commit messages. Discussions with the owner may use Italian.

Preserve personal notebook data, existing workspace changes, and historical raw execution evidence. Do not translate user-authored project content or rewrite historical logs as though translated text were original evidence.

Use isolated notebooks and attempts for validation. Repository inspection must not dispatch personal projects or automatically push, merge, reset, or overwrite their source files.

Do not report work as done, tested, or verified without having run the command and seen its result. Applies to every model and provider. State what was not run. A check that cannot fail is not evidence.

Keep provider/model IDs explicit. The lead has full tool access for inspection and coordination; workers implement jobs. Never silently substitute another provider or fabricate model access, quotas, or completion evidence.


## Installed coding-agent skills

This project has local Matt Pocock skills in `.agents/skills`. Consult the names
in `.agents/matt-pocock-install.json` and read the relevant skill's `SKILL.md`
before applying it. Preserve user-invoked skill boundaries; planning, TDD and
review workflows come from Matt's collection when enabled.

Selected ECC skills are listed with their intended use in `.agents/ecc-README.md`.
Read and apply relevant `.agents/skills/<name>/SKILL.md` guidance for the current
task, including supporting references when needed. These skills apply to coding
agents regardless of model/provider; do not assume the host discovers them
without reading this project's instructions.

Preserve existing project rules, assigned roles, dependency policies and user
authorization. Examples do not authorize new dependencies, commits, publishing,
services, device commands or other side effects. Adapt guidance to the existing
stack and supported runtime versions. Do not load another project's skill set.

When this project is selected in Planwharf, its installed ECC instructions are
supplied to leads and workers across providers, including isolated research
workers. The Matt checkbox controls Matt skills only; ECC availability follows
this project's local installation.


## Portable skills and session-start update check

After the required project documentation reads, at the start of each working
session, read `.agents/README.md` and inspect `.agents/SKILLS.md`. Run
`python .agents/check-updates.py` from the project root (use `python3` or
`py -3` if appropriate for the environment). Report available upstream changes
and local differences briefly. If Python, network access, or GitHub is
unavailable, report the check as unavailable; never claim the skills are current.
The check is read-only. Do not install updates or overwrite local adaptations
without user authorization. Do not re-run it for every message in one session.

Skills in `.agents/skills/` are available to every agent/provider working on
this checkout. Read the relevant `SKILL.md` before using a skill, including its
supporting references when needed. Availability does not override explicit
user invocation requirements, project rules, permissions, or host controls.
Keep `.agents/`, this file, and the agent instruction entry points in version
control, including source manifests and third-party license notices. An agent
that does not load repository instructions must be explicitly directed to
`AGENTS.md` and `.agents/SKILLS.md` in its initial task prompt.
