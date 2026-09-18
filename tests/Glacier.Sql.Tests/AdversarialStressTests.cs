namespace Glacier.Sql.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Storage.Wal;
using Xunit;
using ExecutionContext = Glacier.Sql.Engine.ExecutionContext;

public class AdversarialStressTests : IDisposable
{
    private readonly string _testDir;

    public AdversarialStressTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"glacier_sql_stress_{Guid.NewGuid():N}");
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
    public async Task WalCrashRecovery_SimulatePowerCutMidTxAndTornTail_RestoresDatabaseConsistency()
    {
        // Phase 1: Initialize DB, create table, insert base data and checkpoint
        using (var catalog1 = new CatalogManager(_testDir))
        {
            var engine1 = new SqlEngine(catalog1);
            var context1 = new ExecutionContext(catalog1);

            await engine1.ExecuteAsync("CREATE TABLE accounts (id INT, balance DOUBLE, name VARCHAR)", context1);
            await engine1.ExecuteAsync("INSERT INTO accounts VALUES (1, 100.0, 'Alice')", context1);
            await engine1.ExecuteAsync("INSERT INTO accounts VALUES (2, 200.0, 'Bob')", context1);
            await engine1.CheckpointAsync();

            // Transaction 1: Committed transaction
            await engine1.ExecuteAsync("BEGIN TRANSACTION", context1);
            await engine1.ExecuteAsync("INSERT INTO accounts VALUES (3, 300.0, 'Charlie')", context1);
            await engine1.ExecuteAsync("COMMIT", context1);

            // Transaction 2: Mid-transaction crash simulation (uncommitted)
            await engine1.ExecuteAsync("BEGIN TRANSACTION", context1);
            await engine1.ExecuteAsync("INSERT INTO accounts VALUES (4, 400.0, 'Dave_Uncommitted')", context1);
            await engine1.ExecuteAsync("INSERT INTO accounts VALUES (5, 500.0, 'Eve_Uncommitted')", context1);
            // Sudden power cut: process halts without COMMIT or checkpoint!
        }

        // Phase 2: Simulate torn write at EOF of glacier.wal (partial corrupted frame)
        string walPath = Path.Combine(_testDir, "glacier.wal");
        Assert.True(File.Exists(walPath), "glacier.wal must exist");

        // Append 17 bytes of corrupted garbage simulating a torn sector write during sudden power loss
        using (var fs = new FileStream(walPath, FileMode.Append, FileAccess.Write))
        {
            byte[] tornBytes = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x01, 0x02, 0x03, 0x04, 0x05 };
            fs.Write(tornBytes, 0, tornBytes.Length);
            fs.Flush(true);
        }

        // Phase 3: Spin up a fresh CatalogManager / SqlEngine on the crashed directory
        using (var catalog2 = new CatalogManager(_testDir))
        {
            var engine2 = new SqlEngine(catalog2);
            var context2 = new ExecutionContext(catalog2);

            var result = await engine2.ExecuteAsync("SELECT * FROM accounts ORDER BY id ASC", context2);

            Assert.True(result.Success);
            Assert.NotNull(result.DataFrame);
            var df = result.DataFrame;
            Assert.Equal(3, df.RowCount); // Rows 1, 2, and committed 3

            // Verify row contents
            var ids = new HashSet<int>();
            for (int i = 0; i < df.RowCount; i++)
            {
                ids.Add(Convert.ToInt32(df.GetColumn("id").Get(i)));
            }

            Assert.Contains(1, ids);
            Assert.Contains(2, ids);
            Assert.Contains(3, ids);

            // Assert uncommitted rows from Transaction 2 were not applied
            Assert.DoesNotContain(4, ids);
            Assert.DoesNotContain(5, ids);
        }
    }

    [Fact]
    public async Task TableBufferPool_ConcurrentMultiThreadedInsertAndSelect_ThreadSafeAndConsistent()
    {
        using var catalog = new CatalogManager(_testDir);
        var engine = new SqlEngine(catalog);
        var setupContext = new ExecutionContext(catalog);
        await engine.ExecuteAsync("CREATE TABLE telemetry (sensor_id INT, reading DOUBLE)", setupContext);

        const int writerCount = 4;
        const int readerCount = 4;
        const int insertsPerWriter = 40;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cts.Token;
        int completedReads = 0;
        Exception? error = null;

        var readers = Enumerable.Range(0, readerCount).Select(r => Task.Run(async () =>
        {
            var ctx = new ExecutionContext(catalog);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var res = await engine.ExecuteAsync("SELECT * FROM telemetry", ctx);
                    Assert.True(res.Success);
                    Assert.NotNull(res.DataFrame);
                    Interlocked.Increment(ref completedReads);
                    await Task.Delay(5);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref error, ex);
                    break;
                }
            }
        })).ToArray();

        var writers = Enumerable.Range(0, writerCount).Select(w => Task.Run(async () =>
        {
            var ctx = new ExecutionContext(catalog);
            for (int i = 0; i < insertsPerWriter && !token.IsCancellationRequested; i++)
            {
                try
                {
                    var res = await engine.ExecuteAsync($"INSERT INTO telemetry VALUES ({w}, {i * 1.5})", ctx);
                    Assert.True(res.Success);
                    await Task.Delay(2);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref error, ex);
                    break;
                }
            }
        })).ToArray();

        await Task.WhenAll(writers);
        cts.Cancel();
        await Task.WhenAll(readers);

        Assert.Null(error);
        Assert.True(completedReads > 5);

        // Verify total row count matches expected inserts
        var verifyCtx = new ExecutionContext(catalog);
        var finalResult = await engine.ExecuteAsync("SELECT * FROM telemetry", verifyCtx);
        Assert.True(finalResult.Success);
        Assert.Equal(writerCount * insertsPerWriter, finalResult.DataFrame!.RowCount);

        // Verify checkpointing flushes in-memory buffer pool cleanly to disk
        await engine.CheckpointAsync();
        var postCheckpointResult = await engine.ExecuteAsync("SELECT * FROM telemetry", verifyCtx);
        Assert.True(postCheckpointResult.Success);
        Assert.Equal(writerCount * insertsPerWriter, postCheckpointResult.DataFrame!.RowCount);
    }
}
