# Grader validation

Run on 2026-11-07 with `node validate.cjs` (the file in this folder). Each mutant is the reference with one typical bug. The grader must fail at least one check on every mutant.

- Reference: **65/65**.
- An empty seed project: **1/65**.
- Mutants: **27/27 caught**.

An earlier run missed "stale takeover deletes then creates without a guard": that race only shows when two processes meet the same stale lock at the same moment. This run caught it, but because it is a race, a single run can still miss it.

| Mutant | Failing checks |
| --- | ---: |
| no lock at all | 7 |
| stale locks are never taken over | 6 |
| live locks are treated as stale | 4 |
| empty locks are taken over at once | 1 |
| stale takeover deletes then creates without a guard | 1 |
| db.json written in place | 2 |
| import commits line by line | 5 |
| reads rewrite db.json | 1 |
| usage checked after the store | 2 |
| any flag accepted by any command | 1 |
| repeated flags allowed | 1 |
| payload equality depends on key order | 1 |
| plain objects for keys (__proto__ trap) | 1 |
| leading zeros accepted | 3 |
| no 1000 cap on read | 1 |
| compaction leaves cursors behind | 1 |
| compaction keeps ids | 4 |
| poll does not raise a cursor behind compaction | 1 |
| stream-delete checks cursors before events | 1 |
| limit checked before the idempotent case | 1 |
| import limit counted on the total | 1 |
| import allows identical repeated ids | 1 |
| corrupt store treated as empty | 1 |
| lock timeout of 1 second | 3 |
| lock left after an error | 2 |
| two lines of output | 1 |
| default id uses seq of the last event | 13 |

Final line of the run: `grader is sound`.
