using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
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

            return _buffers.GetOrAdd(meta.TableName, _ =>
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
                return new TableBufferEntry(meta.TableName, meta.BackingFile, df);
            });
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
            if (!entry.IsDirty || string.IsNullOrEmpty(entry.BackingFile)) return;

            DataFrame snapshot;
            ulong lsn;
            using (entry.AcquireReadLock())
            {
                snapshot = entry.CurrentSnapshot;
                lsn = entry.LastAppliedLsn;
            }

            string targetPath = entry.BackingFile;
            string? dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                snapshot.WriteIpc(tmpPath);
                File.Move(tmpPath, targetPath, overwrite: true);

                using (entry.AcquireWriteLock())
                {
                    entry.IsDirty = false;
                }

                if (walWriter != null && lsn > 0)
                {
                    walWriter.UpdateCheckpointLsn(lsn);
                }
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { }
                }
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
                var entry = kvp.Value;
                if (entry.IsDirty && !string.IsNullOrEmpty(entry.BackingFile))
                {
                    DataFrame snapshot;
                    ulong lsn;
                    using (entry.AcquireReadLock())
                    {
                        snapshot = entry.CurrentSnapshot;
                        lsn = entry.LastAppliedLsn;
                    }

                    string targetPath = entry.BackingFile;
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (string.IsNullOrEmpty(dir)) continue;

                    string tmpPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
                    try
                    {
                        if (!Directory.Exists(dir))
                        {
                            Directory.CreateDirectory(dir);
                        }

                        snapshot.WriteIpc(tmpPath);

                        if (Directory.Exists(dir))
                        {
                            File.Move(tmpPath, targetPath, overwrite: true);

                            using (entry.AcquireWriteLock())
                            {
                                entry.IsDirty = false;
                            }

                            if (lsn > highestFlushedLsn)
                            {
                                highestFlushedLsn = lsn;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
                    {
                        // Target directory may have been concurrently deleted or locked during cleanup
                    }
                    finally
                    {
                        if (File.Exists(tmpPath))
                        {
                            try { File.Delete(tmpPath); } catch { }
                        }
                    }
                }
            }

            if (walWriter != null)
            {
                walWriter.AppendRecord(WalRecordType.CheckpointEnd, Guid.Empty, string.Empty, ReadOnlySpan<byte>.Empty);
                if (highestFlushedLsn > 0)
                {
                    walWriter.UpdateCheckpointLsn(highestFlushedLsn);
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
