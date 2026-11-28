using System;
using System.Collections.Generic;
using System.Threading;
using Glacier.Polaris;

namespace Glacier.Sql.Storage.BufferPool
{
    public sealed class TableBufferEntry : IDisposable
    {
        private const int DeltaCompactionThreshold = 128;

        private readonly ReaderWriterLockSlim _rwLock = new(LockRecursionPolicy.NoRecursion);
        private readonly object _mergeLock = new();

        private DataFrame _currentBase;
        private readonly List<DataFrame> _currentDeltas = new();
        private DataFrame? _currentCached;
        private int _currentRowCount;

        private DataFrame _committedBase;
        private readonly List<DataFrame> _committedDeltas = new();
        private DataFrame? _committedCached;

        private bool _isDirty;
        private bool _disposed;

        public string TableName { get; }
        public string BackingFile { get; }
        public Guid TableId { get; set; }

        public int RowCount
        {
            get
            {
                lock (_mergeLock)
                {
                    return _currentRowCount;
                }
            }
        }

        public DataFrame CurrentSnapshot
        {
            get
            {
                lock (_mergeLock)
                {
                    if (_currentDeltas.Count == 0)
                    {
                        return _currentBase;
                    }

                    if (_currentCached == null)
                    {
                        var all = new List<DataFrame>(1 + _currentDeltas.Count) { _currentBase };
                        all.AddRange(_currentDeltas);
                        _currentCached = DataFrame.Concat(all);
                        _currentBase = _currentCached;
                        _currentDeltas.Clear();
                        _currentRowCount = _currentBase.RowCount;
                    }

                    return _currentCached;
                }
            }
            set
            {
                var snap = value ?? new DataFrame();
                lock (_mergeLock)
                {
                    _currentBase = snap;
                    _currentDeltas.Clear();
                    _currentCached = snap;
                    _currentRowCount = snap.RowCount;

                    if (ActiveTransactionId == Guid.Empty)
                    {
                        _committedBase = snap;
                        _committedDeltas.Clear();
                        _committedCached = snap;
                    }
                }
            }
        }

        public DataFrame CommittedSnapshot
        {
            get
            {
                lock (_mergeLock)
                {
                    if (_committedDeltas.Count == 0)
                    {
                        return _committedBase;
                    }

                    if (_committedCached == null)
                    {
                        var all = new List<DataFrame>(1 + _committedDeltas.Count) { _committedBase };
                        all.AddRange(_committedDeltas);
                        _committedCached = DataFrame.Concat(all);
                        _committedBase = _committedCached;
                        _committedDeltas.Clear();
                    }

                    return _committedCached;
                }
            }
            set
            {
                var snap = value ?? new DataFrame();
                lock (_mergeLock)
                {
                    _committedBase = snap;
                    _committedDeltas.Clear();
                    _committedCached = snap;
                }
            }
        }

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

        public TableBufferEntry(string tableName, string backingFile, DataFrame initialDf, ulong lsn = 0, Guid tableId = default)
        {
            TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
            BackingFile = backingFile ?? string.Empty;
            TableId = tableId;
            var snapshot = initialDf ?? new DataFrame();
            _currentBase = snapshot;
            _currentCached = snapshot;
            _currentRowCount = snapshot.RowCount;
            _committedBase = snapshot;
            _committedCached = snapshot;
            LastAppliedLsn = lsn;
            LastCommittedLsn = lsn;
            FirstUnflushedLsn = 0;
            FirstUncommittedTxLsn = 0;
            HasUnflushedCommittedChanges = false;
            ActiveTransactionId = Guid.Empty;
            _isDirty = false;
        }

        internal void GetCurrentChunks(List<DataFrame> target)
        {
            lock (_mergeLock)
            {
                target.Add(_currentBase);
                if (_currentDeltas.Count > 0)
                {
                    target.AddRange(_currentDeltas);
                }
            }
        }

        public void ApplyAppend(DataFrame deltaDf, ulong lsn, Guid txId)
        {
            if (deltaDf == null) throw new ArgumentNullException(nameof(deltaDf));

            lock (_mergeLock)
            {
                if (deltaDf.RowCount > 0)
                {
                    _currentDeltas.Add(deltaDf);
                    _currentCached = null;
                    _currentRowCount += deltaDf.RowCount;

                    if (_currentDeltas.Count >= DeltaCompactionThreshold)
                    {
                        var all = new List<DataFrame>(1 + _currentDeltas.Count) { _currentBase };
                        all.AddRange(_currentDeltas);
                        _currentBase = DataFrame.Concat(all);
                        _currentDeltas.Clear();
                        _currentCached = _currentBase;
                        _currentRowCount = _currentBase.RowCount;
                    }
                }

                if (txId == Guid.Empty)
                {
                    if (deltaDf.RowCount > 0)
                    {
                        _committedDeltas.Add(deltaDf);
                        _committedCached = null;

                        if (_committedDeltas.Count >= DeltaCompactionThreshold)
                        {
                            var all = new List<DataFrame>(1 + _committedDeltas.Count) { _committedBase };
                            all.AddRange(_committedDeltas);
                            _committedBase = DataFrame.Concat(all);
                            _committedDeltas.Clear();
                            _committedCached = _committedBase;
                        }
                    }

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
        }

        public void ApplyMutation(DataFrame newSnapshot, ulong lsn, Guid txId)
        {
            var snap = newSnapshot ?? new DataFrame();
            lock (_mergeLock)
            {
                _currentBase = snap;
                _currentDeltas.Clear();
                _currentCached = snap;
                _currentRowCount = snap.RowCount;

                if (txId == Guid.Empty)
                {
                    _committedBase = snap;
                    _committedDeltas.Clear();
                    _committedCached = snap;
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
        }

        public void CommitTransaction(Guid txId, ulong commitLsn)
        {
            using (AcquireWriteLock())
            {
                if (ActiveTransactionId == txId)
                {
                    lock (_mergeLock)
                    {
                        _committedBase = _currentBase;
                        _committedDeltas.Clear();
                        _committedDeltas.AddRange(_currentDeltas);
                        _committedCached = _currentCached;
                    }

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
                    lock (_mergeLock)
                    {
                        _currentBase = _committedBase;
                        _currentDeltas.Clear();
                        _currentDeltas.AddRange(_committedDeltas);
                        _currentCached = _committedCached;

                        int count = _currentBase.RowCount;
                        for (int i = 0; i < _currentDeltas.Count; i++)
                        {
                            count += _currentDeltas[i].RowCount;
                        }
                        _currentRowCount = count;
                    }

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
