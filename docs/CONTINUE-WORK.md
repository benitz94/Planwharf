# Continue work: import a project export and resume

Planwharf restores a `planwharf-project-export` (schema 1) file as a **new** notebook project. You can then resume paused work with **Resume** in the chat.

This is the Planwharf-native version of a pattern other local coordinators keep: keep the plan, notes, task status and checkpoints when you move machines or notebooks, and continue only after you confirm the folder on this PC.

## What is restored

- Project name (a colliding id becomes `name-2`)
- Living Plan revisions
- Tasks, with any `running` status saved as `paused`
- Pinned and AI-relevant notes
- Execution MegaTasks, SubTasks, structured results, checkpoints and timeline events for that project
- A continue hint: `resume` when a paused MegaTask has a checkpoint, `approve` when work is waiting for approval

The folder stored in the export is a hint, not a silent copy: the imported project starts without a folder. Set it in **Settings → This project**.

## What is not restored

- Source files, Git history, or the original attempts directory
- Provider credentials, tokens, auth profiles, or raw worker logs
- Live worker processes, PIDs, or an authorized backend session
- Global Settings, model catalogs, or other projects in the notebook
- Automatic worker launch

Resume still needs the linked folder for implementation work, and it still compares the folder with the checkpoint. It does not restore a model's internal conversation memory.

## How to continue

1. On the source computer, open **Settings → Move a project** and press **Save … to a file**.
2. Copy the JSON file to the destination PC, together with the project source folder if you still need the files.
3. On the destination computer, open **Settings → Move a project → Open a project file** and choose the export.
4. Planwharf selects the new project. Set its folder in **Settings → This project**.
5. If a checkpoint was restored, press **Resume** when you want workers to continue; Planwharf asks for confirmation if the folder differs from the checkpoint.

## HTTP

`POST /api/project-import` accepts JSON `{ version, bundle, folder?, github? }` (4 MiB). Responses:

- `201` `{ plan, version, projectId, continue }`
- `400` invalid export, invalid notebook, or missing folder
- `409` stale notebook `version`
- `415` non-JSON body

Exports set `restoreSupported: true`. Older documentation that described export-only recovery remains historical evidence from the v0.1.0 checkpoint.

## Tests

`node --test engine/project-export.test.cjs engine/final.test.cjs` covers sanitize, new-project restore, folder relink, collision ids, unknown payloads, HTTP 201/409, and no worker launch.
