![Glacier.Sql Banner](assets/banner.jpg)

# Glacier.Sql

[![CI](https://github.com/ian-cowley/Glacier.Sql/actions/workflows/dotnet.yml/badge.svg)](https://github.com/ian-cowley/Glacier.Sql/actions/workflows/dotnet.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)
![NativeAOT](https://img.shields.io/badge/NativeAOT-Compatible-brightgreen.svg)

`Glacier.Sql` is an embedded transactional T-SQL query engine for .NET 10 backed by `Glacier.Polaris` columnar DataFrames and Apache Arrow IPC files. It parses SQL into an abstract syntax tree, compiles queries into vectorized `LazyFrame` pipelines, and protects mutations with a CRC32-verified Write-Ahead Log (`glacier.wal`), async reader-writer table locks, views, triggers, and a stdio MCP server host (`Glacier.Sql.Host`).

---

## Install

Add the core engine library:

```bash
dotnet add package Glacier.Sql
```

Or clone and run tests / launch the MCP server from source:

```bash
git clone https://github.com/ian-cowley/Glacier.Sql.git
cd Glacier.Sql
dotnet test Glacier.Sql.slnx -c Release
dotnet run --project src/Glacier.Sql.Host/Glacier.Sql.Host.csproj -c Release -- --data-dir ./db
```

---

## Quick Start

```csharp
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;

using var catalog = new CatalogManager("./my_glacier_db");
var engine = new SqlEngine(catalog);
var context = new ExecutionContext(catalog);

await engine.ExecuteAsync(@"
    CREATE TABLE customers (
        customer_id INT PRIMARY KEY,
        name VARCHAR NOT NULL,
        age INT CHECK (age >= 18)
    );", context);

await engine.ExecuteAsync(
    "INSERT INTO customers VALUES (1, 'Alice', 30), (2, 'Bob', 24);",
    context);

ExecuteResult res = await engine.ExecuteAsync(
    "SELECT name, age FROM customers WHERE age >= 25 ORDER BY age DESC;",
    context);

if (res.Success && res.DataFrame != null)
{
    Console.WriteLine($"Returned {res.DataFrame.RowCount} row(s).");
}

catalog.Checkpoint();
```

---

## What You Can Do

- **T-SQL Parsing & Columnar Execution:** Tokenize and parse DDL, DML, and DQL (`SELECT`, `TOP n`, `INNER`/`LEFT`/`RIGHT`/`FULL`/`CROSS JOIN`, `GROUP BY`, `HAVING`, `ORDER BY`) compiled into `Glacier.Polaris` `LazyFrame` execution plans.
- **Constraints & Triggers:** Enforce `PRIMARY KEY`, `UNIQUE`, `NOT NULL`, and `CHECK` constraints, plus recursive `AFTER` and `INSTEAD OF` triggers with virtual `inserted` and `deleted` tables.
- **Views & Subqueries:** Persist SQL views expanded transparently during query planning, alongside scalar subqueries, `IN` / `NOT IN` (3-valued `NULL` semantics), and correlated `EXISTS` / `NOT EXISTS`.
- **ACID Transactions & WAL Crash Recovery:** Group statements with `BEGIN TRANSACTION`, `COMMIT`, and `ROLLBACK`, backed by in-memory `TableBufferPool` caching, CRC32-framed `glacier.wal`, `AsyncReaderWriterLock`, and `.ipc` table checkpoints.
- **Catalog Introspection & MCP Server:** Query `INFORMATION_SCHEMA.TABLES` / `INFORMATION_SCHEMA.COLUMNS`, or attach AI coding clients directly over JSON-RPC stdio via `Glacier.Sql.Host` (`execute_sql`, `list_tables`, `get_table_schema`).

---

## Platform & Compatibility

- **Runtime:** .NET 10 (`net10.0`), Native AOT compatible (`IsAotCompatible=true`).
- **Storage Format:** Central `catalog.json` metadata + per-table Apache Arrow IPC files (`*.ipc`) + binary CRC32 WAL (`glacier.wal`).
- **Cross-Platform:** Tested on Windows and Linux x64 in GitHub Actions CI.

---

## Benchmarks

Measured in `Release` on Windows 11 with an AMD Ryzen AI 9 HX 370 (Zen 5, AVX-512). Reproduce with `dotnet run --project benchmarks/Glacier.Sql.Benchmarks/Glacier.Sql.Benchmarks.csproj -c Release`:

| Pipeline Stage | Workload | Throughput | Latency | Allocation |
| :--- | :--- | ---: | ---: | ---: |
| `SqlLexer.Tokenize` | Simple `SELECT` | 770,402 ops/s | 1.30 us | 1,184.0 B/op |
| `SqlLexer.Tokenize` | `INSERT` Batch | 2,309,367 ops/s | 0.43 us | 1,808.0 B/op |
| `TSqlParser.Parse` | Simple `SELECT` AST | 817,643 ops/s | 1.22 us | 640.0 B/op |
| `TSqlParser.Parse` | Complex `JOIN` AST | 615,680 ops/s | 1.62 us | 1,480.0 B/op |
| `QueryPlanner.PlanQuery` | Plan Simple `SELECT` | 96,739 ops/s | 10.34 us | 6,848.4 B/op |
| `QueryPlanner.PlanQuery` | Plan `AGG` + `GROUP BY` | 155,558 ops/s | 6.43 us | 11,928.0 B/op |

See [the full benchmark suite](docs/benchmarks.md) for all 13 operations across lexer, parser, and query planner stages.

---

## Documentation

- [SQL & API Manual](docs/manual.md) — T-SQL syntax, types, constraints, joins, subqueries, triggers, transactions, and C# API
- [Architecture](docs/architecture.md) — Lexer/Pratt parser, Polaris planner compilation, `TableBufferPool`, WAL v2 recovery, and `AsyncReaderWriterLock`
- [MCP Server Setup](docs/mcp.md) — Connecting AI clients to `Glacier.Sql.Host` (`execute_sql`, `list_tables`, `get_table_schema`)
- [Benchmarks](docs/benchmarks.md) — Full Release microbenchmarks and BenchmarkDotNet reproduction commands
- [Changelog](CHANGELOG.md) — Version history

---

## License & Credits

Licensed under the [MIT License](LICENSE).

Developed by Ian Cowley, Antigravity (Google DeepMind) and Claude.
