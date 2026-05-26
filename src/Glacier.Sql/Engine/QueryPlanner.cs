using System;
using System.Collections.Generic;
using System.Linq;
using Glacier.Polaris;
using Glacier.Sql.Catalog;
using Glacier.Sql.Parser;

namespace Glacier.Sql.Engine
{
    public class QueryPlanner
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

                var viewLazy = PlanQuery(viewSelect, inserted, deleted);
                var schemaDf = viewLazy.Limit(0).Collect().GetAwaiter().GetResult();
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
                currentLazy = viewLazy;
            }
            else
            {
                tableMeta = _catalog.GetTable(source.TableName);
                if (tableMeta == null)
                {
                    throw new Exception($"Table '{source.TableName}' does not exist in catalog.");
                }
                var df = TableStorage.ReadTable(tableMeta.BackingFile);
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
                    var joinDf = TableStorage.ReadTable(joinTableMeta.BackingFile);
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

        public Expr CompileExpression(SqlExpression expr, string tableName)
        {
            var tableMeta = _catalog.GetTable(tableName) ?? throw new Exception($"Table '{tableName}' does not exist.");
            var activeTables = new List<(string Name, string? Alias, TableMetadata Metadata)>
            {
                (tableName, null, tableMeta)
            };
            return CompileExpression(expr, activeTables);
        }

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

        private bool HasAggregateFunction(SqlExpression expr)
        {
            if (expr is SqlFunctionCall call)
            {
                string fn = call.FunctionName.ToUpperInvariant();
                if (fn == "SUM" || fn == "AVG" || fn == "MIN" || fn == "MAX" || fn == "COUNT")
                    return true;
            }
            if (expr is SqlBinaryExpression binary)
            {
                return HasAggregateFunction(binary.Left) || HasAggregateFunction(binary.Right);
            }
            if (expr is SqlUnaryExpression unary)
            {
                return HasAggregateFunction(unary.Operand);
            }
            return false;
        }

        private Expr CompileExpression(SqlExpression sqlExpr, List<(string Name, string? Alias, TableMetadata Metadata)>? activeTables, bool isAggregating = false, DataFrame? inserted = null, DataFrame? deleted = null)
        {
            switch (sqlExpr)
            {
                case SqlLiteral lit:
                    return Expr.Lit(lit.Value!);

                case SqlColumnRef col:
                    if (activeTables == null)
                    {
                        return Expr.Col(col.ColumnName);
                    }
                    string resolvedName = ResolveColumnName(col, activeTables);
                    return Expr.Col(resolvedName);

                case SqlSubqueryExpression subqueryExpr:
                    {
                        var subLazy = PlanQuery(subqueryExpr.SelectQuery, inserted, deleted);
                        var subDf = subLazy.Collect().GetAwaiter().GetResult();
                        object? val = null;
                        if (subDf.RowCount > 0 && subDf.Columns.Count > 0)
                        {
                            var firstCol = subDf.Columns[0];
                            if (firstCol.ValidityMask.IsValid(0))
                            {
                                val = firstCol.Get(0);
                            }
                        }
                        return Expr.Lit(val!);
                    }

                case SqlExistsExpression existsExpr:
                    {
                        var subLazy = PlanQuery(existsExpr.SelectQuery, inserted, deleted);
                        var subDf = subLazy.Collect().GetAwaiter().GetResult();
                        return Expr.Lit(subDf.RowCount > 0);
                    }

                case SqlInSubqueryExpression inSubqueryExpr:
                    {
                        var leftCompiled = CompileExpression(inSubqueryExpr.Left, activeTables, isAggregating, inserted, deleted);
                        var subLazy = PlanQuery(inSubqueryExpr.Subquery, inserted, deleted);
                        var subDf = subLazy.Collect().GetAwaiter().GetResult();

                        if (subDf.RowCount == 0 || subDf.Columns.Count == 0)
                        {
                            return Expr.Lit(false);
                        }

                        var subCol = subDf.Columns[0];
                        Expr? chain = null;
                        for (int i = 0; i < subCol.Length; i++)
                        {
                            if (subCol.ValidityMask.IsValid(i))
                            {
                                var val = subCol.Get(i);
                                if (val != null)
                                {
                                    var litVal = Expr.Lit(val);
                                    var eq = leftCompiled == litVal;
                                    if (chain is null)
                                    {
                                        chain = eq;
                                    }
                                    else
                                    {
                                        chain = chain | eq;
                                    }
                                }
                            }
                        }
                        return chain ?? Expr.Lit(false);
                    }

                case SqlInListExpression inListExpr:
                    {
                        var leftCompiled = CompileExpression(inListExpr.Left, activeTables, isAggregating, inserted, deleted);
                        Expr? chain = null;
                        foreach (var exprVal in inListExpr.List)
                        {
                            var rightCompiled = CompileExpression(exprVal, activeTables, isAggregating, inserted, deleted);
                            var eq = leftCompiled == rightCompiled;
                            if (chain is null)
                            {
                                chain = eq;
                            }
                            else
                            {
                                chain = chain | eq;
                            }
                        }
                        return chain ?? Expr.Lit(false);
                    }

                case SqlUnaryExpression unary:
                    var operand = CompileExpression(unary.Operand, activeTables, isAggregating, inserted, deleted);
                    if (unary.Operator.Equals("NOT", StringComparison.OrdinalIgnoreCase))
                    {
                        // logical NOT workaround: operand == false
                        return operand == Expr.Lit(false);
                    }
                    if (unary.Operator == "-")
                    {
                        return -operand;
                    }
                    throw new NotSupportedException($"Unary operator '{unary.Operator}' is not supported.");

                case SqlBinaryExpression binary:
                    var left = CompileExpression(binary.Left, activeTables, isAggregating, inserted, deleted);
                    var right = CompileExpression(binary.Right, activeTables, isAggregating, inserted, deleted);

                    return binary.Operator.ToUpperInvariant() switch
                    {
                        "+" => left + right,
                        "-" => left - right,
                        "*" => left * right,
                        "/" => left / right,
                        "=" => left == right,
                        "<>" => left != right,
                        "!=" => left != right,
                        ">" => left > right,
                        "<" => left < right,
                        ">=" => left >= right,
                        "<=" => left <= right,
                        "AND" => left & right,
                        "OR" => left | right,
                        "IS" => binary.Right is SqlLiteral l && l.Value == null ? left.IsNull() : left == right,
                        "IS NOT" => binary.Right is SqlLiteral l2 && l2.Value == null ? left.IsNotNull() : left != right,
                        _ => throw new NotSupportedException($"Binary operator '{binary.Operator}' is not supported.")
                    };

                case SqlFunctionCall call:
                    string fn = call.FunctionName.ToUpperInvariant();

                    // Aggregates
                    if (fn == "SUM")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("SUM expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Sum();
                    }
                    if (fn == "AVG")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("AVG expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Mean();
                    }
                    if (fn == "MIN")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("MIN expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Min();
                    }
                    if (fn == "MAX")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("MAX expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Max();
                    }
                    if (fn == "COUNT")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("COUNT expects exactly 1 argument.");
                        var arg = call.Arguments[0];
                        if (arg is SqlStarRef)
                        {
                            // COUNT(*) resolves to count of the first column of the active schemas
                            if (activeTables == null || activeTables.Count == 0)
                            {
                                return Expr.Lit(1).Count();
                            }
                            string firstColName = activeTables[0].Metadata.Columns[0].Name;
                            return Expr.Col(firstColName).Count();
                        }
                        return CompileExpression(arg, activeTables, isAggregating, inserted, deleted).Count();
                    }

                    // String functions
                    if (fn == "LEN" || fn == "LENGTH")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("LEN expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Str().Lengths();
                    }
                    if (fn == "UPPER")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("UPPER expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Str().ToUppercase();
                    }
                    if (fn == "LOWER")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("LOWER expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Str().ToLowercase();
                    }

                    // Math functions
                    if (fn == "ABS")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("ABS expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Abs();
                    }
                    if (fn == "SQRT")
                    {
                        if (call.Arguments.Count != 1) throw new Exception("SQRT expects exactly 1 argument.");
                        return CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted).Sqrt();
                    }
                    if (fn == "ROUND")
                    {
                        if (call.Arguments.Count < 1 || call.Arguments.Count > 2) throw new Exception("ROUND expects 1 or 2 arguments.");
                        var target = CompileExpression(call.Arguments[0], activeTables, isAggregating, inserted, deleted);
                        int decimals = 0;
                        if (call.Arguments.Count == 2 && call.Arguments[1] is SqlLiteral l && l.Value is int i)
                        {
                            decimals = i;
                        }
                        return target.Round(decimals);
                    }

                    // Date functions
                    if (fn == "GETDATE" || fn == "CURRENT_TIMESTAMP")
                    {
                        // Maps to current timestamp in milliseconds
                        return Expr.Lit(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    }

                    throw new NotSupportedException($"Function '{call.FunctionName}' is not supported.");

                default:
                    throw new NotSupportedException($"Expression type '{sqlExpr.GetType().Name}' is not supported.");
            }
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
