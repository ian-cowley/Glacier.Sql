using System;
using System.Collections.Generic;
using System.Linq;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;

namespace Glacier.Sql.Engine
{
    public partial class QueryPlanner
    {
        private string ResolveColumnName(SqlColumnRef colRef, List<(string Name, string? Alias, TableMetadata Metadata)> activeTables)
        {
            // If column has prefix (table name or alias)
            if (colRef.Prefix != null)
            {
                var matchedTable = activeTables.FirstOrDefault(t => 
                    (t.Alias != null && t.Alias.Equals(colRef.Prefix, StringComparison.OrdinalIgnoreCase)) ||
                    t.Name.Equals(colRef.Prefix, StringComparison.OrdinalIgnoreCase));

                if (matchedTable.Metadata == null)
                {
                    throw new Exception($"Unknown table alias/prefix '{colRef.Prefix}' for column '{colRef.ColumnName}'.");
                }

                // Check collision. If this column exists in other tables too, and it is in a joined table (not the first one),
                // it would be renamed to col_right in Polaris join output.
                int tableIndex = activeTables.IndexOf(matchedTable);
                bool existsInLeft = activeTables.Take(tableIndex).Any(t => t.Metadata.Columns.Any(c => c.Name.Equals(colRef.ColumnName, StringComparison.OrdinalIgnoreCase)));

                if (tableIndex > 0 && existsInLeft)
                {
                    return $"{colRef.ColumnName}_right";
                }
                return colRef.ColumnName;
            }

            // Unprefixed column reference. Search in all tables.
            var candidates = activeTables.Where(t => t.Metadata.Columns.Any(c => c.Name.Equals(colRef.ColumnName, StringComparison.OrdinalIgnoreCase))).ToList();
            if (candidates.Count == 0)
            {
                throw new Exception($"Column '{colRef.ColumnName}' does not exist in any active table schemas.");
            }

            var primaryTable = candidates[0];
            int idx = activeTables.IndexOf(primaryTable);
            bool hasColLeft = activeTables.Take(idx).Any(t => t.Metadata.Columns.Any(c => c.Name.Equals(colRef.ColumnName, StringComparison.OrdinalIgnoreCase)));

            if (idx > 0 && hasColLeft)
            {
                return $"{colRef.ColumnName}_right";
            }
            return colRef.ColumnName;
        }

        private string GetSqlDataType(ISeries col)
        {
            var type = col.DataType;
            string className = col.GetType().Name;
            if (className.Contains("Int32") || type == typeof(int)) return "INT";
            if (className.Contains("Float64") || className.Contains("Double") || type == typeof(double)) return "FLOAT";
            if (className.Contains("Boolean") || className.Contains("Bool") || type == typeof(bool)) return "BIT";
            if (className.Contains("Time") || className.Contains("Date") || className.Contains("DateTime") || type == typeof(long)) return "DATETIME";
            return "VARCHAR";
        }

        private TableMetadata CreateVirtualTableMetadata(string tableName, DataFrame df)
        {
            var cols = new List<ColumnMetadata>();
            foreach (var col in df.Columns)
            {
                string sqlDataType = "VARCHAR";
                var type = col.DataType;
                string className = col.GetType().Name;
                if (className.Contains("Int32") || type == typeof(int))
                {
                    sqlDataType = "INT";
                }
                else if (className.Contains("Float64") || className.Contains("Double") || type == typeof(double))
                {
                    sqlDataType = "FLOAT";
                }
                else if (className.Contains("Boolean") || className.Contains("Bool") || type == typeof(bool))
                {
                    sqlDataType = "BIT";
                }
                else if (className.Contains("Time") || className.Contains("Date") || className.Contains("DateTime"))
                {
                    sqlDataType = "DATETIME";
                }
                else if (className.Contains("Utf8") || className.Contains("String") || type == typeof(string))
                {
                    sqlDataType = "VARCHAR";
                }

                cols.Add(new ColumnMetadata { Name = col.Name, DataType = sqlDataType });
            }

            return new TableMetadata
            {
                TableName = tableName,
                Columns = cols,
                BackingFile = ""
            };
        }

        private void SplitConjuncts(SqlExpression expr, List<SqlExpression> conjuncts)
        {
            if (expr is SqlBinaryExpression bin && bin.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                SplitConjuncts(bin.Left, conjuncts);
                SplitConjuncts(bin.Right, conjuncts);
            }
            else
            {
                conjuncts.Add(expr);
            }
        }

        private List<(string Name, string? Alias, TableMetadata Metadata)> GetActiveTablesForSelect(SelectStatement select)
        {
            var active = new List<(string Name, string? Alias, TableMetadata Metadata)>();
            if (select.From is SqlTableSource source)
            {
                TableMetadata? meta = null;
                if (source.TableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
                {
                    meta = new TableMetadata { TableName = "inserted", Columns = new() };
                }
                else if (source.TableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                {
                    meta = new TableMetadata { TableName = "deleted", Columns = new() };
                }
                else if (_catalog.ViewExists(source.TableName))
                {
                    meta = new TableMetadata { TableName = source.TableName, Columns = new() };
                }
                else
                {
                    meta = _catalog.GetTable(source.TableName);
                }

                if (meta != null)
                {
                    active.Add((source.TableName, source.Alias, meta));
                }
            }

            foreach (var join in select.Joins)
            {
                if (join.Table is SqlTableSource joinSource)
                {
                    TableMetadata? meta = null;
                    if (joinSource.TableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
                    {
                        meta = new TableMetadata { TableName = "inserted", Columns = new() };
                    }
                    else if (joinSource.TableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                    {
                        meta = new TableMetadata { TableName = "deleted", Columns = new() };
                    }
                    else
                    {
                        meta = _catalog.GetTable(joinSource.TableName);
                    }

                    if (meta != null)
                    {
                        active.Add((joinSource.TableName, joinSource.Alias, meta));
                    }
                }
            }

            return active;
        }

        private void ExtractCorrelations(
            SqlExpression expr, 
            List<(string Name, string? Alias, TableMetadata Metadata)> outerTables, 
            List<(string Name, string? Alias, TableMetadata Metadata)> innerTables, 
            List<(SqlColumnRef innerCol, SqlColumnRef outerCol)> correlations, 
            out SqlExpression? remainingExpr)
        {
            if (expr is SqlBinaryExpression bin)
            {
                if (bin.Operator.Equals("AND", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractCorrelations(bin.Left, outerTables, innerTables, correlations, out var leftRemaining);
                    ExtractCorrelations(bin.Right, outerTables, innerTables, correlations, out var rightRemaining);

                    if (leftRemaining == null)
                    {
                        remainingExpr = rightRemaining;
                    }
                    else if (rightRemaining == null)
                    {
                        remainingExpr = leftRemaining;
                    }
                    else
                    {
                        remainingExpr = new SqlBinaryExpression(leftRemaining, "AND", rightRemaining);
                    }
                    return;
                }
                else if (bin.Operator == "=")
                {
                    SqlColumnRef? leftCol = bin.Left as SqlColumnRef;
                    SqlColumnRef? rightCol = bin.Right as SqlColumnRef;

                    if (leftCol != null && rightCol != null)
                    {
                        bool leftIsInner = IsTableInList(leftCol.Prefix, innerTables);
                        bool leftIsOuter = IsTableInList(leftCol.Prefix, outerTables);
                        bool rightIsInner = IsTableInList(rightCol.Prefix, innerTables);
                        bool rightIsOuter = IsTableInList(rightCol.Prefix, outerTables);

                        if (leftIsInner && rightIsOuter)
                        {
                            correlations.Add((leftCol, rightCol));
                            remainingExpr = null;
                            return;
                        }
                        else if (leftIsOuter && rightIsInner)
                        {
                            correlations.Add((rightCol, leftCol));
                            remainingExpr = null;
                            return;
                        }
                    }
                }
            }

            remainingExpr = expr;
        }

        private bool IsTableInList(string? prefix, List<(string Name, string? Alias, TableMetadata Metadata)> tables)
        {
            if (prefix == null) return false;
            return tables.Any(t => 
                (t.Alias != null && t.Alias.Equals(prefix, StringComparison.OrdinalIgnoreCase)) ||
                t.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }
}
