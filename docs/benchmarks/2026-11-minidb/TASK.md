# Task: minidb.js, a small SQL database

Write `minidb.js`, a SQL database engine in a single file. It must run on Node.js 18 or later, use CommonJS and no third-party packages, and use only Node's built-in modules (no `node:sqlite`).

Deliver in this folder:

- `minidb.js`, the program;
- `minidb.test.js`, your tests, run with `node --test minidb.test.js`. They must pass and must not need network access;
- `README.md`, a short description of how you store data, keep it safe after a crash and handle several processes.
- `REPORT.json`, your work report (section 6).

A hidden grader will run many generated queries and compare your answers with a reference implementation. The answers must match **exactly**, so read the rules below carefully: where this text decides something, follow it, even if another database does it differently.

## 1. How the program runs

```
node minidb.js <datadir>
```

- The program reads SQL from standard input until end of input and runs the statements one by one, in order.
- For each statement it writes its result to standard output (section 3), then goes on to the next one, even after an error.
- After the last statement it exits with code 0.
- If `<datadir>` is missing, print `ERR\tUSAGE` and exit with code 2.
- `<datadir>` is created if it does not exist. All data lives inside it.
- The program writes nothing else to standard output. It may write anything to standard error.
- Run each statement as soon as its closing `;` has been read; do not wait for the end of input. The grader may send a statement, wait for its result, and only then send the next.
- Each result must be written to standard output (flushed) before the next statement starts. The grader reads the output while the program runs.

### Splitting statements

- Statements end with `;`. A `;` inside a string literal does not end a statement.
- Text after the last `;` is a statement too, if it contains anything other than spaces and comments.
- `-- comment` runs to the end of the line. Comments and white space (space, tab, CR, LF) separate tokens and are otherwise ignored.
- An empty statement (only spaces and comments between two `;`) produces no output.

## 2. The language

Keywords and identifiers are case-insensitive. Keywords are written in upper case here.

### Lexical rules

- **Identifier:** `[A-Za-z_][A-Za-z0-9_]*`, not a keyword. Identifiers are compared without regard to ASCII case. Quoted identifiers do not exist.
- **Keywords:** `ALL AND AS ASC BEGIN BETWEEN BY CASE COMMIT COUNT CREATE DELETE DESC DISTINCT DROP ELSE END EXISTS FROM GROUP HAVING IN INDEX INNER INSERT INTEGER INTO IS JOIN KEY LEFT LIMIT NOT NULL OFFSET ON OR ORDER PRIMARY ROLLBACK SELECT SET TABLE TEXT THEN UNIQUE UPDATE VALUES WHEN WHERE`.
- **Integer literal:** one or more digits. A minus sign is the unary operator, not part of the literal. A literal larger than 9007199254740991 is `OVERFLOW`.
- **String literal:** between single quotes. Two single quotes inside a string stand for one quote. There are no other escapes; any character, including a newline, may appear. A string that does not end is `SYNTAX`.
- **Symbols:** `( ) , . ; * + - / % || = <> != < <= > >=`.

Anything else is `SYNTAX`.

### Values and types

There are three kinds of value:

- **INTEGER:** a whole number from -9007199254740991 to 9007199254740991.
- **TEXT:** a string of Unicode characters.
- **NULL.**

There is no automatic conversion between INTEGER and TEXT, except in `||` (see below).

### Statements

```
CREATE TABLE name ( coldef [, coldef]... )
  coldef: column (INTEGER | TEXT) [PRIMARY KEY] [NOT NULL] [UNIQUE]   (constraints in any order, each at most once)
DROP TABLE name
CREATE [UNIQUE] INDEX name ON table ( column )
DROP INDEX name
INSERT INTO table [ ( column [, column]... ) ] VALUES ( expr [, expr]... ) [, ( ... )]...
UPDATE table SET column = expr [, column = expr]... [WHERE expr]
DELETE FROM table [WHERE expr]
SELECT [DISTINCT | ALL] item [, item]...
  [FROM source [join]...]
  [WHERE expr]
  [GROUP BY expr [, expr]...]
  [HAVING expr]
  [ORDER BY expr [ASC | DESC] [, ...]]
  [LIMIT expr [OFFSET expr]]
    item:   *  |  table.*  |  expr [[AS] alias]
    source: table [[AS] alias]
    join:   [INNER] JOIN source ON expr  |  LEFT JOIN source ON expr
BEGIN
COMMIT
ROLLBACK
```

Rules for tables and columns:

- **Table definitions:**
  - A table has at least one column.
  - Column names in one table must differ; otherwise `SYNTAX`.
  - At most one column is `PRIMARY KEY`; otherwise `SYNTAX`.
  - `PRIMARY KEY` means `NOT NULL` and `UNIQUE`.
- **Name clashes:** tables and indexes share one namespace. Creating a name that exists is `ALREADY_EXISTS`.
- **Indexes:**
  - An index changes only speed, never results.
  - `CREATE UNIQUE INDEX` on a column whose current values (ignoring NULL) are not unique is `CONSTRAINT_UNIQUE`, and the index is not created.
  - A unique index adds a `UNIQUE` constraint while it exists.
  - `DROP TABLE` also drops the table's indexes.
- **INSERT columns:**
  - Without a column list, values are given for every column in table order.
  - With a list, the listed columns get the values and the others get NULL.
  - A column listed twice, or a row with the wrong number of values, is `SYNTAX`.
  - An unknown column is `NO_SUCH_COLUMN`.
- **Expressions in INSERT and UPDATE:**
  - The expressions in `INSERT ... VALUES` cannot refer to columns: a column name there is `NO_SUCH_COLUMN`.
  - In `UPDATE`, every expression sees the row as it was before the statement.
  - Setting the same column twice in one `UPDATE` is `SYNTAX`.

### Expressions

From the loosest binding to the tightest:

| Level | Operators |
| --- | --- |
| 1 | `OR` |
| 2 | `AND` |
| 3 | `NOT` (prefix) |
| 4 | `=` `<>` `!=` `<` `<=` `>` `>=`, `IS [NOT] NULL`, `[NOT] BETWEEN a AND b`, `[NOT] IN (...)` |
| 5 | `||` |
| 6 | `+` `-` |
| 7 | `*` `/` `%` |
| 8 | unary `-` |

Binary operators of the same level group left to right. Level 4 operators do not chain: `a = b = c` is `SYNTAX`. Use parentheses.

Primary expressions:

- literals, `NULL`;
- `column` or `table.column`;
- `( expr )`;
- `( SELECT ... )` as a scalar subquery;
- `EXISTS ( SELECT ... )`;
- `CASE WHEN cond THEN expr [WHEN ...]... [ELSE expr] END`, and `CASE expr WHEN value THEN expr ... [ELSE expr] END`;
- function calls (below).

**Arithmetic.**

- If either side is NULL, the result is NULL.
- Both sides must be INTEGER; otherwise `TYPE_MISMATCH`.
- `/` divides and truncates toward zero. `%` is the remainder, with the sign of the left side.
- Division or remainder by zero gives NULL.
- A result outside the INTEGER range is `OVERFLOW`, and so is the negation of a value whose negation would be out of range.

**Concatenation `||`.**

- If either side is NULL, the result is NULL.
- Otherwise both sides are turned into text and joined. An INTEGER becomes its decimal form, with `-` for negative numbers and no leading zeros.

**Comparison.**

- If either side is NULL, the result is NULL.
- Two INTEGERs compare by value.
- Two TEXTs compare by Unicode code point, one character at a time; a string that is a prefix of another is smaller. JavaScript's `<` on strings compares UTF-16 units, not code points, and gives a different order for characters outside the Basic Multilingual Plane.
- **An INTEGER and a TEXT are never equal, and every INTEGER is smaller than every TEXT.**
- The result is 1 for true and 0 for false.

**Truth.** A condition is:
- true when it is a non-zero INTEGER;
- false when it is the INTEGER 0;
- unknown when it is NULL;
- `TYPE_MISMATCH` when it is TEXT.

`WHERE`, `HAVING`, `ON` and `CASE WHEN` keep or choose only when true.

- `NOT x`: NULL stays NULL; otherwise the result is 1 if x is false and 0 if x is true.
- `AND`, `OR`: three-valued logic.
  - `AND` is 0 if either side is false, NULL if either is unknown, and 1 otherwise.
  - `OR` is 1 if either side is true, NULL if either is unknown, and 0 otherwise.
  - Both sides are always evaluated, so an error on either side is raised.
- `x IS NULL` and `x IS NOT NULL` give 1 or 0, never NULL.
- `x BETWEEN a AND b` means `x >= a AND x <= b`. `NOT BETWEEN` is `NOT (x BETWEEN a AND b)`.
- `x IN (v1, v2, ...)`:
  - 1 if x equals some v;
  - otherwise NULL if x is NULL or some v is NULL;
  - otherwise 0.

  `x IN (SELECT ...)` works the same with the subquery's rows, and the subquery must have exactly one column (`SUBQUERY_COLUMNS`). An empty list or empty subquery gives 0, even if x is NULL. `NOT IN` is `NOT (x IN ...)`.
- **A scalar subquery** must have exactly one column (`SUBQUERY_COLUMNS`). With zero rows it gives NULL, with one row that value, and with more rows `SUBQUERY_ROWS`.
- **`EXISTS`** gives 1 if the subquery has any row, otherwise 0.
- **Correlation:** a subquery may refer to columns of the enclosing queries. A name is looked up in the innermost query first, then outward.
- **`CASE`:**
  - The `WHEN` clauses are tried in order. Without a matching `WHEN` and without `ELSE`, the result is NULL.
  - In `CASE x WHEN v`, a clause matches when `x = v` is true, so a NULL x matches nothing.
  - Only the chosen `THEN` or `ELSE` expression is evaluated.
  - Conditions are evaluated in order until one matches, and errors in evaluated parts are raised.

**Functions.** Names are case-insensitive. A wrong number of arguments or an unknown name is `SYNTAX`.

| Function | Result |
| --- | --- |
| `LENGTH(x)` | number of Unicode code points of TEXT x; NULL for NULL; `TYPE_MISMATCH` for INTEGER |
| `UPPER(x)`, `LOWER(x)` | TEXT x with only ASCII letters a-z/A-Z changed; NULL for NULL; `TYPE_MISMATCH` for INTEGER |
| `ABS(x)` | absolute value of INTEGER x; NULL for NULL; `TYPE_MISMATCH` for TEXT |
| `COALESCE(a, b, ...)` | at least 2 arguments; the first that is not NULL, else NULL; later arguments are not evaluated once one is chosen |
| `SUBSTR(s, start [, len])` | see below |

`SUBSTR`:
- If any argument is NULL, the result is NULL.
- `s` must be TEXT, and `start` and `len` INTEGER; otherwise `TYPE_MISMATCH`.
- Positions count code points from 1.
- The result is the code points at positions `p` with `start <= p` and, when `len` is given, `p < start + len`, for `1 <= p <= LENGTH(s)`. So a start below 1 or a negative length shortens the result, and may make it empty.

**Aggregates:** `COUNT(*)`, `COUNT([DISTINCT] x)`, `SUM([DISTINCT] x)`, `MIN(x)`, `MAX(x)`.

- They ignore NULL values.
- `COUNT` of nothing is 0. `SUM`, `MIN` and `MAX` of nothing are NULL.
- `SUM` needs INTEGER values (`TYPE_MISMATCH` on TEXT), and its result must stay in range (`OVERFLOW`).
- `MIN` and `MAX` use the comparison order above, so they work on mixed INTEGER and TEXT.
- `DISTINCT` drops repeated values before counting or summing.

Rules for where aggregates may appear:

- They are allowed only in the `SELECT` list, `HAVING` and `ORDER BY` of a query. Anywhere else, such as `WHERE`, `ON`, `GROUP BY`, `UPDATE` or `VALUES`, they are `MISUSE`.
- An aggregate inside another aggregate is `MISUSE`.
- A subquery inside an aggregate's argument is allowed, and the subquery has its own aggregates.

### How a SELECT is evaluated

1. **FROM:**
   - `FROM` with joins builds rows left to right. `JOIN` keeps the combinations where `ON` is true.
   - `LEFT JOIN` also keeps each left row that matched nothing, with NULL for every column of the right table.
   - Without `FROM`, there is one row with no columns.
2. **WHERE** keeps the rows where the condition is true.
3. **GROUP BY** puts rows with equal values of all its expressions in one group. For grouping, and also for `DISTINCT`, NULL equals NULL, and an INTEGER never equals a TEXT.
   - Without GROUP BY, a query that uses an aggregate anywhere in SELECT, HAVING or ORDER BY is one group of all rows. That group exists even when there are no rows.
   - A query without GROUP BY and without aggregates keeps its rows as they are. In that case HAVING is `MISUSE`.
4. **Column references in grouped queries:** in a grouped query, a column outside an aggregate in SELECT, HAVING or ORDER BY must be one of the GROUP BY expressions, which must be a plain column reference to count. Otherwise the query is `GROUPING`.
5. **HAVING** keeps the groups where it is true.
6. **SELECT** computes the output columns, and `DISTINCT` removes repeated output rows.
7. **ORDER BY** sorts. Each key is one of these:
   - a positive integer literal, which means that output column (out of range is `SYNTAX`);
   - a name equal to an output column alias, which means that output column;
   - any other expression over the source rows. With `DISTINCT`, such an expression must be one of the output expressions; otherwise `MISUSE`.

   NULL sorts before every other value in `ASC`, and after in `DESC`. Rows equal on every key may come in any order.
8. **LIMIT and OFFSET:**
   - Both take expressions without column references that give a non-negative INTEGER. A column reference there is `NO_SUCH_COLUMN`. Any other value is `TYPE_MISMATCH`; NULL counts as any other value.
   - `OFFSET` skips rows, then `LIMIT` keeps at most that many.

**Output column names:**
- the alias when there is one;
- else the column name as written in `CREATE TABLE`, for a plain column reference;
- else `expr<N>`, where N is the 1-based position in the output.

`*` expands to the columns of all sources in order, and `t.*` to the columns of `t`.

### Name rules

- A table alias replaces the table name in that query. The same name used twice for sources in one FROM is `SYNTAX`.
- An unqualified column found in more than one source of the same query level is `AMBIGUOUS_COLUMN`.
- An unknown table is `NO_SUCH_TABLE`, and an unknown column `NO_SUCH_COLUMN`.
  - A qualifier `t` in `t.column` or `t.*` that names no source of this query or of an enclosing query is `NO_SUCH_TABLE`. If `t` names a source that has no such column, it is `NO_SUCH_COLUMN`.
- Name errors are reported before any row is read or changed.

## 3. Output

Each statement writes exactly one result:

| Statement | Output |
| --- | --- |
| SELECT | a header line `H\t` + JSON array of column names, one line `R\t` + JSON array of values per row, then `OK\t<number of rows>` |
| INSERT, UPDATE, DELETE | `OK\t<number of rows inserted, changed or deleted>` |
| any other statement | `OK\t0` |
| a failing statement | only `ERR\t<CODE>` |

- `\t` is one tab character. Every line ends with `\n`.
- Values are written as JSON: an INTEGER as a number, a TEXT as a JSON string written by `JSON.stringify`, and NULL as `null`.
- `UPDATE` counts every row that matches `WHERE`, even when its values do not change.

Error codes:

`SYNTAX`, `NO_SUCH_TABLE`, `NO_SUCH_COLUMN`, `NO_SUCH_INDEX`, `AMBIGUOUS_COLUMN`, `ALREADY_EXISTS`, `TYPE_MISMATCH`, `OVERFLOW`, `CONSTRAINT_UNIQUE`, `CONSTRAINT_NOT_NULL`, `GROUPING`, `MISUSE`, `SUBQUERY_ROWS`, `SUBQUERY_COLUMNS`, `NO_TRANSACTION`, `IN_TRANSACTION`, `BUSY`, `CORRUPT`, `IO_ERROR`, `USAGE`.

**Which error wins:**
1. A statement that does not parse is `SYNTAX`, before anything else. This includes every rule above that says `SYNTAX`.
2. Then names are checked: `NO_SUCH_TABLE`, `NO_SUCH_INDEX`, `ALREADY_EXISTS`, `NO_SUCH_COLUMN`, `AMBIGUOUS_COLUMN`. Then `GROUPING` and `MISUSE`.
3. Then it runs. The first runtime error met while working through rows stops the statement.

The grader only uses statements that have one possible first error under these rules.

**Inserting and updating values:**
- Inserting or updating a value of the wrong type for its column is `TYPE_MISMATCH`.
- NULL in a `NOT NULL` column is `CONSTRAINT_NOT_NULL`.
- A repeated non-NULL value in a `UNIQUE` column, including within the same statement, is `CONSTRAINT_UNIQUE`. Several NULLs are allowed.
- For `UPDATE`, uniqueness is checked on the table as it is after the whole statement, so swapping two unique values in one `UPDATE` works.
- If one row of a statement fails, the whole statement has no effect.

## 4. Transactions

- Without `BEGIN`, each statement is its own transaction and is committed when its result is written.
- `BEGIN` starts a transaction. Inside, statements see their own changes.
  - `COMMIT` makes the changes permanent.
  - `ROLLBACK` undoes everything since `BEGIN`.
  - `BEGIN` inside a transaction is `IN_TRANSACTION`. `COMMIT` or `ROLLBACK` outside one is `NO_TRANSACTION`.
- A failing statement inside a transaction has no effect, and the transaction stays open.
- `CREATE` and `DROP` take part in transactions like other statements.
- If the input ends while a transaction is open, the transaction is rolled back.

## 5. Safety

**After a crash:**
- Once `OK` for an autocommit statement or a `COMMIT` has been written, its changes must survive the process being killed at any point after that.
- A transaction that was not committed must leave no trace, and nothing may be half applied.
- The grader kills processes at random moments. Afterwards, the database must hold the changes of every transaction whose `OK` was written. It may also hold the transaction that was committing at the moment of the kill, complete, but nothing else.
- An unreadable data store is `CORRUPT` for every statement.

**Several processes:**
- Any number of processes may use the same `<datadir>` at once, and the result must be as if their transactions ran one after another.
- A process that cannot get access within 10 seconds reports `BUSY` for that statement.
- If the statement was `BEGIN`, no transaction is open afterwards.
- A process killed while holding access must not block others for more than 5 seconds after its death.
- Work on Windows, Linux and macOS. On Windows, opening or renaming a file another process has open can fail briefly with `EPERM`, `EACCES` or `EBUSY`. Retry rather than report an error.

**Speed (checked by the grader, with generous limits):**
- On a table of 100,000 rows, one process answers 1,000 lookups on the `PRIMARY KEY` and 1,000 on an indexed column (`WHERE col = value`) in under 4 seconds in total, start-up included.
- A join of two 20,000-row tables on an indexed column finishes in under 5 seconds.
- 1,000 single-row autocommit inserts into an existing database finish in under 30 seconds.

## 6. Your work report

**Before anything else**, read the computer's clock (for example `node -e "console.log(new Date().toISOString())"`) and note the time. Do not guess times: read the clock at the start and end of each phase.

When you have finished, write `REPORT.json` in this folder. It is not graded, but it must be honest. Use `null` for anything you do not know. **Never estimate or invent a number**: a `null` is better than a guess.

```json
{
  "tool": "the program you worked in, e.g. Claude Code, Codex",
  "models": ["each model that did work, as your tool names it"],
  "started_at": "2026-01-01T09:00:00.000Z",
  "finished_at": "2026-01-01T09:40:00.000Z",
  "phases": [
    { "name": "reading the task", "started_at": "...", "finished_at": "..." },
    { "name": "design", "started_at": "...", "finished_at": "..." },
    { "name": "writing the code", "started_at": "...", "finished_at": "..." },
    { "name": "testing and fixing", "started_at": "...", "finished_at": "..." },
    { "name": "final checks", "started_at": "...", "finished_at": "..." }
  ],
  "tokens": { "input": null, "cached_input": null, "output": null, "total": null, "source": "where the numbers come from, or null" },
  "cost_usd": null,
  "test_runs": 0,
  "own_tests": { "count": 0, "passing": 0 },
  "restarts_or_context_resets": 0,
  "unclear_rules": [ { "section": "2", "question": "...", "what_you_did": "..." } ],
  "known_bugs": ["anything you know is wrong or missing"],
  "not_done": ["any part of the task you did not finish"],
  "notes": "anything else worth knowing"
}
```

- Times are ISO 8601 in UTC, read from the clock. Phases may be renamed, split or repeated, in the order they happened.
- `tokens` and `cost_usd`: only numbers your tool shows you (for example a usage or cost command). If several models worked, give the total and explain in `notes`.
- `test_runs`: how many times you ran your tests.
