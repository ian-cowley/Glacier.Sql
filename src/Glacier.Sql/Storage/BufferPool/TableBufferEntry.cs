using System;
using System.Threading;
using Glacier.Polaris;

namespace Glacier.Sql.Storage.BufferPool
{
    public sealed class TableBufferEntry : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
        private DataFrame _currentSnapshot;
        private bool _isDirty;
        private bool _disposed;

        public string TableName { get; }
        public string BackingFile { get; }

        public DataFrame CurrentSnapshot
        {
            get => _currentSnapshot;
            set
            {
                _currentSnapshot = value ?? new DataFrame();
                if (ActiveTransactionId == Guid.Empty)
                {
                    CommittedSnapshot = _currentSnapshot;
                }
            }
        }

        public DataFrame CommittedSnapshot { get; set; }
        public ulong LastAppliedLsn { get; set; }
        public ulong LastCommittedLsn { get; set; }
        public ulong FirstUnflushedLsn { get; set; }
        public ulong FirstUncommittedTxLsn { get; set; }
        public bool HasUnflushedCommittedChanges { get; set; }
        public Guid ActiveTransactionId { get; set; }

        public bool IsDirty
        {
            get => _isDirty;
            set
            {
                _isDirty = value;
                if (value && ActiveTransactionId == Guid.Empty)
                {
                    HasUnflushedCommittedChanges = true;
                }
                else if (!value && ActiveTransactionId == Guid.Empty)
                {
                    HasUnflushedCommittedChanges = false;
                    FirstUnflushedLsn = 0;
                }
            }
        }

        public TableBufferEntry(string tableName, string backingFile, DataFrame initialDf, ulong lsn = 0)
        {
            TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            BackingFile = backingFile ?? string.Empty;
            var snapshot = initialDf ?? new DataFrame();
            _currentSnapshot = snapshot;
            CommittedSnapshot = snapshot;
            LastAppliedLsn = lsn;
            LastCommittedLsn = lsn;
            FirstUnflushedLsn = 0;
            FirstUncommittedTxLsn = 0;
            HasUnflushedCommittedChanges = false;
            ActiveTransactionId = Guid.Empty;
            _isDirty = false;
        }

        public void ApplyMutation(DataFrame newSnapshot, ulong lsn, Guid txId)
        {
            if (txId == Guid.Empty)
            {
                _currentSnapshot = newSnapshot;
                CommittedSnapshot = newSnapshot;
                LastAppliedLsn = lsn;
                LastCommittedLsn = lsn;
                if (FirstUnflushedLsn == 0 && lsn > 0)
                {
                    FirstUnflushedLsn = lsn;
                }
                HasUnflushedCommittedChanges = true;
                _isDirty = true;
            }
            else
            {
                ActiveTransactionId = txId;
                _currentSnapshot = newSnapshot;
                LastAppliedLsn = lsn;
                if (FirstUncommittedTxLsn == 0 && lsn > 0)
                {
                    FirstUncommittedTxLsn = lsn;
                }
                if (FirstUnflushedLsn == 0 && lsn > 0)
                {
                    FirstUnflushedLsn = lsn;
                }
                _isDirty = true;
            }
        }

        public void CommitTransaction(Guid txId, ulong commitLsn)
        {
            using (AcquireWriteLock())
            {
                if (ActiveTransactionId == txId)
                {
                    CommittedSnapshot = _currentSnapshot;
                    ulong effectiveLsn = Math.Max(LastAppliedLsn, commitLsn);
                    LastCommittedLsn = effectiveLsn;
                    LastAppliedLsn = effectiveLsn;
                    if (FirstUnflushedLsn == 0 && FirstUncommittedTxLsn > 0)
                    {
                        FirstUnflushedLsn = FirstUncommittedTxLsn;
                    }
                    FirstUncommittedTxLsn = 0;
                    ActiveTransactionId = Guid.Empty;
                    HasUnflushedCommittedChanges = true;
                    _isDirty = true;
                }
            }
        }

        public void RollbackTransaction(Guid txId)
        {
            using (AcquireWriteLock())
            {
                if (ActiveTransactionId == txId)
                {
                    _currentSnapshot = CommittedSnapshot;
                    LastAppliedLsn = LastCommittedLsn;
                    ActiveTransactionId = Guid.Empty;
                    FirstUncommittedTxLsn = 0;
                    if (!HasUnflushedCommittedChanges)
                    {
                        FirstUnflushedLsn = 0;
                    }
                    _isDirty = HasUnflushedCommittedChanges;
                }
            }
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
            private int _released;

            public LockReleaser(ReaderWriterLockSlim rwLock, bool isWrite)
            {
                _lock = rwLock;
                _isWrite = isWrite;
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _released, 1, 0) == 0)
                {
                    if (_isWrite)
                        _lock.ExitWriteLock();
                    else
                        _lock.ExitReadLock();
                }
            }
        }
    }
}
