namespace Glacier.Sql.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.Wal;
using Xunit;
using ExecutionContext = Glacier.Sql.Engine.ExecutionContext;

public class RemediationVerificationTests : IDisposable
{
    private readonly string _testDir;

    public RemediationVerificationTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"glacier_sql_remediation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Having_ProjectedUnprojectedCompoundAliasesGlobalAndPostGroupOrderBy_ExecutesAccurately()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var ctx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE sales (id INT, region VARCHAR, qty INT, price DOUBLE)", ctx);
        await engine.ExecuteAsync("INSERT INTO sales VALUES (1, 'North', 40, 10.0)", ctx);
        await engine.ExecuteAsync("INSERT INTO sales VALUES (2, 'North', 70, 20.0)", ctx);
        await engine.ExecuteAsync("INSERT INTO sales VALUES (3, 'North', 15, 15.0)", ctx);
        await engine.ExecuteAsync("INSERT INTO sales VALUES (4, 'South', 30, 50.0)", ctx);
        await engine.ExecuteAsync("INSERT INTO sales VALUES (5, 'South', 20, 60.0)", ctx);
        await engine.ExecuteAsync("INSERT INTO sales VALUES (6, 'West', 90, 600.0)", ctx);

        // 1. Grouped HAVING with unprojected aggregate (COUNT(*) > 2) and post-group ORDER BY on unprojected aggregate
        var r1 = await engine.ExecuteAsync(
            "SELECT region FROM sales GROUP BY region HAVING COUNT(*) > 2 ORDER BY SUM(qty) DESC",
            ctx);
        Assert.True(r1.Success, r1.Message);
        Assert.Single(r1.DataFrame!.Columns); // helper __agg_* columns must not leak into output
        Assert.Equal("region", r1.DataFrame.Columns[0].Name);
        Assert.Equal(1, r1.DataFrame.RowCount);
        Assert.Equal("North", r1.DataFrame.GetColumn("region").Get(0));

        // 2. Grouped HAVING on projected aggregate SUM(qty) > 100
        var r2 = await engine.ExecuteAsync(
            "SELECT region, SUM(qty) AS total_qty FROM sales GROUP BY region HAVING SUM(qty) > 100",
            ctx);
        Assert.True(r2.Success, r2.Message);
        Assert.Equal(1, r2.DataFrame!.RowCount);
        Assert.Equal("North", r2.DataFrame.GetColumn("region").Get(0));
        Assert.Equal(125, Convert.ToInt32(r2.DataFrame.GetColumn("total_qty").Get(0)));

        // 3. Compound HAVING with AND / OR + post-group ORDER BY on alias DESC
        var r3 = await engine.ExecuteAsync(
            "SELECT region, SUM(qty) AS total_qty FROM sales GROUP BY region HAVING (COUNT(*) >= 2 AND SUM(qty) >= 50) OR MAX(price) >= 500.0 ORDER BY total_qty DESC",
            ctx);
        Assert.True(r3.Success, r3.Message);
        Assert.Equal(3, r3.DataFrame!.RowCount);
        Assert.Equal("North", r3.DataFrame.GetColumn("region").Get(0)); // 125
        Assert.Equal("West", r3.DataFrame.GetColumn("region").Get(1));  // 90
        Assert.Equal("South", r3.DataFrame.GetColumn("region").Get(2)); // 50
        Assert.Equal(2, r3.DataFrame.Columns.Count);

        // 4. HAVING referencing SELECT projection alias + ORDER BY grouped column ASC
        var r4 = await engine.ExecuteAsync(
            "SELECT region, SUM(qty) AS total_qty FROM sales GROUP BY region HAVING total_qty >= 50 ORDER BY region ASC",
            ctx);
        Assert.True(r4.Success, r4.Message);
        Assert.Equal(3, r4.DataFrame!.RowCount);
        Assert.Equal("North", r4.DataFrame.GetColumn("region").Get(0));
        Assert.Equal("South", r4.DataFrame.GetColumn("region").Get(1));
        Assert.Equal("West", r4.DataFrame.GetColumn("region").Get(2));

        // 5. Global aggregate with HAVING (no GROUP BY): true vs false filter
        var r5True = await engine.ExecuteAsync("SELECT COUNT(*) AS cnt FROM sales HAVING SUM(qty) > 200", ctx);
        Assert.True(r5True.Success, r5True.Message);
        Assert.Equal(1, r5True.DataFrame!.RowCount);
        Assert.Equal(6, Convert.ToInt32(r5True.DataFrame.GetColumn("cnt").Get(0)));

        var r5False = await engine.ExecuteAsync("SELECT COUNT(*) AS cnt FROM sales HAVING SUM(qty) > 9999", ctx);
        Assert.True(r5False.Success, r5False.Message);
        Assert.Equal(0, r5False.DataFrame!.RowCount);
    }

    [Fact]
    public async Task AsyncReaderWriterLock_ConcurrentReadToWriteUpgrades_NeverDeadlockAndPreserveExclusivity()
    {
        var rwLock = new AsyncReaderWriterLock();

        var read1 = await rwLock.EnterReadLockAsync();
        var read2 = await rwLock.EnterReadLockAsync();
        Assert.Equal(2, rwLock.CurrentReadCount);

        int activeWriters = 0;
        int maxConcurrentWriters = 0;

        async Task UpgradeWorkerAsync(AsyncReaderWriterLock.LockReleaser readHandle)
        {
            using var writeHandle = await rwLock.UpgradeReadToWriteAsync(readHandle);
            int current = Interlocked.Increment(ref activeWriters);
            int observedMax;
            do
            {
                observedMax = Volatile.Read(ref maxConcurrentWriters);
                if (current <= observedMax) break;
            } while (Interlocked.CompareExchange(ref maxConcurrentWriters, current, observedMax) != observedMax);

            await Task.Yield();
            Interlocked.Decrement(ref activeWriters);
        }

        var t1 = Task.Run(() => UpgradeWorkerAsync(read1));
        var t2 = Task.Run(() => UpgradeWorkerAsync(read2));

        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, maxConcurrentWriters);
        Assert.False(rwLock.IsWriteLocked);
        Assert.Equal(0, rwLock.CurrentReadCount);
    }

    [Fact]
    public async Task ConcurrentDml_UpdatesDeletesInsertSelectAndUniqueConstraints_ZeroLostUpdates()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var setupCtx = new ExecutionContext(catalog);

        // 1. Concurrent UPDATE increments on same row -> zero lost updates
        await engine.ExecuteAsync("CREATE TABLE counters (id INT PRIMARY KEY, cnt INT)", setupCtx);
        await engine.ExecuteAsync("INSERT INTO counters VALUES (1, 0)", setupCtx);

        const int updateWorkers = 16;
        var updateTasks = Enumerable.Range(0, updateWorkers).Select(_ => Task.Run(async () =>
        {
            var ctx = new ExecutionContext(catalog);
            var res = await engine.ExecuteAsync("UPDATE counters SET cnt = cnt + 1 WHERE id = 1", ctx);
            Assert.True(res.Success, res.Message);
        }));
        await Task.WhenAll(updateTasks).WaitAsync(TimeSpan.FromSeconds(20));

        var cntCheck = await engine.ExecuteAsync("SELECT cnt FROM counters WHERE id = 1", setupCtx);
        Assert.Equal(updateWorkers, Convert.ToInt32(cntCheck.DataFrame!.GetColumn("cnt").Get(0)));

        // 2. Concurrent INSERTs racing on PRIMARY KEY duplicate key -> exactly 1 succeeds, rest fail cleanly
        int pkSuccesses = 0;
        var pkTasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            var ctx = new ExecutionContext(catalog);
            var res = await engine.ExecuteAsync("INSERT INTO counters VALUES (99, 1)", ctx);
            if (res.Success) Interlocked.Increment(ref pkSuccesses);
        }));
        await Task.WhenAll(pkTasks).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, pkSuccesses);

        // 3. Concurrent DELETEs & INSERT ... SELECT across tables -> zero lost mutations
        await engine.ExecuteAsync("CREATE TABLE batch_src (id INT, val INT)", setupCtx);
        await engine.ExecuteAsync("CREATE TABLE batch_dst (id INT, val INT, extra VARCHAR)", setupCtx);
        for (int i = 1; i <= 10; i++)
        {
            await engine.ExecuteAsync($"INSERT INTO batch_src VALUES ({i}, {i * 10})", setupCtx);
        }

        var dmlTasks = new List<Task>();
        for (int i = 1; i <= 5; i++)
        {
            int targetId = i;
            dmlTasks.Add(Task.Run(async () =>
            {
                var ctx = new ExecutionContext(catalog);
                var delRes = await engine.ExecuteAsync($"DELETE FROM batch_src WHERE id = {targetId}", ctx);
                Assert.True(delRes.Success, delRes.Message);
            }));
        }
        for (int b = 0; b < 4; b++)
        {
            int val = 1000 + b;
            dmlTasks.Add(Task.Run(async () =>
            {
                var ctx = new ExecutionContext(catalog);
                // Partial column list exercises CreateNullSeries for unmapped VARCHAR 'extra' column
                var insSelRes = await engine.ExecuteAsync($"INSERT INTO batch_dst (id, val) SELECT id, {val} FROM counters WHERE id = 1", ctx);
                Assert.True(insSelRes.Success, insSelRes.Message);
            }));
        }

        await Task.WhenAll(dmlTasks).WaitAsync(TimeSpan.FromSeconds(20));

        var srcAfter = await engine.ExecuteAsync("SELECT * FROM batch_src", setupCtx);
        Assert.Equal(5, srcAfter.DataFrame!.RowCount);

        var dstAfter = await engine.ExecuteAsync("SELECT * FROM batch_dst", setupCtx);
        Assert.Equal(4, dstAfter.DataFrame!.RowCount);
        for (int r = 0; r < dstAfter.DataFrame.RowCount; r++)
        {
            Assert.False(dstAfter.DataFrame.GetColumn("extra").ValidityMask.IsValid(r));
        }
    }

    [Fact]
    public async Task SubqueryAndViewReads_WaitOnExclusiveWriteLockOfReferencedTables()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var writerCtx = new ExecutionContext(catalog);

        await engine.ExecuteAsync("CREATE TABLE outer_t (id INT, label VARCHAR)", writerCtx);
        await engine.ExecuteAsync("CREATE TABLE inner_t (id INT, active BIT)", writerCtx);
        await engine.ExecuteAsync("INSERT INTO outer_t VALUES (1, 'Item1')", writerCtx);
        await engine.ExecuteAsync("INSERT INTO inner_t VALUES (1, 0)", writerCtx);
        await engine.ExecuteAsync("CREATE VIEW v_inner AS SELECT id FROM inner_t WHERE active = 1", writerCtx);

        // Begin transaction mutating inner_t
        await engine.ExecuteAsync("BEGIN TRANSACTION", writerCtx);
        await engine.ExecuteAsync("UPDATE inner_t SET active = 1 WHERE id = 1", writerCtx);

        var innerLock = catalog.GetTableLock("inner_t");
        Assert.True(innerLock.IsWriteLocked);

        // Concurrent SELECT with subquery referencing inner_t must queue behind inner_t write lock
        var subqTask = Task.Run(async () =>
        {
            var readerCtx = new ExecutionContext(catalog);
            return await engine.ExecuteAsync("SELECT label FROM outer_t WHERE id IN (SELECT id FROM v_inner)", readerCtx);
        });

        await innerLock.WaitForReaderQueuedAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(subqTask.IsCompleted);

        await engine.ExecuteAsync("COMMIT", writerCtx);

        var subqRes = await subqTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(subqRes.Success, subqRes.Message);
        Assert.Equal(1, subqRes.DataFrame!.RowCount);
        Assert.Equal("Item1", subqRes.DataFrame.GetColumn("label").Get(0));
    }

    [Fact]
    public async Task CheckpointDuringUncommittedTransaction_NeverFlushesDirtySnapshotToDisk()
    {
        string dir = Path.Combine(_testDir, "dirty_ck");
        Directory.CreateDirectory(dir);

        using (var catalog1 = new CatalogManager(dir))
        {
            var engine1 = new SqlEngine(catalog1);
            var ctx1 = new ExecutionContext(catalog1);

            await engine1.ExecuteAsync("CREATE TABLE ledger (id INT, amount INT)", ctx1);
            await engine1.ExecuteAsync("INSERT INTO ledger VALUES (1, 100)", ctx1);
            catalog1.Checkpoint();

            // Start uncommitted transaction and mutate ledger
            await engine1.ExecuteAsync("BEGIN TRANSACTION", ctx1);
            await engine1.ExecuteAsync("UPDATE ledger SET amount = 9999 WHERE id = 1", ctx1);
            await engine1.ExecuteAsync("INSERT INTO ledger VALUES (2, 5000)", ctx1);

            // Trigger explicit checkpoint mid-transaction: must NOT write uncommitted 9999 / row 2 to ledger.ipc!
            catalog1.Checkpoint();

            var meta = catalog1.GetTable("ledger")!;
            var diskDf = TableStorage.ReadFromFile(meta.BackingFile);
            Assert.Equal(1, diskDf.RowCount);
            Assert.Equal(100, diskDf.GetColumn("amount").Get(0));
            // Crash without COMMIT
        }

        // Restart and verify uncommitted data is absent
        using (var catalog2 = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(catalog2);
            var ctx2 = new ExecutionContext(catalog2);

            var res = await engine2.ExecuteAsync("SELECT id, amount FROM ledger ORDER BY id ASC", ctx2);
            Assert.True(res.Success, res.Message);
            Assert.Equal(1, res.DataFrame!.RowCount);
            Assert.Equal(100, res.DataFrame.GetColumn("amount").Get(0));
        }
    }

    [Fact]
    public async Task WalMultiCrashRecovery_PreservesLsnMonotonicityAndPhysicalUpdateDeleteSnapshots()
    {
        string dir = Path.Combine(_testDir, "multi_crash");
        Directory.CreateDirectory(dir);

        // Session 1: Create, checkpoint, then append uncheckpointed INSERT + crash
        using (var catalog1 = new CatalogManager(dir))
        {
            var engine1 = new SqlEngine(catalog1);
            var ctx1 = new ExecutionContext(catalog1);

            await engine1.ExecuteAsync("CREATE TABLE items (id INT, note VARCHAR)", ctx1);
            await engine1.ExecuteAsync("INSERT INTO items VALUES (1, 'One')", ctx1);
            await engine1.ExecuteAsync("INSERT INTO items VALUES (2, 'Two')", ctx1);
            catalog1.Checkpoint();

            // Uncheckpointed insert before Crash #1
            await engine1.ExecuteAsync("INSERT INTO items VALUES (3, 'Three')", ctx1);
        }

        // Session 2: Recovers row 3 on load (advancing checkpointLsn), then performs AST-direct UPDATE & DELETE
        // with sqlText: null + another INSERT WITHOUT calling Checkpoint(), followed by Crash #2!
        using (var catalog2 = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(catalog2);
            var ctx2 = new ExecutionContext(catalog2);

            // Execute UPDATE and DELETE via ExecuteStatementAsync with sqlText = null
            var updateStmt = new UpdateStatement(
                "items",
                new List<UpdateAssignment> { new("note", new SqlLiteral("One 'Updated' [ok]")) },
                new SqlBinaryExpression(new SqlColumnRef("id"), "=", new SqlLiteral(1)));
            var upRes = await engine2.ExecuteStatementAsync(updateStmt, ctx2, sqlText: null);
            Assert.True(upRes.Success, upRes.Message);

            var deleteStmt = new DeleteStatement(
                "items",
                new SqlBinaryExpression(new SqlColumnRef("id"), "=", new SqlLiteral(2)));
            var delRes = await engine2.ExecuteStatementAsync(deleteStmt, ctx2, sqlText: null);
            Assert.True(delRes.Success, delRes.Message);

            await engine2.ExecuteAsync("INSERT INTO items VALUES (4, 'Four')", ctx2);
            // Crash #2 without Checkpoint()! If WalWriter reset _currentLsn to 0, these LSNs would be <= checkpointLsn and lost!
        }

        // Session 3: Verify all Session 2 post-recovery mutations survived Crash #2
        using (var catalog3 = new CatalogManager(dir))
        {
            var engine3 = new SqlEngine(catalog3);
            var ctx3 = new ExecutionContext(catalog3);

            var res = await engine3.ExecuteAsync("SELECT id, note FROM items ORDER BY id ASC", ctx3);
            Assert.True(res.Success, res.Message);
            var df = res.DataFrame!;
            Assert.Equal(3, df.RowCount); // id 1 (updated), id 3, id 4 (id 2 deleted)
            Assert.Equal(1, df.GetColumn("id").Get(0));
            Assert.Equal("One 'Updated' [ok]", df.GetColumn("note").Get(0));
            Assert.Equal(3, df.GetColumn("id").Get(1));
            Assert.Equal("Three", df.GetColumn("note").Get(1));
            Assert.Equal(4, df.GetColumn("id").Get(2));
            Assert.Equal("Four", df.GetColumn("note").Get(2));
        }
    }

    [Fact]
    public async Task AlterTable_CheckpointsWalBoundary_AndPartialTableCheckpointPreservesOtherDirtyTables()
    {
        string dir = Path.Combine(_testDir, "alter_and_partial_ck");
        Directory.CreateDirectory(dir);

        using (var catalog1 = new CatalogManager(dir))
        {
            var engine1 = new SqlEngine(catalog1);
            var ctx1 = new ExecutionContext(catalog1);

            await engine1.ExecuteAsync("CREATE TABLE tbl_a (id INT)", ctx1);
            await engine1.ExecuteAsync("CREATE TABLE tbl_b (id INT)", ctx1);

            // tbl_a gets uncheckpointed row (1) before ALTER TABLE
            await engine1.ExecuteAsync("INSERT INTO tbl_a VALUES (1)", ctx1);

            // ALTER TABLE tbl_a writes 2-col schema to disk and records per-table CheckpointEnd
            var alterRes = await engine1.ExecuteAsync("ALTER TABLE tbl_a ADD tag VARCHAR", ctx1);
            Assert.True(alterRes.Success, alterRes.Message);

            // Uncheckpointed post-alter insert on tbl_a (LSN X) and tbl_b (LSN X+1)
            await engine1.ExecuteAsync("INSERT INTO tbl_a VALUES (2, 'v2')", ctx1);
            await engine1.ExecuteAsync("INSERT INTO tbl_b VALUES (100)", ctx1);

            // Checkpoint ONLY tbl_b -> global checkpointLsn must NOT advance past tbl_a's unflushed row 2!
            catalog1.BufferPool.CheckpointTable("tbl_b", catalog1.WalWriter);
        }

        // Recover after crash: tbl_a must have (1, NULL) and (2, 'v2'), and tbl_b must have single (100) without duplicate replay
        using (var catalog2 = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(catalog2);
            var ctx2 = new ExecutionContext(catalog2);

            var resA = await engine2.ExecuteAsync("SELECT id, tag FROM tbl_a ORDER BY id ASC", ctx2);
            Assert.True(resA.Success, resA.Message);
            Assert.Equal(2, resA.DataFrame!.RowCount);
            Assert.Equal(1, resA.DataFrame.GetColumn("id").Get(0));
            Assert.False(resA.DataFrame.GetColumn("tag").ValidityMask.IsValid(0));
            Assert.Equal(2, resA.DataFrame.GetColumn("id").Get(1));
            Assert.Equal("v2", resA.DataFrame.GetColumn("tag").Get(1));

            var resB = await engine2.ExecuteAsync("SELECT id FROM tbl_b", ctx2);
            Assert.True(resB.Success, resB.Message);
            Assert.Equal(1, resB.DataFrame!.RowCount);
            Assert.Equal(100, resB.DataFrame.GetColumn("id").Get(0));
        }
    }

    [Fact]
    public void WalHeaderCrc32_CorruptedCheckpointHeader_RejectedByReader()
    {
        string walPath = Path.Combine(_testDir, "header_crc.wal");
        using (var writer = new WalWriter(walPath))
        {
            writer.AppendRecord(WalRecordType.RowInsert, Guid.Empty, "t", new byte[] { 1, 2, 3 });
            writer.UpdateCheckpointLsn(1);
        }

        // Valid header succeeds
        var validRecords = WalReader.Read(walPath, out ulong validCk);
        Assert.Equal(1UL, validCk);
        Assert.Single(validRecords);

        // Flip a byte in checkpointLsn (offset 8) without updating header CRC32 (offset 28)
        byte[] bytes = File.ReadAllBytes(walPath);
        bytes[8] ^= 0xFF;
        File.WriteAllBytes(walPath, bytes);

        var corruptedRecords = WalReader.Read(walPath, out ulong corruptedCk);
        Assert.Equal(0UL, corruptedCk);
        Assert.Empty(corruptedRecords);
    }

    [Fact]
    public async Task BracketedIdentifiersAndLargeLongLiterals_RoundTripAcrossCheckConstraintsAndViews()
    {
        string dir = Path.Combine(_testDir, "brackets_roundtrip");
        Directory.CreateDirectory(dir);

        using (var catalog1 = new CatalogManager(dir))
        {
            var engine1 = new SqlEngine(catalog1);
            var ctx1 = new ExecutionContext(catalog1);

            var createRes = await engine1.ExecuteAsync(
                "CREATE TABLE [my items] ([col 1] INT CHECK ([col 1] > 0), [select] VARCHAR)",
                ctx1);
            Assert.True(createRes.Success, createRes.Message);

            var viewRes = await engine1.ExecuteAsync(
                "CREATE VIEW [v items] AS SELECT [col 1], [select] FROM [my items] WHERE [col 1] >= 10",
                ctx1);
            Assert.True(viewRes.Success, viewRes.Message);

            var badInsert = await engine1.ExecuteAsync("INSERT INTO [my items] VALUES (-3, 'bad')", ctx1);
            Assert.False(badInsert.Success);
            Assert.Contains("CHECK constraint", badInsert.Message);

            var okInsert = await engine1.ExecuteAsync("INSERT INTO [my items] VALUES (25, 'good')", ctx1);
            Assert.True(okInsert.Success, okInsert.Message);
            catalog1.Checkpoint();
        }

        // Reload catalog from disk to verify persisted CHECK expression and VIEW SQL parse cleanly
        using (var catalog2 = new CatalogManager(dir))
        {
            var engine2 = new SqlEngine(catalog2);
            var ctx2 = new ExecutionContext(catalog2);

            var viewQuery = await engine2.ExecuteAsync("SELECT [col 1], [select] FROM [v items]", ctx2);
            Assert.True(viewQuery.Success, viewQuery.Message);
            Assert.Equal(1, viewQuery.DataFrame!.RowCount);
            Assert.Equal(25, viewQuery.DataFrame.GetColumn("col 1").Get(0));
            Assert.Equal("good", viewQuery.DataFrame.GetColumn("select").Get(0));

            // Verify large 64-bit integer literal > int.MaxValue parses without Int32 overflow
            var bigLit = await engine2.ExecuteAsync("SELECT 5000000000 AS big_num", ctx2);
            Assert.True(bigLit.Success, bigLit.Message);
            Assert.Equal(5000000000L, Convert.ToInt64(bigLit.DataFrame!.GetColumn("big_num").Get(0)));
        }
    }
}
