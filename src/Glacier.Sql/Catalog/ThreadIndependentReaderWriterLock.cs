using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Glacier.Sql.Catalog
{
    public class ThreadIndependentReaderWriterLock : System.Threading.ReaderWriterLockSlim
    {
        private int _readers = 0;
        private bool _writeLocked = false;
        private readonly Queue<TaskCompletionSource<bool>> _writerQueue = new();
        private readonly Queue<TaskCompletionSource<bool>> _readerQueue = new();
        private readonly object _stateLock = new();

        public new void EnterReadLock()
        {
            TaskCompletionSource<bool>? tcs = null;
            lock (_stateLock)
            {
                if (!_writeLocked && _writerQueue.Count == 0)
                {
                    _readers++;
                    return;
                }
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _readerQueue.Enqueue(tcs);
            }
            tcs.Task.Wait();
        }

        public new void ExitReadLock()
        {
            TaskCompletionSource<bool>? writerToWake = null;
            lock (_stateLock)
            {
                _readers--;
                if (_readers == 0 && _writerQueue.Count > 0)
                {
                    _writeLocked = true;
                    writerToWake = _writerQueue.Dequeue();
                }
            }
            writerToWake?.TrySetResult(true);
        }

        public new void EnterWriteLock()
        {
            TaskCompletionSource<bool>? tcs = null;
            lock (_stateLock)
            {
                if (!_writeLocked && _readers == 0)
                {
                    _writeLocked = true;
                    return;
                }
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _writerQueue.Enqueue(tcs);
            }
            tcs.Task.Wait();
        }

        public new void ExitWriteLock()
        {
            List<TaskCompletionSource<bool>> readersToWake = new();
            TaskCompletionSource<bool>? writerToWake = null;
            lock (_stateLock)
            {
                _writeLocked = false;
                if (_writerQueue.Count > 0)
                {
                    _writeLocked = true;
                    writerToWake = _writerQueue.Dequeue();
                }
                else
                {
                    while (_readerQueue.Count > 0)
                    {
                        _readers++;
                        readersToWake.Add(_readerQueue.Dequeue());
                    }
                }
            }
            writerToWake?.TrySetResult(true);
            foreach (var r in readersToWake) r.TrySetResult(true);
        }

        public new bool IsWriteLockHeld => _writeLocked;
        public new bool IsReadLockHeld => _readers > 0;
    }
}
