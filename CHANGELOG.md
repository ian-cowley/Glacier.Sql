# Changelog

All notable changes to `Glacier.Sql` are documented here.

## v1.1.1

- Fixed SQL execution and expression binding edge cases in `QueryPlanner` and `SqlEngine`.
- Updated `Glacier.Polaris` dependency to `1.2.6`.

## v1.1.0

- Upgraded WAL persistence to `glacier.wal` v2 keyed by `Guid TableId` with strict 32-byte IEEE 802.3 CRC32 header validation and fail-closed startup recovery.
- Added transactional DDL tracking (`CREATE`/`DROP`/`ALTER TABLE`, views, triggers) with LIFO rollback undo callbacks.
- Added `AFTER` and `INSTEAD OF` cascading triggers with implicit transaction wrapping.
- Added `maxResults` / `offset` pagination to `SqlMcpServer` (`execute_sql`) and opt-in `GLACIER_SQL_MCP_LOG` diagnostics.
