using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.BufferPool;

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
        public bool Exclusive { get; private set; }
        public AsyncReaderWriterLock Lock { get; }
        public AsyncReaderWriterLock.LockReleaser Releaser { get; private set; }

        public TransactionLockInfo(
            string tableName,
            bool exclusive,
            AsyncReaderWriterLock lk,
            AsyncReaderWriterLock.LockReleaser releaser)
        {
            TableName = tableName;
            Exclusive = exclusive;
            Lock = lk;
            Releaser = releaser;
        }

        internal void UpgradeToWrite(AsyncReaderWriterLock.LockReleaser writeReleaser)
        {
            Exclusive = true;
            Releaser = writeReleaser;
        }
    }

    public class SqlTransaction
    {
        public Guid TransactionGuid { get; } = Guid.NewGuid();
        public string TransactionId { get; }
        public bool IsActive { get; private set; } = true;

        private readonly List<Action> _rollbacks = new();
        private readonly List<TransactionLockInfo> _heldLocks = new();
        private readonly HashSet<string> _backedUpTables = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TableBufferEntry> _mutatedEntries = new(StringComparer.OrdinalIgnoreCase);

        public SqlTransaction()
        {
            TransactionId = TransactionGuid.ToString();
        }

        public bool HasBackedUpTable(string tableName) => _backedUpTables.Contains(tableName);
        public void RegisterBackedUpTable(string tableName) => _backedUpTables.Add(tableName);

        public void RegisterMutatedEntry(TableBufferEntry entry)
        {
            if (entry != null)
            {
                _mutatedEntries[entry.TableName] = entry;
            }
        }

        public void RegisterRollback(Action action)
        {
            _rollbacks.Add(action);
        }

        public void RegisterLock(
            string tableName,
            bool exclusive,
            AsyncReaderWriterLock lk,
            AsyncReaderWriterLock.LockReleaser releaser)
        {
            _heldLocks.Add(new TransactionLockInfo(tableName, exclusive, lk, releaser));
        }

        public TransactionLockInfo? GetHeldLock(string tableName)
        {
            return _heldLocks.FirstOrDefault(l => l.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        }

        public void RemoveHeldLock(string tableName)
        {
            _heldLocks.RemoveAll(l => l.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase));
        }

        public void Commit(ulong commitLsn = 0)
        {
            IsActive = false;

            foreach (var entry in _mutatedEntries.Values)
            {
                try
                {
                    entry.CommitTransaction(TransactionGuid, commitLsn);
                }
                catch { }
            }

            _mutatedEntries.Clear();
            _rollbacks.Clear();
            _backedUpTables.Clear();

            ReleaseHeldLocks();
        }

        public void Rollback()
        {
            IsActive = false;

            foreach (var entry in _mutatedEntries.Values)
            {
                try
                {
                    entry.RollbackTransaction(TransactionGuid);
                }
                catch { }
            }

            foreach (var action in _rollbacks)
            {
                try { action(); } catch { }
            }

            _mutatedEntries.Clear();
            _rollbacks.Clear();
            _backedUpTables.Clear();

            ReleaseHeldLocks();
        }

        private void ReleaseHeldLocks()
        {
            for (int i = _heldLocks.Count - 1; i >= 0; i--)
            {
                try
                {
                    _heldLocks[i].Releaser.Dispose();
                }
                catch { }
            }
            _heldLocks.Clear();
        }
    }

    public interface IDatabaseLock : IDisposable { }

    public sealed class DatabaseLock : IDatabaseLock
    {
        private readonly Action _onDispose;
        private bool _released;

        public DatabaseLock(Action onDispose)
        {
            _onDispose = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
        }

        public DatabaseLock(AsyncReaderWriterLock.LockReleaser releaser)
        {
            if (releaser == null) throw new ArgumentNullException(nameof(releaser));
            _onDispose = releaser.Dispose;
        }

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                _onDispose();
            }
        }
    }

    public sealed class NullDatabaseLock : IDatabaseLock
    {
        public static readonly NullDatabaseLock Instance = new();
        public void Dispose() { }
    }

    internal sealed class StatementLockState
    {
        public string TableName { get; }
        public AsyncReaderWriterLock Lock { get; }
        public AsyncReaderWriterLock.LockReleaser Releaser { get; set; }
        public bool Exclusive { get; set; }
        public int RefCount { get; set; }

        public StatementLockState(
            string tableName,
            AsyncReaderWriterLock lk,
            AsyncReaderWriterLock.LockReleaser releaser,
            bool exclusive)
        {
            TableName = tableName;
            Lock = lk;
            Releaser = releaser;
            Exclusive = exclusive;
            RefCount = 1;
        }
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

        internal readonly object StatementLockSync = new();
        internal readonly Dictionary<string, StatementLockState> StatementLocks = new(StringComparer.OrdinalIgnoreCase);

        public ExecutionContext(CatalogManager catalog)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public void DispatchTriggers(string tableName, string eventType, DataFrame rows)
        {
            var matched = Triggers.Where(t =>
                t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase)).ToList();

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

        public async Task<ExecuteResult> ExecuteStatementAsync(
            SqlStatement stmt,
            ExecutionContext context,
            DataFrame? inserted = null,
            DataFrame? deleted = null,
            string? sqlText = null)
        {
            try
            {
                switch (stmt)
                {
                    case CreateTableStatement create:
                        return ExecuteCreate(create, context);

                    case DropTableStatement drop:
                        return await ExecuteDrop(drop, context);

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
