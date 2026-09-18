using System;
using System.Threading;
using Glacier.Polaris;

namespace Glacier.Sql.Storage.BufferPool
{
    public sealed class TableBufferEntry : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
        private bool _disposed;

        public string TableName { get; }
        public string BackingFile { get; }
        public DataFrame CurrentSnapshot { get; set; }
        public ulong LastAppliedLsn { get; set; }
        public bool IsDirty { get; set; }

        public TableBufferEntry(string tableName, string backingFile, DataFrame initialDf, ulong lsn = 0)
        {
            TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            BackingFile = backingFile ?? string.Empty;
            CurrentSnapshot = initialDf ?? new DataFrame();
            LastAppliedLsn = lsn;
            IsDirty = false;
        }

        public IDisposable AcquireReadLock()
        {
            _rwLock.EnterReadLock();
            return new LockReleaser(_rwLock, isWrite: false);
        }

        public IDisposable AcquireWriteLock()
        {
            _rwLock.EnterWriteLock();
            return new LockReleaser(_rwLock, isWrite: true);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _rwLock.Dispose();
            }
        }

        private sealed class LockReleaser : IDisposable
        {
            private readonly ReaderWriterLockSlim _lock;
            private readonly bool _isWrite;
            private bool _released;

            public LockReleaser(ReaderWriterLockSlim rwLock, bool isWrite)
            {
                _lock = rwLock;
                _isWrite = isWrite;
            }

            public void Dispose()
            {
                if (!_released)
                {
                    _released = true;
                    if (_isWrite)
                    {
                        if (_lock.IsWriteLockHeld) _lock.ExitWriteLock();
                    }
                    else
                    {
                        if (_lock.IsReadLockHeld) _lock.ExitReadLock();
                    }
                }
            }
        }
    }
}
