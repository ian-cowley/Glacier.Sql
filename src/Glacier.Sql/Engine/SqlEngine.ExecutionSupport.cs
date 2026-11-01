using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
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

                DataFrame preTxSnapshot;
                using (entry.AcquireReadLock())
                {
                    preTxSnapshot = entry.CurrentSnapshot;
                }

                context.ActiveTransaction.RegisterRollback(() =>
                {
                    using (entry.AcquireWriteLock())
                    {
                        entry.CurrentSnapshot = preTxSnapshot;
                    }
                });
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
            tx.Commit(commitLsn);
            context.ActiveTransaction = null;
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
            tx.Rollback();
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

        private object? ConvertValue(object? val, string dataType)
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
                        try { acquired[i].Dispose(); } catch { }
                    }
                });
            }
            catch
            {
                for (int i = acquired.Count - 1; i >= 0; i--)
                {
                    try { acquired[i].Dispose(); } catch { }
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
                    var lexer = new SqlLexer(viewMeta.DefinitionSql);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    if (parser.Parse() is SelectStatement viewSelect)
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
    }
}
