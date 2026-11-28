# Glacier.Sql MCP Server (`Glacier.Sql.Host`)

`Glacier.Sql.Host` exposes an embedded `Glacier.Sql` database over JSON-RPC stdio using the Model Context Protocol (`2024-11-05`) and AOT-compatible source-generated JSON serialization (`SqlMcpJsonContext`).

---

## Build or Publish the Server Binary

```bash
dotnet publish src/Glacier.Sql.Host/Glacier.Sql.Host.csproj -c Release -o ./publish/glacier-sql
```

You can also run directly via `dotnet run --project src/Glacier.Sql.Host/Glacier.Sql.Host.csproj -c Release -- --data-dir <path>`.

---

## Data Directory & Logging Resolution

`Glacier.Sql.Host` resolves the database directory in this priority order:

1. CLI `--data-dir <path>` or `--data-dir=<path>`
2. First positional non-flag CLI argument
3. `GLACIER_SQL_DATA_DIR` environment variable
4. `%LOCALAPPDATA%/Glacier/Sql/Data` (or temp fallback if unset)

Diagnostic logging is disabled by default. To enable appending JSON-RPC trace logs to `<dataDir>/mcp_sql_log.txt`, set `GLACIER_SQL_MCP_LOG=1` (or `true`), or pass an explicit log file path.

On clean EOF / exit, `Glacier.Sql.Host` calls `catalog.Checkpoint()` to flush pending snapshots to `.ipc` files.

---

## MCP Client Configuration Example

```json
{
  "mcpServers": {
    "glacier-sql": {
      "command": "/path/to/publish/glacier-sql/Glacier.Sql.Host",
      "args": ["--data-dir", "/path/to/my_glacier_db"],
      "env": {
        "GLACIER_SQL_MCP_LOG": "0"
      }
    }
  }
}
```

---

## Exposed Tools

### 1. `execute_sql`

Executes any supported T-SQL statement or query against the active database and returns Markdown tables or affected-row confirmations.

- **Arguments:**
  - `sql` (`string`, required) — SQL statement or query text to execute.
  - `maxResults` (`integer`, optional, default `200`, clamped to `1..1000`) — Maximum rows returned in the Markdown table.
  - `offset` (`integer`, optional, default `0`) — Zero-based row offset for paginating large result sets.

### 2. `list_tables`

Lists every registered table with column definitions, backing `.ipc` filename, and current in-memory row count. Takes no arguments.

### 3. `get_table_schema`

Returns the column names and `Glacier.Sql` data types for a single table.

- **Arguments:**
  - `tableName` (`string`, required) — Target table name (case-insensitive).
