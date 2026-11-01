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
                var dummyCols = new List<Polaris.ISeries> { new Polaris.Data.Int32Series("dummy", new[] { 1 }) };
                var dummyDf = new DataFrame(dummyCols);
                var lazy = dummyDf.Lazy();

                var dummyProj = select.Projections
                    .Select(p => CompileExpression(p.Expression, null, false, inserted, deleted).Alias(p.Alias ?? "result"))
                    .ToArray();
                return lazy.Select(dummyProj);
            }

            // 1. Resolve FROM clause
            if (select.From is not SqlTableSource source)
            {
                throw new NotSupportedException("Only standard table sources are supported in FROM clause currently.");
            }

            var (tableMeta, currentLazy) = ResolveTableSource(source.TableName, inserted, deleted, isJoinedTable: false);

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

                var (joinTableMeta, joinLazy) = ResolveTableSource(joinSource.TableName, inserted, deleted, isJoinedTable: true);
                activeTables.Add((joinSource.TableName, joinSource.Alias, joinTableMeta));

                if (join.JoinType.Equals("CROSS", StringComparison.OrdinalIgnoreCase))
                {
                    currentLazy = currentLazy.Join(joinLazy, on: "", JoinType.Cross);
                }
                else
                {
                    if (join.On == null)
                    {
                        throw new Exception($"ON clause is required for {join.JoinType} JOIN.");
                    }

                    if (join.On is not SqlBinaryExpression binary || !binary.Operator.Equals("=", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException("Only equality join predicates (e.g. t1.id = t2.id) are supported currently.");
                    }

                    if (binary.Left is not SqlColumnRef leftCol || binary.Right is not SqlColumnRef rightCol)
                    {
                        throw new NotSupportedException("Join predicates must reference columns on both sides.");
                    }

                    string leftKeyName = ResolveColumnName(leftCol, activeTables.SkipLast(1).ToList());
                    string rightKeyName = ResolveColumnName(rightCol, new[] { activeTables.Last() }.ToList());

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
                        var origSub = existsExpr.SelectQuery;
                        var subQuery = CloneSelectStatement(origSub);

                        var innerActiveTables = GetActiveTablesForSelect(subQuery);

                        List<(SqlColumnRef innerCol, SqlColumnRef outerCol)> correlations = new();
                        SqlExpression? remainingWhere = null;

                        if (subQuery.Where != null)
                        {
                            ExtractCorrelations(subQuery.Where, activeTables, innerActiveTables, correlations, out remainingWhere);
                        }

                        subQuery.Where = remainingWhere;

                        if (correlations.Count > 0)
                        {
                            var (innerCol, _) = correlations[0];
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
                        finalPredicateExpr = finalPredicateExpr == null
                            ? expr
                            : new SqlBinaryExpression(finalPredicateExpr, "AND", expr);
                    }

                    if (finalPredicateExpr != null)
                    {
                        var predicate = CompileExpression(finalPredicateExpr, activeTables, false, inserted, deleted);
                        currentLazy = currentLazy.Filter(predicate);
                    }
                }
            }

            // 4. Process Grouping, Aggregations, HAVING, ORDER BY, and Final Projections
            bool isAggregateQuery = select.GroupBy.Count > 0 ||
                                    select.Projections.Any(p => HasAggregateFunction(p.Expression)) ||
                                    (select.Having != null && HasAggregateFunction(select.Having));

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

                var groupColumnsSet = new HashSet<string>(groupColumns, StringComparer.OrdinalIgnoreCase);
                var extractedAggs = new List<(string Key, SqlFunctionCall Call, string TempAlias)>();
                var keyToTempAlias = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var proj in select.Projections)
                {
                    ExtractAggregateCalls(proj.Expression, extractedAggs, keyToTempAlias, activeTables);
                }

                if (select.Having != null)
                {
                    ExtractAggregateCalls(select.Having, extractedAggs, keyToTempAlias, activeTables);
                }

                foreach (var order in select.OrderBy)
                {
                    ExtractAggregateCalls(order.Expression, extractedAggs, keyToTempAlias, activeTables);
                }

                var aggProjections = new List<Expr>(extractedAggs.Count);
                foreach (var (_, call, tempAlias) in extractedAggs)
                {
                    var compiledAgg = CompileExpression(call, activeTables, isAggregating: true, inserted, deleted);
                    aggProjections.Add(compiledAgg.Alias(tempAlias));
                }

                if (groupColumns.Length > 0)
                {
                    currentLazy = currentLazy.GroupBy(groupColumns).Agg(aggProjections.ToArray());
                }
                else if (select.Having != null || select.OrderBy.Count > 0)
                {
                    var globalAggDf = currentLazy.Select(aggProjections.ToArray()).Collect().GetAwaiter().GetResult();
                    currentLazy = globalAggDf.Lazy();
                }
                else
                {
                    currentLazy = currentLazy.Select(aggProjections.ToArray());
                }

                var finalProjections = new List<Expr>(select.Projections.Count);
                var aliasExprMap = new Dictionary<string, Expr>(StringComparer.OrdinalIgnoreCase);
                var aliasDirectColMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var proj in select.Projections)
                {
                    string finalAlias = proj.Alias ?? (proj.Expression is SqlColumnRef cr ? cr.ColumnName : proj.Expression.ToString() ?? "result");
                    var postExpr = CompilePostAggregateExpression(proj.Expression, activeTables, keyToTempAlias, aliasExprMap: null, groupColumnsSet, inserted, deleted);
                    finalProjections.Add(postExpr.Alias(finalAlias));

                    if (proj.Alias != null)
                    {
                        aliasExprMap[proj.Alias] = postExpr;
                        if (proj.Expression is SqlFunctionCall fnCall && IsAggregateFunction(fnCall))
                        {
                            string key = GetAggregateCanonicalKey(fnCall, activeTables);
                            aliasDirectColMap[proj.Alias] = keyToTempAlias[key];
                        }
                        else if (proj.Expression is SqlColumnRef groupedRef)
                        {
                            aliasDirectColMap[proj.Alias] = ResolveColumnName(groupedRef, activeTables);
                        }
                    }
                    else if (proj.Expression is SqlColumnRef unaliasedRef)
                    {
                        string resolved = ResolveColumnName(unaliasedRef, activeTables);
                        aliasExprMap[unaliasedRef.ColumnName] = Expr.Col(resolved);
                        aliasDirectColMap[unaliasedRef.ColumnName] = resolved;
                    }
                }

                // 5a. HAVING on grouped/aggregated frame (before stripping helper __agg_* columns)
                if (select.Having != null)
                {
                    var havingPredicate = CompilePostAggregateExpression(select.Having, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                    currentLazy = currentLazy.Filter(havingPredicate);
                }

                // 6a. ORDER BY on grouped/aggregated frame (resolving aliases, grouped columns, and aggregates before stripping helpers)
                if (select.OrderBy.Count > 0)
                {
                    var sortCols = new List<string>(select.OrderBy.Count);
                    var sortDescs = new List<bool>(select.OrderBy.Count);
                    var extraSortCols = new List<Expr>();

                    for (int i = 0; i < select.OrderBy.Count; i++)
                    {
                        var order = select.OrderBy[i];
                        sortDescs.Add(order.Descending);

                        if (order.Expression is SqlColumnRef orderCol)
                        {
                            if (orderCol.Prefix == null && aliasDirectColMap.TryGetValue(orderCol.ColumnName, out var directCol))
                            {
                                sortCols.Add(directCol);
                            }
                            else if (orderCol.Prefix == null && aliasExprMap.TryGetValue(orderCol.ColumnName, out var computedAliasExpr))
                            {
                                string helperSort = $"__sort_{i}";
                                extraSortCols.Add(computedAliasExpr.Alias(helperSort));
                                sortCols.Add(helperSort);
                            }
                            else
                            {
                                string resolvedGrp = ResolveColumnName(orderCol, activeTables);
                                if (!groupColumnsSet.Contains(resolvedGrp))
                                {
                                    throw new Exception($"Column '{orderCol}' in ORDER BY must appear in the GROUP BY clause or match a SELECT alias.");
                                }
                                sortCols.Add(resolvedGrp);
                            }
                        }
                        else if (order.Expression is SqlFunctionCall fnCall && IsAggregateFunction(fnCall))
                        {
                            string key = GetAggregateCanonicalKey(fnCall, activeTables);
                            sortCols.Add(keyToTempAlias[key]);
                        }
                        else
                        {
                            var computedSortExpr = CompilePostAggregateExpression(order.Expression, activeTables, keyToTempAlias, aliasExprMap, groupColumnsSet, inserted, deleted);
                            string helperSort = $"__sort_{i}";
                            extraSortCols.Add(computedSortExpr.Alias(helperSort));
                            sortCols.Add(helperSort);
                        }
                    }

                    if (extraSortCols.Count > 0)
                    {
                        currentLazy = currentLazy.WithColumns(extraSortCols.ToArray());
                    }

                    currentLazy = currentLazy.Sort(sortCols.ToArray(), sortDescs.ToArray());
                }

                // Project final SELECT columns (stripping __agg_* and __sort_* helper columns)
                currentLazy = currentLazy.Select(finalProjections.ToArray());
            }
            else
            {
                // Standard query (no grouping or aggregation)
                var projections = new List<Expr>();
                foreach (var proj in select.Projections)
                {
                    if (proj.Expression is SqlStarRef star)
                    {
                        var tablesToExpand = star.Prefix == null
                            ? activeTables
                            : activeTables.Where(t => t.Alias?.Equals(star.Prefix, StringComparison.OrdinalIgnoreCase) == true ||
                                                      t.Name.Equals(star.Prefix, StringComparison.OrdinalIgnoreCase));

                        foreach (var t in tablesToExpand)
                        {
                            foreach (var col in t.Metadata.Columns)
                            {
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

                if (select.Having != null)
                {
                    var havingPredicate = CompileExpression(select.Having, activeTables, false, inserted, deleted);
                    currentLazy = currentLazy.Filter(havingPredicate);
                }

                if (select.OrderBy.Count > 0)
                {
                    var sortCols = new List<string>(select.OrderBy.Count);
                    var sortDescs = new List<bool>(select.OrderBy.Count);

                    foreach (var order in select.OrderBy)
                    {
                        if (order.Expression is not SqlColumnRef col)
                        {
                            throw new NotSupportedException("ORDER BY currently only supports direct column references.");
                        }

                        var matchedAlias = col.Prefix == null
                            ? select.Projections.FirstOrDefault(p => p.Alias != null && p.Alias.Equals(col.ColumnName, StringComparison.OrdinalIgnoreCase))
                            : null;

                        sortCols.Add(matchedAlias != null ? matchedAlias.Alias! : ResolveColumnName(col, activeTables));
                        sortDescs.Add(order.Descending);
                    }

                    currentLazy = currentLazy.Sort(sortCols.ToArray(), sortDescs.ToArray());
                }
            }

            // 7. Process TOP clause (Limit)
            if (select.Top.HasValue)
            {
                currentLazy = currentLazy.Limit(select.Top.Value);
            }

            return currentLazy;
        }

        private (TableMetadata Metadata, LazyFrame Frame) ResolveTableSource(string tableName, DataFrame? inserted, DataFrame? deleted, bool isJoinedTable)
        {
            if (tableName.Equals("inserted", StringComparison.OrdinalIgnoreCase))
            {
                if (inserted == null)
                {
                    throw new Exception("The virtual table 'inserted' is only available inside trigger context.");
                }
                return (CreateVirtualTableMetadata("inserted", inserted), inserted.Lazy());
            }

            if (tableName.Equals("deleted", StringComparison.OrdinalIgnoreCase))
            {
                if (deleted == null)
                {
                    throw new Exception("The virtual table 'deleted' is only available inside trigger context.");
                }
                return (CreateVirtualTableMetadata("deleted", deleted), deleted.Lazy());
            }

            if (tableName.Equals("INFORMATION_SCHEMA.TABLES", StringComparison.OrdinalIgnoreCase))
            {
                var tableNames = _catalog.ListTables().Select(t => t.TableName).ToArray();
                var s = new Polaris.Data.Utf8StringSeries("TABLE_NAME", tableNames);
                var transDf = new DataFrame(new List<ISeries> { s });
                var meta = new TableMetadata
                {
                    TableName = "INFORMATION_SCHEMA.TABLES",
                    Columns = new List<ColumnMetadata> { new ColumnMetadata { Name = "TABLE_NAME", DataType = "VARCHAR" } },
                    BackingFile = ""
                };
                return (meta, transDf.Lazy());
            }

            if (tableName.Equals("INFORMATION_SCHEMA.COLUMNS", StringComparison.OrdinalIgnoreCase))
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
                var meta = new TableMetadata
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
                return (meta, transDf.Lazy());
            }

            if (_catalog.ViewExists(tableName))
            {
                var viewMeta = _catalog.GetView(tableName)!;
                var lexer = new SqlLexer(viewMeta.DefinitionSql);
                var tokens = lexer.Tokenize();
                var parser = new TSqlParser(tokens);
                var viewSelect = (SelectStatement)parser.Parse();

                var recursiveLazy = PlanQuery(viewSelect, inserted, deleted);
                var schemaDf = recursiveLazy.Limit(0).Collect().GetAwaiter().GetResult();
                var viewCols = schemaDf.Columns.Select(c => new ColumnMetadata
                {
                    Name = c.Name,
                    DataType = GetSqlDataType(c)
                }).ToList();

                var meta = new TableMetadata
                {
                    TableName = tableName,
                    Columns = viewCols,
                    BackingFile = ""
                };
                return (meta, recursiveLazy);
            }

            var physMeta = _catalog.GetTable(tableName);
            if (physMeta == null)
            {
                throw new Exception(isJoinedTable
                    ? $"Joined table '{tableName}' does not exist in catalog."
                    : $"Table '{tableName}' does not exist in catalog.");
            }

            var entry = _catalog.BufferPool.GetOrLoad(physMeta);
            DataFrame df;
            using (entry.AcquireReadLock())
            {
                df = entry.CurrentSnapshot;
            }
            return (physMeta, df.Lazy());
        }

        private static SelectStatement CloneSelectStatement(SelectStatement src)
        {
            var clone = new SelectStatement
            {
                Top = src.Top,
                From = src.From,
                Where = src.Where,
                Having = src.Having
            };
            clone.Projections.AddRange(src.Projections);
            clone.Joins.AddRange(src.Joins);
            clone.GroupBy.AddRange(src.GroupBy);
            clone.OrderBy.AddRange(src.OrderBy);
            return clone;
        }
    }
}
