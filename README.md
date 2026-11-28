![Glacier.Sql Banner](assets/banner.jpg)

# Glacier.Sql

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Sql.svg)](https://www.nuget.org/packages/Glacier.Sql/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Sql.svg)](https://www.nuget.org/packages/Glacier.Sql/)
[![Ecosystem](https://img.shields.io/badge/Glacier-Ecosystem-blue)](https://github.com/ian-cowley)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)
[![Buy Me A Coffee](https://img.shields.io/badge/Buy%20Me%20A%20Coffee-Support-orange.svg?logo=buy-me-a-coffee)](https://buymeacoffee.com/glaciersoftware)

**Glacier.Sql** is a high-performance, Native AOT-compatible C# SQL query execution engine built directly on top of the **Glacier.Polaris** columnar, memory-efficient Arrow-backed DataFrame engine. It translates T-SQL queries into optimized Polaris `LazyFrame` expression pipelines, combining zero-copy columnar reads with an in-memory `TableBufferPool`, async-native table locking, and deterministic Write-Ahead Logging (WAL).

---

## Features

- **T-SQL Parser & AST**: Zero-allocation `ReadOnlySpan<char>` & ASCII-folding byte-Trie `SqlLexer` and Pratt/recursive-descent `TSqlParser` (with `SqlSyntaxException`, strict `EOF` checking, and `;` terminators) implementing `SELECT` (with `GROUP BY`, `HAVING`, `ORDER BY` expressions, `TOP`, `INNER`/`LEFT [OUTER]`/`RIGHT [OUTER]`/`FULL [OUTER]`/`CROSS JOIN`), multi-row `INSERT ... VALUES (...), (...)`, `INSERT ... SELECT`, `UPDATE`, `DELETE`, `CREATE`/`ALTER`/`DROP TABLE`, `CREATE`/`DROP VIEW`, `CREATE`/`DROP TRIGGER`, and `BEGIN`/`COMMIT`/`ROLLBACK TRANSACTION`.
- **Post-Aggregate & Standard `HAVING` / `ORDER BY`**: Full aggregate extraction and deduplication across `SELECT`, `HAVING`, and `ORDER BY` — supporting projected and unprojected aggregates (`HAVING COUNT(*) > 2`), compound `AND`/`OR` predicates, non-aggregate `HAVING` & `ORDER BY` expressions, projection aliases, and global aggregate filtering.
- **Deterministic WAL v2 & Crash Recovery**: Hardware-accelerated IEEE 802.3 CRC32 (`System.IO.Hashing.Crc32`), `Guid TableId`-keyed WAL v2 frames (preventing drop+recreate replay corruption), compact `RowDeleteDelta`/`RowUpdateDelta` row-ID records, $O(1)$ amortized chunked `INSERT` buffering, fail-closed header validation, monotonic LSN recovery across restarts, automatic WAL truncation on clean checkpoints, and uncommitted transaction isolation (including transactional DDL & auto-commit `AFTER` trigger rollback).
- **Async-Native Concurrency (`AsyncReaderWriterLock`)**: Fair, non-blocking async reader-writer table locks with deadlock-safe `UpgradeReadToWriteAsync`, canonical multi-table lock ordering across joins/views/subqueries, locked `INSTEAD OF` triggers, and re-entrant trigger execution contexts.
- **Native Triggers & Relational Views**: Persistent `AFTER` and `INSTEAD OF` triggers (with `INSTEAD OF UPDATE` running prior to base table constraints) using `inserted`/`deleted` virtual tables, plus catalog-persisted views with AST/schema caching.
- **Correlated Subqueries & Set Membership**: Scalar subqueries, semi-join/hash `IN` / `NOT IN` (with SQL 3-valued `NULL` semantics), and recursive `EXISTS` / `NOT EXISTS` / `OR EXISTS` supporting multi-column and non-`Int32` correlations.
- **Native AOT & MCP Host**: Source-generated `System.Text.Json` contexts (`CatalogJsonContext` and `SqlMcpJsonContext`) across `Glacier.Sql` and `Glacier.Sql.Host`, isolated via `GLACIER_SQL_DATA_DIR`, gated logging (`GLACIER_SQL_MCP_LOG`), `isError` reporting, and `maxResults`/`offset` pagination.

---

## Data Types

Glacier.Sql supports the following SQL-to-Polaris data types:

| SQL Type | Polaris Underlay | Description | Example Literal |
| :--- | :--- | :--- | :--- |
| **`INT`** | `Int32Series` | 32-bit signed integer | `123`, `-42` |
| **`FLOAT`** / **`DOUBLE`** | `Float64Series` | 64-bit double precision float | `123.45`, `-3.14` |
| **`BIT`** / **`BOOLEAN`** | `BooleanSeries` | Logical true/false boolean | `1` / `TRUE`, `0` / `FALSE` |
| **`VARCHAR`** / **`TEXT`** | `Utf8StringSeries` | UTF-8 encoded text string | `'Hello World'` |
| **`DATETIME`** / **`DATE`** | `TimeSeries` | Unix epoch timestamp (ms) | `'2026-05-26'` |

---

## SQL Examples & Usage

### 1. Aggregations, `HAVING` & Post-Group `ORDER BY`
```sql
SELECT region, SUM(qty) AS total_qty
FROM sales
GROUP BY region
HAVING COUNT(*) >= 2 AND SUM(qty) > 100
ORDER BY total_qty DESC;
```

### 2. Constraints & Table Alterations
```sql
-- Create table with PRIMARY KEY, UNIQUE, NOT NULL, and CHECK constraints
CREATE TABLE customers (
    customer_id INT PRIMARY KEY,
    name VARCHAR NOT NULL,
    email VARCHAR UNIQUE,
    age INT CHECK (age >= 18)
);

-- Add nullable VARCHAR or INT column (existing rows are padded with valid NULL masks)
ALTER TABLE customers ADD notes VARCHAR;
ALTER TABLE customers ADD loyalty_points INT CHECK (loyalty_points >= 0);

-- Drop column (atomically updates Arrow IPC storage and WAL checkpoint boundary)
ALTER TABLE customers DROP COLUMN loyalty_points;
```

### 3. Transactions & Triggers
```sql
CREATE TRIGGER audit_log ON orders
AFTER INSERT
AS
INSERT INTO order_history
SELECT id, qty, 'INSERTED' FROM inserted;

BEGIN TRANSACTION;
INSERT INTO orders VALUES (10, 50);
UPDATE inventory SET stock = stock - 10 WHERE item_id = 1;
COMMIT; -- Or ROLLBACK to restore pre-transaction snapshot
```

### 4. Views & Correlated Subqueries
```sql
CREATE VIEW active_customers AS
SELECT customer_id, name
FROM customers
WHERE age >= 18;

SELECT val FROM t_a
WHERE EXISTS (
    SELECT * FROM t_b
    WHERE t_b.id = t_a.id AND score >= 90
);
```

---

## Getting Started

### Prerequisites
- .NET 10.0 SDK

### Build the Solution
```bash
dotnet build Glacier.Sql.slnx -c Release
```

### Run Tests
```bash
dotnet test tests/Glacier.Sql.Tests/Glacier.Sql.Tests.csproj -c Release
```

### Start the MCP Server Host
The executable host runs a Native AOT-compatible JSON-RPC Model Context Protocol (MCP) server over stdio:
```bash
dotnet run --project src/Glacier.Sql.Host/Glacier.Sql.Host.csproj -c Release
```
- **Data Directory**: Resolved via `--data-dir <path>` -> `GLACIER_SQL_DATA_DIR` environment variable -> `%LOCALAPPDATA%/Glacier/Sql/Data` (never writes `Data/` into `AppContext.BaseDirectory`).
- **Diagnostic Logging**: Disabled by default. Set `GLACIER_SQL_MCP_LOG=1` (writes inside the resolved data directory) or an explicit file path to enable `mcp_sql_log.txt`.
- **Pagination**: `execute_sql` accepts optional `maxResults` (default `200`, max `1000`) and `offset` (default `0`) arguments.

---

## Documentation

- **[Architecture Overview (`ARCHITECTURE.md`)](ARCHITECTURE.md)** — Deep dive into the query compilation pipeline, `AsyncReaderWriterLock` concurrency protocol, `TableBufferPool` snapshot isolation, and binary WAL frame/checkpoint design.
- **[Glacier.Sql Reference Manual (`MANUAL.md`)](MANUAL.md)** — Comprehensive SQL syntax reference and MCP host usage guide.

---

## 🆕 What's New in v1.1.1

- **Hardened Concurrency, Triggers & Transactional DDL** — Exclusive locking on `INSTEAD OF INSERT`, `INSTEAD OF UPDATE` ordered before constraint checks, automatic implicit-transaction rollback on failing `AFTER` triggers, LIFO transaction rollback, and full transactional DDL (`CREATE`/`DROP`/`ALTER TABLE`, `CREATE`/`DROP VIEW`, `CREATE`/`DROP TRIGGER`).
- **WAL v2, Hardware CRC32 & $O(1)$ Delta Buffering** — `System.IO.Hashing.Crc32` hardware acceleration, `Guid TableId`-keyed WAL v2 frames (immune to drop+recreate replay), compact `RowDeleteDelta`/`RowUpdateDelta` frames, `ArrayPool` WAL buffering, checkpoint WAL truncation, fail-closed corrupt `catalog.json`/WAL header guards, and chunked `TableBufferEntry` appends.
- **Span/Trie Lexer, Planner & MCP Host Hardening** — Zero-alloc `ReadOnlySpan<char>` + byte-Trie `SqlLexer`, `SqlSyntaxException` with `EOF` enforcement, multi-row `INSERT`, `NOT IN` 3-valued `NULL` handling, `COUNT(*)` NULL accuracy, multi-key/OR `EXISTS`, `RIGHT`/`FULL`/`LEFT OUTER` & swapped `ON` joins, async planner (`PlanQueryAsync`), view AST/schema caching, and `GLACIER_SQL_DATA_DIR` + `maxResults`/`offset` host isolation.
- **81 unit, concurrency & durability tests** passing (100% green).

---

## Credits

Developed by Ian Cowley and Antigravity (Google DeepMind).

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## ❄️ Glacier Ecosystem & Support

Part of the **[Glacier Ecosystem](https://glaciersoftware.dev/)** — high-performance, zero-allocation developer tooling and data engines natively built for modern C# and .NET 10.

* 🌐 **Website & Documentation**: [glaciersoftware.dev](https://glaciersoftware.dev/)
* ☕ **Buy Me a Coffee**: [buymeacoffee.com/glaciersoftware](https://buymeacoffee.com/glaciersoftware)
* ❤️ **GitHub Sponsors**: [github.com/sponsors/ian-cowley](https://github.com/sponsors/ian-cowley)
* 🐙 **All Repositories**: [github.com/ian-cowley](https://github.com/ian-cowley)
