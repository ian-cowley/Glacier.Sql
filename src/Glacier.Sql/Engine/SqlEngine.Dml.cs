using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Engine
{
    public partial class SqlEngine
    {
        private static object? EvaluateInsertExpression(SqlExpression expr)
        {
            switch (expr)
            {
                case SqlLiteral lit:
                    return lit.Value;

                case SqlUnaryExpression unary:
                {
                    object? operand = EvaluateInsertExpression(unary.Operand);
                    if (operand == null) return null;

                    if (unary.Operator == "-")
                    {
                        if (operand is int i) return checked(-i);
                        if (operand is long l)
                        {
                            if (l == 2147483648L) return int.MinValue;
                            return checked(-l);
                        }
                        if (operand is double d) return -d;
                        if (operand is float f) return -f;
                        return -Convert.ToDouble(operand, CultureInfo.InvariantCulture);
                    }
                    if (unary.Operator == "+")
                    {
                        if (operand is int or long or double or float) return operand;
                        return Convert.ToDouble(operand, CultureInfo.InvariantCulture);
                    }
                    if (unary.Operator.Equals("NOT", StringComparison.OrdinalIgnoreCase))
                    {
                        return !Convert.ToBoolean(operand, CultureInfo.InvariantCulture);
                    }

                    throw new NotSupportedException($"Unsupported unary operator '{unary.Operator}' in INSERT VALUES.");
                }

                case SqlBinaryExpression bin:
                {
                    object? left = EvaluateInsertExpression(bin.Left);
                    object? right = EvaluateInsertExpression(bin.Right);
                    if (left == null || right == null) return null;

                    if (bin.Operator == "+" && (left is string || right is string))
                    {
                        return Convert.ToString(left, CultureInfo.InvariantCulture) + Convert.ToString(right, CultureInfo.InvariantCulture);
                    }

                    if ((left is int or long) && (right is int or long))
                    {
                        long l = Convert.ToInt64(left, CultureInfo.InvariantCulture);
                        long r = Convert.ToInt64(right, CultureInfo.InvariantCulture);
                        long res = bin.Operator switch
                        {
                            "+" => checked(l + r),
                            "-" => checked(l - r),
                            "*" => checked(l * r),
                            "/" => l / r,
                            _ => throw new NotSupportedException($"Unsupported binary operator '{bin.Operator}' in INSERT VALUES.")
                        };
                        return res >= int.MinValue && res <= int.MaxValue ? (int)res : res;
                    }

                    double dl = Convert.ToDouble(left, CultureInfo.InvariantCulture);
                    double dr = Convert.ToDouble(right, CultureInfo.InvariantCulture);
                    return bin.Operator switch
                    {
                        "+" => dl + dr,
                        "-" => dl - dr,
                        "*" => dl * dr,
                        "/" => dl / dr,
                        _ => throw new NotSupportedException($"Unsupported binary operator '{bin.Operator}' in INSERT VALUES.")
                    };
                }

                case SqlFunctionCall fn:
                {
                    string name = fn.FunctionName.ToUpperInvariant();
                    if (name == "GETDATE")
                    {
                        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }

                    var args = fn.Arguments.Select(EvaluateInsertExpression).ToList();
                    if (args.Any(a => a == null)) return null;

                    return name switch
                    {
                        "UPPER" when args.Count == 1 => Convert.ToString(args[0], CultureInfo.InvariantCulture)?.ToUpperInvariant(),
                        "LOWER" when args.Count == 1 => Convert.ToString(args[0], CultureInfo.InvariantCulture)?.ToLowerInvariant(),
                        "LEN" when args.Count == 1 => Convert.ToString(args[0], CultureInfo.InvariantCulture)?.Length ?? 0,
                        "ABS" when args.Count == 1 => args[0] is int ai ? Math.Abs(ai) : Math.Abs(Convert.ToDouble(args[0], CultureInfo.InvariantCulture)),
                        "SQRT" when args.Count == 1 => Math.Sqrt(Convert.ToDouble(args[0], CultureInfo.InvariantCulture)),
                        "ROUND" when args.Count == 1 => Math.Round(Convert.ToDouble(args[0], CultureInfo.InvariantCulture)),
                        "ROUND" when args.Count == 2 => Math.Round(
                            Convert.ToDouble(args[0], CultureInfo.InvariantCulture),
                            Convert.ToInt32(args[1], CultureInfo.InvariantCulture)),
                        _ => throw new NotSupportedException($"Unsupported function '{fn.FunctionName}' in INSERT VALUES.")
                    };
                }

                default:
                    throw new NotSupportedException($"Unsupported expression '{expr.GetType().Name}' in INSERT statements.");
            }
        }

        private async Task<ExecuteResult> ExecuteInsert(InsertStatement insert, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(insert.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{insert.TableName}' does not exist.");
            }

            // Parse and map insert values
            int targetColCount = insert.Columns?.Count ?? meta.Columns.Count;
            if (insert.Values.Count != targetColCount)
            {
                return ExecuteResult.Error($"Column count ({targetColCount}) does not match value count ({insert.Values.Count}).");
            }

            // Create a mapping from column name to evaluated constant value
            var valMap = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetColCount; i++)
            {
                string colName = insert.Columns != null ? insert.Columns[i] : meta.Columns[i].Name;
                var expr = insert.Values[i];
                valMap[colName] = EvaluateInsertExpression(expr);
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

            // Check INSTEAD OF triggers before mutating physical table
            if (HasInsteadOfTrigger(insert.TableName, "INSERT", context))
            {
                await RunTriggersAsync(insert.TableName, "INSERT", "INSTEAD OF", newRowDf, null, context);
                return ExecuteResult.Ok("1 row inserted (handled by INSTEAD OF trigger).", 1);
            }

            using (await AcquireLockAsync(insert.TableName, exclusive: true, context))
            {
                var liveMeta = catalog.GetTable(insert.TableName);
                if (liveMeta == null)
                {
                    return ExecuteResult.Error($"Table '{insert.TableName}' does not exist.");
                }

                var bufferEntry = catalog.BufferPool.GetOrLoad(liveMeta);
                DataFrame currentDf;
                using (bufferEntry.AcquireReadLock())
                {
                    currentDf = bufferEntry.CurrentSnapshot;
                }

                // Concatenate in memory under exclusive table lock
                var mergedDf = DataFrame.Concat(new[] { currentDf, newRowDf });

                // Validate constraints under exclusive table lock
                await ValidateConstraintsAsync(liveMeta, mergedDf, context);

                // Register transaction rollback state before applying mutation
                BackupForTransaction(liveMeta, catalog, context, bufferEntry);

                // Update in-memory Buffer Pool and append physical Arrow IPC delta to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    byte[] payload = TableStorage.SerializeDataFrame(newRowDf);
                    Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowInsert, txId, insert.TableName, payload);
                    bufferEntry.ApplyMutation(mergedDf, lsn, txId);
                }

                // Dispatch AFTER insert triggers
                await RunTriggersAsync(insert.TableName, "INSERT", "AFTER", newRowDf, null, context);

                return ExecuteResult.Ok("1 row inserted successfully.", 1);
            }
        }

        private async Task<ExecuteResult> ExecuteSelect(
            SelectStatement select,
            ExecutionContext context,
            DataFrame? inserted = null,
            DataFrame? deleted = null)
        {
            var lockRequests = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            CollectSelectLockRequests(select, context.Catalog, lockRequests, exclusive: false);

            using (await AcquireTableLocksAsync(lockRequests, context))
            {
                var lazy = _planner.PlanQuery(select, inserted, deleted);
                var resultDf = await lazy.Collect();
                return ExecuteResult.Query(resultDf);
            }
        }

        private async Task<ExecuteResult> ExecuteDelete(DeleteStatement delete, ExecutionContext context, string? sqlText = null)
        {
            _ = sqlText;
            var catalog = context.Catalog;
            if (!catalog.TableExists(delete.TableName))
            {
                return ExecuteResult.Error($"Table '{delete.TableName}' does not exist.");
            }

            var lockRequests = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [delete.TableName] = true
            };
            if (delete.Where != null)
            {
                CollectExpressionLockRequests(delete.Where, catalog, lockRequests);
            }

            using (await AcquireTableLocksAsync(lockRequests, context))
            {
                var meta = catalog.GetTable(delete.TableName);
                if (meta == null)
                {
                    return ExecuteResult.Error($"Table '{delete.TableName}' does not exist.");
                }

                var bufferEntry = catalog.BufferPool.GetOrLoad(meta);
                DataFrame df;
                using (bufferEntry.AcquireReadLock())
                {
                    df = bufferEntry.CurrentSnapshot;
                }

                int originalRowCount = df.RowCount;
                if (originalRowCount == 0)
                {
                    return ExecuteResult.Ok("0 rows deleted.", 0);
                }

                DataFrame deletedRowsDf;
                DataFrame filteredDf;
                int deletedCount;

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

                    filteredDf = deletedCount > 0
                        ? await df.Lazy().Filter(keepExpr).Collect()
                        : df;
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

                BackupForTransaction(meta, catalog, context, bufferEntry);

                // Update in-memory buffer pool and append deterministic Arrow IPC post-delete snapshot to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    byte[] payload = TableStorage.SerializeDataFrame(filteredDf);
                    Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowDelete, txId, delete.TableName, payload);
                    bufferEntry.ApplyMutation(filteredDf, lsn, txId);
                }

                // Dispatch AFTER delete triggers
                await RunTriggersAsync(delete.TableName, "DELETE", "AFTER", null, deletedRowsDf, context);

                return ExecuteResult.Ok($"{deletedCount} row{(deletedCount == 1 ? "" : "s")} deleted.", deletedCount);
            }
        }

        private async Task<ExecuteResult> ExecuteUpdate(UpdateStatement update, ExecutionContext context, string? sqlText = null)
        {
            _ = sqlText;
            var catalog = context.Catalog;
            if (!catalog.TableExists(update.TableName))
            {
                return ExecuteResult.Error($"Table '{update.TableName}' does not exist.");
            }

            var lockRequests = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [update.TableName] = true
            };
            if (update.Where != null)
            {
                CollectExpressionLockRequests(update.Where, catalog, lockRequests);
            }
            foreach (var assignment in update.Assignments)
            {
                CollectExpressionLockRequests(assignment.Expression, catalog, lockRequests);
            }

            using (await AcquireTableLocksAsync(lockRequests, context))
            {
                var meta = catalog.GetTable(update.TableName);
                if (meta == null)
                {
                    return ExecuteResult.Error($"Table '{update.TableName}' does not exist.");
                }

                var bufferEntry = catalog.BufferPool.GetOrLoad(meta);
                DataFrame df;
                using (bufferEntry.AcquireReadLock())
                {
                    df = bufferEntry.CurrentSnapshot;
                }

                if (df.RowCount == 0)
                {
                    return ExecuteResult.Ok("0 rows updated.", 0);
                }

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

                var updateMap = update.Assignments.ToDictionary(a => a.ColumnName, a => a.Expression, StringComparer.OrdinalIgnoreCase);
                var tableProjections = new List<Expr>(meta.Columns.Count);
                var insertedProjections = new List<Expr>(meta.Columns.Count);

                foreach (var col in meta.Columns)
                {
                    if (updateMap.TryGetValue(col.Name, out var assignExpr))
                    {
                        Expr valExpr;
                        string colDt = col.DataType.ToUpperInvariant();
                        if ((colDt == "BIT" || colDt == "BOOLEAN") && assignExpr is SqlLiteral { Value: int bitLit })
                        {
                            valExpr = Expr.Lit(bitLit != 0);
                        }
                        else
                        {
                            valExpr = _planner.CompileExpression(assignExpr, update.TableName);
                        }
                        tableProjections.Add(Expr.When(conditionExpr).Then(valExpr).Otherwise(Expr.Col(col.Name)).Alias(col.Name));
                        insertedProjections.Add(valExpr.Alias(col.Name));
                    }
                    else
                    {
                        tableProjections.Add(Expr.Col(col.Name).Alias(col.Name));
                        insertedProjections.Add(Expr.Col(col.Name).Alias(col.Name));
                    }
                }

                // Compute updated table snapshot and validate constraints under exclusive lock
                var updatedDf = await df.Lazy().Select(tableProjections.ToArray()).Collect();
                await ValidateConstraintsAsync(meta, updatedDf, context);

                // Compute exact post-update rows from matched pre-update rows (accurate even if WHERE column changed)
                var insertedRowsDf = await deletedRowsDf.Lazy().Select(insertedProjections.ToArray()).Collect();

                // Check INSTEAD OF triggers
                if (HasInsteadOfTrigger(update.TableName, "UPDATE", context))
                {
                    await RunTriggersAsync(update.TableName, "UPDATE", "INSTEAD OF", insertedRowsDf, deletedRowsDf, context);
                    return ExecuteResult.Ok($"{updatedCount} row(s) updated (handled by INSTEAD OF trigger).", updatedCount);
                }

                BackupForTransaction(meta, catalog, context, bufferEntry);

                // Update in-memory buffer pool and append deterministic Arrow IPC post-update snapshot to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    byte[] payload = TableStorage.SerializeDataFrame(updatedDf);
                    Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowUpdate, txId, update.TableName, payload);
                    bufferEntry.ApplyMutation(updatedDf, lsn, txId);
                }

                // Dispatch AFTER update triggers
                await RunTriggersAsync(update.TableName, "UPDATE", "AFTER", insertedRowsDf, deletedRowsDf, context);

                return ExecuteResult.Ok($"{updatedCount} row{(updatedCount == 1 ? "" : "s")} updated.", updatedCount);
            }
        }

        private async Task<ExecuteResult> ExecuteInsertSelect(
            InsertSelectStatement insertSelect,
            ExecutionContext context,
            DataFrame? inserted = null,
            DataFrame? deleted = null)
        {
            var catalog = context.Catalog;
            if (!catalog.TableExists(insertSelect.TableName))
            {
                return ExecuteResult.Error($"Table '{insertSelect.TableName}' does not exist.");
            }

            var lockRequests = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [insertSelect.TableName] = true
            };
            CollectSelectLockRequests(insertSelect.SelectQuery, catalog, lockRequests, exclusive: false);

            using (await AcquireTableLocksAsync(lockRequests, context))
            {
                var meta = catalog.GetTable(insertSelect.TableName);
                if (meta == null)
                {
                    return ExecuteResult.Error($"Table '{insertSelect.TableName}' does not exist.");
                }

                // 1. Execute SELECT query under acquired table locks
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

                var newSeriesList = new List<ISeries>(meta.Columns.Count);
                for (int targetIdx = 0; targetIdx < meta.Columns.Count; targetIdx++)
                {
                    var targetCol = meta.Columns[targetIdx];
                    int sourceIdx = insertSelect.Columns != null
                        ? insertSelect.Columns.FindIndex(c => c.Equals(targetCol.Name, StringComparison.OrdinalIgnoreCase))
                        : targetIdx;

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

                var bufferEntry = catalog.BufferPool.GetOrLoad(meta);
                DataFrame currentDf;
                using (bufferEntry.AcquireReadLock())
                {
                    currentDf = bufferEntry.CurrentSnapshot;
                }

                var mergedDf = DataFrame.Concat(new[] { currentDf, newRowsDf });
                await ValidateConstraintsAsync(meta, mergedDf, context);

                BackupForTransaction(meta, catalog, context, bufferEntry);

                using (bufferEntry.AcquireWriteLock())
                {
                    byte[] payload = TableStorage.SerializeDataFrame(newRowsDf);
                    Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowInsert, txId, insertSelect.TableName, payload);
                    bufferEntry.ApplyMutation(mergedDf, lsn, txId);
                }

                await RunTriggersAsync(insertSelect.TableName, "INSERT", "AFTER", newRowsDf, null, context);

                return ExecuteResult.Ok($"{newRowsDf.RowCount} row{(newRowsDf.RowCount == 1 ? "" : "s")} inserted successfully.", newRowsDf.RowCount);
            }
        }
    }
}
