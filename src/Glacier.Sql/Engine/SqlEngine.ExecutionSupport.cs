using System;
using System.Collections.Generic;
using System.IO;
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
            if (context.ActiveTransaction != null && !context.ActiveTransaction.HasBackedUpTable(meta.TableName))
            {
                context.ActiveTransaction.RegisterBackedUpTable(meta.TableName);

                var entry = bufferEntry ?? catalog.BufferPool.GetOrLoad(meta);
                var preTxSnapshot = entry.CurrentSnapshot;
                context.ActiveTransaction.RegisterRollback(() =>
                {
                    using (entry.AcquireWriteLock())
                    {
                        entry.CurrentSnapshot = preTxSnapshot;
                    }
                });

                // Fallback for legacy on-disk backup recovery test compatibility
                string backupPath = Path.Combine(catalog.DataDirectory, $"{meta.TableName}_backup.ipc");
                try
                {
                    if (!File.Exists(backupPath))
                    {
                        if (File.Exists(meta.BackingFile))
                        {
                            File.Copy(meta.BackingFile, backupPath, true);
                        }
                        else
                        {
                            var emptyDf = TableStorage.CreateEmptyDataFrame(meta.Columns);
                            emptyDf.WriteIpc(backupPath);
                        }
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
            Guid txId = Guid.Parse(context.ActiveTransaction.TransactionId);
            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxBegin, txId, string.Empty, ReadOnlySpan<byte>.Empty);
            return ExecuteResult.Ok("Transaction started.", 0);
        }

        private ExecuteResult ExecuteCommitTransaction(CommitTransactionStatement commit, ExecutionContext context)
        {
            if (context.ActiveTransaction == null)
            {
                return ExecuteResult.Error("No active transaction found to commit.");
            }
            Guid txId = Guid.Parse(context.ActiveTransaction.TransactionId);
            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxCommit, txId, string.Empty, ReadOnlySpan<byte>.Empty);
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
            Guid txId = Guid.Parse(context.ActiveTransaction.TransactionId);
            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxAbort, txId, string.Empty, ReadOnlySpan<byte>.Empty);
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
    }
}
