using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
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

    public class SqlEngine
    {
        private readonly QueryPlanner _planner;

        public SqlEngine(CatalogManager catalog)
        {
            _planner = new QueryPlanner(catalog);
        }

        public async Task<ExecuteResult> ExecuteAsync(string sqlText, ExecutionContext context)
        {
            try
            {
                var lexer = new SqlLexer(sqlText);
                var tokens = lexer.Tokenize();
                var parser = new TSqlParser(tokens);
                var stmt = parser.Parse();

                return await ExecuteStatementAsync(stmt, context);
            }
            catch (Exception ex)
            {
                return ExecuteResult.Error($"Execution Error: {ex.Message}");
            }
        }

        public async Task<ExecuteResult> ExecuteStatementAsync(SqlStatement stmt, ExecutionContext context, DataFrame? inserted = null, DataFrame? deleted = null)
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
                        return await ExecuteDelete(delete, context);

                    case UpdateStatement update:
                        return await ExecuteUpdate(update, context);

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

        private ExecuteResult ExecuteCreate(CreateTableStatement create, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (catalog.TableExists(create.TableName))
            {
                return ExecuteResult.Error($"Table '{create.TableName}' already exists.");
            }

            var columns = create.Columns.Select(c => new ColumnMetadata
            {
                Name = c.Name,
                DataType = c.DataType,
                IsNullable = c.IsNullable,
                IsPrimaryKey = c.IsPrimaryKey,
                IsUnique = c.IsUnique,
                CheckExpression = c.CheckExpression
            }).ToList();

            catalog.AddTable(create.TableName, columns);

            var meta = catalog.GetTable(create.TableName)!;
            TableStorage.InitializeTable(meta.BackingFile, columns);

            return ExecuteResult.Ok($"Table '{create.TableName}' created successfully.");
        }

        private ExecuteResult ExecuteDrop(DropTableStatement drop, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (!catalog.TableExists(drop.TableName))
            {
                return ExecuteResult.Error($"Table '{drop.TableName}' does not exist.");
            }

            catalog.RemoveTable(drop.TableName);
            return ExecuteResult.Ok($"Table '{drop.TableName}' dropped successfully.");
        }

        private async Task<ExecuteResult> ExecuteInsert(InsertStatement insert, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(insert.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{insert.TableName}' does not exist.");
            }

            // Load existing table data
            var df = TableStorage.ReadTable(meta.BackingFile);

            // Parse and map insert values
            int targetColCount = insert.Columns?.Count ?? meta.Columns.Count;
            if (insert.Values.Count != targetColCount)
            {
                return ExecuteResult.Error($"Column count ({targetColCount}) does not match value count ({insert.Values.Count}).");
            }

            // Create a mapping from column name to value
            var valMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetColCount; i++)
            {
                string colName = insert.Columns != null ? insert.Columns[i] : meta.Columns[i].Name;
                var expr = insert.Values[i];

                if (expr is not SqlLiteral lit)
                {
                    throw new NotSupportedException("Only literal values are supported in INSERT statements currently.");
                }

                valMap[colName] = lit.Value;
            }

            // Construct single-row DataFrame
            var newSeriesList = new List<ISeries>();
            foreach (var col in meta.Columns)
            {
                valMap.TryGetValue(col.Name, out var rawVal);
                var resolvedVal = ConvertValue(rawVal, col.DataType);

                string dt = col.DataType.ToUpperInvariant();
                if (dt == "INT" || dt == "INTEGER")
                {
                    var s = new Int32Series(col.Name, 1);
                    if (resolvedVal == null) s.ValidityMask.SetNull(0);
                    else { s.Memory.Span[0] = (int)resolvedVal; s.ValidityMask.SetValid(0); }
                    newSeriesList.Add(s);
                }
                else if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
                {
                    var s = new Float64Series(col.Name, 1);
                    if (resolvedVal == null) s.ValidityMask.SetNull(0);
                    else { s.Memory.Span[0] = (double)resolvedVal; s.ValidityMask.SetValid(0); }
                    newSeriesList.Add(s);
                }
                else if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
                {
                    string? strVal = (string?)resolvedVal;
                    var s = new Utf8StringSeries(col.Name, new string[] { strVal ?? "" });
                    if (strVal == null) s.ValidityMask.SetNull(0);
                    newSeriesList.Add(s);
                }
                else if (dt == "BIT" || dt == "BOOLEAN")
                {
                    var s = new BooleanSeries(col.Name, 1);
                    if (resolvedVal == null) s.ValidityMask.SetNull(0);
                    else { s.Memory.Span[0] = (bool)resolvedVal; s.ValidityMask.SetValid(0); }
                    newSeriesList.Add(s);
                }
                else if (dt == "DATETIME" || dt == "DATE")
                {
                    var s = new TimeSeries(col.Name, 1);
                    if (resolvedVal == null) s.ValidityMask.SetNull(0);
                    else { s.Memory.Span[0] = (long)resolvedVal; s.ValidityMask.SetValid(0); }
                    newSeriesList.Add(s);
                }
            }

            var newRowDf = new DataFrame(newSeriesList);

            // Check INSTEAD OF triggers
            if (HasInsteadOfTrigger(insert.TableName, "INSERT", context))
            {
                await RunTriggersAsync(insert.TableName, "INSERT", "INSTEAD OF", newRowDf, null, context);
                return ExecuteResult.Ok("1 row inserted (handled by INSTEAD OF trigger).", 1);
            }

            using (AcquireLock(insert.TableName, true, context))
            {
                // Transaction rollback backup support
                BackupForTransaction(meta, catalog, context);

                // Concatenate
                var mergedDf = DataFrame.Concat(new[] { df, newRowDf });

                // Validate constraints
                await ValidateConstraintsAsync(meta, mergedDf, context);

                TableStorage.WriteTable(mergedDf, meta.BackingFile);

                // Dispatch AFTER insert triggers
                await RunTriggersAsync(insert.TableName, "INSERT", "AFTER", newRowDf, null, context);

                return ExecuteResult.Ok("1 row inserted successfully.", 1);
            }
        }

        private async Task<ExecuteResult> ExecuteSelect(SelectStatement select, ExecutionContext context, DataFrame? inserted = null, DataFrame? deleted = null)
        {
            var selectLocks = AcquireSelectLocks(select, false, context);
            try
            {
                var lazy = _planner.PlanQuery(select, inserted, deleted);
                var resultDf = await lazy.Collect();
                return ExecuteResult.Query(resultDf);
            }
            finally
            {
                foreach (var lk in selectLocks) lk.Dispose();
            }
        }

        private async Task<ExecuteResult> ExecuteDelete(DeleteStatement delete, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(delete.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{delete.TableName}' does not exist.");
            }

            var df = TableStorage.ReadTable(meta.BackingFile);
            int originalRowCount = df.RowCount;

            if (originalRowCount == 0)
            {
                return ExecuteResult.Ok("0 rows deleted.", 0);
            }

            DataFrame deletedRowsDf;
            DataFrame filteredDf;
            int deletedCount = 0;

            if (delete.Where == null)
            {
                deletedRowsDf = df;
                deletedCount = originalRowCount;
                var emptyCols = df.Columns.Select(col => col.CloneEmpty(0)).ToList();
                filteredDf = new DataFrame(emptyCols);
            }
            else
            {
                var conditionExpr = _planner.CompileExpression(delete.Where, delete.TableName);
                var keepExpr = conditionExpr.IsNull() | (conditionExpr == Expr.Lit(false));
                
                deletedRowsDf = await df.Lazy().Filter(conditionExpr).Collect();
                deletedCount = deletedRowsDf.RowCount;
                
                if (deletedCount > 0)
                {
                    filteredDf = await df.Lazy().Filter(keepExpr).Collect();
                }
                else
                {
                    filteredDf = df;
                }
            }

            if (deletedCount == 0)
            {
                return ExecuteResult.Ok("0 rows deleted.", 0);
            }

            // Check INSTEAD OF triggers
            if (HasInsteadOfTrigger(delete.TableName, "DELETE", context))
            {
                await RunTriggersAsync(delete.TableName, "DELETE", "INSTEAD OF", null, deletedRowsDf, context);
                return ExecuteResult.Ok($"{deletedCount} row(s) deleted (handled by INSTEAD OF trigger).", deletedCount);
            }

            using (AcquireLock(delete.TableName, true, context))
            {
                // Transaction rollback backup support
                BackupForTransaction(meta, catalog, context);

                // Write back to storage
                TableStorage.WriteTable(filteredDf, meta.BackingFile);

                // Dispatch AFTER delete triggers
                await RunTriggersAsync(delete.TableName, "DELETE", "AFTER", null, deletedRowsDf, context);

                return ExecuteResult.Ok($"{deletedCount} row{(deletedCount == 1 ? "" : "s")} deleted.", deletedCount);
            }
        }

        private async Task<ExecuteResult> ExecuteUpdate(UpdateStatement update, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(update.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{update.TableName}' does not exist.");
            }

            var df = TableStorage.ReadTable(meta.BackingFile);
            int originalRowCount = df.RowCount;

            if (originalRowCount == 0)
            {
                return ExecuteResult.Ok("0 rows updated.", 0);
            }

            // Compile the condition
            var conditionExpr = update.Where != null 
                ? _planner.CompileExpression(update.Where, update.TableName)
                : Expr.Lit(true);

            // Get rows before update (deleted)
            var deletedRowsDf = await df.Lazy().Filter(conditionExpr).Collect();
            int updatedCount = deletedRowsDf.RowCount;

            if (updatedCount == 0)
            {
                return ExecuteResult.Ok("0 rows updated.", 0);
            }

            // Build select projections to perform update
            var updateMap = update.Assignments.ToDictionary(a => a.ColumnName, a => a.Expression, StringComparer.OrdinalIgnoreCase);
            var projections = new List<Expr>();

            foreach (var col in meta.Columns)
            {
                if (updateMap.TryGetValue(col.Name, out var assignExpr))
                {
                    var valExpr = _planner.CompileExpression(assignExpr, update.TableName);
                    var condProj = Expr.When(conditionExpr).Then(valExpr).Otherwise(Expr.Col(col.Name)).Alias(col.Name);
                    projections.Add(condProj);
                }
                else
                {
                    projections.Add(Expr.Col(col.Name).Alias(col.Name));
                }
            }

            // Run lazy projection to update the values
            var updatedDf = await df.Lazy().Select(projections.ToArray()).Collect();
            
            // Validate constraints
            await ValidateConstraintsAsync(meta, updatedDf, context);

            // Get rows after update (inserted)
            var insertedRowsDf = await updatedDf.Lazy().Filter(conditionExpr).Collect();

            // Check INSTEAD OF triggers
            if (HasInsteadOfTrigger(update.TableName, "UPDATE", context))
            {
                await RunTriggersAsync(update.TableName, "UPDATE", "INSTEAD OF", insertedRowsDf, deletedRowsDf, context);
                return ExecuteResult.Ok($"{updatedCount} row(s) updated (handled by INSTEAD OF trigger).", updatedCount);
            }

            using (AcquireLock(update.TableName, true, context))
            {
                // Transaction rollback backup support
                BackupForTransaction(meta, catalog, context);

                // Save back to table storage
                TableStorage.WriteTable(updatedDf, meta.BackingFile);

                // Dispatch AFTER update triggers
                await RunTriggersAsync(update.TableName, "UPDATE", "AFTER", insertedRowsDf, deletedRowsDf, context);

                return ExecuteResult.Ok($"{updatedCount} row{(updatedCount == 1 ? "" : "s")} updated.", updatedCount);
            }
        }

        private void BackupForTransaction(TableMetadata meta, CatalogManager catalog, ExecutionContext context)
        {
            if (context.ActiveTransaction != null)
            {
                string backupPath = Path.Combine(catalog.DataDirectory, $"{meta.TableName}_backup.ipc");
                try
                {
                    if (!File.Exists(backupPath))
                    {
                        File.Copy(meta.BackingFile, backupPath, true);
                        context.ActiveTransaction.RegisterBackupFile(backupPath);
                        context.ActiveTransaction.RegisterRollback(() =>
                        {
                            if (File.Exists(backupPath))
                            {
                                File.Copy(backupPath, meta.BackingFile, true);
                                File.Delete(backupPath);
                            }
                        });
                    }
                }
                catch { }
            }
        }

        private ExecuteResult ExecuteBeginTransaction(BeginTransactionStatement begin, ExecutionContext context)
        {
            if (context.ActiveTransaction != null)
            {
                return ExecuteResult.Error("A transaction is already active. Nested transactions are not supported.");
            }
            context.ActiveTransaction = new SqlTransaction();
            return ExecuteResult.Ok("Transaction started.", 0);
        }

        private ExecuteResult ExecuteCommitTransaction(CommitTransactionStatement commit, ExecutionContext context)
        {
            if (context.ActiveTransaction == null)
            {
                return ExecuteResult.Error("No active transaction found to commit.");
            }
            context.ActiveTransaction.Commit();
            context.ActiveTransaction = null;
            return ExecuteResult.Ok("Transaction committed.", 0);
        }

        private ExecuteResult ExecuteRollbackTransaction(RollbackTransactionStatement rollback, ExecutionContext context)
        {
            if (context.ActiveTransaction == null)
            {
                return ExecuteResult.Error("No active transaction found to rollback.");
            }
            context.ActiveTransaction.Rollback();
            context.ActiveTransaction = null;
            return ExecuteResult.Ok("Transaction rolled back.", 0);
        }

        private ExecuteResult ExecuteCreateTrigger(CreateTriggerStatement createTrigger, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (!catalog.TableExists(createTrigger.TableName))
            {
                return ExecuteResult.Error($"Table '{createTrigger.TableName}' does not exist for trigger.");
            }

            var trigMeta = new TriggerMetadata
            {
                TriggerName = createTrigger.TriggerName,
                TableName = createTrigger.TableName,
                EventType = createTrigger.EventType,
                Timing = createTrigger.Timing,
                ActionSql = createTrigger.ActionSql
            };

            catalog.AddTrigger(trigMeta);
            return ExecuteResult.Ok($"Trigger '{createTrigger.TriggerName}' created successfully.");
        }

        private async Task<ExecuteResult> ExecuteInsertSelect(InsertSelectStatement insertSelect, ExecutionContext context, DataFrame? inserted = null, DataFrame? deleted = null)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(insertSelect.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{insertSelect.TableName}' does not exist.");
            }

            // 1. Execute SELECT query
            var lazy = _planner.PlanQuery(insertSelect.SelectQuery, inserted, deleted);
            var selectDf = await lazy.Collect();

            if (selectDf.RowCount == 0)
            {
                return ExecuteResult.Ok("0 rows inserted.", 0);
            }

            // 2. Map and align columns
            int targetColCount = insertSelect.Columns?.Count ?? meta.Columns.Count;
            if (selectDf.Columns.Count != targetColCount)
            {
                return ExecuteResult.Error($"Column count in SELECT ({selectDf.Columns.Count}) does not match target column count ({targetColCount}).");
            }

            var newSeriesList = new List<ISeries>();
            for (int targetIdx = 0; targetIdx < meta.Columns.Count; targetIdx++)
            {
                var targetCol = meta.Columns[targetIdx];
                int sourceIdx = -1;
                if (insertSelect.Columns != null)
                {
                    sourceIdx = insertSelect.Columns.FindIndex(c => c.Equals(targetCol.Name, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    sourceIdx = targetIdx;
                }

                if (sourceIdx == -1)
                {
                    newSeriesList.Add(CreateNullSeries(targetCol.Name, targetCol.DataType, selectDf.RowCount));
                }
                else
                {
                    newSeriesList.Add(ConvertSeries(selectDf.Columns[sourceIdx], targetCol.Name, targetCol.DataType));
                }
            }

            var newRowsDf = new DataFrame(newSeriesList);

            // Check INSTEAD OF triggers
            if (HasInsteadOfTrigger(insertSelect.TableName, "INSERT", context))
            {
                await RunTriggersAsync(insertSelect.TableName, "INSERT", "INSTEAD OF", newRowsDf, null, context);
                return ExecuteResult.Ok($"{newRowsDf.RowCount} row(s) inserted (handled by INSTEAD OF trigger).", newRowsDf.RowCount);
            }

            // Load existing table data
            var df = TableStorage.ReadTable(meta.BackingFile);

            using (AcquireLock(insertSelect.TableName, true, context))
            {
                // Transaction rollback backup support
                BackupForTransaction(meta, catalog, context);

                // Concatenate
                var mergedDf = DataFrame.Concat(new[] { df, newRowsDf });

                // Validate constraints
                await ValidateConstraintsAsync(meta, mergedDf, context);

                TableStorage.WriteTable(mergedDf, meta.BackingFile);

                // Dispatch AFTER insert triggers
                await RunTriggersAsync(insertSelect.TableName, "INSERT", "AFTER", newRowsDf, null, context);

                return ExecuteResult.Ok($"{newRowsDf.RowCount} row{(newRowsDf.RowCount == 1 ? "" : "s")} inserted successfully.", newRowsDf.RowCount);
            }
        }

        private async Task RunTriggersAsync(string tableName, string eventType, string timing, DataFrame? inserted, DataFrame? deleted, ExecutionContext context)
        {
            // 1. Dispatch custom/mocked memory triggers in context
            if (timing == "AFTER")
            {
                if (eventType.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
                {
                    if (deleted != null) context.DispatchTriggers(tableName, eventType, deleted);
                }
                else
                {
                    if (inserted != null) context.DispatchTriggers(tableName, eventType, inserted);
                }
            }

            // 2. Dispatch persistent catalog triggers
            var persistentTriggers = context.Catalog.GetTriggersForTable(tableName, eventType);
            foreach (var trig in persistentTriggers)
            {
                if (trig.Timing.Equals(timing, StringComparison.OrdinalIgnoreCase))
                {
                    var lexer = new SqlLexer(trig.ActionSql);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    var actionStmt = parser.Parse();

                    var res = await ExecuteStatementAsync(actionStmt, context, inserted, deleted);
                    if (!res.Success)
                    {
                        throw new Exception($"Trigger '{trig.TriggerName}' failed: {res.Message}");
                    }
                }
            }
        }

        private bool HasInsteadOfTrigger(string tableName, string eventType, ExecutionContext context)
        {
            var persistentTriggers = context.Catalog.GetTriggersForTable(tableName, eventType);
            return persistentTriggers.Any(t => t.Timing.Equals("INSTEAD OF", StringComparison.OrdinalIgnoreCase) && 
                                               t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase));
        }

        private ISeries CreateNullSeries(string name, string dataType, int length)
        {
            string dt = dataType.ToUpperInvariant();
            if (dt == "INT" || dt == "INTEGER")
            {
                var s = new Int32Series(name, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
            {
                var s = new Float64Series(name, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
            {
                var s = new Utf8StringSeries(name, new string[length]);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "BIT" || dt == "BOOLEAN")
            {
                var s = new BooleanSeries(name, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            if (dt == "DATETIME" || dt == "DATE")
            {
                var s = new TimeSeries(name, length);
                for (int i = 0; i < length; i++) s.ValidityMask.SetNull(i);
                return s;
            }
            throw new NotSupportedException($"Data type '{dataType}' is not supported.");
        }

        private ISeries ConvertSeries(ISeries source, string name, string dataType)
        {
            int length = source.Length;
            string dt = dataType.ToUpperInvariant();
            if (dt == "INT" || dt == "INTEGER")
            {
                var s = new Int32Series(name, length);
                for (int i = 0; i < length; i++)
                {
                    if (!source.ValidityMask.IsValid(i))
                    {
                        s.ValidityMask.SetNull(i);
                    }
                    else
                    {
                        var converted = ConvertValue(source.Get(i), dataType);
                        if (converted == null) s.ValidityMask.SetNull(i);
                        else { s.Memory.Span[i] = (int)converted; s.ValidityMask.SetValid(i); }
                    }
                }
                return s;
            }
            if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
            {
                var s = new Float64Series(name, length);
                for (int i = 0; i < length; i++)
                {
                    if (!source.ValidityMask.IsValid(i))
                    {
                        s.ValidityMask.SetNull(i);
                    }
                    else
                    {
                        var converted = ConvertValue(source.Get(i), dataType);
                        if (converted == null) s.ValidityMask.SetNull(i);
                        else { s.Memory.Span[i] = (double)converted; s.ValidityMask.SetValid(i); }
                    }
                }
                return s;
            }
            if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
            {
                var arr = new string[length];
                var validity = new bool[length];
                for (int i = 0; i < length; i++)
                {
                    if (!source.ValidityMask.IsValid(i))
                    {
                        arr[i] = "";
                        validity[i] = false;
                    }
                    else
                    {
                        var converted = ConvertValue(source.Get(i), dataType);
                        if (converted == null)
                        {
                            arr[i] = "";
                            validity[i] = false;
                        }
                        else
                        {
                            arr[i] = (string)converted;
                            validity[i] = true;
                        }
                    }
                }
                var s = new Utf8StringSeries(name, arr);
                for (int i = 0; i < length; i++)
                {
                    if (!validity[i]) s.ValidityMask.SetNull(i);
                }
                return s;
            }
            if (dt == "BIT" || dt == "BOOLEAN")
            {
                var s = new BooleanSeries(name, length);
                for (int i = 0; i < length; i++)
                {
                    if (!source.ValidityMask.IsValid(i))
                    {
                        s.ValidityMask.SetNull(i);
                    }
                    else
                    {
                        var converted = ConvertValue(source.Get(i), dataType);
                        if (converted == null) s.ValidityMask.SetNull(i);
                        else { s.Memory.Span[i] = (bool)converted; s.ValidityMask.SetValid(i); }
                    }
                }
                return s;
            }
            if (dt == "DATETIME" || dt == "DATE")
            {
                var s = new TimeSeries(name, length);
                for (int i = 0; i < length; i++)
                {
                    if (!source.ValidityMask.IsValid(i))
                    {
                        s.ValidityMask.SetNull(i);
                    }
                    else
                    {
                        var converted = ConvertValue(source.Get(i), dataType);
                        if (converted == null) s.ValidityMask.SetNull(i);
                        else { s.Memory.Span[i] = (long)converted; s.ValidityMask.SetValid(i); }
                    }
                }
                return s;
            }
            throw new NotSupportedException($"Data type '{dataType}' is not supported.");
        }

        private object? ConvertValue(object? val, string dataType)
        {
            if (val == null) return null;

            string dt = dataType.ToUpperInvariant();
            try
            {
                if (dt == "INT" || dt == "INTEGER")
                {
                    return Convert.ToInt32(val);
                }
                if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
                {
                    return Convert.ToDouble(val);
                }
                if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
                {
                    return val.ToString();
                }
                if (dt == "BIT" || dt == "BOOLEAN")
                {
                    if (val is bool b) return b;
                    if (val is int i) return i != 0;
                    if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
                    return Convert.ToBoolean(val);
                }
                if (dt == "DATETIME" || dt == "DATE")
                {
                    if (val is string sDate)
                    {
                        return DateTimeOffset.Parse(sDate).ToUnixTimeMilliseconds();
                    }
                    return Convert.ToInt64(val);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Cannot convert value '{val}' to type '{dataType}': {ex.Message}");
            }

            return val;
        }

        // Lock & Constraint Helpers
        private IDatabaseLock AcquireLock(string tableName, bool exclusive, ExecutionContext context)
        {
            if (tableName.Equals("inserted", StringComparison.OrdinalIgnoreCase) ||
                tableName.Equals("deleted", StringComparison.OrdinalIgnoreCase) ||
                tableName.StartsWith("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase))
            {
                return new NullDatabaseLock();
            }

            var lk = context.Catalog.GetTableLock(tableName);
            if (context.ActiveTransaction != null)
            {
                var existing = context.ActiveTransaction.GetHeldLock(tableName);
                if (existing != null)
                {
                    if (existing.Exclusive || !exclusive)
                    {
                        return new NullDatabaseLock();
                    }
                    else
                    {
                        // Upgrade lock
                        if (existing.Lock.IsReadLockHeld)
                            existing.Lock.ExitReadLock();
                        context.ActiveTransaction.RemoveHeldLock(tableName);
                    }
                }

                if (exclusive)
                    lk.EnterWriteLock();
                else
                    lk.EnterReadLock();

                context.ActiveTransaction.RegisterLock(tableName, exclusive, lk);
                return new NullDatabaseLock();
            }
            else
            {
                return new DatabaseLock(lk, exclusive);
            }
        }

        private List<IDatabaseLock> AcquireSelectLocks(SelectStatement select, bool exclusive, ExecutionContext context)
        {
            var locks = new List<IDatabaseLock>();
            var tables = GetSelectTables(select);
            foreach (var t in tables)
            {
                if (context.Catalog.ViewExists(t))
                {
                    var viewMeta = context.Catalog.GetView(t)!;
                    var lexer = new SqlLexer(viewMeta.DefinitionSql);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    var viewSelect = (SelectStatement)parser.Parse();
                    locks.AddRange(AcquireSelectLocks(viewSelect, exclusive, context));
                }
                else
                {
                    locks.Add(AcquireLock(t, exclusive, context));
                }
            }
            return locks;
        }

        private List<string> GetSelectTables(SelectStatement select)
        {
            var tables = new List<string>();
            if (select.From is SqlTableSource source)
            {
                tables.Add(source.TableName);
            }
            foreach (var join in select.Joins)
            {
                if (join.Table is SqlTableSource joinSource)
                {
                    tables.Add(joinSource.TableName);
                }
            }
            return tables;
        }

        private async Task ValidateConstraintsAsync(TableMetadata meta, DataFrame df, ExecutionContext context)
        {
            foreach (var col in meta.Columns)
            {
                var series = df.Columns.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                if (series == null) continue;

                // 1. NOT NULL / PRIMARY KEY
                if (!col.IsNullable || col.IsPrimaryKey)
                {
                    for (int r = 0; r < series.Length; r++)
                    {
                        if (!series.ValidityMask.IsValid(r))
                        {
                            throw new Exception($"Cannot insert or update NULL into NOT NULL column '{col.Name}' in table '{meta.TableName}'.");
                        }
                    }
                }

                // 2. UNIQUE / PRIMARY KEY
                if (col.IsUnique || col.IsPrimaryKey)
                {
                    var values = new HashSet<object>();
                    for (int r = 0; r < series.Length; r++)
                    {
                        if (series.ValidityMask.IsValid(r))
                        {
                            var val = series.Get(r);
                            if (val != null)
                            {
                                if (!values.Add(val))
                                {
                                    throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{col.Name}' in table '{meta.TableName}'. Duplicate value '{val}' is not allowed.");
                                }
                            }
                        }
                    }
                }

                // 3. CHECK
                if (!string.IsNullOrEmpty(col.CheckExpression))
                {
                    var lexer = new SqlLexer(col.CheckExpression);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    var checkExpr = parser.ParseExpression(0);

                    var compiled = _planner.CompileExpression(checkExpr, meta.TableName);

                    var violatingDf = await df.Lazy().Filter((compiled == Expr.Lit(false)) & compiled.IsNotNull()).Collect();
                    if (violatingDf.RowCount > 0)
                    {
                        throw new Exception($"Violation of CHECK constraint '{col.CheckExpression}' on column '{col.Name}' in table '{meta.TableName}'.");
                    }
                }
            }
        }

        private async Task<ExecuteResult> ExecuteAlter(AlterTableStatement alter, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(alter.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{alter.TableName}' does not exist.");
            }

            using (AcquireLock(alter.TableName, true, context))
            {
                var df = TableStorage.ReadTable(meta.BackingFile);

                if (alter.AlterAction.Equals("ADD", StringComparison.OrdinalIgnoreCase))
                {
                    var colDef = alter.ColumnDef ?? throw new Exception("Column definition is required for ADD COLUMN");
                    if (meta.Columns.Any(c => c.Name.Equals(colDef.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return ExecuteResult.Error($"Column '{colDef.Name}' already exists in table '{alter.TableName}'.");
                    }

                    if (!colDef.IsNullable && df.RowCount > 0)
                    {
                        return ExecuteResult.Error($"Cannot add NOT NULL column '{colDef.Name}' to a non-empty table '{alter.TableName}'.");
                    }

                    BackupForTransaction(meta, catalog, context);

                    var newColMeta = new ColumnMetadata
                    {
                        Name = colDef.Name,
                        DataType = colDef.DataType,
                        IsNullable = colDef.IsNullable,
                        IsPrimaryKey = colDef.IsPrimaryKey,
                        IsUnique = colDef.IsUnique,
                        CheckExpression = colDef.CheckExpression
                    };
                    meta.Columns.Add(newColMeta);

                    var newSeries = CreateNullSeries(colDef.Name, colDef.DataType, df.RowCount);
                    var newCols = df.Columns.Concat(new[] { newSeries }).ToList();
                    var newDf = new DataFrame(newCols);

                    TableStorage.WriteTable(newDf, meta.BackingFile);
                    catalog.Save();

                    return ExecuteResult.Ok($"Column '{colDef.Name}' added to table '{alter.TableName}' successfully.");
                }
                else if (alter.AlterAction.Equals("DROP", StringComparison.OrdinalIgnoreCase))
                {
                    string colName = alter.ColumnName ?? throw new Exception("Column name is required for DROP COLUMN");
                    var targetCol = meta.Columns.FirstOrDefault(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                    if (targetCol == null)
                    {
                        return ExecuteResult.Error($"Column '{colName}' does not exist in table '{alter.TableName}'.");
                    }

                    if (meta.Columns.Count <= 1)
                    {
                        return ExecuteResult.Error($"Cannot drop column '{colName}' because it is the only column in table '{alter.TableName}'.");
                    }

                    BackupForTransaction(meta, catalog, context);

                    meta.Columns.Remove(targetCol);

                    var newCols = df.Columns.Where(c => !c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase)).ToList();
                    var newDf = new DataFrame(newCols);

                    TableStorage.WriteTable(newDf, meta.BackingFile);
                    catalog.Save();

                    return ExecuteResult.Ok($"Column '{colName}' dropped from table '{alter.TableName}' successfully.");
                }
                else
                {
                    return ExecuteResult.Error($"Unsupported alter action: {alter.AlterAction}");
                }
            }
        }

        private ExecuteResult ExecuteCreateView(CreateViewStatement createView, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (catalog.TableExists(createView.ViewName))
            {
                return ExecuteResult.Error($"A table named '{createView.ViewName}' already exists.");
            }
            if (catalog.ViewExists(createView.ViewName))
            {
                return ExecuteResult.Error($"View '{createView.ViewName}' already exists.");
            }

            catalog.AddView(createView.ViewName, createView.DefinitionSql);
            return ExecuteResult.Ok($"View '{createView.ViewName}' created successfully.");
        }

        private ExecuteResult ExecuteDropView(DropViewStatement dropView, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (!catalog.ViewExists(dropView.ViewName))
            {
                return ExecuteResult.Error($"View '{dropView.ViewName}' does not exist.");
            }
            catalog.RemoveView(dropView.ViewName);
            return ExecuteResult.Ok($"View '{dropView.ViewName}' dropped successfully.");
        }
    }
}
