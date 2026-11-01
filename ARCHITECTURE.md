# Glacier.Sql Architecture

`Glacier.Sql` is an embedded, Native AOT-ready relational SQL engine built over `Glacier.Polaris` Arrow-backed columnar `DataFrame` and `LazyFrame` pipelines. This document describes its internal subsystems, concurrency invariants, query compilation pipeline, and crash-recovery architecture.

---

## 1. High-Level Execution Pipeline

```text
SQL Text
   │
   ▼
SqlLexer ──► Token Stream
   │
   ▼
TSqlParser (Pratt + Recursive Descent) ──► SqlStatement AST
   │
   ▼
SqlEngine (DDL / DML / Transaction Coordinator / Trigger Dispatcher)
   │  ├── AsyncReaderWriterLock (Canonical Multi-Table Lock Acquisition)
   │  ├── QueryPlanner ──► Glacier.Polaris LazyFrame ──► Collect()
   │  ├── Constraint Validator (NOT NULL / UNIQUE / PRIMARY KEY / CHECK)
   │  ├── WalWriter (CRC32 Binary Frame Append + Flush(true))
   │  └── TableBufferPool (In-Memory Committed vs Active-Tx Snapshots)
   ▼
Arrow IPC Table Files (*.ipc) + Atomic Catalog Metadata (catalog.json)
```

---

## 2. Subsystem Breakdown

### 2.1 Lexer & Pratt Parser (`src/Glacier.Sql/Parser/`)
- **`SqlLexer`**: Zero-dependency scanner tokenizing keywords, operators, strings (`''` escaped), signed/64-bit numbers, and `[bracketed identifiers]`. Exposes `SqlLexer.IsKeyword` so AST-to-SQL reconstruction safely brackets reserved words and identifiers containing whitespace or punctuation.
- **`TSqlParser`** (`TSqlParser.cs`, `TSqlParser.Dml.cs`, `TSqlParser.Expressions.cs`):
  - Recursive-descent statement parser for `SELECT`, `INSERT`, `INSERT ... SELECT`, `UPDATE`, `DELETE`, `CREATE/ALTER/DROP TABLE`, `CREATE/DROP VIEW`, `CREATE TRIGGER`, and `BEGIN/COMMIT/ROLLBACK TRANSACTION`.
  - Top-down operator-precedence (Pratt) expression parser supporting unary `+`, `-`, `NOT`, arithmetic, comparisons, `IS [NOT] NULL`, `IN (...)`, scalar subqueries, and `EXISTS`.
  - Parses integer literals with `long.TryParse(..., CultureInfo.InvariantCulture)`, narrowing to `int` when within `[int.MinValue, int.MaxValue]` and folding `-2147483648` cleanly without overflow.

### 2.2 Query Planner (`src/Glacier.Sql/Engine/QueryPlanner*.cs`)
- Translates `SelectStatement` ASTs into `Glacier.Polaris.LazyFrame` plans:
  1. **Source & Join Resolution**: Resolves physical tables from the owning `CatalogManager.BufferPool`, recursively inlines `CREATE VIEW` definitions, synthesizes transient `INFORMATION_SCHEMA.TABLES` / `INFORMATION_SCHEMA.COLUMNS` frames, and resolves `inserted` / `deleted` trigger tables.
  2. **Correlated `EXISTS` Optimization**: Clones subquery ASTs before extracting correlation predicates (`inner.col = outer.col`) and compiles correlated `EXISTS` / `NOT EXISTS` into `JoinType.Semi` / `JoinType.Anti` joins without mutating shared AST nodes.
  3. **Pre-Group `WHERE` Filtering**: Compiles row-level predicates prior to aggregation, including T-SQL `BIT` literal coercion (`bit_col = 1` / `0`).
  4. **Aggregate Extraction, `GROUP BY`, `HAVING`, & Post-Group `ORDER BY`**:
     - Walks `Projections`, `Having`, and `OrderBy` to deduplicate all `SUM`, `AVG`, `MIN`, `MAX`, and `COUNT` calls into canonical temporary columns (`__agg_0`, `__agg_1`, ...).
     - Executes `.GroupBy(groupColumns).Agg(...)` (or materializes a 1-row global aggregate barrier when `GROUP BY` is absent).
     - Evaluates post-aggregate `HAVING` predicates (supporting unprojected aggregates, projection aliases, and compound `AND`/`OR` expressions).
     - Evaluates post-aggregate `ORDER BY` before projecting final user-visible columns and stripping `__agg_*` / `__sort_*` helpers.

### 2.3 Concurrency & Locking (`src/Glacier.Sql/Catalog/AsyncReaderWriterLock.cs`)
- **Fair Async-Native Reader-Writer Lock**:
  - Uses `TaskCompletionSource<bool>` queues (`RunContinuationsAsynchronously`) with zero thread-affinity or sync-over-async blocking.
  - Lock ownership is scoped per `LockReleaser` handle (`Interlocked.CompareExchange` idempotency) rather than fragile ambient boolean flags.
  - Alternates reader batches and waiting writers fairly so continuous reader traffic cannot starve writers.
  - **`UpgradeReadToWriteAsync(LockReleaser)`**: Atomically upgrades in-place when the caller is the sole reader; if concurrent readers hold shared locks, releases the caller's read slot and queues for write to prevent classic two-upgrader deadlocks.
- **Engine Lock Discipline**:
  - Every `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `INSERT ... SELECT`, `ALTER TABLE`, and `DROP TABLE` collects all referenced physical tables (recursively expanding views and `WHERE` / `SELECT` / `SET` subqueries) and acquires locks in **canonical case-insensitive alphabetical order** before reading snapshots or validating constraints.
  - `ExecutionContext` maintains re-entrant statement lock reference counts so `AFTER` and `INSTEAD OF` triggers executing nested SQL on the same context never self-deadlock.

### 2.4 Buffer Pool & Snapshot Isolation (`src/Glacier.Sql/Storage/BufferPool/`)
- Each `CatalogManager` owns an isolated `TableBufferPool` (no static cross-instance state).
- **`TableBufferEntry`** tracks:
  - `CommittedSnapshot`: Latest durable/committed `DataFrame` eligible for disk checkpointing.
  - `CurrentSnapshot`: Working `DataFrame` visible to the active transaction holding the table's exclusive write lock.
  - `LastCommittedLsn`, `FirstUnflushedLsn`, `FirstUncommittedTxLsn`, and `ActiveTransactionId`.
- **Dirty-Flush Prevention**: `CheckpointTable` and `CheckpointAll` flush **only** `CommittedSnapshot` via temp-file write + atomic `File.Move(..., overwrite: true)`. Uncommitted transaction state is never written to `.ipc` files, and `AdvanceSafeCheckpointLsn` never advances the global WAL `checkpointLsn` past any table's unflushed or uncommitted LSN.

### 2.5 Write-Ahead Log & Crash Recovery (`src/Glacier.Sql/Storage/Wal/`)
- **Binary Layout (`glacier.wal`)**:
  - **32-Byte File Header**: `Magic ("GWAL")` + `Version (1)` + `CheckpointLsn (UInt64)` + `Reserved (12B)` + `HeaderCrc32 (UInt32)`.
  - **Variable-Length Frames**: `[PayloadLength: 4B][Lsn: 8B][Type: 1B][TxId: 16B][TableNameLen: 2B][TableName: UTF-8][Payload: Arrow IPC bytes][FrameCrc32: 4B]`.
- **Deterministic Physical Payloads**:
  - `RowInsert`: Serialized Arrow IPC delta `DataFrame` of inserted row(s).
  - `RowUpdate` / `RowDelete`: Serialized Arrow IPC post-mutation table snapshot `DataFrame` (immune to SQL text formatting, `sqlText == null` AST execution, `GETDATE()`, or nested triggers).
  - `CheckpointEnd` (table-scoped): Carries the 8-byte flushed `LastCommittedLsn` (or post-`ALTER TABLE` barrier LSN) so recovery never double-applies already-flushed inserts or replays pre-`ALTER` schemas onto post-`ALTER` `.ipc` files.
- **Recovery & Monotonic LSN Continuation**:
  - On startup, `WalReader` validates both the 32-byte header CRC32 and each frame's CRC32, halting cleanly at any torn EOF write.
  - `CatalogManager.RecoverFromWal` replays committed mutations (`TxId == Guid.Empty` or `TxId` with a matching `TxCommit` frame) whose `Lsn > checkpointLsn` and `Lsn > flushedTableLsns[table]`, then checkpoints recovered state.
  - `WalWriter` initializes `_currentLsn = Math.Max(_checkpointLsn, maxValidFrameLsn)` and truncates any trailing torn frame bytes so subsequent post-recovery writes strictly increase LSNs across arbitrary crash cycles.

### 2.6 Native AOT & MCP Host (`src/Glacier.Sql.Host/`)
- Both `CatalogManager` (`CatalogJsonContext`) and `SqlMcpServer` (`SqlMcpJsonContext`) use `System.Text.Json` source generation (`JsonSerializerContext`), enabling trim-safe, reflection-free Native AOT compilation (`<IsAotCompatible>true</IsAotCompatible>`).
- `SqlMcpServer` exposes `execute_sql`, `list_tables` (reading live row counts directly from `CatalogManager.BufferPool`), and `get_table_schema` over JSON-RPC 2.0 stdio.
