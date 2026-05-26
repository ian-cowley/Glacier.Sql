# Glacier.Sql: Developer and User Reference Manual

This manual describes the syntax, internal architecture, transactional features, and concurrency models implemented in **Glacier.Sql**.

---

## 1. SQL Grammar and Language Reference

Glacier.Sql supports a sub-dialect of T-SQL tailored to columnar processing.

### 1.1 Data Definition Language (DDL)

#### CREATE TABLE
Creates a new table, defines its column schemas, and registers it in the catalog.
```sql
CREATE TABLE table_name (
    column_name DATA_TYPE [Constraints],
    ...
)
```
- **Supported Data Types**: `INT`, `FLOAT` (double precision), `BIT` (boolean), `VARCHAR` (utf-8 strings), `DATETIME` (represented as long timestamps).
- **Supported Column Constraints**:
  - `PRIMARY KEY`: Implicitly specifies `NOT NULL` and enforces uniqueness across the column.
  - `UNIQUE`: Restricts duplicate non-null entries in the column.
  - `NOT NULL`: Rejects inserts or updates containing `NULL` values.
  - `CHECK (expression)`: Rejects modifications if the expression evaluates to `FALSE` (allows `NULL` values).

#### DROP TABLE
Deletes the table definition from the catalog and deletes its backing Arrow IPC file.
```sql
DROP TABLE table_name
```

#### ALTER TABLE
Modifies table schema. Changes are saved back to backing Arrow IPC files immediately.
```sql
ALTER TABLE table_name ADD column_name DATA_TYPE [Constraints]
ALTER TABLE table_name DROP COLUMN column_name
```

---

### 1.2 Data Manipulation Language (DML)

#### INSERT
Inserts new values into a table.
```sql
-- Positional INSERT (Values must map to all schema columns in order)
INSERT INTO table_name VALUES (val1, val2, ...)

-- Explicit column INSERT (Omitted columns are populated with NULL)
INSERT INTO table_name (col1, col2) VALUES (val1, val2)

-- INSERT SELECT
INSERT INTO table_name SELECT col1, col2 FROM other_table
```

#### UPDATE
Updates matching rows in a table.
```sql
UPDATE table_name 
SET column1 = expr1, column2 = expr2 
[WHERE condition]
```

#### DELETE
Deletes matching rows from a table.
```sql
DELETE FROM table_name 
[WHERE condition]
```

---

### 1.3 Data Query Language (DQL)

#### SELECT
Queries data using the Polaris lazy projection engine.
```sql
SELECT [TOP n] projection_list
FROM table_source
[JOIN join_source ON join_condition]
[WHERE filter_condition]
[GROUP BY group_columns]
[ORDER BY sort_column [ASC | DESC]]
```

---

## 2. Advanced SQL Capabilities

### 2.1 Views
Views are named queries stored in the database catalog. 
```sql
CREATE VIEW view_name AS SELECT ...
DROP VIEW view_name
```
- **View Resolution**: The query planner recursively compiles views inlined into the main plan. Columns are correctly propagated, permitting projections, Joins, and aggregations on top of views.

### 2.2 Subqueries
Glacier.Sql compiles subqueries using two distinct strategies:

#### Scalar Subqueries
Used inside expressions (e.g. `WHERE id = (SELECT MIN(id) FROM t)`). The planner compiles and executes the subquery DataFrame, extracts the single scalar value at coordinate `[0,0]`, and replaces the subquery node with a constant literal `Expr.Lit` in the outer query plan.

#### Relational Existential Qualifiers (`EXISTS` / `NOT EXISTS`)
Used in `WHERE` clauses (e.g., `WHERE EXISTS (SELECT * FROM t_b WHERE t_b.id = t_a.id)`).
- **Correlated Subqueries**: The planner splits the filter clause into conjuncts, detects outer-inner table reference correlations, isolates the subquery's filtering expressions, and executes a high-performance **`Semi` Join** (for `EXISTS`) or **`Anti` Join** (for `NOT EXISTS`) using the outer `LazyFrame`.
- **Uncorrelated Subqueries**: Checked once against the subquery result row count; evaluates to a static boolean filter condition.

---

## 3. Concurrency, Locking, and Transactions

### 3.1 Concurrency Locking Model
Glacier.Sql uses a custom, async-friendly `ThreadIndependentReaderWriterLock` on each table to manage isolation:
- **Shared Reads**: Multiple queries can read a table simultaneously.
- **Exclusive Writes**: Write actions (`INSERT`, `UPDATE`, `DELETE`, `ALTER`) acquire exclusive locks, preventing concurrent reads and writes.
- **Async Safety**: Avoids standard C# `ReaderWriterLockSlim` thread-affinity issues by decoupling lock ownership from execution thread IDs, allowing locks to span across async/await continuation steps.

### 3.2 Transactions
Transactions guarantee ACID properties using shadow file rollbacks:
- `BEGIN TRANSACTION`: Opens a transaction context and tracks modifications.
- `COMMIT`: Standardizes all changes, copying shadowed logs to primary Arrow files and cleaning up transaction state.
- `ROLLBACK`: Discards modifications, restores the state from backup table logs, and releases all active locks.

### 3.3 Triggers
SQL-native persistent triggers run synchronously inside DML execution loops:
- `AFTER` triggers execute *after* the primary modification completes (allowing virtual `inserted` or `deleted` selections).
- `INSTEAD OF` triggers override the main DML modification entirely, routing logic to the action script.
- **ACID Integration**: Trigger modifications occurring inside an active transaction scope are fully rolled back if the parent transaction rolls back.

---

## 4. System Metadata (Information Schema)

Query metadata dynamically using standard SQL queries:
- **Tables**: `SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES`
- **Columns**: `SELECT COLUMN_NAME, IS_NULLABLE, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'name'`
