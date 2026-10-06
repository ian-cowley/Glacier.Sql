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
        private readonly CatalogManager _catalog;

        public QueryPlanner(CatalogManager catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        public LazyFrame PlanQuery(SelectStatement select, DataFrame? inserted = null, DataFrame? deleted = null)
        {
            if (select.From == null)
            {
                // Queries without FROM (e.g. SELECT 1 + 2)
                // We construct a dummy single-row DataFrame to run projections on
                var dummyCols = new List<Polaris.ISeries> { new Polaris.Data.Int32Series("dummy", new[] { 1 }) };
                var dummyDf = new DataFrame(dummyCols);
                var lazy = dummyDf.Lazy();
                
                var dummyProj = select.Projections.Select(p => CompileExpression(p.Expression, null, false, inserted, deleted).Alias(p.Alias ?? "result")).ToArray();
                return lazy.Select(dummyProj);
            }

            // 1. Resolve FROM clause
            if (select.From is not SqlTableSource source)
            {
                throw new NotSupportedException("Only standard table sources are supported in FROM clause currently.");
            }

            TableMetadata? tableMeta = null;
            LazyFrame? currentLazy = null;

            if (source.TableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
            {
                if (inserted == null)
                {
                    throw new Exception("The virtual table 'inserted' is only available inside trigger context.");
                }
                tableMeta = CreateVirtualTableMetadata("inserted", inserted);
                currentLazy = inserted.Lazy();
            }
            else if (source.TableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                if (deleted == null)
                {
                    throw new Exception("The virtual table 'deleted' is only available inside trigger context.");
                }
                tableMeta = CreateVirtualTableMetadata("deleted", deleted);
                currentLazy = deleted.Lazy();
            }
            else if (source.TableName.Equals("INFORMATION_SCHEMA.TABLES", StringComparison.OrdinalIgnoreCase))
            {
                var tableNames = _catalog.ListTables().Select(t => t.TableName).ToArray();
                var s = new Polaris.Data.Utf8StringSeries("TABLE_NAME", tableNames);
                var transDf = new DataFrame(new List<ISeries> { s });
                tableMeta = new TableMetadata
                {
                    TableName = "INFORMATION_SCHEMA.TABLES",
                    Columns = new List<ColumnMetadata> { new ColumnMetadata { Name = "TABLE_NAME", DataType = "VARCHAR" } },
                    BackingFile = ""
                };
                currentLazy = transDf.Lazy();
            }
            else if (source.TableName.Equals("INFORMATION_SCHEMA.COLUMNS", StringComparison.OrdinalIgnoreCase))
            {
                var tableNames = new List<string>();
                var columnNames = new List<string>();
                var dataTypes = new List<string>();
                var isNullables = new List<string>();

                foreach (var t in _catalog.ListTables())
                {
                    foreach (var c in t.Columns)
                    {
                        tableNames.Add(t.TableName);
                        columnNames.Add(c.Name);
                        dataTypes.Add(c.DataType);
                        isNullables.Add(c.IsNullable ? "YES" : "NO");
                    }
                }

                var sTable = new Polaris.Data.Utf8StringSeries("TABLE_NAME", tableNames.ToArray());
                var sColumn = new Polaris.Data.Utf8StringSeries("COLUMN_NAME", columnNames.ToArray());
                var sDataType = new Polaris.Data.Utf8StringSeries("DATA_TYPE", dataTypes.ToArray());
                var sNullable = new Polaris.Data.Utf8StringSeries("IS_NULLABLE", isNullables.ToArray());

                var transDf = new DataFrame(new List<ISeries> { sTable, sColumn, sDataType, sNullable });
                tableMeta = new TableMetadata
                {
                    TableName = "INFORMATION_SCHEMA.COLUMNS",
                    Columns = new List<ColumnMetadata>
                    {
                        new ColumnMetadata { Name = "TABLE_NAME", DataType = "VARCHAR" },
                        new ColumnMetadata { Name = "COLUMN_NAME", DataType = "VARCHAR" },
                        new ColumnMetadata { Name = "DATA_TYPE", DataType = "VARCHAR" },
                        new ColumnMetadata { Name = "IS_NULLABLE", DataType = "VARCHAR" }
                    },
                    BackingFile = ""
                };
                currentLazy = transDf.Lazy();
            }
            else if (_catalog.ViewExists(source.TableName))
            {
                var viewMeta = _catalog.GetView(source.TableName)!;
                var lexer = new SqlLexer(viewMeta.DefinitionSql);
                var tokens = lexer.Tokenize();
                var parser = new TSqlParser(tokens);
                var viewSelect = (SelectStatement)parser.Parse();

                var recursiveJoinLazy = PlanQuery(viewSelect, inserted, deleted);
                var schemaDf = recursiveJoinLazy.Limit(0).Collect().GetAwaiter().GetResult();
                var viewCols = schemaDf.Columns.Select(c => new ColumnMetadata 
                { 
                    Name = c.Name, 
                    DataType = GetSqlDataType(c) 
                }).ToList();

                tableMeta = new TableMetadata
                {
                    TableName = source.TableName,
                    Columns = viewCols,
                    BackingFile = ""
                };
                currentLazy = recursiveJoinLazy;
            }
            else
            {
                tableMeta = _catalog.GetTable(source.TableName);
                if (tableMeta == null)
                {
                    throw new Exception($"Table '{source.TableName}' does not exist in catalog.");
                }
                var entry = _catalog.BufferPool.GetOrLoad(tableMeta);
                var df = entry.CurrentSnapshot;
                currentLazy = df.Lazy();
            }

            // Track active schemas and aliases for column resolution
            var activeTables = new List<(string Name, string? Alias, TableMetadata Metadata)>
            {
                (source.TableName, source.Alias, tableMeta)
            };

            // 2. Process JOINS
            foreach (var join in select.Joins)
            {
                if (join.Table is not SqlTableSource joinSource)
                {
                    throw new NotSupportedException("Only standard table sources are supported in JOIN clause.");
                }

                TableMetadata? joinTableMeta = null;
                LazyFrame? joinLazy = null;

                if (joinSource.TableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
                {
                    if (inserted == null)
                    {
                        throw new Exception("The virtual table 'inserted' is only available inside trigger context.");
                    }
                    joinTableMeta = CreateVirtualTableMetadata("inserted", inserted);
                    joinLazy = inserted.Lazy();
                }
                else if (joinSource.TableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
                {
                    if (deleted == null)
                    {
                        throw new Exception("The virtual table 'deleted' is only available inside trigger context.");
                    }
                    joinTableMeta = CreateVirtualTableMetadata("deleted", deleted);
                    joinLazy = deleted.Lazy();
                }
                else if (joinSource.TableName.Equals("INFORMATION_SCHEMA.TABLES", StringComparison.OrdinalIgnoreCase))
                {
                    var tableNames = _catalog.ListTables().Select(t => t.TableName).ToArray();
                    var s = new Polaris.Data.Utf8StringSeries("TABLE_NAME", tableNames);
                    var transDf = new DataFrame(new List<ISeries> { s });
                    joinTableMeta = new TableMetadata
                    {
                        TableName = "INFORMATION_SCHEMA.TABLES",
                        Columns = new List<ColumnMetadata> { new ColumnMetadata { Name = "TABLE_NAME", DataType = "VARCHAR" } },
                        BackingFile = ""
                    };
                    joinLazy = transDf.Lazy();
                }
                else if (joinSource.TableName.Equals("INFORMATION_SCHEMA.COLUMNS", StringComparison.OrdinalIgnoreCase))
                {
                    var tableNames = new List<string>();
                    var columnNames = new List<string>();
                    var dataTypes = new List<string>();
                    var isNullables = new List<string>();

                    foreach (var t in _catalog.ListTables())
                    {
                        foreach (var c in t.Columns)
                        {
                            tableNames.Add(t.TableName);
                            columnNames.Add(c.Name);
                            dataTypes.Add(c.DataType);
                            isNullables.Add(c.IsNullable ? "YES" : "NO");
                        }
                    }

                    var sTable = new Polaris.Data.Utf8StringSeries("TABLE_NAME", tableNames.ToArray());
                    var sColumn = new Polaris.Data.Utf8StringSeries("COLUMN_NAME", columnNames.ToArray());
                    var sDataType = new Polaris.Data.Utf8StringSeries("DATA_TYPE", dataTypes.ToArray());
                    var sNullable = new Polaris.Data.Utf8StringSeries("IS_NULLABLE", isNullables.ToArray());

                    var transDf = new DataFrame(new List<ISeries> { sTable, sColumn, sDataType, sNullable });
                    joinTableMeta = new TableMetadata
                    {
                        TableName = "INFORMATION_SCHEMA.COLUMNS",
                        Columns = new List<ColumnMetadata>
                        {
                            new ColumnMetadata { Name = "TABLE_NAME", DataType = "VARCHAR" },
                            new ColumnMetadata { Name = "COLUMN_NAME", DataType = "VARCHAR" },
                            new ColumnMetadata { Name = "DATA_TYPE", DataType = "VARCHAR" },
                            new ColumnMetadata { Name = "IS_NULLABLE", DataType = "VARCHAR" }
                        },
                        BackingFile = ""
                    };
                    joinLazy = transDf.Lazy();
                }
                else if (_catalog.ViewExists(joinSource.TableName))
                {
                    var viewMeta = _catalog.GetView(joinSource.TableName)!;
                    var lexer = new SqlLexer(viewMeta.DefinitionSql);
                    var tokens = lexer.Tokenize();
                    var parser = new TSqlParser(tokens);
                    var viewSelect = (SelectStatement)parser.Parse();

                    var recursiveJoinLazy = PlanQuery(viewSelect, inserted, deleted);
                    var schemaDf = recursiveJoinLazy.Limit(0).Collect().GetAwaiter().GetResult();
                    var viewCols = schemaDf.Columns.Select(c => new ColumnMetadata 
                    { 
                        Name = c.Name, 
                        DataType = GetSqlDataType(c) 
                    }).ToList();

                    joinTableMeta = new TableMetadata
                    {
                        TableName = joinSource.TableName,
                        Columns = viewCols,
                        BackingFile = ""
                    };
                    joinLazy = recursiveJoinLazy;
                }
                else
                {
                    joinTableMeta = _catalog.GetTable(joinSource.TableName);
                    if (joinTableMeta == null)
                    {
                        throw new Exception($"Joined table '{joinSource.TableName}' does not exist in catalog.");
                    }
                    var joinEntry = _catalog.BufferPool.GetOrLoad(joinTableMeta);
                    var joinDf = joinEntry.CurrentSnapshot;
                    joinLazy = joinDf.Lazy();
                }

                activeTables.Add((joinSource.TableName, joinSource.Alias, joinTableMeta));

                if (join.JoinType.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
                {
                    currentLazy = currentLazy.Join(joinLazy, on: "", JoinType.Cross);
                }
                else
                {
                    // INNER or LEFT join
                    if (join.On == null)
                    {
                        throw new Exception($"ON clause is required for {join.JoinType} JOIN.");
                    }

                    // Extract join keys from ON clause (must be Equality comparison)
                    if (join.On is not SqlBinaryExpression binary || !binary.Operator.Equals("=", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException("Only equality join predicates (e.g. t1.id = t2.id) are supported currently.");
                    }

                    if (binary.Left is not SqlColumnRef leftCol || binary.Right is not SqlColumnRef rightCol)
                    {
                        throw new NotSupportedException("Join predicates must reference columns on both sides.");
                    }

                    // Resolve the actual column names in the backing dataframes
                    string leftKeyName = ResolveColumnName(leftCol, activeTables.SkipLast(1).ToList());
                    string rightKeyName = ResolveColumnName(rightCol, new[] { activeTables.Last() }.ToList());

                    // Polaris requires the same column name for joins.
                    // If they differ, rename the right column to match the left column.
                    if (leftKeyName != rightKeyName)
                    {
                        joinLazy = joinLazy.Rename(new Dictionary<string, string> { { rightKeyName, leftKeyName } });
                    }

                    var jt = join.JoinType.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ? JoinType.Left : JoinType.Inner;
                    currentLazy = currentLazy.Join(joinLazy, on: leftKeyName, jt);
                }
            }

            // 3. Process WHERE clause
            if (select.Where != null)
            {
                var conjuncts = new List<SqlExpression>();
                SplitConjuncts(select.Where, conjuncts);

                var nonExistsConjuncts = new List<SqlExpression>();

                foreach (var conjunct in conjuncts)
                {
                    bool isExists = conjunct is SqlExistsExpression;
                    bool isNotExists = conjunct is SqlUnaryExpression unaryExpr && 
                                       unaryExpr.Operator.Equals("NOT", StringComparison.OrdinalIgnoreCase) && 
                                       unaryExpr.Operand is SqlExistsExpression;

                    if (isExists || isNotExists)
                    {
                        var existsExpr = isExists ? (SqlExistsExpression)conjunct : (SqlExistsExpression)((SqlUnaryExpression)conjunct).Operand;
                        var subQuery = existsExpr.SelectQuery;

                        var innerActiveTables = GetActiveTablesForSelect(subQuery);

                        List<(SqlColumnRef innerCol, SqlColumnRef outerCol)> correlations = new();
                        SqlExpression? remainingWhere = null;

                        if (subQuery.Where != null)
                        {
                            ExtractCorrelations(subQuery.Where, activeTables, innerActiveTables, correlations, out remainingWhere);
                        }

                        // Update subquery WHERE clause without correlations
                        subQuery.Where = remainingWhere;

                        // Ensure inner key is projected in the subquery
                        if (correlations.Count > 0)
                        {
                            var (innerCol, outerCol) = correlations[0];
                            bool alreadyProjected = subQuery.Projections.Any(p => 
                                 p.Expression is SqlColumnRef col && 
                                 col.ColumnName.Equals(innerCol.ColumnName, StringComparison.OrdinalIgnoreCase) &&
                                 (col.Prefix == null || innerCol.Prefix == null || col.Prefix.Equals(innerCol.Prefix, StringComparison.OrdinalIgnoreCase)));

                            if (!alreadyProjected)
                            {
                                subQuery.Projections.Add(new SelectItem(innerCol, null));
                            }
                        }

                        var subLazy = PlanQuery(subQuery, inserted, deleted);

                        if (correlations.Count > 0)
                        {
                            var (innerCol, outerCol) = correlations[0];
                            string outerKey = ResolveColumnName(outerCol, activeTables);
                            string innerKey = ResolveColumnName(innerCol, innerActiveTables);

                            if (outerKey != innerKey)
                            {
                                subLazy = subLazy.Rename(new Dictionary<string, string> { { innerKey, outerKey } });
                            }

                            currentLazy = currentLazy.Join(subLazy, on: outerKey, isExists ? JoinType.Semi : JoinType.Anti);
                        }
                        else
                        {
                            // Uncorrelated EXISTS
                            var subDf = subLazy.Collect().GetAwaiter().GetResult();
                            bool existsResult = subDf.RowCount > 0;
                            bool conjunctTrue = isExists ? existsResult : !existsResult;

                            if (!conjunctTrue)
                            {
                                currentLazy = currentLazy.Filter(Expr.Lit(false));
                            }
                        }
                    }
                    else
                    {
                        nonExistsConjuncts.Add(conjunct);
                    }
                }

                if (nonExistsConjuncts.Count > 0)
                {
                    SqlExpression? finalPredicateExpr = null;
                    foreach (var expr in nonExistsConjuncts)
                    {
                        if (finalPredicateExpr == null)
                        {
                            finalPredicateExpr = expr;
                        }
                        else
                        {
                            finalPredicateExpr = new SqlBinaryExpression(finalPredicateExpr, "AND", expr);
                        }
                    }

                    if (finalPredicateExpr != null)
                    {
                        var predicate = CompileExpression(finalPredicateExpr, activeTables, false, inserted, deleted);
                        currentLazy = currentLazy.Filter(predicate);
                    }
                }
            }

            // 4. Process Grouping and Aggregations
            bool isAggregateQuery = select.GroupBy.Count > 0 || 
                                    select.Projections.Any(p => HasAggregateFunction(p.Expression));

            if (isAggregateQuery)
            {
                var groupColumns = select.GroupBy
                    .Select(g =>
                    {
                        if (g is not SqlColumnRef col)
                            throw new NotSupportedException("GROUP BY only supports direct column references.");
                        return ResolveColumnName(col, activeTables);
                    })
                    .ToArray();

                if (groupColumns.Length > 0)
                {
                    var aggProjections = new List<Expr>();
                    var finalProjections = new List<Expr>();

                    for (int i = 0; i < select.Projections.Count; i++)
                    {
                        var proj = select.Projections[i];
                        string finalAlias = proj.Alias ?? proj.Expression.ToString() ?? "result";

                        if (HasAggregateFunction(proj.Expression))
                        {
                            var compiledAgg = CompileExpression(proj.Expression, activeTables, isAggregating: true, inserted, deleted);
                            string tempAlias = $"__agg_{i}";
                            aggProjections.Add(compiledAgg.Alias(tempAlias));
                            finalProjections.Add(Expr.Col(tempAlias).Alias(finalAlias));
                        }
                        else
                        {
                            var compiledFinal = CompileExpression(proj.Expression, activeTables, isAggregating: false, inserted, deleted);
                            finalProjections.Add(compiledFinal.Alias(finalAlias));
                        }
                    }

                    currentLazy = currentLazy.GroupBy(groupColumns).Agg(aggProjections.ToArray());
                    currentLazy = currentLazy.Select(finalProjections.ToArray());
                }
                else
                {
                    // Global aggregation without GROUP BY. Run in Select directly.
                    var aggProjections = new List<Expr>();
                    foreach (var proj in select.Projections)
                    {
                        var compiled = CompileExpression(proj.Expression, activeTables, isAggregating: true, inserted, deleted);
                        string finalAlias = proj.Alias ?? proj.Expression.ToString() ?? "result";
                        aggProjections.Add(compiled.Alias(finalAlias));
                    }
                    currentLazy = currentLazy.Select(aggProjections.ToArray());
                }
            }
            else
            {
                // Standard query (no grouping or aggregation)
                // Expand projections (handles SELECT *)
                var projections = new List<Expr>();
                foreach (var proj in select.Projections)
                {
                    if (proj.Expression is SqlStarRef star)
                    {
                        // Expand *
                        var tablesToExpand = star.Prefix == null
                            ? activeTables
                            : activeTables.Where(t => t.Alias?.Equals(star.Prefix, StringComparison.OrdinalIgnoreCase) == true || 
                                                     t.Name.Equals(star.Prefix, StringComparison.OrdinalIgnoreCase));

                        foreach (var t in tablesToExpand)
                        {
                            foreach (var col in t.Metadata.Columns)
                            {
                                // Resolve physical name in the joined dataframe
                                string physicalName = ResolveColumnName(new SqlColumnRef(col.Name, t.Alias ?? t.Name), activeTables);
                                projections.Add(Expr.Col(physicalName).Alias(col.Name));
                            }
                        }
                    }
                    else
                    {
                        var compiled = CompileExpression(proj.Expression, activeTables, false, inserted, deleted);
                        if (proj.Alias != null)
                        {
                            compiled = compiled.Alias(proj.Alias);
                        }
                        else if (proj.Expression is SqlColumnRef colRef)
                        {
                            compiled = compiled.Alias(colRef.ColumnName);
                        }
                        projections.Add(compiled);
                    }
                }

                currentLazy = currentLazy.Select(projections.ToArray());
            }

            // 5. Process HAVING clause
            if (select.Having != null)
            {
                var havingPredicate = CompileExpression(select.Having, activeTables, false, inserted, deleted);
                currentLazy = currentLazy.Filter(havingPredicate);
            }

            // 6. Process ORDER BY clause
            if (select.OrderBy.Count > 0)
            {
                var sortCols = new List<string>();
                var sortDescs = new List<bool>();

                foreach (var order in select.OrderBy)
                {
                    if (order.Expression is not SqlColumnRef col)
                    {
                        throw new NotSupportedException("ORDER BY currently only supports direct column references.");
                    }
                    sortCols.Add(ResolveColumnName(col, activeTables));
                    sortDescs.Add(order.Descending);
                }

                currentLazy = currentLazy.Sort(sortCols.ToArray(), sortDescs.ToArray());
            }

            // 7. Process TOP clause (Limit)
            if (select.Top.HasValue)
            {
                currentLazy = currentLazy.Limit(select.Top.Value);
            }

            return currentLazy;
        }
    }
}
