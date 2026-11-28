using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Sql.Catalog;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Engine;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.Wal;
using Xunit;
using ExecutionContext = Glacier.Sql.Engine.ExecutionContext;

namespace Glacier.Sql.Tests;

[Collection("GlacierSqlDiagnostics")]
public class HardeningVerificationTests : IDisposable
{
    private readonly string _testDir;

    public HardeningVerificationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"glacier_sql_hardening_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    // ==================== GROUP 1: Trigger & DDL Concurrency / Correctness ====================

    [Fact]
    public async Task InsteadOfInsert_AcquiresExclusiveLock_AndRefreshesSchemaAfterAlter()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var txCtx = new ExecutionContext(catalog);
        var insertCtx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE base_tbl (id INT, val VARCHAR)", txCtx);
        await engine.ExecuteAsync("CREATE TABLE shadow_tbl (id INT, val VARCHAR, tag INT)", txCtx);
        await engine.ExecuteAsync(
            "CREATE TRIGGER tr_ins ON base_tbl INSTEAD OF INSERT AS INSERT INTO shadow_tbl SELECT id, val, 1 FROM inserted",
            txCtx);

        // Hold exclusive write lock on base_tbl via uncommitted ALTER TABLE
        await engine.ExecuteAsync("BEGIN TRANSACTION", txCtx);
        var alterRes = await engine.ExecuteAsync("ALTER TABLE base_tbl ADD extra INT", txCtx);
        Assert.True(alterRes.Success, alterRes.Message);

        var tblLock = catalog.GetTableLock("base_tbl");
        Assert.True(tblLock.IsWriteLocked);

        // Concurrent INSTEAD OF INSERT must wait on exclusive lock before executing trigger
        var insertTask = Task.Run(async () =>
            await engine.ExecuteAsync("INSERT INTO base_tbl VALUES (7, 'ok', 42)", insertCtx));

        await Task.Delay(60);
        Assert.False(insertTask.IsCompleted);

        await engine.ExecuteAsync("COMMIT", txCtx);

        var insertRes = await insertTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(insertRes.Success, insertRes.Message);

        var shadowCheck = await engine.ExecuteAsync("SELECT id, val, tag FROM shadow_tbl", txCtx);
        Assert.True(shadowCheck.Success, shadowCheck.Message);
        Assert.Equal(1, shadowCheck.DataFrame!.RowCount);
        Assert.Equal(7, shadowCheck.DataFrame.GetColumn("id").Get(0));
    }

    [Fact]
    public async Task InsteadOfUpdate_RunsBeforeConstraintValidation()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE items (id INT PRIMARY KEY, qty INT NOT NULL CHECK (qty >= 0))", ctx);
        await engine.ExecuteAsync("CREATE TABLE rejected_updates (id INT, attempted_qty INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO items VALUES (1, 10)", ctx);
        await engine.ExecuteAsync(
            "CREATE TRIGGER tr_upd ON items INSTEAD OF UPDATE AS INSERT INTO rejected_updates SELECT id, qty FROM inserted",
            ctx);

        // Attempting UPDATE that would violate CHECK (qty >= 0) and NOT NULL must reach INSTEAD OF trigger cleanly
        var updRes = await engine.ExecuteAsync("UPDATE items SET qty = -5 WHERE id = 1", ctx);
        Assert.True(updRes.Success, updRes.Message);

        var baseCheck = await engine.ExecuteAsync("SELECT qty FROM items WHERE id = 1", ctx);
        Assert.Equal(10, baseCheck.DataFrame!.GetColumn("qty").Get(0));

        var rejCheck = await engine.ExecuteAsync("SELECT id, attempted_qty FROM rejected_updates", ctx);
        Assert.Equal(1, rejCheck.DataFrame!.RowCount);
        Assert.Equal(-5, rejCheck.DataFrame.GetColumn("attempted_qty").Get(0));
    }

    [Fact]
    public async Task AutoCommitDml_WhenAfterTriggerFails_RollsBackBaseAndNestedWritesAndWal()
    {
        string dir = Path.Combine(_testDir, "autocommit_trig_rb");
        using (var catalog = new CatalogManager(dir))
        {
            var engine = new SqlEngine(catalog);
            var ctx = new ExecutionContext(catalog);

            await engine.ExecuteAsync("CREATE TABLE orders (id INT PRIMARY KEY, qty INT)", ctx);
            await engine.ExecuteAsync("CREATE TABLE audit (id INT, qty INT CHECK (qty > 0))", ctx);
            await engine.ExecuteAsync("INSERT INTO orders VALUES (1, 10)", ctx);

            // First AFTER trigger logs into audit; in-memory trigger throws afterwards (simulating failure)
            await engine.ExecuteAsync(
                "CREATE TRIGGER tr_audit ON orders AFTER INSERT AS INSERT INTO audit SELECT id, qty FROM inserted",
                ctx);
            ctx.Triggers.Add(new SqlTrigger
            {
                Name = "FailInsert",
                TableName = "orders",
                EventType = "INSERT",
                TriggerAction = (_, _) => throw new InvalidOperationException("Simulated AFTER trigger failure")
            });

            var insRes = await engine.ExecuteAsync("INSERT INTO orders VALUES (2, 20)", ctx);
            Assert.False(insRes.Success);
            Assert.Contains("Simulated AFTER trigger failure", insRes.Message);

            // Verify both base table and audit table rolled back in memory
            var ordersCheck = await engine.ExecuteAsync("SELECT * FROM orders", ctx);
            Assert.Equal(1, ordersCheck.DataFrame!.RowCount);
            var auditCheck = await engine.ExecuteAsync("SELECT * FROM audit", ctx);
            Assert.Equal(0, auditCheck.DataFrame!.RowCount);

            // Test UPDATE rollback on AFTER trigger failure (persistent trigger violates audit CHECK)
            await engine.ExecuteAsync(
                "CREATE TRIGGER tr_upd_fail ON orders AFTER UPDATE AS INSERT INTO audit SELECT id, qty FROM inserted",
                ctx);
            var updRes = await engine.ExecuteAsync("UPDATE orders SET qty = -99 WHERE id = 1", ctx);
            Assert.False(updRes.Success);

            var ordersAfterUpd = await engine.ExecuteAsync("SELECT qty FROM orders WHERE id = 1", ctx);
            Assert.Equal(10, ordersAfterUpd.DataFrame!.GetColumn("qty").Get(0));

            // Test DELETE rollback on AFTER trigger failure
            ctx.Triggers.Add(new SqlTrigger
            {
                Name = "FailDelete",
                TableName = "orders",
                EventType = "DELETE",
                TriggerAction = (_, _) => throw new InvalidOperationException("Simulated DELETE trigger failure")
            });
            var delRes = await engine.ExecuteAsync("DELETE FROM orders WHERE id = 1", ctx);
            Assert.False(delRes.Success);

            var ordersAfterDel = await engine.ExecuteAsync("SELECT * FROM orders", ctx);
            Assert.Equal(1, ordersAfterDel.DataFrame!.RowCount);
        }

        // Reopen fresh CatalogManager from disk/WAL and confirm aborted auto-commit mutations do NOT replay
        using (var reopened = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(reopened);
            var ctx2 = new ExecutionContext(reopened);
            var ordersReopened = await engine2.ExecuteAsync("SELECT id, qty FROM orders", ctx2);
            Assert.Equal(1, ordersReopened.DataFrame!.RowCount);
            Assert.Equal(1, ordersReopened.DataFrame.GetColumn("id").Get(0));
            Assert.Equal(10, ordersReopened.DataFrame.GetColumn("qty").Get(0));

            var auditReopened = await engine2.ExecuteAsync("SELECT * FROM audit", ctx2);
            Assert.Equal(0, auditReopened.DataFrame!.RowCount);
        }
    }

    [Fact]
    public async Task TransactionalDdl_CreateDropAlterViewTrigger_CommitAndLifoRollback()
    {
        string dir = Path.Combine(_testDir, "tx_ddl");
        using (var catalog = new CatalogManager(dir))
        {
            var engine = new SqlEngine(catalog);
            var ctx = new ExecutionContext(catalog);

            await engine.ExecuteAsync("CREATE TABLE base_t (id INT)", ctx);
            await engine.ExecuteAsync("INSERT INTO base_t VALUES (1)", ctx);
            await engine.ExecuteAsync("CREATE TABLE aud_t (id INT)", ctx);
            await engine.ExecuteAsync("CREATE TRIGGER tr_keep ON base_t AFTER INSERT AS INSERT INTO aud_t SELECT id FROM inserted", ctx);
            await engine.ExecuteAsync("CREATE VIEW v_keep AS SELECT id FROM base_t", ctx);
            catalog.Checkpoint();

            // 1. Rollback CREATE TABLE, DROP TABLE, ALTER TABLE, CREATE/DROP VIEW, CREATE/DROP TRIGGER
            await engine.ExecuteAsync("BEGIN TRANSACTION", ctx);
            Assert.True((await engine.ExecuteAsync("CREATE TABLE temp_t (x INT)", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("INSERT INTO temp_t VALUES (42)", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("ALTER TABLE base_t ADD tag VARCHAR", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("INSERT INTO base_t VALUES (2, 'b')", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("DROP VIEW v_keep", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("CREATE VIEW v_new AS SELECT id FROM aud_t", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("DROP TRIGGER tr_keep", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("CREATE TRIGGER tr_new ON aud_t AFTER INSERT AS INSERT INTO temp_t VALUES (9)", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("ROLLBACK", ctx)).Success);

            Assert.False(catalog.TableExists("temp_t"));
            Assert.False(File.Exists(Path.Combine(dir, "temp_t.ipc")));
            Assert.True(catalog.ViewExists("v_keep"));
            Assert.False(catalog.ViewExists("v_new"));
            Assert.True(catalog.TriggerExists("tr_keep"));
            Assert.False(catalog.TriggerExists("tr_new"));

            var baseAfterRb = await engine.ExecuteAsync("SELECT * FROM base_t", ctx);
            Assert.Equal(1, baseAfterRb.DataFrame!.RowCount);
            Assert.Single(baseAfterRb.DataFrame.Columns);

            // Verify disk .ipc and catalog.json were NOT polluted by uncommitted ALTER TABLE
            var diskMeta = catalog.GetTable("base_t")!;
            var diskDf = TableStorage.ReadFromFile(diskMeta.BackingFile);
            Assert.Single(diskDf.Columns);
            Assert.Equal(1, diskDf.RowCount);

            // 2. Rollback DROP TABLE preserves table & triggers
            await engine.ExecuteAsync("BEGIN TRANSACTION", ctx);
            Assert.True((await engine.ExecuteAsync("DROP TABLE base_t", ctx)).Success);
            Assert.False(catalog.TableExists("base_t"));
            Assert.True((await engine.ExecuteAsync("ROLLBACK", ctx)).Success);
            Assert.True(catalog.TableExists("base_t"));
            Assert.True(catalog.TriggerExists("tr_keep"));
            Assert.Equal(1, (await engine.ExecuteAsync("SELECT * FROM base_t", ctx)).DataFrame!.RowCount);

            // 3. Committed transactional ALTER TABLE + INSERT + DROP TRIGGER persists across reopen
            await engine.ExecuteAsync("BEGIN TRANSACTION", ctx);
            Assert.True((await engine.ExecuteAsync("ALTER TABLE base_t ADD note VARCHAR", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("INSERT INTO base_t VALUES (2, 'committed')", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("DROP TRIGGER tr_keep", ctx)).Success);
            Assert.True((await engine.ExecuteAsync("COMMIT", ctx)).Success);
            Assert.False(catalog.TriggerExists("tr_keep"));
        }

        using (var reopened = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(reopened);
            var ctx2 = new ExecutionContext(reopened);
            Assert.False(reopened.TriggerExists("tr_keep"));
            var res = await engine2.ExecuteAsync("SELECT id, note FROM base_t ORDER BY id ASC", ctx2);
            Assert.True(res.Success, res.Message);
            Assert.Equal(2, res.DataFrame!.RowCount);
            Assert.Equal("committed", res.DataFrame.GetColumn("note").Get(1));
        }
    }

    // ==================== GROUP 2: Error Handling, Durability & Security Hygiene ====================

    [Fact]
    public void WalWriter_CorruptOrTruncatedHeader_ThrowsInvalidDataWithoutOverwritingFile()
    {
        // Case A: Corrupt existing header CRC -> throws InvalidDataException and preserves bytes
        string walCorrupt = Path.Combine(_testDir, "corrupt_hdr.wal");
        using (var writer = new WalWriter(walCorrupt))
        {
            writer.AppendRecord(WalRecordType.RowInsert, Guid.Empty, Guid.NewGuid(), "t", new byte[] { 1, 2, 3, 4 });
        }

        byte[] originalBytes = File.ReadAllBytes(walCorrupt);
        byte[] tamperedBytes = (byte[])originalBytes.Clone();
        tamperedBytes[0] ^= 0xFF; // corrupt magic
        File.WriteAllBytes(walCorrupt, tamperedBytes);

        Assert.Throws<InvalidDataException>(() => new WalWriter(walCorrupt));
        Assert.Equal(tamperedBytes, File.ReadAllBytes(walCorrupt));

        // Case B: Non-empty truncated header (< 32 bytes) -> throws InvalidDataException without truncating/padding
        string walShort = Path.Combine(_testDir, "short_hdr.wal");
        byte[] shortData = new byte[] { 0x31, 0x4C, 0x41, 0x57, 1, 2, 3 };
        File.WriteAllBytes(walShort, shortData);

        Assert.Throws<InvalidDataException>(() => new WalWriter(walShort));
        Assert.Equal(shortData, File.ReadAllBytes(walShort));
    }

    [Fact]
    public void CatalogManager_CorruptWalHeader_ThrowsInvalidDataException()
    {
        string dir = Path.Combine(_testDir, "corrupt_wal_load");
        using (var cm = new CatalogManager(dir))
        {
            cm.AddTable("t1", new List<ColumnMetadata> { new() { Name = "id", DataType = "INT" } });
            cm.WalWriter.AppendRecord(WalRecordType.RowInsert, Guid.Empty, cm.GetTable("t1")!.TableId, "t1", new byte[] { 1 });
        }

        string walPath = Path.Combine(dir, "glacier.wal");
        byte[] bytes = File.ReadAllBytes(walPath);
        bytes[10] ^= 0xAA;
        File.WriteAllBytes(walPath, bytes);

        Assert.Throws<InvalidDataException>(() => new CatalogManager(dir));
    }

    [Fact]
    public async Task DropAndRecreateSameTableName_WithUncheckpointedWal_DoesNotReplayDeadRecords()
    {
        string dir = Path.Combine(_testDir, "drop_recreate");
        using (var catalog1 = new CatalogManager(dir))
        {
            var engine1 = new SqlEngine(catalog1);
            var ctx1 = new ExecutionContext(catalog1);

            await engine1.ExecuteAsync("CREATE TABLE reuse_t (id INT, num INT)", ctx1);
            await engine1.ExecuteAsync("INSERT INTO reuse_t VALUES (1, 100)", ctx1);
            // Drop without checkpointing first, then recreate with completely different schema
            await engine1.ExecuteAsync("DROP TABLE reuse_t", ctx1);
            await engine1.ExecuteAsync("CREATE TABLE reuse_t (code VARCHAR)", ctx1);
            await engine1.ExecuteAsync("INSERT INTO reuse_t VALUES ('fresh')", ctx1);
            // Crash without checkpoint
        }

        using (var catalog2 = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(catalog2);
            var ctx2 = new ExecutionContext(catalog2);

            var res = await engine2.ExecuteAsync("SELECT * FROM reuse_t", ctx2);
            Assert.True(res.Success, res.Message);
            Assert.Equal(1, res.DataFrame!.RowCount);
            Assert.Single(res.DataFrame.Columns);
            Assert.Equal("fresh", res.DataFrame.GetColumn("code").Get(0));
        }
    }

    [Fact]
    public async Task SqlEngine_Errors_DoNotLeakRawStackTraces()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        var logged = new System.Collections.Concurrent.ConcurrentBag<(LogLevel Level, string Message, Exception? Ex)>();
        GlacierSqlDiagnostics.SetLogger(new DelegateGlacierLogger((lvl, msg, ex) => logged.Add((lvl, msg, ex)), LogLevel.Trace));
        try
        {
            var res = await engine.ExecuteAsync("SELECT * FROM nonexistent_table_abc", ctx);
            Assert.False(res.Success);
            Assert.StartsWith("Execution Error:", res.Message);
            Assert.DoesNotContain("   at ", res.Message);
            Assert.DoesNotContain(".cs:line", res.Message);
            Assert.Contains(logged, e => e.Level == LogLevel.Error && e.Ex != null);
        }
        finally
        {
            GlacierSqlDiagnostics.Reset();
        }
    }

    // ==================== GROUP 3: Parser & Lexer (Opt #6) ====================

    [Fact]
    public void Parser_EnforcesEofRejectsTrailingAndInvalidTokens_AndSupportsSemicolon()
    {
        // Valid trailing semicolon
        var validStmt = new TSqlParser(new SqlLexer("SELECT 1 AS a;").Tokenize()).Parse();
        Assert.IsType<SelectStatement>(validStmt);

        // Trailing garbage throws SqlSyntaxException
        Assert.Throws<SqlSyntaxException>(() =>
            new TSqlParser(new SqlLexer("SELECT 1 AS a GARBAGE").Tokenize()).Parse());

        // Multiple statements separated by semicolon throw SqlSyntaxException
        Assert.Throws<SqlSyntaxException>(() =>
            new TSqlParser(new SqlLexer("SELECT 1; SELECT 2;").Tokenize()).Parse());

        // Invalid token throws SqlSyntaxException
        Assert.Throws<SqlSyntaxException>(() =>
            new TSqlParser(new SqlLexer("SELECT @bad").Tokenize()).Parse());
    }

    [Fact]
    public async Task MultiRowInsert_InsertsAllRowsAtomically_AndRollsBackOnConstraintFailure()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE batch_t (id INT PRIMARY KEY, name VARCHAR, qty INT CHECK (qty > 0))", ctx);

        var okRes = await engine.ExecuteAsync(
            "INSERT INTO batch_t VALUES (1, 'A', 10), (2, 'B''s', 20), (3, 'C', 30);",
            ctx);
        Assert.True(okRes.Success, okRes.Message);
        Assert.Equal(3, okRes.AffectedRows);

        var checkOk = await engine.ExecuteAsync("SELECT id, name, qty FROM batch_t ORDER BY id ASC", ctx);
        Assert.Equal(3, checkOk.DataFrame!.RowCount);
        Assert.Equal("B's", checkOk.DataFrame.GetColumn("name").Get(1));

        // Second row in multi-row batch violates CHECK -> none of the batch rows should be inserted
        var badRes = await engine.ExecuteAsync(
            "INSERT INTO batch_t VALUES (4, 'D', 40), (5, 'E', -1)",
            ctx);
        Assert.False(badRes.Success);
        Assert.Equal(3, (await engine.ExecuteAsync("SELECT * FROM batch_t", ctx)).DataFrame!.RowCount);
    }

    [Fact]
    public async Task NotIn_ListAndSubquery_HandlesSqlThreeValuedNullLogic()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE nums (id INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO nums VALUES (1), (2), (3), (NULL)", ctx);

        await engine.ExecuteAsync("CREATE TABLE ex_no_null (id INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO ex_no_null VALUES (1), (2)", ctx);

        await engine.ExecuteAsync("CREATE TABLE ex_with_null (id INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO ex_with_null VALUES (1), (NULL)", ctx);

        await engine.ExecuteAsync("CREATE TABLE ex_empty (id INT)", ctx);

        // NOT IN literal list without NULL excludes matches and NULLs
        var r1 = await engine.ExecuteAsync("SELECT id FROM nums WHERE id NOT IN (1, 2)", ctx);
        Assert.True(r1.Success, r1.Message);
        Assert.Equal(1, r1.DataFrame!.RowCount);
        Assert.Equal(3, r1.DataFrame.GetColumn("id").Get(0));

        // NOT IN literal list containing NULL returns 0 rows
        var r2 = await engine.ExecuteAsync("SELECT id FROM nums WHERE id NOT IN (1, NULL)", ctx);
        Assert.True(r2.Success, r2.Message);
        Assert.Equal(0, r2.DataFrame!.RowCount);

        // NOT IN subquery without NULL
        var r3 = await engine.ExecuteAsync("SELECT id FROM nums WHERE id NOT IN (SELECT id FROM ex_no_null)", ctx);
        Assert.True(r3.Success, r3.Message);
        Assert.Equal(1, r3.DataFrame!.RowCount);
        Assert.Equal(3, r3.DataFrame.GetColumn("id").Get(0));

        // NOT IN subquery containing NULL returns 0 rows
        var r4 = await engine.ExecuteAsync("SELECT id FROM nums WHERE id NOT IN (SELECT id FROM ex_with_null)", ctx);
        Assert.True(r4.Success, r4.Message);
        Assert.Equal(0, r4.DataFrame!.RowCount);

        // NOT IN empty subquery returns all rows (including NULL)
        var r5 = await engine.ExecuteAsync("SELECT id FROM nums WHERE id NOT IN (SELECT id FROM ex_empty)", ctx);
        Assert.True(r5.Success, r5.Message);
        Assert.Equal(4, r5.DataFrame!.RowCount);
    }

    // ==================== GROUP 4: Query Planner & Execution ====================

    [Fact]
    public async Task CountStar_WithNullsInCol0_CountsAllRowsGroupedUngroupedAndEmpty()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE empty_t (a VARCHAR, b INT)", ctx);
        var emptyCount = await engine.ExecuteAsync("SELECT COUNT(*) AS cnt FROM empty_t", ctx);
        Assert.True(emptyCount.Success, emptyCount.Message);
        Assert.Equal(0L, Convert.ToInt64(emptyCount.DataFrame!.GetColumn("cnt").Get(0)));

        await engine.ExecuteAsync("CREATE TABLE null_col0 (a VARCHAR, grp INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO null_col0 VALUES (NULL, 1), ('x', 1), (NULL, 2), (NULL, 2)", ctx);

        var ungrouped = await engine.ExecuteAsync("SELECT COUNT(*) AS cnt FROM null_col0", ctx);
        Assert.True(ungrouped.Success, ungrouped.Message);
        Assert.Equal(4L, Convert.ToInt64(ungrouped.DataFrame!.GetColumn("cnt").Get(0)));

        var grouped = await engine.ExecuteAsync("SELECT grp, COUNT(*) AS cnt FROM null_col0 GROUP BY grp ORDER BY grp ASC", ctx);
        Assert.True(grouped.Success, grouped.Message);
        Assert.Equal(2, grouped.DataFrame!.RowCount);
        Assert.Equal(2L, Convert.ToInt64(grouped.DataFrame.GetColumn("cnt").Get(0)));
        Assert.Equal(2L, Convert.ToInt64(grouped.DataFrame.GetColumn("cnt").Get(1)));
    }

    [Fact]
    public async Task Exists_MultiKeyAndStringCorrelations_AndOrExists_EvaluateCorrectly()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE orders_mk (k1 INT, k2 VARCHAR, label VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO orders_mk VALUES (1, 'US', 'match_both'), (1, 'EU', 'wrong_k2'), (2, 'CA', 'match_alt'), (NULL, 'US', 'null_k1')", ctx);

        await engine.ExecuteAsync("CREATE TABLE targets_a (k1 INT, k2 VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO targets_a VALUES (1, 'US'), (NULL, 'US')", ctx);

        await engine.ExecuteAsync("CREATE TABLE targets_b (k2 VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO targets_b VALUES ('CA')", ctx);

        // Multi-key correlation (both k1 and k2 must match; NULL != NULL)
        var multiRes = await engine.ExecuteAsync(
            "SELECT o.label FROM orders_mk o WHERE EXISTS (SELECT 1 FROM targets_a a WHERE a.k1 = o.k1 AND a.k2 = o.k2)",
            ctx);
        Assert.True(multiRes.Success, multiRes.Message);
        Assert.Equal(1, multiRes.DataFrame!.RowCount);
        Assert.Equal("match_both", multiRes.DataFrame.GetColumn("label").Get(0));

        // OR EXISTS combining two different EXISTS predicates
        var orRes = await engine.ExecuteAsync(
            "SELECT o.label FROM orders_mk o WHERE EXISTS (SELECT 1 FROM targets_a a WHERE a.k1 = o.k1 AND a.k2 = o.k2) OR EXISTS (SELECT 1 FROM targets_b b WHERE b.k2 = o.k2) ORDER BY o.label ASC",
            ctx);
        Assert.True(orRes.Success, orRes.Message);
        Assert.Equal(2, orRes.DataFrame!.RowCount);
        Assert.Equal("match_alt", orRes.DataFrame.GetColumn("label").Get(0));
        Assert.Equal("match_both", orRes.DataFrame.GetColumn("label").Get(1));
    }

    [Fact]
    public async Task Joins_RightOuter_FullOuter_LeftOuter_AndSwappedOn_ExecuteAccurately()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE users_j (uid INT, dept_id INT, name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO users_j VALUES (1, 10, 'Alice'), (2, 99, 'Orphan')", ctx);

        await engine.ExecuteAsync("CREATE TABLE depts_j (did INT, title VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO depts_j VALUES (10, 'Core'), (20, 'Search')", ctx);

        // Swapped ON (right = left) + LEFT OUTER JOIN
        var leftRes = await engine.ExecuteAsync(
            "SELECT u.uid, u.name, d.title FROM users_j u LEFT OUTER JOIN depts_j d ON d.did = u.dept_id ORDER BY u.uid ASC",
            ctx);
        Assert.True(leftRes.Success, leftRes.Message);
        Assert.Equal(2, leftRes.DataFrame!.RowCount);
        Assert.Equal("Core", leftRes.DataFrame.GetColumn("title").Get(0));
        Assert.Null(leftRes.DataFrame.GetColumn("title").Get(1));

        // RIGHT OUTER JOIN with swapped ON
        var rightRes = await engine.ExecuteAsync(
            "SELECT u.name, d.title FROM users_j u RIGHT OUTER JOIN depts_j d ON d.did = u.dept_id ORDER BY d.title ASC",
            ctx);
        Assert.True(rightRes.Success, rightRes.Message);
        Assert.Equal(2, rightRes.DataFrame!.RowCount);
        Assert.Equal("Alice", rightRes.DataFrame.GetColumn("name").Get(0));
        Assert.Equal("Core", rightRes.DataFrame.GetColumn("title").Get(0));
        Assert.Null(rightRes.DataFrame.GetColumn("name").Get(1));
        Assert.Equal("Search", rightRes.DataFrame.GetColumn("title").Get(1));

        // FULL OUTER JOIN
        var fullRes = await engine.ExecuteAsync(
            "SELECT u.name, d.title FROM users_j u FULL OUTER JOIN depts_j d ON u.dept_id = d.did",
            ctx);
        Assert.True(fullRes.Success, fullRes.Message);
        Assert.Equal(3, fullRes.DataFrame!.RowCount);
    }

    [Fact]
    public async Task NonAggregate_HavingAndOrderByExpressions_ExecuteCleanly()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE line_items (id INT, qty INT, price DOUBLE)", ctx);
        await engine.ExecuteAsync("INSERT INTO line_items VALUES (1, 2, 10.0), (2, 5, 20.0), (3, 3, 25.0)", ctx);

        // HAVING on alias + unprojected column, ORDER BY computed expression
        var res = await engine.ExecuteAsync(
            "SELECT id, qty * price AS total FROM line_items HAVING total >= 50.0 ORDER BY qty * price DESC",
            ctx);
        Assert.True(res.Success, res.Message);
        Assert.Equal(2, res.DataFrame!.RowCount);
        Assert.Equal(2, res.DataFrame.GetColumn("id").Get(0)); // 100.0
        Assert.Equal(3, res.DataFrame.GetColumn("id").Get(1)); // 75.0
    }

    // ==================== GROUP 5: DML/WAL Performance & Hardware CRC (Opt #4) ====================

    [Fact]
    public void Crc32_MatchesIeee8023Vector_AndIncrementalUpdate()
    {
        byte[] standardVec = Encoding.ASCII.GetBytes("123456789");
        Assert.Equal(0xCBF43926u, Crc32.Compute(standardVec));

        uint part1 = Crc32.Update(0u, standardVec.AsSpan(0, 4));
        uint full = Crc32.Update(part1, standardVec.AsSpan(4));
        Assert.Equal(0xCBF43926u, full);
    }

    [Fact]
    public async Task BulkInsertDeltaWalAndCheckpointTruncation_RecoverAndShrinkWalToHeader()
    {
        string dir = Path.Combine(_testDir, "delta_wal_perf");
        using (var catalog1 = new CatalogManager(dir))
        {
            var engine1 = new SqlEngine(catalog1);
            var ctx1 = new ExecutionContext(catalog1);

            await engine1.ExecuteAsync("CREATE TABLE perf_rows (id INT PRIMARY KEY, tag VARCHAR)", ctx1);

            // Multi-row batch insert of 2,000 rows + individual inserts
            var sb = new StringBuilder("INSERT INTO perf_rows VALUES ");
            for (int i = 1; i <= 2000; i++)
            {
                if (i > 1) sb.Append(',');
                sb.Append($"({i}, 'r{i}')");
            }
            var insRes = await engine1.ExecuteAsync(sb.ToString(), ctx1);
            Assert.True(insRes.Success, insRes.Message);

            // Update 2 rows and delete 2 rows -> verify compact delta WAL frames (RowUpdateDelta / RowDeleteDelta)
            var updRes = await engine1.ExecuteAsync("UPDATE perf_rows SET tag = 'changed' WHERE id <= 2", ctx1);
            Assert.True(updRes.Success, updRes.Message);
            var delRes = await engine1.ExecuteAsync("DELETE FROM perf_rows WHERE id >= 1999", ctx1);
            Assert.True(delRes.Success, delRes.Message);

            string walPath = Path.Combine(dir, "glacier.wal");
            var walRecords = WalReader.Read(walPath, out _);
            Assert.Contains(walRecords, r => r.Type == WalRecordType.RowUpdateDelta);
            Assert.Contains(walRecords, r => r.Type == WalRecordType.RowDeleteDelta);

            // Checkpoint must flush and truncate glacier.wal back to 32-byte header
            catalog1.Checkpoint();
            Assert.Equal(32L, new FileInfo(walPath).Length);

            // Now perform uncheckpointed delta update + delete before crash
            await engine1.ExecuteAsync("UPDATE perf_rows SET tag = 'unck_upd' WHERE id = 10", ctx1);
            await engine1.ExecuteAsync("DELETE FROM perf_rows WHERE id = 11", ctx1);
        }

        // Crash recovery replays RowUpdateDelta and RowDeleteDelta cleanly
        using (var catalog2 = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(catalog2);
            var ctx2 = new ExecutionContext(catalog2);

            var cnt = await engine2.ExecuteAsync("SELECT COUNT(*) AS cnt FROM perf_rows", ctx2);
            Assert.Equal(1997L, Convert.ToInt64(cnt.DataFrame!.GetColumn("cnt").Get(0)));

            var r10 = await engine2.ExecuteAsync("SELECT tag FROM perf_rows WHERE id = 10", ctx2);
            Assert.Equal("unck_upd", r10.DataFrame!.GetColumn("tag").Get(0));

            var r11 = await engine2.ExecuteAsync("SELECT id FROM perf_rows WHERE id = 11", ctx2);
            Assert.Equal(0, r11.DataFrame!.RowCount);
        }
    }

    [Fact]
    public async Task TypedUniqueConstraints_EnforceAcrossAllTypesAndLongStrings()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync(
            "CREATE TABLE uniq_all (i INT UNIQUE, f FLOAT UNIQUE, s VARCHAR UNIQUE, b BIT UNIQUE, d DATETIME UNIQUE)",
            ctx);

        string longStrA = new string('a', 400);
        string longStrB = new string('b', 400);

        Assert.True((await engine.ExecuteAsync(
            $"INSERT INTO uniq_all VALUES (1, 1.5, '{longStrA}', 1, '2026-01-01')", ctx)).Success);
        Assert.True((await engine.ExecuteAsync(
            $"INSERT INTO uniq_all VALUES (2, 2.5, '{longStrB}', 0, '2026-01-02')", ctx)).Success);

        // Duplicate long UTF-8 string (>256 chars exercises ArrayPool branch)
        var dupStr = await engine.ExecuteAsync(
            $"INSERT INTO uniq_all VALUES (3, 3.5, '{longStrA}', NULL, '2026-01-03')", ctx);
        Assert.False(dupStr.Success);
        Assert.Contains("UNIQUE", dupStr.Message);

        // Duplicate BIT
        var dupBit = await engine.ExecuteAsync(
            "INSERT INTO uniq_all VALUES (4, 4.5, 'unique_s', 1, '2026-01-04')", ctx);
        Assert.False(dupBit.Success);
        Assert.Contains("UNIQUE", dupBit.Message);
    }

    // ==================== ITERATION 2: Join Key Preservation, Multi-Hop Disambiguation, IN+NULL, Correlated OR EXISTS ====================

    [Fact]
    public async Task Joins_LeftRightInnerFull_PreserveBothJoinKeysWithDifferentAndSameNames()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        // Different key names: Orders2.UserId = Users2.Id
        await engine.ExecuteAsync("CREATE TABLE Orders2 (OrderId INT, UserId INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO Orders2 VALUES (101, 1), (102, 99)", ctx);

        await engine.ExecuteAsync("CREATE TABLE Users2 (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO Users2 VALUES (1, 'Alice'), (2, 'Bob')", ctx);

        // LEFT OUTER JOIN: unmatched row yields o.UserId = 99 and u.Id = NULL
        var leftDiff = await engine.ExecuteAsync(
            "SELECT o.OrderId, o.UserId, u.Id FROM Orders2 o LEFT OUTER JOIN Users2 u ON o.UserId = u.Id ORDER BY o.OrderId ASC",
            ctx);
        Assert.True(leftDiff.Success, leftDiff.Message);
        Assert.Equal(2, leftDiff.DataFrame!.RowCount);
        Assert.Equal(1, leftDiff.DataFrame.GetColumn("UserId").Get(0));
        Assert.Equal(1, leftDiff.DataFrame.GetColumn("Id").Get(0));
        Assert.Equal(99, leftDiff.DataFrame.GetColumn("UserId").Get(1));
        Assert.False(leftDiff.DataFrame.GetColumn("Id").ValidityMask.IsValid(1));

        // RIGHT OUTER JOIN: unmatched row yields o.UserId = NULL and u.Id = 2
        var rightDiff = await engine.ExecuteAsync(
            "SELECT o.OrderId, o.UserId, u.Id FROM Orders2 o RIGHT OUTER JOIN Users2 u ON o.UserId = u.Id ORDER BY u.Id ASC",
            ctx);
        Assert.True(rightDiff.Success, rightDiff.Message);
        Assert.Equal(2, rightDiff.DataFrame!.RowCount);
        Assert.Equal(1, rightDiff.DataFrame.GetColumn("UserId").Get(0));
        Assert.Equal(1, rightDiff.DataFrame.GetColumn("Id").Get(0));
        Assert.False(rightDiff.DataFrame.GetColumn("UserId").ValidityMask.IsValid(1));
        Assert.Equal(2, rightDiff.DataFrame.GetColumn("Id").Get(1));

        // INNER JOIN: both o.UserId and u.Id resolve cleanly
        var innerDiff = await engine.ExecuteAsync(
            "SELECT o.OrderId, o.UserId, u.Id FROM Orders2 o INNER JOIN Users2 u ON o.UserId = u.Id",
            ctx);
        Assert.True(innerDiff.Success, innerDiff.Message);
        Assert.Equal(1, innerDiff.DataFrame!.RowCount);
        Assert.Equal(1, innerDiff.DataFrame.GetColumn("UserId").Get(0));
        Assert.Equal(1, innerDiff.DataFrame.GetColumn("Id").Get(0));

        // FULL OUTER JOIN: different key names, NULL on respective unmatched sides
        var fullDiff = await engine.ExecuteAsync(
            "SELECT o.OrderId, o.UserId, u.Id FROM Orders2 o FULL OUTER JOIN Users2 u ON o.UserId = u.Id",
            ctx);
        Assert.True(fullDiff.Success, fullDiff.Message);
        Assert.Equal(3, fullDiff.DataFrame!.RowCount);

        // Same key names: T1.Id = T2.Id
        await engine.ExecuteAsync("CREATE TABLE T1k (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO T1k VALUES (1, 'A1'), (3, 'A3')", ctx);

        await engine.ExecuteAsync("CREATE TABLE T2k (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO T2k VALUES (1, 'B1'), (2, 'B2')", ctx);

        var leftSame = await engine.ExecuteAsync(
            "SELECT t1.Id AS Id1, t2.Id AS Id2, t1.Name AS N1, t2.Name AS N2 FROM T1k t1 LEFT OUTER JOIN T2k t2 ON t1.Id = t2.Id ORDER BY t1.Id ASC",
            ctx);
        Assert.True(leftSame.Success, leftSame.Message);
        Assert.Equal(2, leftSame.DataFrame!.RowCount);
        Assert.Equal(1, leftSame.DataFrame.GetColumn("Id1").Get(0));
        Assert.Equal(1, leftSame.DataFrame.GetColumn("Id2").Get(0));
        Assert.Equal(3, leftSame.DataFrame.GetColumn("Id1").Get(1));
        Assert.False(leftSame.DataFrame.GetColumn("Id2").ValidityMask.IsValid(1));

        var rightSame = await engine.ExecuteAsync(
            "SELECT t1.Id AS Id1, t2.Id AS Id2, t1.Name AS N1, t2.Name AS N2 FROM T1k t1 RIGHT OUTER JOIN T2k t2 ON t1.Id = t2.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(rightSame.Success, rightSame.Message);
        Assert.Equal(2, rightSame.DataFrame!.RowCount);
        Assert.Equal(1, rightSame.DataFrame.GetColumn("Id1").Get(0));
        Assert.Equal(1, rightSame.DataFrame.GetColumn("Id2").Get(0));
        Assert.False(rightSame.DataFrame.GetColumn("Id1").ValidityMask.IsValid(1));
        Assert.Equal(2, rightSame.DataFrame.GetColumn("Id2").Get(1));

        var fullSame = await engine.ExecuteAsync(
            "SELECT t1.Id AS Id1, t2.Id AS Id2 FROM T1k t1 FULL OUTER JOIN T2k t2 ON t1.Id = t2.Id",
            ctx);
        Assert.True(fullSame.Success, fullSame.Message);
        Assert.Equal(3, fullSame.DataFrame!.RowCount);
        int matchedRows = 0, leftOnlyRows = 0, rightOnlyRows = 0;
        var c1 = fullSame.DataFrame.GetColumn("Id1");
        var c2 = fullSame.DataFrame.GetColumn("Id2");
        for (int r = 0; r < fullSame.DataFrame.RowCount; r++)
        {
            bool v1 = c1.ValidityMask.IsValid(r);
            bool v2 = c2.ValidityMask.IsValid(r);
            if (v1 && v2) matchedRows++;
            else if (v1 && !v2) leftOnlyRows++;
            else if (!v1 && v2) rightOnlyRows++;
        }
        Assert.Equal(1, matchedRows);
        Assert.Equal(1, leftOnlyRows);
        Assert.Equal(1, rightOnlyRows);

        // Verify source TableBufferEntry snapshots never had ISeries.Name mutated
        Assert.Equal(new[] { "OrderId", "UserId" }, catalog.BufferPool.GetOrLoad(catalog.GetTable("Orders2")!).CurrentSnapshot.ColumnNames);
        Assert.Equal(new[] { "Id", "Name" }, catalog.BufferPool.GetOrLoad(catalog.GetTable("Users2")!).CurrentSnapshot.ColumnNames);
        Assert.Equal(new[] { "Id", "Name" }, catalog.BufferPool.GetOrLoad(catalog.GetTable("T1k")!).CurrentSnapshot.ColumnNames);
        Assert.Equal(new[] { "Id", "Name" }, catalog.BufferPool.GetOrLoad(catalog.GetTable("T2k")!).CurrentSnapshot.ColumnNames);
    }

    [Fact]
    public async Task Joins_ThreeWayWithSharedColumnsAndSelectStar_DisambiguatesCleanlyWithoutLeakingInternalCols()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE J1 (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO J1 VALUES (1, 'One1'), (2, 'Two1')", ctx);

        await engine.ExecuteAsync("CREATE TABLE J2 (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO J2 VALUES (1, 'One2')", ctx);

        await engine.ExecuteAsync("CREATE TABLE J3 (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO J3 VALUES (1, 'One3'), (3, 'Three3')", ctx);

        // 3-way join with shared Id and Name across all 3 tables including RIGHT OUTER JOIN
        var threeWay = await engine.ExecuteAsync(
            "SELECT j1.Name AS N1, j2.Name AS N2, j3.Name AS N3, j2.Id AS Id2, j3.Id AS Id3 " +
            "FROM J1 j1 INNER JOIN J2 j2 ON j1.Id = j2.Id RIGHT OUTER JOIN J3 j3 ON j2.Id = j3.Id ORDER BY j3.Id ASC",
            ctx);
        Assert.True(threeWay.Success, threeWay.Message);
        Assert.Equal(2, threeWay.DataFrame!.RowCount);
        Assert.Equal("One1", threeWay.DataFrame.GetColumn("N1").Get(0));
        Assert.Equal("One2", threeWay.DataFrame.GetColumn("N2").Get(0));
        Assert.Equal("One3", threeWay.DataFrame.GetColumn("N3").Get(0));
        Assert.False(threeWay.DataFrame.GetColumn("N1").ValidityMask.IsValid(1));
        Assert.False(threeWay.DataFrame.GetColumn("N2").ValidityMask.IsValid(1));
        Assert.False(threeWay.DataFrame.GetColumn("Id2").ValidityMask.IsValid(1));
        Assert.Equal(3, threeWay.DataFrame.GetColumn("Id3").Get(1));
        Assert.Equal("Three3", threeWay.DataFrame.GetColumn("N3").Get(1));

        // SELECT * on 3-way join projects all 6 columns with unique names and zero __jk_* / __rk_* leakage
        var starRes = await engine.ExecuteAsync(
            "SELECT * FROM J1 j1 INNER JOIN J2 j2 ON j1.Id = j2.Id RIGHT OUTER JOIN J3 j3 ON j2.Id = j3.Id ORDER BY j3.Id ASC",
            ctx);
        Assert.True(starRes.Success, starRes.Message);
        Assert.Equal(6, starRes.DataFrame!.Columns.Count);
        Assert.DoesNotContain(starRes.DataFrame.ColumnNames, n => n.StartsWith("__", StringComparison.Ordinal));
        Assert.Equal(6, starRes.DataFrame.ColumnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task InAndNotIn_NonLiteralExpressionsAndNull_HonorSqlThreeValuedLogic()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE expr_t (id INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO expr_t VALUES (1), (2), (3), (NULL)", ctx);

        // NOT IN mixing expression + NULL -> 0 rows
        var notInExprNull = await engine.ExecuteAsync("SELECT id FROM expr_t WHERE id NOT IN (1 + 1, NULL)", ctx);
        Assert.True(notInExprNull.Success, notInExprNull.Message);
        Assert.Equal(0, notInExprNull.DataFrame!.RowCount);

        // IN mixing expression + NULL -> matching row (2)
        var inExprNull = await engine.ExecuteAsync("SELECT id FROM expr_t WHERE id IN (1 + 1, NULL)", ctx);
        Assert.True(inExprNull.Success, inExprNull.Message);
        Assert.Equal(1, inExprNull.DataFrame!.RowCount);
        Assert.Equal(2, inExprNull.DataFrame.GetColumn("id").Get(0));

        // NULL IN (1 + 1) -> 0 rows
        var nullInExpr = await engine.ExecuteAsync("SELECT id FROM expr_t WHERE NULL IN (1 + 1)", ctx);
        Assert.True(nullInExpr.Success, nullInExpr.Message);
        Assert.Equal(0, nullInExpr.DataFrame!.RowCount);

        // NULL NOT IN (1 + 1) -> 0 rows
        var nullNotInExpr = await engine.ExecuteAsync("SELECT id FROM expr_t WHERE NULL NOT IN (1 + 1)", ctx);
        Assert.True(nullNotInExpr.Success, nullNotInExpr.Message);
        Assert.Equal(0, nullNotInExpr.DataFrame!.RowCount);

        // 1 NOT IN (1 + 1) -> true (returns matching row), and id NOT IN (1 + 1) -> rows 1, 3
        var oneNotInExpr = await engine.ExecuteAsync("SELECT id FROM expr_t WHERE id = 1 AND 1 NOT IN (1 + 1)", ctx);
        Assert.True(oneNotInExpr.Success, oneNotInExpr.Message);
        Assert.Equal(1, oneNotInExpr.DataFrame!.RowCount);
        Assert.Equal(1, oneNotInExpr.DataFrame.GetColumn("id").Get(0));

        var colNotInExpr = await engine.ExecuteAsync("SELECT id FROM expr_t WHERE id NOT IN (1 + 1) ORDER BY id ASC", ctx);
        Assert.True(colNotInExpr.Success, colNotInExpr.Message);
        Assert.Equal(2, colNotInExpr.DataFrame!.RowCount);
        Assert.Equal(1, colNotInExpr.DataFrame.GetColumn("id").Get(0));
        Assert.Equal(3, colNotInExpr.DataFrame.GetColumn("id").Get(1));
    }

    [Fact]
    public async Task Exists_WithOrInInnerWhere_AndSubqueryInOrderBy_EvaluateCorrectly()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE E1 (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO E1 VALUES (1, 'Alpha'), (2, 'Beta'), (3, 'Gamma')", ctx);

        await engine.ExecuteAsync("CREATE TABLE E2 (Id INT, Name VARCHAR, Active INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO E2 VALUES (1, 'Other', 1), (99, 'Beta', 1), (3, 'Gamma', 0)", ctx);

        // Correlated EXISTS with OR inside inner WHERE
        var orInnerRes = await engine.ExecuteAsync(
            "SELECT t1.Id FROM E1 t1 WHERE EXISTS (SELECT 1 FROM E2 t2 WHERE t2.Id = t1.Id OR t2.Name = t1.Name) ORDER BY t1.Id ASC",
            ctx);
        Assert.True(orInnerRes.Success, orInnerRes.Message);
        Assert.Equal(3, orInnerRes.DataFrame!.RowCount);
        Assert.Equal(1, orInnerRes.DataFrame.GetColumn("Id").Get(0));
        Assert.Equal(2, orInnerRes.DataFrame.GetColumn("Id").Get(1));
        Assert.Equal(3, orInnerRes.DataFrame.GetColumn("Id").Get(2));

        // Correlated EXISTS with AND + OR inside inner WHERE
        var andOrRes = await engine.ExecuteAsync(
            "SELECT t1.Id FROM E1 t1 WHERE EXISTS (SELECT 1 FROM E2 t2 WHERE t2.Active = 1 AND (t2.Id = t1.Id OR t2.Name = t1.Name)) ORDER BY t1.Id ASC",
            ctx);
        Assert.True(andOrRes.Success, andOrRes.Message);
        Assert.Equal(2, andOrRes.DataFrame!.RowCount);
        Assert.Equal(1, andOrRes.DataFrame.GetColumn("Id").Get(0));
        Assert.Equal(2, andOrRes.DataFrame.GetColumn("Id").Get(1));

        // Correlated NOT EXISTS with OR inside inner WHERE
        var notExistsOr = await engine.ExecuteAsync(
            "SELECT t1.Id FROM E1 t1 WHERE NOT EXISTS (SELECT 1 FROM E2 t2 WHERE t2.Active = 1 AND (t2.Id = t1.Id OR t2.Name = t1.Name))",
            ctx);
        Assert.True(notExistsOr.Success, notExistsOr.Message);
        Assert.Equal(1, notExistsOr.DataFrame!.RowCount);
        Assert.Equal(3, notExistsOr.DataFrame.GetColumn("Id").Get(0));

        // Subquery inside ORDER BY executes asynchronously without sync-over-async issues
        var orderSubq = await engine.ExecuteAsync(
            "SELECT Id FROM E1 ORDER BY Id + (SELECT Active FROM E2 WHERE Id = 1) DESC",
            ctx);
        Assert.True(orderSubq.Success, orderSubq.Message);
        Assert.Equal(3, orderSubq.DataFrame!.RowCount);
        Assert.Equal(3, orderSubq.DataFrame.GetColumn("Id").Get(0));

        // Ensure base schemas remain intact
        Assert.Equal(new[] { "Id", "Name" }, catalog.BufferPool.GetOrLoad(catalog.GetTable("E1")!).CurrentSnapshot.ColumnNames);
        Assert.Equal(new[] { "Id", "Name", "Active" }, catalog.BufferPool.GetOrLoad(catalog.GetTable("E2")!).CurrentSnapshot.ColumnNames);
    }

    // ==================== ITERATION 3: Alias Shadowing, 0-Row Joins, 3VL NOT(NOT IN), NULL Literals/Empty Subqueries, EXISTS Outer-OR ====================

    [Fact]
    public async Task AliasShadowing_PreventsInnerShadowingOuterAndSupportsSelfJoins()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE T1s (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO T1s VALUES (1, 'L1'), (3, 'L3')", ctx);

        await engine.ExecuteAsync("CREATE TABLE T2s (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO T2s VALUES (1, 'R1'), (2, 'R2')", ctx);

        // Inner T1s is aliased as t3; outer t1 must NOT bind to inner t3
        var existsShadow = await engine.ExecuteAsync(
            "SELECT t1.Id FROM T1s t1 WHERE EXISTS (SELECT 1 FROM T2s t2 INNER JOIN T1s t3 ON t2.Id = t3.Id WHERE t2.Id = t1.Id OR t3.Name = t1.Name)",
            ctx);
        Assert.True(existsShadow.Success, existsShadow.Message);
        Assert.Equal(1, existsShadow.DataFrame!.RowCount);
        Assert.Equal(1, existsShadow.DataFrame.GetColumn("Id").Get(0));

        // Self-join where second table alias matches table name (Emp mgr INNER JOIN Emp emp)
        await engine.ExecuteAsync("CREATE TABLE Emp (Id INT, ManagerId INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO Emp VALUES (1, NULL, 'Boss'), (2, 1, 'Worker')", ctx);

        var selfJoin = await engine.ExecuteAsync(
            "SELECT mgr.Name AS MgrName, emp.Name AS EmpName FROM Emp mgr INNER JOIN Emp emp ON emp.ManagerId = mgr.Id",
            ctx);
        Assert.True(selfJoin.Success, selfJoin.Message);
        Assert.Equal(1, selfJoin.DataFrame!.RowCount);
        Assert.Equal("Boss", selfJoin.DataFrame.GetColumn("MgrName").Get(0));
        Assert.Equal("Worker", selfJoin.DataFrame.GetColumn("EmpName").Get(0));

        // Star ref and BIT alias shadowing
        await engine.ExecuteAsync("CREATE TABLE BitT (Id INT, Flag BIT)", ctx);
        await engine.ExecuteAsync("INSERT INTO BitT VALUES (1, 1), (2, 0)", ctx);

        var starShadowed = await engine.ExecuteAsync("SELECT BitT.* FROM BitT b", ctx);
        Assert.False(starShadowed.Success);

        var bitAlias = await engine.ExecuteAsync("SELECT b1.Id FROM BitT b1 INNER JOIN BitT b2 ON b1.Id = b2.Id WHERE b2.Flag = 1", ctx);
        Assert.True(bitAlias.Success, bitAlias.Message);
        Assert.Equal(1, bitAlias.DataFrame!.RowCount);
        Assert.Equal(1, bitAlias.DataFrame.GetColumn("Id").Get(0));
    }

    [Fact]
    public async Task Joins_ZeroRowInputsAndDisjointIntermediateJoins_PreserveSchemaWithoutCrashing()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE EmptyT (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("CREATE TABLE J1z (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO J1z VALUES (1, 'A')", ctx);
        await engine.ExecuteAsync("CREATE TABLE J2z (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO J2z VALUES (2, 'B'), (4, 'D')", ctx);
        await engine.ExecuteAsync("CREATE TABLE J3z (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO J3z VALUES (3, 'C')", ctx);

        // EmptyT RIGHT OUTER JOIN J2z -> 2 rows (NULL, 2), (NULL, 4)
        var rightEmpty = await engine.ExecuteAsync(
            "SELECT e.Id AS EId, t2.Id AS T2Id FROM EmptyT e RIGHT OUTER JOIN J2z t2 ON e.Id = t2.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(rightEmpty.Success, rightEmpty.Message);
        Assert.Equal(2, rightEmpty.DataFrame!.RowCount);
        Assert.False(rightEmpty.DataFrame.GetColumn("EId").ValidityMask.IsValid(0));
        Assert.Equal(2, rightEmpty.DataFrame.GetColumn("T2Id").Get(0));
        Assert.False(rightEmpty.DataFrame.GetColumn("EId").ValidityMask.IsValid(1));
        Assert.Equal(4, rightEmpty.DataFrame.GetColumn("T2Id").Get(1));

        // J2z LEFT OUTER JOIN EmptyT -> 2 rows (2, NULL), (4, NULL)
        var leftEmpty = await engine.ExecuteAsync(
            "SELECT e.Id AS EId, t2.Id AS T2Id FROM J2z t2 LEFT OUTER JOIN EmptyT e ON t2.Id = e.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(leftEmpty.Success, leftEmpty.Message);
        Assert.Equal(2, leftEmpty.DataFrame!.RowCount);
        Assert.False(leftEmpty.DataFrame.GetColumn("EId").ValidityMask.IsValid(0));
        Assert.Equal(2, leftEmpty.DataFrame.GetColumn("T2Id").Get(0));
        Assert.False(leftEmpty.DataFrame.GetColumn("EId").ValidityMask.IsValid(1));
        Assert.Equal(4, leftEmpty.DataFrame.GetColumn("T2Id").Get(1));

        // J2z INNER JOIN EmptyT -> 0 rows with typed schema
        var innerEmpty = await engine.ExecuteAsync(
            "SELECT e.Id AS EId, t2.Id AS T2Id FROM J2z t2 INNER JOIN EmptyT e ON t2.Id = e.Id",
            ctx);
        Assert.True(innerEmpty.Success, innerEmpty.Message);
        Assert.Equal(0, innerEmpty.DataFrame!.RowCount);
        Assert.Equal(2, innerEmpty.DataFrame.Columns.Count);

        // J2z FULL OUTER JOIN EmptyT -> 2 rows (NULL, 2), (NULL, 4)
        var fullEmpty = await engine.ExecuteAsync(
            "SELECT e.Id AS EId, t2.Id AS T2Id FROM J2z t2 FULL OUTER JOIN EmptyT e ON t2.Id = e.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(fullEmpty.Success, fullEmpty.Message);
        Assert.Equal(2, fullEmpty.DataFrame!.RowCount);
        Assert.False(fullEmpty.DataFrame.GetColumn("EId").ValidityMask.IsValid(0));
        Assert.Equal(2, fullEmpty.DataFrame.GetColumn("T2Id").Get(0));

        // Disjoint 3-way: J1z INNER JOIN J2z (0 rows) RIGHT OUTER JOIN J3z -> 1 row (NULL, NULL, 3)
        var disjoint3Way = await engine.ExecuteAsync(
            "SELECT t1.Id AS I1, t2.Id AS I2, t3.Id AS I3 FROM J1z t1 INNER JOIN J2z t2 ON t1.Id = t2.Id RIGHT OUTER JOIN J3z t3 ON t2.Id = t3.Id",
            ctx);
        Assert.True(disjoint3Way.Success, disjoint3Way.Message);
        Assert.Equal(1, disjoint3Way.DataFrame!.RowCount);
        Assert.False(disjoint3Way.DataFrame.GetColumn("I1").ValidityMask.IsValid(0));
        Assert.False(disjoint3Way.DataFrame.GetColumn("I2").ValidityMask.IsValid(0));
        Assert.Equal(3, disjoint3Way.DataFrame.GetColumn("I3").Get(0));
    }

    [Fact]
    public async Task NotWrappedAroundNotInWithNull_HonorsSqlThreeValuedLogic()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE nums3 (id INT, val INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO nums3 VALUES (0, 10), (1, NULL), (2, 20), (3, 30), (NULL, 40)", ctx);

        await engine.ExecuteAsync("CREATE TABLE sub_null3 (v INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO sub_null3 VALUES (2), (NULL)", ctx);

        // NOT (id NOT IN (1 + 1, NULL)) -> ONLY row (2)
        var notNotInExpr = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (id NOT IN (1 + 1, NULL))",
            ctx);
        Assert.True(notNotInExpr.Success, notNotInExpr.Message);
        Assert.Equal(1, notNotInExpr.DataFrame!.RowCount);
        Assert.Equal(2, notNotInExpr.DataFrame.GetColumn("id").Get(0));

        // NOT (id NOT IN (2, NULL)) -> ONLY row (2)
        var notNotInLit = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (id NOT IN (2, NULL))",
            ctx);
        Assert.True(notNotInLit.Success, notNotInLit.Message);
        Assert.Equal(1, notNotInLit.DataFrame!.RowCount);
        Assert.Equal(2, notNotInLit.DataFrame.GetColumn("id").Get(0));

        // NOT (id NOT IN (SELECT v FROM sub_null3)) -> ONLY row (2)
        var notNotInSub = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (id NOT IN (SELECT v FROM sub_null3))",
            ctx);
        Assert.True(notNotInSub.Success, notNotInSub.Message);
        Assert.Equal(1, notNotInSub.DataFrame!.RowCount);
        Assert.Equal(2, notNotInSub.DataFrame.GetColumn("id").Get(0));

        // Double NOT: NOT (NOT (id NOT IN (2, NULL))) -> 0 rows
        var doubleNot = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (NOT (id NOT IN (2, NULL)))",
            ctx);
        Assert.True(doubleNot.Success, doubleNot.Message);
        Assert.Equal(0, doubleNot.DataFrame!.RowCount);

        // NOT (id NOT IN (2)) -> ONLY row (2), NOT NULL row
        var notNotInNoNull = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (id NOT IN (2))",
            ctx);
        Assert.True(notNotInNoNull.Success, notNotInNoNull.Message);
        Assert.Equal(1, notNotInNoNull.DataFrame!.RowCount);
        Assert.Equal(2, notNotInNoNull.DataFrame.GetColumn("id").Get(0));

        // NOT (id IN (2, NULL)) -> 0 rows (matches are FALSE, non-matches/NULL are UNKNOWN)
        var notInWithNull = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (id IN (2, NULL))",
            ctx);
        Assert.True(notInWithNull.Success, notInWithNull.Message);
        Assert.Equal(0, notInWithNull.DataFrame!.RowCount);

        // NOT (id NOT IN (1 + NULL, 2)) -> ONLY row (2)
        var notNotInArithNull = await engine.ExecuteAsync(
            "SELECT id FROM nums3 WHERE NOT (id NOT IN (1 + NULL, 2))",
            ctx);
        Assert.True(notNotInArithNull.Success, notNotInArithNull.Message);
        Assert.Equal(1, notNotInArithNull.DataFrame!.RowCount);
        Assert.Equal(2, notNotInArithNull.DataFrame.GetColumn("id").Get(0));

        // GROUP BY + HAVING with NOT (id NOT IN (2, NULL)) -> ONLY group (2)
        var groupedHavingNot = await engine.ExecuteAsync(
            "SELECT id, COUNT(*) AS cnt FROM nums3 WHERE id IS NOT NULL GROUP BY id HAVING NOT (id NOT IN (2, NULL))",
            ctx);
        Assert.True(groupedHavingNot.Success, groupedHavingNot.Message);
        Assert.Equal(1, groupedHavingNot.DataFrame!.RowCount);
        Assert.Equal(2, groupedHavingNot.DataFrame.GetColumn("id").Get(0));
    }

    [Fact]
    public async Task NullLiteralsAndEmptyScalarSubqueries_DoNotThrowAndPropagateNulls()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE nums4 (id INT)", ctx);
        await engine.ExecuteAsync("INSERT INTO nums4 VALUES (1), (2), (NULL)", ctx);

        await engine.ExecuteAsync("CREATE TABLE sub_empty (v INT, Active BIT)", ctx);
        await engine.ExecuteAsync("INSERT INTO sub_empty VALUES (1, 1)", ctx);

        // 1 + NULL inside NOT IN -> 0 rows (no Literal type not supported)
        var arithNullIn = await engine.ExecuteAsync(
            "SELECT id FROM nums4 WHERE id NOT IN (1 + NULL, 2)",
            ctx);
        Assert.True(arithNullIn.Success, arithNullIn.Message);
        Assert.Equal(0, arithNullIn.DataFrame!.RowCount);

        // Empty scalar subquery in WHERE -> 0 rows; OR with valid predicate -> returns (1)
        var emptyWhereSub = await engine.ExecuteAsync(
            "SELECT id FROM nums4 WHERE id = (SELECT v FROM sub_empty WHERE v = 999)",
            ctx);
        Assert.True(emptyWhereSub.Success, emptyWhereSub.Message);
        Assert.Equal(0, emptyWhereSub.DataFrame!.RowCount);

        var kleeneOrSub = await engine.ExecuteAsync(
            "SELECT id FROM nums4 WHERE id = 1 OR id = (SELECT v FROM sub_empty WHERE v = 999)",
            ctx);
        Assert.True(kleeneOrSub.Success, kleeneOrSub.Message);
        Assert.Equal(1, kleeneOrSub.DataFrame!.RowCount);
        Assert.Equal(1, kleeneOrSub.DataFrame.GetColumn("id").Get(0));

        // Empty scalar subquery in SELECT projection -> all rows with NULL column
        var emptySelectSub = await engine.ExecuteAsync(
            "SELECT id, (SELECT v FROM sub_empty WHERE v = 999) AS empty_sub FROM nums4 ORDER BY id ASC",
            ctx);
        Assert.True(emptySelectSub.Success, emptySelectSub.Message);
        Assert.Equal(3, emptySelectSub.DataFrame!.RowCount);
        for (int r = 0; r < 3; r++)
        {
            Assert.False(emptySelectSub.DataFrame.GetColumn("empty_sub").ValidityMask.IsValid(r));
        }

        // Empty scalar subquery in ORDER BY -> succeeds and sorts by tie-breaker Id ASC
        var emptyOrderSub = await engine.ExecuteAsync(
            "SELECT id FROM nums4 WHERE id IS NOT NULL ORDER BY (SELECT Active FROM sub_empty WHERE v = 99999) ASC, id ASC",
            ctx);
        Assert.True(emptyOrderSub.Success, emptyOrderSub.Message);
        Assert.Equal(2, emptyOrderSub.DataFrame!.RowCount);
        Assert.Equal(1, emptyOrderSub.DataFrame.GetColumn("id").Get(0));
        Assert.Equal(2, emptyOrderSub.DataFrame.GetColumn("id").Get(1));

        // SELECT NULL without FROM and UPDATE WHERE with empty scalar subquery
        var selectNullNoFrom = await engine.ExecuteAsync("SELECT NULL AS n", ctx);
        Assert.True(selectNullNoFrom.Success, selectNullNoFrom.Message);
        Assert.Equal(1, selectNullNoFrom.DataFrame!.RowCount);
        Assert.False(selectNullNoFrom.DataFrame.GetColumn("n").ValidityMask.IsValid(0));

        var updateEmptySub = await engine.ExecuteAsync(
            "UPDATE nums4 SET id = 99 WHERE id = (SELECT v FROM sub_empty WHERE v = 999)",
            ctx);
        Assert.True(updateEmptySub.Success, updateEmptySub.Message);
        Assert.Equal(0, updateEmptySub.AffectedRows);
    }

    [Fact]
    public async Task Exists_WithOuterOnlyPredicateInOr_AndMultipleTableStarRefs_WorkCleanly()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE C1o (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO C1o VALUES (1, 'A'), (2, 'B'), (3, 'C'), (NULL, 'N')", ctx);

        await engine.ExecuteAsync("CREATE TABLE C2o (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO C2o VALUES (1, 'X')", ctx);

        await engine.ExecuteAsync("CREATE TABLE C2Empty (Id INT)", ctx);

        // EXISTS with inner OR containing outer-only predicate t1.Id = 2 (C2o non-empty) -> (1), (2)
        var existsOuterOr = await engine.ExecuteAsync(
            "SELECT t1.Id FROM C1o t1 WHERE EXISTS (SELECT 1 FROM C2o t2 WHERE t2.Id = t1.Id OR t1.Id = 2) ORDER BY t1.Id ASC",
            ctx);
        Assert.True(existsOuterOr.Success, existsOuterOr.Message);
        Assert.Equal(2, existsOuterOr.DataFrame!.RowCount);
        Assert.Equal(1, existsOuterOr.DataFrame.GetColumn("Id").Get(0));
        Assert.Equal(2, existsOuterOr.DataFrame.GetColumn("Id").Get(1));

        // NOT EXISTS with outer NULL row -> returns (3) and (NULL)
        var notExistsOuterOr = await engine.ExecuteAsync(
            "SELECT t1.Id FROM C1o t1 WHERE NOT EXISTS (SELECT 1 FROM C2o t2 WHERE t2.Id = t1.Id OR t1.Id = 2)",
            ctx);
        Assert.True(notExistsOuterOr.Success, notExistsOuterOr.Message);
        Assert.Equal(2, notExistsOuterOr.DataFrame!.RowCount);

        // When C2Empty has 0 rows, EXISTS (... OR t1.Id = 2) must be FALSE for all rows
        var existsOnEmptyInner = await engine.ExecuteAsync(
            "SELECT t1.Id FROM C1o t1 WHERE EXISTS (SELECT 1 FROM C2Empty t2 WHERE t2.Id = t1.Id OR t1.Id = 2)",
            ctx);
        Assert.True(existsOnEmptyInner.Success, existsOnEmptyInner.Message);
        Assert.Equal(0, existsOnEmptyInner.DataFrame!.RowCount);

        // Multiple SqlStarRefs (t1.*, t2.*) deduplicate output column names
        var multiStar = await engine.ExecuteAsync(
            "SELECT t1.*, t2.* FROM C1o t1 INNER JOIN C2o t2 ON t1.Id = t2.Id",
            ctx);
        Assert.True(multiStar.Success, multiStar.Message);
        Assert.Equal(4, multiStar.DataFrame!.Columns.Count);
        Assert.Equal(4, multiStar.DataFrame.ColumnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ==================== ITERATION 4: Shared ISeries Isolation (.Alias / 0-Row Joins / Triggers / DML) ====================

    [Fact]
    public async Task SingleTableAliasProjection_DoesNotMutateBufferPoolSnapshot()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE CtrlA (Id INT, V VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO CtrlA VALUES (1, 'a')", ctx);
        await engine.ExecuteAsync("CREATE TABLE CtrlB (BId INT, W VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO CtrlB VALUES (1, 'b')", ctx);

        // Case C: Single-table SELECT Id AS X must NOT mutate CtrlA.CurrentSnapshot.ColumnNames
        var aliasRes = await engine.ExecuteAsync("SELECT Id AS X, V FROM CtrlA", ctx);
        Assert.True(aliasRes.Success, aliasRes.Message);
        Assert.Equal(new[] { "X", "V" }, aliasRes.DataFrame!.ColumnNames);
        Assert.Equal(1, aliasRes.DataFrame.GetColumn("X").Get(0));
        Assert.Equal("a", aliasRes.DataFrame.GetColumn("V").Get(0));

        Assert.Equal(
            new[] { "Id", "V" },
            catalog.BufferPool.GetOrLoad(catalog.GetTable("CtrlA")!).CurrentSnapshot.ColumnNames);

        var joinRes = await engine.ExecuteAsync(
            "SELECT a.Id AS Y, b.BId AS Z FROM CtrlA a INNER JOIN CtrlB b ON a.Id = b.BId",
            ctx);
        Assert.True(joinRes.Success, joinRes.Message);
        Assert.Equal(1, joinRes.DataFrame!.RowCount);
        Assert.Equal(1, joinRes.DataFrame.GetColumn("Y").Get(0));
        Assert.Equal(1, joinRes.DataFrame.GetColumn("Z").Get(0));
    }

    [Fact]
    public async Task ZeroRowJoinsWithColumnAliases_DoNotMutateSourceTableSnapshots()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE T1j (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO T1j VALUES (1, 'L1')", ctx);

        await engine.ExecuteAsync("CREATE TABLE T2j (Id INT, Name VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO T2j VALUES (1, 'R1'), (2, 'R2')", ctx);

        await engine.ExecuteAsync("CREATE TABLE EmptyTj (Id INT, Name VARCHAR)", ctx);

        // Case A: 0-row LEFT OUTER JOIN with column alias must NOT mutate T2j.CurrentSnapshot.ColumnNames
        var leftAlias = await engine.ExecuteAsync(
            "SELECT t2.Id AS T2Id, e.Id AS EId FROM T2j t2 LEFT OUTER JOIN EmptyTj e ON t2.Id = e.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(leftAlias.Success, leftAlias.Message);
        Assert.Equal(2, leftAlias.DataFrame!.RowCount);
        Assert.Equal(1, leftAlias.DataFrame.GetColumn("T2Id").Get(0));
        Assert.Equal(2, leftAlias.DataFrame.GetColumn("T2Id").Get(1));
        Assert.False(leftAlias.DataFrame.GetColumn("EId").ValidityMask.IsValid(0));
        Assert.False(leftAlias.DataFrame.GetColumn("EId").ValidityMask.IsValid(1));

        Assert.Equal(
            new[] { "Id", "Name" },
            catalog.BufferPool.GetOrLoad(catalog.GetTable("T2j")!).CurrentSnapshot.ColumnNames);

        // Subsequent FULL OUTER JOIN, GROUP BY, and INNER JOIN against T2j must return exact uncorrupted rows
        var fullAfter = await engine.ExecuteAsync(
            "SELECT t2.Id AS T2Id, e.Id AS EId FROM T2j t2 FULL OUTER JOIN EmptyTj e ON t2.Id = e.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(fullAfter.Success, fullAfter.Message);
        Assert.Equal(2, fullAfter.DataFrame!.RowCount);
        Assert.Equal(1, fullAfter.DataFrame.GetColumn("T2Id").Get(0));
        Assert.Equal(2, fullAfter.DataFrame.GetColumn("T2Id").Get(1));

        var groupAfter = await engine.ExecuteAsync(
            "SELECT t2.Id, COUNT(*) AS Cnt FROM T2j t2 LEFT OUTER JOIN EmptyTj e ON t2.Id = e.Id GROUP BY t2.Id ORDER BY t2.Id ASC",
            ctx);
        Assert.True(groupAfter.Success, groupAfter.Message);
        Assert.Equal(2, groupAfter.DataFrame!.RowCount);
        Assert.Equal(1, groupAfter.DataFrame.GetColumn("Id").Get(0));
        Assert.Equal(1L, Convert.ToInt64(groupAfter.DataFrame.GetColumn("Cnt").Get(0)));
        Assert.Equal(2, groupAfter.DataFrame.GetColumn("Id").Get(1));
        Assert.Equal(1L, Convert.ToInt64(groupAfter.DataFrame.GetColumn("Cnt").Get(1)));

        var innerAfter = await engine.ExecuteAsync(
            "SELECT t1.*, t2.* FROM T1j t1 INNER JOIN T2j t2 ON t1.Id = t2.Id",
            ctx);
        Assert.True(innerAfter.Success, innerAfter.Message);
        Assert.Equal(1, innerAfter.DataFrame!.RowCount);
        Assert.Equal(1, innerAfter.DataFrame.GetColumn("Id").Get(0));
        Assert.Equal("L1", innerAfter.DataFrame.GetColumn("Name").Get(0));
        Assert.Equal("R1", innerAfter.DataFrame.GetColumn("Name_right").Get(0));

        // Case B: 0-row RIGHT OUTER JOIN with column alias must NOT mutate FullBj.CurrentSnapshot.ColumnNames
        await engine.ExecuteAsync("CREATE TABLE EmptyAj (AId INT, AName VARCHAR)", ctx);
        await engine.ExecuteAsync("CREATE TABLE FullBj (BId INT, BName VARCHAR)", ctx);
        await engine.ExecuteAsync("INSERT INTO FullBj VALUES (1, 'B1')", ctx);

        var rightAlias = await engine.ExecuteAsync(
            "SELECT b.BId AS RenamedB, b.BName FROM EmptyAj a RIGHT OUTER JOIN FullBj b ON a.AId = b.BId",
            ctx);
        Assert.True(rightAlias.Success, rightAlias.Message);
        Assert.Equal(1, rightAlias.DataFrame!.RowCount);
        Assert.Equal(1, rightAlias.DataFrame.GetColumn("RenamedB").Get(0));
        Assert.Equal("B1", rightAlias.DataFrame.GetColumn("BName").Get(0));

        Assert.Equal(
            new[] { "BId", "BName" },
            catalog.BufferPool.GetOrLoad(catalog.GetTable("FullBj")!).CurrentSnapshot.ColumnNames);

        var followUpRight = await engine.ExecuteAsync(
            "SELECT b.BId, b.BName FROM FullBj b WHERE b.BId = 1",
            ctx);
        Assert.True(followUpRight.Success, followUpRight.Message);
        Assert.Equal(1, followUpRight.DataFrame!.RowCount);
        Assert.Equal(1, followUpRight.DataFrame.GetColumn("BId").Get(0));
        Assert.Equal("B1", followUpRight.DataFrame.GetColumn("BName").Get(0));
    }

    [Fact]
    public async Task ColumnCloning_AllSqlTypesWithNulls_AndDmlOrTriggerAliases_PreserveSnapshots()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync(
            "CREATE TABLE TypedSrc (I INT, F FLOAT, B BIT, D DATETIME, S VARCHAR)",
            ctx);
        await engine.ExecuteAsync(
            "CREATE TABLE TypedEmpty (I INT, F FLOAT, B BIT, D DATETIME, S VARCHAR)",
            ctx);
        await engine.ExecuteAsync(
            "CREATE TABLE TriggerAudit (TrigId INT, TrigStr VARCHAR)",
            ctx);
        await engine.ExecuteAsync(
            "CREATE TRIGGER tr_typed_ins ON TypedSrc AFTER INSERT AS INSERT INTO TriggerAudit SELECT I AS TrigId, S AS TrigStr FROM inserted",
            ctx);

        await engine.ExecuteAsync(
            "INSERT INTO TypedSrc VALUES (1, 1.5, 0, '2026-11-28T00:00:00Z', 'alpha'), (2, NULL, NULL, NULL, NULL)",
            ctx);

        // Verify trigger inserted table alias projection did not mutate TypedSrc buffer delta/snapshot
        Assert.Equal(
            new[] { "I", "F", "B", "D", "S" },
            catalog.BufferPool.GetOrLoad(catalog.GetTable("TypedSrc")!).CurrentSnapshot.ColumnNames);

        var singleAlias = await engine.ExecuteAsync(
            "SELECT I AS I2, F AS F2, B AS B2, D AS D2, S AS S2 FROM TypedSrc ORDER BY I ASC",
            ctx);
        Assert.True(singleAlias.Success, singleAlias.Message);
        Assert.Equal(2, singleAlias.DataFrame!.RowCount);
        Assert.Equal(1, singleAlias.DataFrame.GetColumn("I2").Get(0));
        Assert.Equal(1.5, singleAlias.DataFrame.GetColumn("F2").Get(0));
        Assert.Equal(false, singleAlias.DataFrame.GetColumn("B2").Get(0));
        Assert.Equal("alpha", singleAlias.DataFrame.GetColumn("S2").Get(0));
        Assert.False(singleAlias.DataFrame.GetColumn("F2").ValidityMask.IsValid(1));
        Assert.False(singleAlias.DataFrame.GetColumn("B2").ValidityMask.IsValid(1));
        Assert.False(singleAlias.DataFrame.GetColumn("D2").ValidityMask.IsValid(1));
        Assert.False(singleAlias.DataFrame.GetColumn("S2").ValidityMask.IsValid(1));

        // 0-row join across all 5 SQL types with aliases
        var zeroJoinAllTypes = await engine.ExecuteAsync(
            "SELECT s.I AS I3, s.F AS F3, s.B AS B3, s.D AS D3, s.S AS S3, e.I AS E3 " +
            "FROM TypedSrc s LEFT OUTER JOIN TypedEmpty e ON s.I = e.I ORDER BY s.I ASC",
            ctx);
        Assert.True(zeroJoinAllTypes.Success, zeroJoinAllTypes.Message);
        Assert.Equal(2, zeroJoinAllTypes.DataFrame!.RowCount);
        Assert.Equal("alpha", zeroJoinAllTypes.DataFrame.GetColumn("S3").Get(0));
        Assert.False(zeroJoinAllTypes.DataFrame.GetColumn("F3").ValidityMask.IsValid(1));
        Assert.False(zeroJoinAllTypes.DataFrame.GetColumn("S3").ValidityMask.IsValid(1));
        Assert.False(zeroJoinAllTypes.DataFrame.GetColumn("E3").ValidityMask.IsValid(0));

        // Direct BIT column predicate in DELETE / UPDATE (uses .Alias("__match")) must NOT rename CurrentSnapshot's 'B' column
        var delBit = await engine.ExecuteAsync("DELETE FROM TypedSrc WHERE B", ctx);
        Assert.True(delBit.Success, delBit.Message);
        Assert.Equal(0, delBit.AffectedRows);
        Assert.Equal(
            new[] { "I", "F", "B", "D", "S" },
            catalog.BufferPool.GetOrLoad(catalog.GetTable("TypedSrc")!).CurrentSnapshot.ColumnNames);

        var updBit = await engine.ExecuteAsync("UPDATE TypedSrc SET I = 99 WHERE B", ctx);
        Assert.True(updBit.Success, updBit.Message);
        Assert.Equal(0, updBit.AffectedRows);
        Assert.Equal(
            new[] { "I", "F", "B", "D", "S" },
            catalog.BufferPool.GetOrLoad(catalog.GetTable("TypedSrc")!).CurrentSnapshot.ColumnNames);
    }
}
