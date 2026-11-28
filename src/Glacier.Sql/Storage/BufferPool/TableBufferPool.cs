using System;
using System.Collections.Concurrent;
using System.IO;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Storage.BufferPool
{
    public sealed class TableBufferPool : IDisposable
    {
        private readonly ConcurrentDictionary<string, TableBufferEntry> _buffers = new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public TableBufferEntry GetOrLoad(TableMetadata meta)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TableBufferPool));
            if (meta == null) throw new ArgumentNullException(nameof(meta));

            var entry = _buffers.GetOrAdd(meta.TableName, _ =>
            {
                DataFrame df;
                if (File.Exists(meta.BackingFile))
                {
                    df = TableStorage.ReadFromFile(meta.BackingFile);
                }
                else
                {
                    df = TableStorage.CreateEmptyDataFrame(meta.Columns);
                }
                return new TableBufferEntry(meta.TableName, meta.BackingFile, df, tableId: meta.TableId);
            });

            if (entry.TableId == Guid.Empty && meta.TableId != Guid.Empty)
            {
                entry.TableId = meta.TableId;
            }
            return entry;
        }

        public TableBufferEntry GetOrLoad(string tableName, string backingFile, Func<DataFrame>? loader = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TableBufferPool));
            if (string.IsNullOrEmpty(tableName)) throw new ArgumentNullException(nameof(tableName));

            return _buffers.GetOrAdd(tableName, _ =>
            {
                DataFrame df;
                if (loader != null)
                {
                    df = loader();
                }
                else if (!string.IsNullOrEmpty(backingFile) && File.Exists(backingFile))
                {
                    df = TableStorage.ReadFromFile(backingFile);
                }
                else
                {
                    df = new DataFrame();
                }
                return new TableBufferEntry(tableName, backingFile, df);
            });
        }

        internal void AttachEntry(TableBufferEntry entry)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(TableBufferPool));
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            _buffers[entry.TableName] = entry;
        }

        internal bool DetachEntry(string tableName, out TableBufferEntry? entry)
        {
            return _buffers.TryRemove(tableName, out entry);
        }

        public bool TryGet(string tableName, out TableBufferEntry? entry)
        {
            return _buffers.TryGetValue(tableName, out entry);
        }

        public bool TryGetByFile(string filePath, out TableBufferEntry? entry)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                entry = null;
                return false;
            }

            foreach (var kvp in _buffers)
            {
                if (string.Equals(kvp.Value.BackingFile, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    entry = kvp.Value;
                    return true;
                }
            }

            entry = null;
            return false;
        }

        public void Invalidate(string tableName)
        {
            if (_buffers.TryRemove(tableName, out var entry))
            {
                entry.Dispose();
            }
        }

        public void CheckpointTable(string tableName, WalWriter? walWriter = null)
        {
            if (!_buffers.TryGetValue(tableName, out var entry)) return;

            ulong flushedLsn = FlushCommittedEntry(entry, walWriter);
            if (walWriter != null && flushedLsn > 0)
            {
                AdvanceSafeCheckpointLsn(walWriter, flushedLsn);
            }
        }

        public void CheckpointAll(WalWriter? walWriter = null)
        {
            ulong highestFlushedLsn = 0;

            if (walWriter != null)
            {
                walWriter.AppendRecord(WalRecordType.CheckpointStart, Guid.Empty, string.Empty, ReadOnlySpan<byte>.Empty);
            }

            foreach (var kvp in _buffers)
            {
                ulong flushedLsn = FlushCommittedEntry(kvp.Value, walWriter);
                if (flushedLsn > highestFlushedLsn)
                {
                    highestFlushedLsn = flushedLsn;
                }
            }

            if (walWriter != null)
            {
                walWriter.AppendRecord(WalRecordType.CheckpointEnd, Guid.Empty, string.Empty, ReadOnlySpan<byte>.Empty);
                ulong maxCandidate = highestFlushedLsn > 0 ? highestFlushedLsn : walWriter.CurrentLsn;
                bool hasPending = AdvanceSafeCheckpointLsn(walWriter, maxCandidate);
                if (!hasPending)
                {
                    walWriter.Truncate(Math.Max(walWriter.CheckpointLsn, walWriter.CurrentLsn));
                }
            }
        }

        internal bool AdvanceSafeCheckpointLsn(WalWriter walWriter, ulong maxCandidateLsn)
        {
            ulong minPendingLsn = ulong.MaxValue;

            foreach (var kvp in _buffers)
            {
                var entry = kvp.Value;
                using (entry.AcquireReadLock())
                {
                    if (entry.HasUnflushedCommittedChanges || entry.ActiveTransactionId != Guid.Empty)
                    {
                        ulong pending = entry.FirstUnflushedLsn > 0
                            ? entry.FirstUnflushedLsn
                            : (entry.FirstUncommittedTxLsn > 0 ? entry.FirstUncommittedTxLsn : 1UL);

                        if (pending < minPendingLsn)
                        {
                            minPendingLsn = pending;
                        }
                    }
                }
            }

            bool hasPending = minPendingLsn != ulong.MaxValue;
            ulong safeLsn = !hasPending
                ? maxCandidateLsn
                : Math.Min(maxCandidateLsn, minPendingLsn - 1);

            if (safeLsn > walWriter.CheckpointLsn)
            {
                walWriter.UpdateCheckpointLsn(safeLsn);
            }

            return hasPending;
        }

        private static ulong FlushCommittedEntry(TableBufferEntry entry, WalWriter? walWriter)
        {
            DataFrame snapshot;
            ulong lsn;
            string targetPath;
            Guid tableId;

            using (entry.AcquireReadLock())
            {
                if (!entry.HasUnflushedCommittedChanges || string.IsNullOrEmpty(entry.BackingFile))
                {
                    return 0;
                }

                snapshot = entry.CommittedSnapshot;
                lsn = entry.LastCommittedLsn;
                targetPath = entry.BackingFile;
                tableId = entry.TableId;
            }

            string? dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrEmpty(dir)) return 0;

            string tmpPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                snapshot.WriteIpc(tmpPath);
                File.Move(tmpPath, targetPath, overwrite: true);

                using (entry.AcquireWriteLock())
                {
                    if (entry.LastCommittedLsn == lsn)
                    {
                        entry.HasUnflushedCommittedChanges = false;
                        if (entry.ActiveTransactionId != Guid.Empty)
                        {
                            entry.FirstUnflushedLsn = entry.FirstUncommittedTxLsn;
                        }
                        else
                        {
                            entry.FirstUnflushedLsn = 0;
                            entry.IsDirty = false;
                        }
                    }
                }

                if (walWriter != null && lsn > 0)
                {
                    Span<byte> lsnBytes = stackalloc byte[8];
                    BitConverter.TryWriteBytes(lsnBytes, lsn);
                    walWriter.AppendRecord(WalRecordType.CheckpointEnd, Guid.Empty, tableId, entry.TableName, lsnBytes);
                }

                return lsn;
            }
            catch (Exception ex)
            {
                GlacierSqlDiagnostics.LogError($"Failed to flush table '{entry.TableName}' to '{targetPath}': {ex.Message}", ex);
                throw;
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        GlacierSqlDiagnostics.LogError($"Failed to clean temp file '{tmpPath}': {ex.Message}", ex);
                    }
                }
            }
        }

        public void Clear()
        {
            foreach (var kvp in _buffers)
            {
                kvp.Value.Dispose();
            }
            _buffers.Clear();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Clear();
            }
        }
    }
}
