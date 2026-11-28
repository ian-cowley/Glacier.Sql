using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;
using Glacier.Sql.Storage.Wal;

namespace Glacier.Sql.Engine
{
    public partial class SqlEngine
    {
        private async Task<ExecuteResult> ExecuteCreate(CreateTableStatement create, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (catalog.TableExists(create.TableName))
            {
                return ExecuteResult.Error($"Table '{create.TableName}' already exists.");
            }

            using (await AcquireLockAsync(create.TableName, exclusive: true, context))
            {
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

                if (context.ActiveTransaction != null)
                {
                    var meta = catalog.AddTableInMemory(create.TableName, columns);
                    context.ActiveTransaction.RegisterRollback(() =>
                    {
                        catalog.RemoveTableInMemory(create.TableName, out _, out _, out var detached);
                        detached?.Dispose();
                        catalog.PruneTableLock(create.TableName);
                    });
                    context.ActiveTransaction.RegisterCommit(() =>
                    {
                        if (!File.Exists(meta.BackingFile))
                        {
                            var emptyDf = TableStorage.CreateEmptyDataFrame(meta.Columns);
                            TableStorage.WriteTable(emptyDf, meta.BackingFile, bufferPool: null);
                        }
                        catalog.Save();
                    });
                }
                else
                {
                    catalog.AddTable(create.TableName, columns);
                }

                return ExecuteResult.Ok($"Table '{create.TableName}' created successfully.");
            }
        }

        private async Task<ExecuteResult> ExecuteDrop(DropTableStatement drop, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (!catalog.TableExists(drop.TableName))
            {
                return ExecuteResult.Error($"Table '{drop.TableName}' does not exist.");
            }

            using (await AcquireLockAsync(drop.TableName, exclusive: true, context))
            {
                if (!catalog.TableExists(drop.TableName))
                {
                    return ExecuteResult.Error($"Table '{drop.TableName}' does not exist.");
                }

                if (context.ActiveTransaction != null)
                {
                    catalog.RemoveTableInMemory(drop.TableName, out var removedMeta, out var removedTriggers, out var detachedEntry);
                    if (removedMeta != null)
                    {
                        context.ActiveTransaction.RegisterRollback(() =>
                        {
                            catalog.RestoreTableInMemory(removedMeta, removedTriggers, detachedEntry);
                        });
                        context.ActiveTransaction.RegisterCommit(() =>
                        {
                            catalog.WalWriter.AppendRecord(WalRecordType.TableDrop, Guid.Empty, removedMeta.TableId, removedMeta.TableName, ReadOnlySpan<byte>.Empty);
                            catalog.FinalizeTableDrop(removedMeta, detachedEntry);
                        });
                    }
                }
                else
                {
                    var meta = catalog.GetTable(drop.TableName);
                    catalog.RemoveTable(drop.TableName);
                    if (meta != null)
                    {
                        catalog.WalWriter.AppendRecord(WalRecordType.TableDrop, Guid.Empty, meta.TableId, meta.TableName, ReadOnlySpan<byte>.Empty);
                    }
                }

                return ExecuteResult.Ok($"Table '{drop.TableName}' dropped successfully.");
            }
        }

        private async Task<ExecuteResult> ExecuteAlter(AlterTableStatement alter, ExecutionContext context)
        {
            var catalog = context.Catalog;
            if (!catalog.TableExists(alter.TableName))
            {
                return ExecuteResult.Error($"Table '{alter.TableName}' does not exist.");
            }

            using (await AcquireLockAsync(alter.TableName, exclusive: true, context))
            {
                var meta = catalog.GetTable(alter.TableName);
                if (meta == null)
                {
                    return ExecuteResult.Error($"Table '{alter.TableName}' does not exist.");
                }

                var alterEntry = catalog.BufferPool.GetOrLoad(meta);
                DataFrame df;
                using (alterEntry.AcquireReadLock())
                {
                    df = alterEntry.CurrentSnapshot;
                }

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

                    var oldColumns = new List<ColumnMetadata>(meta.Columns);

                    var newColMeta = new ColumnMetadata
                    {
                        Name = colDef.Name,
                        DataType = colDef.DataType,
                        IsNullable = colDef.IsNullable,
                        IsPrimaryKey = colDef.IsPrimaryKey,
                        IsUnique = colDef.IsUnique,
                        CheckExpression = colDef.CheckExpression
                    };

                    var newSeries = CreateNullSeries(colDef.Name, colDef.DataType, df.RowCount);
                    var newCols = df.Columns.Concat(new[] { newSeries }).ToList();
                    var newDf = new DataFrame(newCols);

                    if (context.ActiveTransaction != null)
                    {
                        BackupForTransaction(meta, catalog, context, alterEntry);
                        context.ActiveTransaction.RegisterRollback(() =>
                        {
                            meta.Columns.Clear();
                            meta.Columns.AddRange(oldColumns);
                            catalog.InvalidateAllViewSchemas();
                        });

                        meta.Columns.Add(newColMeta);
                        catalog.InvalidateAllViewSchemas();

                        using (alterEntry.AcquireWriteLock())
                        {
                            byte[] payload = TableStorage.SerializeDataFrame(newDf);
                            Guid txId = context.ActiveTransaction.TransactionGuid;
                            ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowUpdate, txId, meta.TableId, meta.TableName, payload);
                            alterEntry.ApplyMutation(newDf, lsn, txId);
                        }

                        context.ActiveTransaction.RegisterCommit(() =>
                        {
                            PersistAlteredTable(meta, alterEntry.CommittedSnapshot, alterEntry, catalog);
                        });
                    }
                    else
                    {
                        meta.Columns.Add(newColMeta);
                        catalog.InvalidateAllViewSchemas();
                        PersistAlteredTable(meta, newDf, alterEntry, catalog);
                    }

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

                    var oldColumns = new List<ColumnMetadata>(meta.Columns);

                    var newCols = df.Columns.Where(c => !c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase)).ToList();
                    var newDf = new DataFrame(newCols);

                    if (context.ActiveTransaction != null)
                    {
                        BackupForTransaction(meta, catalog, context, alterEntry);
                        context.ActiveTransaction.RegisterRollback(() =>
                        {
                            meta.Columns.Clear();
                            meta.Columns.AddRange(oldColumns);
                            catalog.InvalidateAllViewSchemas();
                        });

                        meta.Columns.Remove(targetCol);
                        catalog.InvalidateAllViewSchemas();

                        using (alterEntry.AcquireWriteLock())
                        {
                            byte[] payload = TableStorage.SerializeDataFrame(newDf);
                            Guid txId = context.ActiveTransaction.TransactionGuid;
                            ulong lsn = catalog.WalWriter.AppendRecord(WalRecordType.RowUpdate, txId, meta.TableId, meta.TableName, payload);
                            alterEntry.ApplyMutation(newDf, lsn, txId);
                        }

                        context.ActiveTransaction.RegisterCommit(() =>
                        {
                            PersistAlteredTable(meta, alterEntry.CommittedSnapshot, alterEntry, catalog);
                        });
                    }
                    else
                    {
                        meta.Columns.Remove(targetCol);
                        catalog.InvalidateAllViewSchemas();
                        PersistAlteredTable(meta, newDf, alterEntry, catalog);
                    }

                    return ExecuteResult.Ok($"Column '{colName}' dropped from table '{alter.TableName}' successfully.");
                }
                else
                {
                    return ExecuteResult.Error($"Unsupported alter action: {alter.AlterAction}");
                }
            }
        }

        private static void PersistAlteredTable(
            TableMetadata meta,
            DataFrame newDf,
            Storage.BufferPool.TableBufferEntry alterEntry,
            CatalogManager catalog)
        {
            TableStorage.WriteTable(newDf, meta.BackingFile);

            ulong ckLsn = catalog.WalWriter.AppendRecord(
                WalRecordType.CheckpointEnd,
                Guid.Empty,
                meta.TableId,
                meta.TableName,
                ReadOnlySpan<byte>.Empty);

            using (alterEntry.AcquireWriteLock())
            {
                alterEntry.CurrentSnapshot = newDf;
                alterEntry.CommittedSnapshot = newDf;
                alterEntry.LastAppliedLsn = ckLsn;
                alterEntry.LastCommittedLsn = ckLsn;
                alterEntry.HasUnflushedCommittedChanges = false;
                alterEntry.FirstUnflushedLsn = 0;
                alterEntry.FirstUncommittedTxLsn = 0;
                alterEntry.IsDirty = false;
            }

            catalog.BufferPool.AdvanceSafeCheckpointLsn(catalog.WalWriter, ckLsn);
            catalog.Save();
        }

        private async Task<ExecuteResult> ExecuteCreateView(CreateViewStatement createView, ExecutionContext context)
        {
            var catalog = context.Catalog;
            using (await AcquireLockAsync("__view__:" + createView.ViewName, exclusive: true, context))
            {
                if (catalog.TableExists(createView.ViewName))
                {
                    return ExecuteResult.Error($"A table named '{createView.ViewName}' already exists.");
                }
                if (catalog.ViewExists(createView.ViewName))
                {
                    return ExecuteResult.Error($"View '{createView.ViewName}' already exists.");
                }

                if (context.ActiveTransaction != null)
                {
                    catalog.AddViewInMemory(createView.ViewName, createView.DefinitionSql);
                    context.ActiveTransaction.RegisterRollback(() => catalog.RemoveViewInMemory(createView.ViewName));
                    context.ActiveTransaction.RegisterCommit(() => catalog.Save());
                }
                else
                {
                    catalog.AddView(createView.ViewName, createView.DefinitionSql);
                }

                return ExecuteResult.Ok($"View '{createView.ViewName}' created successfully.");
            }
        }

        private async Task<ExecuteResult> ExecuteDropView(DropViewStatement dropView, ExecutionContext context)
        {
            var catalog = context.Catalog;
            using (await AcquireLockAsync("__view__:" + dropView.ViewName, exclusive: true, context))
            {
                if (!catalog.ViewExists(dropView.ViewName))
                {
                    return ExecuteResult.Error($"View '{dropView.ViewName}' does not exist.");
                }

                if (context.ActiveTransaction != null)
                {
                    var removed = catalog.RemoveViewInMemory(dropView.ViewName);
                    if (removed != null)
                    {
                        context.ActiveTransaction.RegisterRollback(() => catalog.RestoreViewInMemory(removed));
                        context.ActiveTransaction.RegisterCommit(() => catalog.Save());
                    }
                }
                else
                {
                    catalog.RemoveView(dropView.ViewName);
                }

                return ExecuteResult.Ok($"View '{dropView.ViewName}' dropped successfully.");
            }
        }
    }
}
