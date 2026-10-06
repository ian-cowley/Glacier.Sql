using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;

namespace Glacier.Sql.Engine
{
    public partial class SqlEngine
    {
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
                var alterEntry = catalog.BufferPool.GetOrLoad(meta);
                var df = alterEntry.CurrentSnapshot;

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
                    using (alterEntry.AcquireWriteLock())
                    {
                        alterEntry.CurrentSnapshot = newDf;
                        alterEntry.IsDirty = false;
                    }
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
                    using (alterEntry.AcquireWriteLock())
                    {
                        alterEntry.CurrentSnapshot = newDf;
                        alterEntry.IsDirty = false;
                    }
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
