# Task: logbook.js — a crash-safe, multi-process append-only event log

Implement a command-line program `logbook.js` (Node.js >= 18, CommonJS, no third-party dependencies) that stores named streams of immutable events, lets consumers read them through persistent cursors, supports compaction of old events and bulk import, and stays consistent when killed at any moment or when many processes use the same data directory at the same time.

## Deliverables

Place these files in the workspace root:

- `logbook.js` — the program.
- `README.md` — usage documentation (requirements, an example for every command, how to run the tests).
- `logbook.test.js` — an automated test suite runnable with `node --test logbook.test.js` from the workspace root, exiting with code 0. It must not depend on files outside the workspace and must clean up any temporary directories it creates.

## Invocation and output

```
node logbook.js <datadir> <command> [args...] [flags...]
```

Every invocation writes exactly one line of JSON to stdout, followed by `\n`, and nothing else on stdout.

- Success: `{"ok":true, ...command fields}` — exit code 0.
- Error: `{"ok":false,"error":"<human readable message>","code":"<ERROR_CODE>"}` — exit code 1.
- Usage error: the same error shape with `"code":"USAGE"` — exit code 2.

Field order in JSON output does not matter; field names, types and values do. Do not add fields that are not specified.

### Arguments and flags

The first argument is always `<datadir>` and the second is always the command, taken as they are. All later arguments are read left to right:

- An argument starting with `--` is a flag. Each command accepts only the flags listed for it; any other flag, including a flag that another command accepts, is a usage error. A flag given twice is a usage error.
- `--force` takes no value. `--id`, `--from` and `--count` take the next argument as their value, whatever it looks like (`--id --force` gives `--id` the value `--force`, which is then validated normally). A value flag that is the last argument is a usage error.
- Every other argument is positional. Each command takes an exact number of positional arguments.

Usage errors are: missing `<datadir>`, missing command, unknown command, wrong number of positional arguments, a flag the command does not accept, a repeated flag, and a value flag without its value. They are detected before anything else, including before checking whether the store exists.

### Store checks

After argument parsing, every command except `init` checks the store in this order:

1. If `<datadir>` or `<datadir>/db.json` does not exist: `NOT_INITIALIZED` ("Store not initialized"). This check does not take the lock.
2. Take the lock (see Concurrency).
3. Read `db.json`. If it is not valid JSON, or it is not an object with `"version": 1` and object-valued `streams`, `cursors` and `eventIds`: `CORRUPT_STORE`. The file is left untouched.

Any other unexpected file-system failure gives `IO_ERROR` (exit 1).

### Numbers

Sequence arguments (`--from`, the seq of `get`, the before-seq of `compact`) and `--count` must match `/^[1-9][0-9]*$/` and be at most `Number.MAX_SAFE_INTEGER`. Anything else (`0`, `01`, `+1`, `1.0`, `-1`, ` 1`, `abc`) is `INVALID_SEQ` or `INVALID_COUNT` respectively.

### Names that do not exist

Only `stream-create`, the cursor name of `cursor-create`, and `import` validate stream and cursor names against the naming rule. Every other command looks the name up as given: a malformed name simply does not exist and gives `STREAM_NOT_FOUND` or `NOT_FOUND` as listed for that command.

### Error order

Each command lists its errors in the order they are checked. The first failing check is reported.

## Data directory

| File | Purpose |
| --- | --- |
| `<datadir>/db.json` | the entire state |
| `<datadir>/db.json.tmp` | temporary file used while writing; may be left over after a crash and must be ignored/overwritten |
| `<datadir>/lock` | exclusive lock file, contains the owner's PID followed by `\n` |

The program may create other files in `<datadir>` whose names start with `lock.` for its own locking needs.

### db.json format (mandatory)

`db.json` is part of the interface (other tools read and write it), so it must have exactly this structure:

```json
{
  "version": 1,
  "streams": {
    "<name>": {
      "nextSeq": 1,
      "compactBefore": 1,
      "events": [ {"seq": 1, "type": "<type>", "id": "<id>", "payload": {}} ]
    }
  },
  "cursors":  { "<name>": {"stream": "<stream-name>", "pos": 0} },
  "eventIds": { "<id>":   {"stream": "<stream-name>", "seq": 1} }
}
```

Invariants:

- `nextSeq` is the sequence number the next appended event will receive (the first event of a stream gets seq 1). Sequence numbers are never reused, even after compaction or idempotent appends.
- `compactBefore = 1` means nothing has been compacted; `events` contains exactly the events with `compactBefore <= seq < nextSeq`, in seq order.
- `eventIds` maps every event id currently present in some stream (not compacted, not deleted) to its location. Ids are globally unique across all streams.
- A cursor's `pos` is the seq of the last event it has consumed (0 = nothing consumed). After compaction, `pos >= compactBefore - 1` for every cursor of that stream.
- The empty store is `{"version":1,"streams":{},"cursors":{},"eventIds":{}}`.

Any name or id allowed by the rules below is a valid key, including names such as `constructor` and ids such as `__proto__`.

If `db.json` is edited externally while no process holds the lock, the program must honour the edited content on its next run.

## Validation rules

| Item | Rule | Error code |
| --- | --- | --- |
| Stream name, cursor name | `/^[a-z0-9][a-z0-9_-]{0,62}$/` (1–63 chars) | `INVALID_NAME` |
| Event type | `/^[a-z0-9][a-z0-9-]{0,63}$/` (1–64 chars; no underscore) | `INVALID_TYPE` |
| Event id | `/^[a-zA-Z0-9._-]{1,128}$/` | `INVALID_ID` |
| Payload | must parse as JSON and be an object (not array, null, string, number, boolean) | `INVALID_PAYLOAD` |
| Seq arguments | see Numbers | `INVALID_SEQ` |
| `--count` | see Numbers | `INVALID_COUNT` |

Limits (`LIMIT_EXCEEDED`): at most 100 streams; at most 10000 events currently stored per stream (`nextSeq - compactBefore`); at most 1000 cursors.

Payload equality (used for idempotency) is deep JSON equality: object key order is irrelevant, array order matters, and numbers compare by value (`1` equals `1.0`).

## Commands

### 1. init

`node logbook.js <datadir> init` — creates `<datadir>` (recursively) if missing, takes the lock, and writes the empty store.

Success: `{"ok":true,"version":1}`.

Errors: `ALREADY_INITIALIZED` if `db.json` already exists, whatever its content (existing data must remain untouched).

### 2. stream-create `<name>`

Creates an empty stream (`nextSeq` 1, `compactBefore` 1).

Success: `{"ok":true,"stream":"<name>"}`.

Errors, in order: `INVALID_NAME`, `ALREADY_EXISTS`, `LIMIT_EXCEEDED` (100 streams already exist).

### 3. stream-delete `<name>` [`--force`]

Without `--force`: fails with `HAS_EVENTS` if the stream currently stores at least one event, else `HAS_CURSORS` if any cursor points to it. With `--force` (or when neither condition applies): deletes the stream, its events, every cursor pointing to it, and frees all its event ids (they can be reused afterwards).

Success: `{"ok":true,"stream":"<name>","eventsDeleted":<events stored in the stream>,"cursorsDeleted":<cursors removed>}`.

Errors, in order: `NOT_FOUND`, `HAS_EVENTS`, `HAS_CURSORS`.

### 4. stream-list

Success: `{"ok":true,"streams":[{"name":"...","nextSeq":n,"count":n,"compactBefore":n},...],"count":<number of streams>}` — sorted by name in plain code-unit order; per-stream `count = nextSeq - compactBefore`.

### 5. append `<stream>` `<type>` `<payload-json>` [`--id <id>`]

`<payload-json>` is one argument containing a JSON object.

Errors, in order: `STREAM_NOT_FOUND`, `INVALID_TYPE`, `INVALID_PAYLOAD`, `INVALID_ID`, id conflict, `LIMIT_EXCEEDED`.

With `--id` whose id already exists:

- same stream, same type and deep-equal payload → nothing changes (even when the stream is at its event limit); success `{"ok":true,"stream":"<stream>","seq":<original seq>,"id":"<id>","idempotent":true}`;
- otherwise (other stream, other type or other payload) → `DUPLICATE_ID`.

Without `--id` the id is `"<stream>-<seq>"` (e.g. `orders-1`); if that id is already taken → `DUPLICATE_ID`.

New event success: `{"ok":true,"stream":"<stream>","seq":<n>,"id":"<id>"}`.

### 6. read `<stream>` [`--from <seq>`] [`--count <n>`]

Returns stored events with `seq >= from` in seq order, at most `count`. `--from` defaults to `compactBefore`. `--count` defaults to 1000, and a larger value is silently capped at 1000. `--from >= nextSeq` gives an empty list (success).

Success: `{"ok":true,"stream":"<stream>","events":[{"seq":n,"type":"...","id":"...","payload":{...}},...],"count":<returned>,"hasMore":<bool>}` — `hasMore` is true iff more events with `seq >= from` exist beyond those returned.

Errors, in order: `STREAM_NOT_FOUND`, `INVALID_SEQ`, `INVALID_COUNT`, `COMPACTED` (`from < compactBefore`).

### 7. get `<stream>` `<seq>`

Success: `{"ok":true,"event":{"seq":n,"type":"...","id":"...","payload":{...}}}`.

Errors, in order: `STREAM_NOT_FOUND`, `INVALID_SEQ`, `COMPACTED` (`seq < compactBefore`), `NOT_FOUND` (`seq >= nextSeq`).

### 8. head `<stream>`

Success: `{"ok":true,"stream":"<name>","nextSeq":n,"count":n,"compactBefore":n}` (exactly these fields), `count = nextSeq - compactBefore`.

Errors: `STREAM_NOT_FOUND`.

### 9. compact `<stream>` `<before-seq>`

Removes all events with `seq < before-seq`.

Errors, in order: `STREAM_NOT_FOUND`, `INVALID_SEQ`, `ALREADY_COMPACTED` (`before-seq < compactBefore`), `FUTURE_SEQ` (`before-seq > nextSeq`).

`before-seq == compactBefore` is a successful no-op (`removed` 0); `before-seq == nextSeq` is valid and empties the stream (the next append still gets `nextSeq`).

Effects: removed events' ids are freed; every cursor on the stream gets `pos = max(pos, before-seq - 1)`; `compactBefore = before-seq`.

Success: `{"ok":true,"stream":"<name>","compactBefore":<new value>,"removed":<n>}`.

### 10. cursor-create `<cursor>` `<stream>`

Creates a cursor at pos 0 (even if the stream already has events or was compacted).

Success: `{"ok":true,"cursor":"<name>","stream":"<stream>","pos":0}`.

Errors, in order: `INVALID_NAME`, `ALREADY_EXISTS`, `STREAM_NOT_FOUND`, `LIMIT_EXCEEDED` (1000 cursors).

### 11. cursor-delete `<cursor>`

Success: `{"ok":true,"cursor":"<name>"}`.

Errors: `NOT_FOUND`.

### 12. cursor-list

Success: `{"ok":true,"cursors":[{"name":"...","stream":"...","pos":n},...],"count":n}` sorted by name in plain code-unit order.

### 13. poll `<cursor>` [`--count <n>`]

`--count` defaults to 10, and a larger value than 1000 is silently capped at 1000.

Errors, in order: `NOT_FOUND` (cursor), `INVALID_COUNT`.

If `pos < compactBefore - 1`, pos is first raised to `compactBefore - 1`. Returns the stored events with `pos < seq <= pos + count`; `newPos` = seq of the last returned event, or the (possibly raised) pos if none; `hasMore` = true iff the stream stores events with `seq > newPos`. The cursor's new position is persisted in the same atomic write: two concurrent polls on the same cursor must never return the same event, and no event may be skipped.

Success: `{"ok":true,"cursor":"<name>","stream":"<stream>","events":[...],"count":n,"newPos":n,"hasMore":bool}`.

### 14. import `<file>`

`<file>` (path relative to the current working directory) is UTF-8 text with one JSON object per line. Lines are separated by `\n`; a trailing `\r` is removed; lines that are empty or contain only whitespace are ignored. Each line is `{"stream":"...","type":"...","id":"...","payload":{...}}`. All four fields are required; extra fields such as `seq` are ignored.

Validation happens before any change, line by line in file order, and each line is checked as if every earlier valid line had already been applied. The first problem aborts the whole import with, checked in this order for each line:

1. `INVALID_FORMAT`: the line is not valid JSON, not an object, or lacks one of the four fields;
2. `INVALID_NAME`: `stream` is not a string matching the naming rule;
3. `INVALID_TYPE`: `type` is not a string matching the type rule;
4. `INVALID_PAYLOAD`: `payload` is not an object;
5. `INVALID_ID`: `id` is not a string matching the id rule;
6. `DUPLICATE_ID`: the id appeared on an earlier line of the file (even with identical data), or it is already stored with a different stream, type or payload;
7. `LIMIT_EXCEEDED`: applying this line would exceed the stream limit or the stream's event limit.

A missing or unreadable file gives `INVALID_FILE` before any line is read. The error message names the line number.

A line whose id is already stored with the same stream, type and payload is skipped (idempotent) and counts toward no limit. All other lines are appended in file order (missing streams are created automatically), and the whole import is committed in one atomic write: other processes and crashes observe either none or all of it.

Success: `{"ok":true,"imported":<n>,"skipped":<n>,"streamsCreated":[<names of streams created, sorted>]}`.

### 15. status

Success: `{"ok":true,"streams":n,"cursors":n,"totalEvents":<Σ nextSeq - compactBefore>,"compactedEvents":<Σ compactBefore - 1>}`.

## Crash safety (mandatory)

The process may be killed at any instant (SIGKILL on POSIX, TerminateProcess on Windows). Afterwards the store must be fully consistent and reflect either the complete state before or the complete state after the interrupted command — never a partial import, a half-written file, or an unparseable `db.json`.

`db.json` must never be modified in place: write the complete new state to `db.json.tmp` in the same directory and atomically rename it over `db.json`. On Windows the rename can fail briefly with `EPERM`, `EBUSY` or `EACCES` while another program has the file open; retry for up to one second before giving `IO_ERROR`.

Read-only commands (`stream-list`, `read`, `get`, `head`, `cursor-list`, `status`) must not rewrite `db.json`.

## Concurrency (mandatory)

Any number of `logbook.js` processes may run simultaneously against the same `<datadir>`. Every command (reads included) must run its whole read–modify–write cycle while holding an exclusive lock, so that no update is lost, no seq is assigned twice, no cursor event is delivered twice, and readers never observe partial state.

Lock semantics (implementation details are up to you, observable behaviour is not):

- The lock is the file `<datadir>/lock`, created exclusively (e.g. `fs.openSync(path, 'wx')`), containing the owner PID and `\n`. It is removed when the command finishes, on success and on error.
- A lock whose PID belongs to a process that is no longer alive is stale: replace it and proceed.
- A lock whose content is not exactly digits followed by `\n` (for example empty, because its owner was killed between creating and writing it) is treated as held while its modification time is less than 2 seconds old, and as stale afterwards.
- Taking over a stale lock must never let two live processes both believe they own the lock. Deleting the stale file and then creating a new one is not safe on its own: between the two steps another process may have taken over too.
- On Windows, creating, reading or deleting the lock file can fail briefly with `EPERM`, `EACCES` or `EBUSY` while another process is deleting it. Treat that as "held" and retry; it is not an error.
- If the owner is alive, retry with short sleeps; after about 5 seconds give up with `{"ok":false,"error":"Could not acquire lock","code":"LOCK_TIMEOUT"}` (exit 1).
- `init` also uses the lock (after creating the directory); a stale lock left in a fresh directory must not prevent initialization.
