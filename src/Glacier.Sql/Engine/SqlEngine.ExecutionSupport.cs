using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.BufferPool;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Engine
{
    public partial class SqlEngine
    {
        private void BackupForTransaction(TableMetadata meta, CatalogManager catalog, ExecutionContext context, TableBufferEntry? bufferEntry = null)
        {
            if (context.ActiveTransaction == null) return;

            var entry = bufferEntry ?? catalog.BufferPool.GetOrLoad(meta);
            context.ActiveTransaction.RegisterMutatedEntry(entry);

            if (!context.ActiveTransaction.HasBackedUpTable(meta.TableName))
            {
                context.ActiveTransaction.RegisterBackedUpTable(meta.TableName);
            }
        }

        private ExecuteResult ExecuteBeginTransaction(BeginTransactionStatement begin, ExecutionContext context)
        {
            _ = begin;
            if (context.ActiveTransaction != null)
            {
                return ExecuteResult.Error("A transaction is already active. Nested transactions are not supported.");
            }
            var tx = new SqlTransaction();
            context.ActiveTransaction = tx;
            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxBegin, tx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);
            return ExecuteResult.Ok("Transaction started.", 0);
        }

        private ExecuteResult ExecuteCommitTransaction(CommitTransactionStatement commit, ExecutionContext context)
        {
            _ = commit;
            if (context.ActiveTransaction == null)
            {
                return ExecuteResult.Error("No active transaction found to commit.");
            }
            var tx = context.ActiveTransaction;
            ulong commitLsn = context.Catalog.WalWriter.AppendRecord(WalRecordType.TxCommit, tx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);
            context.ActiveTransaction = null;
            tx.Commit(commitLsn);
            return ExecuteResult.Ok("Transaction committed.", 0);
        }

        private ExecuteResult ExecuteRollbackTransaction(RollbackTransactionStatement rollback, ExecutionContext context)
        {
            _ = rollback;
            if (context.ActiveTransaction == null)
            {
                return ExecuteResult.Error("No active transaction found to rollback.");
            }
            var tx = context.ActiveTransaction;
            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxAbort, tx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);
            context.ActiveTransaction = null;
            tx.Rollback();
            return ExecuteResult.Ok("Transaction rolled back.", 0);
        }

        private async Task<ExecuteResult> ExecuteCreateTrigger(CreateTriggerStatement createTrigger, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (!catalog.TableExists(createTrigger.TableName))
            {
                return ExecuteResult.Error($"Table '{createTrigger.TableName}' does not exist for trigger.");
            }

            using (await AcquireLockAsync(createTrigger.TableName, exclusive: true, context))
            {
                if (!catalog.TableExists(createTrigger.TableName))
                {
                    return ExecuteResult.Error($"Table '{createTrigger.TableName}' does not exist for trigger.");
                }

                var previousTrigger = catalog.GetTrigger(createTrigger.TriggerName);
                var trigMeta = new TriggerMetadata
                {
                    TriggerName = createTrigger.TriggerName,
                    TableName = createTrigger.TableName,
                    EventType = createTrigger.EventType,
                    Timing = createTrigger.Timing,
                    ActionSql = createTrigger.ActionSql
                };

                if (context.ActiveTransaction != null)
                {
                    catalog.AddTriggerInMemory(trigMeta);
                    context.ActiveTransaction.RegisterRollback(() =>
                    {
                        if (previousTrigger != null)
                        {
                            catalog.AddTriggerInMemory(previousTrigger);
                        }
                        else
                        {
                            catalog.RemoveTriggerInMemory(trigMeta.TriggerName);
                        }
                    });
                    context.ActiveTransaction.RegisterCommit(() => catalog.Save());
                }
                else
                {
                    catalog.AddTrigger(trigMeta);
                }

                return ExecuteResult.Ok($"Trigger '{createTrigger.TriggerName}' created successfully.");
            }
        }

        private async Task<ExecuteResult> ExecuteDropTrigger(DropTriggerStatement dropTrigger, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var existing = catalog.GetTrigger(dropTrigger.TriggerName);
            if (existing == null)
            {
                return ExecuteResult.Error($"Trigger '{dropTrigger.TriggerName}' does not exist.");
            }

            using (await AcquireLockAsync(existing.TableName, exclusive: true, context))
            {
                existing = catalog.GetTrigger(dropTrigger.TriggerName);
                if (existing == null)
                {
                    return ExecuteResult.Error($"Trigger '{dropTrigger.TriggerName}' does not exist.");
                }

                if (context.ActiveTransaction != null)
                {
                    var removed = catalog.RemoveTriggerInMemory(dropTrigger.TriggerName);
                    if (removed != null)
                    {
                        context.ActiveTransaction.RegisterRollback(() => catalog.AddTriggerInMemory(removed));
                        context.ActiveTransaction.RegisterCommit(() => catalog.Save());
                    }
                }
                else
                {
                    catalog.RemoveTrigger(dropTrigger.TriggerName);
                }

                return ExecuteResult.Ok($"Trigger '{dropTrigger.TriggerName}' dropped successfully.");
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

        private static bool HasInsteadOfTrigger(string tableName, string eventType, ExecutionContext context)
        {
            var persistentTriggers = context.Catalog.GetTriggersForTable(tableName, eventType);
            return persistentTriggers.Any(t => t.Timing.Equals("INSTEAD OF", StringComparison.OrdinalIgnoreCase) &&
                                               t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase));
        }

        private static bool HasAfterTrigger(string tableName, string eventType, ExecutionContext context)
        {
            if (context.Triggers.Any(t =>
                    t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
                    t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var persistentTriggers = context.Catalog.GetTriggersForTable(tableName, eventType);
            return persistentTriggers.Any(t =>
                t.Timing.Equals("AFTER", StringComparison.OrdinalIgnoreCase) &&
                t.EventType.Equals(eventType, StringComparison.OrdinalIgnoreCase));
        }

        private static ISeries CreateNullSeries(string name, string dataType, int length)
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
                var emptyStrings = new string[length];
                Array.Fill(emptyStrings, string.Empty);
                var s = new Utf8StringSeries(name, emptyStrings);
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

        private static object? ConvertValue(object? val, string dataType)
        {
            if (val == null) return null;

            string dt = dataType.ToUpperInvariant();
            try
            {
                if (dt == "INT" || dt == "INTEGER")
                {
                    return Convert.ToInt32(val, CultureInfo.InvariantCulture);
                }
                if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
                {
                    return Convert.ToDouble(val, CultureInfo.InvariantCulture);
                }
                if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
                {
                    return Convert.ToString(val, CultureInfo.InvariantCulture);
                }
                if (dt == "BIT" || dt == "BOOLEAN")
                {
                    if (val is bool b) return b;
                    if (val is int i) return i != 0;
                    if (val is long l) return l != 0L;
                    if (val is string s) return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
                    return Convert.ToBoolean(val, CultureInfo.InvariantCulture);
                }
                if (dt == "DATETIME" || dt == "DATE")
                {
                    if (val is string sDate)
                    {
                        return DateTimeOffset.Parse(sDate, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
                    }
                    return Convert.ToInt64(val, CultureInfo.InvariantCulture);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Cannot convert value '{val}' to type '{dataType}': {ex.Message}");
            }

            return val;
        }

        // Async Lock & Table Dependency Helpers
        private static bool IsVirtualOrSystemTable(string tableName)
        {
            return string.IsNullOrEmpty(tableName) ||
                   tableName.Equals("inserted", StringComparison.OrdinalIgnoreCase) ||
                   tableName.Equals("deleted", StringComparison.OrdinalIgnoreCase) ||
                   tableName.StartsWith("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase);
        }

        private async ValueTask<IDatabaseLock> AcquireLockAsync(string tableName, bool exclusive, ExecutionContext context)
        {
            if (IsVirtualOrSystemTable(tableName))
            {
                return NullDatabaseLock.Instance;
            }

            var lk = context.Catalog.GetTableLock(tableName);

            if (context.ActiveTransaction != null)
            {
                var existing = context.ActiveTransaction.GetHeldLock(tableName);
                if (existing != null)
                {
                    if (existing.Exclusive || !exclusive)
                    {
                        return NullDatabaseLock.Instance;
                    }

                    // Deadlock-safe async read-to-write upgrade
                    var writeReleaser = await lk.UpgradeReadToWriteAsync(existing.Releaser).ConfigureAwait(false);
                    existing.UpgradeToWrite(writeReleaser);
                    return NullDatabaseLock.Instance;
                }

                var releaser = exclusive
                    ? await lk.EnterWriteLockAsync().ConfigureAwait(false)
                    : await lk.EnterReadLockAsync().ConfigureAwait(false);

                context.ActiveTransaction.RegisterLock(tableName, exclusive, lk, releaser);
                return NullDatabaseLock.Instance;
            }

            // Re-entrant statement lock tracking (supports nested AFTER / INSTEAD OF triggers on the same ExecutionContext)
            AsyncReaderWriterLock.LockReleaser? existingReadToUpgrade = null;
            lock (context.StatementLockSync)
            {
                if (context.StatementLocks.TryGetValue(tableName, out var state))
                {
                    if (state.Exclusive || !exclusive)
                    {
                        state.RefCount++;
                        return new DatabaseLock(() => ReleaseStatementLock(context, tableName));
                    }

                    existingReadToUpgrade = state.Releaser;
                }
            }

            if (existingReadToUpgrade != null)
            {
                var upgradedReleaser = await lk.UpgradeReadToWriteAsync(existingReadToUpgrade).ConfigureAwait(false);
                lock (context.StatementLockSync)
                {
                    if (context.StatementLocks.TryGetValue(tableName, out var state))
                    {
                        state.Releaser = upgradedReleaser;
                        state.Exclusive = true;
                        state.RefCount++;
                    }
                    else
                    {
                        context.StatementLocks[tableName] = new StatementLockState(tableName, lk, upgradedReleaser, exclusive: true);
                    }
                }
                return new DatabaseLock(() => ReleaseStatementLock(context, tableName));
            }

            var stmtReleaser = exclusive
                ? await lk.EnterWriteLockAsync().ConfigureAwait(false)
                : await lk.EnterReadLockAsync().ConfigureAwait(false);

            lock (context.StatementLockSync)
            {
                context.StatementLocks[tableName] = new StatementLockState(tableName, lk, stmtReleaser, exclusive);
            }

            return new DatabaseLock(() => ReleaseStatementLock(context, tableName));
        }

        private static void ReleaseStatementLock(ExecutionContext context, string tableName)
        {
            AsyncReaderWriterLock.LockReleaser? releaserToDispose = null;
            lock (context.StatementLockSync)
            {
                if (context.StatementLocks.TryGetValue(tableName, out var state))
                {
                    state.RefCount--;
                    if (state.RefCount <= 0)
                    {
                        context.StatementLocks.Remove(tableName);
                        releaserToDispose = state.Releaser;
                    }
                }
            }

            releaserToDispose?.Dispose();
        }

        private async ValueTask<IDatabaseLock> AcquireTableLocksAsync(
            Dictionary<string, bool> lockRequests,
            ExecutionContext context)
        {
            if (lockRequests.Count == 0)
            {
                return NullDatabaseLock.Instance;
            }

            // Canonical alphabetical ordering prevents multi-table AB/BA deadlocks
            var ordered = lockRequests
                .Where(kvp => !IsVirtualOrSystemTable(kvp.Key))
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ordered.Count == 0)
            {
                return NullDatabaseLock.Instance;
            }

            if (ordered.Count == 1)
            {
                return await AcquireLockAsync(ordered[0].Key, ordered[0].Value, context).ConfigureAwait(false);
            }

            var acquired = new List<IDatabaseLock>(ordered.Count);
            try
            {
                foreach (var kvp in ordered)
                {
                    acquired.Add(await AcquireLockAsync(kvp.Key, kvp.Value, context).ConfigureAwait(false));
                }

                return new DatabaseLock(() =>
                {
                    for (int i = acquired.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            acquired[i].Dispose();
                        }
                        catch (Exception ex)
                        {
                            GlacierSqlDiagnostics.LogError($"Error releasing statement lock: {ex.Message}", ex);
                        }
                    }
                });
            }
            catch
            {
                for (int i = acquired.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        acquired[i].Dispose();
                    }
                    catch (Exception ex)
                    {
                        GlacierSqlDiagnostics.LogError($"Error releasing lock during cleanup: {ex.Message}", ex);
                    }
                }
                throw;
            }
        }

        private static void AddLockRequest(Dictionary<string, bool> lockRequests, string tableName, bool exclusive)
        {
            if (IsVirtualOrSystemTable(tableName)) return;

            if (lockRequests.TryGetValue(tableName, out bool existingExclusive))
            {
                if (exclusive && !existingExclusive)
                {
                    lockRequests[tableName] = true;
                }
            }
            else
            {
                lockRequests[tableName] = exclusive;
            }
        }

        private static void CollectTableOrViewLockRequests(
            string name,
            CatalogManager catalog,
            Dictionary<string, bool> lockRequests,
            bool exclusive,
            HashSet<string>? visitedViews = null)
        {
            if (IsVirtualOrSystemTable(name)) return;

            if (catalog.ViewExists(name))
            {
                visitedViews ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!visitedViews.Add(name))
                {
                    return;
                }

                var viewMeta = catalog.GetView(name);
                if (viewMeta != null)
                {
                    SelectStatement? viewSelect = null;
                    if (catalog.TryGetCachedView(name, viewMeta.DefinitionSql, out var cachedAst, out _) && cachedAst != null)
                    {
                        viewSelect = cachedAst;
                    }
                    else
                    {
                        var lexer = new SqlLexer(viewMeta.DefinitionSql);
                        var tokens = lexer.Tokenize();
                        var parser = new TSqlParser(tokens);
                        if (parser.Parse() is SelectStatement parsed)
                        {
                            viewSelect = parsed;
                            catalog.CacheView(name, viewMeta.DefinitionSql, parsed, null);
                        }
                    }

                    if (viewSelect != null)
                    {
                        CollectSelectLockRequests(viewSelect, catalog, lockRequests, exclusive, visitedViews);
                    }
                }
            }
            else
            {
                AddLockRequest(lockRequests, name, exclusive);
            }
        }

        private static void CollectSelectLockRequests(
            SelectStatement select,
            CatalogManager catalog,
            Dictionary<string, bool> lockRequests,
            bool exclusive,
            HashSet<string>? visitedViews = null)
        {
            if (select.From is SqlTableSource source)
            {
                CollectTableOrViewLockRequests(source.TableName, catalog, lockRequests, exclusive, visitedViews);
            }

            foreach (var join in select.Joins)
            {
                if (join.Table is SqlTableSource joinSource)
                {
                    CollectTableOrViewLockRequests(joinSource.TableName, catalog, lockRequests, exclusive, visitedViews);
                }
                if (join.On != null)
                {
                    CollectExpressionLockRequests(join.On, catalog, lockRequests, visitedViews);
                }
            }

            foreach (var proj in select.Projections)
            {
                CollectExpressionLockRequests(proj.Expression, catalog, lockRequests, visitedViews);
            }

            if (select.Where != null)
            {
                CollectExpressionLockRequests(select.Where, catalog, lockRequests, visitedViews);
            }

            foreach (var grp in select.GroupBy)
            {
                CollectExpressionLockRequests(grp, catalog, lockRequests, visitedViews);
            }

            if (select.Having != null)
            {
                CollectExpressionLockRequests(select.Having, catalog, lockRequests, visitedViews);
            }

            foreach (var ord in select.OrderBy)
            {
                CollectExpressionLockRequests(ord.Expression, catalog, lockRequests, visitedViews);
            }
        }

        private static void CollectExpressionLockRequests(
            SqlExpression expr,
            CatalogManager catalog,
            Dictionary<string, bool> lockRequests,
            HashSet<string>? visitedViews = null)
        {
            switch (expr)
            {
                case SqlSubqueryExpression sub:
                    CollectSelectLockRequests(sub.SelectQuery, catalog, lockRequests, exclusive: false, visitedViews);
                    break;

                case SqlExistsExpression exists:
                    CollectSelectLockRequests(exists.SelectQuery, catalog, lockRequests, exclusive: false, visitedViews);
                    break;

                case SqlInSubqueryExpression inSub:
                    CollectExpressionLockRequests(inSub.Left, catalog, lockRequests, visitedViews);
                    CollectSelectLockRequests(inSub.Subquery, catalog, lockRequests, exclusive: false, visitedViews);
                    break;

                case SqlInListExpression inList:
                    CollectExpressionLockRequests(inList.Left, catalog, lockRequests, visitedViews);
                    foreach (var item in inList.List)
                    {
                        CollectExpressionLockRequests(item, catalog, lockRequests, visitedViews);
                    }
                    break;

                case SqlBinaryExpression bin:
                    CollectExpressionLockRequests(bin.Left, catalog, lockRequests, visitedViews);
                    CollectExpressionLockRequests(bin.Right, catalog, lockRequests, visitedViews);
                    break;

                case SqlUnaryExpression unary:
                    CollectExpressionLockRequests(unary.Operand, catalog, lockRequests, visitedViews);
                    break;

                case SqlFunctionCall fn:
                    foreach (var arg in fn.Arguments)
                    {
                        CollectExpressionLockRequests(arg, catalog, lockRequests, visitedViews);
                    }
                    break;
            }
        }

        private async Task ValidateConstraintsAsync(TableMetadata meta, DataFrame df, ExecutionContext context)
        {
            _ = context;
            foreach (var col in meta.Columns)
            {
                var series = df.Columns.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                if (series == null) continue;

                // 1. NOT NULL / PRIMARY KEY (O(1) bitmask word scan)
                if (!col.IsNullable || col.IsPrimaryKey)
                {
                    if (series.ValidityMask.HasNulls)
                    {
                        throw new Exception($"Cannot insert or update NULL into NOT NULL column '{col.Name}' in table '{meta.TableName}'.");
                    }
                }

                // 2. UNIQUE / PRIMARY KEY (zero-boxing typed checks)
                if (col.IsUnique || col.IsPrimaryKey)
                {
                    ValidateUniqueSeries(meta.TableName, col.Name, new[] { series });
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

        private async Task ValidateInsertConstraintsAsync(
            TableMetadata meta,
            IReadOnlyList<DataFrame> existingChunks,
            DataFrame newRowsDf,
            ExecutionContext context)
        {
            _ = context;
            foreach (var col in meta.Columns)
            {
                var newSeries = newRowsDf.Columns.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                if (newSeries == null) continue;

                // 1. NOT NULL / PRIMARY KEY (only need to check incoming rows)
                if (!col.IsNullable || col.IsPrimaryKey)
                {
                    if (newSeries.ValidityMask.HasNulls)
                    {
                        throw new Exception($"Cannot insert or update NULL into NOT NULL column '{col.Name}' in table '{meta.TableName}'.");
                    }
                }

                // 2. UNIQUE / PRIMARY KEY across existing chunks + new rows (zero DataFrame.Concat, zero boxing)
                if (col.IsUnique || col.IsPrimaryKey)
                {
                    var allColSeries = new List<ISeries>(existingChunks.Count + 1);
                    for (int i = 0; i < existingChunks.Count; i++)
                    {
                        var existingCol = existingChunks[i].Columns.FirstOrDefault(c => c.Name.Equals(col.Name, StringComparison.OrdinalIgnoreCase));
                        if (existingCol != null && existingCol.Length > 0)
                        {
                            allColSeries.Add(existingCol);
                        }
                    }
                    allColSeries.Add(newSeries);
                    ValidateUniqueSeries(meta.TableName, col.Name, allColSeries);
                }

                // 3. CHECK (only need to evaluate against new rows)
                if (!string.IsNullOrEmpty(col.CheckExpression))
                {
                    var lexer = new SqlLexer(col.CheckExpression);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    var checkExpr = parser.ParseExpression(0);

                    var compiled = _planner.CompileExpression(checkExpr, meta.TableName);

                    var violatingDf = await newRowsDf.Lazy().Filter((compiled == Expr.Lit(false)) & compiled.IsNotNull()).Collect();
                    if (violatingDf.RowCount > 0)
                    {
                        throw new Exception($"Violation of CHECK constraint '{col.CheckExpression}' on column '{col.Name}' in table '{meta.TableName}'.");
                    }
                }
            }
        }

        private static void ValidateUniqueSeries(string tableName, string colName, IReadOnlyList<ISeries> seriesList)
        {
            if (seriesList.Count == 0) return;

            var first = seriesList[0];
            if (first is Int32Series)
            {
                var set = new HashSet<int>();
                for (int s = 0; s < seriesList.Count; s++)
                {
                    var i32 = (Int32Series)seriesList[s];
                    var span = i32.Memory.Span;
                    var mask = i32.ValidityMask;
                    bool hasNulls = mask.HasNulls;
                    for (int r = 0; r < i32.Length; r++)
                    {
                        if (hasNulls && !mask.IsValid(r)) continue;
                        int val = span[r];
                        if (!set.Add(val))
                        {
                            throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{val}' is not allowed.");
                        }
                    }
                }
            }
            else if (first is Float64Series)
            {
                var set = new HashSet<double>();
                for (int s = 0; s < seriesList.Count; s++)
                {
                    var f64 = (Float64Series)seriesList[s];
                    var span = f64.Memory.Span;
                    var mask = f64.ValidityMask;
                    bool hasNulls = mask.HasNulls;
                    for (int r = 0; r < f64.Length; r++)
                    {
                        if (hasNulls && !mask.IsValid(r)) continue;
                        double val = span[r];
                        if (!set.Add(val))
                        {
                            throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{val}' is not allowed.");
                        }
                    }
                }
            }
            else if (first is TimeSeries)
            {
                var set = new HashSet<long>();
                for (int s = 0; s < seriesList.Count; s++)
                {
                    var ts = (TimeSeries)seriesList[s];
                    var span = ts.Memory.Span;
                    var mask = ts.ValidityMask;
                    bool hasNulls = mask.HasNulls;
                    for (int r = 0; r < ts.Length; r++)
                    {
                        if (hasNulls && !mask.IsValid(r)) continue;
                        long val = span[r];
                        if (!set.Add(val))
                        {
                            throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{val}' is not allowed.");
                        }
                    }
                }
            }
            else if (first is BooleanSeries)
            {
                bool seenTrue = false;
                bool seenFalse = false;
                for (int s = 0; s < seriesList.Count; s++)
                {
                    var bs = (BooleanSeries)seriesList[s];
                    var span = bs.Memory.Span;
                    var mask = bs.ValidityMask;
                    bool hasNulls = mask.HasNulls;
                    for (int r = 0; r < bs.Length; r++)
                    {
                        if (hasNulls && !mask.IsValid(r)) continue;
                        bool val = span[r];
                        if (val)
                        {
                            if (seenTrue)
                            {
                                throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{val}' is not allowed.");
                            }
                            seenTrue = true;
                        }
                        else
                        {
                            if (seenFalse)
                            {
                                throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{val}' is not allowed.");
                            }
                            seenFalse = true;
                        }
                    }
                }
            }
            else if (first is Utf8StringSeries)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                var lookup = set.GetAlternateLookup<ReadOnlySpan<char>>();
                Span<char> stackBuf = stackalloc char[256];
                for (int s = 0; s < seriesList.Count; s++)
                {
                    var u8 = (Utf8StringSeries)seriesList[s];
                    var mask = u8.ValidityMask;
                    bool hasNulls = mask.HasNulls;
                    for (int r = 0; r < u8.Length; r++)
                    {
                        if (hasNulls && !mask.IsValid(r)) continue;
                        var utf8Bytes = u8.GetStringSpan(r);
                        int maxChars = Encoding.UTF8.GetMaxCharCount(utf8Bytes.Length);
                        char[]? rented = maxChars > stackBuf.Length ? ArrayPool<char>.Shared.Rent(maxChars) : null;
                        Span<char> charBuf = rented != null ? rented : stackBuf;
                        try
                        {
                            int charCount = Encoding.UTF8.GetChars(utf8Bytes, charBuf);
                            var charSpan = charBuf.Slice(0, charCount);
                            if (!lookup.Add(charSpan))
                            {
                                throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{charSpan.ToString()}' is not allowed.");
                            }
                        }
                        finally
                        {
                            if (rented != null)
                            {
                                ArrayPool<char>.Shared.Return(rented);
                            }
                        }
                    }
                }
            }
            else
            {
                var values = new HashSet<object>();
                for (int s = 0; s < seriesList.Count; s++)
                {
                    var series = seriesList[s];
                    var mask = series.ValidityMask;
                    bool hasNulls = mask.HasNulls;
                    for (int r = 0; r < series.Length; r++)
                    {
                        if (hasNulls && !mask.IsValid(r)) continue;
                        var val = series.Get(r);
                        if (val != null && !values.Add(val))
                        {
                            throw new Exception($"Violation of UNIQUE/PRIMARY KEY constraint on column '{colName}' in table '{tableName}'. Duplicate value '{val}' is not allowed.");
                        }
                    }
                }
            }
        }
    }
}
