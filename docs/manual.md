# Glacier.Sql User Manual

This manual covers both the embedded C# engine API (`Glacier.Sql`) and the supported T-SQL dialect compiled onto `Glacier.Polaris`.

---

## 1. Embedded C# API (`CatalogManager`, `SqlEngine`, `Diagnostics`)

```csharp
using Glacier.Sql.Catalog;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Engine;

GlacierSqlDiagnostics.SetLogger(new ConsoleGlacierLogger(LogLevel.Warning));

using var catalog = new CatalogManager("./database_dir");
var engine = new SqlEngine(catalog);
var ctx = new ExecutionContext(catalog);

ExecuteResult res = await engine.ExecuteAsync("SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES;", ctx);
if (!res.Success)
{
    Console.Error.WriteLine(res.Message);
}

await engine.CheckpointAsync(); // or catalog.Checkpoint()
```

Key types:

- `CatalogManager(string dataDirectory)` — Loads `catalog.json`, validates/replays `glacier.wal`, manages `TableBufferPool`, views, triggers, and per-table `AsyncReaderWriterLock` instances.
- `SqlEngine` — Parses scripts with `SqlLexer` and `TSqlParser`, executes multi-statement scripts (`ExecuteAsync`) or single statements (`ExecuteStatementAsync`), manages `SqlTransaction` state, and fires triggers.
- `ExecuteResult` — Exposes `Success`, `Message`, `DataFrame?`, and `AffectedRows`.
- `GlacierSqlDiagnostics` — Optional pluggable logger (`NullGlacierLogger`, `ConsoleGlacierLogger`, `DelegateGlacierLogger`). Logging is off by default (`NullGlacierLogger`).

---

## 2. Supported Data Types

| SQL Type | Polaris Underlay | Description | Example Literal |
| :--- | :--- | :--- | :--- |
| `INT` | `Int32Series` | 32-bit signed integer | `123` |
| `FLOAT` | `Float64Series` | 64-bit floating-point number | `123.45` |
| `BIT` | `BooleanSeries` | Boolean value | `1`, `0` |
| `VARCHAR` | `Utf8StringSeries` | UTF-8 text string | `'Hello World'` |
| `DATETIME` | `TimeSeries` | Unix epoch timestamp (`Int64` ms) | `'2026-05-26'` |

---

## 3. Data Definition Language (DDL)

### `CREATE TABLE` & Constraints

```sql
CREATE TABLE customers (
    customer_id INT PRIMARY KEY,
    name VARCHAR NOT NULL,
    email VARCHAR UNIQUE,
    age INT CHECK (age >= 18)
);
```

- `PRIMARY KEY`: Enforces `NOT NULL` and uniqueness.
- `UNIQUE`: Rejects duplicate non-null values (multiple `NULL` entries are permitted).
- `NOT NULL`: Rejects `NULL` values on `INSERT` and `UPDATE`.
- `CHECK (expr)`: Rejects writes when the expression evaluates to `FALSE`; `NULL/UNKNOWN` passes.

### `DROP TABLE` & `ALTER TABLE`

```sql
ALTER TABLE customers ADD loyalty_points INT CHECK (loyalty_points >= 0);
ALTER TABLE customers DROP COLUMN loyalty_points;
DROP TABLE customers;
```

`ALTER TABLE` rewrites the table schema and underlying Arrow IPC storage cleanly inside the active transaction scope.

---

## 4. Data Manipulation Language (DML)

```sql
-- Positional multi-row INSERT
INSERT INTO customers VALUES
    (1, 'Alice', 'alice@test.com', 30),
    (2, 'Carla', 'carla@test.com', 28);

-- Explicit column INSERT (remaining columns initialized to NULL)
INSERT INTO customers (customer_id, name) VALUES (3, 'Bob');

-- INSERT ... SELECT
INSERT INTO customer_archive SELECT * FROM customers WHERE age > 65;

-- UPDATE & DELETE
UPDATE customers SET age = age + 1, email = 'updated_' + email WHERE customer_id = 1;
DELETE FROM customers WHERE age < 18;
```

---

## 5. Queries (`SELECT`, Joins, Aggregations, Sorting)

```sql
SELECT [TOP n] projection_list
FROM table_source
[INNER | LEFT [OUTER] | RIGHT [OUTER] | FULL [OUTER] | CROSS JOIN join_source [ON join_condition]]
[WHERE filter_condition]
[GROUP BY group_columns]
[HAVING having_condition]
[ORDER BY sort_expression [ASC | DESC]]
```

- **Aggregations:** `COUNT(*)`, `COUNT(col)`, `SUM(col)`, `AVG(col)`, `MIN(col)`, `MAX(col)`. `SUM`/`AVG`/`MIN`/`MAX` ignore `NULL`; `COUNT(*)` counts all rows.
- **Joins:** `INNER`, `LEFT`, `RIGHT`, `FULL`, and `CROSS JOIN`. When `ON t1.id = t2.cust_id` uses different column names, `QueryPlanner` automatically aligns right-side join keys before calling `LazyFrame.Join`.

---

## 6. SQL Views

Views store named SQL definitions in the catalog and expand inline during `QueryPlanner` compilation:

```sql
CREATE VIEW active_customers AS
SELECT customer_id, name, email
FROM customers
WHERE age >= 18;

SELECT * FROM active_customers WHERE name = 'Alice';

DROP VIEW active_customers;
```

---

## 7. Subqueries (`Scalar`, `IN`, `EXISTS`)

- **Scalar subqueries:** Evaluated up front and folded into `Expr.Lit(...)` in the outer filter:
  ```sql
  SELECT name FROM customers WHERE age = (SELECT MAX(age) FROM customers);
  ```
- **List membership (`IN` / `NOT IN`):** Compiles to vectorized hash-set membership (`IsIn`) with SQL 3-valued `NULL` semantics (`NOT IN` yields empty when the subquery or literal set contains `NULL`):
  ```sql
  SELECT name FROM customers
  WHERE customer_id IN (SELECT customer_id FROM orders WHERE amount > 100);
  ```
- **Existential qualifiers (`EXISTS` / `NOT EXISTS`):** Supports single- and multi-column correlations plus boolean combinations:
  ```sql
  SELECT val FROM t_a
  WHERE EXISTS (SELECT * FROM t_b WHERE t_b.id = t_a.id AND score >= 90);
  ```

---

## 8. Transactions & Crash Recovery

```sql
BEGIN TRANSACTION;
INSERT INTO orders VALUES (10, 50.0);
UPDATE inventory SET stock = stock - 10 WHERE item_id = 1;
COMMIT; -- or ROLLBACK;
```

- Auto-commit statements are atomic; `BEGIN TRANSACTION` acquires canonical per-table locks via `AsyncReaderWriterLock` and releases them on `COMMIT` / `ROLLBACK`.
- On startup, `CatalogManager.Load()` validates `glacier.wal` headers and replays committed records past the checkpoint LSN while ignoring aborted or uncommitted transactions.
- `catalog.Checkpoint()` flushes dirty `CommittedSnapshot` DataFrames atomically to `.ipc` files and resets `glacier.wal`.

---

## 9. Triggers

`Glacier.Sql` supports persistent `AFTER` and `INSTEAD OF` triggers with virtual `inserted` and `deleted` tables and recursive trigger cascading:

```sql
CREATE TRIGGER audit_log ON orders
AFTER INSERT
AS
INSERT INTO order_history
SELECT id, qty, 'INSERTED' FROM inserted;

CREATE TRIGGER route_large ON orders
INSTEAD OF INSERT
AS
INSERT INTO high_value_orders SELECT * FROM inserted WHERE amount >= 1000;

DROP TRIGGER audit_log;
```

Auto-commit DML on tables with `AFTER` triggers automatically wraps base mutations and cascading triggers inside an implicit transaction so any failure rolls back the entire chain atomically.

---

## 10. Information Schema

```sql
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES;

SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_NAME = 'customers';
```
