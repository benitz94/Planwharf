# Continue work: import a project export and resume

Planwharf restores a `planwharf-project-export` (schema 1) file as a **new** notebook project. You can then resume paused MegaTasks from Home using the existing checkpoint controls.

This is the Planwharf-native version of a pattern other local coordinators keep: keep the plan, notes, task status and checkpoints when you move machines or notebooks, and continue only after you confirm the folder on this PC.

## Import project vs Import revision

| Control | Where | What it restores |
| --- | --- | --- |
| **Import project** | Projects rail | A full export: Living Plan revisions, tasks, notes, checkpoints and summarized execution. Creates a new project. |
| **Import** on The plan | Plan card | One revision JSON object (`title`, goal, state, decisions, questions). Does not create a project or restore execution. |

## What is restored

- Project name (a colliding id becomes `name-2`)
- Living Plan revisions (`manual`, `grill`, or `import`)
- Tasks, with any `running` status saved as `paused`
- Pinned and AI-relevant notes
- Execution MegaTasks, SubTasks, structured results, checkpoints and timeline events for that project
- A continue hint: `resume` when a paused MegaTask has a checkpoint, `approve` when work is waiting for approval

Folder and GitHub URL come only from the values you confirm in the import dialog. The path stored in the export is a hint, not a silent copy.

## What is not restored

- Source files, Git history, or the original attempts directory
- Provider credentials, tokens, auth profiles, or raw worker logs
- Live worker processes, PIDs, or an authorized backend session
- Global Settings, model catalogs, or other projects in the notebook
- Automatic worker launch

Resume still needs the linked folder for implementation work, and it still uses Home checkpoint comparison. It does not restore a model's internal conversation memory.

## How to continue

1. On the source notebook, open Home and choose **Export project**.
2. Copy the JSON file to the destination PC, together with the project source folder if you still need the files.
3. In the destination Planwharf, choose **Import project**, select the export, and confirm **Folder on this PC** (and GitHub URL if you use one).
4. Planwharf selects the new project and opens Home. If a checkpoint was restored, the toast says **Resume from checkpoint on Home**.
5. Review **Since your last visit** / checkpoint comparison, then **Resume** when you want workers to continue.

## HTTP

`POST /api/project-import` accepts JSON `{ version, bundle, folder?, github? }` (4 MiB). Responses:

- `201` `{ plan, version, projectId, continue }`
- `400` invalid export, invalid notebook, or missing folder
- `409` stale notebook `version`
- `415` non-JSON body

Exports set `restoreSupported: true`. Older documentation that described export-only recovery remains historical evidence from the v0.1.0 checkpoint.

## Tests

`node --test engine/project-export.test.cjs engine/final.test.cjs` covers sanitize, new-project restore, folder relink, collision ids, unknown payloads, HTTP 201/409, and no worker launch.
