using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Engine;
using Glacier.Sql.Storage.BufferPool;
using Glacier.Sql.Storage.Wal;
using Xunit;
using ExecutionContext = Glacier.Sql.Engine.ExecutionContext;

namespace Glacier.Sql.Tests
{
    public class WalAndBufferPoolTests
    {
        [Fact]
        public void WalWriterAndReader_BinaryFrameRoundtrip_ValidatesCrcAndMonotonicLsn()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "wal_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string walPath = Path.Combine(tempDir, "test.wal");

            try
            {
                var txId = Guid.NewGuid();
                byte[] payload1 = Encoding.UTF8.GetBytes("INSERT INTO test VALUES (1, 'Alpha')");
                byte[] payload2 = Encoding.UTF8.GetBytes("UPDATE test SET val = 'Beta' WHERE id = 1");

                using (var writer = new WalWriter(walPath))
                {
                    Assert.Equal(0u, writer.CheckpointLsn);
                    ulong lsn1 = writer.AppendRecord(WalRecordType.TxBegin, txId, string.Empty, ReadOnlySpan<byte>.Empty);
                    ulong lsn2 = writer.AppendRecord(WalRecordType.RowInsert, txId, "test", payload1);
                    ulong lsn3 = writer.AppendRecord(WalRecordType.RowUpdate, txId, "test", payload2);
                    ulong lsn4 = writer.AppendRecord(WalRecordType.TxCommit, txId, string.Empty, ReadOnlySpan<byte>.Empty);

                    Assert.Equal(1u, lsn1);
                    Assert.Equal(2u, lsn2);
                    Assert.Equal(3u, lsn3);
                    Assert.Equal(4u, lsn4);
                }

                // Read records back with WalReader
                var records = WalReader.Read(walPath, out ulong checkpointLsn);
                Assert.Equal(0u, checkpointLsn);
                Assert.Equal(4, records.Count);

                Assert.Equal(WalRecordType.TxBegin, records[0].Type);
                Assert.Equal(txId, records[0].TxId);

                Assert.Equal(WalRecordType.RowInsert, records[1].Type);
                Assert.Equal("test", records[1].TableName);
                Assert.Equal("INSERT INTO test VALUES (1, 'Alpha')", Encoding.UTF8.GetString(records[1].Payload));

                Assert.Equal(WalRecordType.RowUpdate, records[2].Type);
                Assert.Equal("test", records[2].TableName);
                Assert.Equal("UPDATE test SET val = 'Beta' WHERE id = 1", Encoding.UTF8.GetString(records[2].Payload));

                Assert.Equal(WalRecordType.TxCommit, records[3].Type);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public void WalReader_CorruptedFrame_StopsCleanlyAtCorruption()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "wal_corrupt_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string walPath = Path.Combine(tempDir, "corrupted.wal");

            try
            {
                using (var writer = new WalWriter(walPath))
                {
                    writer.AppendRecord(WalRecordType.RowInsert, Guid.Empty, "tbl1", Encoding.UTF8.GetBytes("val1"));
                    writer.AppendRecord(WalRecordType.RowInsert, Guid.Empty, "tbl1", Encoding.UTF8.GetBytes("val2"));
                }

                // Corrupt file by appending partial garbage bytes
                using (var fs = new FileStream(walPath, FileMode.Append, FileAccess.Write))
                {
                    fs.Write(new byte[] { 0xAA, 0xBB, 0xCC }); // Corrupted frame header
                }

                var records = WalReader.Read(walPath, out _);
                // Should gracefully parse the 2 valid frames and halt at corruption
                Assert.Equal(2, records.Count);
                Assert.Equal("val1", Encoding.UTF8.GetString(records[0].Payload));
                Assert.Equal("val2", Encoding.UTF8.GetString(records[1].Payload));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task TableBufferPool_ReaderWriterLockSlim_SupportsConcurrentReadsAndExclusiveWrites()
        {
            var df = new DataFrame(new ISeries[]
            {
                new Int32Series("id", 10),
                new Utf8StringSeries("val", Enumerable.Repeat("item", 10).ToArray())
            });

            using var entry = new TableBufferEntry("concurrent_test", "", df);

            int readSuccessCount = 0;
            var readTasks = new List<Task>();

            // Spin up 10 concurrent readers
            for (int i = 0; i < 10; i++)
            {
                readTasks.Add(Task.Run(() =>
                {
                    for (int j = 0; j < 50; j++)
                    {
                        using (entry.AcquireReadLock())
                        {
                            var snapshot = entry.CurrentSnapshot;
                            if (snapshot.RowCount >= 10)
                            {
                                Interlocked.Increment(ref readSuccessCount);
                            }
                        }
                    }
                }));
            }

            // Spin up a writer that updates snapshot
            var writeTask = Task.Run(() =>
            {
                for (int j = 0; j < 20; j++)
                {
                    using (entry.AcquireWriteLock())
                    {
                        var col1 = new Int32Series("id", 1);
                        col1.Memory.Span[0] = 100 + j;
                        var col2 = new Utf8StringSeries("val", new[] { $"mutated_{j}" });
                        var extraDf = new DataFrame(new ISeries[] { col1, col2 });
                        entry.CurrentSnapshot = DataFrame.Concat(new[] { entry.CurrentSnapshot, extraDf });
                        entry.IsDirty = true;
                    }
                }
            });

            await Task.WhenAll(readTasks.Concat(new[] { writeTask }));
            Assert.True(readSuccessCount > 0);
            Assert.Equal(30, entry.CurrentSnapshot.RowCount);
        }

        [Fact]
        public async Task TableBufferPool_EliminatesFileRewritesOnMutations()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "no_rewrite_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            try
            {
                using var catalog = new CatalogManager(dataDir);
                var engine = new SqlEngine(catalog);
                var context = new ExecutionContext(catalog);

                await engine.ExecuteAsync("CREATE TABLE test_perf (id INT, val VARCHAR)", context);
                var meta = catalog.GetTable("test_perf")!;

                // Capture initial state of disk file after CREATE TABLE
                Assert.True(File.Exists(meta.BackingFile));
                DateTime initialWriteTime = File.GetLastWriteTimeUtc(meta.BackingFile);

                // Wait slightly to ensure any potential subsequent disk write has a distinguishable timestamp
                await Task.Delay(50);

                // Perform multiple INSERTs in memory
                await engine.ExecuteAsync("INSERT INTO test_perf VALUES (1, 'row1')", context);
                await engine.ExecuteAsync("INSERT INTO test_perf VALUES (2, 'row2')", context);
                await engine.ExecuteAsync("INSERT INTO test_perf VALUES (3, 'row3')", context);

                // Verify rows are immediately queryable via memory buffer pool
                var selectRes = await engine.ExecuteAsync("SELECT * FROM test_perf ORDER BY id ASC", context);
                Assert.True(selectRes.Success);
                Assert.Equal(3, selectRes.DataFrame!.RowCount);

                // Verify base disk file timestamp has NOT changed (O(N) file rewrite was eliminated!)
                DateTime postInsertWriteTime = File.GetLastWriteTimeUtc(meta.BackingFile);
                Assert.Equal(initialWriteTime, postInsertWriteTime);

                // Verify WAL captured the mutation records
                string walPath = Path.Combine(dataDir, "glacier.wal");
                Assert.True(File.Exists(walPath));
                var walRecords = WalReader.Read(walPath, out _);
                Assert.Equal(3, walRecords.Count(r => r.Type == WalRecordType.RowInsert));
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task TableBufferPool_AtomicCheckpoint_FlushesMemoryToDisk()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "checkpoint_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            try
            {
                using var catalog = new CatalogManager(dataDir);
                var engine = new SqlEngine(catalog);
                var context = new ExecutionContext(catalog);

                await engine.ExecuteAsync("CREATE TABLE ck_test (id INT, val VARCHAR)", context);
                var meta = catalog.GetTable("ck_test")!;

                await engine.ExecuteAsync("INSERT INTO ck_test VALUES (1, 'persisted')", context);

                var bufferEntry = catalog.BufferPool.GetOrLoad(meta);
                Assert.True(bufferEntry.IsDirty, "Buffer entry should be dirty before checkpoint.");

                // Trigger checkpoint
                await engine.CheckpointAsync();

                Assert.False(bufferEntry.IsDirty, "Buffer entry should not be dirty after checkpoint.");

                // Verify temporary files are cleaned up
                var tmpFiles = Directory.GetFiles(dataDir, "*.tmp.*");
                Assert.Empty(tmpFiles);

                // Read directly from physical file to verify atomic flush
                var diskDf = TableStorage.ReadFromFile(meta.BackingFile);
                Assert.Equal(1, diskDf.RowCount);
                Assert.Equal(1, diskDf.GetColumn("id").Get(0));
                Assert.Equal("persisted", diskDf.GetColumn("val").Get(0));
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task WalCrashRecovery_ReplaysCommittedMutationsAccurately()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "recovery_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            try
            {
                // Phase 1: Create table and perform mutations (without checkpointing to .ipc)
                using (var catalog1 = new CatalogManager(dataDir))
                {
                    var engine1 = new SqlEngine(catalog1);
                    var context1 = new ExecutionContext(catalog1);

                    await engine1.ExecuteAsync("CREATE TABLE recovered_table (id INT, val VARCHAR)", context1);
                    await engine1.ExecuteAsync("INSERT INTO recovered_table VALUES (10, 'Initial')", context1);

                    // Checkpoint initial row to base .ipc
                    await engine1.CheckpointAsync();

                    // Now insert further rows that stay in memory + WAL
                    await engine1.ExecuteAsync("INSERT INTO recovered_table VALUES (20, 'Uncheckpointed')", context1);
                    await engine1.ExecuteAsync("INSERT INTO recovered_table VALUES (30, 'CrashSurviving')", context1);
                    await engine1.ExecuteAsync("UPDATE recovered_table SET val = 'UpdatedInitial' WHERE id = 10", context1);
                }

                // Phase 2: Simulate crash and restart by loading a fresh CatalogManager
                using (var catalog2 = new CatalogManager(dataDir))
                {
                    var engine2 = new SqlEngine(catalog2);
                    var context2 = new ExecutionContext(catalog2);

                    var res = await engine2.ExecuteAsync("SELECT * FROM recovered_table ORDER BY id ASC", context2);
                    Assert.True(res.Success);

                    var df = res.DataFrame!;
                    Assert.Equal(3, df.RowCount);

                    Assert.Equal(10, df.GetColumn("id").Get(0));
                    Assert.Equal("UpdatedInitial", df.GetColumn("val").Get(0));

                    Assert.Equal(20, df.GetColumn("id").Get(1));
                    Assert.Equal("Uncheckpointed", df.GetColumn("val").Get(1));

                    Assert.Equal(30, df.GetColumn("id").Get(2));
                    Assert.Equal("CrashSurviving", df.GetColumn("val").Get(2));
                }
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                }
            }
        }

        [Fact]
        public async Task WalCrashRecovery_SkipsUncommittedTransactions()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "uncommitted_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);

            try
            {
                // Phase 1: Start a transaction, insert rows, but crash before COMMIT
                using (var catalog1 = new CatalogManager(dataDir))
                {
                    var engine1 = new SqlEngine(catalog1);
                    var context1 = new ExecutionContext(catalog1);

                    await engine1.ExecuteAsync("CREATE TABLE tx_abort_table (id INT, val VARCHAR)", context1);
                    await engine1.ExecuteAsync("INSERT INTO tx_abort_table VALUES (1, 'CommittedInitial')", context1);
                    await engine1.CheckpointAsync();

                    // Begin multi-statement transaction
                    await engine1.ExecuteAsync("BEGIN TRANSACTION", context1);
                    await engine1.ExecuteAsync("INSERT INTO tx_abort_table VALUES (2, 'UncommittedGhost')", context1);
                    await engine1.ExecuteAsync("INSERT INTO tx_abort_table VALUES (3, 'UncommittedGhost2')", context1);

                    // Simulated sudden power outage: no commit, no checkpoint, process terminates!
                }

                // Phase 2: Recover on restart
                using (var catalog2 = new CatalogManager(dataDir))
                {
                    var engine2 = new SqlEngine(catalog2);
                    var context2 = new ExecutionContext(catalog2);

                    var res = await engine2.ExecuteAsync("SELECT * FROM tx_abort_table ORDER BY id ASC", context2);
                    Assert.True(res.Success);

                    var df = res.DataFrame!;
                    Assert.Equal(1, df.RowCount);
                    Assert.Equal(1, df.GetColumn("id").Get(0));
                    Assert.Equal("CommittedInitial", df.GetColumn("val").Get(0));
                }
            }
            finally
            {
                if (Directory.Exists(dataDir))
                {
                    try { Directory.Delete(dataDir, true); } catch { }
                }
            }
        }
    }
}
