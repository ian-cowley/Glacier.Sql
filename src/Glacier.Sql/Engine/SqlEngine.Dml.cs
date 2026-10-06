using System;
using System.Collections.Generic;
using System.Text;
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
        private async Task<ExecuteResult> ExecuteInsert(InsertStatement insert, ExecutionContext context)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(insert.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{insert.TableName}' does not exist.");
            }

            // Load existing table data via buffer pool
            var bufferEntry = catalog.BufferPool.GetOrLoad(meta);

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
                // Transaction rollback in-memory snapshot support
                BackupForTransaction(meta, catalog, context, bufferEntry);

                var currentDf = bufferEntry.CurrentSnapshot;

                // Concatenate in memory
                var mergedDf = DataFrame.Concat(new[] { currentDf, newRowDf });

                // Validate constraints
                await ValidateConstraintsAsync(meta, mergedDf, context);

                // Update in-memory Buffer Pool and append to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    bufferEntry.CurrentSnapshot = mergedDf;
                    bufferEntry.IsDirty = true;

                    byte[] payload = TableStorage.SerializeDataFrame(newRowDf);
                    Guid txId = context.ActiveTransaction != null ? Guid.Parse(context.ActiveTransaction.TransactionId) : Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowInsert, txId, insert.TableName, payload);
                    bufferEntry.LastAppliedLsn = lsn;
                }

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

        private async Task<ExecuteResult> ExecuteDelete(DeleteStatement delete, ExecutionContext context, string? sqlText = null)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(delete.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{delete.TableName}' does not exist.");
            }

            var bufferEntry = catalog.BufferPool.GetOrLoad(meta);
            var df = bufferEntry.CurrentSnapshot;
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
                // Transaction rollback in-memory snapshot support
                BackupForTransaction(meta, catalog, context, bufferEntry);

                // Update in-memory buffer pool and append to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    bufferEntry.CurrentSnapshot = filteredDf;
                    bufferEntry.IsDirty = true;

                    string delText = sqlText ?? (delete.Where != null ? $"DELETE FROM {delete.TableName} WHERE {delete.Where}" : $"DELETE FROM {delete.TableName}");
                    byte[] payload = Encoding.UTF8.GetBytes(delText);
                    Guid txId = context.ActiveTransaction != null ? Guid.Parse(context.ActiveTransaction.TransactionId) : Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowDelete, txId, delete.TableName, payload);
                    bufferEntry.LastAppliedLsn = lsn;
                }

                // Dispatch AFTER delete triggers
                await RunTriggersAsync(delete.TableName, "DELETE", "AFTER", null, deletedRowsDf, context);

                return ExecuteResult.Ok($"{deletedCount} row{(deletedCount == 1 ? "" : "s")} deleted.", deletedCount);
            }
        }

        private async Task<ExecuteResult> ExecuteUpdate(UpdateStatement update, ExecutionContext context, string? sqlText = null)
        {
            var catalog = context.Catalog;
            var meta = catalog.GetTable(update.TableName);
            if (meta == null)
            {
                return ExecuteResult.Error($"Table '{update.TableName}' does not exist.");
            }

            var bufferEntry = catalog.BufferPool.GetOrLoad(meta);
            var df = bufferEntry.CurrentSnapshot;
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
                // Transaction rollback in-memory snapshot support
                BackupForTransaction(meta, catalog, context, bufferEntry);

                // Update in-memory buffer pool and append to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    bufferEntry.CurrentSnapshot = updatedDf;
                    bufferEntry.IsDirty = true;

                    string updText = sqlText ?? $"UPDATE {update.TableName}";
                    byte[] payload = Encoding.UTF8.GetBytes(updText);
                    Guid txId = context.ActiveTransaction != null ? Guid.Parse(context.ActiveTransaction.TransactionId) : Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowUpdate, txId, update.TableName, payload);
                    bufferEntry.LastAppliedLsn = lsn;
                }

                // Dispatch AFTER update triggers
                await RunTriggersAsync(update.TableName, "UPDATE", "AFTER", insertedRowsDf, deletedRowsDf, context);

                return ExecuteResult.Ok($"{updatedCount} row{(updatedCount == 1 ? "" : "s")} updated.", updatedCount);
            }
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

            // Load existing table data via buffer pool
            var bufferEntry = catalog.BufferPool.GetOrLoad(meta);

            using (AcquireLock(insertSelect.TableName, true, context))
            {
                // Transaction rollback in-memory snapshot support
                BackupForTransaction(meta, catalog, context, bufferEntry);

                var currentDf = bufferEntry.CurrentSnapshot;

                // Concatenate in memory
                var mergedDf = DataFrame.Concat(new[] { currentDf, newRowsDf });

                // Validate constraints
                await ValidateConstraintsAsync(meta, mergedDf, context);

                // Update in-memory buffer pool and append to WAL
                using (bufferEntry.AcquireWriteLock())
                {
                    bufferEntry.CurrentSnapshot = mergedDf;
                    bufferEntry.IsDirty = true;

                    byte[] payload = TableStorage.SerializeDataFrame(newRowsDf);
                    Guid txId = context.ActiveTransaction != null ? Guid.Parse(context.ActiveTransaction.TransactionId) : Guid.Empty;
                    ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowInsert, txId, insertSelect.TableName, payload);
                    bufferEntry.LastAppliedLsn = lsn;
                }

                // Dispatch AFTER insert triggers
                await RunTriggersAsync(insertSelect.TableName, "INSERT", "AFTER", newRowsDf, null, context);

                return ExecuteResult.Ok($"{newRowsDf.RowCount} row{(newRowsDf.RowCount == 1 ? "" : "s")} inserted successfully.", newRowsDf.RowCount);
            }
        }
    }
}
