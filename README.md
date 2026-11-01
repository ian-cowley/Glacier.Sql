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

- **T-SQL Parser & AST**: Custom Pratt/recursive-descent tokenizer and parser implementing `SELECT` (with `GROUP BY`, `HAVING`, `ORDER BY`, `TOP`, `JOIN`), `INSERT`, `INSERT ... SELECT`, `UPDATE`, `DELETE`, `CREATE`, `ALTER`, and `DROP`, including bracketed identifiers (`[col 1]`), signed/64-bit numeric literals, and constant scalar expressions.
- **Post-Aggregate `HAVING` & `ORDER BY`**: Full aggregate extraction and deduplication across `SELECT`, `HAVING`, and `ORDER BY` — supporting projected and unprojected aggregates (`HAVING COUNT(*) > 2`), compound `AND`/`OR` predicates, projection aliases, and global aggregate filtering.
- **Deterministic WAL & Crash Recovery**: Physical Arrow IPC mutation logging (`RowInsert` deltas and `RowUpdate`/`RowDelete` post-mutation snapshots) protected by 32-byte header CRC32 + per-frame CRC32 validation, monotonic LSN recovery across restarts, per-table `CheckpointEnd` watermarks, and uncommitted transaction isolation during checkpoints.
- **Async-Native Concurrency (`AsyncReaderWriterLock`)**: Fair, non-blocking async reader-writer table locks with deadlock-safe `UpgradeReadToWriteAsync`, canonical multi-table lock ordering across joins/views/subqueries, and re-entrant trigger execution contexts.
- **Native Triggers & Relational Views**: Persistent `AFTER` and `INSTEAD OF` triggers with `inserted`/`deleted` virtual tables, plus catalog-persisted views recursively inlined into query plans.
- **Correlated Subqueries**: Scalar subqueries, `IN (...)` subqueries, and correlated `EXISTS` / `NOT EXISTS` compiled to Polaris `Semi` / `Anti` joins without mutating cached ASTs.
- **Native AOT Ready**: Source-generated `System.Text.Json` contexts (`CatalogJsonContext` and `SqlMcpJsonContext`) across both `Glacier.Sql` and `Glacier.Sql.Host` with `<IsAotCompatible>true</IsAotCompatible>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.

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

---

## Documentation

- **[Architecture Overview (`ARCHITECTURE.md`)](ARCHITECTURE.md)** — Deep dive into the query compilation pipeline, `AsyncReaderWriterLock` concurrency protocol, `TableBufferPool` snapshot isolation, and binary WAL frame/checkpoint design.
- **[Glacier.Sql Reference Manual (`MANUAL.md`)](MANUAL.md)** — Comprehensive SQL syntax reference and MCP host usage guide.

---

## 🆕 What's New in v1.1.0

- **Complete `HAVING` & Post-Aggregate `ORDER BY` Pipeline** — Supports projected and unprojected aggregates (`HAVING COUNT(*) > 2`), compound boolean expressions, `SELECT` aliases, global aggregate `HAVING`, and post-group sorting without leaking internal helper columns.
- **Fair Async-Native Locking (`AsyncReaderWriterLock`)** — Replaces sync-over-async `SemaphoreSlim` hacks with handle-scoped `LockReleaser` ownership, deadlock-safe read-to-write lock upgrades, canonical multi-table lock ordering across views/subqueries, and serialized DML read-modify-write execution (zero lost updates).
- **Physical Arrow IPC WAL & Monotonic Crash Recovery** — `RowUpdate` and `RowDelete` now persist deterministic Arrow IPC snapshots rather than raw SQL text; `WalWriter`/`WalReader` validate 32-byte header CRC32 checksums, restore monotonic `_currentLsn` across multi-crash restarts, and isolate uncommitted transactions during checkpoints.
- **Native AOT & Strict Build Hygiene** — Enabled `<IsAotCompatible>true</IsAotCompatible>` and `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` with source-generated JSON contexts (`CatalogJsonContext`, `SqlMcpJsonContext`) and zero transitive NuGet vulnerabilities.
- **59 unit & adversarial stress tests** passing (100% green).

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
