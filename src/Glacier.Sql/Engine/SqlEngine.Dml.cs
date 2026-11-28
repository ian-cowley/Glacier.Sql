using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Sql.Catalog;
using Glacier.Sql.Diagnostics;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Engine
{
    public partial class SqlEngine
    {
        private static async Task<ExecuteResult> ExecuteWithAutoCommitTransactionAsync(
            string tableName,
            string eventType,
            ExecutionContext context,
            Func<Task<ExecuteResult>> action)
        {
            if (context.ActiveTransaction != null || !HasAfterTrigger(tableName, eventType, context))
            {
                return await action().ConfigureAwait(false);
            }

            var implicitTx = new SqlTransaction();
            context.ActiveTransaction = implicitTx;
            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxBegin, implicitTx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);

            try
            {
                var res = await action().ConfigureAwait(false);
                if (!res.Success)
                {
                    if (implicitTx.IsActive)
                    {
                        try
                        {
                            context.Catalog.WalWriter.AppendRecord(WalRecordType.TxAbort, implicitTx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);
                        }
                        catch (Exception walEx)
                        {
                            GlacierSqlDiagnostics.LogError($"Error appending TxAbort during implicit rollback: {walEx.Message}", walEx);
                        }
                        implicitTx.Rollback();
                    }
                    context.ActiveTransaction = null;
                    return res;
                }

                if (implicitTx.IsActive)
                {
                    ulong commitLsn = context.Catalog.WalWriter.AppendRecord(WalRecordType.TxCommit, implicitTx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);
                    implicitTx.Commit(commitLsn);
                }
                context.ActiveTransaction = null;
                return res;
            }
            catch (Exception ex)
            {
                if (implicitTx.IsActive)
                {
                    try
                    {
                        context.Catalog.WalWriter.AppendRecord(WalRecordType.TxAbort, implicitTx.TransactionGuid, string.Empty, ReadOnlySpan<byte>.Empty);
                    }
                    catch (Exception walEx)
                    {
                        GlacierSqlDiagnostics.LogError($"Error appending TxAbort during implicit exception rollback: {walEx.Message}", walEx);
                    }

                    try
                    {
                        implicitTx.Rollback();
                    }
                    catch (Exception rbEx)
                    {
                        GlacierSqlDiagnostics.LogError($"Error rolling back implicit transaction after trigger failure: {rbEx.Message}", rbEx);
                    }
                }
                context.ActiveTransaction = null;
                _ = ex;
                throw;
            }
        }

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

        private static DataFrame BuildInsertRowsDataFrame(InsertStatement insert, TableMetadata liveMeta)
        {
            int rowCount = insert.Rows.Count;
            int targetColCount = insert.Columns?.Count ?? liveMeta.Columns.Count;

            var colIndexMap = new int[liveMeta.Columns.Count];
            for (int c = 0; c < liveMeta.Columns.Count; c++)
            {
                if (insert.Columns == null)
                {
                    colIndexMap[c] = c;
                }
                else
                {
                    colIndexMap[c] = insert.Columns.FindIndex(name => name.Equals(liveMeta.Columns[c].Name, StringComparison.OrdinalIgnoreCase));
                }
            }

            for (int r = 0; r < rowCount; r++)
            {
                if (insert.Rows[r].Count != targetColCount)
                {
                    throw new InvalidOperationException($"Column count ({targetColCount}) does not match value count ({insert.Rows[r].Count}) in row {r + 1}.");
                }
            }

            var evaluatedRows = new object?[rowCount, liveMeta.Columns.Count];
            for (int r = 0; r < rowCount; r++)
            {
                var rowExprs = insert.Rows[r];
                for (int c = 0; c < liveMeta.Columns.Count; c++)
                {
                    int srcIdx = colIndexMap[c];
                    if (srcIdx < 0)
                    {
                        evaluatedRows[r, c] = null;
                    }
                    else
                    {
                        object? rawVal = EvaluateInsertExpression(rowExprs[srcIdx]);
                        evaluatedRows[r, c] = ConvertValue(rawVal, liveMeta.Columns[c].DataType);
                    }
                }
            }

            var newSeriesList = new List<ISeries>(liveMeta.Columns.Count);
            for (int c = 0; c < liveMeta.Columns.Count; c++)
            {
                var col = liveMeta.Columns[c];
                string dt = col.DataType.ToUpperInvariant();

                if (dt == "INT" || dt == "INTEGER")
                {
                    var s = new Int32Series(col.Name, rowCount);
                    for (int r = 0; r < rowCount; r++)
                    {
                        if (evaluatedRows[r, c] == null) s.ValidityMask.SetNull(r);
                        else { s.Memory.Span[r] = (int)evaluatedRows[r, c]!; s.ValidityMask.SetValid(r); }
                    }
                    newSeriesList.Add(s);
                }
                else if (dt == "FLOAT" || dt == "DOUBLE" || dt == "REAL")
                {
                    var s = new Float64Series(col.Name, rowCount);
                    for (int r = 0; r < rowCount; r++)
                    {
                        if (evaluatedRows[r, c] == null) s.ValidityMask.SetNull(r);
                        else { s.Memory.Span[r] = (double)evaluatedRows[r, c]!; s.ValidityMask.SetValid(r); }
                    }
                    newSeriesList.Add(s);
                }
                else if (dt == "VARCHAR" || dt == "TEXT" || dt == "CHAR")
                {
                    var arr = new string[rowCount];
                    for (int r = 0; r < rowCount; r++)
                    {
                        arr[r] = (string?)evaluatedRows[r, c] ?? string.Empty;
                    }
                    var s = new Utf8StringSeries(col.Name, arr);
                    for (int r = 0; r < rowCount; r++)
                    {
                        if (evaluatedRows[r, c] == null) s.ValidityMask.SetNull(r);
                    }
                    newSeriesList.Add(s);
                }
                else if (dt == "BIT" || dt == "BOOLEAN")
                {
                    var s = new BooleanSeries(col.Name, rowCount);
                    for (int r = 0; r < rowCount; r++)
                    {
                        if (evaluatedRows[r, c] == null) s.ValidityMask.SetNull(r);
                        else { s.Memory.Span[r] = (bool)evaluatedRows[r, c]!; s.ValidityMask.SetValid(r); }
                    }
                    newSeriesList.Add(s);
                }
                else if (dt == "DATETIME" || dt == "DATE")
                {
                    var s = new TimeSeries(col.Name, rowCount);
                    for (int r = 0; r < rowCount; r++)
                    {
                        if (evaluatedRows[r, c] == null) s.ValidityMask.SetNull(r);
                        else { s.Memory.Span[r] = (long)evaluatedRows[r, c]!; s.ValidityMask.SetValid(r); }
                    }
                    newSeriesList.Add(s);
                }
                else
                {
                    throw new NotSupportedException($"Data type '{col.DataType}' for column '{col.Name}' is not supported.");
                }
            }

            return new DataFrame(newSeriesList);
        }

        private Task<ExecuteResult> ExecuteInsert(InsertStatement insert, ExecutionContext context)
        {
            return ExecuteWithAutoCommitTransactionAsync(insert.TableName, "INSERT", context, async () =>
            {
                var catalog = context.Catalog;
                if (!catalog.TableExists(insert.TableName))
                {
                    return ExecuteResult.Error($"Table '{insert.TableName}' does not exist.");
                }

                using (await AcquireLockAsync(insert.TableName, exclusive: true, context))
                {
                    var liveMeta = catalog.GetTable(insert.TableName);
                    if (liveMeta == null)
                    {
                        return ExecuteResult.Error($"Table '{insert.TableName}' does not exist.");
                    }

                    int targetColCount = insert.Columns?.Count ?? liveMeta.Columns.Count;
                    for (int r = 0; r < insert.Rows.Count; r++)
                    {
                        if (insert.Rows[r].Count != targetColCount)
                        {
                            return ExecuteResult.Error($"Column count ({targetColCount}) does not match value count ({insert.Rows[r].Count}).");
                        }
                    }

                    var newRowsDf = BuildInsertRowsDataFrame(insert, liveMeta);
                    int insertedRowCount = newRowsDf.RowCount;

                    // Check INSTEAD OF triggers under exclusive lock before mutating physical table
                    if (HasInsteadOfTrigger(insert.TableName, "INSERT", context))
                    {
                        await RunTriggersAsync(insert.TableName, "INSERT", "INSTEAD OF", newRowsDf, null, context);
                        return ExecuteResult.Ok($"{insertedRowCount} row{(insertedRowCount == 1 ? "" : "s")} inserted (handled by INSTEAD OF trigger).", insertedRowCount);
                    }

                    var bufferEntry = catalog.BufferPool.GetOrLoad(liveMeta);
                    var existingChunks = new List<DataFrame>();
                    using (bufferEntry.AcquireReadLock())
                    {
                        bufferEntry.GetCurrentChunks(existingChunks);
                    }

                    // Validate constraints incrementally under exclusive table lock
                    await ValidateInsertConstraintsAsync(liveMeta, existingChunks, newRowsDf, context);

                    // Register transaction rollback state before applying mutation
                    BackupForTransaction(liveMeta, catalog, context, bufferEntry);

                    // Update in-memory Buffer Pool (O(1) delta append) and append physical Arrow IPC delta to WAL
                    using (bufferEntry.AcquireWriteLock())
                    {
                        byte[] payload = TableStorage.SerializeDataFrame(newRowsDf);
                        Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                        ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowInsert, txId, liveMeta.TableId, insert.TableName, payload);
                        bufferEntry.ApplyAppend(newRowsDf, lsn, txId);
                    }

                    // Dispatch AFTER insert triggers
                    await RunTriggersAsync(insert.TableName, "INSERT", "AFTER", newRowsDf, null, context);

                    return ExecuteResult.Ok($"{insertedRowCount} row{(insertedRowCount == 1 ? "" : "s")} inserted successfully.", insertedRowCount);
                }
            });
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
                var lazy = await _planner.PlanQueryAsync(select, inserted, deleted).ConfigureAwait(false);
                var resultDf = await lazy.Collect().ConfigureAwait(false);
                return ExecuteResult.Query(resultDf);
            }
        }

        private Task<ExecuteResult> ExecuteDelete(DeleteStatement delete, ExecutionContext context, string? sqlText = null)
        {
            _ = sqlText;
            return ExecuteWithAutoCommitTransactionAsync(delete.TableName, "DELETE", context, async () =>
            {
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
                    int[]? deletedIndices = null;
                    bool deleteAll = false;

                    if (delete.Where == null)
                    {
                        deleteAll = true;
                        deletedRowsDf = df;
                        deletedCount = originalRowCount;
                        var emptyCols = df.Columns.Select(col => col.CloneEmpty(0)).ToList();
                        filteredDf = new DataFrame(emptyCols);
                    }
                    else
                    {
                        var conditionExpr = _planner.CompileExpression(delete.Where, delete.TableName);
                        var matchDf = await QueryPlanner.CloneDataFrameShallow(df).Lazy().Select(conditionExpr.Alias("__match")).Collect().ConfigureAwait(false);
                        var matchCol = (BooleanSeries)matchDf.GetColumn("__match");
                        var matchSpan = matchCol.Memory.Span;
                        var matchMask = matchCol.ValidityMask;

                        var delIdxList = new List<int>();
                        var keepIdxList = new List<int>(originalRowCount);
                        for (int r = 0; r < originalRowCount; r++)
                        {
                            if (matchMask.IsValid(r) && matchSpan[r])
                            {
                                delIdxList.Add(r);
                            }
                            else
                            {
                                keepIdxList.Add(r);
                            }
                        }

                        deletedCount = delIdxList.Count;
                        if (deletedCount == 0)
                        {
                            return ExecuteResult.Ok("0 rows deleted.", 0);
                        }

                        if (deletedCount == originalRowCount)
                        {
                            deleteAll = true;
                            deletedRowsDf = df;
                            var emptyCols = df.Columns.Select(col => col.CloneEmpty(0)).ToList();
                            filteredDf = new DataFrame(emptyCols);
                        }
                        else
                        {
                            deletedIndices = delIdxList.ToArray();
                            deletedRowsDf = TableStorage.TakeRows(df, deletedIndices);
                            filteredDf = TableStorage.TakeRows(df, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(keepIdxList));
                        }
                    }

                    // Check INSTEAD OF triggers
                    if (HasInsteadOfTrigger(delete.TableName, "DELETE", context))
                    {
                        await RunTriggersAsync(delete.TableName, "DELETE", "INSTEAD OF", null, deletedRowsDf, context);
                        return ExecuteResult.Ok($"{deletedCount} row(s) deleted (handled by INSTEAD OF trigger).", deletedCount);
                    }

                    BackupForTransaction(meta, catalog, context, bufferEntry);

                    // Update in-memory buffer pool and append compact row-ID delta record to WAL
                    using (bufferEntry.AcquireWriteLock())
                    {
                        byte[] payload;
                        if (deleteAll || deletedIndices == null)
                        {
                            payload = new byte[4];
                            BitConverter.TryWriteBytes(payload, -1);
                        }
                        else
                        {
                            payload = new byte[4 + deletedIndices.Length * 4];
                            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), deletedIndices.Length);
                            for (int i = 0; i < deletedIndices.Length; i++)
                            {
                                BitConverter.TryWriteBytes(payload.AsSpan(4 + i * 4, 4), deletedIndices[i]);
                            }
                        }

                        Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                        ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowDeleteDelta, txId, meta.TableId, delete.TableName, payload);
                        bufferEntry.ApplyMutation(filteredDf, lsn, txId);
                    }

                    // Dispatch AFTER delete triggers
                    await RunTriggersAsync(delete.TableName, "DELETE", "AFTER", null, deletedRowsDf, context);

                    return ExecuteResult.Ok($"{deletedCount} row{(deletedCount == 1 ? "" : "s")} deleted.", deletedCount);
                }
            });
        }

        private Task<ExecuteResult> ExecuteUpdate(UpdateStatement update, ExecutionContext context, string? sqlText = null)
        {
            _ = sqlText;
            return ExecuteWithAutoCommitTransactionAsync(update.TableName, "UPDATE", context, async () =>
            {
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

                    int[] updatedIndices;
                    DataFrame deletedRowsDf;

                    if (update.Where == null)
                    {
                        updatedIndices = new int[df.RowCount];
                        for (int i = 0; i < updatedIndices.Length; i++) updatedIndices[i] = i;
                        deletedRowsDf = df;
                    }
                    else
                    {
                        var conditionExpr = _planner.CompileExpression(update.Where, update.TableName);
                        var matchDf = await QueryPlanner.CloneDataFrameShallow(df).Lazy().Select(conditionExpr.Alias("__match")).Collect().ConfigureAwait(false);
                        var matchCol = (BooleanSeries)matchDf.GetColumn("__match");
                        var matchSpan = matchCol.Memory.Span;
                        var matchMask = matchCol.ValidityMask;

                        var idxList = new List<int>();
                        for (int r = 0; r < df.RowCount; r++)
                        {
                            if (matchMask.IsValid(r) && matchSpan[r])
                            {
                                idxList.Add(r);
                            }
                        }

                        if (idxList.Count == 0)
                        {
                            return ExecuteResult.Ok("0 rows updated.", 0);
                        }

                        updatedIndices = idxList.ToArray();
                        deletedRowsDf = TableStorage.TakeRows(df, updatedIndices);
                    }

                    int updatedCount = updatedIndices.Length;
                    var updateMap = update.Assignments.ToDictionary(a => a.ColumnName, a => a.Expression, StringComparer.OrdinalIgnoreCase);

                    var nullCols = new HashSet<int>();
                    var insertedProjections = new List<Expr>(meta.Columns.Count);

                    for (int c = 0; c < meta.Columns.Count; c++)
                    {
                        var col = meta.Columns[c];
                        if (updateMap.TryGetValue(col.Name, out var assignExpr))
                        {
                            if (assignExpr is SqlLiteral { Value: null })
                            {
                                nullCols.Add(c);
                                insertedProjections.Add(Expr.Col(col.Name).Alias(col.Name));
                            }
                            else
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
                                insertedProjections.Add(valExpr.Alias(col.Name));
                            }
                        }
                        else
                        {
                            insertedProjections.Add(Expr.Col(col.Name).Alias(col.Name));
                        }
                    }

                    // Compute exact post-update rows from matched pre-update rows BEFORE constraint checks
                    var projectedInserted = await QueryPlanner.CloneDataFrameShallow(deletedRowsDf).Lazy().Select(insertedProjections.ToArray()).Collect().ConfigureAwait(false);
                    var normalizedCols = new List<ISeries>(meta.Columns.Count);
                    for (int c = 0; c < meta.Columns.Count; c++)
                    {
                        var col = meta.Columns[c];
                        if (nullCols.Contains(c))
                        {
                            normalizedCols.Add(CreateNullSeries(col.Name, col.DataType, updatedCount));
                        }
                        else
                        {
                            var srcCol = projectedInserted.Columns[c];
                            var dstTemplate = df.Columns[c];
                            if (srcCol.DataType == dstTemplate.DataType)
                            {
                                normalizedCols.Add(srcCol);
                            }
                            else
                            {
                                normalizedCols.Add(ConvertSeries(srcCol, col.Name, col.DataType));
                            }
                        }
                    }
                    var insertedRowsDf = new DataFrame(normalizedCols);

                    // Run INSTEAD OF UPDATE trigger BEFORE validating base table constraints
                    if (HasInsteadOfTrigger(update.TableName, "UPDATE", context))
                    {
                        await RunTriggersAsync(update.TableName, "UPDATE", "INSTEAD OF", insertedRowsDf, deletedRowsDf, context);
                        return ExecuteResult.Ok($"{updatedCount} row(s) updated (handled by INSTEAD OF trigger).", updatedCount);
                    }

                    // Apply delta to compute updatedDf and validate constraints under exclusive lock
                    var updatedDf = TableStorage.ApplyUpdatedRows(df, updatedIndices, insertedRowsDf);
                    await ValidateConstraintsAsync(meta, updatedDf, context);

                    BackupForTransaction(meta, catalog, context, bufferEntry);

                    // Update in-memory buffer pool and append compact row-ID delta + k updated rows to WAL
                    using (bufferEntry.AcquireWriteLock())
                    {
                        byte[] deltaIpc = TableStorage.SerializeDataFrame(insertedRowsDf);
                        int headerBytes = 4 + updatedIndices.Length * 4;
                        byte[] payload = new byte[headerBytes + deltaIpc.Length];
                        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), updatedIndices.Length);
                        for (int i = 0; i < updatedIndices.Length; i++)
                        {
                            BitConverter.TryWriteBytes(payload.AsSpan(4 + i * 4, 4), updatedIndices[i]);
                        }
                        deltaIpc.CopyTo(payload.AsSpan(headerBytes));

                        Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                        ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowUpdateDelta, txId, meta.TableId, update.TableName, payload);
                        bufferEntry.ApplyMutation(updatedDf, lsn, txId);
                    }

                    // Dispatch AFTER update triggers
                    await RunTriggersAsync(update.TableName, "UPDATE", "AFTER", insertedRowsDf, deletedRowsDf, context);

                    return ExecuteResult.Ok($"{updatedCount} row{(updatedCount == 1 ? "" : "s")} updated.", updatedCount);
                }
            });
        }

        private Task<ExecuteResult> ExecuteInsertSelect(
            InsertSelectStatement insertSelect,
            ExecutionContext context,
            DataFrame? inserted = null,
            DataFrame? deleted = null)
        {
            return ExecuteWithAutoCommitTransactionAsync(insertSelect.TableName, "INSERT", context, async () =>
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
                    var lazy = await _planner.PlanQueryAsync(insertSelect.SelectQuery, inserted, deleted).ConfigureAwait(false);
                    var selectDf = await lazy.Collect().ConfigureAwait(false);

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
                    var existingChunks = new List<DataFrame>();
                    using (bufferEntry.AcquireReadLock())
                    {
                        bufferEntry.GetCurrentChunks(existingChunks);
                    }

                    await ValidateInsertConstraintsAsync(meta, existingChunks, newRowsDf, context);

                    BackupForTransaction(meta, catalog, context, bufferEntry);

                    using (bufferEntry.AcquireWriteLock())
                    {
                        byte[] payload = TableStorage.SerializeDataFrame(newRowsDf);
                        Guid txId = context.ActiveTransaction?.TransactionGuid ?? Guid.Empty;
                        ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowInsert, txId, meta.TableId, insertSelect.TableName, payload);
                        bufferEntry.ApplyAppend(newRowsDf, lsn, txId);
                    }

                    await RunTriggersAsync(insertSelect.TableName, "INSERT", "AFTER", newRowsDf, null, context);

                    return ExecuteResult.Ok($"{newRowsDf.RowCount} row{(newRowsDf.RowCount == 1 ? "" : "s")} inserted successfully.", newRowsDf.RowCount);
                }
            });
        }
    }
}
