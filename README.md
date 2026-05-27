# Glacier.Sql

[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Sql.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Sql/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Sql.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Sql/)

**Glacier.Sql** is a high-performance C# SQL query execution engine built directly on top of the **Glacier.Polaris** columnar, memory-efficient Arrow-backed DataFrame engine. It translates T-SQL queries into optimized Polaris `LazyFrame` expression pipelines, enabling low-overhead SQL access to columnar files on disk.

---

## Features

- **T-SQL Parser**: Custom Pratt/recursive-descent tokenizer and parser implementing SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, and DROP.
- **Transactional Storage**: ACID compliant SQL-native transactions (`BEGIN TRANSACTION`, `COMMIT`, `ROLLBACK`) with crash-recovery shadowed table file logging.
- **Native Triggers**: Persistent, automated SQL-native triggers (`AFTER` and `INSTEAD OF`) with virtual `inserted` and `deleted` tables context resolution.
- **Relational View Engine**: Relational view definitions in the catalog, recursively inlined into query execution plans.
- **Correlated Subqueries**: Scalar subqueries, list membership (`IN`), and existential qualifiers (`EXISTS` / `NOT EXISTS`) mapped to optimized `Semi`/`Anti` joins.
- **Schema Alterations**: Schema modifications (`ALTER TABLE ADD/DROP COLUMN`) with automatic Arrow IPC file rewrites and null-padding.
- **Data Integrity Constraints**: Column-level constraints (`PRIMARY KEY`, `UNIQUE`, `NOT NULL`, and `CHECK` expressions) with transaction rollbacks on violation.
- **Concurrency & Locking**: Async-friendly, thread-independent reader-writer locking mechanism to ensure serializable write isolation and non-blocking shared reads.
- **Information Schema**: Transient system metadata querying via `INFORMATION_SCHEMA.TABLES` and `INFORMATION_SCHEMA.COLUMNS`.

---

## Data Types

Glacier.Sql supports the following SQL-to-Polaris data types:

| SQL Type | Polaris Underlay | Description | Example Literal |
| :--- | :--- | :--- | :--- |
| **`INT`** | `Int32Series` | 32-bit signed integer | `123` |
| **`FLOAT`** | `Float64Series` | 64-bit double precision float | `123.45` |
| **`BIT`** | `BooleanSeries` | Logical true/false boolean | `1` (true), `0` (false) |
| **`VARCHAR`** | `Utf8StringSeries` | UTF-8 encoded text string | `'Hello World'` |
| **`DATETIME`** | `TimeSeries` | Unix epoch timestamp | `'2026-05-26'` |

---

## SQL Examples & Usage

### 1. Constraints & Table Alterations
```sql
-- Create table with PRIMARY KEY, UNIQUE, NOT NULL, and CHECK constraints
CREATE TABLE customers (
    customer_id INT PRIMARY KEY,
    name VARCHAR NOT NULL,
    email VARCHAR UNIQUE,
    age INT CHECK (age >= 18)
);

-- Add column with CHECK constraint (existing rows are padded with NULL)
ALTER TABLE customers ADD loyalty_points INT CHECK (loyalty_points >= 0);

-- Drop column (rewrites underlying Arrow IPC files)
ALTER TABLE customers DROP COLUMN loyalty_points;
```

### 2. Transactions & Triggers
```sql
-- Create an audit trigger
CREATE TRIGGER audit_log ON orders 
AFTER INSERT 
AS 
INSERT INTO order_history 
SELECT id, qty, 'INSERTED' FROM inserted;

-- Execute DML modifications inside an ACID transaction
BEGIN TRANSACTION;
INSERT INTO orders VALUES (10, 50);
UPDATE inventory SET stock = stock - 10 WHERE item_id = 1;
COMMIT; -- Or ROLLBACK to restore state and shadow files
```

### 3. Views & Correlated Subqueries
```sql
-- Create view (defined as select query)
CREATE VIEW active_customers AS 
SELECT customer_id, name 
FROM customers 
WHERE age >= 18;

-- Correlated EXISTS subquery (planner maps this to an optimized Semi-Join)
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
Run the following command in the solution root directory to compile the project:
```bash
dotnet build
```

### Run Tests
To run the automated integration test suite validating transactions, triggers, view inlining, subqueries, concurrency locking, and constraints:
```bash
dotnet test
```

### Start the MCP Server Host
The executable host acts as a JSON-RPC Model Context Protocol (MCP) server via stdio. Start it using:
```bash
dotnet run --project src/Glacier.Sql.Host/Glacier.Sql.Host.csproj
```

---

## Documentation

For a comprehensive guide covering all syntax, internal engine architecture, database locking model, and CLI/MCP host details, see the **[Glacier.Sql Reference Manual (MANUAL.md)](MANUAL.md)**.

---

## Credits

Developed by Ian Cowley and Antigravity (Google DeepMind).

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
