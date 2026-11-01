using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Glacier.Sql.Catalog
{
    /// <summary>
    /// Genuine async-native, fair reader-writer lock without thread affinity or sync-over-async blocking.
    /// Ownership is tracked per <see cref="LockReleaser"/> handle rather than global boolean flags.
    /// </summary>
    public sealed class AsyncReaderWriterLock
    {
        private readonly object _stateLock = new();
        private readonly Queue<TaskCompletionSource<bool>> _writerQueue = new();
        private readonly Queue<TaskCompletionSource<bool>> _readerQueue = new();
        private readonly List<(int MinCount, TaskCompletionSource<bool> Tcs)> _readerQueuedWaiters = new();
        private int _readers;
        private bool _writeLocked;

        public bool IsWriteLocked
        {
            get { lock (_stateLock) return _writeLocked; }
        }

        public int CurrentReadCount
        {
            get { lock (_stateLock) return _readers; }
        }

        public int WaitingReadCount
        {
            get { lock (_stateLock) return _readerQueue.Count; }
        }

        public int WaitingWriteCount
        {
            get { lock (_stateLock) return _writerQueue.Count; }
        }

        public ValueTask<LockReleaser> EnterReadLockAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> tcs;
            List<TaskCompletionSource<bool>>? notifyWaiters = null;

            lock (_stateLock)
            {
                if (!_writeLocked && _writerQueue.Count == 0)
                {
                    _readers++;
                    return new ValueTask<LockReleaser>(new LockReleaser(this, isWrite: false));
                }

                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerQueue.Enqueue(tcs);

                for (int i = _readerQueuedWaiters.Count - 1; i >= 0; i--)
                {
                    if (_readerQueue.Count >= _readerQueuedWaiters[i].MinCount)
                    {
                        notifyWaiters ??= new List<TaskCompletionSource<bool>>();
                        notifyWaiters.Add(_readerQueuedWaiters[i].Tcs);
                        _readerQueuedWaiters.RemoveAt(i);
                    }
                }
            }

            if (notifyWaiters != null)
            {
                foreach (var w in notifyWaiters) w.TrySetResult(true);
            }

            return AwaitLockAsync(tcs, isWrite: false, cancellationToken);
        }

        public ValueTask<LockReleaser> EnterWriteLockAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> tcs;
            lock (_stateLock)
            {
                if (!_writeLocked && _readers == 0)
                {
                    _writeLocked = true;
                    return new ValueTask<LockReleaser>(new LockReleaser(this, isWrite: true));
                }

                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _writerQueue.Enqueue(tcs);
            }

            return AwaitLockAsync(tcs, isWrite: true, cancellationToken);
        }

        /// <summary>
        /// Upgrades a currently held read lock (identified by <paramref name="existingReadReleaser"/>) to an exclusive write lock.
        /// Atomically upgrades in-place if this caller is the sole reader; otherwise releases the read slot and queues fairly to prevent upgrade deadlocks.
        /// </summary>
        public ValueTask<LockReleaser> UpgradeReadToWriteAsync(LockReleaser existingReadReleaser, CancellationToken cancellationToken = default)
        {
            if (existingReadReleaser == null) throw new ArgumentNullException(nameof(existingReadReleaser));
            cancellationToken.ThrowIfCancellationRequested();

            TaskCompletionSource<bool>? tcs = null;
            TaskCompletionSource<bool>? writerToWake = null;

            lock (_stateLock)
            {
                if (!existingReadReleaser.TryConsumeWithoutExit())
                {
                    throw new SynchronizationLockException("The provided read lock handle has already been released.");
                }

                if (_readers <= 0)
                {
                    throw new SynchronizationLockException("Read lock is not currently held.");
                }

                if (_readers == 1 && !_writeLocked)
                {
                    _readers = 0;
                    _writeLocked = true;
                    return new ValueTask<LockReleaser>(new LockReleaser(this, isWrite: true));
                }

                // Multiple readers exist: drop our read count so concurrent upgraders never deadlock, then queue for write
                _readers--;
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _writerQueue.Enqueue(tcs);

                if (_readers == 0 && !_writeLocked && _writerQueue.Count > 0)
                {
                    _writeLocked = true;
                    writerToWake = _writerQueue.Dequeue();
                }
            }

            writerToWake?.TrySetResult(true);
            return AwaitLockAsync(tcs, isWrite: true, cancellationToken);
        }

        public Task WaitForReaderQueuedAsync(int minWaitingReaders = 1, CancellationToken cancellationToken = default)
        {
            if (minWaitingReaders <= 0) return Task.CompletedTask;

            TaskCompletionSource<bool> tcs;
            lock (_stateLock)
            {
                if (_readerQueue.Count >= minWaitingReaders)
                {
                    return Task.CompletedTask;
                }

                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerQueuedWaiters.Add((minWaitingReaders, tcs));
            }

            return cancellationToken.CanBeCanceled ? tcs.Task.WaitAsync(cancellationToken) : tcs.Task;
        }

        private async ValueTask<LockReleaser> AwaitLockAsync(TaskCompletionSource<bool> tcs, bool isWrite, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                await tcs.Task.ConfigureAwait(false);
                return new LockReleaser(this, isWrite);
            }

            using var reg = cancellationToken.Register(() =>
            {
                bool cancelled = false;
                lock (_stateLock)
                {
                    // Only cancel if the lock has not yet been granted to this waiter
                    if (!tcs.Task.IsCompleted)
                    {
                        cancelled = tcs.TrySetCanceled(cancellationToken);
                    }
                }
                _ = cancelled;
            });

            await tcs.Task.ConfigureAwait(false);
            return new LockReleaser(this, isWrite);
        }

        internal void ExitReadLock()
        {
            TaskCompletionSource<bool>? writerToWake = null;
            lock (_stateLock)
            {
                if (_readers <= 0)
                {
                    throw new SynchronizationLockException("Read lock is not currently held.");
                }

                _readers--;
                while (_readers == 0 && _writerQueue.Count > 0)
                {
                    var candidate = _writerQueue.Dequeue();
                    if (!candidate.Task.IsCanceled)
                    {
                        _writeLocked = true;
                        writerToWake = candidate;
                        break;
                    }
                }
            }

            writerToWake?.TrySetResult(true);
        }

        internal void ExitWriteLock()
        {
            List<TaskCompletionSource<bool>>? readersToWake = null;
            TaskCompletionSource<bool>? writerToWake = null;

            lock (_stateLock)
            {
                if (!_writeLocked)
                {
                    throw new SynchronizationLockException("Write lock is not currently held.");
                }

                _writeLocked = false;

                // Fair alternating batch handoff: wake waiting readers if any; otherwise wake next valid writer
                while (_readerQueue.Count > 0)
                {
                    var candidate = _readerQueue.Dequeue();
                    if (!candidate.Task.IsCanceled)
                    {
                        _readers++;
                        readersToWake ??= new List<TaskCompletionSource<bool>>();
                        readersToWake.Add(candidate);
                    }
                }

                if (_readers == 0)
                {
                    while (_writerQueue.Count > 0)
                    {
                        var candidate = _writerQueue.Dequeue();
                        if (!candidate.Task.IsCanceled)
                        {
                            _writeLocked = true;
                            writerToWake = candidate;
                            break;
                        }
                    }
                }
            }

            writerToWake?.TrySetResult(true);
            if (readersToWake != null)
            {
                foreach (var r in readersToWake) r.TrySetResult(true);
            }
        }

        public sealed class LockReleaser : IDisposable
        {
            private readonly AsyncReaderWriterLock _owner;
            private readonly bool _isWrite;
            private int _disposed;

            internal bool IsWrite => _isWrite;

            internal LockReleaser(AsyncReaderWriterLock owner, bool isWrite)
            {
                _owner = owner;
                _isWrite = isWrite;
            }

            internal bool TryConsumeWithoutExit()
            {
                return !_isWrite && Interlocked.CompareExchange(ref _disposed, 1, 0) == 0;
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                {
                    if (_isWrite)
                        _owner.ExitWriteLock();
                    else
                        _owner.ExitReadLock();
                }
            }
        }
    }
}
