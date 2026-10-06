using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;

namespace Glacier.Sql.Engine
{
    public class ExecuteResult
    {
        public bool Success { get; }
        public string Message { get; }
        public DataFrame? DataFrame { get; }
        public int AffectedRows { get; }

        public ExecuteResult(bool success, string message, DataFrame? df = null, int affectedRows = 0)
        {
            Success = success;
            Message = message;
            DataFrame = df;
            AffectedRows = affectedRows;
        }

        public static ExecuteResult Ok(string message, int affectedRows = 0) => new(true, message, null, affectedRows);
        public static ExecuteResult Query(DataFrame df) => new(true, $"Query returned {df.RowCount} rows.", df);
        public static ExecuteResult Error(string message) => new(false, message);
    }

    public class TransactionLockInfo
    {
        public string TableName { get; }
        public bool Exclusive { get; }
        public ThreadIndependentReaderWriterLock Lock { get; }

        public TransactionLockInfo(string tableName, bool exclusive, ThreadIndependentReaderWriterLock lk)
        {
            TableName = tableName;
            Exclusive = exclusive;
            Lock = lk;
        }
    }

    public class SqlTransaction
    {
        public string TransactionId { get; } = Guid.NewGuid().ToString();
        public bool IsActive { get; private set; } = true;
        private readonly List<Action> _rollbacks = new();
        private readonly List<string> _backupFiles = new();
        private readonly List<TransactionLockInfo> _heldLocks = new();
        private readonly HashSet<string> _backedUpTables = new(StringComparer.OrdinalIgnoreCase);

        public bool HasBackedUpTable(string tableName) => _backedUpTables.Contains(tableName);
        public void RegisterBackedUpTable(string tableName) => _backedUpTables.Add(tableName);

        public void RegisterRollback(Action action)
        {
            _rollbacks.Add(action);
        }

        public void RegisterBackupFile(string path)
        {
            _backupFiles.Add(path);
        }

        public void RegisterLock(string tableName, bool exclusive, ThreadIndependentReaderWriterLock lk)
        {
            _heldLocks.Add(new TransactionLockInfo(tableName, exclusive, lk));
        }

        public TransactionLockInfo? GetHeldLock(string tableName)
        {
            return _heldLocks.FirstOrDefault(l => l.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        }

        public void RemoveHeldLock(string tableName)
        {
            _heldLocks.RemoveAll(l => l.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        }

        public void Commit()
        {
            IsActive = false;
            _rollbacks.Clear();
            foreach (var path in _backupFiles)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch { }
            }
            _backupFiles.Clear();

            // Release locks
            foreach (var lockInfo in _heldLocks)
            {
                if (lockInfo.Exclusive)
                {
                    if (lockInfo.Lock.IsWriteLockHeld)
                        lockInfo.Lock.ExitWriteLock();
                }
                else
                {
                    if (lockInfo.Lock.IsReadLockHeld)
                        lockInfo.Lock.ExitReadLock();
                }
            }
            _heldLocks.Clear();
        }

        public void Rollback()
        {
            IsActive = false;
            foreach (var action in _rollbacks)
            {
                try { action(); } catch { }
            }
            _rollbacks.Clear();
            _backupFiles.Clear();

            // Release locks
            foreach (var lockInfo in _heldLocks)
            {
                if (lockInfo.Exclusive)
                {
                    if (lockInfo.Lock.IsWriteLockHeld)
                        lockInfo.Lock.ExitWriteLock();
                }
                else
                {
                    if (lockInfo.Lock.IsReadLockHeld)
                        lockInfo.Lock.ExitReadLock();
                }
            }
            _heldLocks.Clear();
        }
    }

    public interface IDatabaseLock : IDisposable { }

    public class DatabaseLock : IDatabaseLock
    {
        private readonly ThreadIndependentReaderWriterLock _lock;
        private readonly bool _exclusive;
        private bool _released;

        public DatabaseLock(ThreadIndependentReaderWriterLock lk, bool exclusive)
        {
            _lock = lk;
            _exclusive = exclusive;
            if (exclusive)
                _lock.EnterWriteLock();
            else
                _lock.EnterReadLock();
        }

        public void Dispose()
        {
            if (!_released)
            {
                if (_exclusive)
                {
                    if (_lock.IsWriteLockHeld)
                        _lock.ExitWriteLock();
                }
                else
                {
                    if (_lock.IsReadLockHeld)
                        _lock.ExitReadLock();
                }
                _released = true;
            }
        }
    }

    public class NullDatabaseLock : IDatabaseLock
    {
        public void Dispose() { }
    }

    public class SqlTrigger
    {
        public string Name { get; set; } = "";
        public string TableName { get; set; } = "";
        public string EventType { get; set; } = ""; // "INSERT", "DELETE", "UPDATE"
        public Action<ExecutionContext, DataFrame> TriggerAction { get; set; } = null!;
    }

    public class ExecutionContext
    {
        public CatalogManager Catalog { get; }
        public SqlTransaction? ActiveTransaction { get; set; }
        public List<SqlTrigger> Triggers { get; } = new();

        public ExecutionContext(CatalogManager catalog)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public void DispatchTriggers(string tableName, string eventType, DataFrame rows)
        {
            var matched = Triggers.Where(t => 
                t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) && 
                t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase));

            foreach (var trigger in matched)
            {
                trigger.TriggerAction(this, rows);
            }
        }
    }

    public partial class SqlEngine
    {
        private readonly QueryPlanner _planner;
        private readonly CatalogManager _catalog;

        public CatalogManager Catalog => _catalog;

        public SqlEngine(CatalogManager catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _planner = new QueryPlanner(catalog);
        }

        public void Checkpoint() => _catalog.Checkpoint();
        public Task CheckpointAsync() => Task.Run(() => _catalog.Checkpoint());

        public async Task<ExecuteResult> ExecuteAsync(string sqlText, ExecutionContext context)
        {
            try
            {
                var lexer = new SqlLexer(sqlText);
                var tokens = lexer.Tokenize();
                var parser = new TSqlParser(tokens);
                var stmt = parser.Parse();

                return await ExecuteStatementAsync(stmt, context, sqlText: sqlText);
            }
            catch (Exception ex)
            {
                return ExecuteResult.Error($"Execution Error: {ex.Message}");
            }
        }

        public async Task<ExecuteResult> ExecuteStatementAsync(SqlStatement stmt, ExecutionContext context, DataFrame? inserted = null, DataFrame? deleted = null, string? sqlText = null)
        {
            try
            {
                switch (stmt)
                {
                    case CreateTableStatement create:
                        return ExecuteCreate(create, context);

                    case DropTableStatement drop:
                        return ExecuteDrop(drop, context);

                    case AlterTableStatement alter:
                        return await ExecuteAlter(alter, context);

                    case CreateViewStatement createView:
                        return ExecuteCreateView(createView, context);

                    case DropViewStatement dropView:
                        return ExecuteDropView(dropView, context);

                    case InsertStatement insert:
                        return await ExecuteInsert(insert, context);

                    case SelectStatement select:
                        return await ExecuteSelect(select, context, inserted, deleted);

                    case DeleteStatement delete:
                        return await ExecuteDelete(delete, context, sqlText);

                    case UpdateStatement update:
                        return await ExecuteUpdate(update, context, sqlText);

                    case BeginTransactionStatement begin:
                        return ExecuteBeginTransaction(begin, context);

                    case CommitTransactionStatement commit:
                        return ExecuteCommitTransaction(commit, context);

                    case RollbackTransactionStatement rollback:
                        return ExecuteRollbackTransaction(rollback, context);

                    case CreateTriggerStatement createTrigger:
                        return ExecuteCreateTrigger(createTrigger, context);

                    case InsertSelectStatement insertSelect:
                        return await ExecuteInsertSelect(insertSelect, context, inserted, deleted);

                    default:
                        return ExecuteResult.Error($"Unsupported statement type: {stmt.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                return ExecuteResult.Error($"Execution Error: {ex}");
            }
        }
    }
}
