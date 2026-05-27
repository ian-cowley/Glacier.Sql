# Glacier.Sql

[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Sql.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Sql/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Sql.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Sql/)

**Glacier.Sql** is a high-performance C# SQL query execution engine built directly on top of the **Glacier.Polaris** columnar, memory-efficient Arrow-backed DataFrame engine. It translates T-SQL queries into optimized Polaris `LazyFrame` expression pipelines, enabling low-overhead SQL access to columnar files on disk.

---

## Features

- **T-SQL Parser**: A custom Pratt/recursive-descent tokenizer and parser implementing standard SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, and DROP statements.
- **Transactional Storage**: SQL-native transactions (`BEGIN TRANSACTION`, `COMMIT`, `ROLLBACK`) with crash-recovery mechanisms via shadowed table file logging.
- **Native Triggers**: Persistent, automated SQL-native triggers (`AFTER` and `INSTEAD OF` for `INSERT`, `UPDATE`, and `DELETE` events) with virtual `inserted` and `deleted` context resolution.
- **Relational View Engine**: Fully persisted relational view definitions in the catalog, recursively inlined into query execution plans.
- **Correlated Subqueries**: Support for scalar subqueries, list membership (`IN`), and correlated/uncorrelated existential qualifiers (`EXISTS` / `NOT EXISTS`) mapped to optimized `Semi`/`Anti` relational joins.
- **Schema Alterations**: Schema modifications (`ALTER TABLE ADD/DROP COLUMN`) with automatic Arrow IPC file rewrites and null-padding.
- **Data Integrity Constraints**: Robust column-level constraints including `PRIMARY KEY`, `UNIQUE`, `NOT NULL`, and `CHECK` expressions with transaction rollbacks on violation.
- **Concurrency & Locking**: Async-friendly, thread-independent reader-writer locking mechanism to ensure serializable write isolation and non-blocking shared reads.
- **Information Schema**: Transient system metadata querying via `INFORMATION_SCHEMA.TABLES` and `INFORMATION_SCHEMA.COLUMNS`.

---

## Project Structure

```text
Glacier.Sql/
│
├── Glacier.Sql.slnx               # Visual Studio Solution model
│
├── src/
│   ├── Glacier.Sql/               # Main engine assembly
│   │   ├── Catalog/               # Metadata catalog & async-friendly table locking
│   │   ├── Engine/                # Query compilation (Planner), Execution handlers & SQL Engine
│   │   ├── Parser/                # Lexer, AST Nodes, Pratt Statement/Expression Parsers
│   │   └── Storage/               # Arrow IPC reading and writing wrappers
│   │
│   └── Glacier.Sql.Host/          # Stdio-based JSON-RPC MCP Server host
│
├── tests/
│   └── Glacier.Sql.Tests/         # Extensively covered XUnit test suite
│
└── MANUAL.md                      # Comprehensive User & Developer SQL reference manual
```

---

## Getting Started

### Prerequisites

- .NET 10.0 SDK

### Build the Solution

Run the following command in the solution root directory to compile:

```bash
dotnet build
```

### Run Tests

To execute the test suite validating transactions, triggers, view inlining, subqueries, concurrency locking, and constraint enforcement:

```bash
dotnet test
```

For more detailed information on syntax, design patterns, and internal architecture, see [MANUAL.md](file:///c:/Users/spuri/source/repos/PolarsPlus/Glacier.Sql/MANUAL.md).
